using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Threading.Channels;

namespace StoryboardStudio.Api.Services;

internal sealed record FrozenGenerationImageInput(
    Guid Id,
    Guid ProjectId,
    string ContentHash,
    string DisplayName,
    string OriginalFileName,
    string MimeType,
    long Bytes,
    int? Width,
    int? Height);

internal sealed record FrozenGenerationAuthorityInput(
    string ReferenceId,
    int Version,
    string ReferenceVersionHash,
    string Description,
    string LockedConstraint,
    FrozenGenerationImageInput? Image);

internal sealed record FrozenGenerationInputs(
    FrozenGenerationImageInput? Composition,
    FrozenGenerationImageInput? LastFrame,
    IReadOnlyList<FrozenGenerationAuthorityInput> Authorities);

internal sealed record FrozenGenerationManifestEnvelope(
    Guid ProjectId,
    string Route,
    string Purpose,
    string CreativeBrief,
    IReadOnlyList<AuthorityBinding> Authorities,
    IReadOnlyList<string> Constraints,
    FrozenGenerationInputs? FrozenInputs);

internal static class GenerationFrozenInputContract
{
    public static FrozenGenerationImageInput Capture(AssetRecord asset) => new(
        asset.Id,
        asset.ProjectId,
        asset.ContentHash,
        asset.DisplayName,
        asset.OriginalFileName,
        asset.MimeType,
        asset.Bytes,
        asset.Width,
        asset.Height);
}

public sealed class GenerationJobSignal
{
    // This channel is intentionally only a wake-up bell. The durable Jobs table
    // is the queue of record, so a duplicate click, process restart, or dropped
    // in-memory signal can never duplicate provider work.
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public ValueTask QueueAsync(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        channel.Writer.TryWrite(true);
        return ValueTask.CompletedTask;
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        // A wake-up signal is only an optimization. Poll periodically so another
        // process can recover an expired database lease even though it cannot ring
        // this process's in-memory bell.
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scan.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (await channel.Reader.WaitToReadAsync(scan.Token))
                channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Periodic durable-queue scan; a little patience is also a spell.
        }
    }
}

public sealed class GenerationOrchestrator(
    StudioDbContext db,
    AssetStore assets,
    IEnumerable<IGenerationAdapter> adapters,
    IProjectScope projectScope,
    RuntimeReadinessService readiness,
    IVideoMediaProbe videoMediaProbe,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IGenerationAdapter> adapters = adapters.ToDictionary(x => x.Describe().Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GenerationAdapterSummary> ListAdapters() => adapters.Values.Select(x => x.Describe()).OrderBy(x => x.Kind).ThenBy(x => x.Name).ToArray();

    public async Task<RepositoryResult<JobSummary>> DispatchAsync(Guid manifestId, DispatchManifestRequest request, CancellationToken cancellationToken)
    {
        var manifest = await db.GenerationManifests.SingleOrDefaultAsync(x => x.Id == manifestId, cancellationToken);
        if (manifest is null) return RepositoryResult<JobSummary>.NotFound();
        if (!string.Equals(manifest.ManifestHash, request.ExpectedManifestHash, StringComparison.Ordinal))
            return RepositoryResult<JobSummary>.Conflict("The manifest hash changed. Reopen the frozen packet before dispatching.");
        if (manifest.State != ManifestState.Prepared.ToString())
            return RepositoryResult<JobSummary>.Conflict($"This manifest is already {manifest.State.ToLowerInvariant()} and cannot be dispatched again.");
        if (!adapters.TryGetValue(request.AdapterId, out var adapter))
            return RepositoryResult<JobSummary>.Invalid("The selected generation adapter is not installed.");
        var descriptor = adapter.Describe();
        var route = Enum.Parse<GenerationRoute>(manifest.Route);
        var purpose = Enum.Parse<GenerationPurpose>(manifest.Purpose);
        if (!descriptor.Routes.Contains(route) || !descriptor.Purposes.Contains(purpose))
            return RepositoryResult<JobSummary>.Invalid($"{descriptor.Name} does not support this {route} {purpose} manifest.");
        if (!descriptor.CanDispatch) return RepositoryResult<JobSummary>.Conflict(descriptor.Detail);
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == manifest.ShotId, cancellationToken);
        if (shot is null) return RepositoryResult<JobSummary>.NotFound();
        if (shot.Version != manifest.ShotVersion)
            return RepositoryResult<JobSummary>.Conflict("The shot changed after this manifest was prepared. Prepare a new manifest from the current shot version.");
        if (await db.Jobs.AnyAsync(x => x.ManifestId == manifest.Id && (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString() || x.State == JobState.Completed.ToString()), cancellationToken))
            return RepositoryResult<JobSummary>.Conflict("This immutable manifest already has a generation job.");

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var now = timeProvider.GetUtcNow();
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            ShotCode = shot.Code,
            Kind = purpose switch { GenerationPurpose.Final => "Final still", GenerationPurpose.Video => "Video", _ => "Draft frame" },
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen manifest queued",
            Backend = descriptor.Name,
            CreatedAt = now,
            Attempt = 1,
            LastHeartbeatAt = now,
            ManifestId = manifest.Id,
            AdapterId = descriptor.Id,
            IdempotencyKey = ShotIdempotencyKey(manifest.Id, descriptor.Id)
        };
        db.Jobs.Add(job);
        manifest.State = ManifestState.Dispatched.ToString();
        AddAudit("ManifestDispatched", "GenerationManifest", manifest.Id.ToString(), new { job.Id, adapter = descriptor.Id, manifest.ManifestHash });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    /// <summary>Retries a terminal job from its original immutable manifest.
    /// History is append-only: the failed attempt remains visible and a new job
    /// records its lineage instead of rewriting the old row.</summary>
    public async Task<RepositoryResult<JobSummary>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var failed = await db.Jobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (failed is null) return RepositoryResult<JobSummary>.NotFound();
        if (failed.State is not ("Failed" or "Cancelled"))
            return RepositoryResult<JobSummary>.Conflict("Only a failed or cancelled generation can be retried.");
        if (failed.ManifestId is null || failed.AdapterId is null)
            return RepositoryResult<JobSummary>.Conflict("This job no longer has its immutable manifest or adapter binding.");
        if (!adapters.TryGetValue(failed.AdapterId, out var adapter))
            return RepositoryResult<JobSummary>.Conflict("The original generation adapter is not installed.");
        var descriptor = adapter.Describe();
        if (!descriptor.CanDispatch) return RepositoryResult<JobSummary>.Conflict(descriptor.Detail);

        var manifest = await db.GenerationManifests.SingleOrDefaultAsync(x => x.Id == failed.ManifestId, cancellationToken);
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == failed.ShotId, cancellationToken);
        if (manifest is null || shot is null) return RepositoryResult<JobSummary>.NotFound();
        if (shot.Version != manifest.ShotVersion)
            return RepositoryResult<JobSummary>.Conflict("The shot changed after this attempt. Generate again from the current frame so an old packet cannot replace newer work.");
        try { await ResolveFrozenManifestAsync(manifest, cancellationToken); }
        catch (GenerationDispatchException ex) { return RepositoryResult<JobSummary>.Conflict(ex.Message); }
        if (await db.Jobs.AnyAsync(x => x.ManifestId == manifest.Id && x.Id != failed.Id
                && (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString() || x.State == JobState.Completed.ToString()), cancellationToken))
            return RepositoryResult<JobSummary>.Conflict("A newer attempt for this generation packet already exists.");

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var now = timeProvider.GetUtcNow();
        var nextAttempt = await db.Jobs.Where(x => x.ManifestId == manifest.Id).MaxAsync(x => (int?)x.Attempt, cancellationToken) ?? 0;
        var retry = new JobRecord
        {
            Id = Guid.NewGuid(),
            ShotId = failed.ShotId,
            ShotCode = failed.ShotCode,
            Kind = failed.Kind,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Retry queued from the frozen packet",
            Backend = descriptor.Name,
            CreatedAt = now,
            Attempt = nextAttempt + 1,
            RetryOfJobId = failed.Id,
            LastHeartbeatAt = now,
            ManifestId = manifest.Id,
            AdapterId = descriptor.Id,
            IdempotencyKey = ShotIdempotencyKey(manifest.Id, descriptor.Id)
        };
        db.Jobs.Add(retry);
        manifest.State = ManifestState.Dispatched.ToString();
        manifest.ProviderCallMade = false;
        AddAudit("GenerationRetried", "GenerationManifest", manifest.Id.ToString(), new { retry.Id, retry.Attempt, retry.RetryOfJobId, adapter = descriptor.Id });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(retry));
    }

    /// <summary>Completes an explicit Codex desktop handoff after the user has
    /// approved ImageGen and the MCP client supplies its generated local file.</summary>
    public async Task<RepositoryResult<JobSummary>> CompleteCodexHandoffAsync(
        Guid manifestId, string expectedManifestHash, string generatedImagePath, CancellationToken cancellationToken)
    {
        var manifest = await db.GenerationManifests.SingleOrDefaultAsync(x => x.Id == manifestId, cancellationToken);
        if (manifest is null) return RepositoryResult<JobSummary>.NotFound();
        if (manifest.State != ManifestState.Prepared.ToString()) return RepositoryResult<JobSummary>.Conflict($"This manifest is already {manifest.State.ToLowerInvariant()}.");
        if (!string.Equals(manifest.ManifestHash, expectedManifestHash, StringComparison.Ordinal)) return RepositoryResult<JobSummary>.Conflict("The manifest hash changed.");
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == manifest.ShotId, cancellationToken);
        if (shot is null) return RepositoryResult<JobSummary>.NotFound();
        if (shot.Version != manifest.ShotVersion) return RepositoryResult<JobSummary>.Conflict("The shot changed after this Codex packet was prepared.");

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        await using var image = new FileStream(generatedImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imported = await assets.ImportGeneratedImageAsync(image, Path.GetFileName(generatedImagePath), image.Length, cancellationToken);
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null) return new(null, imported.Kind, imported.Error);

        var now = timeProvider.GetUtcNow();
        var sourceVersion = shot.Version;
        var currentCandidates = await db.CandidateVersions.Where(x => x.ShotId == shot.Id && x.IsCurrent).ToListAsync(cancellationToken);
        foreach (var currentCandidate in currentCandidates) { currentCandidate.IsCurrent = false; currentCandidate.SupersededAt = now; }
        var highestVersion = await db.CandidateVersions.Where(x => x.ShotId == shot.Id)
            .MaxAsync(x => (int?)x.Version, cancellationToken) ?? shot.Version;
        shot.Version = Math.Max(shot.Version, highestVersion) + 1;
        shot.Stage = manifest.Purpose == nameof(GenerationPurpose.Final) ? ShotStage.Final.ToString() : ShotStage.Draft.ToString();
        shot.Approval = ApprovalState.Working.ToString();
        shot.CurrentAssetId = imported.Value.Id;
        shot.ProductionVideoJobId = null;
        shot.ProductionVideoAssetId = null;
        shot.ContinuityState = "Pending";
        shot.UpdatedAt = now;
        var candidate = new CandidateVersionRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            Version = shot.Version,
            Stage = shot.Stage,
            Approval = shot.Approval,
            IsCurrent = true,
            AssetId = imported.Value.Id,
            SourceManifestId = manifest.Id,
            CreatedAt = now
        };
        db.CandidateVersions.Add(candidate);
        await CarryOpenFeedbackForwardAsync(shot.Id, sourceVersion, shot.Version, now, cancellationToken);
        ApplyVideoEndpointIntent(shot, candidate, manifest);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            ShotCode = shot.Code,
            Kind = manifest.Purpose == nameof(GenerationPurpose.Final) ? "Final still" : "Draft frame",
            State = JobState.Completed.ToString(),
            Progress = 100,
            Phase = "Codex candidate ready for review",
            Backend = "Codex ImageGen handoff",
            CreatedAt = now,
            CompletedAt = now,
            ManifestId = manifest.Id,
            AdapterId = "codex-imagegen-handoff",
            IdempotencyKey = ShotIdempotencyKey(manifest.Id, "codex-imagegen-handoff"),
            OutputAssetId = imported.Value.Id,
            ProviderRequestId = manifest.ManifestHash[..16]
        };
        db.Jobs.Add(job);
        manifest.State = ManifestState.Completed.ToString();
        manifest.ProviderCallMade = true;
        AddAudit("CodexImageHandoffCompleted", "GenerationManifest", manifest.Id.ToString(), new { job.Id, outputAssetId = imported.Value.Id, manifest.ManifestHash });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    /// <summary>
    /// Requeues interrupted work across every project, not only the active one — a
    /// restart must not abandon a render just because the artist had switched away
    /// from that project before shutting down.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.Jobs.IgnoreQueryFilters()
            .Where(x => x.WorkType == "Shot" &&
                (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString()))
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
                job.Error = "Framewright restarted after this provider accepted the request, but that provider cannot be reconnected safely. Retry when ready; no duplicate was submitted automatically.";
                job.CompletedAt = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                if (job.ManifestId is { } manifestId)
                {
                    var manifest = await db.GenerationManifests.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == manifestId, cancellationToken);
                    if (manifest is not null) manifest.State = ManifestState.Failed.ToString();
                }
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
        // The worker runs outside any request, so nothing has resolved a project
        // for this scope. Find the job's own project first and pin the scope to it:
        // otherwise switching projects while a render is in flight would make the
        // job invisible to its own completion path and it would silently stall.
        var owner = await db.Jobs.IgnoreQueryFilters().Where(x => x.Id == jobId).Select(x => x.ProjectId).FirstOrDefaultAsync(cancellationToken);
        if (owner == Guid.Empty) return;
        projectScope.Bind(owner);

        var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        if (job is null || job.State is not ("Queued" or "Running")) return;
        var manifest = job.ManifestId is null ? null : await db.GenerationManifests.SingleOrDefaultAsync(x => x.Id == job.ManifestId, cancellationToken);
        if (manifest is null || job.AdapterId is null || !adapters.TryGetValue(job.AdapterId, out var adapter))
        {
            await FailAsync(job, manifest, "The job no longer has a valid immutable manifest and adapter.", false, null, cancellationToken);
            return;
        }

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
        {
            await FailAsync(job, manifest, admission.Error ?? "Generation prerequisites are not ready.", false, null, cancellationToken);
            return;
        }

        job.State = JobState.Running.ToString(); job.Progress = Math.Max(job.Progress, 12); job.Phase = job.ProviderRequestId is null ? "Loading frozen authority packet" : "Loading frozen packet to resume provider work"; job.LastHeartbeatAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var context = await BuildContextAsync(job, manifest, cancellationToken);
            job.Progress = Math.Max(job.Progress, 34); job.Phase = job.ProviderRequestId is null ? $"Dispatching through {job.Backend}" : $"Reconnecting through {job.Backend}"; job.LastHeartbeatAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var output = await adapter.ExecuteAsync(context, cancellationToken);
            await using var outputContent = output.Content;
            job.Progress = 82; job.Phase = "Validating and importing candidate"; job.LastHeartbeatAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var imported = output.Kind == AssetKind.Image
                ? await assets.ImportGeneratedImageAsync(outputContent, output.FileName, outputContent.CanSeek ? outputContent.Length : null, cancellationToken)
                : await assets.ImportGeneratedMediaAsync(outputContent, output.FileName, output.MimeType, outputContent.CanSeek ? outputContent.Length : null, output.Kind, cancellationToken);
            if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
                throw new GenerationDispatchException(imported.Error ?? "The generated output failed local image validation.", output.ProviderCallMade, output.ProviderRequestId);

            if (output.Kind == AssetKind.Video && context.VideoQuality == VideoQuality.Max)
            {
                if (context.DeliveryWidth is not > 0 || context.DeliveryHeight is not > 0 ||
                    context.VideoFramesPerSecond is not > 0 || context.VideoFrameCount is not > 0)
                    throw new GenerationDispatchException("The Max video packet is missing its frozen delivery dimensions or timing.", output.ProviderCallMade, output.ProviderRequestId);
                var importedVideo = await db.Assets.SingleAsync(x => x.Id == imported.Value.Id, cancellationToken);
                var mediaValidation = await videoMediaProbe.ValidateAsync(
                    assets.ResolveContentPath(importedVideo),
                    new VideoMediaExpectation(
                        context.DeliveryWidth.Value,
                        context.DeliveryHeight.Value,
                        context.VideoFramesPerSecond.Value,
                        context.VideoFrameCount.Value),
                    cancellationToken);
                if (!mediaValidation.IsValid)
                    throw new GenerationDispatchException($"The Max render did not satisfy its frozen delivery contract. {mediaValidation.Detail}", output.ProviderCallMade, output.ProviderRequestId);
            }

            var shot = await db.Shots.SingleAsync(x => x.Id == job.ShotId, cancellationToken);
            var promoted = shot.Version == manifest.ShotVersion;
            var siblingCandidate = false;
            if (promoted)
            {
                var now = timeProvider.GetUtcNow();
                var sourceVersion = shot.Version;
                var currentCandidates = await db.CandidateVersions.Where(x => x.ShotId == shot.Id && x.IsCurrent).ToListAsync(cancellationToken);
                foreach (var currentCandidate in currentCandidates) { currentCandidate.IsCurrent = false; currentCandidate.SupersededAt = now; }
                var highestVersion = await db.CandidateVersions.Where(x => x.ShotId == shot.Id)
                    .MaxAsync(x => (int?)x.Version, cancellationToken) ?? shot.Version;
                shot.Version = Math.Max(shot.Version, highestVersion) + 1;
                shot.Stage = manifest.Purpose switch { nameof(GenerationPurpose.Final) => ShotStage.Final.ToString(), nameof(GenerationPurpose.Video) => ShotStage.Video.ToString(), _ => ShotStage.Draft.ToString() };
                shot.Approval = ApprovalState.Working.ToString();
                if (output.Kind == AssetKind.Image) shot.CurrentAssetId = imported.Value.Id;
                shot.ProductionVideoJobId = null;
                shot.ProductionVideoAssetId = null;
                if (output.Kind == AssetKind.Video && context.VideoQuality == VideoQuality.Max)
                {
                    shot.ProductionVideoJobId = job.Id;
                    shot.ProductionVideoAssetId = imported.Value.Id;
                }
                shot.ContinuityState = "Pending";
                shot.UpdatedAt = now;
                var candidate = new CandidateVersionRecord
                {
                    Id = Guid.NewGuid(),
                    ShotId = shot.Id,
                    Version = shot.Version,
                    Stage = shot.Stage,
                    Approval = shot.Approval,
                    IsCurrent = true,
                    AssetId = output.Kind == AssetKind.Image ? imported.Value.Id : shot.CurrentAssetId,
                    SourceManifestId = manifest.Id,
                    CreatedAt = now
                };
                db.CandidateVersions.Add(candidate);
                await CarryOpenFeedbackForwardAsync(shot.Id, sourceVersion, shot.Version, now, cancellationToken);
                if (output.Kind == AssetKind.Image) ApplyVideoEndpointIntent(shot, candidate, manifest);
            }
            else if (output.Kind == AssetKind.Image)
            {
                // Requests prepared from the same live frame are siblings. The
                // first completion becomes live; later completions remain fully
                // reviewable instead of becoming invisible orphan assets.
                var now = timeProvider.GetUtcNow();
                var highestVersion = await db.CandidateVersions.Where(x => x.ShotId == shot.Id)
                    .MaxAsync(x => (int?)x.Version, cancellationToken) ?? shot.Version;
                var variantVersion = Math.Max(highestVersion, shot.Version) + 1;
                db.CandidateVersions.Add(new CandidateVersionRecord
                {
                    Id = Guid.NewGuid(), ShotId = shot.Id, Version = variantVersion,
                    Stage = manifest.Purpose == nameof(GenerationPurpose.Final) ? ShotStage.Final.ToString() : ShotStage.Draft.ToString(),
                    Approval = ApprovalState.Working.ToString(), IsCurrent = false,
                    AssetId = imported.Value.Id, SourceManifestId = manifest.Id,
                    CreatedAt = now, SupersededAt = now
                });
                await CarryOpenFeedbackForwardAsync(shot.Id, manifest.ShotVersion, variantVersion, now, cancellationToken);
                siblingCandidate = true;
            }
            job.OutputAssetId = imported.Value.Id;
            job.ProviderRequestId = output.ProviderRequestId;
            job.State = JobState.Completed.ToString(); job.Progress = 100;
            job.Phase = promoted ? "Candidate ready for review" : siblingCandidate ? "Variant ready for comparison" : "Output ready; shot changed after dispatch";
            job.CompletedAt = timeProvider.GetUtcNow(); job.LastHeartbeatAt = job.CompletedAt;
            manifest.State = ManifestState.Completed.ToString(); manifest.ProviderCallMade = output.ProviderCallMade;
            AddAudit("GenerationCompleted", "GenerationManifest", manifest.Id.ToString(), new { job.Id, outputAssetId = imported.Value.Id, promoted, output.ProviderCallMade, output.ProviderRequestId });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GenerationDispatchException ex)
        {
            await FailAsync(job, manifest, ex.Message, ex.ProviderCallMade, ex.ProviderRequestId, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailAsync(job, manifest, "Generation failed inside the adapter boundary. " + ex.Message, false, null, cancellationToken);
        }
    }

    private async Task CarryOpenFeedbackForwardAsync(Guid shotId, int sourceVersion, int targetVersion, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var openFeedback = await db.Comments.AsNoTracking()
            .Where(comment => comment.ShotId == shotId && comment.Version == sourceVersion && comment.State == "Open")
            .ToListAsync(cancellationToken);
        foreach (var comment in openFeedback)
        {
            db.Comments.Add(new CommentRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = comment.ProjectId,
                ShotId = shotId,
                Version = targetVersion,
                X = comment.X,
                Y = comment.Y,
                Body = comment.Body,
                State = "Open",
                CreatedAt = createdAt,
                ReferenceId = comment.ReferenceId,
                ReferenceVersion = comment.ReferenceVersion
            });
        }
    }

    private async Task<GenerationExecutionContext> BuildContextAsync(JobRecord job, GenerationManifestRecord manifest, CancellationToken cancellationToken)
    {
        var frozen = await ResolveFrozenManifestAsync(manifest, cancellationToken);
        var packet = frozen.Packet;
        var composition = frozen.Composition?.Asset;
        var compositionPath = frozen.Composition?.Path;
        var lastFrame = frozen.LastFrame?.Asset;
        var lastFramePath = frozen.LastFrame?.Path;
        // Adapters that wait on an external queue report through this so the
        // artist sees "waiting behind 2 jobs" rather than a bar stuck at 34%.
        async Task Report(int percent, string phase, string? providerRequestId, CancellationToken ct)
        {
            job.Progress = Math.Clamp(percent, 0, 99);
            job.Phase = phase;
            job.LastHeartbeatAt = timeProvider.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(providerRequestId)) job.ProviderRequestId = providerRequestId;
            await db.SaveChangesAsync(ct);
        }

        var bindings = packet.Authorities;
        var referenceImages = frozen.AuthorityImages;
        var authorityCanon = frozen.AuthorityCanon;
        var constraints = packet.Constraints;
        var currentFrameEdit = GenerationReferenceDispatchPlanner.IsCurrentFrameManifest(manifest.ManifestJson, packet.CreativeBrief);
        var markupEdit = GenerationReferenceDispatchPlanner.IsMarkupEditManifest(manifest.ManifestJson, packet.CreativeBrief);
        var prompt = new StringBuilder(currentFrameEdit
            ? BuildCurrentFrameDeltaPrompt(packet.CreativeBrief, bindings)
            : packet.CreativeBrief.Trim());
        if (authorityCanon.Count > 0)
        {
            prompt.Append(currentFrameEdit
                    ? "\n\nEXISTING-SUBJECT CANON CHECKS (validation only; never create a new person or object from these lines):\n"
                    : "\n\nIMMUTABLE AUTHORITY CANON (exact versions; overrides any conflicting requested change or directing note):\n")
                .AppendJoin("\n", authorityCanon.Select(item =>
                    currentFrameEdit
                        ? $"- Existing {item.Binding.Name} ({item.Binding.Category}) v{item.Binding.Version}: {item.LockedConstraint}"
                        : $"- {item.Binding.Name} ({item.Binding.Category}) v{item.Binding.Version}: {item.Description} LOCKED: {item.LockedConstraint}"));
        }
        if (constraints.Count > 0) prompt.Append("\n\nSHOT CONSTRAINTS (authority canon wins if older shot text conflicts):\n").AppendJoin("\n", constraints.Select(x => $"- {x}"));
        if (markupEdit)
        {
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS THE CURRENT GENERATED FRAME WITH TEMPORARY GOLD EDIT MARKUP. Preserve all unmarked content unless the requested change requires otherwise. Treat gold strokes only as spatial guidance for the requested edit. Remove every trace of the markup from the output; no gold line, circle, scribble, annotation, or UI element may survive.");
        }
        else if (currentFrameEdit)
        {
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS THE CURRENT GENERATED FRAME, NOT THE ORIGINAL SKETCH. Treat it as the visual base for an image edit. Preserve composition, identity, wardrobe, lighting, environment, and all unmentioned details unless the requested change requires otherwise.");
        }
        else if (composition is not null && packet.Purpose == GenerationPurpose.Video.ToString())
        {
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS THE RATIFIED PRODUCTION FIRST FRAME, NOT A SKETCH. Preserve its character identities, wardrobe, styling, lighting, camera, environment, and screen positions. Animate only the restrained motion described above; do not redesign or replace the frame.");
        }
        else if (composition is not null)
        {
            // Instruction-following edit models can treat
            // sketches as artwork unless told plainly that they are stand-ins.
            prompt.Append("\n\nTHE COMPOSITION IMAGE IS A ROUGH BLOCKING GUIDE, NOT ARTWORK TO PRESERVE. Stick figures, boxes, and scribbles mark only where a subject stands, how it is posed, how it is oriented, and how large it sits in frame. Replace every placeholder with a fully realized character or subject in the project's approved visual language, while preserving position, pose, orientation, scale, framing, camera, horizon, and layout. No literal guide geometry, stick-figure placeholders, boxes, scribbles, or annotation marks may remain. Deliberate stylized animation, cel shading, and controlled linework required by the world settings are final-image qualities—not guide marks. Authority images and locked constraints control identity and world truth. Do not infer that an off-camera world feature has been removed.");
        }
        else
        {
            prompt.Append("\n\nGENERATE THIS AS A NEW FRAME FROM THE SHOT INTENT. There is no composition sketch for this pass. Use the camera guidance, action, approved authorities, and locked constraints to choose clear cinematic blocking. Do not invent changes to world canon merely because a feature is outside the crop.");
        }
        var videoQuality = VideoQuality.Low; long? videoSeed = null; int? videoWidth = null; int? videoHeight = null; int? deliveryWidth = null; int? deliveryHeight = null; int? videoFramesPerSecond = null; int? videoFrameCount = null;
        try
        {
            using var payload = JsonDocument.Parse(manifest.ManifestJson);
            var root = payload.RootElement;
            if (root.TryGetProperty("projectFormat", out var format))
            {
                if (format.TryGetProperty("deliveryWidth", out var widthNode) && widthNode.TryGetInt32(out var parsedWidth)) deliveryWidth = parsedWidth;
                if (format.TryGetProperty("deliveryHeight", out var heightNode) && heightNode.TryGetInt32(out var parsedHeight)) deliveryHeight = parsedHeight;
                if (format.TryGetProperty("framesPerSecond", out var fpsNode) && fpsNode.TryGetInt32(out var parsedFps)) videoFramesPerSecond = parsedFps;
            }
            if (packet.Purpose == GenerationPurpose.Video.ToString())
            {
                if (root.TryGetProperty("videoQuality", out var qualityNode)) Enum.TryParse(qualityNode.GetString(), true, out videoQuality);
                if (root.TryGetProperty("videoSeed", out var seedNode) && seedNode.TryGetInt64(out var parsedSeed)) videoSeed = parsedSeed;
                if (root.TryGetProperty("videoCanvas", out var canvas))
                {
                    if (canvas.TryGetProperty("width", out var widthNode) && widthNode.TryGetInt32(out var parsedWidth)) videoWidth = parsedWidth;
                    if (canvas.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var parsedHeight)) videoHeight = parsedHeight;
                }
                if (root.TryGetProperty("shot", out var shotNode) && shotNode.TryGetProperty("durationFrames", out var durationNode) && durationNode.TryGetInt32(out var parsedDuration)) videoFrameCount = parsedDuration;
            }
        }
        catch (JsonException ex) { throw new GenerationDispatchException("The frozen manifest payload cannot be read.", false, null, ex); }
        return new(job.Id, manifest.Id, manifest.ShotCode, Enum.Parse<GenerationRoute>(packet.Route), Enum.Parse<GenerationPurpose>(packet.Purpose), prompt.ToString(), manifest.ManifestHash, composition, compositionPath, referenceImages, lastFrame, lastFramePath, Report, videoQuality, videoSeed, videoWidth, videoHeight, deliveryWidth, deliveryHeight, job.ProviderRequestId, currentFrameEdit, videoFramesPerSecond, videoFrameCount);
    }

    private async Task<ResolvedGenerationManifest> ResolveFrozenManifestAsync(
        GenerationManifestRecord manifest,
        CancellationToken cancellationToken)
    {
        var actualManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ManifestJson))).ToLowerInvariant();
        if (!string.Equals(actualManifestHash, manifest.ManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new GenerationDispatchException("The frozen manifest JSON no longer matches its prepared hash. The provider was not called.");

        FrozenGenerationManifestEnvelope? packet;
        try { packet = JsonSerializer.Deserialize<FrozenGenerationManifestEnvelope>(manifest.ManifestJson, WebJson); }
        catch (JsonException ex)
        {
            throw new GenerationDispatchException("The frozen manifest JSON cannot be read. The provider was not called.", false, null, ex);
        }
        if (packet is null || packet.Authorities is null || packet.Constraints is null || packet.FrozenInputs is null
            || packet.FrozenInputs.Authorities is null || string.IsNullOrWhiteSpace(packet.Route)
            || string.IsNullOrWhiteSpace(packet.Purpose) || string.IsNullOrWhiteSpace(packet.CreativeBrief))
            throw new GenerationDispatchException("This manifest predates the immutable generation-input contract or its packet is incomplete. Prepare a new manifest; mutable state will not be reconstructed silently.");
        if (packet.ProjectId != manifest.ProjectId || packet.ProjectId != projectScope.ProjectId)
            throw new GenerationDispatchException("The frozen manifest project binding no longer matches the queued project.");
        if (!string.Equals(packet.Route, manifest.Route, StringComparison.Ordinal)
            || !string.Equals(packet.Purpose, manifest.Purpose, StringComparison.Ordinal)
            || !string.Equals(packet.CreativeBrief, manifest.CreativeBrief, StringComparison.Ordinal)
            || !JsonEquivalent(manifest.AuthoritiesJson, packet.Authorities)
            || !JsonEquivalent(manifest.ConstraintsJson, packet.Constraints))
            throw new GenerationDispatchException("A duplicated manifest column no longer matches the hash-protected manifest JSON. The provider was not called.");

        ValidateFrozenAssetColumns(
            "composition",
            manifest.CompositionAssetId,
            manifest.CompositionAssetHash,
            packet.FrozenInputs.Composition);
        ValidateFrozenAssetColumns(
            "last-frame",
            manifest.LastFrameAssetId,
            manifest.LastFrameAssetHash,
            packet.FrozenInputs.LastFrame);
        if (packet.Authorities.Count != packet.FrozenInputs.Authorities.Count)
            throw new GenerationDispatchException("The frozen authority packet is incomplete. The provider was not called.");

        var composition = packet.FrozenInputs.Composition is null
            ? null
            : await ResolveFrozenImageAsync(packet.FrozenInputs.Composition, "composition", manifest.ProjectId, cancellationToken);
        var lastFrame = packet.FrozenInputs.LastFrame is null
            ? null
            : await ResolveFrozenImageAsync(packet.FrozenInputs.LastFrame, "last-frame", manifest.ProjectId, cancellationToken);
        var referenceImages = new List<(AuthorityBinding Binding, AssetRecord Asset, string Path)>();
        var authorityCanon = new List<(AuthorityBinding Binding, string Description, string LockedConstraint)>();
        for (var index = 0; index < packet.Authorities.Count; index++)
        {
            var binding = packet.Authorities[index];
            var frozenAuthority = packet.FrozenInputs.Authorities[index];
            if (!string.Equals(binding.Id, frozenAuthority.ReferenceId, StringComparison.Ordinal)
                || binding.Version != frozenAuthority.Version)
                throw new GenerationDispatchException("The frozen authority order no longer matches the hash-protected manifest binding.");

            var version = await db.ReferenceVersions.AsNoTracking().IgnoreQueryFilters().SingleOrDefaultAsync(
                candidate => candidate.ProjectId == manifest.ProjectId
                    && candidate.ReferenceId == frozenAuthority.ReferenceId
                    && candidate.Version == frozenAuthority.Version,
                cancellationToken);
            var expectedImageId = frozenAuthority.Image?.Id;
            if (version is null || version.ProjectId != manifest.ProjectId
                || !string.Equals(version.ContentHash, frozenAuthority.ReferenceVersionHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(version.Description, frozenAuthority.Description, StringComparison.Ordinal)
                || !string.Equals(version.LockedConstraint, frozenAuthority.LockedConstraint, StringComparison.Ordinal)
                || version.ImageAssetId != expectedImageId)
                throw new GenerationDispatchException($"Approved authority {binding.Name} v{binding.Version} no longer matches its frozen manifest packet. The provider was not called.");

            authorityCanon.Add((binding, frozenAuthority.Description, frozenAuthority.LockedConstraint));
            if (frozenAuthority.Image is null) continue;
            var image = await ResolveFrozenImageAsync(
                frozenAuthority.Image,
                $"authority {binding.Name} v{binding.Version}",
                manifest.ProjectId,
                cancellationToken);
            referenceImages.Add((binding, image.Asset, image.Path));
        }

        return new(packet, composition, lastFrame, referenceImages, authorityCanon);
    }

    private async Task<ResolvedGenerationImage> ResolveFrozenImageAsync(
        FrozenGenerationImageInput frozen,
        string label,
        Guid manifestProjectId,
        CancellationToken cancellationToken)
    {
        var current = await db.Assets.AsNoTracking().IgnoreQueryFilters()
            .SingleOrDefaultAsync(candidate => candidate.Id == frozen.Id, cancellationToken);
        if (current is null || current.Kind != AssetKind.Image.ToString())
            throw new GenerationDispatchException($"The frozen {label} image is no longer available. The provider was not called.");
        if (current.ProjectId != manifestProjectId || current.ProjectId != frozen.ProjectId)
            throw new GenerationDispatchException($"The frozen {label} image no longer belongs to the prepared project. The provider was not called.");
        if (!string.Equals(current.ContentHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GenerationDispatchException($"The frozen {label} image row no longer matches its prepared content hash. The provider was not called.");

        var path = assets.ResolveContentPath(current);
        if (!File.Exists(path))
            throw new GenerationDispatchException($"The frozen {label} image file is no longer available. The provider was not called.");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(actualHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new GenerationDispatchException($"The frozen {label} image bytes no longer match their prepared content hash. The provider was not called.");
        }

        var immutable = new AssetRecord
        {
            Id = frozen.Id,
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

    private static void ValidateFrozenAssetColumns(
        string label,
        Guid? columnId,
        string? columnHash,
        FrozenGenerationImageInput? frozen)
    {
        if (frozen is null)
        {
            if (columnId is not null || columnHash is not null)
                throw new GenerationDispatchException($"The duplicated {label} manifest columns no longer match the hash-protected packet.");
            return;
        }
        if (columnId != frozen.Id || !string.Equals(columnHash, frozen.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new GenerationDispatchException($"The duplicated {label} manifest columns no longer match the hash-protected packet.");
    }

    private static bool JsonEquivalent<T>(string json, T expected)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(json), JsonSerializer.SerializeToNode(expected, WebJson));
        }
        catch (JsonException) { return false; }
    }

    private sealed record ResolvedGenerationImage(AssetRecord Asset, string Path);

    private sealed record ResolvedGenerationManifest(
        FrozenGenerationManifestEnvelope Packet,
        ResolvedGenerationImage? Composition,
        ResolvedGenerationImage? LastFrame,
        IReadOnlyList<(AuthorityBinding Binding, AssetRecord Asset, string Path)> AuthorityImages,
        IReadOnlyList<(AuthorityBinding Binding, string Description, string LockedConstraint)> AuthorityCanon);

    internal static string BuildCurrentFrameDeltaPrompt(string creativeBrief, IReadOnlyList<AuthorityBinding>? bindings = null)
    {
        // Frozen packets can be authored on Windows and consumed in Linux (or
        // vice versa). Normalize once so section boundaries are semantic rather
        // than an accidental property of the checkout's line endings.
        var normalizedBrief = creativeBrief.ReplaceLineEndings("\n");
        const string requestedMarker = "\n\nREQUESTED CHANGE:";
        var requestedAt = normalizedBrief.IndexOf(requestedMarker, StringComparison.Ordinal);
        var requested = requestedAt >= 0
            ? normalizedBrief[(requestedAt + requestedMarker.Length)..]
            : "Preserve the selected frame and apply the artist's attached markup only.";
        foreach (var endMarker in new[] { "\n\nCURRENT FRAME EDIT:", "\n\nFRAME EDIT GUIDE:" })
        {
            var endAt = requested.IndexOf(endMarker, StringComparison.Ordinal);
            if (endAt >= 0) requested = requested[..endAt];
        }

        var worldSettings = ExtractPromptSection(
            normalizedBrief,
            "PROJECT WORLD SETTINGS",
            ["\n\nSHOT OR ASSET BRIEF:"]);
        var authoritySpecs = ExtractPromptSection(
            normalizedBrief,
            "APPROVED AUTHORITY SPECS:",
            ["\n\nREFERENCE COMPOSITION CONTRACT:", "\n\nREFERENCE PLACEMENTS", "\n\nBLOCKING OBJECT CONTRACT:"]);
        var placements = (bindings ?? [])
            .Where(binding => binding.IsPinned)
            .SelectMany(binding => binding.Placements ?? [])
            .Select(placement => placement.Instruction)
            .Where(instruction => !string.IsNullOrWhiteSpace(instruction))
            .ToArray();
        var placementContract = placements.Length == 0
            ? "No authority is spatially pinned for this edit."
            : string.Join('\n', placements);

        return $"""
            EDIT PICTURE 1 IN PLACE. Picture 1 already contains the complete intended cast, set, wardrobe, lighting, and composition.
            Apply only the requested delta below. Do not restage the shot from its description. Do not add a person to represent an action that the existing person already performs. Do not duplicate, replace, recast, or relocate any existing subject unless the delta explicitly says to do so. Preserve every unmentioned pixel-level detail.
            Treat Picture 1 as the authority for composition and existing content, but not as permission to retain a rendering style that conflicts with the approved project look. When the requested delta or Style authority corrects the look, restyle the complete frame coherently while preserving its composition, cast, and set.

            APPROVED PROJECT LOOK AND WORLD RULES (retain during every revision):
            {worldSettings}

            REQUESTED DELTA:
            {requested.Trim()}

            APPROVED POSITIVE AUTHORITY SPECS (use these to identify and apply the attached references):
            {authoritySpecs}

            FROZEN PINNED REFERENCE PLACEMENTS (semantic anchors, never pasted pixels):
            {placementContract}
            """;
    }

    private static string ExtractPromptSection(string source, string heading, IReadOnlyList<string> endMarkers)
    {
        var start = source.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0) return "No additional section was frozen in this legacy packet.";
        var end = source.Length;
        foreach (var marker in endMarkers)
        {
            var candidate = source.IndexOf(marker, start, StringComparison.Ordinal);
            if (candidate >= 0 && candidate < end) end = candidate;
        }
        return source[start..end].Trim();
    }

    private async Task FailAsync(JobRecord job, GenerationManifestRecord? manifest, string error, bool providerCallMade, string? providerRequestId, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString(); job.Progress = 100; job.Phase = "Generation failed"; job.Error = error; job.ProviderRequestId = providerRequestId ?? job.ProviderRequestId; job.CompletedAt = timeProvider.GetUtcNow(); job.LastHeartbeatAt = job.CompletedAt;
        if (manifest is not null) { manifest.State = ManifestState.Failed.ToString(); manifest.ProviderCallMade = providerCallMade; }
        AddAudit("GenerationFailed", manifest is null ? "Job" : "GenerationManifest", manifest?.Id.ToString() ?? job.Id.ToString(), new { job.Id, error, providerCallMade, providerRequestId });
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddAudit(string type, string targetType, string targetId, object payload) => db.AuditEvents.Add(new AuditEventRecord
    {
        Id = Guid.NewGuid(),
        Type = type,
        TargetType = targetType,
        TargetId = targetId,
        PayloadJson = JsonSerializer.Serialize(payload),
        CreatedAt = timeProvider.GetUtcNow()
    });

    private static string ShotIdempotencyKey(Guid manifestId, string adapterId)
        => $"shot:{manifestId:N}:{adapterId.Trim().ToLowerInvariant()}";

    private static void ApplyVideoEndpointIntent(ShotRecord shot, CandidateVersionRecord candidate, GenerationManifestRecord manifest)
    {
        using var document = JsonDocument.Parse(manifest.ManifestJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("videoEndpointRole", out var role) || !string.Equals(role.GetString(), "LastFrame", StringComparison.Ordinal)) return;
        if (root.TryGetProperty("videoEndpointSourceCandidateId", out var source) && Guid.TryParse(source.GetString(), out var sourceCandidateId))
            shot.VideoFirstFrameCandidateId = sourceCandidateId;
        shot.VideoLastFrameCandidateId = candidate.Id;
    }

    private static JobSummary MapJob(JobRecord x) => new(x.Id, x.ShotId, x.ShotCode, x.Kind, Enum.Parse<JobState>(x.State), x.Progress, x.Phase, x.Backend, x.CreatedAt, x.CompletedAt, x.Error, x.ManifestId, x.AdapterId, x.OutputAssetId, x.OutputAssetId is null ? null : $"/api/assets/{x.OutputAssetId}/content", x.ProviderRequestId, x.Attempt, x.RetryOfJobId, x.LastHeartbeatAt, x.WorkType);
}

public sealed partial class GenerationJobWorker(GenerationJobSignal queue, IServiceScopeFactory scopeFactory, ILogger<GenerationJobWorker> logger) : BackgroundService
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Generation job {JobId} escaped its adapter boundary")]
    private static partial void LogEscapedAdapterFailure(ILogger logger, Exception exception, Guid jobId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var pending = await scope.ServiceProvider.GetRequiredService<GenerationOrchestrator>().RecoverableJobsAsync(stoppingToken);
            foreach (var jobId in pending) await queue.QueueAsync(jobId, stoppingToken);
            var assetPending = await scope.ServiceProvider.GetRequiredService<AssetImageGenerationService>().RecoverableJobsAsync(stoppingToken);
            foreach (var jobId in assetPending) await queue.QueueAsync(jobId, stoppingToken);
            var audioPending = await scope.ServiceProvider.GetRequiredService<AudioGenerationJobService>().RecoverableJobsAsync(stoppingToken);
            foreach (var jobId in audioPending) await queue.QueueAsync(jobId, stoppingToken);
        }

        // Always scan once on startup, including when there was no state change
        // for RecoverableJobsAsync to report (for example, a queued row created
        // immediately before an unclean shutdown).
        await queue.QueueAsync(Guid.Empty, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            GenerationJobLease? lease;
            await using (var claimScope = scopeFactory.CreateAsyncScope())
            {
                lease = await claimScope.ServiceProvider
                    .GetRequiredService<GenerationJobLeaseService>()
                    .ClaimNextAsync(workerId, stoppingToken);
            }

            if (lease is null)
            {
                await queue.WaitAsync(stoppingToken);
                continue;
            }

            try
            {
                await ExecuteLeaseAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Leave the persisted lease intact. Startup recovery can then
                // distinguish a clean pre-submit retry from accepted provider work.
                break;
            }
            catch (Exception ex)
            {
                LogEscapedAdapterFailure(logger, ex, lease.JobId);
                await using var failureScope = scopeFactory.CreateAsyncScope();
                await failureScope.ServiceProvider.GetRequiredService<GenerationJobLeaseService>()
                    .FailEscapedWorkerErrorAsync(lease, ex, stoppingToken);
            }
        }
    }

    private async Task ExecuteLeaseAsync(GenerationJobLease lease, CancellationToken cancellationToken)
    {
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewLeaseAsync(lease, heartbeatStop.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(lease.ProjectId);
            if (string.Equals(lease.WorkType, "Asset", StringComparison.Ordinal))
                await scope.ServiceProvider.GetRequiredService<AssetImageGenerationService>().RunAsync(lease.JobId, cancellationToken);
            else if (string.Equals(lease.WorkType, AudioGenerationJobService.VoiceWorkType, StringComparison.Ordinal)
                || string.Equals(lease.WorkType, AudioGenerationJobService.MusicWorkType, StringComparison.Ordinal)
                || string.Equals(lease.WorkType, AudioGenerationJobService.VoiceDesignWorkType, StringComparison.Ordinal))
                await scope.ServiceProvider.GetRequiredService<AudioGenerationJobService>().RunAsync(lease.JobId, cancellationToken);
            else
                await scope.ServiceProvider.GetRequiredService<GenerationOrchestrator>().RunAsync(lease.JobId, cancellationToken);

            await scope.ServiceProvider.GetRequiredService<GenerationJobLeaseService>()
                .ReleaseAsync(lease, cancellationToken);
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        }
    }

    private async Task RenewLeaseAsync(GenerationJobLease lease, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await using var heartbeatScope = scopeFactory.CreateAsyncScope();
            await heartbeatScope.ServiceProvider.GetRequiredService<GenerationJobLeaseService>()
                .ExtendAsync(lease, cancellationToken);
        }
    }
}
