using System.Diagnostics;
using System.Globalization;

namespace StoryboardStudio.Api.Services;

public sealed record SceneVideoEncoderReadiness(bool Ready, string Version, string Detail);

public sealed record SceneVideoEncodeRequest(
    string FramesDirectory,
    string OutputPath,
    int Width,
    int Height,
    int FramesPerSecond,
    int FrameCount,
    string ExpectedVersion);

public sealed record SceneVideoEncodeResult(bool Success, string Version, string Detail);

public interface ISceneVideoEncoder
{
    Task<SceneVideoEncoderReadiness> InspectAsync(CancellationToken cancellationToken);
    Task<SceneVideoEncodeResult> EncodeAsync(SceneVideoEncodeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Encodes numbered, exact-time PNG frames into one deterministic H.264 take.
/// The renderer supplies the pictures; this boundary never records a viewport
/// or relies on wall-clock playback.
/// </summary>
internal sealed class FfmpegSceneVideoEncoder(IConfiguration configuration) : ISceneVideoEncoder
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);

    public async Task<SceneVideoEncoderReadiness> InspectAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
            return new(false, "", "FFmpeg was not found. Configure Studio:Tools:FfmpegPath before rendering an animated scene take.");

        var result = await RunAsync(executable, ["-version"], InspectionTimeout, cancellationToken);
        if (!result.Success)
            return new(false, "", $"FFmpeg could not be inspected. {result.Detail}");

        var firstLine = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return firstLine.Length == 0
            ? new(false, "", "FFmpeg did not report a version.")
            : new(true, firstLine, "FFmpeg is ready for exact-frame scene encoding.");
    }

    public async Task<SceneVideoEncodeResult> EncodeAsync(
        SceneVideoEncodeRequest request,
        CancellationToken cancellationToken)
    {
        var readiness = await InspectAsync(cancellationToken);
        if (!readiness.Ready) return new(false, readiness.Version, readiness.Detail);
        if (!string.Equals(readiness.Version, request.ExpectedVersion, StringComparison.Ordinal))
            return new(false, readiness.Version, "FFmpeg changed after the render packet was frozen. Prepare the take again so its encoder is explicit.");

        var executable = ResolveExecutable();
        if (executable is null) return new(false, readiness.Version, "FFmpeg disappeared before encoding began.");

        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath) ?? request.FramesDirectory);
        var partial = request.OutputPath + ".partial.mp4";
        if (File.Exists(partial)) File.Delete(partial);

        var inputPattern = Path.Combine(request.FramesDirectory, "frame-%06d.png");
        var fps = request.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var count = request.FrameCount.ToString(CultureInfo.InvariantCulture);
        var arguments = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-framerate", fps,
            "-start_number", "0",
            "-i", inputPattern,
            "-frames:v", count,
            "-an",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-map_metadata", "-1",
            "-threads", "1",
            "-color_range", "tv",
            "-color_primaries", "bt709",
            "-color_trc", "bt709",
            "-colorspace", "bt709",
            "-x264-params", "colorprim=bt709:transfer=bt709:colormatrix=bt709",
            partial,
        };

        try
        {
            var result = await RunAsync(executable, arguments, EncodeTimeout, cancellationToken);
            if (!result.Success)
                return new(false, readiness.Version, $"FFmpeg could not encode the scene take. {result.Detail}");
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
                return new(false, readiness.Version, "FFmpeg reported success but produced no movie.");

            File.Move(partial, request.OutputPath, overwrite: true);
            return new(true, readiness.Version,
                $"Encoded {request.FrameCount} exact frames at {request.Width} x {request.Height} and {request.FramesPerSecond} fps.");
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private string? ResolveExecutable()
    {
        var configured = configuration["Studio:Tools:FfmpegPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full)) return full;
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new(false, "", "The process could not be started.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return new(false, "", "The process timed out. Even the order keeps a clock, though it rarely admits it.");
            }

            var standardOutput = await outputTask;
            var standardError = await errorTask;
            return process.ExitCode == 0
                ? new(true, standardOutput, "")
                : new(false, standardOutput, Bound(standardError));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(false, "", Bound(exception.Message));
        }
    }

    private static string Bound(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "No diagnostic was returned." : value.Trim();
        return normalized.Length <= 1_500 ? normalized : normalized[..1_500] + "...";
    }

    private sealed record ProcessResult(bool Success, string StandardOutput, string Detail);
}
