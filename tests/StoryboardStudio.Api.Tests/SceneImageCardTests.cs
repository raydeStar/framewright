using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// An image card is a stand-in that shows one library picture, for backdrops
/// and cutouts. It cites the picture by id and content hash, only ever shows a
/// picture its own project holds, and a working package carries it to the new
/// project's copy of that picture.
/// </summary>
public sealed class SceneImageCardTests
{
    private static readonly SceneCameraSummary Camera = new(0.9, 0.42, 6, [0, 0.5, 0], 38);
    private static readonly SceneEnvironmentSummary Lighting = new(2.2, 0.8, 0.9, 1.4);

    [Fact]
    public async Task AnImageCardShowsItsPictureThroughARestartAndAPackageThatRemapsIt()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-image-cards", Guid.NewGuid().ToString("N"));
        Guid sceneId;
        Guid imageId;
        string imageHash;
        var card = Guid.NewGuid();
        try
        {
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var image = await AssetUploads.ImageAsync(client, "harbour-backdrop.png");
                imageId = image.Id;
                imageHash = image.ContentHash;
                sceneId = await CreateSceneAsync(client, "Harbour");
                var saved = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(1, "Harbour", Camera, Lighting,
                    [Card(card, "Harbour backdrop", imageId)]));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

                // The library counts the card as a use of the picture.
                var usage = (await client.GetFromJsonAsync<AssetUsageSummary[]>("/api/assets/usage"))!;
                var used = Assert.Single(usage, x => x.AssetId == imageId);
                Assert.Equal(1, used.Scenes);
                Assert.Contains("Harbour", used.Where);
            }

            using (var restarted = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = restarted.CreateClient())
            {
                var scene = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{sceneId}"))!;
                var shown = Assert.Single(scene.Instances).Placeholder!;
                Assert.Equal("Card", shown.Shape);
                Assert.Equal(imageId, shown.ImageAssetId);
                Assert.Equal(imageHash, shown.ImageContentHash);
                Assert.Equal($"/api/assets/{imageId}/content", shown.ImageUrl);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(shown.ImageUrl)).StatusCode);

                // In a new project the card shows that project's own copy of the
                // picture, never the source project's.
                var package = await client.GetByteArrayAsync("/api/export/working-package");
                var content = new MultipartFormDataContent();
                var file = new ByteArrayContent(package);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Add(file, "file", "harbour.zip");
                var imported = await client.PostAsync("/api/projects/import", content);
                Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
                var summary = (await imported.Content.ReadFromJsonAsync<PortableProjectImportSummary>())!;
                (await client.PostAsync($"/api/projects/{summary.ProjectId}/activate", null)).EnsureSuccessStatusCode();
                var scenes = (await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes"))!;
                var copy = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{Assert.Single(scenes, x => x.Name == "Harbour").Id}"))!;
                var copied = Assert.Single(copy.Instances).Placeholder!;
                Assert.NotEqual(imageId, copied.ImageAssetId);
                Assert.Equal(imageHash, copied.ImageContentHash);
                var assets = (await client.GetFromJsonAsync<AssetSummary[]>("/api/assets"))!;
                Assert.Contains(assets, x => x.Id == copied.ImageAssetId && x.ProjectId == summary.ProjectId);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(copied.ImageUrl)).StatusCode);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task ACardShowsOnlyAPictureThisProjectHolds()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var image = await AssetUploads.ImageAsync(client, "own-picture.png");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "not-a-picture.glb");
        var sceneId = await CreateSceneAsync(client, "Card refusals");
        var card = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(1, "Card refusals", Camera, Lighting,
            [Card(card, "Backdrop", image.Id)]))).EnsureSuccessStatusCode();

        // A picture from another project is not this project's to show.
        var original = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Project.Id;
        var otherProject = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            "Other production", "Isolation", "OT-01", "Other sequence", 24, "2.39:1", 2304, 960));
        otherProject.EnsureSuccessStatusCode();
        var otherId = (await otherProject.Content.ReadFromJsonAsync<ProjectSummary>())!.Id;
        (await client.PostAsync($"/api/projects/{otherId}/activate", null)).EnsureSuccessStatusCode();
        var foreign = await AssetUploads.ImageAsync(client, "foreign-picture.png");
        (await client.PostAsync($"/api/projects/{original}/activate", null)).EnsureSuccessStatusCode();

        foreach (var invalid in new[]
        {
            new ScenePlaceholderSummary("Card", [4, 2, 0.01]),
            new ScenePlaceholderSummary("Card", [4, 2, 0.01], ImageAssetId: model.Id),
            new ScenePlaceholderSummary("Card", [4, 2, 0.01], ImageAssetId: foreign.Id),
            new ScenePlaceholderSummary("Card", [4, 2, 0.01], ImageAssetId: Guid.NewGuid()),
            new ScenePlaceholderSummary("Box", [1, 1, 1], ImageAssetId: image.Id),
        })
        {
            var refused = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(2, "Card refusals", Camera, Lighting,
                [new SaveSceneInstanceRequest(card, null, "Backdrop", [0, 1, 0], [0, 0, 0], [1, 1, 1], invalid)]));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var scene = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{sceneId}"))!;
        Assert.Equal(2, scene.Version);
        Assert.Equal(image.Id, Assert.Single(scene.Instances).Placeholder!.ImageAssetId);
    }

    private static SaveSceneInstanceRequest Card(Guid id, string name, Guid imageId) =>
        new(id, null, name, [0, 1, -2], [0, 0, 0], [1, 1, 1], new ScenePlaceholderSummary("Card", [4, 2, 0.01], ImageAssetId: imageId));

    private static async Task<Guid> CreateSceneAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/scenes", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SceneSummary>())!.Id;
    }
}
