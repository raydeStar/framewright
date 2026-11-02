using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed record ComfyUiMissingModel(string File, string Node, string Folder);

/// <param name="State">Ready, Missing, or Unknown (ComfyUI could not be asked).</param>
public sealed record ComfyUiWorkflowReadiness(
    string Route,
    string Workflow,
    string Kind,
    string State,
    string Detail,
    IReadOnlyList<string> MissingNodes,
    IReadOnlyList<ComfyUiMissingModel> MissingModels);

/// <summary>
/// Asks ComfyUI whether it has what each configured Framewright workflow
/// needs, before anything is sent. It reads the workflow files Framewright
/// will actually submit, then compares their node types and model files with
/// ComfyUI's own <c>/object_info</c>, which lists the choices each loader
/// offers. The same list is what ComfyUI validates a prompt against.
/// Read-only: it never uploads, submits, or touches the queue.
/// </summary>
public sealed class ComfyUiInstallCheck(IHttpClientFactory clients, IConfiguration configuration, WorkflowLibrary? library = null)
{
    private static readonly (string Key, string Route, string Kind)[] Routes =
    [
        ("Integrations:ComfyUi:ExternalTextWorkflowPath", "Draft from the shot card", "Image"),
        ("Integrations:ComfyUi:ExternalWorkflowPath", "Draft from a sketch", "Image"),
        ("Integrations:ComfyUi:ExternalReferenceWorkflowPath", "Draft from approved references", "Image"),
        ("Integrations:ComfyUi:ExternalCurrentFrameWorkflowPath", "Revise the current frame", "Image"),
        ("Integrations:ComfyUi:ExternalVideoWorkflowPath", "H3 video", "Video"),
    ];

    /// <summary>Checks every workflow the ComfyUI routes are configured to send.</summary>
    public async Task<IReadOnlyList<ComfyUiWorkflowReadiness>> CheckConfiguredAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeLookup>(StringComparer.Ordinal);
        var results = new List<ComfyUiWorkflowReadiness>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unanswered = false;
        foreach (var (key, route, kind) in Routes)
        {
            if (ComfyUi.ResolveWorkflowPath(configuration[key]) is not { } path || !seen.Add(path)) continue;
            // Once ComfyUI stops answering, asking again per workflow only adds waiting.
            var result = unanswered
                ? new ComfyUiWorkflowReadiness(route, WorkflowName(path), kind, "Unknown", "ComfyUI did not answer the node check, so the installed models could not be confirmed.", [], [])
                : await CheckFileAsync(endpoint, path, route, kind, nodes, cancellationToken);
            unanswered |= result.State == "Unknown" && nodes.Values.Any(item => item.Failed);
            results.Add(result);
        }
        return results;
    }

    /// <summary>Checks one workflow file, as the preflight does for the packet it is about to send.</summary>
    public Task<ComfyUiWorkflowReadiness> CheckFileAsync(Uri endpoint, string workflowPath, string route, string kind, CancellationToken cancellationToken)
        => CheckFileAsync(endpoint, workflowPath, route, kind, new Dictionary<string, NodeLookup>(StringComparer.Ordinal), cancellationToken);

    private async Task<ComfyUiWorkflowReadiness> CheckFileAsync(
        Uri endpoint, string workflowPath, string route, string kind, Dictionary<string, NodeLookup> nodes, CancellationToken cancellationToken)
    {
        var name = WorkflowName(workflowPath);
        Requirements needs;
        try { needs = Read(workflowPath); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(route, name, kind, "Unknown", $"Framewright could not read {Path.GetFileName(workflowPath)}: {ex.Message}", [], []);
        }

        // Ask about the node types not already answered, a few at a time; the
        // preflight runs this before every video dispatch.
        var client = clients.CreateClient("integration-probe");
        using (var gate = new SemaphoreSlim(6))
        {
            var answers = await Task.WhenAll(needs.NodeTypes.Where(type => !nodes.ContainsKey(type)).Select(async type =>
            {
                await gate.WaitAsync(cancellationToken);
                try { return (Type: type, Lookup: await LookUpAsync(client, endpoint, type, cancellationToken)); }
                finally { gate.Release(); }
            }));
            foreach (var (type, lookup) in answers) nodes[type] = lookup;
        }
        var unknown = needs.NodeTypes.Any(type => nodes[type].Failed);
        var missingNodes = needs.NodeTypes.Where(type => nodes[type].Info is null).ToList();
        if (unknown)
            return new(route, name, kind, "Unknown", "ComfyUI did not answer the node check, so the installed models could not be confirmed.", [], []);

        var missingModels = new List<ComfyUiMissingModel>();
        foreach (var model in needs.Models)
        {
            if (!nodes.TryGetValue(model.Node, out var lookup) || lookup.Info is not { } info) continue;
            var options = Options(info, model.Input);
            if (options is null) continue;
            if (!options.Any(option => SameFile(option, model.File)))
                missingModels.Add(new(model.File, model.Node, ComfyUiModelFolders.For(model.Node, model.Input)!));
        }

        if (missingNodes.Count == 0 && missingModels.Count == 0)
            return new(route, name, kind, "Ready", $"ComfyUI has all {needs.NodeTypes.Count} node types and {needs.Models.Count} model files this workflow uses.", [], []);
        var parts = new List<string>();
        if (missingNodes.Count > 0) parts.Add($"{missingNodes.Count} node type{(missingNodes.Count == 1 ? " is" : "s are")} not installed ({string.Join(", ", missingNodes)})");
        if (missingModels.Count > 0) parts.Add($"{missingModels.Count} model file{(missingModels.Count == 1 ? " is" : "s are")} missing ({string.Join("; ", missingModels.Select(item => $"{item.File} in models/{item.Folder}"))})");
        return new(route, name, kind, "Missing", $"{string.Join(", and ", parts)}.", missingNodes, missingModels);
    }

    private string WorkflowName(string workflowPath)
        => library?.FindEntry(workflowPath)?.Name ?? Path.GetFileName(workflowPath);

    private sealed record ModelNeed(string Node, string Input, string File);
    private sealed record Requirements(IReadOnlyList<string> NodeTypes, IReadOnlyList<ModelNeed> Models);
    private sealed record NodeLookup(JsonElement? Info, bool Failed);

    /// <summary>The node types and loader files a workflow file selects. Bound
    /// placeholders are replaced by inert values first, exactly as validation does.</summary>
    private static Requirements Read(string workflowPath)
    {
        if (new FileInfo(workflowPath).Length > ComfyUi.MaxWorkflowBytes) throw new IOException("the workflow exceeds the 5 MB safety limit");
        var raw = File.ReadAllText(workflowPath);
        foreach (var token in WorkflowLibrary.KnownPlaceholders)
        {
            raw = raw.Replace($"\"{token}\"", "\"{{bound}}\"", StringComparison.Ordinal);
            raw = raw.Replace(token, "1", StringComparison.Ordinal);
        }
        using var document = JsonDocument.Parse(raw);
        var types = new List<string>();
        var models = new List<ModelNeed>();
        if (document.RootElement.ValueKind != JsonValueKind.Object) return new(types, models);
        foreach (var node in document.RootElement.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object || !node.Value.TryGetProperty("class_type", out var type) || type.GetString() is not { Length: > 0 } classType) continue;
            if (!types.Contains(classType, StringComparer.Ordinal)) types.Add(classType);
            if (!node.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) continue;
            foreach (var input in inputs.EnumerateObject())
            {
                if (input.Value.ValueKind != JsonValueKind.String || input.Value.GetString() is not { Length: > 0 } file || file == "{{bound}}") continue;
                if (ComfyUiModelFolders.For(classType, input.Name) is null) continue;
                if (!models.Any(item => item.Node == classType && item.Input == input.Name && item.File == file)) models.Add(new(classType, input.Name, file));
            }
        }
        return new(types, models);
    }

    private static async Task<NodeLookup> LookUpAsync(HttpClient client, Uri endpoint, string classType, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, $"/object_info/{Uri.EscapeDataString(classType)}"), cancellationToken);
            if (!response.IsSuccessStatusCode) return new(null, true);
            var body = await ComfyUi.ReadControlJsonAsync(response.Content, "The ComfyUI node description exceeded the 5 MB safety limit.", false, null, cancellationToken);
            using var json = JsonDocument.Parse(body);
            // ComfyUI answers {} for a node type it does not have.
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(classType, out var info)
                ? new(info.Clone(), false)
                : new(null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or GenerationDispatchException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new(null, true);
        }
    }

    /// <summary>The choices a combo input offers, in either of ComfyUI's
    /// shapes: [[options...], {...}] or ["COMBO", {"options": [...]}].</summary>
    private static string[]? Options(JsonElement info, string input)
    {
        if (!info.TryGetProperty("input", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return null;
        foreach (var group in new[] { "required", "optional" })
        {
            if (!inputs.TryGetProperty(group, out var section) || section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(input, out var spec)) continue;
            if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return null;
            var first = spec[0];
            if (first.ValueKind == JsonValueKind.Array)
                return first.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
            if (first.ValueKind == JsonValueKind.String && first.GetString() == "COMBO" && spec.GetArrayLength() > 1
                && spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                return options.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
            return null;
        }
        return null;
    }

    // ComfyUI lists subfolders with the host's separator; a workflow written on
    // Windows names them with a backslash.
    private static bool SameFile(string offered, string wanted)
        => string.Equals(offered.Replace('\\', '/'), wanted.Replace('\\', '/'), StringComparison.Ordinal);
}

/// <summary>
/// Turns ComfyUI's own validation answers into sentences an artist can act on.
/// ComfyUI refuses a workflow before queueing it when a node type or a model
/// file is missing; its JSON names the exact input and file, so say that
/// instead of pasting the response.
/// </summary>
internal static class ComfyUiModelFolders
{
    /// <summary>The models subfolder each loader input reads from, for the
    /// loaders Framewright's workflows use. Unknown loaders get no hint.</summary>
    internal static string? For(string? classType, string? inputName) => (classType, inputName) switch
    {
        ("UNETLoader", "unet_name") => "diffusion_models",
        ("CLIPLoader", "clip_name") or ("DualCLIPLoader", "clip_name1") or ("DualCLIPLoader", "clip_name2") => "text_encoders",
        ("VAELoader", "vae_name") => "vae",
        ("LoraLoaderModelOnly" or "LoraLoader", "lora_name") => "loras",
        ("CheckpointLoaderSimple", "ckpt_name") => "checkpoints",
        ("UpscaleModelLoader", "model_name") => "upscale_models",
        _ => null
    };

    internal static string Where(string? classType, string? inputName)
        => For(classType, inputName) is { } folder ? $"ComfyUI/models/{folder}" : $"the folder {classType ?? "that node"} reads from";
}

internal static class ComfyUiRejection
{
    private const int MaxListed = 3;

    /// <summary>Describes a non-success <c>/prompt</c> answer. Falls back to the
    /// bounded raw body when the answer is not ComfyUI's validation shape.</summary>
    internal static string Describe(int statusCode, string body, string what)
    {
        var plain = TryDescribe(body, what);
        return plain ?? $"ComfyUI rejected the {what} workflow with {statusCode}: {ComfyUi.Bound(body)}";
    }

    private static string? TryDescribe(string body, string what)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var missingModels = new List<string>();
            var other = new List<string>();
            if (root.TryGetProperty("node_errors", out var nodeErrors) && nodeErrors.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodeErrors.EnumerateObject())
                {
                    var classType = node.Value.TryGetProperty("class_type", out var type) ? type.GetString() : null;
                    if (!node.Value.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) continue;
                    foreach (var error in errors.EnumerateArray())
                    {
                        var kind = error.TryGetProperty("type", out var errorType) ? errorType.GetString() : null;
                        var extra = error.TryGetProperty("extra_info", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
                        var input = extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("input_name", out var name) ? name.GetString() : null;
                        var received = extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty("received_value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        if (kind == "value_not_in_list" && received is { Length: > 0 } && ComfyUiModelFolders.For(classType, input) is not null)
                            missingModels.Add($"{received} (put it in {ComfyUiModelFolders.Where(classType, input)})");
                        else
                        {
                            var details = error.TryGetProperty("details", out var detail) ? detail.GetString() : null;
                            var message = error.TryGetProperty("message", out var text) ? text.GetString() : null;
                            other.Add($"{classType ?? "A node"}: {message ?? kind ?? "invalid input"}{(string.IsNullOrWhiteSpace(details) ? "" : $" ({ComfyUi.Bound(details!.Trim())})")}");
                        }
                    }
                }
            }
            if (missingModels.Count > 0)
            {
                var distinct = missingModels.Distinct(StringComparer.Ordinal).ToArray();
                var listed = string.Join("; ", distinct.Take(MaxListed)) + (distinct.Length > MaxListed ? $"; and {distinct.Length - MaxListed} more" : "");
                // The file leads: the failure dock clamps long errors to three lines.
                return $"Missing model file{(distinct.Length == 1 ? "" : "s")}: {listed}. ComfyUI refused the {what} workflow, so nothing was queued. Add the file{(distinct.Length == 1 ? "" : "s")}, restart ComfyUI, then choose Retry.";
            }

            if (root.TryGetProperty("error", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                var kind = top.TryGetProperty("type", out var errorType) ? errorType.GetString() : null;
                if (kind == "missing_node_type")
                {
                    var classType = top.TryGetProperty("extra_info", out var info) && info.ValueKind == JsonValueKind.Object
                        && info.TryGetProperty("class_type", out var type) ? type.GetString() : null;
                    return classType is { Length: > 0 }
                        ? $"Missing ComfyUI node '{classType}'. ComfyUI refused the {what} workflow, so nothing was queued. Update ComfyUI or install the custom node that provides it, restart ComfyUI, then choose Retry."
                        : $"A node in the {what} workflow is not installed in ComfyUI, so nothing was queued.";
                }
                if (other.Count == 0 && top.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
                    return $"ComfyUI refused the {what} workflow: {ComfyUi.Bound(text)}. Nothing was queued.";
            }
            if (other.Count > 0)
                return $"ComfyUI refused the {what} workflow: {string.Join("; ", other.Distinct(StringComparer.Ordinal).Take(MaxListed))}. Nothing was queued.";
            return null;
        }
    }
}
