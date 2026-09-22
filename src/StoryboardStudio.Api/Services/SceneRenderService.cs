using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Turns exact-time frames from the browser scene renderer into a durable,
/// reviewable production take. The browser owns drawing Three.js frames; this
/// service freezes lineage, validates every frame, resumes partial captures,
/// encodes them, and promotes only when the source shot is still current.
/// </summary>
public sealed class SceneRenderService(
    StudioDbContext db,
    SceneService scenes,
    AssetStore assets,
    ISceneVideoEncoder encoder,
    IVideoMediaProbe videoProbe,
    IProjectScope projectScope,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public const string WorkType = "SceneRender";
    public const string AdapterId = "framewright-scene-render";
    public const int MaxFrames = 240;
    public const int MaxWidth = 4096;
    public const int MaxHeight = 2160;
    private static readonly TimeSpan FinalizeLockLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly string renderRoot = Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "scene-renders");

    public async Task<RepositoryResult<SceneRenderSummary>> PrepareAsync(
        Guid sceneId,
        PrepareSceneRenderRequest request,
        CancellationToken cancellationToken)
    {
        var binding = await db.SceneShotBindings.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == request.BindingId && row.SceneId == sceneId, cancellationToken);
        if (binding is null) return RepositoryResult<SceneRenderSummary>.NotFound();

        var frozenHash = HashText(binding.SnapshotJson);
        if (!string.Equals(frozenHash, binding.SnapshotHash, StringComparison.Ordinal))
            return RepositoryResult<SceneRenderSummary>.Conflict("The scene-to-shot snapshot no longer matches its recorded hash.");

        var shot = await db.Shots.SingleOrDefaultAsync(row => row.Id == binding.ShotId, cancellationToken);
        if (shot is null) return RepositoryResult<SceneRenderSummary>.NotFound();
        if (shot.Version != binding.ShotVersion || shot.CurrentAssetId != binding.StillAssetId)
            return RepositoryResult<SceneRenderSummary>.Conflict("The shot has moved beyond this scene still. Open its current scene binding before rendering motion.");
        if (shot.Approval != ApprovalState.Ratified.ToString())
            return RepositoryResult<SceneRenderSummary>.Conflict("Approve the bound scene still before rendering an animated take.");

        var currentScene = await scenes.GetAsync(sceneId, cancellationToken);
        if (currentScene.Kind != RepositoryResultKind.Ok || currentScene.Value is null)
            return RepositoryResult<SceneRenderSummary>.NotFound();
        if (currentScene.Value.Version != binding.SceneVersion)
            return RepositoryResult<SceneRenderSummary>.Conflict("The editable scene changed after this still was frozen. Render and approve a current still first.");
        if (!currentScene.Value.Instances.Any(instance => instance.Clip is not null || instance.Motion is not null))
            return RepositoryResult<SceneRenderSummary>.Invalid("Give at least one scene object a clip or pivot motion before rendering an animated take.");

        var project = await db.Projects.AsNoTracking().SingleAsync(row => row.Id == projectScope.ProjectId, cancellationToken);
        if (binding.DeliveryWidth != project.DeliveryWidth || binding.DeliveryHeight != project.DeliveryHeight ||
            binding.FramesPerSecond != project.FramesPerSecond ||
            !string.Equals(binding.ColorSpace, project.ColorSpace, StringComparison.OrdinalIgnoreCase))
            return RepositoryResult<SceneRenderSummary>.Conflict("The project delivery format changed after this still was frozen. Render and approve a current still first.");
        if (!string.Equals(project.ColorSpace.Trim(), "Rec.709", StringComparison.OrdinalIgnoreCase))
            return RepositoryResult<SceneRenderSummary>.Invalid("The deterministic scene renderer currently supports Rec.709 delivery projects.");
        if (project.DeliveryWidth > MaxWidth || project.DeliveryHeight > MaxHeight)
            return RepositoryResult<SceneRenderSummary>.Invalid($"Animated scene takes are bounded to {MaxWidth} x {MaxHeight} in this MVP.");
        if (shot.DurationFrames is <= 0 or > MaxFrames)
            return RepositoryResult<SceneRenderSummary>.Invalid($"Animated scene takes are bounded to 1-{MaxFrames} frames in this MVP.");

        var expectedFrames = (binding.EndTime - binding.StartTime) * binding.FramesPerSecond;
        if (Math.Abs(expectedFrames - shot.DurationFrames) > 0.5)
            return RepositoryResult<SceneRenderSummary>.Conflict("The frozen scene range no longer agrees with the shot duration.");

        var existing = await db.Jobs.AsNoTracking()
            .Where(job => job.ShotId == shot.Id && job.WorkType == WorkType && job.State == JobState.Running.ToString())
            .ToArrayAsync(cancellationToken);
        foreach (var active in existing.OrderByDescending(job => job.CreatedAt))
        {
            var activePacket = ReadPacket(active);
            if (activePacket?.BindingId == binding.Id)
                return RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(active, activePacket, cancellationToken));
        }

        var readiness = await encoder.InspectAsync(cancellationToken);
        if (!readiness.Ready)
            return RepositoryResult<SceneRenderSummary>.Unavailable(readiness.Detail);

        var assetIds = currentScene.Value.Instances
            .SelectMany(instance => new[] { instance.AssetId, instance.Clip?.ClipAssetId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var sourceAssets = await db.Assets.AsNoTracking().Where(asset => assetIds.Contains(asset.Id)).ToArrayAsync(cancellationToken);
        if (sourceAssets.Length != assetIds.Length || sourceAssets.Any(asset => !assets.StoredFileExists(asset.StoragePath)))
            return RepositoryResult<SceneRenderSummary>.Conflict("One or more frozen scene assets are no longer available. Restore them before rendering motion.");

        SceneCameraSummary startCamera;
        JsonElement sceneSnapshot;
        try
        {
            using var snapshot = JsonDocument.Parse(binding.SnapshotJson);
            sceneSnapshot = snapshot.RootElement.GetProperty("scene").Clone();
            startCamera = sceneSnapshot.GetProperty("camera").Deserialize<SceneCameraSummary>(Json)
                ?? throw new JsonException("The inspection camera is missing.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return RepositoryResult<SceneRenderSummary>.Conflict($"The frozen scene snapshot cannot be read. {exception.Message}");
        }

        var manifestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var videoCanvas = ProductionVideoContract.VideoCanvas(project.DeliveryWidth, project.DeliveryHeight, VideoQuality.Max);
        var manifestDocument = new
        {
            schema = "framewright.scene-render.v1",
            videoQuality = nameof(VideoQuality.Max),
            projectFormat = new
            {
                project.AspectRatio,
                project.FramesPerSecond,
                project.DeliveryWidth,
                project.DeliveryHeight,
                project.ColorSpace,
                project.AudioSampleRate,
            },
            videoCanvas = new { width = videoCanvas.Width, height = videoCanvas.Height },
            shot = new
            {
                shot.Id,
                shot.Code,
                version = shot.Version,
                shot.Description,
                shot.DurationFrames,
                shot.Camera,
                shot.Action,
            },
            sceneRender = new
            {
                bindingId = binding.Id,
                sceneId = binding.SceneId,
                sceneVersion = binding.SceneVersion,
                binding.SnapshotHash,
                startTime = binding.StartTime,
                endTime = binding.EndTime,
                width = binding.DeliveryWidth,
                height = binding.DeliveryHeight,
                framesPerSecond = binding.FramesPerSecond,
                frameCount = shot.DurationFrames,
                startCamera,
                endCamera = JsonSerializer.Deserialize<SceneCameraSummary>(binding.CameraJson, Json),
                renderer = "Three.js exact-frame PNG",
                encoder = readiness.Version,
                scene = sceneSnapshot,
                assets = sourceAssets.OrderBy(asset => asset.Id).Select(asset => new
                {
                    asset.Id,
                    asset.Kind,
                    asset.ContentHash,
                    asset.RevisionFamilyId,
                    asset.RevisionNumber,
                }),
            },
        };
        var manifestJson = JsonSerializer.Serialize(manifestDocument, Json);
        var manifestHash = HashText(manifestJson);
        var endCamera = JsonSerializer.Deserialize<SceneCameraSummary>(binding.CameraJson, Json)
            ?? throw new InvalidDataException("A stored scene shot camera could not be read.");
        var packet = new SceneRenderPacket(
            binding.Id, binding.SceneId, binding.ShotId, binding.ShotCode, binding.ShotVersion,
            binding.DeliveryWidth, binding.DeliveryHeight, binding.FramesPerSecond, shot.DurationFrames,
            binding.StartTime, binding.EndTime, startCamera, endCamera,
            readiness.Version, manifestHash);

        var manifest = new GenerationManifestRecord
        {
            Id = manifestId,
            ProjectId = projectScope.ProjectId,
            ShotId = shot.Id,
            ShotCode = shot.Code,
            ShotVersion = shot.Version,
            SketchId = Guid.Empty,
            SketchRevision = 0,
            Route = nameof(GenerationRoute.FastDraft),
            Purpose = nameof(GenerationPurpose.Video),
            State = nameof(ManifestState.Dispatched),
            CreativeBrief = $"Deterministic animated take from {binding.SceneName} v{binding.SceneVersion}.",
            AuthoritiesJson = "[]",
            ConstraintsJson = shot.ConstraintsJson,
            ManifestJson = manifestJson,
            ManifestHash = manifestHash,
            ProviderCallMade = false,
            CompositionAssetId = binding.StillAssetId,
            CompositionAssetHash = await db.Assets.Where(asset => asset.Id == binding.StillAssetId)
                .Select(asset => asset.ContentHash).SingleAsync(cancellationToken),
            CreatedAt = now,
        };
        var job = new JobRecord
        {
            Id = jobId,
            ProjectId = projectScope.ProjectId,
            ShotId = shot.Id,
            ShotCode = shot.Code,
            Kind = nameof(AssetKind.Video),
            State = nameof(JobState.Running),
            Progress = 0,
            Phase = "Waiting for exact scene frames",
            Backend = "Three.js + FFmpeg",
            CreatedAt = now,
            LastHeartbeatAt = now,
            ManifestId = manifestId,
            AdapterId = AdapterId,
            Attempt = 1,
            WorkType = WorkType,
            RequestJson = JsonSerializer.Serialize(packet, Json),
        };
        db.GenerationManifests.Add(manifest);
        db.Jobs.Add(job);
        AddAudit("SceneRenderPrepared", "GenerationManifest", manifest.Id, new
        {
            job.Id,
            packet.BindingId,
            packet.SceneId,
            packet.SourceShotVersion,
            packet.FrameCount,
            packet.Width,
            packet.Height,
            packet.FramesPerSecond,
            packet.EncoderVersion,
            packet.ManifestHash,
        });
        await db.SaveChangesAsync(cancellationToken);
        Directory.CreateDirectory(FramesPath(manifest.Id));
        return RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(job, packet, cancellationToken));
    }

    public async Task<RepositoryResult<SceneRenderSummary>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == jobId && row.WorkType == WorkType, cancellationToken);
        if (job is null) return RepositoryResult<SceneRenderSummary>.NotFound();
        var packet = ReadPacket(job);
        return packet is null
            ? RepositoryResult<SceneRenderSummary>.Conflict("The stored scene render packet cannot be read.")
            : RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(job, packet, cancellationToken));
    }

    public async Task<RepositoryResult<SceneRenderFrameReceipt>> UploadFrameAsync(
        Guid jobId,
        int frameIndex,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(row => row.Id == jobId && row.WorkType == WorkType, cancellationToken);
        if (job is null) return RepositoryResult<SceneRenderFrameReceipt>.NotFound();
        if (job.State != JobState.Running.ToString())
            return RepositoryResult<SceneRenderFrameReceipt>.Conflict("Only a running scene render can accept frames.");
        var packet = ReadPacket(job);
        if (packet is null) return RepositoryResult<SceneRenderFrameReceipt>.Conflict("The stored scene render packet cannot be read.");
        if (frameIndex < 0 || frameIndex >= packet.FrameCount)
            return RepositoryResult<SceneRenderFrameReceipt>.Invalid($"Frame index must be between 0 and {packet.FrameCount - 1}.");
        if (file.Length is <= 0 or > AssetStore.MaxImageBytes)
            return RepositoryResult<SceneRenderFrameReceipt>.Invalid("The rendered frame is empty or exceeds the image limit.");

        var frames = FramesPath(job.ManifestId!.Value);
        Directory.CreateDirectory(frames);
        var destination = FramePath(job.ManifestId.Value, frameIndex);
        var staging = Path.Combine(frames, $".{Guid.NewGuid():N}.png");
        try
        {
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);

            var validation = await AssetStore.ValidatePortablePayloadAsync(
                staging, AssetKind.Image, "image/png", packet.Width, packet.Height, cancellationToken);
            if (validation is not null)
                return RepositoryResult<SceneRenderFrameReceipt>.Invalid($"Frame {frameIndex} was refused because {validation}.");
            var hash = await HashFileAsync(staging, cancellationToken);
            var alreadyPresent = false;
            if (File.Exists(destination))
            {
                var existingHash = await HashFileAsync(destination, cancellationToken);
                if (!string.Equals(existingHash, hash, StringComparison.Ordinal))
                    return RepositoryResult<SceneRenderFrameReceipt>.Conflict($"Frame {frameIndex} already exists with different pixels.");
                alreadyPresent = true;
            }
            else
            {
                File.Move(staging, destination);
            }

            var uploaded = CountFrames(job.ManifestId.Value, packet.FrameCount);
            job.Progress = Math.Min(90, (int)Math.Floor(uploaded * 90d / packet.FrameCount));
            job.Phase = uploaded == packet.FrameCount ? "All frames ready to encode" : $"Captured {uploaded} of {packet.FrameCount} exact frames";
            job.LastHeartbeatAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<SceneRenderFrameReceipt>.Ok(new(
                job.Id, frameIndex, hash, alreadyPresent, uploaded, packet.FrameCount));
        }
        catch (IOException exception)
        {
            return RepositoryResult<SceneRenderFrameReceipt>.Unavailable($"The frame could not be stored. {exception.Message}");
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    public async Task<RepositoryResult<SceneRenderSummary>> CompleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(row => row.Id == jobId && row.WorkType == WorkType, cancellationToken);
        if (job is null) return RepositoryResult<SceneRenderSummary>.NotFound();
        var packet = ReadPacket(job);
        if (packet is null) return RepositoryResult<SceneRenderSummary>.Conflict("The stored scene render packet cannot be read.");
        if (job.State == JobState.Completed.ToString())
            return RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(job, packet, cancellationToken));
        if (job.State != JobState.Running.ToString())
            return RepositoryResult<SceneRenderSummary>.Conflict("Retry the failed scene render before completing it.");

        var missing = MissingFrames(job.ManifestId!.Value, packet.FrameCount);
        if (missing.Length > 0)
            return RepositoryResult<SceneRenderSummary>.Conflict($"Capture the remaining {missing.Length} frame(s) before encoding.");

        Directory.CreateDirectory(RenderPath(job.ManifestId.Value));
        var lockPath = Path.Combine(RenderPath(job.ManifestId.Value), ".finalizing");
        FileStream? renderLock = null;
        try
        {
            if (File.Exists(lockPath) && File.GetLastWriteTimeUtc(lockPath) < timeProvider.GetUtcNow().UtcDateTime - FinalizeLockLifetime)
                File.Delete(lockPath);
            try
            {
                renderLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                return RepositoryResult<SceneRenderSummary>.Conflict("This take is already being finalized.");
            }

            job.Progress = 94;
            job.Phase = "Encoding exact frames";
            job.LastHeartbeatAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            var outputPath = Path.Combine(RenderPath(job.ManifestId.Value), "render.mp4");
            var encoded = await encoder.EncodeAsync(new(
                FramesPath(job.ManifestId.Value), outputPath,
                packet.Width, packet.Height, packet.FramesPerSecond, packet.FrameCount,
                packet.EncoderVersion), cancellationToken);
            if (!encoded.Success)
                return await FailAsync(job, packet, encoded.Detail, cancellationToken);

            var validation = await videoProbe.ValidateAsync(outputPath,
                new(packet.Width, packet.Height, packet.FramesPerSecond, packet.FrameCount, "Rec.709"), cancellationToken);
            if (!validation.IsValid)
                return await FailAsync(job, packet, $"The encoded take failed its frozen media contract. {validation.Detail}", cancellationToken);

            await using var movie = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var imported = await assets.ImportGeneratedMediaAsync(
                movie,
                $"{packet.ShotCode.ToLowerInvariant()}-scene-take-v{packet.SourceShotVersion}.mp4",
                "video/mp4",
                movie.Length,
                AssetKind.Video,
                cancellationToken,
                "Scene render");
            if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
                return await FailAsync(job, packet, imported.Error ?? "The encoded take could not enter the library.", cancellationToken);

            var manifest = await db.GenerationManifests.SingleAsync(row => row.Id == job.ManifestId, cancellationToken);
            var shot = await db.Shots.SingleAsync(row => row.Id == job.ShotId, cancellationToken);
            var promoted = shot.Version == packet.SourceShotVersion &&
                shot.Approval == ApprovalState.Ratified.ToString() &&
                shot.CurrentAssetId == manifest.CompositionAssetId;
            var now = timeProvider.GetUtcNow();
            if (promoted)
            {
                var currentCandidates = await db.CandidateVersions.Where(candidate => candidate.ShotId == shot.Id && candidate.IsCurrent).ToArrayAsync(cancellationToken);
                foreach (var current in currentCandidates)
                {
                    current.IsCurrent = false;
                    current.SupersededAt = now;
                }
                var highestVersion = await db.CandidateVersions.Where(candidate => candidate.ShotId == shot.Id)
                    .MaxAsync(candidate => (int?)candidate.Version, cancellationToken) ?? shot.Version;
                shot.Version = Math.Max(shot.Version, highestVersion) + 1;
                shot.Stage = ShotStage.Video.ToString();
                shot.Approval = ApprovalState.Working.ToString();
                shot.ProductionVideoJobId = job.Id;
                shot.ProductionVideoAssetId = imported.Value.Id;
                shot.ContinuityState = "Review";
                shot.UpdatedAt = now;
                db.CandidateVersions.Add(new CandidateVersionRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectScope.ProjectId,
                    ShotId = shot.Id,
                    Version = shot.Version,
                    Stage = shot.Stage,
                    Approval = shot.Approval,
                    IsCurrent = true,
                    AssetId = shot.CurrentAssetId,
                    SourceManifestId = manifest.Id,
                    CreatedAt = now,
                });
            }

            var frameHashes = new Dictionary<int, string>();
            for (var index = 0; index < packet.FrameCount; index++)
                frameHashes[index] = await HashFileAsync(FramePath(job.ManifestId.Value, index), cancellationToken);
            job.OutputAssetId = imported.Value.Id;
            job.State = JobState.Completed.ToString();
            job.Progress = 100;
            job.Phase = promoted ? "Animated take ready for review" : "Take ready; the shot changed after capture";
            job.CompletedAt = now;
            job.LastHeartbeatAt = now;
            job.ResultJson = JsonSerializer.Serialize(new
            {
                frameHashes,
                encoder = encoded.Version,
                validation.Detail,
                validation.Metadata,
                outputAssetId = imported.Value.Id,
                promoted,
            }, Json);
            manifest.State = ManifestState.Completed.ToString();
            manifest.ProviderCallMade = false;
            AddAudit("SceneRenderCompleted", "GenerationManifest", manifest.Id, new
            {
                job.Id,
                outputAssetId = imported.Value.Id,
                promoted,
                validation.Metadata,
                packet.FrameCount,
            });
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(job, packet, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return await FailAsync(job, packet, $"Scene encoding failed safely. {exception.Message}", cancellationToken);
        }
        finally
        {
            if (renderLock is not null)
            {
                await renderLock.DisposeAsync();
                if (File.Exists(lockPath)) File.Delete(lockPath);
            }
        }
    }

    public async Task<RepositoryResult<SceneRenderSummary>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var prior = await db.Jobs.SingleOrDefaultAsync(row => row.Id == jobId && row.WorkType == WorkType, cancellationToken);
        if (prior is null) return RepositoryResult<SceneRenderSummary>.NotFound();
        if (prior.State != JobState.Failed.ToString())
            return RepositoryResult<SceneRenderSummary>.Conflict("Only a failed scene render can be retried.");
        var packet = ReadPacket(prior);
        if (packet is null) return RepositoryResult<SceneRenderSummary>.Conflict("The stored scene render packet cannot be read.");
        var readiness = await encoder.InspectAsync(cancellationToken);
        if (!readiness.Ready) return RepositoryResult<SceneRenderSummary>.Unavailable(readiness.Detail);
        if (!string.Equals(readiness.Version, packet.EncoderVersion, StringComparison.Ordinal))
            return RepositoryResult<SceneRenderSummary>.Conflict("FFmpeg changed after this take was frozen. Prepare a new render from the approved still.");

        var now = timeProvider.GetUtcNow();
        var retry = new JobRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = prior.ProjectId,
            ShotId = prior.ShotId,
            ShotCode = prior.ShotCode,
            Kind = prior.Kind,
            State = JobState.Running.ToString(),
            Progress = Math.Min(90, (int)Math.Floor(CountFrames(prior.ManifestId!.Value, packet.FrameCount) * 90d / packet.FrameCount)),
            Phase = "Resuming preserved exact frames",
            Backend = prior.Backend,
            CreatedAt = now,
            LastHeartbeatAt = now,
            ManifestId = prior.ManifestId,
            AdapterId = prior.AdapterId,
            Attempt = prior.Attempt + 1,
            RetryOfJobId = prior.Id,
            WorkType = WorkType,
            RequestJson = prior.RequestJson,
        };
        db.Jobs.Add(retry);
        AddAudit("SceneRenderRetried", "Job", retry.Id, new { priorJobId = prior.Id, retry.Attempt, retry.ManifestId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SceneRenderSummary>.Ok(await MapAsync(retry, packet, cancellationToken));
    }

    private async Task<RepositoryResult<SceneRenderSummary>> FailAsync(
        JobRecord job,
        SceneRenderPacket packet,
        string error,
        CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString();
        job.Phase = "Scene render failed";
        job.Error = error.Length <= 2_000 ? error : error[..2_000];
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastHeartbeatAt = job.CompletedAt;
        AddAudit("SceneRenderFailed", "Job", job.Id, new { job.Error, job.ManifestId, job.Attempt });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SceneRenderSummary>.Unavailable(job.Error);
    }

    private async Task<SceneRenderSummary> MapAsync(
        JobRecord job,
        SceneRenderPacket packet,
        CancellationToken cancellationToken)
    {
        var missing = job.ManifestId is null ? Enumerable.Range(0, packet.FrameCount).ToArray() : MissingFrames(job.ManifestId.Value, packet.FrameCount);
        var promoted = job.OutputAssetId is not null && await db.Shots.AsNoTracking()
            .AnyAsync(shot => shot.Id == job.ShotId && shot.ProductionVideoJobId == job.Id && shot.ProductionVideoAssetId == job.OutputAssetId, cancellationToken);
        return new(
            job.Id,
            job.ManifestId ?? Guid.Empty,
            packet.BindingId,
            packet.SceneId,
            packet.ShotId,
            packet.ShotCode,
            packet.SourceShotVersion,
            Enum.Parse<JobState>(job.State),
            job.Progress,
            job.Phase,
            job.Error,
            packet.Width,
            packet.Height,
            packet.FramesPerSecond,
            packet.FrameCount,
            packet.StartTime,
            packet.EndTime,
            packet.StartCamera,
            packet.EndCamera,
            missing,
            packet.EncoderVersion,
            packet.ManifestHash,
            job.Attempt,
            job.RetryOfJobId,
            job.OutputAssetId,
            job.OutputAssetId is null ? null : $"/api/assets/{job.OutputAssetId}/content",
            promoted);
    }

    private static SceneRenderPacket? ReadPacket(JobRecord job)
    {
        try { return JsonSerializer.Deserialize<SceneRenderPacket>(job.RequestJson ?? "", Json); }
        catch (JsonException) { return null; }
    }

    private string RenderPath(Guid manifestId) => Path.Combine(renderRoot, manifestId.ToString("N"));
    private string FramesPath(Guid manifestId) => Path.Combine(RenderPath(manifestId), "frames");
    private string FramePath(Guid manifestId, int index) => Path.Combine(FramesPath(manifestId), $"frame-{index:000000}.png");
    private int CountFrames(Guid manifestId, int count) => Enumerable.Range(0, count).Count(index => File.Exists(FramePath(manifestId, index)));
    private int[] MissingFrames(Guid manifestId, int count) => Enumerable.Range(0, count).Where(index => !File.Exists(FramePath(manifestId, index))).ToArray();

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private void AddAudit(string type, string targetType, Guid targetId, object payload) => db.AuditEvents.Add(new AuditEventRecord
    {
        Id = Guid.NewGuid(),
        Type = type,
        TargetType = targetType,
        TargetId = targetId.ToString(),
        PayloadJson = JsonSerializer.Serialize(payload, Json),
        CreatedAt = timeProvider.GetUtcNow(),
    });

    private sealed record SceneRenderPacket(
        Guid BindingId,
        Guid SceneId,
        Guid ShotId,
        string ShotCode,
        int SourceShotVersion,
        int Width,
        int Height,
        int FramesPerSecond,
        int FrameCount,
        double StartTime,
        double EndTime,
        SceneCameraSummary StartCamera,
        SceneCameraSummary EndCamera,
        string EncoderVersion,
        string ManifestHash);
}
