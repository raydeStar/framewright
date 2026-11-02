using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// What the artist sees while ComfyUI works: a rejection explained in plain
/// words, real sampler steps with a measured time left, a warning when a
/// render stops moving, an install check before anything is sent, and a Max
/// promotion that can be pressed again after it failed.
///
/// Every network test talks to a stand-in ComfyUI on a random loopback port,
/// never to the workstation's own ComfyUI.
/// </summary>
[Collection("Provider adapter environment")]
public sealed class ComfyUiLiveFeedbackTests : IDisposable
{
    private static readonly byte[] PixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly string root = Path.Combine(Path.GetTempPath(), "framewright-comfy-feedback", Guid.NewGuid().ToString("N"));

    public ComfyUiLiveFeedbackTests() => Directory.CreateDirectory(root);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    [Fact]
    public void ARejectedPromptNamesTheMissingModelFileAndWhereItGoes()
    {
        // ComfyUI's own answer when a loader's file is not installed. Model
        // lists longer than 20 are summarized, so the file is in extra_info.
        const string body = """
            {"error":{"type":"prompt_outputs_failed_validation","message":"Prompt outputs failed validation","details":"Value not in list: unet_name: 'minimax_h3_fl2va_pruned_int8_convrot.safetensors' not in (list of length 31)","extra_info":{}},
             "node_errors":{"1":{"errors":[{"type":"value_not_in_list","message":"Value not in list","details":"unet_name: 'minimax_h3_fl2va_pruned_int8_convrot.safetensors' not in (list of length 31)","extra_info":{"input_name":"unet_name","input_config":null,"received_value":"minimax_h3_fl2va_pruned_int8_convrot.safetensors"}}],"dependent_outputs":["15"],"class_type":"UNETLoader"}}}
            """;

        var text = ComfyUiRejection.Describe(400, body, "H3 video");

        Assert.Contains("minimax_h3_fl2va_pruned_int8_convrot.safetensors", text);
        Assert.Contains("ComfyUI/models/diffusion_models", text);
        Assert.Contains("nothing was queued", text);
        Assert.DoesNotContain("node_errors", text);
    }

    [Fact]
    public void ARejectedPromptNamesTheMissingNodeType()
    {
        const string body = """{"error":{"type":"missing_node_type","message":"Node 'Framewright quality-profile render' not found. The custom node may not be installed.","details":"Node ID '#7'","extra_info":{"node_id":"7","class_type":"MiniMaxH3ImageToVideo","node_title":"Framewright quality-profile render"}},"node_errors":{}}""";

        var text = ComfyUiRejection.Describe(400, body, "H3 video");

        Assert.Contains("'MiniMaxH3ImageToVideo'", text);
        Assert.Contains("Update ComfyUI", text);
    }

    [Fact]
    public void AnUnrecognizedRejectionStillShowsWhatComfyUiSaid()
    {
        var text = ComfyUiRejection.Describe(502, "<html>Bad gateway</html>", "image");

        Assert.Contains("rejected the image workflow with 502", text);
        Assert.Contains("Bad gateway", text);
    }

    [Fact]
    public void LiveProgressReportsTheSamplerStepAndAMeasuredTimeLeft()
    {
        var clock = new ManualClock();
        var tracker = new ComfyUiProgressTracker(clock, new Dictionary<string, ComfyUiNodeInfo> { ["12"] = new("SamplerCustomAdvanced", null) });
        tracker.SetConnected(true);
        tracker.Track("p-1");
        tracker.Apply(Message("executing", new { node = "12", prompt_id = "p-1" }));
        tracker.Apply(Message("progress", new { value = 1, max = 32, node = "12", prompt_id = "p-1" }));
        clock.Advance(TimeSpan.FromSeconds(14));
        tracker.Apply(Message("progress", new { value = 3, max = 32, node = "12", prompt_id = "p-1" }));

        var live = ComfyUiProgressText.Describe(tracker.Snapshot(), clock.GetUtcNow(), TimeSpan.FromMinutes(10), "video");

        // Seven seconds a step, 29 steps to go: 203 seconds.
        Assert.NotNull(live);
        Assert.Equal("Sampling · step 3 of 32 · about 4 min left", live.Value.Phase);
        Assert.Equal(52, live.Value.Percent);
        Assert.Null(live.Value.Warning);
    }

    [Fact]
    public void TimeLeftRecoversFromAStallInsteadOfAveragingItIn()
    {
        var clock = new ManualClock();
        var tracker = new ComfyUiProgressTracker(clock, new Dictionary<string, ComfyUiNodeInfo> { ["12"] = new("SamplerCustomAdvanced", null) });
        tracker.SetConnected(true);
        tracker.Apply(Message("progress", new { value = 1, max = 32, node = "12", prompt_id = "p-1" }));
        // Ten minutes stuck on the first step (the offload stall), then a
        // healthy five seconds a step. The stalled step is still among the
        // recent steps the estimate looks at.
        clock.Advance(TimeSpan.FromMinutes(10));
        for (var step = 2; step <= 5; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            tracker.Apply(Message("progress", new { value = step, max = 32, node = "12", prompt_id = "p-1" }));
        }

        var live = ComfyUiProgressText.Describe(tracker.Snapshot(), clock.GetUtcNow(), TimeSpan.FromMinutes(10), "video");

        // 27 steps at 5 seconds, not 27 steps at the 2.6-minute average.
        Assert.Equal("Sampling · step 5 of 32 · about 3 min left", live!.Value.Phase);
        Assert.Null(live.Value.Warning);
    }

    [Fact]
    public void ARenderThatStopsReportingRaisesAWarningThatQueueChatterDoesNotHide()
    {
        var clock = new ManualClock();
        var tracker = new ComfyUiProgressTracker(clock, new Dictionary<string, ComfyUiNodeInfo> { ["12"] = new("SamplerCustomAdvanced", null) });
        tracker.SetConnected(true);
        tracker.Apply(Message("executing", new { node = "12", prompt_id = "p-1" }));
        tracker.Apply(Message("progress", new { value = 1, max = 32, node = "12", prompt_id = "p-1" }));
        clock.Advance(TimeSpan.FromMinutes(11));
        // The queue-count broadcast keeps arriving for everyone; it is not
        // progress on this prompt.
        tracker.Apply(Message("status", new { status = new { exec_info = new { queue_remaining = 1 } } }));

        var live = ComfyUiProgressText.Describe(tracker.Snapshot(), clock.GetUtcNow(), TimeSpan.FromMinutes(10), "video");

        Assert.NotNull(live);
        Assert.Equal("No progress for 11 min at step 1 of 32", live.Value.Phase);
        Assert.Contains("hasn't reported progress on this video for 11 minutes", live.Value.Warning);
        Assert.Contains("won't stop the render", live.Value.Warning);
    }

    [Fact]
    public void ProgressForAnotherPromptIsIgnoredAndADisconnectedWatchRaisesNoWarning()
    {
        var clock = new ManualClock();
        var tracker = new ComfyUiProgressTracker(clock, new Dictionary<string, ComfyUiNodeInfo>());
        tracker.Track("ours");
        tracker.Apply(Message("progress", new { value = 9, max = 20, node = "3", prompt_id = "someone-else" }));
        Assert.Null(tracker.Snapshot().Value);

        tracker.Apply(Message("progress", new { value = 2, max = 20, node = "3", prompt_id = "ours" }));
        clock.Advance(TimeSpan.FromHours(1));
        var live = ComfyUiProgressText.Describe(tracker.Snapshot(), clock.GetUtcNow(), TimeSpan.FromMinutes(10), "image");

        // Without a live connection there is no evidence either way.
        Assert.NotNull(live);
        Assert.Null(live.Value.Warning);
        Assert.Equal("Rendering · step 2 of 20", live.Value.Phase);
    }

    [Fact]
    public async Task TheH3AdapterSubmitsUnderItsJobClientIdAndReportsStepsThenAStall()
    {
        var imagePath = Path.Combine(root, "first.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "video.json");
        await File.WriteAllTextAsync(workflowPath, """{"1":{"class_type":"LoadImage","inputs":{"image":"{{INPUT_IMAGE}}"}},"12":{"class_type":"SamplerCustomAdvanced","inputs":{"text":"{{PROMPT}}","seed":"{{SEED}}","steps":{{VIDEO_STEPS}},"length":{{VIDEO_LENGTH}}}},"15":{"class_type":"SaveVideo","inputs":{"video":["12",0],"fps":{{VIDEO_FPS}}}}}""");
        var mp4 = new byte[24]; "ftyp"u8.CopyTo(mp4.AsSpan(4));
        var calls = new List<string>();
        string? submitted = null;
        var historyReads = 0;
        var handler = new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (calls) calls.Add($"{request.Method} {path}");
            if (path == "/queue") return Json("{\"queue_running\":[[0,\"video-live\"]],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json("{\"name\":\"first.png\"}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"video-live\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal))
                return Json(++historyReads < 4 ? "{}" : "{\"video-live\":{\"outputs\":{\"15\":{\"videos\":[{\"filename\":\"take.mp4\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mp4) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var now = DateTimeOffset.UtcNow;
        var snapshots = 0;
        var progress = new ScriptedProgressSource(() => ++snapshots == 1
            ? new ComfyUiProgressSnapshot(true, "12", "SamplerCustomAdvanced", null, 4, 32, DateTimeOffset.UtcNow, null)
            : new ComfyUiProgressSnapshot(true, "12", "SamplerCustomAdvanced", null, 4, 32, now - TimeSpan.FromMinutes(20), null));
        var adapter = new ComfyUiVideoGenerationAdapter(new Factory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1",
            ["Integrations:ComfyUi:StallWarningSeconds"] = "60"
        }), progress: progress);
        var phases = new List<string>();
        var warnings = new List<string?>();
        var context = VideoContext(imagePath) with
        {
            Report = (_, phase, _, _) => { phases.Add(phase); return Task.CompletedTask; },
            Attention = (warning, _) => { warnings.Add(warning); return Task.CompletedTask; },
        };

        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        using (var envelope = JsonDocument.Parse(submitted!))
            Assert.Equal($"framewright-{context.JobId:N}", envelope.RootElement.GetProperty("client_id").GetString());
        Assert.Equal($"framewright-{context.JobId:N}", progress.ClientId);
        Assert.Equal("video-live", progress.Tracked);
        Assert.Equal("SamplerCustomAdvanced", progress.Nodes!["12"].ClassType);
        Assert.Contains("Sampling · step 4 of 32", phases);
        Assert.Contains(phases, phase => phase.StartsWith("No progress for 20 min at step 4 of 32", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning?.Contains("hasn't reported progress", StringComparison.Ordinal) == true);
        // Watching is read-only: nothing is posted except the one prompt and upload.
        Assert.DoesNotContain(calls, call => call.StartsWith("POST", StringComparison.Ordinal) && call is not ("POST /prompt" or "POST /upload/image"));
        Assert.True(progress.Disposed);
    }

    [Fact]
    public async Task AComfyUiRejectionReachesTheJobAsPlainWords()
    {
        var imagePath = Path.Combine(root, "first.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "video.json");
        await File.WriteAllTextAsync(workflowPath, """{"1":{"class_type":"LoadImage","inputs":{"image":"{{INPUT_IMAGE}}"}},"2":{"class_type":"SaveVideo","inputs":{"text":"{{PROMPT}}","fps":{{VIDEO_FPS}},"length":{{VIDEO_LENGTH}}}}}""");
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Task.FromResult(Json("{\"queue_running\":[],\"queue_pending\":[]}"));
            if (path == "/upload/image") return Task.FromResult(Json("{\"name\":\"first.png\"}"));
            if (path == "/prompt") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"type":"prompt_outputs_failed_validation","message":"Prompt outputs failed validation","details":"","extra_info":{}},"node_errors":{"4":{"errors":[{"type":"value_not_in_list","message":"Value not in list","details":"vae_name: 'h3_vae.safetensors' not in []","extra_info":{"input_name":"vae_name","received_value":"h3_vae.safetensors"}}],"class_type":"VAELoader"}}}""", Encoding.UTF8, "application/json")
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var adapter = new ComfyUiVideoGenerationAdapter(new Factory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = workflowPath,
        }));

        var failure = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(VideoContext(imagePath), CancellationToken.None));

        Assert.Equal("Missing model file: h3_vae.safetensors (put it in ComfyUI/models/vae). ComfyUI refused the H3 video workflow, so nothing was queued. Add the file, restart ComfyUI, then choose Retry.", failure.Message);
    }

    [Fact]
    public async Task TheWebSocketWatchHearsOnlyItsOwnClientIdAndSkipsPreviewFrames()
    {
        await using var comfy = await FakeComfyUi.StartAsync((app, fake) => app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            lock (fake.SocketClients) fake.SocketClients.Add(context.Request.Query["clientId"].ToString());
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SendAsync(socket, new { type = "status", data = new { status = new { exec_info = new { queue_remaining = 1 } }, sid = "x" } });
            await socket.SendAsync(new byte[] { 0, 0, 0, 1, 0x89, 0x50 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await SendAsync(socket, new { type = "executing", data = new { node = "12", display_node = "12", prompt_id = "p-live" } });
            await SendAsync(socket, new { type = "progress", data = new { value = 5, max = 32, prompt_id = "p-live", node = "12" } });
            var buffer = new byte[256];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); break; }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }));
        var source = new WebSocketComfyUiProgressSource(TimeProvider.System, NullLogger<WebSocketComfyUiProgressSource>.Instance);

        ComfyUiProgressSnapshot snapshot;
        await using (var watch = source.Watch(comfy.Endpoint, "framewright-test-job", new Dictionary<string, ComfyUiNodeInfo> { ["12"] = new("SamplerCustomAdvanced", "H3 sampler") }))
        {
            watch.Track("p-live");
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            do
            {
                snapshot = watch.Snapshot();
                if (snapshot.Value == 5) break;
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
        }

        Assert.True(snapshot.Connected);
        Assert.Equal(5, snapshot.Value);
        Assert.Equal(32, snapshot.Max);
        Assert.Equal("SamplerCustomAdvanced", snapshot.NodeClass);
        Assert.Equal("framewright-test-job", Assert.Single(comfy.SocketClients));
        Assert.All(comfy.Requests, request => Assert.Equal("GET /ws", request));
    }

    [Fact]
    public async Task TheInstallCheckNamesTheMissingNodeAndModelFileFromComfyUisOwnLists()
    {
        var entry = H3Entry();
        await using var comfy = await FakeComfyUi.StartAsync((app, _) => app.MapGet("/object_info/{node}", (string node) =>
            node == "MiniMaxH3ImageToVideo" || !entry.RequiredNodeTypes.Contains(node)
                ? Results.Json(new { })
                : Results.Json(new Dictionary<string, object> { [node] = NodeInfo(node, clipFile: "qwen_2.5_vl_7b_fp8_scaled.safetensors") })));
        var check = new ComfyUiInstallCheck(new ProbeFactory(), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = H3WorkflowPath(),
        }), new WorkflowLibrary(Path.GetDirectoryName(H3WorkflowPath())!));

        var result = Assert.Single(await check.CheckConfiguredAsync(comfy.Endpoint, CancellationToken.None));

        Assert.Equal("H3 video", result.Route);
        Assert.Equal(entry.Name, result.Workflow);
        Assert.Equal("Missing", result.State);
        Assert.Equal("MiniMaxH3ImageToVideo", Assert.Single(result.MissingNodes));
        var model = Assert.Single(result.MissingModels);
        Assert.Equal("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors", model.File);
        Assert.Equal("text_encoders", model.Folder);
        Assert.Contains("qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors in models/text_encoders", result.Detail);
        Assert.All(comfy.Requests, request => Assert.StartsWith("GET /object_info/", request));
    }

    [Fact]
    public async Task TheInstallCheckPassesAnInstallationThatHasEverythingAndStopsAskingWhenComfyUiFails()
    {
        var entry = H3Entry();
        await using var complete = await FakeComfyUi.StartAsync((app, _) => app.MapGet("/object_info/{node}", (string node) =>
            Results.Json(new Dictionary<string, object> { [node] = NodeInfo(node, clipFile: "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors") })));
        var configuration = Configuration(new Dictionary<string, string?> { ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = H3WorkflowPath() });

        var ready = Assert.Single(await new ComfyUiInstallCheck(new ProbeFactory(), configuration).CheckConfiguredAsync(complete.Endpoint, CancellationToken.None));
        Assert.Equal("Ready", ready.State);
        Assert.Contains($"all {entry.RequiredNodeTypes.Length} node types and 3 model files", ready.Detail);

        await using var failing = await FakeComfyUi.StartAsync((app, _) => app.MapGet("/object_info/{node}", () => Results.StatusCode(500)));
        var unknown = Assert.Single(await new ComfyUiInstallCheck(new ProbeFactory(), configuration).CheckConfiguredAsync(failing.Endpoint, CancellationToken.None));
        Assert.Equal("Unknown", unknown.State);
        Assert.Single(failing.Requests);
    }

    [Fact]
    public async Task TheSetupConnectionTestListsWhatEachConfiguredWorkflowNeeds()
    {
        await using var comfy = await FakeComfyUi.StartAsync((app, _) =>
        {
            app.MapGet("/queue", () => Results.Json(new { queue_running = Array.Empty<object>(), queue_pending = Array.Empty<object>() }));
            app.MapGet("/object_info/{node}", () => Results.Json(new { }));
        });
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        using var probe = new HttpRequestMessage(HttpMethod.Post, "/api/setup/comfyui/test") { Content = JsonContent.Create(new ComfyUiConnectionTestRequest(comfy.Endpoint.ToString())) };
        probe.Headers.Add("X-Storyboard-Studio", "1");

        var result = await (await client.SendAsync(probe)).Content.ReadFromJsonAsync<ComfyUiConnectionTest>() ?? throw new InvalidOperationException();

        Assert.True(result.Reachable);
        Assert.NotNull(result.Workflows);
        var video = Assert.Single(result.Workflows, item => item.Route == "H3 video");
        Assert.Equal("Missing", video.State);
        Assert.Contains("MiniMaxH3ImageToVideo", video.MissingNodes);
        Assert.Contains("need files or nodes that ComfyUI doesn't have yet", result.Detail);
        Assert.DoesNotContain(comfy.Requests, request => request.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VideoPreflightBlocksAPacketWhoseWorkflowComfyUiCannotRun()
    {
        await using var comfy = await FakeComfyUi.StartAsync((app, _) => app.MapGet("/object_info/{node}", (string node) =>
            node == "MiniMaxH3ImageToVideo" ? Results.Json(new { }) : Results.Json(new Dictionary<string, object> { [node] = NodeInfo(node, clipFile: "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors") })));
        using var factory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "framewright-preflight-install", Guid.NewGuid().ToString("N")), deleteDataRoot: true,
            startGenerationWorker: false,
            settings: new Dictionary<string, string?>
            {
                ["Integrations:ComfyUi:Endpoint"] = comfy.Endpoint.ToString().TrimEnd('/'),
                ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            });
        using var client = factory.CreateClient();
        var image = await AssetUploads.ImageAsync(client, "preflight-first.png");
        var shot = (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-PRE", "Install preflight", "A deck at dusk.", 48, "35mm · locked", "Hold.", [], [], image.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;
        var manifest = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
            new PrepareVideoManifestRequest(shot.Version, "A slow drift along the rail.")))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>())!;

        var preflight = (await client.GetFromJsonAsync<GenerationPreflightSummary>($"/api/manifests/{manifest.Id}/preflight?adapterId=comfyui-h3-video"))!;

        Assert.False(preflight.Ready);
        var installed = Assert.Single(preflight.Checks, check => check.Title == "ComfyUI has this workflow's nodes and models");
        Assert.Equal("Block", installed.State);
        Assert.Contains("MiniMaxH3ImageToVideo", installed.Detail);
        Assert.DoesNotContain(comfy.Requests, request => !request.StartsWith("GET /object_info/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotingAgainAfterAFailedMaxRetriesTheSameFrozenPacketAndNeverDuplicatesARunningOne()
    {
        var video = new ScriptedVideoAdapter { FailMax = true };
        using var factory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "framewright-max-retry", Guid.NewGuid().ToString("N")), deleteDataRoot: true,
            startGenerationWorker: false,
            configureServices: services => services.AddSingleton<IGenerationAdapter>(video));
        using var client = factory.CreateClient();
        var image = await AssetUploads.ImageAsync(client, "max-retry-first.png");
        var shot = (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-MAX", "Max retry", "A deck at dusk.", 48, "35mm · locked", "Hold.", [], [], image.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;
        var low = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
            new PrepareVideoManifestRequest(shot.Version, "A slow drift along the rail.")))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>())!;
        var lowJob = (await (await client.PostAsJsonAsync($"/api/manifests/{low.Id}/dispatch", new DispatchManifestRequest(low.ManifestHash, video.Describe().Id)))
            .Content.ReadFromJsonAsync<JobSummary>())!;
        await RunAsync(factory, lowJob.Id);
        var take = await ShotAsync(client, shot.Id);
        Assert.Equal(ShotStage.Video, take.Stage);

        var first = await PromoteAsync(client, shot.Id, lowJob.Id, take.Version, video);
        var pressedAgain = await PromoteAsync(client, shot.Id, lowJob.Id, take.Version, video);
        Assert.Equal(first.Id, pressedAgain.Id);

        await RunAsync(factory, first.Id);
        var failed = await JobAsync(client, first.Id);
        Assert.Equal(JobState.Failed, failed.State);

        video.FailMax = false;
        var retried = await PromoteAsync(client, shot.Id, lowJob.Id, take.Version, video);
        Assert.NotEqual(first.Id, retried.Id);
        Assert.Equal(2, retried.Attempt);
        Assert.Equal(first.Id, retried.RetryOfJobId);
        Assert.Equal(first.ManifestId, retried.ManifestId);

        await RunAsync(factory, retried.Id);
        Assert.Equal(JobState.Completed, (await JobAsync(client, retried.Id)).State);
        Assert.Equal(2, video.MaxRenders);
        // The failed attempt is still history, not rewritten.
        Assert.Equal(JobState.Failed, (await JobAsync(client, first.Id)).State);
    }

    [Fact]
    public async Task JobWarningsV25UpgradesAWorkstationWhoseJobsCannotHoldAWarning()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-schema-job-warning", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");
        try
        {
            Guid jobId;
            using (var baseline = new StudioApiFactory(dataRoot, deleteDataRoot: false, startGenerationWorker: false))
            using (var client = baseline.CreateClient())
            {
                var image = await AssetUploads.ImageAsync(client, "before-v25.png");
                var shot = (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
                    "SH-V25", "Before v25", "A queued render from before warnings.", 48, "35mm · locked", "Hold.", [], [], image.Id)))
                    .Content.ReadFromJsonAsync<ShotSummary>())!;
                var manifest = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
                    new PrepareVideoManifestRequest(shot.Version, "A slow drift.")))
                    .Content.ReadFromJsonAsync<GenerationManifestSummary>())!;
                jobId = (await (await client.PostAsJsonAsync($"/api/manifests/{manifest.Id}/dispatch", new DispatchManifestRequest(manifest.ManifestHash, "serial-probe")))
                    .Content.ReadFromJsonAsync<JobSummary>())!.Id;
            }

            // Reproduce a workstation that recorded v24 and never saw v25.
            await using (var legacy = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var downgrade = legacy.CreateCommand();
                downgrade.CommandText = """
                    DELETE FROM "SchemaMigrations" WHERE "Id" = '20261101-job-warnings-v25';
                    ALTER TABLE "Jobs" DROP COLUMN "Warning";
                    """;
                await downgrade.ExecuteNonQueryAsync();
            }

            using (var upgraded = new StudioApiFactory(dataRoot, deleteDataRoot: false, startGenerationWorker: false))
            using (var client = upgraded.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
                var job = await JobAsync(client, jobId);
                Assert.Equal(JobState.Queued, job.State);
                Assert.Null(job.Warning);
            }

            await using var verified = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verified.OpenAsync();
            await using var ledger = verified.CreateCommand();
            ledger.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Id\" = '20261101-job-warnings-v25';";
            Assert.Equal(1L, (long)(await ledger.ExecuteScalarAsync() ?? 0L));
            await using var column = verified.CreateCommand();
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Jobs') WHERE name = 'Warning';";
            Assert.Equal(1L, (long)(await column.ExecuteScalarAsync() ?? 0L));
            Assert.NotEmpty(Directory.EnumerateFiles(dataRoot, "*job-warnings-v25*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static async Task<JobSummary> PromoteAsync(HttpClient client, Guid shotId, Guid takeJobId, int version, ScriptedVideoAdapter adapter)
    {
        var response = await client.PostAsJsonAsync($"/api/shots/{shotId}/video-jobs/{takeJobId}/promote", new PromoteVideoTakeRequest(version, adapter.Describe().Id));
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JobSummary>())!;
    }

    private static async Task RunAsync(StudioApiFactory factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GenerationOrchestrator>().RunAsync(jobId, CancellationToken.None);
    }

    private static async Task<JobSummary> JobAsync(HttpClient client, Guid jobId)
        => (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(job => job.Id == jobId);

    private static async Task<ShotSummary> ShotAsync(HttpClient client, Guid shotId)
        => (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Shots.Single(shot => shot.Id == shotId);

    private static string H3WorkflowPath()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "workflows", "library.json"))) probe = probe.Parent;
        return Path.Combine(probe?.FullName ?? throw new InvalidOperationException("The repository workflows folder was not found."), "workflows", "h3-video-i2v.json");
    }

    private static WorkflowEntry H3Entry()
        => new WorkflowLibrary(Path.GetDirectoryName(H3WorkflowPath())!).LoadIndex().Single(entry => entry.Id == "h3-video-i2v");

    /// <summary>A node description in ComfyUI's shape. The VAE uses the newer
    /// COMBO form so both shapes are read.</summary>
    private static object NodeInfo(string node, string clipFile) => node switch
    {
        "UNETLoader" => new { input = new { required = new Dictionary<string, object> { ["unet_name"] = new object[] { new[] { "other.safetensors", "minimax_h3_fl2va_pruned_int8_convrot.safetensors" }, new { } } } } },
        "CLIPLoader" => new { input = new { required = new Dictionary<string, object> { ["clip_name"] = new object[] { new[] { clipFile }, new { } } } } },
        "VAELoader" => new { input = new { required = new Dictionary<string, object> { ["vae_name"] = new object[] { "COMBO", new { options = new[] { "minimax_h3_video_vae_fp16.safetensors" } } } } } },
        _ => new { input = new { required = new { } } }
    };

    private static ReadOnlySpan<byte> Message(string type, object data) => JsonSerializer.SerializeToUtf8Bytes(new { type, data });

    private static Task SendAsync(WebSocket socket, object message)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);

    private static GenerationExecutionContext VideoContext(string imagePath)
    {
        var asset = new AssetRecord { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = "first.png", MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('a', 64), StoragePath = "first.png", CreatedAt = DateTimeOffset.UtcNow };
        return new(Guid.NewGuid(), Guid.NewGuid(), "SH-TEST", GenerationRoute.FastDraft, GenerationPurpose.Video,
            "A slow drift.", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", asset, imagePath, [],
            VideoFramesPerSecond: 24, VideoFrameCount: 48);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }

    private sealed class ProbeFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) }; }

    private sealed class ScriptedProgressSource(Func<ComfyUiProgressSnapshot> next) : IComfyUiProgressSource
    {
        private readonly Func<ComfyUiProgressSnapshot> next = next;
        public string? ClientId { get; private set; }
        public string? Tracked { get; private set; }
        public IReadOnlyDictionary<string, ComfyUiNodeInfo>? Nodes { get; private set; }
        public bool Disposed { get; private set; }

        public IComfyUiProgressWatch Watch(Uri endpoint, string clientId, IReadOnlyDictionary<string, ComfyUiNodeInfo> nodes)
        {
            ClientId = clientId;
            Nodes = nodes;
            return new ScriptedWatch(this);
        }

        private sealed class ScriptedWatch(ScriptedProgressSource owner) : IComfyUiProgressWatch
        {
            public void Track(string promptId) => owner.Tracked = promptId;
            public ComfyUiProgressSnapshot Snapshot() => owner.next();
            public ValueTask DisposeAsync() { owner.Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class ScriptedVideoAdapter : IGenerationAdapter
    {
        private int maxRenders;
        public bool FailMax { get; set; }
        public int MaxRenders => Volatile.Read(ref maxRenders);

        public GenerationAdapterSummary Describe() => new(
            "scripted-video", "Scripted video", "Test video adapter", "Connected", "Returns a tiny video, or fails Max on request.", true,
            [GenerationRoute.FastDraft], [GenerationPurpose.Video]);

        public Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
        {
            if (context.VideoQuality == VideoQuality.Max)
            {
                Interlocked.Increment(ref maxRenders);
                if (FailMax) throw new GenerationDispatchException("The Max render stopped making progress.", true, "scripted-max");
            }
            var mp4 = new byte[48];
            "ftyp"u8.CopyTo(mp4.AsSpan(4));
            context.JobId.ToByteArray().CopyTo(mp4.AsSpan(24));
            return Task.FromResult(new GenerationAdapterOutput(new MemoryStream(mp4), $"{context.JobId:N}.mp4", "video/mp4", false, null, AssetKind.Video));
        }
    }

    /// <summary>A stand-in ComfyUI on a random loopback port.</summary>
    private sealed class FakeComfyUi : IAsyncDisposable
    {
        private WebApplication? app;
        public Uri Endpoint { get; private set; } = null!;
        public List<string> Requests { get; } = [];
        public List<string> SocketClients { get; } = [];

        public static async Task<FakeComfyUi> StartAsync(Action<WebApplication, FakeComfyUi> map)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var fake = new FakeComfyUi();
            var app = builder.Build();
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                lock (fake.Requests) fake.Requests.Add($"{context.Request.Method} {context.Request.Path}");
                await next();
            });
            map(app, fake);
            await app.StartAsync();
            fake.app = app;
            fake.Endpoint = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            if (app is null) return;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
