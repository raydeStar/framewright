using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Durable asset and authority image work. Submission only freezes a packet and
/// returns; the shared generation worker performs the expensive provider call.
/// This is what lets an artist change pages, projects, or browser tabs safely.
/// </summary>
public sealed class AssetImageGenerationService(
    IEnumerable<IGenerationAdapter> adapters,
    AssetStore assets,
    StudioDbContext db,
    StudioRepository repository,
    IProjectScope projectScope,
    RuntimeReadinessService readiness,
    TimeProvider timeProvider)
{
    private const string AssetWorkType = "Asset";
    private const int FrozenPacketVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IGenerationAdapter> adapters =
        adapters.ToDictionary(adapter => adapter.Describe().Id, StringComparer.OrdinalIgnoreCase);

    public async Task<RepositoryResult<JobSummary>> EnqueueAsync(
        GenerateAssetImageRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request, cancellationToken);
        if (validation is not null) return new(null, validation.Value.Kind, validation.Value.Error);

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var descriptor = adapters[request.AdapterId].Describe();
        var referenceBindings = NormalizeReferenceBindings(request);
        var normalized = request with
        {
            Name = request.Name.Trim(),
            CreativeBrief = request.CreativeBrief.Trim(),
            ReferenceAssetIds = referenceBindings.Select(binding => binding.AssetId).ToArray(),
            ReferenceBindings = referenceBindings,
            AuthorityTarget = request.AuthorityTarget is null ? null : request.AuthorityTarget with
            {
                ReferenceId = request.AuthorityTarget.ReferenceId.Trim(),
                Description = request.AuthorityTarget.Description.Trim(),
                LockedConstraint = request.AuthorityTarget.LockedConstraint.Trim()
            }
        };
        var frozen = await FreezeAsync(normalized, cancellationToken);
        if (frozen.Kind != RepositoryResultKind.Ok || frozen.Value is null)
            return new(null, frozen.Kind, frozen.Error);
        var requestJson = JsonSerializer.Serialize(frozen.Value, Json);
        var now = timeProvider.GetUtcNow();
        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = normalized.Name,
            Kind = normalized.AuthorityTarget is null ? "Asset image" : "Authority image",
            WorkType = AssetWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen image request queued",
            Backend = descriptor.Name,
            AdapterId = descriptor.Id,
            RequestJson = requestJson,
            IdempotencyKey = AssetIdempotencyKey(requestJson, descriptor.Id, jobId, 1),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    public async Task<RepositoryResult<JobSummary>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var failed = await db.Jobs.SingleOrDefaultAsync(x => x.Id == jobId && x.WorkType == AssetWorkType, cancellationToken);
        if (failed is null) return RepositoryResult<JobSummary>.NotFound();
        if (failed.State is not ("Failed" or "Cancelled"))
            return RepositoryResult<JobSummary>.Conflict("Only a failed or cancelled image generation can be retried.");
        if (string.IsNullOrWhiteSpace(failed.RequestJson) || string.IsNullOrWhiteSpace(failed.AdapterId))
            return RepositoryResult<JobSummary>.Conflict("This image job no longer has its frozen request packet.");
        var packet = DeserializeFrozenPacket(failed.RequestJson);
        var packetError = ValidateFrozenPacket(packet, failed.AdapterId);
        if (packetError is not null) return RepositoryResult<JobSummary>.Conflict(packetError);
        try
        {
            await ResolveFrozenInputsAsync(packet!, cancellationToken);
        }
        catch (GenerationDispatchException error)
        {
            return RepositoryResult<JobSummary>.Conflict(error.Message);
        }

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var now = timeProvider.GetUtcNow();
        var nextAttempt = await db.Jobs.Where(x => x.RetryOfJobId == failed.Id || x.Id == failed.Id)
            .MaxAsync(x => (int?)x.Attempt, cancellationToken) ?? failed.Attempt;
        var lineageRoot = await FindAssetLineageRootAsync(failed, cancellationToken);
        var retry = new JobRecord
        {
            Id = Guid.NewGuid(), ShotId = Guid.Empty, ShotCode = failed.ShotCode,
            Kind = failed.Kind, WorkType = AssetWorkType, State = JobState.Queued.ToString(),
            Progress = 0, Phase = "Retry queued from the frozen image request", Backend = failed.Backend,
            AdapterId = failed.AdapterId, RequestJson = failed.RequestJson, CreatedAt = now,
            LastHeartbeatAt = now, Attempt = nextAttempt + 1, RetryOfJobId = failed.Id,
            IdempotencyKey = AssetIdempotencyKey(failed.RequestJson, failed.AdapterId, lineageRoot, nextAttempt + 1)
        };
        db.Jobs.Add(retry);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(retry));
    }

    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.Jobs.IgnoreQueryFilters()
            .Where(x => x.WorkType == AssetWorkType && (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString()))
            .ToListAsync(cancellationToken);
        var jobs = candidates.Where(x =>
            x.State == JobState.Queued.ToString() ||
            x.LeaseExpiresAt is null || x.LeaseExpiresAt <= now).ToArray();
        var recoverable = new List<Guid>();
        foreach (var job in jobs)
        {
            var hadProviderSubmission = !string.IsNullOrWhiteSpace(job.ProviderRequestId);
            var canResume = hadProviderSubmission
                && job.AdapterId is { Length: > 0 } adapterId
                && adapters.TryGetValue(adapterId, out var adapter)
                && adapter is IRecoverableGenerationAdapter resumable
                && resumable.CanResume(job.ProviderRequestId!);
            if (hadProviderSubmission && !canResume)
            {
                job.State = JobState.Failed.ToString();
                job.Progress = 100;
                job.Phase = "Needs an explicit retry after restart";
                job.Error = "Framewright restarted after the image provider accepted this request. Retry explicitly so it cannot submit a duplicate behind your back.";
                job.CompletedAt = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                continue;
            }
            job.State = JobState.Queued.ToString();
            job.Progress = canResume ? Math.Max(job.Progress, 35) : 0;
            job.Phase = canResume ? "Reconnecting to provider after restart" : "Recovered before provider submission";
            job.LastHeartbeatAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            recoverable.Add(job.Id);
        }
        await db.SaveChangesAsync(cancellationToken);
        return recoverable;
    }

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var owner = await db.Jobs.IgnoreQueryFilters().Where(x => x.Id == jobId).Select(x => x.ProjectId).FirstOrDefaultAsync(cancellationToken);
        if (owner == Guid.Empty) return;
        projectScope.Bind(owner);

        var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == jobId && x.WorkType == AssetWorkType, cancellationToken);
        if (job is null || job.State is not ("Queued" or "Running")) return;
        if (string.IsNullOrWhiteSpace(job.RequestJson) || string.IsNullOrWhiteSpace(job.AdapterId)
            || !adapters.TryGetValue(job.AdapterId, out var adapter))
        {
            await FailAsync(job, "The queued image no longer has a valid frozen request and adapter.", false, null, cancellationToken);
            return;
        }
        var packet = DeserializeFrozenPacket(job.RequestJson);
        var packetError = ValidateFrozenPacket(packet, job.AdapterId);
        if (packetError is not null)
        {
            await FailAsync(job, packetError, false, null, cancellationToken);
            return;
        }
        var request = packet!.Request;

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
        {
            await FailAsync(job, admission.Error ?? "Generation prerequisites are not ready.", false, null, cancellationToken);
            return;
        }

        job.State = JobState.Running.ToString();
        job.Progress = Math.Max(job.Progress, 10);
        job.Phase = "Loading frozen image request";
        job.LastHeartbeatAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var context = await BuildContextAsync(job, packet, cancellationToken);
            job.Progress = Math.Max(job.Progress, 28);
            job.Phase = $"Generating through {job.Backend}";
            await db.SaveChangesAsync(cancellationToken);
            var output = await adapter.ExecuteAsync(context, cancellationToken);
            await using var content = output.Content;
            if (output.Kind != AssetKind.Image)
                throw new GenerationDispatchException("The selected adapter did not return an image.", output.ProviderCallMade, output.ProviderRequestId);
            job.Progress = 84;
            job.Phase = "Validating and saving image";
            await db.SaveChangesAsync(cancellationToken);
            var imported = await assets.ImportGeneratedImageWithStatusAsync(content, output.FileName, content.CanSeek ? content.Length : null, cancellationToken);
            if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
                throw new GenerationDispatchException(imported.Error ?? "The generated image failed local validation.", output.ProviderCallMade, output.ProviderRequestId);
            if (!imported.Value.Created && request.ParentAssetId is not null)
            {
                // Content-addressed import returns the existing row when the
                // provider produced byte-for-byte identical pixels. Complete as
                // a deliberate no-change result before touching metadata; the
                // previous implementation renamed/tagged the parent and only
                // then discovered that it could not revise an asset with itself.
                job.OutputAssetId = imported.Value.Asset.Id;
                job.ProviderRequestId = output.ProviderRequestId;
                job.State = JobState.Completed.ToString();
                job.Progress = 100;
                job.Phase = "No change — generated pixels match the current asset";
                job.Error = request.ParentAssetId == imported.Value.Asset.Id
                    ? "The provider returned the exact current image. The parent asset and authority were left unchanged."
                    : "The provider returned pixels that already exist in Assets. The existing asset, parent, and authority were left unchanged.";
                job.CompletedAt = timeProvider.GetUtcNow();
                job.LastHeartbeatAt = job.CompletedAt;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            var descriptor = adapter.Describe();
            var updated = await assets.UpdateAsync(imported.Value.Asset.Id, new UpdateAssetRequest(
                request.Name.Trim(), null, ["generated", "concept", descriptor.Name],
                $"Generated in Framewright through {descriptor.Name}."), cancellationToken);
            if (updated.Kind != RepositoryResultKind.Ok || updated.Value is null)
                throw new GenerationDispatchException(updated.Error ?? "The generated image metadata could not be saved.", output.ProviderCallMade, output.ProviderRequestId);

            AssetSummary result;
            if (request.ParentAssetId is not null)
            {
                var revised = await assets.AddRevisionAsync(request.ParentAssetId.Value,
                    new AddAssetRevisionRequest(updated.Value.Id, request.CreativeBrief.Trim(), descriptor.Name), cancellationToken);
                if (revised.Kind != RepositoryResultKind.Ok || revised.Value is null)
                    throw new GenerationDispatchException(revised.Error ?? "The generated revision could not be attached to its asset stack.", output.ProviderCallMade, output.ProviderRequestId);
                result = revised.Value;
            }
            else
            {
                var record = await db.Assets.SingleAsync(x => x.Id == updated.Value.Id, cancellationToken);
                record.RevisionFamilyId ??= request.RevisionFamilyId ?? Guid.NewGuid();
                record.RevisionNumber ??= 1;
                record.IsCurrentRevision = true;
                record.RevisionPrompt = request.CreativeBrief.Trim();
                record.RevisionEngine = descriptor.Name;
                record.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                result = (await assets.ListRevisionsAsync(record.Id, cancellationToken)).Value!.Single();
            }

            var phase = "Image ready in Assets";
            string? completionWarning = null;
            if (request.AuthorityTarget is { } authority)
            {
                var promoted = await repository.CreateReferenceVersionAsync(authority.ReferenceId,
                    new CreateReferenceVersionRequest(authority.ExpectedVersion, authority.Description, authority.LockedConstraint, result.Id), cancellationToken);
                if (promoted.Kind == RepositoryResultKind.Ok && promoted.Value is not null)
                    phase = $"{promoted.Value.Name} v{promoted.Value.Version} ready";
                else
                {
                    phase = "Image ready; authority changed before promotion";
                    completionWarning = promoted.Error ?? "The authority changed while this render was queued. The image is safe in Assets and can be attached manually.";
                }
            }

            job.OutputAssetId = result.Id;
            job.ProviderRequestId = output.ProviderRequestId;
            job.State = JobState.Completed.ToString();
            job.Progress = 100;
            job.Phase = phase;
            job.Error = completionWarning;
            job.CompletedAt = timeProvider.GetUtcNow();
            job.LastHeartbeatAt = job.CompletedAt;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GenerationDispatchException ex)
        {
            await FailAsync(job, ex.Message, ex.ProviderCallMade, ex.ProviderRequestId, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailAsync(job, "Image generation failed inside the adapter boundary. " + ex.Message, false, null, cancellationToken);
        }
    }

    private async Task<GenerationExecutionContext> BuildContextAsync(
        JobRecord job,
        FrozenAssetImagePacket packet,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveFrozenInputsAsync(packet, cancellationToken);
        async Task Report(int percent, string phase, string? providerRequestId, CancellationToken ct)
        {
            job.Progress = Math.Clamp(percent, 0, 99);
            job.Phase = phase;
            job.LastHeartbeatAt = timeProvider.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(providerRequestId)) job.ProviderRequestId = providerRequestId;
            await db.SaveChangesAsync(ct);
        }
        return new(job.Id, job.Id, packet.Request.Name, packet.Request.Route, GenerationPurpose.Draft,
            packet.Prompt, packet.ManifestHash, resolved.Composition?.Asset, resolved.Composition?.Path,
            resolved.References, Report: Report,
            ExistingProviderRequestId: job.ProviderRequestId,
            IsCurrentFrameEdit: packet.Request.UseCurrentFrame);
    }

    private async Task<RepositoryResult<FrozenAssetImagePacket>> FreezeAsync(
        GenerateAssetImageRequest request,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == projectScope.ProjectId,
            cancellationToken);
        if (project is null)
            return RepositoryResult<FrozenAssetImagePacket>.NotFound();

        FrozenAssetDescriptor? composition = null;
        if (request.CompositionAssetId is { } compositionId)
        {
            var asset = await assets.GetAsync(compositionId, cancellationToken);
            if (asset is null || asset.Kind != AssetKind.Image.ToString())
                return RepositoryResult<FrozenAssetImagePacket>.Conflict("The composition changed while its immutable generation packet was being prepared.");
            composition = FreezeDescriptor(asset);
        }

        var bindings = NormalizeReferenceBindings(request);
        var references = new List<FrozenReferenceDescriptor>(bindings.Count);
        for (var sourceOrder = 0; sourceOrder < bindings.Count; sourceOrder++)
        {
            var binding = bindings[sourceOrder];
            var asset = await assets.GetAsync(binding.AssetId, cancellationToken);
            if (asset is null || asset.Kind != AssetKind.Image.ToString())
                return RepositoryResult<FrozenAssetImagePacket>.Conflict("A visual reference changed while its immutable generation packet was being prepared.");
            references.Add(new(sourceOrder, NormalizeReferenceRole(binding.Role) ?? "General", FreezeDescriptor(asset)));
        }

        var worldPrompt = WorldPromptContract.Build(project);
        var prompt = BuildFrozenPrompt(worldPrompt, request, composition, references);
        var manifestHash = ComputeFrozenManifestHash(request, worldPrompt, prompt, composition, references);
        return RepositoryResult<FrozenAssetImagePacket>.Ok(new(
            FrozenPacketVersion,
            request,
            worldPrompt,
            prompt,
            manifestHash,
            composition,
            references));
    }

    private async Task<ResolvedFrozenInputs> ResolveFrozenInputsAsync(
        FrozenAssetImagePacket packet,
        CancellationToken cancellationToken)
    {
        ResolvedFrozenAsset? composition = null;
        if (packet.Composition is not null)
            composition = await ResolveFrozenAssetAsync(packet.Composition, "composition", cancellationToken);

        var references = new List<(AuthorityBinding Binding, AssetRecord Asset, string Path)>(packet.References.Count);
        foreach (var reference in packet.References.OrderBy(candidate => candidate.SourceOrder))
        {
            var resolved = await ResolveFrozenAssetAsync(reference.Asset, $"reference {reference.SourceOrder + 1}", cancellationToken);
            references.Add((new AuthorityBinding(
                reference.Asset.AssetId.ToString(),
                reference.Asset.DisplayName,
                reference.Role,
                1,
                IsPinned: true,
                Placements: [],
                SourceOrder: reference.SourceOrder), resolved.Asset, resolved.Path));
        }
        return new(composition, references);
    }

    private async Task<ResolvedFrozenAsset> ResolveFrozenAssetAsync(
        FrozenAssetDescriptor frozen,
        string label,
        CancellationToken cancellationToken)
    {
        var current = await assets.GetAsync(frozen.AssetId, cancellationToken);
        if (current is null || current.Kind != AssetKind.Image.ToString()
            || current.ProjectId != frozen.ProjectId)
            throw new GenerationDispatchException($"The frozen {label} image is no longer available.");
        if (!string.Equals(current.ContentHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GenerationDispatchException($"The frozen {label} image no longer matches its enqueue-time content hash. Prepare a new request instead of silently changing the provider input.");

        var path = assets.ResolveContentPath(current);
        if (!File.Exists(path))
            throw new GenerationDispatchException($"The frozen {label} image file is no longer available.");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new GenerationDispatchException($"The frozen {label} image bytes no longer match their enqueue-time content hash. The provider was not called.");
        }

        var immutable = new AssetRecord
        {
            Id = frozen.AssetId,
            ProjectId = frozen.ProjectId,
            Kind = AssetKind.Image.ToString(),
            OriginalFileName = frozen.OriginalFileName,
            MimeType = frozen.MimeType,
            Bytes = frozen.Bytes,
            Width = frozen.Width,
            Height = frozen.Height,
            ContentHash = frozen.ContentHash,
            StoragePath = current.StoragePath,
            DisplayName = frozen.DisplayName
        };
        return new(immutable, path);
    }

    private static string BuildFrozenPrompt(
        string worldPrompt,
        GenerateAssetImageRequest request,
        FrozenAssetDescriptor? composition,
        IReadOnlyList<FrozenReferenceDescriptor> references)
    {
        var prompt = new StringBuilder(worldPrompt).Append("\n\nLIBRARY ASSET BRIEF:\n").Append(request.CreativeBrief);
        prompt.Append("\n\nLIBRARY ASSET: Create a reusable visual asset, not a finished sequence shot. Keep the subject clean, legible, and useful as future generation context.");
        if (references.Count > 0)
            prompt.Append("\n\nSELECTED VISUAL REFERENCES:\n").AppendJoin("\n", references.OrderBy(reference => reference.SourceOrder)
                .Select(reference => $"- {reference.Asset.DisplayName}: {reference.Role}"));
        prompt.Append("\n\nREFERENCE ORCHESTRATION: Treat every selected image as a deliberately assigned source, not as a collage ingredient. Follow any REFERENCE ASSIGNMENTS in the artist brief. Do not copy a reference's pose, crop, background, face, or styling unless its assignment explicitly asks for that trait. Keep multiple people distinct and preserve the requested subject count and framing.");
        if (composition is not null && request.UseCurrentFrame)
        {
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS THE CURRENT GENERATED FRAME, NOT THE ORIGINAL SKETCH.");
            prompt.Append("\nEDIT PICTURE 1 IN PLACE. Apply every requested change and pinned image revision note visibly. Do not return an unchanged copy.");
            prompt.Append("\nPreserve all unmentioned identity, wardrobe, props, architecture, lighting, crop, and composition. Do not restage, duplicate, replace, or relocate the subject unless the requested change explicitly requires it.");
        }
        else if (composition is not null)
        {
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS THE ARTIST'S SPATIAL GUIDE. Replace rough marks with finished imagery while preserving intentional placement and proportion.");
        }
        return prompt.ToString();
    }

    private static string ComputeFrozenManifestHash(
        GenerateAssetImageRequest request,
        string worldPrompt,
        string prompt,
        FrozenAssetDescriptor? composition,
        IReadOnlyList<FrozenReferenceDescriptor> references)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            schemaVersion = FrozenPacketVersion,
            request,
            worldPrompt,
            prompt,
            composition,
            references
        }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static FrozenAssetImagePacket? DeserializeFrozenPacket(string requestJson)
    {
        try { return JsonSerializer.Deserialize<FrozenAssetImagePacket>(requestJson, Json); }
        catch (JsonException) { return null; }
    }

    private string? ValidateFrozenPacket(FrozenAssetImagePacket? packet, string adapterId)
    {
        if (packet is null || packet.SchemaVersion != FrozenPacketVersion || packet.Request is null
            || string.IsNullOrWhiteSpace(packet.WorldPrompt) || string.IsNullOrWhiteSpace(packet.Prompt)
            || string.IsNullOrWhiteSpace(packet.ManifestHash) || packet.References is null)
            return "This image job predates the immutable asset packet contract or its frozen packet is invalid. Prepare a new request; mutable state will not be reconstructed silently.";
        if (!string.Equals(packet.Request.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase))
            return "The frozen image packet no longer matches its queued adapter binding.";
        if (!adapters.TryGetValue(adapterId, out var adapter))
            return "The frozen image packet's adapter is no longer installed.";
        var descriptor = adapter.Describe();
        if (!descriptor.Routes.Contains(packet.Request.Route) || !descriptor.Purposes.Contains(GenerationPurpose.Draft))
            return $"{descriptor.Name} no longer supports this frozen image packet.";
        if (!descriptor.CanDispatch) return descriptor.Detail;
        var expectedHash = ComputeFrozenManifestHash(
            packet.Request,
            packet.WorldPrompt,
            packet.Prompt,
            packet.Composition,
            packet.References);
        return string.Equals(expectedHash, packet.ManifestHash, StringComparison.Ordinal)
            ? null
            : "The frozen image packet failed its integrity check. Prepare a new request; provider input will not be rebuilt from current mutable state.";
    }

    private static FrozenAssetDescriptor FreezeDescriptor(AssetRecord asset) => new(
        asset.Id,
        asset.ProjectId,
        asset.ContentHash,
        asset.DisplayName,
        asset.OriginalFileName,
        asset.MimeType,
        asset.Bytes,
        asset.Width,
        asset.Height);

    private sealed record FrozenAssetImagePacket(
        int SchemaVersion,
        GenerateAssetImageRequest Request,
        string WorldPrompt,
        string Prompt,
        string ManifestHash,
        FrozenAssetDescriptor? Composition,
        IReadOnlyList<FrozenReferenceDescriptor> References);

    private sealed record FrozenAssetDescriptor(
        Guid AssetId,
        Guid ProjectId,
        string ContentHash,
        string DisplayName,
        string OriginalFileName,
        string MimeType,
        long Bytes,
        int? Width,
        int? Height);

    private sealed record FrozenReferenceDescriptor(
        int SourceOrder,
        string Role,
        FrozenAssetDescriptor Asset);

    private sealed record ResolvedFrozenAsset(AssetRecord Asset, string Path);

    private sealed record ResolvedFrozenInputs(
        ResolvedFrozenAsset? Composition,
        IReadOnlyList<(AuthorityBinding Binding, AssetRecord Asset, string Path)> References);

    private async Task<(RepositoryResultKind Kind, string Error)?> ValidateAsync(GenerateAssetImageRequest request, CancellationToken cancellationToken)
    {
        var name = (request.Name ?? string.Empty).Trim();
        var brief = (request.CreativeBrief ?? string.Empty).Trim();
        if (name.Length is 0 or > 120) return (RepositoryResultKind.Invalid, "Asset name must contain 1 to 120 characters.");
        if (brief.Length is 0 or > 5_000) return (RepositoryResultKind.Invalid, "Creative brief must contain 1 to 5,000 characters.");
        if (request.Route is not (GenerationRoute.FastDraft or GenerationRoute.PrecisionDraft))
            return (RepositoryResultKind.Invalid, "Asset images support fast or precision image routes.");
        if (request.UseCurrentFrame && request.CompositionAssetId is null)
            return (RepositoryResultKind.Invalid, "A current-frame revision requires the selected source image.");
        if (!adapters.TryGetValue(request.AdapterId ?? string.Empty, out var adapter))
            return (RepositoryResultKind.Invalid, "The selected image adapter is not installed.");
        var descriptor = adapter.Describe();
        if (!descriptor.Routes.Contains(request.Route) || !descriptor.Purposes.Contains(GenerationPurpose.Draft))
            return (RepositoryResultKind.Invalid, $"{descriptor.Name} cannot generate this asset route.");
        if (!descriptor.CanDispatch) return (RepositoryResultKind.Conflict, descriptor.Detail);
        if (request.AuthorityTarget is { } target)
        {
            if (string.IsNullOrWhiteSpace(target.ReferenceId) || target.ExpectedVersion < 1
                || string.IsNullOrWhiteSpace(target.Description) || target.Description.Trim().Length > 2_000
                || string.IsNullOrWhiteSpace(target.LockedConstraint) || target.LockedConstraint.Trim().Length > 1_000)
                return (RepositoryResultKind.Invalid, "The authority promotion target is incomplete or invalid.");
            var reference = await db.References.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.ReferenceId, cancellationToken);
            if (reference is null) return (RepositoryResultKind.Invalid, "The authority to revise no longer exists.");
        }
        if (request.CompositionAssetId is { } compositionId)
        {
            var composition = await assets.GetAsync(compositionId, cancellationToken);
            if (composition is null || composition.Kind != AssetKind.Image.ToString())
                return (RepositoryResultKind.Invalid, "The composition must be an available image asset.");
        }
        var referenceBindings = NormalizeReferenceBindings(request);
        if (referenceBindings.Any(binding => NormalizeReferenceRole(binding.Role) is null))
            return (RepositoryResultKind.Invalid, "Reference roles must be General, Style, Identity, Face, Wardrobe, Outfit, Pose, Location, Architecture, or Prop.");
        var referenceIds = referenceBindings.Select(binding => binding.AssetId).Distinct().ToArray();
        var referenceLimit = request.Route == GenerationRoute.FastDraft ? 3 : 8;
        if (referenceIds.Length > referenceLimit)
            return (RepositoryResultKind.Invalid, request.Route == GenerationRoute.FastDraft
                ? "Fast Draft accepts up to three direct visual references. Remove one or switch to a precision route for up to eight."
                : "Precision image generation accepts up to eight visual references.");
        foreach (var id in referenceIds)
        {
            var asset = await assets.GetAsync(id, cancellationToken);
            if (asset is null || asset.Kind != AssetKind.Image.ToString())
                return (RepositoryResultKind.Invalid, "Every selected reference must be an available image asset.");
        }
        return null;
    }

    private static List<AssetGenerationReference> NormalizeReferenceBindings(GenerateAssetImageRequest request)
    {
        var result = new List<AssetGenerationReference>();
        foreach (var binding in request.ReferenceBindings ?? [])
        {
            if (result.Any(existing => existing.AssetId == binding.AssetId)) continue;
            result.Add(new(binding.AssetId, NormalizeReferenceRole(binding.Role) ?? (binding.Role ?? string.Empty).Trim()));
        }
        foreach (var id in request.ReferenceAssetIds ?? [])
        {
            if (result.Any(existing => existing.AssetId == id)) continue;
            result.Add(new(id, "General"));
        }
        return result;
    }

    private static string? NormalizeReferenceRole(string? role)
        => (role ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "general" or "visual reference" => "General",
            "style" => "Style",
            "identity" => "Identity",
            "face" => "Face",
            "wardrobe" => "Wardrobe",
            "outfit" or "clothing" => "Outfit",
            "pose" => "Pose",
            "location" => "Location",
            "architecture" => "Architecture",
            "prop" => "Prop",
            _ => null
        };

    private async Task<Guid> FindAssetLineageRootAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var root = job;
        var seen = new HashSet<Guid> { root.Id };
        while (root.RetryOfJobId is { } parentId && seen.Add(parentId))
        {
            var parent = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == parentId && candidate.WorkType == AssetWorkType,
                cancellationToken);
            if (parent is null) break;
            root = parent;
        }
        return root.Id;
    }

    private static string AssetIdempotencyKey(string requestJson, string adapterId, Guid lineageRoot, int attempt)
    {
        var frozenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{adapterId.Trim().ToLowerInvariant()}\n{requestJson}"))).ToLowerInvariant();
        return $"asset:{frozenHash}:{lineageRoot:N}:{attempt}";
    }

    private async Task FailAsync(JobRecord job, string error, bool providerCallMade, string? providerRequestId, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString();
        job.Progress = 100;
        job.Phase = "Image generation failed";
        job.Error = error;
        job.ProviderRequestId = providerRequestId ?? job.ProviderRequestId;
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastHeartbeatAt = job.CompletedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static JobSummary MapJob(JobRecord x) => new(
        x.Id, x.ShotId, x.ShotCode, x.Kind, Enum.Parse<JobState>(x.State), x.Progress, x.Phase,
        x.Backend, x.CreatedAt, x.CompletedAt, x.Error, x.ManifestId, x.AdapterId, x.OutputAssetId,
        x.OutputAssetId is null ? null : $"/api/assets/{x.OutputAssetId}/content", x.ProviderRequestId,
        x.Attempt, x.RetryOfJobId, x.LastHeartbeatAt, x.WorkType);
}
