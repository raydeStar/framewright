using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed record VideoMediaExpectation(
    int Width,
    int Height,
    int FramesPerSecond,
    int FrameCount);

public sealed record VideoMediaMetadata(
    int Width,
    int Height,
    double FramesPerSecond,
    long FrameCount,
    double DurationSeconds);

public sealed record VideoMediaValidation(
    bool IsValid,
    string Detail,
    VideoMediaMetadata? Metadata = null);

public interface IVideoMediaProbe
{
    Task<VideoMediaValidation> ValidateAsync(
        string path,
        VideoMediaExpectation expectation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads the encoded video stream rather than trusting the generation manifest.
/// A manifest describes intent; ffprobe tells us what the renderer actually made.
/// </summary>
internal sealed class FfprobeVideoMediaProbe(IConfiguration configuration) : IVideoMediaProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    public async Task<VideoMediaValidation> ValidateAsync(
        string path,
        VideoMediaExpectation expectation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new(false, "The rendered video file is no longer available.");

        var ffprobe = ResolveFfprobe();
        if (ffprobe is null)
            return new(false, "FFprobe was not found. Configure Studio:Tools:FfprobePath (or FFmpeg) before selecting a production video.");

        var start = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error",
            "-count_frames",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,avg_frame_rate,r_frame_rate,nb_frames,nb_read_frames,duration:format=duration",
            "-of", "json",
            path
        }) start.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new(false, "FFprobe could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return new(false, "FFprobe timed out while verifying the production video.");
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                return new(false, $"FFprobe could not read the rendered video: {Bound(error)}");

            var metadata = Parse(output);
            return metadata is null
                ? new(false, "FFprobe did not return a readable primary video stream.")
                : Validate(metadata, expectation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or JsonException)
        {
            return new(false, $"The production-video probe failed safely: {Bound(ex.Message)}");
        }
    }

    internal static VideoMediaMetadata? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array ||
            streams.GetArrayLength() == 0)
            return null;

        var stream = streams[0];
        if (!TryReadInt(stream, "width", out var width) ||
            !TryReadInt(stream, "height", out var height))
            return null;

        var framesPerSecond = ReadRate(stream, "avg_frame_rate");
        if (framesPerSecond is not > 0) framesPerSecond = ReadRate(stream, "r_frame_rate");
        if (framesPerSecond is not > 0) return null;

        var duration = ReadDouble(stream, "duration");
        if (duration is not > 0 && document.RootElement.TryGetProperty("format", out var format))
            duration = ReadDouble(format, "duration");

        var frameCount = ReadLong(stream, "nb_read_frames") ?? ReadLong(stream, "nb_frames");
        if (frameCount is not > 0 && duration is > 0)
            frameCount = (long)Math.Round(duration.Value * framesPerSecond.Value, MidpointRounding.AwayFromZero);
        if (frameCount is not > 0) return null;
        if (duration is not > 0) duration = frameCount.Value / framesPerSecond.Value;

        return new(width, height, framesPerSecond.Value, frameCount.Value, duration.Value);
    }

    internal static VideoMediaValidation Validate(VideoMediaMetadata actual, VideoMediaExpectation expected)
    {
        if (actual.Width != expected.Width || actual.Height != expected.Height)
            return new(false, $"The rendered video is {actual.Width} x {actual.Height}; the project delivery canvas is {expected.Width} x {expected.Height}.", actual);
        if (Math.Abs(actual.FramesPerSecond - expected.FramesPerSecond) > 0.01)
            return new(false, $"The rendered video is {actual.FramesPerSecond:0.###} fps; the project requires {expected.FramesPerSecond} fps.", actual);
        if (actual.FrameCount != expected.FrameCount)
            return new(false, $"The rendered video contains {actual.FrameCount} frames; the shot requires {expected.FrameCount} frames.", actual);

        var expectedDuration = expected.FrameCount / (double)expected.FramesPerSecond;
        var frameTolerance = Math.Max(0.001, 0.5 / expected.FramesPerSecond);
        if (Math.Abs(actual.DurationSeconds - expectedDuration) > frameTolerance)
            return new(false, $"The rendered video lasts {actual.DurationSeconds:0.###} seconds; {expected.FrameCount} frames at {expected.FramesPerSecond} fps requires {expectedDuration:0.###} seconds.", actual);

        return new(true, $"Verified {actual.Width} x {actual.Height}, {actual.FramesPerSecond:0.###} fps, {actual.FrameCount} frames.", actual);
    }

    private string? ResolveFfprobe()
    {
        var configured = configuration["Studio:Tools:FfprobePath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full)) return full;
        }

        var ffmpeg = configuration["Studio:Tools:FfmpegPath"];
        if (!string.IsNullOrWhiteSpace(ffmpeg))
        {
            var full = Path.GetFullPath(ffmpeg);
            var sibling = Path.Combine(
                Path.GetDirectoryName(full) ?? "",
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }

        var executable = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
    }

    private static double? ReadRate(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node)) return null;
        var value = node.ValueKind == JsonValueKind.String ? node.GetString() : node.GetRawText();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/', 2);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
        if (parts.Length == 1) return numerator;
        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0
            ? numerator / denominator
            : null;
    }

    private static bool TryReadInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var node)) return false;
        return node.ValueKind == JsonValueKind.Number
            ? node.TryGetInt32(out value)
            : int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static long? ReadLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node)) return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var number)) return number;
        return long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static double? ReadDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node)) return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var number)) return number;
        return double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static string Bound(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "no diagnostic was returned" : value.Trim();
        return normalized.Length <= 1_000 ? normalized : normalized[..1_000] + "...";
    }
}
