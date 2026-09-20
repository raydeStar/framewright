using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A scene is editable working state: distinct instances pinned to exact model
/// revisions, a camera and lighting that reopen as they were left, and a save
/// that refuses to overwrite newer work.
/// </summary>
public sealed class SceneApiTests
{
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
            tags = new[] { "renamed" }, notes = "Renaming must not move anything.",
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
            camera = new { yaw = 1.25, pitch = 0.5, distance = 9.5, target = new[] { 0d, 0.75, 0d }, fieldOfView = 40d },
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
}
