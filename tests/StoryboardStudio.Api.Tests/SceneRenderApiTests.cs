using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The animated scene route is a file-backed exact-frame job, not a viewport
/// recording. These tests close the application mid-capture, resume the same
/// job, and prove the resulting Max take enters ordinary shot review.
/// </summary>
public sealed class SceneRenderApiTests
{
    [Fact]
    public async Task ExactFramesResumeAcrossARestartAndPromoteOneReviewableTake()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-scene-render", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var encoder = new TestSceneVideoEncoder();
        try
        {
            RenderFixture fixture;
            SceneRenderSummary prepared;
            using (var factory = Factory(dataRoot, encoder))
            using (var client = factory.CreateClient())
            {
                fixture = await CreateFixtureAsync(client);

                // Motion cannot skip the artist's accepted-still gate.
                var blocked = await client.PostAsJsonAsync($"/api/scenes/{fixture.SceneId}/shot-renders", new PrepareSceneRenderRequest(fixture.Binding.Id));
                Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
                var ratified = await client.PostAsJsonAsync($"/api/shots/{fixture.ShotId}/ratify", new RatifyRequest(fixture.Binding.ShotVersion, "The scene still is the approved source."));
                ratified.EnsureSuccessStatusCode();

                var prepare = await client.PostAsJsonAsync($"/api/scenes/{fixture.SceneId}/shot-renders", new PrepareSceneRenderRequest(fixture.Binding.Id));
                Assert.True(prepare.IsSuccessStatusCode, await prepare.Content.ReadAsStringAsync());
                prepared = await prepare.Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
                Assert.Equal(JobState.Running, prepared.State);
                Assert.Equal(4, prepared.FrameCount);
                Assert.Equal([0, 1, 2, 3], prepared.MissingFrames);
                Assert.NotEqual(prepared.StartCamera.Yaw, prepared.EndCamera.Yaw);
                Assert.Equal(64, prepared.ManifestHash.Length);

                var first = await UploadFrameAsync(client, prepared.JobId, 0, PngHeader(prepared.Width, prepared.Height, 1));
                first.EnsureSuccessStatusCode();
            }

            // A fresh application process discovers the frame already on disk,
            // and preparing the same accepted binding returns the same job.
            using (var reopened = Factory(dataRoot, encoder))
            using (var client = reopened.CreateClient())
            {
                var resumed = await client.GetFromJsonAsync<SceneRenderSummary>($"/api/scene-renders/{prepared.JobId}")
                    ?? throw new InvalidOperationException();
                Assert.Equal([1, 2, 3], resumed.MissingFrames);
                var samePrepare = await (await client.PostAsJsonAsync(
                    $"/api/scenes/{fixture.SceneId}/shot-renders", new PrepareSceneRenderRequest(fixture.Binding.Id)))
                    .Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
                Assert.Equal(prepared.JobId, samePrepare.JobId);

                using var wrongSize = await UploadFrameAsync(client, prepared.JobId, 1, PngHeader(prepared.Width - 1, prepared.Height, 2));
                Assert.Equal(HttpStatusCode.BadRequest, wrongSize.StatusCode);
                var frameOne = PngHeader(prepared.Width, prepared.Height, 2);
                using var accepted = await UploadFrameAsync(client, prepared.JobId, 1, frameOne);
                accepted.EnsureSuccessStatusCode();
                using var duplicate = await UploadFrameAsync(client, prepared.JobId, 1, frameOne);
                duplicate.EnsureSuccessStatusCode();
                var duplicateReceipt = await duplicate.Content.ReadFromJsonAsync<SceneRenderFrameReceipt>() ?? throw new InvalidOperationException();
                Assert.True(duplicateReceipt.AlreadyPresent);
                using var changed = await UploadFrameAsync(client, prepared.JobId, 1, PngHeader(prepared.Width, prepared.Height, 9));
                Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

                using var frameTwo = await UploadFrameAsync(client, prepared.JobId, 2, PngHeader(prepared.Width, prepared.Height, 3));
                frameTwo.EnsureSuccessStatusCode();
                using var frameThree = await UploadFrameAsync(client, prepared.JobId, 3, PngHeader(prepared.Width, prepared.Height, 4));
                frameThree.EnsureSuccessStatusCode();

                var completeResponse = await client.PostAsync($"/api/scene-renders/{prepared.JobId}/complete", null);
                completeResponse.EnsureSuccessStatusCode();
                var completed = await completeResponse.Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
                Assert.Equal(JobState.Completed, completed.State);
                Assert.True(completed.Promoted);
                Assert.NotNull(completed.OutputAssetId);
                Assert.Empty(completed.MissingFrames);
                Assert.Equal(1, encoder.EncodeCount);

                var studio = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio") ?? throw new InvalidOperationException();
                var shot = studio.Shots.Single(item => item.Id == fixture.ShotId);
                Assert.Equal(ShotStage.Video, shot.Stage);
                Assert.Equal(ApprovalState.Working, shot.Approval);
                Assert.Equal(completed.JobId, shot.ProductionVideoJobId);
                Assert.Equal(completed.OutputAssetId, shot.ProductionVideoAssetId);
                Assert.Equal(fixture.Binding.StillAssetId, shot.CurrentAssetId);

                var approveVideo = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/ratify", new RatifyRequest(shot.Version, "The decoded exact-frame take is approved."));
                approveVideo.EnsureSuccessStatusCode();
                var approvedVideo = await approveVideo.Content.ReadFromJsonAsync<ShotSummary>() ?? throw new InvalidOperationException();
                Assert.Equal(ApprovalState.Ratified, approvedVideo.Approval);

                // Completion is idempotent and cannot manufacture another asset.
                var again = await (await client.PostAsync($"/api/scene-renders/{prepared.JobId}/complete", null))
                    .Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
                Assert.Equal(completed.OutputAssetId, again.OutputAssetId);
                Assert.Equal(1, encoder.EncodeCount);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task FailedEncodingKeepsFramesAndRetryLineage()
    {
        var encoder = new TestSceneVideoEncoder { FailNext = true };
        using var factory = new StudioApiFactory(services =>
        {
            services.RemoveAll<ISceneVideoEncoder>();
            services.AddSingleton<ISceneVideoEncoder>(encoder);
        });
        using var client = factory.CreateClient();
        var fixture = await CreateFixtureAsync(client);
        (await client.PostAsJsonAsync($"/api/shots/{fixture.ShotId}/ratify", new RatifyRequest(fixture.Binding.ShotVersion, "Approved source."))).EnsureSuccessStatusCode();
        var prepare = await client.PostAsJsonAsync(
            $"/api/scenes/{fixture.SceneId}/shot-renders", new PrepareSceneRenderRequest(fixture.Binding.Id));
        Assert.True(prepare.IsSuccessStatusCode, await prepare.Content.ReadAsStringAsync());
        var prepared = await prepare.Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
        foreach (var index in prepared.MissingFrames)
            (await UploadFrameAsync(client, prepared.JobId, index, PngHeader(prepared.Width, prepared.Height, (byte)(index + 1)))).EnsureSuccessStatusCode();

        var failed = await client.PostAsync($"/api/scene-renders/{prepared.JobId}/complete", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var failedStatus = await client.GetFromJsonAsync<SceneRenderSummary>($"/api/scene-renders/{prepared.JobId}") ?? throw new InvalidOperationException();
        Assert.Equal(JobState.Failed, failedStatus.State);
        Assert.Empty(failedStatus.MissingFrames);

        var retryResponse = await client.PostAsync($"/api/jobs/{prepared.JobId}/retry", null);
        retryResponse.EnsureSuccessStatusCode();
        var retryJob = await retryResponse.Content.ReadFromJsonAsync<JobSummary>() ?? throw new InvalidOperationException();
        Assert.Equal(SceneRenderService.WorkType, retryJob.WorkType);
        Assert.Equal(2, retryJob.Attempt);
        Assert.Equal(prepared.JobId, retryJob.RetryOfJobId);
        var retried = await client.GetFromJsonAsync<SceneRenderSummary>($"/api/scene-renders/{retryJob.Id}") ?? throw new InvalidOperationException();
        Assert.Empty(retried.MissingFrames);

        var completed = await client.PostAsync($"/api/scene-renders/{retryJob.Id}/complete", null);
        completed.EnsureSuccessStatusCode();
        var summary = await completed.Content.ReadFromJsonAsync<SceneRenderSummary>() ?? throw new InvalidOperationException();
        Assert.Equal(JobState.Completed, summary.State);
        Assert.True(summary.Promoted);
        Assert.Equal(2, encoder.EncodeCount);
    }

    private static StudioApiFactory Factory(string dataRoot, TestSceneVideoEncoder encoder) => new(
        dataRoot,
        deleteDataRoot: false,
        startGenerationWorker: false,
        configureServices: services =>
        {
            services.RemoveAll<ISceneVideoEncoder>();
            services.AddSingleton<ISceneVideoEncoder>(encoder);
        });

    private static async Task<RenderFixture> CreateFixtureAsync(HttpClient client)
    {
        var studio = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio") ?? throw new InvalidOperationException();
        var shotResponse = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SC-970", "Exact scene take", "A bounded rig and prop move through an exact-frame render.",
            4, "Bound scene camera", "Raise the arm and open the prop.", [], []));
        shotResponse.EnsureSuccessStatusCode();
        var shot = await shotResponse.Content.ReadFromJsonAsync<ShotSummary>() ?? throw new InvalidOperationException();
        var propId = await ImportModelAsync(client, ModelFixtures.AsymmetricBlock(), "render-prop.glb");
        var sceneResponse = await client.PostAsJsonAsync("/api/scenes", new { name = "Render fixture" });
        sceneResponse.EnsureSuccessStatusCode();
        var scene = await sceneResponse.Content.ReadFromJsonAsync<SceneSummary>() ?? throw new InvalidOperationException();
        var save = await client.PutAsJsonAsync($"/api/scenes/{scene.Id}", new
        {
            expectedVersion = scene.Version,
            name = scene.Name,
            camera = new { yaw = 0.1, pitch = 0.35, distance = 8d, target = (double[])[0d, 0.8d, 0d], fieldOfView = 42d },
            environment = new { keyIntensity = 2.2, keyYaw = 0.8, keyPitch = 0.9, ambientIntensity = 1.4 },
            instances = new[]
            {
                new
                {
                    id = Guid.NewGuid(), assetId = propId, name = "Door",
                    position = (double[])[0d, 0d, 0d], rotation = (double[])[0d, 0d, 0d], scale = (double[])[1d, 1d, 1d],
                    motion = new { pivot = (double[])[-1d, 0d, 0d], axis = "Y", fromRadians = 0d, toRadians = 1.2d, seconds = 1d, pingPong = false },
                },
            },
        });
        save.EnsureSuccessStatusCode();
        var saved = await save.Content.ReadFromJsonAsync<SceneSummary>() ?? throw new InvalidOperationException();
        var camera = new SceneCameraSummary(1.1, 0.45, 6.5, [0.5, 1, 0], 35);
        var still = await RenderStillAsync(
            client, scene.Id, shot.Id, saved.Version, shot.Version, camera,
            0, shot.DurationFrames / (double)studio.Project.FramesPerSecond, 0,
            PngHeader(studio.Project.DeliveryWidth, studio.Project.DeliveryHeight));
        still.EnsureSuccessStatusCode();
        var binding = await still.Content.ReadFromJsonAsync<SceneShotBindingSummary>() ?? throw new InvalidOperationException();
        return new(scene.Id, shot.Id, binding);
    }

    private static async Task<Guid> ImportModelAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var asset = await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
        return asset.Id;
    }

    private static async Task<HttpResponseMessage> RenderStillAsync(
        HttpClient client, Guid sceneId, Guid shotId, int expectedSceneVersion, int expectedShotVersion,
        SceneCameraSummary camera, double startTime, double endTime, double stillTime, byte[] png)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "scene-still.png");
        content.Add(new StringContent(shotId.ToString()), "shotId");
        content.Add(new StringContent(expectedSceneVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedSceneVersion");
        content.Add(new StringContent(expectedShotVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)), "expectedShotVersion");
        content.Add(new StringContent(startTime.ToString(System.Globalization.CultureInfo.InvariantCulture)), "startTime");
        content.Add(new StringContent(endTime.ToString(System.Globalization.CultureInfo.InvariantCulture)), "endTime");
        content.Add(new StringContent(stillTime.ToString(System.Globalization.CultureInfo.InvariantCulture)), "stillTime");
        content.Add(new StringContent(System.Text.Json.JsonSerializer.Serialize(camera)), "camera");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenes/{sceneId}/shot-stills") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> UploadFrameAsync(HttpClient client, Guid jobId, int index, byte[] png)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", $"frame-{index:000000}.png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scene-renders/{jobId}/frames/{index}") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static byte[] PngHeader(int width, int height, byte marker = 0)
    {
        var bytes = new byte[25];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = marker;
        return bytes;
    }

    private sealed record RenderFixture(Guid SceneId, Guid ShotId, SceneShotBindingSummary Binding);

    private sealed class TestSceneVideoEncoder : ISceneVideoEncoder
    {
        public bool FailNext { get; set; }
        public int EncodeCount { get; private set; }

        public Task<SceneVideoEncoderReadiness> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SceneVideoEncoderReadiness(true, "test-ffmpeg 1", "Test encoder ready."));
        }

        public async Task<SceneVideoEncodeResult> EncodeAsync(SceneVideoEncodeRequest request, CancellationToken cancellationToken)
        {
            EncodeCount++;
            Assert.Equal(request.FrameCount, Directory.GetFiles(request.FramesDirectory, "frame-*.png").Length);
            Assert.Equal("test-ffmpeg 1", request.ExpectedVersion);
            if (FailNext)
            {
                FailNext = false;
                return new(false, "test-ffmpeg 1", "Deliberate encoder refusal.");
            }
            var bytes = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), 16);
            Encoding.ASCII.GetBytes("ftypisom").CopyTo(bytes, 4);
            await File.WriteAllBytesAsync(request.OutputPath, bytes, cancellationToken);
            return new(true, "test-ffmpeg 1", "Test movie encoded.");
        }
    }
}
