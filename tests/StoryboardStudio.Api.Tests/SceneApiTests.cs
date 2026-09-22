using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A scene is editable working state: distinct instances pinned to exact model
/// revisions, a camera and lighting that reopen as they were left, and a save
/// that refuses to overwrite newer work.
/// </summary>
public sealed class SceneApiTests
{
    [Fact]
    public async Task ASceneStillFreezesItsInputsAndReopensInTheShotReviewPath()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-scene-shot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid sceneId;
            Guid shotId;
            Guid bindingId;
            Guid stillAssetId;
            string snapshotHash;
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var studio = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                    ?? throw new InvalidOperationException();
                var shotResponse = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
                    "SC-900", "Scene still", "A frozen scene becomes an ordinary review frame.",
                    48, "Unbound", "Hold for review.", [], []));
                shotResponse.EnsureSuccessStatusCode();
                var shot = await shotResponse.Content.ReadFromJsonAsync<ShotSummary>()
                    ?? throw new InvalidOperationException();
                shotId = shot.Id;

                var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "scene-still-block.glb");
                sceneId = await CreateSceneAsync(client, "Still source");
                var savedResponse = await SaveAsync(client, sceneId, 1, "Still source", [
                    Instance(Guid.NewGuid(), modelId, "Hero block", [1, 0, -2], [0, 0.25, 0], [1, 1, 1]),
                ]);
                savedResponse.EnsureSuccessStatusCode();

                var camera = new SceneCameraSummary(0.75, 0.35, 7.5, [0.25, 0.8, -0.5], 36);
                using var rendered = await RenderStillAsync(
                    client, sceneId, shotId, expectedSceneVersion: 2, expectedShotVersion: 1,
                    camera, startTime: 0, endTime: 2, stillTime: 1,
                    PngHeader(studio.Project.DeliveryWidth, studio.Project.DeliveryHeight));
                rendered.EnsureSuccessStatusCode();
                var binding = await rendered.Content.ReadFromJsonAsync<SceneShotBindingSummary>()
                    ?? throw new InvalidOperationException();
                bindingId = binding.Id;
                stillAssetId = binding.StillAssetId;
                snapshotHash = binding.SnapshotHash;
                Assert.Equal(2, binding.SceneVersion);
                Assert.Equal(2, binding.ShotVersion);
                Assert.Equal(camera.Yaw, binding.Camera.Yaw);
                Assert.Equal(camera.Pitch, binding.Camera.Pitch);
                Assert.Equal(camera.Distance, binding.Camera.Distance);
                Assert.Equal(camera.Target, binding.Camera.Target);
                Assert.Equal(camera.FieldOfView, binding.Camera.FieldOfView);
                Assert.Equal(studio.Project.DeliveryWidth, binding.DeliveryWidth);
                Assert.Equal(studio.Project.DeliveryHeight, binding.DeliveryHeight);
                Assert.Equal(64, binding.SnapshotHash.Length);

                var afterRender = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                    ?? throw new InvalidOperationException();
                var reviewedShot = afterRender.Shots.Single(x => x.Id == shotId);
                Assert.Equal(2, reviewedShot.Version);
                Assert.Equal(ShotStage.Draft, reviewedShot.Stage);
                Assert.Equal(ApprovalState.Working, reviewedShot.Approval);
                Assert.Equal(stillAssetId, reviewedShot.CurrentAssetId);
                Assert.Contains("Still source", reviewedShot.Camera, StringComparison.Ordinal);
                var candidates = await client.GetFromJsonAsync<CandidateVersionSummary[]>($"/api/shots/{shotId}/candidates")
                    ?? throw new InvalidOperationException();
                Assert.Equal(2, candidates.Length);
                Assert.Equal(stillAssetId, candidates.Single(x => x.IsCurrent).AssetId);

                // Later scene work does not rewrite what this review frame used.
                var newerScene = await SaveAsync(client, sceneId, 2, "Still source revised", [
                    Instance(Guid.NewGuid(), modelId, "Moved block", [9, 0, 4], [0, 1, 0], [2, 2, 2]),
                ]);
                newerScene.EnsureSuccessStatusCode();
                var listResponse = await client.GetAsync($"/api/scenes/{sceneId}/shot-stills");
                Assert.True(listResponse.IsSuccessStatusCode, await listResponse.Content.ReadAsStringAsync());
                var listed = await listResponse.Content.ReadFromJsonAsync<SceneShotBindingSummary[]>()
                    ?? throw new InvalidOperationException();
                var frozen = Assert.Single(listed);
                Assert.Equal(bindingId, frozen.Id);
                Assert.Equal("Still source", frozen.SceneName);
                Assert.Equal(2, frozen.SceneVersion);
                Assert.Equal(snapshotHash, frozen.SnapshotHash);

                using var stale = await RenderStillAsync(
                    client, sceneId, shotId, expectedSceneVersion: 2, expectedShotVersion: 2,
                    camera, startTime: 0, endTime: 2, stillTime: 1,
                    PngHeader(studio.Project.DeliveryWidth, studio.Project.DeliveryHeight, 0x5a));
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                Assert.Single(await client.GetFromJsonAsync<SceneShotBindingSummary[]>($"/api/scenes/{sceneId}/shot-stills")
                    ?? throw new InvalidOperationException());

                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
                var stored = await db.SceneShotBindings.AsNoTracking().SingleAsync(x => x.Id == bindingId);
                using var snapshot = JsonDocument.Parse(stored.SnapshotJson);
                var storedScene = snapshot.RootElement.GetProperty("scene");
                Assert.Equal("Still source", storedScene.GetProperty("name").GetString());
                Assert.Equal(1, storedScene.GetProperty("instances")[0].GetProperty("position")[0].GetDouble());

                var exportResponse = await client.GetAsync("/api/export/working-package");
                exportResponse.EnsureSuccessStatusCode();
                using var package = new ZipArchive(
                    new MemoryStream(await exportResponse.Content.ReadAsByteArrayAsync()),
                    ZipArchiveMode.Read);
                using var manifest = JsonDocument.Parse(package.GetEntry("production-manifest.json")!.Open());
                Assert.Equal(5, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Contains(manifest.RootElement.GetProperty("scenes").EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == sceneId);
                Assert.Contains(manifest.RootElement.GetProperty("sceneInstances").EnumerateArray(),
                    item => item.GetProperty("sceneId").GetGuid() == sceneId);
                var exportedBinding = Assert.Single(
                    manifest.RootElement.GetProperty("sceneShotBindings").EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == bindingId);
                Assert.Equal(snapshotHash, exportedBinding.GetProperty("snapshotHash").GetString());
                Assert.Contains(manifest.RootElement.GetProperty("assets").EnumerateArray(),
                    item => item.GetProperty("id").GetGuid() == stillAssetId);
            }

            // Both the immutable binding and ordinary review image survive a full app restart.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                var bindings = await client.GetFromJsonAsync<SceneShotBindingSummary[]>($"/api/scenes/{sceneId}/shot-stills")
                    ?? throw new InvalidOperationException();
                var binding = Assert.Single(bindings);
                Assert.Equal(bindingId, binding.Id);
                Assert.Equal(snapshotHash, binding.SnapshotHash);
                Assert.Equal(stillAssetId, binding.StillAssetId);
                var content = await client.GetAsync(binding.StillAssetUrl);
                Assert.Equal(HttpStatusCode.OK, content.StatusCode);
                var studio = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                    ?? throw new InvalidOperationException();
                Assert.Equal(stillAssetId, studio.Shots.Single(x => x.Id == shotId).CurrentAssetId);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task OverlappingSavesCannotOverwriteTheWinningScene()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var id = await CreateSceneAsync(client, "Concurrent scene");
        using var staleScope = factory.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<StudioDbContext>();
        // Keep the same tracked version that an overlapping request already read.
        await staleDb.Scenes.SingleAsync(scene => scene.Id == id);
        using var winning = await SaveAsync(client, id, 1, "Winning scene", []);
        winning.EnsureSuccessStatusCode();
        var result = await staleScope.ServiceProvider.GetRequiredService<SceneService>().SaveAsync(id,
            new SaveSceneRequest(1, "Losing scene",
                new SceneCameraSummary(0.9, 0.42, 6, [0, 0.5, 0], 38),
                new SceneEnvironmentSummary(2.2, 0.8, 0.9, 1.4), []), CancellationToken.None);
        Assert.Equal(RepositoryResultKind.Conflict, result.Kind);
        using var stored = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{id}") ?? throw new InvalidOperationException();
        Assert.Equal("Winning scene", stored.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, stored.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task TwoInstancesOfOneModelMoveIndependentlyAndSurviveARestart()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-scene", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid sceneId, leftId = Guid.NewGuid(), rightId = Guid.NewGuid();
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
                sceneId = await CreateSceneAsync(client, "Chain court");

                // The same model revision, placed twice, moved separately.
                var saved = await SaveAsync(client, sceneId, 1, "Chain court", [
                    Instance(leftId, modelId, "Left block", [-1.5, 0, 0], [0, 0, 0], [1, 1, 1]),
                    Instance(rightId, modelId, "Right block", [2.5, 0, 1], [0, 0.7853981634, 0], [2, 2, 2]),
                ]);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                using var scene = await saved.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                Assert.Equal(2, scene.RootElement.GetProperty("version").GetInt32());
                Assert.Equal(2, scene.RootElement.GetProperty("instances").GetArrayLength());
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
                var instances = scene.RootElement.GetProperty("instances");
                var left = instances.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == leftId);
                var right = instances.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == rightId);

                // Two objects, one model revision, different transforms.
                Assert.Equal(right.GetProperty("assetId").GetGuid(), left.GetProperty("assetId").GetGuid());
                Assert.Equal([-1.5, 0d, 0d], Numbers(left, "position"));
                Assert.Equal([2.5, 0d, 1d], Numbers(right, "position"));
                Assert.Equal([1d, 1d, 1d], Numbers(left, "scale"));
                Assert.Equal([2d, 2d, 2d], Numbers(right, "scale"));
                Assert.True(right.GetProperty("rotation")[1].GetDouble() > 0.78);
                Assert.True(left.GetProperty("available").GetBoolean());
                Assert.Equal([2d, 1d, 0.75d], Numbers(left, "dimensions"));

                // The camera and lighting reopen exactly as they were saved.
                var camera = scene.RootElement.GetProperty("camera");
                Assert.Equal(1.25, camera.GetProperty("yaw").GetDouble(), 6);
                Assert.Equal(9.5, camera.GetProperty("distance").GetDouble(), 6);
                Assert.Equal([0d, 0.75d, 0d], Numbers(camera, "target"));
                Assert.Equal(3.4, scene.RootElement.GetProperty("environment").GetProperty("keyIntensity").GetDouble(), 6);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task AStaleSaveIsRefusedWithoutErasingNewerWork()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "Conflict proof");
        var keeper = Guid.NewGuid();

        // One artist saves.
        var first = await SaveAsync(client, sceneId, 1, "Conflict proof", [
            Instance(keeper, modelId, "Newer work", [4, 0, 0], [0, 0, 0], [1, 1, 1]),
        ]);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A second save built on the version before that one is refused.
        var stale = await SaveAsync(client, sceneId, 1, "Stale overwrite", [
            Instance(Guid.NewGuid(), modelId, "Stale block", [0, 0, 0], [0, 0, 0], [1, 1, 1]),
        ]);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The newer work is still exactly as it was.
        using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        Assert.Equal("Conflict proof", scene.RootElement.GetProperty("name").GetString());
        var instance = Assert.Single(scene.RootElement.GetProperty("instances").EnumerateArray().ToArray());
        Assert.Equal(keeper, instance.GetProperty("id").GetGuid());
        Assert.Equal([4d, 0d, 0d], Numbers(instance, "position"));
    }

    [Fact]
    public async Task RenamingTheLibraryModelDoesNotMoveAnythingAndRemovingAnInstanceKeepsTheAsset()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "Library independence");
        var keptId = Guid.NewGuid();
        var removedId = Guid.NewGuid();

        await SaveAsync(client, sceneId, 1, "Library independence", [
            Instance(keptId, modelId, "Kept", [1, 2, 3], [0, 0, 0], [1, 1, 1]),
            Instance(removedId, modelId, "Removed", [-1, 0, 0], [0, 0, 0], [1, 1, 1]),
        ]);

        // Editing library organisation is not a scene edit.
        var renamed = await client.PutAsJsonAsync($"/api/assets/{modelId}", new
        {
            displayName = "Renamed in the library", collectionId = (string?)null,
            tags = (string[])["renamed"], notes = "Renaming must not move anything.",
        });
        renamed.EnsureSuccessStatusCode();

        using var afterRename = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var kept = afterRename.RootElement.GetProperty("instances").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == keptId);
        Assert.Equal([1d, 2d, 3d], Numbers(kept, "position"));
        Assert.Equal("Kept", kept.GetProperty("name").GetString());
        Assert.Equal("Renamed in the library", kept.GetProperty("assetName").GetString());

        // Removing an instance removes the placement, not the library asset.
        var removal = await SaveAsync(client, sceneId, afterRename.RootElement.GetProperty("version").GetInt32(), "Library independence", [
            Instance(keptId, modelId, "Kept", [1, 2, 3], [0, 0, 0], [1, 1, 1]),
        ]);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        using var afterRemoval = await removal.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Single(afterRemoval.RootElement.GetProperty("instances").EnumerateArray().ToArray());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{modelId}/model-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{modelId}/content")).StatusCode);
    }

    [Fact]
    public async Task AnInstanceWhoseModelIsUnavailableKeepsItsIdentityInsteadOfDisappearing()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-scene-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false);
            using var client = factory.CreateClient();
            var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
            var sceneId = await CreateSceneAsync(client, "Missing model");
            var instanceId = Guid.NewGuid();
            await SaveAsync(client, sceneId, 1, "Missing model", [
                Instance(instanceId, modelId, "Placeholder candidate", [0, 0, 0], [0, 0, 0], [1, 1, 1]),
            ]);

            // The stored file goes away underneath the scene.
            foreach (var file in Directory.GetFiles(dataRoot, "*.glb", SearchOption.AllDirectories)) File.Delete(file);

            using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
            var instance = Assert.Single(scene.RootElement.GetProperty("instances").EnumerateArray().ToArray());
            Assert.Equal(instanceId, instance.GetProperty("id").GetGuid());
            Assert.Equal(modelId, instance.GetProperty("assetId").GetGuid());
            Assert.False(instance.GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, instance.GetProperty("contentUrl").ValueKind);
            Assert.Equal("Placeholder candidate", instance.GetProperty("name").GetString());
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task ASceneRefusesImpossibleTransformsAndForeignAssets()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "Validation proof");

        var zeroScale = await SaveAsync(client, sceneId, 1, "Validation proof", [
            Instance(Guid.NewGuid(), modelId, "Collapsed", [0, 0, 0], [0, 0, 0], [0, 1, 1]),
        ]);
        Assert.Equal(HttpStatusCode.BadRequest, zeroScale.StatusCode);

        var faraway = await SaveAsync(client, sceneId, 1, "Validation proof", [
            Instance(Guid.NewGuid(), modelId, "Lost", [50_000, 0, 0], [0, 0, 0], [1, 1, 1]),
        ]);
        Assert.Equal(HttpStatusCode.BadRequest, faraway.StatusCode);

        // An image is not a model, and cannot be placed as one.
        using var assets = await client.GetFromJsonAsync<JsonDocument>("/api/assets") ?? throw new InvalidOperationException();
        var image = assets.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == "Image");
        if (image.ValueKind == JsonValueKind.Object)
        {
            var wrongKind = await SaveAsync(client, sceneId, 1, "Validation proof", [
                Instance(Guid.NewGuid(), image.GetProperty("id").GetGuid(), "A picture", [0, 0, 0], [0, 0, 0], [1, 1, 1]),
            ]);
            Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        }

        // Nothing was written by any of the refusals.
        using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        Assert.Equal(1, scene.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(scene.RootElement.GetProperty("instances").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task AnotherProjectCannotSeeOrSaveThisProjectsScene()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "Private scene");
        await SaveAsync(client, sceneId, 1, "Private scene", [
            Instance(Guid.NewGuid(), modelId, "Block", [0, 0, 0], [0, 0, 0], [1, 1, 1]),
        ]);

        var project = await client.PostAsJsonAsync("/api/projects", new { name = "Other production", production = "Scenes", sequenceCode = "SCN-02", sequenceName = "Empty sequence", framesPerSecond = 24, aspectRatio = "2.40:1", deliveryWidth = 2304, deliveryHeight = 960 });
        project.EnsureSuccessStatusCode();
        using var projectJson = await project.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        (await client.PostAsync($"/api/projects/{projectJson.RootElement.GetProperty("id").GetGuid()}/activate", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/scenes/{sceneId}")).StatusCode);
        var save = await SaveAsync(client, sceneId, 2, "Hijacked", []);
        Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
        using var scenes = await client.GetFromJsonAsync<JsonDocument>("/api/scenes") ?? throw new InvalidOperationException();
        Assert.Empty(scenes.RootElement.EnumerateArray().ToArray());
    }

    private static double[] Numbers(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(x => x.GetDouble())];

    private static object Instance(Guid id, Guid assetId, string name, double[] position, double[] rotation, double[] scale) =>
        new { id, assetId, name, position, rotation, scale };

    private static async Task<Guid> CreateSceneAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/scenes", new { name });
        response.EnsureSuccessStatusCode();
        using var scene = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return scene.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid sceneId, int expectedVersion, string name, object[] instances) =>
        client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion, name,
            camera = new { yaw = 1.25, pitch = 0.5, distance = 9.5, target = (double[])[0d, 0.75, 0d], fieldOfView = 40d },
            environment = new { keyIntensity = 3.4, keyYaw = 0.6, keyPitch = 1.0, ambientIntensity = 1.1 },
            instances,
        });

    private static async Task<Guid> ImportAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var asset = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return asset.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> RenderStillAsync(
        HttpClient client,
        Guid sceneId,
        Guid shotId,
        int expectedSceneVersion,
        int expectedShotVersion,
        SceneCameraSummary camera,
        double startTime,
        double endTime,
        double stillTime,
        byte[] png)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "scene-still.png");
        content.Add(new StringContent(shotId.ToString()), "shotId");
        content.Add(new StringContent(expectedSceneVersion.ToString(CultureInfo.InvariantCulture)), "expectedSceneVersion");
        content.Add(new StringContent(expectedShotVersion.ToString(CultureInfo.InvariantCulture)), "expectedShotVersion");
        content.Add(new StringContent(startTime.ToString(CultureInfo.InvariantCulture)), "startTime");
        content.Add(new StringContent(endTime.ToString(CultureInfo.InvariantCulture)), "endTime");
        content.Add(new StringContent(stillTime.ToString(CultureInfo.InvariantCulture)), "stillTime");
        content.Add(new StringContent(JsonSerializer.Serialize(camera)), "camera");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenes/{sceneId}/shot-stills") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static byte[] PngHeader(int width, int height, byte marker = 0)
    {
        // AssetStore deliberately performs a bounded structural/header check;
        // the browser journey below proves real canvas PNG encoding.
        var bytes = new byte[25];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = marker;
        return bytes;
    }
}
