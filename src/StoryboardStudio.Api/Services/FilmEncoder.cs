using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>One shot's picture in the film: a video take, a still held for the
/// shot's length, or black when the shot has no picture yet.</summary>
public sealed record FilmSegment(string Kind, string? Path, int Frames, string Code);

/// <summary>Everything the encoder needs, already resolved to files.</summary>
public sealed record FilmPlan(
    int Width,
    int Height,
    int FramesPerSecond,
    bool Rec709,
    IReadOnlyList<FilmSegment> Segments,
    string? AudioMixPath,
    string? CaptionsPath,
    bool Fades,
    bool NormalizeLoudness)
{
    public int TotalFrames => Segments.Sum(segment => segment.Frames);
    public double DurationSeconds => TotalFrames / (double)FramesPerSecond;
}

public sealed record FilmEncoderReadiness(bool Ready, string Detail);
/// <param name="LoudnessNormalized">Whether the mix was normalized to the -16 LUFS target (silence cannot be).</param>
public sealed record FilmEncodeResult(bool Succeeded, string Detail, bool LoudnessNormalized = false);

public interface IFilmEncoder
{
    FilmEncoderReadiness Inspect();
    Task<FilmEncodeResult> EncodeAsync(FilmPlan plan, string outputPath, Func<double, Task> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Renders a film in one encode: every shot is scaled to cover the delivery
/// frame, centre-cropped, conformed to the project frame rate and held or
/// trimmed to its exact frame count, then joined with the editorial mix and
/// optional soft captions. Loudness is measured first and applied linearly,
/// so the mix keeps its dynamics.
/// </summary>
public sealed class FfmpegFilmEncoder(IConfiguration configuration) : IFilmEncoder
{
    internal const double FadeInSeconds = 0.5;
    internal const double FadeOutSeconds = 0.6;
    internal const string TargetLoudness = "I=-16:TP=-1.5:LRA=11";
    // Keeps a five-minute film under the asset store's media limit.
    internal const int MaxVideoBitsPerSecond = 12_000_000;

    public FilmEncoderReadiness Inspect()
        => ResolveFfmpeg() is null
            ? new(false, "FFmpeg was not found. Install it on PATH or set Studio:Tools:FfmpegPath to render a film.")
            : new(true, "FFmpeg is ready to render the film.");

    public async Task<FilmEncodeResult> EncodeAsync(FilmPlan plan, string outputPath, Func<double, Task> progress, CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, Inspect().Detail);
        LoudnessMeasurement? loudness = null;
        if (plan.NormalizeLoudness && plan.AudioMixPath is not null)
            loudness = await MeasureLoudnessAsync(ffmpeg, plan.AudioMixPath, cancellationToken);

        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in BuildArguments(plan, loudness, outputPath)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return new(false, "FFmpeg could not be started.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            // -progress writes key=value lines; out_time_us is the encoded position.
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || !long.TryParse(line[12..], out var micros) || micros < 0) continue;
                await progress(Math.Clamp(micros / 1_000_000d / plan.DurationSeconds, 0, 1));
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var error = await errors;
        return process.ExitCode == 0 && File.Exists(outputPath)
            ? new(true, "Film encoded.", loudness is not null)
            : new(false, $"FFmpeg could not render the film: {Bound(error)}");
    }

    internal sealed record LoudnessMeasurement(string Integrated, string TruePeak, string Range, string Threshold, string Offset);

    internal static IReadOnlyList<string> BuildArguments(FilmPlan plan, LoudnessMeasurement? loudness, string outputPath)
    {
        string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" };
        var filters = new List<string>();
        for (var index = 0; index < plan.Segments.Count; index++)
        {
            var segment = plan.Segments[index];
            var seconds = N(segment.Frames / (double)plan.FramesPerSecond);
            switch (segment.Kind)
            {
                case "Video": args.AddRange(["-i", segment.Path!]); break;
                case "Still": args.AddRange(["-loop", "1", "-framerate", plan.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-t", seconds, "-i", segment.Path!]); break;
                default: args.AddRange(["-f", "lavfi", "-t", seconds, "-i", $"color=c=black:s={plan.Width}x{plan.Height}:r={plan.FramesPerSecond}"]); break;
            }
            // Cover the delivery frame, conform the rate, and hold the last
            // frame if a take runs short, then cut to the exact frame count.
            filters.Add($"[{index}:v]scale={plan.Width}:{plan.Height}:force_original_aspect_ratio=increase:flags=lanczos,crop={plan.Width}:{plan.Height},setsar=1,fps={plan.FramesPerSecond},tpad=stop_mode=clone:stop_duration={seconds},trim=end_frame={segment.Frames},setpts=PTS-STARTPTS,format=yuv420p[v{index}]");
        }
        var audioIndex = plan.Segments.Count;
        if (plan.AudioMixPath is not null) args.AddRange(["-i", plan.AudioMixPath]);
        else args.AddRange(["-f", "lavfi", "-t", N(plan.DurationSeconds), "-i", "anullsrc=r=48000:cl=stereo"]);
        var captionIndex = plan.CaptionsPath is null ? -1 : audioIndex + 1;
        if (plan.CaptionsPath is not null) args.AddRange(["-i", plan.CaptionsPath]);

        var duration = plan.DurationSeconds;
        var fade = plan.Fades && duration > (FadeInSeconds + FadeOutSeconds) * 2;
        var cut = string.Concat(Enumerable.Range(0, plan.Segments.Count).Select(index => $"[v{index}]")) + $"concat=n={plan.Segments.Count}:v=1:a=0";
        filters.Add(cut + (fade ? $",fade=t=in:st=0:d={N(FadeInSeconds)},fade=t=out:st={N(duration - FadeOutSeconds)}:d={N(FadeOutSeconds)}" : "") + "[v]");
        var audio = $"[{audioIndex}:a]aresample=48000,atrim=0:{N(duration)},apad=whole_dur={N(duration)}";
        if (loudness is not null)
            audio += $",loudnorm={TargetLoudness}:measured_I={loudness.Integrated}:measured_TP={loudness.TruePeak}:measured_LRA={loudness.Range}:measured_thresh={loudness.Threshold}:offset={loudness.Offset}:linear=true,aresample=48000";
        if (fade) audio += $",afade=t=in:st=0:d={N(FadeInSeconds)},afade=t=out:st={N(duration - FadeOutSeconds)}:d={N(FadeOutSeconds)}";
        filters.Add(audio + "[a]");

        args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "[v]", "-map", "[a]"]);
        if (captionIndex >= 0) args.AddRange(["-map", $"{captionIndex}:s", "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=English"]);
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-maxrate", MaxVideoBitsPerSecond.ToString(CultureInfo.InvariantCulture), "-bufsize", (MaxVideoBitsPerSecond * 2).ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
            "-r", plan.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-frames:v", plan.TotalFrames.ToString(CultureInfo.InvariantCulture)]);
        // x264 writes the BT.709 primaries and transfer into the stream only when
        // told through its own parameters, not just the container flags.
        if (plan.Rec709)
            args.AddRange(["-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
                "-x264-params", "colorprim=bt709:transfer=bt709:colormatrix=bt709:range=tv"]);
        args.AddRange(["-c:a", "aac", "-b:a", "256k", "-ar", "48000", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    private static async Task<LoudnessMeasurement?> MeasureLoudnessAsync(string ffmpeg, string mixPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-i", mixPath, "-af", $"loudnorm={TargetLoudness}:print_format=json", "-f", "null", "-" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return null;
        try
        {
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return ParseLoudness(await errors);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>Reads loudnorm's JSON report. Silence measures as -inf, which
    /// cannot be normalized, so it yields nothing and the mix is left as is.</summary>
    internal static LoudnessMeasurement? ParseLoudness(string report)
    {
        var open = report.LastIndexOf('{');
        var close = report.LastIndexOf('}');
        if (open < 0 || close < open) return null;
        try
        {
            using var json = JsonDocument.Parse(report[open..(close + 1)]);
            string? Read(string name) => json.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
            var values = new[] { Read("input_i"), Read("input_tp"), Read("input_lra"), Read("input_thresh"), Read("target_offset") };
            if (values.Any(value => value is null || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)))
                return null;
            return new(values[0]!, values[1]!, values[2]!, values[3]!, values[4]!);
        }
        catch (JsonException) { return null; }
    }

    private string? ResolveFfmpeg() => MediaTools.ResolveFfmpeg(configuration);

    private static string Bound(string value) => value.Length <= 1_000 ? value : value[..1_000] + "...";
}

/// <summary>Soft captions from the dialogue and voice clips' own text.</summary>
public static class FilmCaptions
{
    public sealed record Cue(int StartFrame, int EndFrame, string Text);

    public static string ToSrt(IReadOnlyList<Cue> cues, int framesPerSecond)
    {
        static string Time(double seconds)
        {
            var span = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
            return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00},{span.Milliseconds:000}";
        }
        var text = new StringBuilder();
        var number = 1;
        foreach (var cue in cues.Where(cue => cue.EndFrame > cue.StartFrame && !string.IsNullOrWhiteSpace(cue.Text)).OrderBy(cue => cue.StartFrame))
        {
            text.Append(number++).Append('\n')
                .Append(Time(cue.StartFrame / (double)framesPerSecond)).Append(" --> ").Append(Time(cue.EndFrame / (double)framesPerSecond)).Append('\n')
                .Append(cue.Text.Trim().ReplaceLineEndings("\n")).Append("\n\n");
        }
        return text.ToString();
    }
}
