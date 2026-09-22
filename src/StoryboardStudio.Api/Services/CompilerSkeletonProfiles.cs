using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

public sealed record CompilerSkeletonProfileSet(
    IReadOnlyList<HumanoidRigProfile> Profiles,
    string? Detail);

public interface ICompilerSkeletonProfiles
{
    CompilerSkeletonProfileSet Load();
}

/// <summary>
/// Reads the compiler-owned skeleton profiles. Framewright consumes these
/// contracts; it never carries a second production copy of them.
/// </summary>
public sealed class CompilerSkeletonProfiles(IConfiguration configuration) : ICompilerSkeletonProfiles
{
    private const string Section = "Integrations:ReferenceAssetCompiler";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CompilerSkeletonProfileSet Load()
    {
        var configured = configuration[$"{Section}:SkeletonProfilePath"]?.Trim();
        var checkout = configuration[$"{Section}:CheckoutPath"]?.Trim();
        var directory = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : !string.IsNullOrWhiteSpace(checkout)
                ? Path.Combine(Path.GetFullPath(checkout), "profiles", "skeletons")
                : null;
        if (directory is null)
            return new([], $"Configure {Section}:CheckoutPath so Framewright can read the compiler's skeleton profiles.");
        if (!Directory.Exists(directory))
            return new([], "The configured compiler skeleton profile directory was not found.");

        try
        {
            var profiles = new List<HumanoidRigProfile>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(path);
                var source = JsonSerializer.Deserialize<ProfileDocument>(stream, Json)
                    ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
                if (string.IsNullOrWhiteSpace(source.ProfileId))
                    throw new InvalidDataException($"{Path.GetFileName(path)} has no profile_id.");
                if (source.RequiredBones is null || source.OptionalBones is null || source.ExpectedParents is null)
                    throw new InvalidDataException($"{source.ProfileId} does not contain the complete skeleton contract.");
                if (source.RequiredBones.Count == 0 && source.ExactBoneCount == 0)
                    continue; // static_prop is the ordinary no-skeleton answer.
                if (source.MaxInfluences <= 0 || source.TriBudget <= 0)
                    throw new InvalidDataException($"{source.ProfileId} has an invalid influence or triangle budget.");
                profiles.Add(new(
                    source.ProfileId.Trim(),
                    Humanize(source.ProfileId),
                    [.. source.RequiredBones], new Dictionary<string, string>(source.ExpectedParents, StringComparer.Ordinal),
                    [.. source.OptionalBones], source.AllowUnlistedBones, source.ExactBoneCount,
                    source.RootBone, source.RootMayBeArmatureObject, source.MaxInfluences, source.TriBudget));
            }
            if (profiles.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
                throw new InvalidDataException("The compiler skeleton profile directory contains duplicate profile ids.");
            return profiles.Count == 0
                ? new([], "The compiler profile directory contains no rigged skeleton profiles.")
                : new(profiles, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new([], "The compiler skeleton profiles could not be read. Check the configured checkout and profile JSON.");
        }
    }

    private static string Humanize(string profileId)
        => string.Join(' ', profileId.Trim().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.StartsWith("ue", StringComparison.OrdinalIgnoreCase)
                && part.Length > 2 && part[2..].All(char.IsDigit)
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]));

    private sealed class ProfileDocument
    {
        [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("required_bones")] public List<string>? RequiredBones { get; set; }
        [JsonPropertyName("expected_parents")] public Dictionary<string, string>? ExpectedParents { get; set; }
        [JsonPropertyName("optional_bones")] public List<string>? OptionalBones { get; set; }
        [JsonPropertyName("allow_unlisted_bones")] public bool AllowUnlistedBones { get; set; }
        [JsonPropertyName("exact_bone_count")] public int? ExactBoneCount { get; set; }
        [JsonPropertyName("root_bone")] public string? RootBone { get; set; }
        [JsonPropertyName("root_may_be_armature_object")] public bool RootMayBeArmatureObject { get; set; }
        [JsonPropertyName("max_influences")] public int MaxInfluences { get; set; }
        [JsonPropertyName("tri_budget")] public int TriBudget { get; set; }
    }
}
