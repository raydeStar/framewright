using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Diagnostics;
using System.Globalization;

namespace StoryboardStudio.Api.Services;

public sealed record AudioMixExport(Stream Content, string FileName);

public sealed class AudioMasteringService(
    StudioDbContext db,
    AssetStore assets,
    IProjectScope projectScope,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    TimeProvider timeProvider)
{
    public async Task<AudioMasteringStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var sampleRate = await db.Projects.AsNoTracking().Where(x => x.Id == projectScope.ProjectId).Select(x => x.AudioSampleRate).SingleAsync(cancellationToken);
        var clips = await db.TimelineClips.AsNoTracking().ToListAsync(cancellationToken);
        var mediaIds = clips.Where(x => x.AssetId is not null).Select(x => x.AssetId!.Value).Distinct().ToArray();
        var validIds = mediaIds.Length == 0 ? [] : await db.Assets.AsNoTracking().Where(x => mediaIds.Contains(x.Id) && x.Kind == AssetKind.Audio.ToString()).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var available = clips.Count(x => x.AssetId is Guid id && validIds.Contains(id));
        var ffmpeg = ResolveFfmpeg();
        var canMix = ffmpeg is not null && available > 0;
        var detail = ffmpeg is null
            ? $"FFmpeg was not found. Configure Studio:Tools:FfmpegPath to enable a deterministic {sampleRate / 1000d:0.#} kHz WAV mix."
            : available == 0
                ? "FFmpeg is ready; attach or synthesize at least one audio asset before exporting a mix."
                : $"Ready to mix {available} media clip(s) into a separate {sampleRate / 1000d:0.#} kHz, 24-bit WAV. {clips.Count - available} guide clip(s) remain silent.";
        return new(ffmpeg is not null, available, clips.Count - available, canMix, detail);
    }

    public async Task<RepositoryResult<AudioMixExport>> CreateAsync(CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return RepositoryResult<AudioMixExport>.Invalid("FFmpeg is not configured; no process was started.");
        var clips = await db.TimelineClips.AsNoTracking().Where(x => x.AssetId != null).OrderBy(x => x.StartFrame).ToListAsync(cancellationToken);
        var ids = clips.Select(x => x.AssetId!.Value).Distinct().ToArray();
        var assetRecords = ids.Length == 0 ? [] : await db.Assets.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Kind == AssetKind.Audio.ToString()).ToListAsync(cancellationToken);
        var byId = assetRecords.ToDictionary(x => x.Id);
        var inputs = clips.Where(x => byId.ContainsKey(x.AssetId!.Value)).Select(x => (Clip: x, Asset: byId[x.AssetId!.Value])).ToArray();
        if (inputs.Length == 0) return RepositoryResult<AudioMixExport>.Invalid("No valid audio media is attached; no process was started.");

        var projectFormat = await db.Projects.AsNoTracking().Where(x => x.Id == projectScope.ProjectId).Select(x => new { x.FramesPerSecond, x.AudioSampleRate }).SingleAsync(cancellationToken);
        var fps = projectFormat.FramesPerSecond;
        var sampleRate = projectFormat.AudioSampleRate;
        var sequenceFrames = await db.Shots.AsNoTracking().SumAsync(x => (int?)x.DurationFrames, cancellationToken) ?? 0;
        var clipEnd = inputs.Max(x => x.Clip.StartFrame + x.Clip.DurationFrames);
        var duration = Math.Max(sequenceFrames, clipEnd) / (double)fps;
        var outputRoot = Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "exports");
        Directory.CreateDirectory(outputRoot);
        var outputPath = Path.Combine(outputRoot, $"audio-mix-{Guid.NewGuid():N}.wav");

        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var value in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }) start.ArgumentList.Add(value);
        foreach (var input in inputs)
        {
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add(assets.ResolveContentPath(input.Asset));
        }
        var filters = inputs.Select((input, index) =>
        {
            var trim = input.Clip.TrimStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var length = (input.Clip.DurationFrames / (double)fps).ToString("0.###", CultureInfo.InvariantCulture);
            var volume = input.Clip.Volume.ToString("0.###", CultureInfo.InvariantCulture);
            var delay = Math.Max(0, (int)Math.Round(input.Clip.StartFrame * 1000d / fps));
            return $"[{index}:a]atrim=start={trim}:duration={length},asetpts=PTS-STARTPTS,volume={volume},adelay={delay}:all=1[a{index}]";
        }).ToList();
        filters.Add($"{string.Concat(inputs.Select((_, i) => $"[a{i}]"))}amix=inputs={inputs.Length}:duration=longest:normalize=0,atrim=duration={duration.ToString("0.###", CultureInfo.InvariantCulture)},alimiter=limit=0.95[out]");
        foreach (var value in new[] { "-filter_complex", string.Join(';', filters), "-map", "[out]", "-c:a", "pcm_s24le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), outputPath }) start.ArgumentList.Add(value);

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return RepositoryResult<AudioMixExport>.Invalid("FFmpeg could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var error = await errorTask; _ = await outputTask;
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                return RepositoryResult<AudioMixExport>.Invalid($"FFmpeg mix failed: {Bound(error)}");
            }
            db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "EditorialAudioMixExported", TargetType = "Project", TargetId = projectScope.ProjectId.ToString(), PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { mediaClips = inputs.Length, guideClips = Math.Max(0, clips.Count - inputs.Length), sampleRate, bitDepth = 24 }), CreatedAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return RepositoryResult<AudioMixExport>.Ok(new(stream, $"framewright-audio-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.wav"));
        }
        catch { if (File.Exists(outputPath)) File.Delete(outputPath); throw; }
    }

    private string? ResolveFfmpeg()
    {
        var configured = configuration["Studio:Tools:FfmpegPath"];
        if (!string.IsNullOrWhiteSpace(configured)) { var full = Path.GetFullPath(configured); if (File.Exists(full)) return full; }
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Path.Combine(x, executable)).FirstOrDefault(File.Exists);
    }

    private static string Bound(string value) => value.Length <= 1_000 ? value : value[..1_000] + "...";
}
