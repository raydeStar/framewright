using System.Reflection;
using System.Globalization;

namespace StoryboardStudio.Api.Services;

public sealed record BuildIdentity(
    string Version,
    string Commit,
    string BuiltAtUtc,
    string Channel);

/// <summary>
/// Reads provenance compiled into the artifact. Runtime environment variables
/// cannot rewrite this identity after an image has been reviewed and shipped.
/// </summary>
public sealed class BuildIdentityService
{
    public BuildIdentity Current { get; }

    public BuildIdentityService()
        : this(typeof(BuildIdentityService).Assembly) { }

    internal BuildIdentityService(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? "unknown", StringComparer.Ordinal);
        Current = new(
            Read(metadata, "FramewrightVersion", "development"),
            Read(metadata, "FramewrightCommit", "unknown"),
            ReadUtc(metadata, "FramewrightBuiltAtUtc"),
            Read(metadata, "FramewrightChannel", "development"));
    }

    private static string Read(Dictionary<string, string> metadata, string key, string fallback)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static string ReadUtc(Dictionary<string, string> metadata, string key)
    {
        var value = Read(metadata, key, "unknown");
        return value != "unknown" && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "unknown";
    }
}
