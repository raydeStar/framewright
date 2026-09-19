using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Persists voice and music work before any local or remote generator is called.
/// The browser may navigate away, the workstation may restart, and a second app
/// process may wake up; the Jobs ledger remains the sole owner of the request.
/// </summary>
public sealed class AudioGenerationJobService(
    StudioDbContext db,
    VoiceSynthesisService voices,
    QwenVoiceService qwen,
    MusicGenerationService music,
    IProjectScope projectScope,
    RuntimeReadinessService readiness,
    TimeProvider timeProvider)
{
    public const string VoiceWorkType = "Voice";
    public const string MusicWorkType = "Music";
    public const string VoiceDesignWorkType = "VoiceDesign";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RepositoryResult<JobSummary>> EnqueueVoiceAsync(
        Guid clipId,
        SynthesizeVoiceRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateVoiceAsync(clipId, request, cancellationToken);
        if (validation.Error is not null)
        {
            return validation.Kind switch
            {
                RepositoryResultKind.NotFound => RepositoryResult<JobSummary>.NotFound(),
                RepositoryResultKind.Conflict => RepositoryResult<JobSummary>.Conflict(validation.Error),
                _ => RepositoryResult<JobSummary>.Invalid(validation.Error)
            };
        }

        var packet = JsonSerializer.Serialize(new VoiceJobRequest(clipId, request), Json);
        return await EnqueueAsync(
            VoiceWorkType,
            "VOICE",
            "Character dialogue",
            validation.Backend!,
            validation.AdapterId!,
            packet,
            $"project:{projectScope.ProjectId:N}:voice:{clipId:N}:{request.ExpectedUpdatedAt.UtcTicks}",
            cancellationToken);
    }

    public async Task<RepositoryResult<JobSummary>> EnqueueMusicAsync(
        RenderMusicRequest request,
        CancellationToken cancellationToken)
    {
        var status = await music.StatusAsync(cancellationToken);
        if (!status.CanRender)
        {
            return RepositoryResult<JobSummary>.Invalid(status.Detail);
        }
        if (!await db.MusicCompositionRevisions.AsNoTracking().AnyAsync(item => item.Id == request.RevisionId, cancellationToken))
            return RepositoryResult<JobSummary>.NotFound();

        var packet = JsonSerializer.Serialize(request, Json);
        return await EnqueueAsync(
            MusicWorkType,
            "MUSIC",
            "YuE2 composition render",
            status.Model,
            "yue2.local",
            packet,
            $"project:{projectScope.ProjectId:N}:music-revision:{request.RevisionId:N}:{Hash(packet)}",
            cancellationToken);
    }

    public async Task<RepositoryResult<JobSummary>> EnqueueVoiceDesignAsync(
        string referenceId,
        DesignVoiceAuditionsRequest request,
        CancellationToken cancellationToken)
    {
        var character = await db.References.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == referenceId && item.Category == "Character",
            cancellationToken);
        if (character is null)
        {
            return RepositoryResult<JobSummary>.NotFound();
        }

        var direction = request.Direction?.Trim() ?? string.Empty;
        var text = request.CalibrationText?.Trim() ?? string.Empty;
        if (direction.Length is < 20 or > 1_000)
        {
            return RepositoryResult<JobSummary>.Invalid("Voice direction must contain 20 to 1,000 characters.");
        }

        if (text.Length is < 20 or > 150)
        {
            return RepositoryResult<JobSummary>.Invalid("Audition text must contain 20 to 150 characters so the approved identity remains portable.");
        }

        if (request.Count is < 1 or > 3)
        {
            return RepositoryResult<JobSummary>.Invalid("Generate between one and three auditions at a time.");
        }

        var status = voices.Status();
        if (!status.LocalCanSynthesize)
        {
            return RepositoryResult<JobSummary>.Invalid(status.LocalDetail ?? "The local Qwen voice worker is not ready.");
        }

        var normalized = new VoiceDesignJobRequest(referenceId, new(direction, text, request.Count));
        var packet = JsonSerializer.Serialize(normalized, Json);
        return await EnqueueAsync(
            VoiceDesignWorkType,
            "VOICE",
            $"Voice auditions · {character.Name}",
            status.LocalModel ?? QwenVoiceService.DesignModel,
            $"qwen3-tts.design:{referenceId}",
            packet,
            $"project:{projectScope.ProjectId:N}:voice-design:{referenceId}:{Hash(packet)}",
            cancellationToken);
    }

    public async Task<RepositoryResult<JobSummary>> RetryAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var failed = await db.Jobs.SingleOrDefaultAsync(
            job => job.Id == jobId &&
                (job.WorkType == VoiceWorkType || job.WorkType == MusicWorkType || job.WorkType == VoiceDesignWorkType),
            cancellationToken);
        if (failed is null)
        {
            return RepositoryResult<JobSummary>.NotFound();
        }

        if (failed.State is not ("Failed" or "Cancelled"))
        {
            return RepositoryResult<JobSummary>.Conflict("Only failed or cancelled audio generation can be retried.");
        }

        if (string.IsNullOrWhiteSpace(failed.RequestJson))
        {
            return RepositoryResult<JobSummary>.Conflict("This audio job no longer has its frozen request packet.");
        }

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var nextAttempt = await db.Jobs
            .Where(job => job.RetryOfJobId == failed.Id || job.Id == failed.Id)
            .MaxAsync(job => (int?)job.Attempt, cancellationToken) ?? failed.Attempt;
        var now = timeProvider.GetUtcNow();
        var retry = new JobRecord
        {
            Id = Guid.NewGuid(),
            ShotId = Guid.Empty,
            ShotCode = failed.ShotCode,
            Kind = failed.Kind,
            WorkType = failed.WorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Retry queued from the frozen audio request",
            Backend = failed.Backend,
            AdapterId = failed.AdapterId,
            RequestJson = failed.RequestJson,
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = nextAttempt + 1,
            RetryOfJobId = failed.Id,
            IdempotencyKey = $"audio-retry:{failed.Id:N}:{nextAttempt + 1}"
        };
        db.Jobs.Add(retry);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(retry));
    }

    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await db.Jobs.IgnoreQueryFilters()
            .Where(job => (job.WorkType == VoiceWorkType || job.WorkType == MusicWorkType || job.WorkType == VoiceDesignWorkType) &&
                (job.State == JobState.Queued.ToString() || job.State == JobState.Running.ToString()))
            .ToListAsync(cancellationToken);
        var recoverable = new List<Guid>();
        foreach (var job in candidates.Where(job =>
                     job.State == JobState.Queued.ToString() ||
                     job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now))
        {
            // An external provider may already have accepted this request. Neither
            // OpenAI speech nor arbitrary ComfyUI graphs promise a resumable query,
            // so failing visibly is safer than producing and billing a duplicate.
            if (!string.IsNullOrWhiteSpace(job.ProviderRequestId))
            {
                job.State = JobState.Failed.ToString();
                job.Progress = 100;
                job.Phase = "Needs an explicit retry after restart";
                job.Error = "Framewright restarted after the audio provider may have accepted this request. It was not resubmitted automatically; retry explicitly after checking the provider output.";
                job.CompletedAt = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                continue;
            }

            job.State = JobState.Queued.ToString();
            job.Progress = 0;
            job.Phase = "Recovered before provider submission";
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
        var owner = await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.Id == jobId)
            .Select(job => job.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);
        if (owner == Guid.Empty)
        {
            return;
        }

        projectScope.Bind(owner);
        var job = await db.Jobs.SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);
        if (job is null || job.State != JobState.Running.ToString())
        {
            return;
        }

        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
        {
            await FailAsync(job, admission.Error ?? "Generation prerequisites are not ready.", cancellationToken);
            return;
        }

        try
        {
            job.Progress = 10;
            job.Phase = "Validating frozen audio request";
            job.LastHeartbeatAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            RepositoryResult<Guid> output;
            if (job.WorkType == VoiceWorkType)
            {
                var packet = JsonSerializer.Deserialize<VoiceJobRequest>(job.RequestJson ?? string.Empty, Json)
                    ?? throw new InvalidOperationException("The frozen voice request is invalid.");
                output = await RunVoiceAsync(job, packet, cancellationToken);
            }
            else if (job.WorkType == MusicWorkType)
            {
                var packet = JsonSerializer.Deserialize<RenderMusicRequest>(job.RequestJson ?? string.Empty, Json)
                    ?? throw new InvalidOperationException("The frozen music request is invalid.");
                output = await RunMusicAsync(job, packet, cancellationToken);
            }
            else if (job.WorkType == VoiceDesignWorkType)
            {
                var packet = JsonSerializer.Deserialize<VoiceDesignJobRequest>(job.RequestJson ?? string.Empty, Json)
                    ?? throw new InvalidOperationException("The frozen voice-design request is invalid.");
                output = await RunVoiceDesignAsync(job, packet, cancellationToken);
            }
            else
            {
                output = RepositoryResult<Guid>.Invalid($"Unsupported audio work type {job.WorkType}.");
            }

            if (output.Kind != RepositoryResultKind.Ok || output.Value == Guid.Empty)
            {
                await FailAsync(job, output.Error ?? "Audio generation did not return media.", cancellationToken);
                return;
            }

            job.OutputAssetId = output.Value;
            job.State = JobState.Completed.ToString();
            job.Progress = 100;
            job.Phase = "Audio ready";
            job.CompletedAt = timeProvider.GetUtcNow();
            job.LastHeartbeatAt = job.CompletedAt;
            job.Error = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            await FailAsync(job, Bound(error.Message), cancellationToken);
        }
    }

    private async Task<RepositoryResult<Guid>> RunVoiceAsync(
        JobRecord job,
        VoiceJobRequest packet,
        CancellationToken cancellationToken)
    {
        job.Progress = 25;
        job.Phase = "Synthesizing character dialogue";
        await db.SaveChangesAsync(cancellationToken);
        var result = await voices.SynthesizeAsync(
            packet.ClipId,
            packet.Request,
            cancellationToken,
            (providerRequestId, token) => RecordProviderAcceptanceAsync(job, providerRequestId, token));
        if (result.Kind != RepositoryResultKind.Ok || result.Value is null)
        {
            return new(default, result.Kind, result.Error);
        }

        return result.Value.Clip.AssetId is { } assetId
            ? RepositoryResult<Guid>.Ok(assetId)
            : RepositoryResult<Guid>.Invalid("Voice synthesis completed without attaching an audio asset.");
    }

    private async Task<RepositoryResult<Guid>> RunMusicAsync(
        JobRecord job,
        RenderMusicRequest packet,
        CancellationToken cancellationToken)
    {
        job.Progress = 25;
        job.Phase = "Preparing composition";
        await db.SaveChangesAsync(cancellationToken);
        var result = await music.RenderAsync(
            packet,
            job.Id,
            cancellationToken,
            (providerRequestId, token) => RecordProviderAcceptanceAsync(job, providerRequestId, token),
            async (phase, token) =>
            {
                job.Phase = phase;
                job.Progress = phase == "Processing audio" ? 85 : phase == "Rendering" ? 55 : 30;
                job.LastHeartbeatAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(token);
            });
        return result.Kind == RepositoryResultKind.Ok && result.Value is not null
            ? RepositoryResult<Guid>.Ok(result.Value.Id)
            : new(default, result.Kind, result.Error);
    }

    private async Task<RepositoryResult<Guid>> RunVoiceDesignAsync(
        JobRecord job,
        VoiceDesignJobRequest packet,
        CancellationToken cancellationToken)
    {
        job.Progress = 25;
        job.Phase = "Designing local character voice auditions";
        await db.SaveChangesAsync(cancellationToken);
        await RecordProviderAcceptanceAsync(
            job,
            $"local-qwen-design:{packet.ReferenceId}:{job.Id:N}",
            cancellationToken);
        var result = await qwen.DesignAuditionsAsync(packet.ReferenceId, packet.Request, cancellationToken);
        if (result.Kind != RepositoryResultKind.Ok || result.Value is null || result.Value.Count == 0)
        {
            return new(default, result.Kind, result.Error);
        }

        job.ResultJson = JsonSerializer.Serialize(result.Value, Json);
        return RepositoryResult<Guid>.Ok(result.Value[0].AssetId);
    }

    private async Task RecordProviderAcceptanceAsync(
        JobRecord job,
        string providerRequestId,
        CancellationToken cancellationToken)
    {
        job.ProviderRequestId = providerRequestId;
        job.Progress = Math.Max(job.Progress, 35);
        job.Phase = "Provider accepted audio request";
        job.LastHeartbeatAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(RepositoryResultKind Kind, string? Error, string? Backend, string? AdapterId)> ValidateVoiceAsync(
        Guid clipId,
        SynthesizeVoiceRequest request,
        CancellationToken cancellationToken)
    {
        var clip = await db.TimelineClips.AsNoTracking().SingleOrDefaultAsync(item => item.Id == clipId, cancellationToken);
        if (clip is null)
        {
            return (RepositoryResultKind.NotFound, "The voice clip does not exist.", null, null);
        }

        if (clip.UpdatedAt != request.ExpectedUpdatedAt)
        {
            return (RepositoryResultKind.Conflict, "The voice clip changed. Refresh before synthesizing it.", null, null);
        }

        if (clip.Track != TimelineTrackKind.Voice.ToString() || clip.VoiceProfileId is null || string.IsNullOrWhiteSpace(clip.Text))
        {
            return (RepositoryResultKind.Invalid, "Only a saved Voice clip with dialogue and a reusable profile can be synthesized.", null, null);
        }

        var profile = await db.VoiceProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == clip.VoiceProfileId, cancellationToken);
        if (profile is null)
        {
            return (RepositoryResultKind.Invalid, "The voice profile no longer exists.", null, null);
        }

        var status = voices.Status();
        if (profile.Provider.Equals(QwenVoiceService.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return status.LocalCanSynthesize
                ? (RepositoryResultKind.Ok, null, status.LocalModel ?? "Local Qwen voice", "qwen3-tts.local")
                : (RepositoryResultKind.Invalid, status.LocalDetail ?? "The local Qwen voice worker is not ready.", null, null);
        }

        if (profile.Kind != VoiceProfileKind.ProviderPreset.ToString() || !profile.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return (RepositoryResultKind.Invalid, "This voice route is not configured for synthesis.", null, null);
        }

        return status.CanSynthesize
            ? (RepositoryResultKind.Ok, null, status.Model, "openai.speech")
            : (RepositoryResultKind.Invalid, status.Detail, null, null);
    }

    private async Task<RepositoryResult<JobSummary>> EnqueueAsync(
        string workType,
        string code,
        string kind,
        string backend,
        string adapterId,
        string requestJson,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "Generation prerequisites are not ready.");

        var active = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(
            job => job.IdempotencyKey == idempotencyKey &&
                (job.State == JobState.Queued.ToString() || job.State == JobState.Running.ToString()),
            cancellationToken);
        if (active is not null)
        {
            return RepositoryResult<JobSummary>.Conflict("This exact audio request is already queued or running.");
        }

        var now = timeProvider.GetUtcNow();
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            ShotId = Guid.Empty,
            ShotCode = code,
            Kind = kind,
            WorkType = workType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen audio request queued",
            Backend = backend,
            AdapterId = adapterId,
            RequestJson = requestJson,
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
            IdempotencyKey = idempotencyKey
        };
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(job).State = EntityState.Detached;
            return RepositoryResult<JobSummary>.Conflict("This exact audio request was queued by another Framewright process.");
        }

        return RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    private async Task FailAsync(JobRecord job, string error, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString();
        job.Progress = 100;
        job.Phase = "Audio generation failed";
        job.Error = Bound(error);
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastHeartbeatAt = job.CompletedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Bound(string value) => value.Length <= 1_000 ? value : value[..1_000] + "...";
    private static JobSummary MapJob(JobRecord job) => new(
        job.Id, job.ShotId, job.ShotCode, job.Kind, Enum.Parse<JobState>(job.State), job.Progress,
        job.Phase, job.Backend, job.CreatedAt, job.CompletedAt, job.Error, job.ManifestId,
        job.AdapterId, job.OutputAssetId, job.OutputAssetId is null ? null : $"/api/assets/{job.OutputAssetId}/content",
        job.ProviderRequestId, job.Attempt, job.RetryOfJobId, job.LastHeartbeatAt, job.WorkType);

    private sealed record VoiceJobRequest(Guid ClipId, SynthesizeVoiceRequest Request);
    private sealed record VoiceDesignJobRequest(string ReferenceId, DesignVoiceAuditionsRequest Request);
}
