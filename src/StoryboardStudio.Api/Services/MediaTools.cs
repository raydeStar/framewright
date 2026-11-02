namespace StoryboardStudio.Api.Services;

/// <summary>
/// Finds the FFmpeg tools the studio runs: the configured
/// <c>Studio:Tools:FfmpegPath</c> first, then the PATH.
/// </summary>
internal static class MediaTools
{
    public static string? ResolveFfmpeg(IConfiguration configuration)
    {
        var configured = configuration["Studio:Tools:FfmpegPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full)) return full;
        }
        return FindOnPath("ffmpeg");
    }

    public static string? FindOnPath(string tool)
    {
        var executable = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, executable))
            .FirstOrDefault(File.Exists);
    }
}
