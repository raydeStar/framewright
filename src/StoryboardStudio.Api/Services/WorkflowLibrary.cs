using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

public sealed record WorkflowEntry(
    string Id,
    string Name,
    string File,
    string Kind,
    string ConfigKey,
    string Summary,
    string[] RequiredPlaceholders,
    string[] OptionalPlaceholders,
    string[] RequiredNodeTypes,
    Dictionary<string, string> RequiredModels,
    string Outputs,
    string[]? Capabilities = null,
    int MaxReferenceImages = 0);

public sealed record WorkflowValidation(
    string Id,
    string Name,
    string Kind,
    string File,
    string ConfigKey,
    string Summary,
    bool Valid,
    string[] Problems,
    string[] Placeholders,
    string Outputs,
    string? AbsolutePath,
    string[]? Capabilities = null,
    int MaxReferenceImages = 0);

/// <summary>
/// Loads and validates <c>workflows/library.json</c>.
///
/// The point of this class is that a broken workflow fails at build/test time
/// rather than two minutes into somebody else's render queue. It checks the
/// things that are knowable without a GPU: the JSON parses, every declared
/// placeholder is present, no unknown placeholder is left unbound, every node
/// reference points at a node that exists in the same file, and the models named
/// in <c>requiredModels</c> match what the workflow actually selects.
///
/// Whether the models are *installed* is a live question and lives in the
/// preflight endpoint, which asks ComfyUI's read-only <c>/object_info</c>.
/// </summary>
public sealed class WorkflowLibrary
{
    public static readonly string[] KnownCapabilities =
    [
        "text-to-image", "sketch-to-image", "image-edit", "multi-reference",
        "text-to-video", "image-to-video", "first-frame", "last-frame-optional",
        "text-to-audio", "lyrics-optional", "project-canvas", "separate-audio", "silent-video"
    ];
    private static readonly JsonSerializerOptions LibraryJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Every placeholder the dispatcher knows how to bind.</summary>
    public static readonly string[] KnownPlaceholders =
    [
        "{{PROMPT}}", "{{NEGATIVE_PROMPT}}", "{{INPUT_IMAGE}}", "{{LAST_FRAME_IMAGE}}",
        "{{REFERENCE_IMAGE_1}}", "{{REFERENCE_IMAGE_2}}", "{{REFERENCE_IMAGE_3}}",
        "{{SEED}}", "{{DURATION_SECONDS}}", "{{LYRICS}}",
        "{{VIDEO_WIDTH}}", "{{VIDEO_HEIGHT}}", "{{VIDEO_STEPS}}",
        "{{VIDEO_OUTPUT_WIDTH}}", "{{VIDEO_OUTPUT_HEIGHT}}", "{{VIDEO_FPS}}",
        "{{VIDEO_LENGTH}}", "{{VIDEO_FILENAME_PREFIX}}"
    ];

    private readonly string root;

    public WorkflowLibrary(string libraryRoot) => root = libraryRoot;

    public WorkflowLibrary(IConfiguration configuration, IWebHostEnvironment environment)
        => root = ResolveRoot(configuration, environment);

    public static string ResolveRoot(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (configuration["Studio:WorkflowRoot"] is { Length: > 0 } configured) return Path.GetFullPath(configured);
        // Walk up from the content root to find the repo-level workflows folder,
        // so this works from both `dotnet run` and a published layout.
        var probe = new DirectoryInfo(environment.ContentRootPath);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, "workflows");
            if (Directory.Exists(candidate)) return candidate;
            probe = probe.Parent;
        }
        return Path.Combine(environment.ContentRootPath, "workflows");
    }

    public string Root => root;
    public string IndexPath => Path.Combine(root, "library.json");

    public IReadOnlyList<WorkflowEntry> LoadIndex()
    {
        if (!File.Exists(IndexPath)) return [];
        using var stream = File.OpenRead(IndexPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("workflows", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Select(x => x.Deserialize<WorkflowEntry>(LibraryJson))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }

    public IReadOnlyList<WorkflowValidation> ValidateAll()
        => LoadIndex().Select(Validate).ToArray();

    public WorkflowValidation Validate(WorkflowEntry entry)
    {
        var problems = new List<string>();
        var path = Path.GetFullPath(Path.Combine(root, entry.File));
        var used = Array.Empty<string>();

        if (!File.Exists(path))
        {
            problems.Add($"'{entry.File}' is listed in library.json but does not exist.");
            return new(entry.Id, entry.Name, entry.Kind, entry.File, entry.ConfigKey, entry.Summary, false, [.. problems], [], entry.Outputs, null, entry.Capabilities ?? [], entry.MaxReferenceImages);
        }

        var raw = File.ReadAllText(path);
        used = KnownPlaceholders.Where(x => raw.Contains(x, StringComparison.Ordinal)).ToArray();

        foreach (var required in entry.RequiredPlaceholders)
            if (!raw.Contains(required, StringComparison.Ordinal))
                problems.Add($"Required placeholder {required} is missing.");

        var capabilities = entry.Capabilities ?? [];
        foreach (var capability in capabilities)
            if (!KnownCapabilities.Contains(capability, StringComparer.Ordinal))
                problems.Add($"Unknown workflow capability '{capability}'.");
        if (entry.MaxReferenceImages is < 0 or > 3)
            problems.Add("maxReferenceImages must be between 0 and 3.");
        var declaredReferenceSlots = Enumerable.Range(1, 3).Count(slot => raw.Contains($"{{{{REFERENCE_IMAGE_{slot}}}}}", StringComparison.Ordinal));
        if (entry.MaxReferenceImages > declaredReferenceSlots)
            problems.Add($"maxReferenceImages declares {entry.MaxReferenceImages}, but the workflow only has {declaredReferenceSlots} reference placeholders.");
        if (capabilities.Contains("multi-reference", StringComparer.Ordinal) && entry.MaxReferenceImages < 2)
            problems.Add("The multi-reference capability requires at least two declared reference image slots.");
        if (capabilities.Any(capability => capability is "sketch-to-image" or "image-edit" or "image-to-video" or "first-frame")
            && !raw.Contains("{{INPUT_IMAGE}}", StringComparison.Ordinal))
            problems.Add("This image-guided capability requires {{INPUT_IMAGE}}.");
        if (capabilities.Contains("last-frame-optional", StringComparer.Ordinal)
            && !raw.Contains("{{LAST_FRAME_IMAGE}}", StringComparison.Ordinal))
            problems.Add("The last-frame-optional capability requires {{LAST_FRAME_IMAGE}}.");

        // A token the dispatcher cannot bind would reach ComfyUI verbatim.
        foreach (var token in FindTokens(raw))
            if (!KnownPlaceholders.Contains(token, StringComparer.Ordinal))
                problems.Add($"{token} is not a placeholder the dispatcher can bind. See workflows/README.md.");

        // Placeholders are substituted with quotes included, so the file itself
        // must be valid JSON only after binding. Bind representative values.
        var bound = raw;
        foreach (var token in KnownPlaceholders)
        {
            var numeric = token is "{{SEED}}" or "{{DURATION_SECONDS}}" or
                "{{VIDEO_WIDTH}}" or "{{VIDEO_HEIGHT}}" or "{{VIDEO_STEPS}}" or
                "{{VIDEO_OUTPUT_WIDTH}}" or "{{VIDEO_OUTPUT_HEIGHT}}" or
                "{{VIDEO_FPS}}" or "{{VIDEO_LENGTH}}";
            bound = bound.Replace($"\"{token}\"", numeric ? "1" : "\"x\"", StringComparison.Ordinal);
            if (numeric) bound = bound.Replace(token, "1", StringComparison.Ordinal);
        }

        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(bound); }
        catch (JsonException ex) { problems.Add($"Not valid JSON after placeholder binding: {ex.Message}"); }

        if (parsed is not null)
        {
            using (parsed)
            {
                var rootElement = parsed.RootElement;
                if (rootElement.ValueKind != JsonValueKind.Object)
                    problems.Add("A ComfyUI API-format workflow must be a JSON object of node id to node.");
                else
                {
                    var ids = rootElement.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                    var classTypes = new List<string>();
                    foreach (var node in rootElement.EnumerateObject())
                    {
                        if (node.Value.ValueKind != JsonValueKind.Object) { problems.Add($"Node '{node.Name}' is not an object."); continue; }
                        if (!node.Value.TryGetProperty("class_type", out var classType) || classType.GetString() is not { Length: > 0 } type)
                        { problems.Add($"Node '{node.Name}' has no class_type."); continue; }
                        classTypes.Add(type);
                        if (!node.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
                        foreach (var input in inputs.EnumerateObject())
                        {
                            // A link is ["nodeId", slot].
                            if (input.Value.ValueKind != JsonValueKind.Array || input.Value.GetArrayLength() != 2) continue;
                            if (input.Value[0].ValueKind != JsonValueKind.String) continue;
                            var target = input.Value[0].GetString()!;
                            if (!ids.Contains(target))
                                problems.Add($"Node '{node.Name}'.{input.Name} links to node '{target}', which does not exist in this workflow.");
                        }
                    }
                    foreach (var required in entry.RequiredNodeTypes)
                        if (!classTypes.Contains(required, StringComparer.Ordinal))
                            problems.Add($"Declared node type '{required}' is not used by this workflow.");

                    foreach (var (key, expected) in entry.RequiredModels)
                    {
                        var parts = key.Split('.', 2);
                        if (parts.Length != 2) { problems.Add($"requiredModels key '{key}' must be 'NodeType.input_name'."); continue; }
                        var found = rootElement.EnumerateObject()
                            .Where(x => x.Value.ValueKind == JsonValueKind.Object
                                     && x.Value.TryGetProperty("class_type", out var c) && c.GetString() == parts[0]
                                     && x.Value.TryGetProperty("inputs", out var i) && i.ValueKind == JsonValueKind.Object
                                     && i.TryGetProperty(parts[1], out var v) && v.ValueKind == JsonValueKind.String)
                            .Select(x => x.Value.GetProperty("inputs").GetProperty(parts[1]).GetString())
                            .ToArray();
                        if (found.Length == 0) problems.Add($"requiredModels names '{key}', but no such node/input exists in the workflow.");
                        else if (!found.Contains(expected, StringComparer.Ordinal))
                            problems.Add($"requiredModels says {key} = '{expected}', but the workflow selects '{string.Join("', '", found)}'.");
                    }
                }
            }
        }

        return new(entry.Id, entry.Name, entry.Kind, entry.File, entry.ConfigKey, entry.Summary,
            problems.Count == 0, [.. problems], used, entry.Outputs, path, capabilities, entry.MaxReferenceImages);
    }

    /// <summary>Finds every {{TOKEN}} occurrence so unknown ones can be rejected.</summary>
    private static HashSet<string> FindTokens(string raw)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while ((index = raw.IndexOf("{{", index, StringComparison.Ordinal)) >= 0)
        {
            var end = raw.IndexOf("}}", index, StringComparison.Ordinal);
            if (end < 0) break;
            tokens.Add(raw[index..(end + 2)]);
            index = end + 2;
        }
        return tokens;
    }
}
