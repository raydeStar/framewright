using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Services;

/// <summary>Lets an adapter report real progress while it waits on an external
/// queue, so the artist sees "waiting behind 2 jobs" instead of a stalled bar.</summary>
public delegate Task GenerationProgressReporter(int percent, string phase, string? providerRequestId, CancellationToken cancellationToken);

public sealed record GenerationExecutionContext(
    Guid JobId,
    Guid ManifestId,
    string ShotCode,
    GenerationRoute Route,
    GenerationPurpose Purpose,
    string Prompt,
    string ManifestHash,
    AssetRecord? Composition,
    string? CompositionPath,
    IReadOnlyList<(AuthorityBinding Binding, AssetRecord Asset, string Path)> ReferenceImages,
    AssetRecord? LastFrame = null,
    string? LastFramePath = null,
    GenerationProgressReporter? Report = null,
    VideoQuality VideoQuality = VideoQuality.Low,
    long? VideoSeed = null,
    int? VideoWidth = null,
    int? VideoHeight = null,
    int? DeliveryWidth = null,
    int? DeliveryHeight = null,
    string? ExistingProviderRequestId = null,
    bool IsCurrentFrameEdit = false,
    int? VideoFramesPerSecond = null,
    int? VideoFrameCount = null)
{
    public Task ReportAsync(int percent, string phase, CancellationToken cancellationToken)
        => Report?.Invoke(percent, phase, null, cancellationToken) ?? Task.CompletedTask;

    public Task ReportSubmittedAsync(int percent, string phase, string providerRequestId, CancellationToken cancellationToken)
        => Report?.Invoke(percent, phase, providerRequestId, cancellationToken) ?? Task.CompletedTask;
}

public sealed record GenerationAdapterOutput(
    Stream Content,
    string FileName,
    string MimeType,
    bool ProviderCallMade,
    string? ProviderRequestId = null,
    AssetKind Kind = AssetKind.Image);

public sealed class GenerationDispatchException(
    string message,
    bool providerCallMade = false,
    string? providerRequestId = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool ProviderCallMade { get; } = providerCallMade;
    public string? ProviderRequestId { get; } = providerRequestId;
}

public interface IGenerationAdapter
{
    GenerationAdapterSummary Describe();
    Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken);
}

internal sealed record ReferenceDispatchDecision(
    AuthorityBinding Binding,
    bool HasApprovedImage,
    bool WillBindVisually,
    int? VisualSlot,
    string Detail);

/// <summary>
/// One deterministic reference planner is shared by preflight and provider
/// execution. A provider may have fewer visual sockets than the frozen packet,
/// but it may never make that decision by reparsing prompt prose.
/// </summary>
internal static class GenerationReferenceDispatchPlanner
{
    internal static IReadOnlyList<(AuthorityBinding Binding, AssetRecord Asset, string Path, ReferenceDispatchDecision Decision)> PlanImages(
        IReadOnlyList<(AuthorityBinding Binding, AssetRecord Asset, string Path)> references,
        int visualCapacity,
        bool currentFrameEdit)
    {
        var byId = references.ToDictionary(item => item.Binding.Id, StringComparer.OrdinalIgnoreCase);
        return Plan(references.Select(item => (item.Binding, true)).ToArray(), visualCapacity, currentFrameEdit)
            .Select(decision =>
            {
                var source = byId[decision.Binding.Id];
                return (source.Binding, source.Asset, source.Path, decision);
            })
            .ToArray();
    }

    internal static IReadOnlyList<ReferenceDispatchDecision> Plan(
        IReadOnlyList<(AuthorityBinding Binding, bool HasImage)> references,
        int visualCapacity,
        bool currentFrameEdit)
    {
        visualCapacity = Math.Max(0, visualCapacity);
        var ordered = references
            .OrderBy(item => IsStyle(item.Binding) ? 0 : item.Binding.IsPinned ? 1 : 2)
            .ThenBy(item => CategoryPriority(item.Binding.Category))
            .ThenBy(item => item.Binding.SourceOrder)
            .ThenBy(item => item.Binding.Id, StringComparer.Ordinal)
            .ToArray();

        var nextSlot = 1;
        var decisions = new List<ReferenceDispatchDecision>(ordered.Length);
        foreach (var item in ordered)
        {
            var repeatedRole = item.Binding.Category.Equals("Role", StringComparison.OrdinalIgnoreCase)
                && (item.Binding.Placements?.Count ?? 0) > 1;
            var eligible = item.HasImage
                && (!currentFrameEdit || item.Binding.IsPinned || IsStyle(item.Binding))
                && !repeatedRole;
            var visual = eligible && nextSlot <= visualCapacity;
            var detail = !item.HasImage
                ? "Text canon only; this approved version has no image."
                : repeatedRole
                    ? "Text canon and pinned cast count travel; the role exemplar stays text-only so it cannot become an extra portrait."
                    : currentFrameEdit && !item.Binding.IsPinned && !IsStyle(item.Binding)
                        ? "Text canon validates the existing subject; no portrait pixels are reintroduced without an explicit pin."
                        : visual
                            ? $"Approved image is bound to visual reference slot {nextSlot}."
                            : visualCapacity == 0
                                ? "Text canon travels; this provider route has no visual reference socket."
                                : $"Text canon travels; all {visualCapacity} visual reference slots are already assigned.";
            decisions.Add(new(item.Binding, item.HasImage, visual, visual ? nextSlot++ : null, detail));
        }
        return decisions;
    }

    internal static int CapacityForAdapter(string adapterId, int workflowCapacity = 0)
        => adapterId.Equals(ComfyUiGenerationAdapter.AdapterId, StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, workflowCapacity)
            : adapterId.Equals(OpenAiImageGenerationAdapter.AdapterId, StringComparison.OrdinalIgnoreCase)
                || adapterId.Equals(CodexImageGenerationAdapter.AdapterId, StringComparison.OrdinalIgnoreCase)
                    ? 8
                    : 0;

    internal static int CountComfyVisualSlots(string workflow)
        => Enumerable.Range(1, 3).Count(slot =>
            workflow.Contains($"{{{{REFERENCE_IMAGE_{slot}}}}}", StringComparison.Ordinal));

    internal static bool IsCurrentFrameManifest(string manifestJson, string legacyCreativeBrief)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (document.RootElement.TryGetProperty("isCurrentFrameEdit", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }
        catch (JsonException) { }

        // Compatibility for already-frozen packets only. New packets always
        // carry the structured boolean and never depend on this prose.
        return legacyCreativeBrief.Contains("\n\nCURRENT FRAME EDIT:", StringComparison.Ordinal)
            || legacyCreativeBrief.Contains("\n\nFRAME EDIT GUIDE:", StringComparison.Ordinal);
    }

    internal static bool IsMarkupEditManifest(string manifestJson, string legacyCreativeBrief)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (document.RootElement.TryGetProperty("markupRevision", out var value))
                return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        }
        catch (JsonException) { }

        // Compatibility for packets frozen before markupRevision became a
        // structured manifest field.
        return legacyCreativeBrief.Contains("\n\nFRAME EDIT GUIDE:", StringComparison.Ordinal);
    }

    private static bool IsStyle(AuthorityBinding binding)
        => binding.Category.Equals("Style", StringComparison.OrdinalIgnoreCase);

    private static int CategoryPriority(string category) => category.ToLowerInvariant() switch
    {
        "style" => 0,
        "character" or "identity" or "face" => 1,
        "wardrobe" or "outfit" => 2,
        "pose" => 3,
        "prop" => 4,
        "location" or "architecture" => 5,
        "role" => 6,
        _ => 7
    };
}

/// <summary>An adapter that can reconnect to provider-owned work after the local
/// service restarts. Implementations may only resume request ids they submitted.</summary>
public interface IRecoverableGenerationAdapter
{
    bool CanResume(string providerRequestId);
}

/// <summary>
/// Shared ComfyUI transport for every ComfyUI-backed adapter.
///
/// QUEUE POLICY. Framewright submits into whatever queue ComfyUI already
/// has and waits its turn. It deliberately does NOT require an idle queue: this
/// is a shared workstation instance and refusing to dispatch whenever another
/// render is running made the tool unusable alongside real production work.
///
/// What it must never do, and does not do anywhere in this file:
///   * POST /queue with clear or delete
///   * POST /interrupt
///   * read, cancel, or download outputs for a prompt id it did not submit
/// Ownership is enforced by only ever reading /history/{our prompt_id} and only
/// downloading files listed under that entry.
/// </summary>
internal static class ComfyUi
{
    internal const long MaxControlJsonBytes = 5L * 1024 * 1024;
    internal const long MaxWorkflowBytes = 5L * 1024 * 1024;
    internal readonly record struct QueueState(bool Running, int Ahead, int Pending, bool Known);

    internal static Task<string> ReadControlJsonAsync(
        HttpContent content,
        string overflowMessage,
        bool providerCallMade,
        string? providerRequestId,
        CancellationToken cancellationToken)
        => BoundedContentBuffer.ReadUtf8StringAsync(
            content, MaxControlJsonBytes, overflowMessage, providerCallMade, providerRequestId, cancellationToken);

    /// <summary>Reads the shared queue read-only. Never mutates it.</summary>
    internal static async Task<QueueState> ReadQueueAsync(HttpClient client, Uri endpoint, string? promptId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(endpoint, "/queue"), cancellationToken);
        if (!response.IsSuccessStatusCode) return new(false, 0, 0, false);
        using var json = JsonDocument.Parse(await ReadControlJsonAsync(
            response.Content, "The ComfyUI queue response exceeded the 5 MB safety limit.", false, null, cancellationToken));
        var root = json.RootElement;
        var running = Ids(root, "queue_running");
        var pending = Ids(root, "queue_pending");
        if (promptId is null) return new(false, 0, pending.Count, true);
        if (running.Contains(promptId)) return new(true, 0, pending.Count, true);
        var index = pending.IndexOf(promptId);
        return new(false, index < 0 ? 0 : running.Count + index, pending.Count, true);
    }

    private static List<string> Ids(JsonElement root, string property)
    {
        var ids = new List<string>();
        if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Array) return ids;
        foreach (var entry in node.EnumerateArray())
        {
            // Each entry is [number, prompt_id, prompt, extra, outputs].
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
            if (entry[1].ValueKind == JsonValueKind.String && entry[1].GetString() is { Length: > 0 } id) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Confirms the endpoint is reachable before uploading anything.
    /// A failure here means we never submitted, so the caller can say so.</summary>
    internal static async Task EnsureReachableAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(endpoint, "/queue"), cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new GenerationDispatchException($"ComfyUI answered /queue with {(int)response.StatusCode}. Nothing was submitted.");
        }
        catch (HttpRequestException ex)
        {
            throw new GenerationDispatchException($"ComfyUI is not reachable at {endpoint}. Nothing was submitted.", false, null, ex);
        }
    }

    /// <summary>Human-readable phase for the artist while we hold a queue slot.</summary>
    internal static string DescribeWait(QueueState state)
        => state.Running ? "Rendering in ComfyUI"
        : !state.Known ? "Waiting for ComfyUI"
        : state.Ahead > 0 ? $"Waiting in ComfyUI queue — {state.Ahead} job{(state.Ahead == 1 ? "" : "s")} ahead"
        : "Queued in ComfyUI — next up";

    internal static async Task<string> UploadImageAsync(HttpClient client, Uri endpoint, string path, string mimeType, string originalName, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(content, "image", Path.GetFileName(originalName));
        form.Add(new StringContent("input"), "type");
        // Never overwrite: another artist's input of the same name must survive.
        form.Add(new StringContent("false"), "overwrite");
        using var response = await client.PostAsync(new Uri(endpoint, "/upload/image"), form, cancellationToken);
        var body = await ReadControlJsonAsync(
            response.Content, "The ComfyUI upload response exceeded the 5 MB safety limit.", false, null, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new GenerationDispatchException($"ComfyUI upload failed: {Bound(body)}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("name").GetString() ?? throw new GenerationDispatchException("ComfyUI did not name the uploaded file.");
    }

    internal static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "…";

    /// <summary>
    /// Removes nodes whose `_meta.optionalPlaceholder` was never bound, plus any
    /// input that linked to them. Lets one workflow file cover "with a last frame"
    /// and "without" instead of forcing two near-identical copies.
    /// </summary>
    internal static void PruneUnboundOptionalNodes(JsonNode? workflow, string placeholder)
    {
        if (workflow is not JsonObject graph) return;
        var doomed = graph
            .Where(pair => pair.Value is JsonObject node
                && node["_meta"] is JsonObject meta
                && meta["optionalPlaceholder"]?.GetValue<string>() == placeholder)
            .Select(pair => pair.Key)
            .ToArray();
        if (doomed.Length == 0) return;

        foreach (var id in doomed) graph.Remove(id);

        foreach (var pair in graph)
        {
            if (pair.Value is not JsonObject node || node["inputs"] is not JsonObject inputs) continue;
            var orphaned = inputs
                .Where(input => input.Value is JsonArray link && link.Count == 2
                    && link[0]?.GetValue<string>() is { } target && doomed.Contains(target))
                .Select(input => input.Key)
                .ToArray();
            foreach (var key in orphaned) inputs.Remove(key);
        }
    }

    /// <summary>
    /// Resolves a configured workflow path. Config ships repo-relative paths like
    /// "workflows/fast-draft.json", but the process working directory depends on
    /// how the service was launched, so fall back to walking up for the folder.
    /// An absolute path is always honoured as-is.
    /// </summary>
    internal static string? ResolveWorkflowPath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;

        var direct = Path.GetFullPath(configured);
        if (File.Exists(direct)) return direct;

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var probe = new DirectoryInfo(start);
            while (probe is not null)
            {
                var candidate = Path.Combine(probe.FullName, configured);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                probe = probe.Parent;
            }
        }
        return null;
    }
}

internal delegate bool ComfyOutputLocator(JsonElement history, string promptId, out (string FileName, string Subfolder, string Type) file);

internal static class ComfyUiPolling
{
    /// <summary>
    /// Waits for OUR prompt id to produce an output, reporting real queue
    /// position while other people's jobs run ahead of us.
    ///
    /// The timeout is wall-clock, not attempt-count: a job can legitimately sit
    /// behind an hour of someone else's renders on a shared box, and killing it
    /// at a fixed attempt count would abandon a job that is still perfectly
    /// healthy. Timing out here never cancels the ComfyUI-side job — we simply
    /// stop watching, because we do not cancel work we cannot safely reclaim.
    /// </summary>
    internal static async Task<(string FileName, string Subfolder, string Type)> AwaitOutputAsync(
        HttpClient client,
        Uri endpoint,
        string promptId,
        GenerationExecutionContext context,
        IConfiguration configuration,
        int defaultTimeoutMinutes,
        ComfyOutputLocator locate,
        string kind,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue("Integrations:ComfyUi:PollIntervalMilliseconds", 2_000), 250, 10_000));
        var timeout = TimeSpan.FromMinutes(Math.Clamp(defaultTimeoutMinutes, 1, 720));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastPhase = string.Empty;
        var everRan = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);

            using (var history = await client.GetAsync(new Uri(endpoint, $"/history/{Uri.EscapeDataString(promptId)}"), cancellationToken))
            {
                if (history.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await ComfyUi.ReadControlJsonAsync(
                        history.Content, "The ComfyUI history response exceeded the 5 MB safety limit.", true,
                        requestId ?? promptId, cancellationToken));
                    if (locate(json.RootElement, promptId, out var file)) return file;
                    if (json.RootElement.TryGetProperty(promptId, out var failedEntry)
                        && failedEntry.TryGetProperty("status", out var failedStatus)
                        && failedStatus.TryGetProperty("status_str", out var statusText)
                        && string.Equals(statusText.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                    {
                        var detail = "The workflow failed during execution.";
                        if (failedStatus.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var message in messages.EnumerateArray().Reverse())
                            {
                                if (message.ValueKind != JsonValueKind.Array || message.GetArrayLength() < 2 || message[0].GetString() != "execution_error") continue;
                                if (message[1].TryGetProperty("exception_message", out var exceptionMessage) && !string.IsNullOrWhiteSpace(exceptionMessage.GetString()))
                                    detail = ComfyUi.Bound(exceptionMessage.GetString()!.Trim());
                                break;
                            }
                        }
                        throw new GenerationDispatchException($"ComfyUI could not render the {kind}: {detail}", true, requestId ?? promptId);
                    }
                    // Present in history without our output means ComfyUI finished
                    // the prompt and produced nothing we can use.
                    if (everRan && json.RootElement.TryGetProperty(promptId, out var entry)
                        && entry.TryGetProperty("status", out var status)
                        && status.TryGetProperty("completed", out var completed)
                        && completed.ValueKind == JsonValueKind.True)
                        throw new GenerationDispatchException(
                            $"ComfyUI finished the prompt but produced no {kind} output. Check that the workflow ends in a Save node.",
                            true, requestId ?? promptId);
                }
            }

            var state = await ComfyUi.ReadQueueAsync(client, endpoint, promptId, cancellationToken);
            if (state.Running) everRan = true;
            var phase = ComfyUi.DescribeWait(state);
            if (phase != lastPhase)
            {
                lastPhase = phase;
                await context.ReportAsync(state.Running ? 62 : 44, phase, cancellationToken);
            }
        }

        throw new GenerationDispatchException(
            $"ComfyUI did not return a {kind} within {timeout.TotalMinutes:0} minutes. The job was left running and was not interrupted or cleared; raise Integrations:ComfyUi:{(kind == "video" ? "Video" : kind == "audio" ? "Music" : "Image")}TimeoutMinutes if the queue is long.",
            true, requestId ?? promptId);
    }
}

/// <summary>A no-network proof adapter that makes the entire durable pipeline
/// usable before a paid or production backend is enabled. When a composition
/// exists it promotes an exact copy; otherwise it creates a deterministic
/// blocking slate. It is deliberately named "proof", never "AI renderer".</summary>
public sealed class LocalProofGenerationAdapter : IGenerationAdapter
{
    public const string AdapterId = "local-proof";

    public GenerationAdapterSummary Describe() => new(
        AdapterId, "Local composition proof", "Local", "Ready",
        "No network or model call. Verifies manifests, durable jobs, output import, review, and approval using the saved composition.",
        true, [GenerationRoute.FastDraft, GenerationRoute.PrecisionDraft], [GenerationPurpose.Draft, GenerationPurpose.Final]);

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.CompositionPath is not null)
        {
            var memory = new MemoryStream();
            await using var input = new FileStream(context.CompositionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            var extension = context.Composition?.MimeType == "image/jpeg" ? "jpg" : "png";
            return new(memory, $"{context.ShotCode.ToLowerInvariant()}-{context.Purpose.ToString().ToLowerInvariant()}-proof.{extension}", context.Composition?.MimeType ?? "image/png", false);
        }

        var png = ProofPngRenderer.Render(context.ManifestHash, 1024, 576);
        return new(new MemoryStream(png, writable: false), $"{context.ShotCode.ToLowerInvariant()}-blocking-proof.png", "image/png", false);
    }
}

public sealed class OpenAiImageGenerationAdapter(IHttpClientFactory clients, IConfiguration configuration, IProviderCredentialStore credentials) : IGenerationAdapter
{
    public const string AdapterId = "openai-gpt-image";
    private const long MaxResponseJsonBytes = 40 * 1024 * 1024;
    private string? ApiKey => credentials.GetOpenAiApiKey();
    private string Model => configuration["Integrations:OpenAI:ImageModel"] ?? "gpt-image-2";

    public GenerationAdapterSummary Describe()
    {
        var endpoint = OpenAiEndpointPolicy.Resolve(configuration, "Integrations:OpenAI:ImageEditEndpoint", "https://api.openai.com/v1/images/edits", out var endpointError);
        var ready = !string.IsNullOrWhiteSpace(ApiKey) && endpoint is not null;
        var state = ready ? "Ready" : "NeedsSetup";
        var detail = endpoint is null ? endpointError!
            : ready ? $"Server-side {Model} generation is ready. Clicking Generate authorizes this call; the API key never enters the browser."
            : "Connect an OpenAI project API key once in Settings. After that, Generate runs directly with no feature toggle or packet confirmation.";
        return new(AdapterId, "OpenAI GPT Image", "Cloud", state, detail, ready,
            [GenerationRoute.PrecisionDraft], [GenerationPurpose.Draft, GenerationPurpose.Final]);
    }

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        var descriptor = Describe();
        if (!descriptor.CanDispatch) throw new GenerationDispatchException(descriptor.Detail);
        if (context.CompositionPath is null && context.ReferenceImages.Count == 0)
            return await ExecuteTextGenerationAsync(context, cancellationToken);

        var plannedReferences = GenerationReferenceDispatchPlanner.PlanImages(
            context.ReferenceImages,
            GenerationReferenceDispatchPlanner.CapacityForAdapter(AdapterId),
            context.IsCurrentFrameEdit);
        var visualReferences = plannedReferences.Where(item => item.Decision.WillBindVisually).ToArray();

        using var form = new MultipartFormDataContent();
        var imageMap = new List<string>();
        if (context.CompositionPath is not null && context.Composition is not null)
            imageMap.Add("- IMAGE 1: CURRENT VISUAL BASE. Preserve composition, cast, camera, and unmentioned content; approved Style authority overrides conflicting rendering style.");
        foreach (var reference in visualReferences)
        {
            var number = imageMap.Count + 1;
            imageMap.Add($"- IMAGE {number}: {reference.Binding.Name} ({reference.Binding.Category}) v{reference.Binding.Version}. {OpenAiReferenceRole(reference.Binding.Category)}");
        }
        var textOnly = plannedReferences.Where(item => !item.Decision.WillBindVisually)
            .Select(item => $"- {item.Binding.Name} ({item.Binding.Category}) v{item.Binding.Version}: {item.Decision.Detail}")
            .ToArray();
        var mappedPrompt = $"{context.Prompt}\n\nATTACHED IMAGE MAP (ordered exactly like image[]):\n{string.Join('\n', imageMap)}\n\nTEXT-ONLY AUTHORITY MAP:\n{(textOnly.Length == 0 ? "- None." : string.Join('\n', textOnly))}";
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent(mappedPrompt), "prompt");
        form.Add(new StringContent(context.Purpose == GenerationPurpose.Final ? "high" : "medium"), "quality");
        form.Add(new StringContent(configuration["Integrations:OpenAI:ImageSize"] ?? "1536x864"), "size");
        form.Add(new StringContent("png"), "output_format");
        var streams = new List<FileStream>();
        try
        {
            if (context.CompositionPath is not null && context.Composition is not null)
                AddImage(form, streams, context.CompositionPath, context.Composition.MimeType, "01-current-frame.png");
            var index = context.CompositionPath is null ? 1 : 2;
            foreach (var reference in visualReferences)
                AddImage(form, streams, reference.Path, reference.Asset.MimeType, $"{index++:00}-{OpenAiSafeStem(reference.Binding.Category)}-{OpenAiSafeStem(reference.Binding.Name)}-v{reference.Binding.Version}.png");

            var endpoint = OpenAiEndpointPolicy.Resolve(configuration, "Integrations:OpenAI:ImageEditEndpoint", "https://api.openai.com/v1/images/edits", out _)!;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            request.Headers.Add("X-Client-Request-Id", context.JobId.ToString("N"));
            if (configuration["Integrations:OpenAI:ProjectId"] is { Length: > 0 } projectId) request.Headers.Add("OpenAI-Project", projectId);
            var client = clients.CreateClient("openai-generation");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
            var body = await BoundedContentBuffer.ReadUtf8StringAsync(
                response.Content, MaxResponseJsonBytes,
                "The OpenAI image response exceeded the 40 MB JSON safety limit.", true, requestId,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new GenerationDispatchException($"OpenAI image edit failed with {(int)response.StatusCode}: {Bound(body)}", true, requestId);
            try
            {
                using var json = JsonDocument.Parse(body);
                var encoded = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
                if (string.IsNullOrWhiteSpace(encoded)) throw new JsonException("The response did not include image data.");
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.LongLength > AssetStore.MaxImageBytes) throw new GenerationDispatchException("The generated image exceeded the local 25 MB safety limit.", true, requestId);
                return new(new MemoryStream(bytes, writable: false), $"{context.ShotCode.ToLowerInvariant()}-{context.Purpose.ToString().ToLowerInvariant()}-{Model}.png", "image/png", true, requestId);
            }
            catch (GenerationDispatchException) { throw; }
            catch (Exception ex) { throw new GenerationDispatchException("OpenAI returned an unreadable image response.", true, requestId, ex); }
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    private async Task<GenerationAdapterOutput> ExecuteTextGenerationAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        var endpoint = OpenAiEndpointPolicy.Resolve(configuration, "Integrations:OpenAI:ImageGenerationEndpoint", "https://api.openai.com/v1/images/generations", out var endpointError);
        if (endpoint is null) throw new GenerationDispatchException(endpointError!);
        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            prompt = context.Prompt,
            quality = context.Purpose == GenerationPurpose.Final ? "high" : "medium",
            size = configuration["Integrations:OpenAI:ImageSize"] ?? "1536x864",
            output_format = "png"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Add("X-Client-Request-Id", context.JobId.ToString("N"));
        if (configuration["Integrations:OpenAI:ProjectId"] is { Length: > 0 } projectId) request.Headers.Add("OpenAI-Project", projectId);
        var client = clients.CreateClient("openai-generation");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        var body = await BoundedContentBuffer.ReadUtf8StringAsync(response.Content, MaxResponseJsonBytes,
            "The OpenAI image response exceeded the 40 MB JSON safety limit.", true, requestId, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GenerationDispatchException($"OpenAI image generation failed with {(int)response.StatusCode}: {Bound(body)}", true, requestId);
        try
        {
            using var json = JsonDocument.Parse(body);
            var encoded = json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString();
            if (string.IsNullOrWhiteSpace(encoded)) throw new JsonException("The response did not include image data.");
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.LongLength > AssetStore.MaxImageBytes) throw new GenerationDispatchException("The generated image exceeded the local 25 MB safety limit.", true, requestId);
            return new(new MemoryStream(bytes, writable: false), $"{context.ShotCode.ToLowerInvariant()}-{context.Purpose.ToString().ToLowerInvariant()}-{Model}.png", "image/png", true, requestId);
        }
        catch (GenerationDispatchException) { throw; }
        catch (Exception ex) { throw new GenerationDispatchException("OpenAI returned an unreadable image response.", true, requestId, ex); }
    }

    private static void AddImage(MultipartFormDataContent form, List<FileStream> streams, string path, string mimeType, string fileName)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        streams.Add(stream);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(content, "image[]", Path.GetFileName(fileName));
    }

    private static string OpenAiReferenceRole(string category) => category.ToLowerInvariant() switch
    {
        "style" => "Apply this exemplar's rendering language globally; do not depict its subject or copy its composition.",
        "character" or "identity" => "Use for the exact person's identity, facial structure, hair, proportions, skin tone, and approved marks only.",
        "face" => "Use only for facial identity and approved facial marks; do not inherit pose, wardrobe, crop, or background.",
        "role" => "Use for shared costume, equipment, insignia, and demeanor; create a distinct face unless exact identity is requested.",
        "wardrobe" or "outfit" => "Transfer clothing and accessories to the assigned existing subject; do not create another person.",
        "location" or "architecture" => "Use for continuous environmental architecture, materials, and spatial identity; never paste it as a panel.",
        "prop" => "Use for the named prop's design and materials at the packet's placement and scale.",
        "pose" => "Use only for body blocking and gesture; do not inherit identity, wardrobe, or background.",
        _ => "Use only for the explicitly assigned reference role in the immutable packet."
    };

    private static string OpenAiSafeStem(string value)
    {
        var stem = string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'));
        while (stem.Contains("--", StringComparison.Ordinal)) stem = stem.Replace("--", "-", StringComparison.Ordinal);
        return stem.Trim('-');
    }

    private static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "…";
}

public sealed class ComfyUiGenerationAdapter(IHttpClientFactory clients, IConfiguration configuration) : IGenerationAdapter, IRecoverableGenerationAdapter
{
    public const string AdapterId = "comfyui-fast-draft";
    private bool Enabled => configuration.GetValue("Integrations:ComfyUi:SubmissionEnabled", false);
    private string? SketchWorkflowPath => ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalWorkflowPath"]);
    // Fall back to the historical composition graph for custom installations
    // until they opt into the dedicated pixel-preserving edit workflow.
    private string? CurrentFrameWorkflowPath => ComfyUi.ResolveWorkflowPath(
        configuration["Integrations:ComfyUi:ExternalCurrentFrameWorkflowPath"]
        ?? configuration["Integrations:ComfyUi:ExternalWorkflowPath"]);
    private string? TextWorkflowPath => ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalTextWorkflowPath"]);
    private string? ReferenceWorkflowPath => ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalReferenceWorkflowPath"]);
    private Uri? Endpoint => Uri.TryCreate(configuration["Integrations:ComfyUi:Endpoint"], UriKind.Absolute, out var uri) ? uri : null;

    public GenerationAdapterSummary Describe()
    {
        var endpoint = Endpoint;
        var local = endpoint is not null && (endpoint.IsLoopback || configuration.GetValue("Integrations:ComfyUi:AllowRemote", false));
        var workflowReady = SketchWorkflowPath is not null && CurrentFrameWorkflowPath is not null && TextWorkflowPath is not null;
        var ready = Enabled && local && workflowReady;
        var state = !Enabled ? "Protected" : ready ? "Ready" : "NeedsSetup";
        var detail = !Enabled ? "Submission is off. Read-only discovery cannot alter the existing queue."
            : !local ? "The endpoint is invalid or non-loopback; remote ComfyUI requires explicit AllowRemote approval."
            : !workflowReady ? "The text, sketch-synthesis, and current-frame edit API workflows must all be available."
            : "Text, multi-reference, sketch-synthesis, and delta-focused current-frame edit routes are ready. Framewright chooses from the supplied context; jobs join the shared queue and never interrupt existing work.";
        return new(AdapterId, "ComfyUI fast draft", "Local service", state, detail, ready,
            [GenerationRoute.FastDraft], [GenerationPurpose.Draft]);
    }

    public bool CanResume(string providerRequestId) => Guid.TryParse(providerRequestId, out _);

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        var descriptor = Describe();
        if (!descriptor.CanDispatch) throw new GenerationDispatchException(descriptor.Detail);
        var endpoint = Endpoint!;
        var client = clients.CreateClient("comfyui-generation");
        await ComfyUi.EnsureReachableAsync(client, endpoint, cancellationToken);

        if (context.ExistingProviderRequestId is { Length: > 0 } existingPromptId)
        {
            await context.ReportAsync(40, "Reconnected to the existing ComfyUI image job", cancellationToken);
            return await DownloadExistingImageAsync(client, endpoint, existingPromptId, context, cancellationToken);
        }

        string? uploadedName = null;
        if (context.CompositionPath is not null && context.Composition is not null)
            uploadedName = await ComfyUi.UploadImageAsync(client, endpoint, context.CompositionPath, context.Composition.MimeType, context.Composition.OriginalFileName, cancellationToken);

        var workflowPath = context.CompositionPath is not null
            ? context.IsCurrentFrameEdit ? CurrentFrameWorkflowPath! : SketchWorkflowPath!
            : context.ReferenceImages.Count > 0 && ReferenceWorkflowPath is not null
                ? ReferenceWorkflowPath
                : TextWorkflowPath!;
        if (new FileInfo(workflowPath).Length > ComfyUi.MaxWorkflowBytes)
            throw new GenerationDispatchException("The configured ComfyUI workflow exceeds the 5 MB safety limit.");
        var raw = await File.ReadAllTextAsync(workflowPath, cancellationToken);
        if (!raw.Contains("{{PROMPT}}", StringComparison.Ordinal)) throw new GenerationDispatchException("The ComfyUI workflow is missing the {{PROMPT}} placeholder.");
        // A single role exemplar plus multiple anchors for that same role
        // makes the distilled four-step edit route paste the portrait as an
        // extra subject. For repeated-cast composition passes, use the frozen
        // authority spec and spatial pins locally; exact portrait conditioning
        // remains available to precision and later single-subject edit routes.
        var repeatedCastComposition = context.CompositionPath is not null
            && context.ReferenceImages.Any(reference => reference.Binding.Category.Equals("Role", StringComparison.OrdinalIgnoreCase)
                && (reference.Binding.Placements?.Count ?? 0) > 1);
        var referenceBinding = await BindReferenceImagesAsync(client, endpoint, raw, context, cancellationToken);
        raw = referenceBinding.Workflow;
        var imagePrompt = BuildImagePrompt(context, referenceBinding.Bound, referenceBinding.Decisions);
        if (repeatedCastComposition)
            imagePrompt += "\n- FAST REPEATED-CAST SAFETY: Follow the approved textual authority spec and pinned layout for every repeated cast member. Do not create an extra portrait or central reference subject.";
        raw = raw.Replace("\"{{PROMPT}}\"", JsonSerializer.Serialize(imagePrompt), StringComparison.Ordinal);
        // The default fast-draft workflow carries a negative encoder; without a
        // bound value the token would reach ComfyUI verbatim.
        raw = raw.Replace("\"{{NEGATIVE_PROMPT}}\"", JsonSerializer.Serialize(
            configuration["Integrations:ComfyUi:NegativePrompt"]
            ?? "collage, montage, split screen, contact sheet, picture in picture, pasted reference, reference panel, double exposure, ghosted face, giant face in sky, floating portrait, stick figure, line drawing, sketch, silhouette, mannequin, doll, featureless head, faceless, black sphere head, orb head, ball head, blank face, mask, text, watermark"),
            StringComparison.Ordinal);
        if (uploadedName is not null)
        {
            if (!raw.Contains("{{INPUT_IMAGE}}", StringComparison.Ordinal)) throw new GenerationDispatchException("The ComfyUI workflow is missing the {{INPUT_IMAGE}} placeholder required by the composition.");
            raw = raw.Replace("\"{{INPUT_IMAGE}}\"", JsonSerializer.Serialize(uploadedName), StringComparison.Ordinal);
        }
        var deterministicSeed = BitConverter.ToUInt64(Convert.FromHexString(context.ManifestHash[..16]));
        raw = raw.Replace("\"{{SEED}}\"", deterministicSeed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        JsonNode? workflow;
        try { workflow = JsonNode.Parse(raw); }
        catch (JsonException ex) { throw new GenerationDispatchException("The configured ComfyUI workflow is not valid JSON after placeholder binding.", false, null, ex); }
        ApplyProjectImageCanvas(workflow, context);
        PruneUnboundReferenceNodes(workflow);

        // Join whatever queue already exists and wait our turn. Any work already
        // queued or running belongs to someone else and is left completely alone.
        var clientId = Guid.NewGuid().ToString("N");
        using var submit = await client.PostAsJsonAsync(new Uri(endpoint, "/prompt"), new { prompt = workflow, client_id = clientId }, cancellationToken);
        var requestId = submit.Headers.TryGetValues("x-request-id", out var requestIds) ? requestIds.FirstOrDefault() : null;
        var submitBody = await ComfyUi.ReadControlJsonAsync(
            submit.Content, "The ComfyUI submission response exceeded the 5 MB safety limit.", true, requestId, cancellationToken);
        if (!submit.IsSuccessStatusCode) throw new GenerationDispatchException($"ComfyUI rejected the workflow with {(int)submit.StatusCode}: {ComfyUi.Bound(submitBody)}", true, requestId);
        string promptId;
        try { using var json = JsonDocument.Parse(submitBody); promptId = json.RootElement.GetProperty("prompt_id").GetString() ?? throw new JsonException(); }
        catch (Exception ex) { throw new GenerationDispatchException("ComfyUI did not return a prompt id.", true, requestId, ex); }

        // Persist the provider id before polling. If Framewright restarts now, it
        // reconnects to this exact owned prompt instead of paying for a duplicate.
        await context.ReportSubmittedAsync(38, "Submitted to ComfyUI", promptId, cancellationToken);

        return await DownloadExistingImageAsync(client, endpoint, promptId, context, cancellationToken);
    }

    private async Task<GenerationAdapterOutput> DownloadExistingImageAsync(
        HttpClient client, Uri endpoint, string promptId, GenerationExecutionContext context, CancellationToken cancellationToken)
    {

        var file = await ComfyUiPolling.AwaitOutputAsync(
            client, endpoint, promptId, context, configuration,
            configuration.GetValue("Integrations:ComfyUi:ImageTimeoutMinutes", 45),
            TryFindImage, "image", promptId, cancellationToken);

        var view = new Uri(endpoint, $"/view?filename={Uri.EscapeDataString(file.FileName)}&subfolder={Uri.EscapeDataString(file.Subfolder)}&type={Uri.EscapeDataString(file.Type)}");
        using var image = await client.GetAsync(view, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!image.IsSuccessStatusCode) throw new GenerationDispatchException("ComfyUI completed but its output image could not be downloaded.", true, promptId);
        var buffered = await BoundedContentBuffer.CopyToTemporaryFileAsync(
            image.Content, AssetStore.MaxImageBytes,
            "The ComfyUI image exceeded the local 25 MB safety limit.", promptId,
            cancellationToken);
        var mime = image.Content.Headers.ContentType?.MediaType ?? (Path.GetExtension(file.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png");
        return new(buffered, file.FileName, mime, true, promptId);
    }

    private sealed record BoundReference(int ImageNumber, AuthorityBinding Binding);
    private sealed record ReferenceBindingResult(
        string Workflow,
        IReadOnlyList<BoundReference> Bound,
        IReadOnlyList<ReferenceDispatchDecision> Decisions);

    private static async Task<ReferenceBindingResult> BindReferenceImagesAsync(
        HttpClient client,
        Uri endpoint,
        string workflow,
        GenerationExecutionContext context,
        CancellationToken cancellationToken)
    {
        var availableSlots = Enumerable.Range(1, 3)
            .Where(slot => workflow.Contains($"{{{{REFERENCE_IMAGE_{slot}}}}}", StringComparison.Ordinal))
            .ToArray();
        var planned = GenerationReferenceDispatchPlanner.PlanImages(
            context.ReferenceImages,
            availableSlots.Length,
            context.IsCurrentFrameEdit);
        var visual = planned.Where(item => item.Decision.WillBindVisually).ToArray();
        var bound = new List<BoundReference>();
        for (var index = 0; index < visual.Length; index++)
        {
            var slot = availableSlots[index];
            var reference = visual[index];
            var uploaded = await ComfyUi.UploadImageAsync(client, endpoint, reference.Path, reference.Asset.MimeType, reference.Asset.OriginalFileName, cancellationToken);
            workflow = workflow.Replace($"\"{{{{REFERENCE_IMAGE_{slot}}}}}\"", JsonSerializer.Serialize(uploaded), StringComparison.Ordinal);
            var imageNumber = context.CompositionPath is null ? slot : slot + 1;
            bound.Add(new(imageNumber, reference.Binding));
        }
        return new(workflow, bound, planned.Select(item => item.Decision).ToArray());
    }

    private static string BuildImagePrompt(
        GenerationExecutionContext context,
        IReadOnlyList<BoundReference> references,
        IReadOnlyList<ReferenceDispatchDecision> decisions)
    {
        if (decisions.Count == 0) return context.Prompt;
        var map = references.Count == 0 ? "- No authority images are bound on this route." : string.Join('\n', references.Select(reference =>
            $"- IMAGE {reference.ImageNumber}: {reference.Binding.Name} ({reference.Binding.Category}) v{reference.Binding.Version}. {ReferenceRole(reference.Binding.Category)}"));
        var textOnly = decisions.Where(decision => !decision.WillBindVisually)
            .Select(decision => $"- {decision.Binding.Name} ({decision.Binding.Category}) v{decision.Binding.Version}: {decision.Detail}")
            .ToArray();
        var hasRepeatedAnchors = context.ReferenceImages.Any(reference => reference.Binding.Category.Equals("Role", StringComparison.OrdinalIgnoreCase)
            && (reference.Binding.Placements?.Count ?? 0) > 1);
        var baseFrameRule = context.CompositionPath is null
            ? "- Compose a new frame from the brief; attached authorities supply evidence only."
            : hasRepeatedAnchors
                ? "- IMAGE 1 is the sole composition and camera base. Preserve every explicit subject slot in that layout. Repeated spatial anchors deliberately require separate members of the same approved role; apply the authority consistently to each anchored member."
                : "- IMAGE 1 is the sole composition and camera base. Edit the existing people in that frame; an attached identity portrait does not by itself request an extra person.";
        return $"{context.Prompt}\n\nMULTI-REFERENCE SYNTHESIS CONTRACT:\n{baseFrameRule}\n- Render one coherent, continuous camera frame. Never make a collage, montage, split screen, contact sheet, double exposure, picture-in-picture layout, pasted cutout, floating portrait, or ghosted face.\n- Attached images are identity and design evidence, not pixels to place on the canvas. Discard their original backgrounds, crops, framing, poses, and unrelated subjects.\n- Render one distinct subject for each explicit character anchor. Repeated anchors using the same authority request multiple separate subjects; otherwise depict an attached character once. Never enlarge a reference face into the sky or background.\n- Spatial coordinates identify the center of the target subject or region; they never identify where to paste the input image.\n- Style authorities apply globally to the entire finished frame and never become a depicted person, object, panel, or spatial layer.\n\nATTACHED AUTHORITY IMAGE MAP:\n{map}\n\nTEXT-ONLY AUTHORITY MAP:\n{(textOnly.Length == 0 ? "- None." : string.Join('\n', textOnly))}";
    }

    private static void ApplyProjectImageCanvas(JsonNode? workflow, GenerationExecutionContext context)
    {
        if (workflow is not JsonObject graph || context.Purpose == GenerationPurpose.Video
            || context.DeliveryWidth is null || context.DeliveryHeight is null
            || context.DeliveryWidth <= 0 || context.DeliveryHeight <= 0) return;

        var aspect = (double)context.DeliveryWidth.Value / context.DeliveryHeight.Value;
        var height = Math.Max(64, (int)Math.Round(Math.Sqrt(1_000_000d / aspect) / 64d) * 64);
        var width = Math.Max(64, (int)Math.Round(height * aspect / 64d) * 64);
        foreach (var node in graph.Select(entry => entry.Value).OfType<JsonObject>().Where(node => node["class_type"]?.GetValue<string>() == "EmptySD3LatentImage"))
        {
            if (node["inputs"] is not JsonObject inputs) continue;
            inputs["width"] = width;
            inputs["height"] = height;
        }
    }

    private static string ReferenceRole(string category) => category.ToLowerInvariant() switch
    {
        "character" or "identity" => "Use this exact approved identity, facial structure, hair, proportions, skin tone, and distinctive marks for the assigned character. Preserve small marks on the specified anatomical side; do not borrow clothing from this image unless separately approved.",
        "face" => "Use only the approved facial identity and facial marks. Do not inherit pose, wardrobe, crop, or background.",
        "wardrobe" or "outfit" => "Use this exact approved clothing silhouette, materials, colors, accessories, and anatomical-side placement for the assigned character. Do not replace or reinterpret the character's separately approved face.",
        "style" => "Use this as the approved visual-language exemplar for rendering, palette, texture, lighting, and shape treatment. Do not copy its depicted subject into the shot.",
        "location" => "Use this as the approved architecture, spatial, material, and environmental authority. Preserve world features even when the current crop places them off camera.",
        "prop" => "Use this exact approved prop design, count, scale, materials, handedness, and attachment points where assigned.",
        _ => "Use this exact approved visual authority only where the packet assigns it; do not merely approximate its text description or let it redesign unrelated content."
    };

    private static void PruneUnboundReferenceNodes(JsonNode? workflow)
    {
        if (workflow is not JsonObject graph) return;
        var removed = graph
            .Where(node => node.Value is JsonObject nodeObject
                && nodeObject["inputs"]?["image"] is JsonValue image
                && image.TryGetValue<string>(out var value)
                && value.StartsWith("{{REFERENCE_IMAGE_", StringComparison.Ordinal))
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var nodeId in removed) graph.Remove(nodeId);
        if (removed.Count == 0) return;

        foreach (var node in graph)
        {
            if (node.Value is not JsonObject nodeObject || nodeObject["inputs"] is not JsonObject inputs) continue;
            var danglingInputs = inputs
                .Where(input => input.Value is JsonArray link
                    && link.Count == 2
                    && link[0]?.GetValue<string>() is { } nodeId
                    && removed.Contains(nodeId))
                .Select(input => input.Key)
                .ToArray();
            foreach (var inputName in danglingInputs) inputs.Remove(inputName);
        }
    }

    private static bool TryFindImage(JsonElement history, string promptId, out (string FileName, string Subfolder, string Type) file)
    {
        file = default;
        if (!history.TryGetProperty(promptId, out var entry) || !entry.TryGetProperty("outputs", out var outputs)) return false;
        foreach (var node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) continue;
            foreach (var image in images.EnumerateArray())
            {
                var name = image.TryGetProperty("filename", out var filename) ? filename.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                file = (name, image.TryGetProperty("subfolder", out var subfolder) ? subfolder.GetString() ?? "" : "", image.TryGetProperty("type", out var type) ? type.GetString() ?? "output" : "output");
                return true;
            }
        }
        return false;
    }

    private static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "…";
}

public sealed class ComfyUiVideoGenerationAdapter(IHttpClientFactory clients, IConfiguration configuration) : IGenerationAdapter, IRecoverableGenerationAdapter
{
    public const string AdapterId = "comfyui-h3-video";
    private bool Enabled => configuration.GetValue("Integrations:ComfyUi:VideoSubmissionEnabled", false);
    private string? WorkflowPath => ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalVideoWorkflowPath"]);
    private Uri? Endpoint => Uri.TryCreate(configuration["Integrations:ComfyUi:Endpoint"], UriKind.Absolute, out var uri) ? uri : null;

    public GenerationAdapterSummary Describe()
    {
        var endpoint = Endpoint; var local = endpoint is not null && (endpoint.IsLoopback || configuration.GetValue("Integrations:ComfyUi:AllowRemote", false)); var workflowReady = WorkflowPath is not null; var ready = Enabled && local && workflowReady;
        var state = !Enabled ? "Protected" : ready ? "Ready" : "NeedsSetup";
        var detail = !Enabled ? "H3 video submission is independently feature-gated and cannot touch the current ComfyUI queue."
            : !local ? "The endpoint is invalid or non-loopback; remote ComfyUI requires explicit AllowRemote approval."
            : !workflowReady ? "Choose an external API-format H3 video workflow with {{PROMPT}} and {{INPUT_IMAGE}} placeholders."
            : "External H3 workflow is ready. It joins the shared queue and never clears, interrupts, or edits ComfyUI.";
        return new(AdapterId, "ComfyUI H3 video", "Local service", state, detail, ready, [GenerationRoute.FastDraft], [GenerationPurpose.Video]);
    }

    public bool CanResume(string providerRequestId) => Guid.TryParse(providerRequestId, out _);

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        var descriptor = Describe(); if (!descriptor.CanDispatch) throw new GenerationDispatchException(descriptor.Detail);
        var endpoint = Endpoint!; var client = clients.CreateClient("comfyui-generation"); await ComfyUi.EnsureReachableAsync(client, endpoint, cancellationToken);
        if (context.ExistingProviderRequestId is { Length: > 0 } existingPromptId)
        {
            await context.ReportAsync(40, "Reconnected to the existing ComfyUI video job", cancellationToken);
            return await DownloadExistingVideoAsync(client, endpoint, existingPromptId, context, cancellationToken);
        }
        if (new FileInfo(WorkflowPath!).Length > ComfyUi.MaxWorkflowBytes) throw new GenerationDispatchException("The configured H3 workflow exceeds the 5 MB safety limit.");
        var raw = await File.ReadAllTextAsync(WorkflowPath!, cancellationToken);
        if (!raw.Contains("{{PROMPT}}", StringComparison.Ordinal)) throw new GenerationDispatchException("The H3 workflow must contain a {{PROMPT}} placeholder.");
        if (!raw.Contains("{{VIDEO_FPS}}", StringComparison.Ordinal) || !raw.Contains("{{VIDEO_LENGTH}}", StringComparison.Ordinal))
            throw new GenerationDispatchException("The H3 workflow must bind {{VIDEO_FPS}} and {{VIDEO_LENGTH}} so encoded timing matches the frozen shot contract.");
        if (context.VideoFramesPerSecond is not (>= 1 and <= 120) || context.VideoFrameCount is not (>= 1 and <= 86_400))
            throw new GenerationDispatchException("The frozen video packet has no valid project frame rate or shot frame count. Prepare a new video manifest.");

        // Three shapes share this adapter: text-to-video (no anchors), image-to-video
        // (first frame), and two-anchor (first + last). A workflow that binds
        // {{INPUT_IMAGE}} needs a first frame; one that does not is text-to-video.
        var wantsFirst = raw.Contains("{{INPUT_IMAGE}}", StringComparison.Ordinal);
        if (wantsFirst && (context.CompositionPath is null || context.Composition is null))
            throw new GenerationDispatchException("This H3 workflow is image-to-video and needs a selected still as its first frame.");
        string? firstName = null;
        if (wantsFirst) firstName = await ComfyUi.UploadImageAsync(client, endpoint, context.CompositionPath!, context.Composition!.MimeType, context.Composition.OriginalFileName, cancellationToken);
        string? lastName = null; if (context.LastFramePath is not null && context.LastFrame is not null) lastName = await ComfyUi.UploadImageAsync(client, endpoint, context.LastFramePath, context.LastFrame.MimeType, context.LastFrame.OriginalFileName, cancellationToken);

        raw = raw.Replace("\"{{PROMPT}}\"", JsonSerializer.Serialize(context.Prompt), StringComparison.Ordinal);
        if (firstName is not null) raw = raw.Replace("\"{{INPUT_IMAGE}}\"", JsonSerializer.Serialize(firstName), StringComparison.Ordinal);
        if (lastName is not null)
        {
            if (!raw.Contains("{{LAST_FRAME_IMAGE}}", StringComparison.Ordinal)) throw new GenerationDispatchException("A last frame was approved, but the H3 workflow has no {{LAST_FRAME_IMAGE}} placeholder.");
            raw = raw.Replace("\"{{LAST_FRAME_IMAGE}}\"", JsonSerializer.Serialize(lastName), StringComparison.Ordinal);
        }
        var profile = VideoProfile.For(context.VideoQuality, context.VideoWidth, context.VideoHeight, context.DeliveryWidth, context.DeliveryHeight);
        raw = raw.Replace("{{VIDEO_WIDTH}}", profile.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_HEIGHT}}", profile.Height.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_STEPS}}", profile.Steps.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_OUTPUT_WIDTH}}", profile.OutputWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_OUTPUT_HEIGHT}}", profile.OutputHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_FPS}}", context.VideoFramesPerSecond.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{VIDEO_LENGTH}}", context.VideoFrameCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("\"{{VIDEO_FILENAME_PREFIX}}\"", JsonSerializer.Serialize($"storyboard-studio/h3-{profile.Label.ToLowerInvariant()}"), StringComparison.Ordinal);
        var seed = context.VideoSeed ?? (BitConverter.ToInt64(Convert.FromHexString(context.ManifestHash[..16])) & long.MaxValue);
        raw = raw.Replace("\"{{SEED}}\"", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        JsonNode? workflow; try { workflow = JsonNode.Parse(raw); } catch (JsonException ex) { throw new GenerationDispatchException("The configured H3 workflow is invalid after placeholder binding.", false, null, ex); }
        // A last frame is optional. When none is attached, drop the node that was
        // going to load it rather than refusing the job — the same file then serves
        // single-anchor and two-anchor work.
        if (lastName is null)
        {
            ComfyUi.PruneUnboundOptionalNodes(workflow, "{{LAST_FRAME_IMAGE}}");
            if (workflow is JsonObject graph)
            {
                graph.Remove("18");
                if (graph["7"]?["inputs"] is JsonObject inputs) inputs.Remove("last_frame");
            }
        }
        var clientId = Guid.NewGuid().ToString("N");
        using var submit = await client.PostAsJsonAsync(new Uri(endpoint, "/prompt"), new { prompt = workflow, client_id = clientId }, cancellationToken);
        var requestId = submit.Headers.TryGetValues("x-request-id", out var requestIds) ? requestIds.FirstOrDefault() : null;
        var body = await ComfyUi.ReadControlJsonAsync(
            submit.Content, "The ComfyUI video submission response exceeded the 5 MB safety limit.", true, requestId, cancellationToken);
        if (!submit.IsSuccessStatusCode) throw new GenerationDispatchException($"ComfyUI rejected the H3 workflow with {(int)submit.StatusCode}: {ComfyUi.Bound(body)}", true, requestId);
        string promptId; try { using var json = JsonDocument.Parse(body); promptId = json.RootElement.GetProperty("prompt_id").GetString() ?? throw new JsonException(); } catch (Exception ex) { throw new GenerationDispatchException("ComfyUI did not return a video prompt id.", true, requestId, ex); }

        await context.ReportSubmittedAsync(38, "Submitted to ComfyUI", promptId, cancellationToken);
        return await DownloadExistingVideoAsync(client, endpoint, promptId, context, cancellationToken);
    }

    private async Task<GenerationAdapterOutput> DownloadExistingVideoAsync(
        HttpClient client, Uri endpoint, string promptId, GenerationExecutionContext context, CancellationToken cancellationToken)
    {

        var file = await ComfyUiPolling.AwaitOutputAsync(
            client, endpoint, promptId, context, configuration,
            configuration.GetValue("Integrations:ComfyUi:VideoTimeoutMinutes", 120),
            TryFindVideo, "video", promptId, cancellationToken);

        var view = new Uri(endpoint, $"/view?filename={Uri.EscapeDataString(file.FileName)}&subfolder={Uri.EscapeDataString(file.Subfolder)}&type={Uri.EscapeDataString(file.Type)}"); using var response = await client.GetAsync(view, HttpCompletionOption.ResponseHeadersRead, cancellationToken); if (!response.IsSuccessStatusCode) throw new GenerationDispatchException("ComfyUI completed but its video could not be downloaded.", true, promptId); var buffered = await BoundedContentBuffer.CopyToTemporaryFileAsync(response.Content, AssetStore.MaxMediaBytes, "The ComfyUI video exceeded the local 500 MB safety limit.", promptId, cancellationToken); var extension = Path.GetExtension(file.FileName); var mime = extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4"; return new(buffered, file.FileName, mime, true, promptId, AssetKind.Video);
    }

    private static bool TryFindVideo(JsonElement history, string promptId, out (string FileName, string Subfolder, string Type) file)
    {
        file = default; if (!history.TryGetProperty(promptId, out var entry) || !entry.TryGetProperty("outputs", out var outputs)) return false; foreach (var node in outputs.EnumerateObject()) foreach (var property in new[] { "videos", "gifs", "images" }) { if (!node.Value.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array) continue; foreach (var item in items.EnumerateArray()) { var name = item.TryGetProperty("filename", out var filename) ? filename.GetString() : null; if (string.IsNullOrWhiteSpace(name)) continue; var extension = Path.GetExtension(name); if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)) continue; file = (name, item.TryGetProperty("subfolder", out var subfolder) ? subfolder.GetString() ?? "" : "", item.TryGetProperty("type", out var type) ? type.GetString() ?? "output" : "output"); return true; } }
        return false;
    }
    private sealed record VideoProfile(string Label, int Width, int Height, int Steps, int OutputWidth, int OutputHeight)
    {
        public static VideoProfile For(VideoQuality quality, int? width, int? height, int? deliveryWidth, int? deliveryHeight)
        {
            var label = quality.ToString();
            var steps = quality switch { VideoQuality.Medium => 22, VideoQuality.High => 24, VideoQuality.Max => 32, _ => 20 };
            var nativeWidth = width ?? (quality switch { VideoQuality.Medium => 1152, VideoQuality.High or VideoQuality.Max => 1344, _ => 960 });
            var nativeHeight = height ?? (quality switch { VideoQuality.Medium => 480, VideoQuality.High or VideoQuality.Max => 560, _ => 400 });
            var outputWidth = quality is VideoQuality.High or VideoQuality.Max ? deliveryWidth ?? 2304 : nativeWidth;
            var outputHeight = quality is VideoQuality.High or VideoQuality.Max ? deliveryHeight ?? 960 : nativeHeight;
            return new(label, nativeWidth, nativeHeight, steps, outputWidth, outputHeight);
        }
    }
    private static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "...";
}

internal static class ProofPngRenderer
{
    public static byte[] Render(string seed, int width, int height)
    {
        var hash = Convert.FromHexString(seed[..Math.Min(seed.Length, 64)]);
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1); raw[row] = 0;
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 4;
                var horizon = y > height * .58;
                var grid = Math.Abs(x - width / 3) < 2 || Math.Abs(x - width * 2 / 3) < 2 || Math.Abs(y - height / 3) < 2 || Math.Abs(y - height * 2 / 3) < 2;
                var subjectA = Math.Pow((x - width * .35) / (width * .12), 2) + Math.Pow((y - height * .57) / (height * .31), 2) < 1;
                var subjectB = Math.Pow((x - width * .67) / (width * .09), 2) + Math.Pow((y - height * .6) / (height * .25), 2) < 1;
                var baseValue = horizon ? 39 : 27 + (int)(28.0 * y / height);
                raw[p] = (byte)Math.Clamp(baseValue + hash[0] / 12 + (subjectA ? 54 : 0), 0, 255);
                raw[p + 1] = (byte)Math.Clamp(baseValue + hash[1] / 16 + (subjectB ? 45 : 0), 0, 255);
                raw[p + 2] = (byte)Math.Clamp(baseValue + hash[2] / 13 + (subjectA || subjectB ? 30 : 0), 0, 255);
                if (grid) raw[p] = raw[p + 1] = raw[p + 2] = 92;
                raw[p + 3] = 255;
            }
        }
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> ihdr = stackalloc byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, width); BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height); ihdr[8] = 8; ihdr[9] = 6;
        WriteChunk(output, "IHDR"u8, ihdr);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(raw);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length); output.Write(length); output.Write(type); output.Write(data);
        var crcInput = new byte[type.Length + data.Length]; type.CopyTo(crcInput); data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput)); output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }
}
