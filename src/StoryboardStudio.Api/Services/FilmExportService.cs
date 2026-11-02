using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Renders the sequence as one finished film: each shot's best picture in
/// board order, the editorial mix, optional fades and soft captions, at the
/// project's delivery size, frame rate and exact length.
///
/// What goes in is frozen when the artist presses Render: the exact assets
/// (by content hash), clip placements and caption text. The render is a
/// durable job like any other, recovered after a restart and retried from the
/// same packet, and its output is checked against the delivery contract
/// before it enters the asset library. Nothing is generated and no provider is
/// called; shots and clips are only read.
/// </summary>
public sealed class FilmExportService(
    StudioDbContext db,
    AssetStore assets,
    AudioMasteringService audio,
    IFilmEncoder encoder,
    IVideoMediaProbe videoProbe,
    IProjectScope projectScope,
    RuntimeReadinessService readiness,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public const string WorkType = "Film";
    public const string AdapterId = "framewright-film";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ShotSource(ShotRecord Shot, FilmShotSource Summary, AssetRecord? Asset);
    private sealed record FilmPacket(
        int Schema, string Sequence, int Width, int Height, int Fps, string ColorSpace, int SampleRate,
        IReadOnlyList<PacketShot> Shots, IReadOnlyList<PacketClip> Audio, IReadOnlyList<FilmCaptions.Cue> Captions, FilmExportRequest Options)
    {
        public int TotalFrames => Shots.Sum(shot => shot.Frames);
    }
    private sealed record PacketShot(Guid ShotId, string Code, int Frames, string Source, string Detail, Guid? AssetId, string? AssetHash);
    private sealed record PacketClip(Guid AssetId, string AssetHash, int StartFrame, int DurationFrames, double TrimStartSeconds, double Volume);

    public async Task<FilmExportStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var sources = await ResolveShotsAsync(cancellationToken);
        var (clips, captions) = await ResolveAudioAsync(sources.Sum(source => source.Shot.DurationFrames), cancellationToken);
        var refusal = Refusal(project, sources);
        return new(refusal is null, refusal ?? ReadyDetail(sources), sources.Sum(source => source.Shot.DurationFrames), project.FramesPerSecond,
            project.DeliveryWidth, project.DeliveryHeight, sources.Select(source => source.Summary).ToArray(), clips.Count, captions.Count,
            sources.Count(source => source.Summary.Source == "Production" && source.Shot.Approval == nameof(ApprovalState.Ratified)));
    }

    public async Task<RepositoryResult<JobSummary>> EnqueueAsync(FilmExportRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var sources = await ResolveShotsAsync(cancellationToken);
        if (sources.Count == 0) return RepositoryResult<JobSummary>.Invalid("Add at least one shot before rendering a film.");
        var tool = encoder.Inspect();
        if (!tool.Ready) return RepositoryResult<JobSummary>.Unavailable(tool.Detail);
        var refusal = Refusal(project, sources);
        if (refusal is not null) return RepositoryResult<JobSummary>.Invalid(refusal);
        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "The workstation is not ready for new work.");

        var totalFrames = sources.Sum(source => source.Shot.DurationFrames);
        var (clips, captions) = await ResolveAudioAsync(totalFrames, cancellationToken);
        var packet = new FilmPacket(1, $"{project.SequenceCode} {project.SequenceName}".Trim(), project.DeliveryWidth, project.DeliveryHeight,
            project.FramesPerSecond, project.ColorSpace, project.AudioSampleRate,
            sources.Select(source => new PacketShot(source.Shot.Id, source.Shot.Code, source.Shot.DurationFrames, source.Summary.Source, source.Summary.Detail, source.Asset?.Id, source.Asset?.ContentHash)).ToArray(),
            clips, request.Captions ? captions : [], request);
        var requestJson = JsonSerializer.Serialize(packet, Json);
        var key = $"film:{projectScope.ProjectId:N}:{Hash(requestJson)}";
        // Pressing Render twice for the same cut follows the render already underway.
        var active = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(job => job.IdempotencyKey == key
            && (job.State == JobState.Queued.ToString() || job.State == JobState.Running.ToString()), cancellationToken);
        if (active is not null) return RepositoryResult<JobSummary>.Ok(MapJob(active));

        var now = timeProvider.GetUtcNow();
        var job = new JobRecord
        {
            Id = Guid.NewGuid(), ShotId = Guid.Empty, ShotCode = project.SequenceCode, Kind = "Film", WorkType = WorkType,
            State = JobState.Queued.ToString(), Progress = 0, Phase = "Film render queued", Backend = "FFmpeg film render",
            AdapterId = AdapterId, RequestJson = requestJson, CreatedAt = now, LastHeartbeatAt = now, Attempt = 1, IdempotencyKey = key
        };
        db.Jobs.Add(job);
        var audit = new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "FilmRenderQueued", TargetType = "Project", TargetId = projectScope.ProjectId.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { job.Id, shots = packet.Shots.Count, totalFrames, audioClips = clips.Count, captions = packet.Captions.Count, request }, Json),
            CreatedAt = now
        };
        db.AuditEvents.Add(audit);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            // Another press of Render queued the same cut a moment earlier: follow it.
            db.Entry(job).State = EntityState.Detached;
            db.Entry(audit).State = EntityState.Detached;
            var underway = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(item => item.IdempotencyKey == key
                && (item.State == JobState.Queued.ToString() || item.State == JobState.Running.ToString()), cancellationToken);
            return underway is not null ? RepositoryResult<JobSummary>.Ok(MapJob(underway)) : RepositoryResult<JobSummary>.Conflict("The film render could not be queued. Try again.");
        }
        return RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    public async Task<RepositoryResult<JobSummary>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var failed = await db.Jobs.SingleOrDefaultAsync(job => job.Id == jobId && job.WorkType == WorkType, cancellationToken);
        if (failed is null) return RepositoryResult<JobSummary>.NotFound();
        if (failed.State is not ("Failed" or "Cancelled")) return RepositoryResult<JobSummary>.Conflict("Only a failed or cancelled film render can be retried.");
        if (string.IsNullOrWhiteSpace(failed.RequestJson)) return RepositoryResult<JobSummary>.Conflict("This film render no longer has its frozen packet.");
        var tool = encoder.Inspect();
        if (!tool.Ready) return RepositoryResult<JobSummary>.Unavailable(tool.Detail);
        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<JobSummary>.Unavailable(admission.Error ?? "The workstation is not ready for new work.");
        var nextAttempt = await db.Jobs.Where(job => job.RetryOfJobId == failed.Id || job.Id == failed.Id).MaxAsync(job => (int?)job.Attempt, cancellationToken) ?? failed.Attempt;
        var now = timeProvider.GetUtcNow();
        var retry = new JobRecord
        {
            Id = Guid.NewGuid(), ShotId = Guid.Empty, ShotCode = failed.ShotCode, Kind = failed.Kind, WorkType = WorkType,
            State = JobState.Queued.ToString(), Progress = 0, Phase = "Retry queued from the frozen film packet", Backend = failed.Backend,
            AdapterId = failed.AdapterId, RequestJson = failed.RequestJson, CreatedAt = now, LastHeartbeatAt = now,
            Attempt = nextAttempt + 1, RetryOfJobId = failed.Id, IdempotencyKey = $"film-retry:{failed.Id:N}:{nextAttempt + 1}"
        };
        db.Jobs.Add(retry);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(MapJob(retry));
    }

    /// <summary>A film render calls no provider, so an interrupted one simply
    /// starts again from its frozen packet.</summary>
    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var jobs = await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.WorkType == WorkType && (job.State == JobState.Queued.ToString() || job.State == JobState.Running.ToString()))
            .ToListAsync(cancellationToken);
        var recoverable = new List<Guid>();
        foreach (var job in jobs.Where(job => job.State == JobState.Queued.ToString() || job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now))
        {
            job.State = JobState.Queued.ToString(); job.Progress = 0; job.Phase = "Film render recovered after restart";
            job.LastHeartbeatAt = now; job.LeaseOwner = null; job.LeaseExpiresAt = null;
            recoverable.Add(job.Id);
        }
        await db.SaveChangesAsync(cancellationToken);
        return recoverable;
    }

    public async Task RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var owner = await db.Jobs.IgnoreQueryFilters().Where(job => job.Id == jobId).Select(job => job.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (owner == Guid.Empty) return;
        projectScope.Bind(owner);
        var job = await db.Jobs.SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken);
        if (job is null || job.State != JobState.Running.ToString()) return;

        var work = Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "exports", $"film-{job.Id:N}");
        try
        {
            var packet = JsonSerializer.Deserialize<FilmPacket>(job.RequestJson ?? "", Json) ?? throw new InvalidOperationException("The frozen film packet is unreadable.");
            await ReportAsync(job, 5, "Checking the frozen film inputs", cancellationToken);
            var pictures = new List<FilmSegment>();
            foreach (var shot in packet.Shots)
            {
                if (shot.AssetId is not { } assetId) { pictures.Add(new("Black", null, shot.Frames, shot.Code)); continue; }
                var asset = await FrozenAssetAsync(assetId, shot.AssetHash, cancellationToken)
                    ?? throw new InvalidOperationException($"{shot.Code}'s frozen picture is missing or changed. Render the film again from the current sequence.");
                pictures.Add(new(asset.Kind == nameof(AssetKind.Video) ? "Video" : "Still", assets.ResolveContentPath(asset), shot.Frames, shot.Code));
            }

            Directory.CreateDirectory(work);
            string? mixPath = null;
            if (packet.Audio.Count > 0)
            {
                await ReportAsync(job, 10, "Mixing the editorial audio", cancellationToken);
                var inputs = new List<AudioMixInput>();
                foreach (var clip in packet.Audio)
                {
                    var asset = await FrozenAssetAsync(clip.AssetId, clip.AssetHash, cancellationToken)
                        ?? throw new InvalidOperationException("An audio clip in the frozen mix is missing or changed. Render the film again from the current timeline.");
                    inputs.Add(new(assets.ResolveContentPath(asset), clip.StartFrame, clip.DurationFrames, clip.TrimStartSeconds, clip.Volume));
                }
                var ffmpeg = audio.ResolveFfmpeg() ?? throw new InvalidOperationException("FFmpeg was not found, so the audio could not be mixed.");
                mixPath = Path.Combine(work, "mix.wav");
                var mixed = await AudioMasteringService.RenderMixAsync(ffmpeg, inputs, packet.Fps, packet.SampleRate, packet.TotalFrames / (double)packet.Fps, mixPath, cancellationToken);
                if (mixed is not null) throw new InvalidOperationException(mixed);
            }
            string? captionsPath = null;
            if (packet.Captions.Count > 0)
            {
                captionsPath = Path.Combine(work, "captions.srt");
                await File.WriteAllTextAsync(captionsPath, FilmCaptions.ToSrt(packet.Captions, packet.Fps), new UTF8Encoding(false), cancellationToken);
            }

            var rec709 = string.Equals(packet.ColorSpace.Trim(), "Rec.709", StringComparison.OrdinalIgnoreCase);
            var plan = new FilmPlan(packet.Width, packet.Height, packet.Fps, rec709, pictures, mixPath, captionsPath, packet.Options.Fades, packet.Options.NormalizeLoudness);
            var output = Path.Combine(work, "film.mp4");
            await ReportAsync(job, 15, "Encoding the film", cancellationToken);
            var lastPercent = 15;
            var encoded = await encoder.EncodeAsync(plan, output, async fraction =>
            {
                var percent = 15 + (int)(73 * fraction);
                if (percent <= lastPercent) return;
                lastPercent = percent;
                await ReportAsync(job, percent, $"Encoding the film · {(int)(fraction * 100)}%", cancellationToken);
            }, cancellationToken);
            if (!encoded.Succeeded) throw new InvalidOperationException(encoded.Detail);

            await ReportAsync(job, 90, "Checking the film against the delivery format", cancellationToken);
            var probe = await videoProbe.ValidateAsync(output, new VideoMediaExpectation(packet.Width, packet.Height, packet.Fps, packet.TotalFrames, rec709 ? packet.ColorSpace : null), cancellationToken);
            if (!probe.IsValid) throw new InvalidOperationException($"The rendered film does not match the delivery format. {probe.Detail}");

            await ReportAsync(job, 95, "Adding the film to the library", cancellationToken);
            var fileName = $"{FileSafe(packet.Sequence)} film {timeProvider.GetLocalNow():yyyy-MM-dd HHmm}.mp4";
            AssetSummary film;
            await using (var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var imported = await assets.ImportGeneratedMediaAsync(stream, fileName, "video/mp4", stream.Length, AssetKind.Video, cancellationToken, "Film render");
                if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null) throw new InvalidOperationException(imported.Error ?? "The rendered film could not be stored.");
                film = imported.Value;
            }

            job.OutputAssetId = film.Id;
            job.ResultJson = JsonSerializer.Serialize(new
            {
                fileName,
                seconds = packet.TotalFrames / (double)packet.Fps,
                frames = packet.TotalFrames,
                packet.Width, packet.Height, packet.Fps,
                shots = packet.Shots.Select(shot => new { shot.Code, shot.Source, shot.Detail }),
                captions = packet.Captions.Count,
                audioClips = packet.Audio.Count,
                loudness = encoded.LoudnessNormalized ? "Normalized to -16 LUFS" : packet.Audio.Count == 0 ? "Silent" : "Left as mixed"
            }, Json);
            job.State = JobState.Completed.ToString(); job.Progress = 100; job.Phase = "Film ready"; job.Error = null;
            job.CompletedAt = timeProvider.GetUtcNow(); job.LastHeartbeatAt = job.CompletedAt;
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid(), Type = "FilmRendered", TargetType = "Asset", TargetId = film.Id.ToString(),
                PayloadJson = JsonSerializer.Serialize(new { jobId = job.Id, film.ContentHash, packet.TotalFrames }, Json), CreatedAt = job.CompletedAt.Value
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
        {
            job.State = JobState.Failed.ToString(); job.Progress = 100; job.Phase = "Film render failed"; job.Error = Bound(error.Message);
            job.CompletedAt = timeProvider.GetUtcNow(); job.LastHeartbeatAt = job.CompletedAt;
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Each shot's best picture: its Max production take, else its latest video
    /// take, else its current image held, else black. The artist sees which.
    /// </summary>
    private async Task<IReadOnlyList<ShotSource>> ResolveShotsAsync(CancellationToken cancellationToken)
    {
        var shots = await db.Shots.AsNoTracking().OrderBy(shot => shot.SortOrder).ThenBy(shot => shot.Code).ToListAsync(cancellationToken);
        // A take counts only while it is the shot's live head.
        var heads = (await db.CandidateVersions.AsNoTracking().Where(candidate => candidate.IsCurrent)
                .Select(candidate => new { candidate.ShotId, candidate.SourceManifestId }).ToListAsync(cancellationToken))
            .GroupBy(candidate => candidate.ShotId).ToDictionary(group => group.Key, group => group.First().SourceManifestId);
        var takes = (await db.Jobs.AsNoTracking()
                .Where(job => job.WorkType == "Shot" && job.Kind == "Video" && job.State == JobState.Completed.ToString() && job.OutputAssetId != null)
                .ToListAsync(cancellationToken))
            .GroupBy(job => job.ShotId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.CompletedAt ?? job.CreatedAt).First());
        var assetIds = shots.SelectMany(shot => new[] { shot.ProductionVideoAssetId, shot.CurrentAssetId })
            .Concat(takes.Values.Select(job => job.OutputAssetId)).Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var media = await db.Assets.AsNoTracking().Where(asset => assetIds.Contains(asset.Id)).ToDictionaryAsync(asset => asset.Id, cancellationToken);
        var manifestIds = takes.Values.Where(job => job.ManifestId is not null).Select(job => job.ManifestId!.Value).ToArray();
        var manifests = await db.GenerationManifests.AsNoTracking().Where(manifest => manifestIds.Contains(manifest.Id)).ToDictionaryAsync(manifest => manifest.Id, manifest => manifest.ManifestJson, cancellationToken);

        AssetRecord? Usable(Guid? id, AssetKind kind) => id is { } value && media.TryGetValue(value, out var asset)
            && asset.Kind == kind.ToString() && assets.StoredFileExists(asset.StoragePath) ? asset : null;
        var fps = await db.Projects.AsNoTracking().Where(x => x.Id == projectScope.ProjectId).Select(x => x.FramesPerSecond).SingleAsync(cancellationToken);
        var resolved = new List<ShotSource>();
        foreach (var shot in shots)
        {
            var seconds = (shot.DurationFrames / (double)Math.Max(1, fps)).ToString("0.#", CultureInfo.InvariantCulture);
            FilmShotSource Summary(string source, string detail, Guid? asset) => new(shot.Id, shot.Code, shot.Title, shot.DurationFrames, source, detail, asset);
            if (Usable(shot.ProductionVideoAssetId, AssetKind.Video) is { } production)
            {
                var approved = shot.Approval == nameof(ApprovalState.Ratified);
                resolved.Add(new(shot, Summary("Production", approved ? "Approved Max take" : "Max take, not yet approved", production.Id), production));
            }
            else if (takes.TryGetValue(shot.Id, out var take) && take.ManifestId is { } takeManifest
                && heads.TryGetValue(shot.Id, out var head) && head == takeManifest
                && Usable(take.OutputAssetId, AssetKind.Video) is { } latest)
                resolved.Add(new(shot, Summary("Take", $"Latest {Quality(take, manifests)} video take", latest.Id), latest));
            else if (Usable(shot.CurrentAssetId, AssetKind.Image) is { } still)
                // A take of an earlier version would show a frame the artist has moved on from.
                resolved.Add(new(shot, Summary("Still", takes.ContainsKey(shot.Id)
                    ? $"Its video take is from an earlier version of the shot; its current image is held for {seconds} s"
                    : $"No video yet; its current image is held for {seconds} s", still.Id), still));
            else
                resolved.Add(new(shot, Summary("Missing", $"No picture yet; black for {seconds} s", null), null));
        }
        return resolved;
    }

    private static string Quality(JobRecord take, Dictionary<Guid, string> manifests)
    {
        if (take.ManifestId is not { } id || !manifests.TryGetValue(id, out var json)) return "";
        try
        {
            using var manifest = JsonDocument.Parse(json);
            return manifest.RootElement.TryGetProperty("videoQuality", out var quality) && quality.GetString() is { Length: > 0 } value ? value : "";
        }
        catch (JsonException) { return ""; }
    }

    private async Task<(IReadOnlyList<PacketClip> Clips, IReadOnlyList<FilmCaptions.Cue> Captions)> ResolveAudioAsync(int totalFrames, CancellationToken cancellationToken)
    {
        var clips = await db.TimelineClips.AsNoTracking().OrderBy(clip => clip.StartFrame).ToListAsync(cancellationToken);
        var ids = clips.Where(clip => clip.AssetId is not null).Select(clip => clip.AssetId!.Value).Distinct().ToArray();
        var media = await db.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id) && asset.Kind == AssetKind.Audio.ToString()).ToDictionaryAsync(asset => asset.Id, cancellationToken);
        var placed = clips
            .Where(clip => clip.AssetId is { } id && media.ContainsKey(id) && clip.StartFrame < totalFrames)
            .Select(clip => new PacketClip(clip.AssetId!.Value, media[clip.AssetId!.Value].ContentHash, clip.StartFrame, clip.DurationFrames, clip.TrimStartSeconds, clip.Volume))
            .ToArray();
        var captions = clips
            .Where(clip => (clip.Track == nameof(TimelineTrackKind.Voice) || clip.Track == nameof(TimelineTrackKind.Dialogue))
                && !string.IsNullOrWhiteSpace(clip.Text) && clip.StartFrame < totalFrames)
            .Select(clip => new FilmCaptions.Cue(clip.StartFrame, Math.Min(totalFrames, clip.StartFrame + clip.DurationFrames), clip.Text.Trim()))
            .ToArray();
        return (placed, captions);
    }

    private string? Refusal(ProjectRecord project, IReadOnlyList<ShotSource> sources)
    {
        if (sources.Count == 0) return "Add at least one shot before rendering a film.";
        var readiness = encoder.Inspect();
        if (!readiness.Ready) return readiness.Detail;
        // The asset store holds media up to its limit; the encoder caps the
        // video rate, which bounds how long a film can be.
        var seconds = sources.Sum(source => source.Shot.DurationFrames) / (double)project.FramesPerSecond;
        const double bitsPerSecond = FfmpegFilmEncoder.MaxVideoBitsPerSecond + 256_000;
        if (seconds * bitsPerSecond / 8 > AssetStore.MaxMediaBytes * 0.95)
            return $"Films are limited to about {AssetStore.MaxMediaBytes * 0.95 * 8 / bitsPerSecond / 60:0.#} minutes for now; this sequence runs {seconds / 60:0.#}.";
        return null;
    }

    private static string ReadyDetail(IReadOnlyList<ShotSource> sources)
    {
        var stills = sources.Count(source => source.Summary.Source is "Still" or "Missing");
        return stills == 0
            ? $"Ready to render {sources.Count} shot{(sources.Count == 1 ? "" : "s")} from video."
            : $"Ready to render. {stills} of {sources.Count} shot{(sources.Count == 1 ? "" : "s")} have no video yet and will hold a still or black.";
    }

    private async Task<AssetRecord?> FrozenAssetAsync(Guid id, string? hash, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return asset is not null && string.Equals(asset.ContentHash, hash, StringComparison.Ordinal) && assets.StoredFileExists(asset.StoragePath) ? asset : null;
    }

    private async Task ReportAsync(JobRecord job, int percent, string phase, CancellationToken cancellationToken)
    {
        job.Progress = Math.Clamp(percent, 0, 99); job.Phase = phase; job.LastHeartbeatAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string FileSafe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? ' ' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Sequence" : safe;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Bound(string value) => value.Length <= 1_000 ? value : value[..1_000] + "...";
    private static JobSummary MapJob(JobRecord job) => new(
        job.Id, job.ShotId, job.ShotCode, job.Kind, Enum.Parse<JobState>(job.State), job.Progress, job.Phase, job.Backend,
        job.CreatedAt, job.CompletedAt, job.Error, job.ManifestId, job.AdapterId, job.OutputAssetId,
        job.OutputAssetId is null ? null : $"/api/assets/{job.OutputAssetId}/content", job.ProviderRequestId, job.Attempt,
        job.RetryOfJobId, job.LastHeartbeatAt, job.WorkType);
}
