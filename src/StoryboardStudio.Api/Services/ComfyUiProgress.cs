using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Services;

/// <summary>A node of the workflow Framewright submitted, so live progress can
/// say "Sampling" rather than a node number.</summary>
public sealed record ComfyUiNodeInfo(string ClassType, string? Title);

/// <summary>What ComfyUI has told Framewright's own client id about its prompt.</summary>
public sealed record ComfyUiProgressSnapshot(
    bool Connected,
    string? NodeId,
    string? NodeClass,
    string? NodeTitle,
    int? Value,
    int? Max,
    DateTimeOffset? LastActivityAt,
    TimeSpan? Remaining);

/// <summary>Live progress for one submitted prompt. Read-only: a watch never
/// sends anything to ComfyUI and never affects the queue.</summary>
public interface IComfyUiProgressWatch : IAsyncDisposable
{
    void Track(string promptId);
    ComfyUiProgressSnapshot Snapshot();
}

/// <summary>
/// ComfyUI reports sampler steps only over its websocket, and only to the
/// client id that submitted the prompt. Framewright submits with a client id
/// derived from its own job, listens on that id, and so hears about its own
/// work and nothing else.
/// </summary>
public interface IComfyUiProgressSource
{
    IComfyUiProgressWatch Watch(Uri endpoint, string clientId, IReadOnlyDictionary<string, ComfyUiNodeInfo> nodes);
}

/// <summary>Folds ComfyUI websocket messages into a progress snapshot.</summary>
internal sealed class ComfyUiProgressTracker(TimeProvider clock, IReadOnlyDictionary<string, ComfyUiNodeInfo> nodes)
{
    private readonly object gate = new();
    private string? promptId;
    private bool connected;
    private string? nodeId;
    private int? value;
    private int? max;
    private DateTimeOffset? lastActivity;
    // The most recent steps of the current node. Time left comes from these,
    // so a pause or a slow first step stops counting once it scrolls out.
    private const int RateWindow = 6;
    private readonly Queue<(int Value, DateTimeOffset At)> recentSteps = new();
    private string? rateNode;

    public void Track(string id) { lock (gate) promptId = id; }

    public void SetConnected(bool state) { lock (gate) connected = state; }

    public void Apply(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8Json.ToArray()); }
        catch (JsonException) { return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeNode) || typeNode.GetString() is not { } type) return;
            var data = root.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object ? payload : default;
            var messagePrompt = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("prompt_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var now = clock.GetUtcNow();
            lock (gate)
            {
                // Execution messages only ever reach the submitting client id,
                // but once the prompt id is known, hold them to it as well.
                if (messagePrompt is not null && promptId is not null && !string.Equals(messagePrompt, promptId, StringComparison.Ordinal)) return;
                switch (type)
                {
                    case "executing":
                        var node = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("node", out var nodeValue) && nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString() : null;
                        if (!string.Equals(node, nodeId, StringComparison.Ordinal)) { nodeId = node; value = null; max = null; }
                        lastActivity = now;
                        break;
                    case "progress":
                        if (!TryInt(data, "value", out var step) || !TryInt(data, "max", out var steps)) return;
                        var progressNode = data.TryGetProperty("node", out var progressNodeValue) && progressNodeValue.ValueKind == JsonValueKind.String ? progressNodeValue.GetString() : nodeId;
                        if (!string.Equals(progressNode, rateNode, StringComparison.Ordinal) || step < (value ?? 0))
                        {
                            rateNode = progressNode;
                            recentSteps.Clear();
                        }
                        recentSteps.Enqueue((step, now));
                        while (recentSteps.Count > RateWindow) recentSteps.Dequeue();
                        nodeId = progressNode; value = step; max = steps; lastActivity = now;
                        break;
                    case "progress_state" or "execution_start" or "execution_cached" or "executed" or "execution_success" or "execution_error" or "execution_interrupted":
                        lastActivity = now;
                        break;
                    // "status" is the queue count broadcast to every client; it
                    // says nothing about this prompt, so it is not activity.
                }
            }
        }
    }

    public ComfyUiProgressSnapshot Snapshot()
    {
        lock (gate)
        {
            ComfyUiNodeInfo? info = nodeId is not null && nodes.TryGetValue(nodeId, out var found) ? found : null;
            TimeSpan? remaining = null;
            if (rateNode is not null && string.Equals(rateNode, nodeId, StringComparison.Ordinal)
                && value is { } done && max is { } total && total > done
                && recentSteps.Count > 1 && recentSteps.Last().Value - recentSteps.Peek().Value >= 2)
            {
                // The median step time, so one long pause (a stall that then
                // recovered) cannot inflate the estimate.
                var perStep = recentSteps.Zip(recentSteps.Skip(1))
                    .Where(pair => pair.Second.Value > pair.First.Value)
                    .Select(pair => (pair.Second.At - pair.First.At) / (pair.Second.Value - pair.First.Value))
                    .Order()
                    .ToArray();
                if (perStep.Length > 0) remaining = perStep[perStep.Length / 2] * (total - done);
            }
            return new(connected, nodeId, info?.ClassType, info?.Title, value, max, lastActivity, remaining);
        }
    }

    private static bool TryInt(JsonElement data, string property, out int number)
    {
        number = 0;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Number) return false;
        number = (int)Math.Round(node.GetDouble());
        return true;
    }
}

/// <summary>Turns a progress snapshot into the job's phase, percent, and any
/// warning. Kept apart from the transport so it can be tested exactly.</summary>
internal static class ComfyUiProgressText
{
    internal readonly record struct Live(int Percent, string Phase, string? Warning);

    internal static Live? Describe(ComfyUiProgressSnapshot snapshot, DateTimeOffset now, TimeSpan stallAfter, string kind)
    {
        // Nothing heard yet means nothing to say beyond the queue position.
        if (snapshot.NodeId is null && snapshot.LastActivityAt is null) return null;
        var stage = Stage(snapshot.NodeClass, kind);
        var sampling = stage == "Sampling";
        var counted = snapshot.Value is { } value && snapshot.Max is { } max && max > 1
            ? sampling || snapshot.NodeClass is null ? $"step {value} of {max}" : $"{value} of {max}"
            : null;

        if (snapshot.Connected && snapshot.LastActivityAt is { } heard && now - heard >= stallAfter)
        {
            var minutes = Math.Max(1, (int)Math.Floor((now - heard).TotalMinutes));
            var where = counted is null ? $" while {char.ToLowerInvariant(stage[0])}{stage[1..]}" : $" at {counted}";
            return new(
                Percent(stage, snapshot),
                $"No progress for {minutes} min{where}",
                $"ComfyUI hasn't reported progress on this {kind} for {minutes} minute{(minutes == 1 ? "" : "s")}{where}. That usually means it ran out of GPU memory and is swapping the model. Framewright is still waiting and won't stop the render. To recover, close other GPU work or restart ComfyUI; after a restart this job fails within a minute and you can choose Retry.");
        }

        var phase = counted is null ? stage : $"{stage} · {counted}";
        if (sampling && snapshot.Remaining is { } remaining) phase += $" · {Remaining(remaining)}";
        return new(Percent(stage, snapshot), phase, null);
    }

    internal static string Stage(string? classType, string kind) => classType switch
    {
        null => "Rendering",
        "MiniMaxH3ImageToVideo" => "Preparing the H3 render",
        var type when type.StartsWith("KSampler", StringComparison.Ordinal) || type.StartsWith("SamplerCustom", StringComparison.Ordinal) => "Sampling",
        var type when type.Contains("Loader", StringComparison.Ordinal) => "Loading models",
        var type when type.Contains("TextEncode", StringComparison.Ordinal) => "Reading the prompt",
        var type when type.StartsWith("VAEDecode", StringComparison.Ordinal) => kind == "video" ? "Decoding frames" : "Decoding the image",
        var type when type.StartsWith("VAEEncode", StringComparison.Ordinal) => "Encoding the source frame",
        "LoadImage" => "Loading the frames",
        var type when type.StartsWith("ImageScale", StringComparison.Ordinal) || type.Contains("Upscale", StringComparison.Ordinal) => "Scaling to the project canvas",
        "CreateVideo" or "SaveVideo" or "VHS_VideoCombine" => "Writing the video file",
        "SaveImage" => "Saving the image",
        _ => "Rendering"
    };

    private static int Percent(string stage, ComfyUiProgressSnapshot snapshot) => stage switch
    {
        "Sampling" when snapshot.Value is { } value && snapshot.Max is { } max && max > 0 => 50 + (int)(28.0 * Math.Clamp(value, 0, max) / max),
        "Sampling" => 50,
        "Loading models" or "Reading the prompt" or "Encoding the source frame" or "Loading the frames" or "Preparing the H3 render" => 48,
        "Decoding frames" or "Decoding the image" or "Scaling to the project canvas" or "Writing the video file" or "Saving the image" => 79,
        _ => 62
    };

    private static string Remaining(TimeSpan remaining)
        => remaining < TimeSpan.FromMinutes(1) ? "under a minute left" : $"about {(int)Math.Ceiling(remaining.TotalMinutes)} min left";
}

/// <summary>Listens on ComfyUI's websocket under Framewright's own client id.
/// It only receives; a dropped connection is retried quietly and the job falls
/// back to queue-position progress in the meantime.</summary>
public sealed partial class WebSocketComfyUiProgressSource(TimeProvider clock, ILogger<WebSocketComfyUiProgressSource> logger) : IComfyUiProgressSource
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ComfyUI progress socket {Uri} dropped; retrying in {Delay}.")]
    private static partial void LogSocketDropped(ILogger logger, Exception exception, Uri uri, TimeSpan delay);

    // ComfyUI's JSON messages are small; anything larger is not progress.
    private const int MaxMessageBytes = 1024 * 1024;
    private readonly TimeProvider clock = clock;
    private readonly ILogger logger = logger;

    public IComfyUiProgressWatch Watch(Uri endpoint, string clientId, IReadOnlyDictionary<string, ComfyUiNodeInfo> nodes)
        => new Watcher(this, endpoint, clientId, nodes);

    private sealed class Watcher : IComfyUiProgressWatch
    {
        private readonly WebSocketComfyUiProgressSource owner;
        private readonly ComfyUiProgressTracker tracker;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public Watcher(WebSocketComfyUiProgressSource owner, Uri endpoint, string clientId, IReadOnlyDictionary<string, ComfyUiNodeInfo> nodes)
        {
            this.owner = owner;
            tracker = new(owner.clock, nodes);
            var socket = new UriBuilder(endpoint)
            {
                Scheme = endpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Path = "/ws",
                Query = $"clientId={Uri.EscapeDataString(clientId)}"
            }.Uri;
            loop = Task.Run(() => ListenAsync(socket, stop.Token));
        }

        public void Track(string promptId) => tracker.Track(promptId);

        public ComfyUiProgressSnapshot Snapshot() => tracker.Snapshot();

        private async Task ListenAsync(Uri uri, CancellationToken cancellationToken)
        {
            var retry = TimeSpan.FromSeconds(1);
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                try
                {
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(10));
                        await socket.ConnectAsync(uri, connect.Token);
                    }
                    tracker.SetConnected(true);
                    retry = TimeSpan.FromSeconds(1);
                    var overflow = false;
                    while (socket.State == WebSocketState.Open)
                    {
                        var received = await socket.ReceiveAsync(buffer, cancellationToken);
                        if (received.MessageType == WebSocketMessageType.Close) break;
                        // Binary frames are preview images; they are not progress.
                        if (received.MessageType == WebSocketMessageType.Text)
                        {
                            if (message.Length + received.Count > MaxMessageBytes) overflow = true;
                            else message.Write(buffer, 0, received.Count);
                        }
                        if (!received.EndOfMessage) continue;
                        if (received.MessageType == WebSocketMessageType.Text && !overflow)
                            tracker.Apply(message.GetBuffer().AsSpan(0, (int)message.Length));
                        message.SetLength(0);
                        overflow = false;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
                {
                    LogSocketDropped(owner.logger, ex, uri, retry);
                }
                finally
                {
                    tracker.SetConnected(false);
                    message.SetLength(0);
                }
                try { await Task.Delay(retry, owner.clock, cancellationToken); }
                catch (OperationCanceledException) { break; }
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 15));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await loop; }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            stop.Dispose();
        }
    }
}

internal static class ComfyUiWorkflowNodes
{
    /// <summary>The ComfyUI client id for a Framewright job. Stable across a
    /// restart, so a resumed job hears its own prompt again.</summary>
    internal static string ClientIdFor(Guid jobId) => $"framewright-{jobId:N}";

    internal static IReadOnlyDictionary<string, ComfyUiNodeInfo> Map(JsonNode? workflow)
    {
        var map = new Dictionary<string, ComfyUiNodeInfo>(StringComparer.Ordinal);
        if (workflow is not JsonObject graph) return map;
        foreach (var (id, value) in graph)
        {
            if (value is not JsonObject node || node["class_type"] is not JsonValue type || !type.TryGetValue<string>(out var classType)) continue;
            var title = node["_meta"] is JsonObject meta && meta["title"] is JsonValue titleValue && titleValue.TryGetValue<string>(out var text) ? text : null;
            map[id] = new(classType, title);
        }
        return map;
    }
}
