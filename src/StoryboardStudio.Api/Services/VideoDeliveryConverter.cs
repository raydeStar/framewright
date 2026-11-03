using System.Diagnostics;

namespace StoryboardStudio.Api.Services;

public sealed record VideoDeliveryConversion(bool Success, string Detail);

public interface IVideoDeliveryConverter
{
    /// <summary>Re-encodes a provider's video as tagged, limited-range BT.709 at
    /// its own size, frame rate and frame count.</summary>
    Task<VideoDeliveryConversion> ConvertToRec709Async(string sourcePath, VideoMediaMetadata source, string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// ComfyUI's SaveVideo node, like libswscale's default, converts RGB frames to
/// YUV with BT.601 and writes no colour tags, but an approved Rec.709 shot must
/// carry tagged BT.709. The pixels are converted from the matrix they were
/// written with, not merely re-labelled: a re-labelled file shifts every colour.
/// </summary>
internal sealed class FfmpegVideoDeliveryConverter(IConfiguration configuration) : IVideoDeliveryConverter
{
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    public async Task<VideoDeliveryConversion> ConvertToRec709Async(string sourcePath, VideoMediaMetadata source, string outputPath, CancellationToken cancellationToken)
    {
        var ffmpeg = MediaTools.ResolveFfmpeg(configuration);
        if (ffmpeg is null)
            return new(false, "FFmpeg was not found. Install it on PATH or set Studio:Tools:FfmpegPath so a Max take can be delivered in Rec.709.");

        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in BuildArguments(sourcePath, source, outputPath)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return new(false, "FFmpeg could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConversionTimeout);
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await errors;
            return process.ExitCode == 0 && File.Exists(outputPath)
                ? new(true, "Converted to tagged BT.709.")
                : new(false, $"FFmpeg could not convert the take to BT.709: {(error.Length <= 1_000 ? error : error[..1_000] + "...")}");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            return new(false, "FFmpeg did not finish converting the take to BT.709 within ten minutes.");
        }
    }

    internal static IReadOnlyList<string> BuildArguments(string sourcePath, VideoMediaMetadata source, string outputPath)
    {
        // An untagged stream is read as the BT.601 limited range it was written
        // with; a tagged one by its own tags.
        var matrix = IsTagged(source.ColorSpace) ? "auto" : "bt601";
        var range = IsTagged(source.ColorRange) ? "auto" : "tv";
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
            "-map", "0:v:0", "-an", "-sn",
            "-vf", $"scale=in_color_matrix={matrix}:out_color_matrix=bt709:in_range={range}:out_range=tv,format=yuv420p",
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "medium", "-crf", "14", "-pix_fmt", "yuv420p",
            // x264 writes the BT.709 primaries and transfer into the stream only
            // when told through its own parameters, not just the container flags.
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-x264-params", "colorprim=bt709:transfer=bt709:colormatrix=bt709:range=tv",
            "-movflags", "+faststart", outputPath
        ];
    }

    private static bool IsTagged(string? value)
        => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);
}
