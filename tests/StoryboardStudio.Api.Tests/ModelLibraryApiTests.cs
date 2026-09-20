using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A model is a reusable library record, not a one-off upload: it carries a
/// revision stack, editable organisation, provenance, and a non-destructive
/// archive, and all of that has to survive the application closing.
/// </summary>
public sealed class ModelLibraryApiTests
{
    [Fact]
    public async Task AModelSurvivesARestartWithItsExactRevisionMetadataAndMaterials()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-model-library", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid blockId, postId;
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                blockId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
                postId = await ImportAsync(client, ModelFixtures.AsymmetricPost(), "asymmetric-post.glb");

                // Organisation is editable; the immutable media facts are not.
                var renamed = await client.PutAsJsonAsync($"/api/assets/{blockId}", new
                {
                    displayName = "Chain court block", collectionId = (string?)null,
                    tags = new[] { "set-dressing", "chain-court" }, notes = "Blocking stand-in for the gate approach."
                });
                renamed.EnsureSuccessStatusCode();

                // The second model becomes revision 2 of the first.
                var revision = await client.PostAsJsonAsync($"/api/assets/{blockId}/revisions", new
                {
                    assetId = postId, prompt = "Taller variant for the upper gate.", engine = "Imported"
                });
                revision.EnsureSuccessStatusCode();
                using var added = await revision.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                Assert.Equal(2, added.RootElement.GetProperty("revisionNumber").GetInt32());
                Assert.True(added.RootElement.GetProperty("isCurrentRevision").GetBoolean());
                // Organisation travels with the stack rather than being retyped.
                Assert.Equal("Chain court block", added.RootElement.GetProperty("displayName").GetString());

                // The older revision is still current-free but fully intact.
                using var stack = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{blockId}/revisions") ?? throw new InvalidOperationException();
                Assert.Equal(2, stack.RootElement.GetArrayLength());
                var original = stack.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == blockId);
                Assert.False(original.GetProperty("isCurrentRevision").GetBoolean());
                Assert.Equal(1, original.GetProperty("revisionNumber").GetInt32());
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                using var stack = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{blockId}/revisions") ?? throw new InvalidOperationException();
                Assert.Equal(2, stack.RootElement.GetArrayLength());

                var current = stack.RootElement.EnumerateArray().Single(x => x.GetProperty("isCurrentRevision").GetBoolean());
                Assert.Equal(postId, current.GetProperty("id").GetGuid());
                Assert.Equal("Chain court block", current.GetProperty("displayName").GetString());
                Assert.Contains("chain-court", current.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
                Assert.Equal("Taller variant for the upper gate.", current.GetProperty("revisionPrompt").GetString());

                // The exact revision reopens with its own geometry and materials,
                // not with whichever model happened to be imported last.
                using var olderProfile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{blockId}/model-profile") ?? throw new InvalidOperationException();
                Assert.Equal([2d, 1d, 0.75d], olderProfile.RootElement.GetProperty("dimensions").EnumerateArray().Select(x => x.GetDouble()).ToArray());
                Assert.Equal("Block body", olderProfile.RootElement.GetProperty("materials")[0].GetProperty("name").GetString());

                using var currentProfile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{postId}/model-profile") ?? throw new InvalidOperationException();
                Assert.Equal([1d, 2d, 0.45d], currentProfile.RootElement.GetProperty("dimensions").EnumerateArray().Select(x => x.GetDouble()).ToArray());

                // Selecting the older revision again is an ordinary, reversible move.
                var promoted = await client.PostAsync($"/api/assets/{blockId}/make-current", null);
                promoted.EnsureSuccessStatusCode();
                using var afterPromotion = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{blockId}/revisions") ?? throw new InvalidOperationException();
                Assert.Equal(blockId, afterPromotion.RootElement.EnumerateArray().Single(x => x.GetProperty("isCurrentRevision").GetBoolean()).GetProperty("id").GetGuid());
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task ArchivingAModelHidesItFromTheLibraryWithoutBreakingAnythingThatCitesIt()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var assetId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");

        var archived = await client.PostAsync($"/api/assets/{assetId}/archive", null);
        archived.EnsureSuccessStatusCode();
        using var archivedAsset = await archived.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.True(archivedAsset.RootElement.GetProperty("isArchived").GetBoolean());

        // Archive is not deletion: the bytes, the profile, and the record all
        // still resolve, because something may still cite them.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{assetId}/content")).StatusCode);
        using var profile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{assetId}/model-profile") ?? throw new InvalidOperationException();
        Assert.Equal([2d, 1d, 0.75d], profile.RootElement.GetProperty("dimensions").EnumerateArray().Select(x => x.GetDouble()).ToArray());

        var restored = await client.PostAsync($"/api/assets/{assetId}/restore", null);
        restored.EnsureSuccessStatusCode();
        using var restoredAsset = await restored.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.False(restoredAsset.RootElement.GetProperty("isArchived").GetBoolean());
    }

    [Fact]
    public async Task IdenticalBytesAreStoredOnceButNeverSharedAcrossProjects()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-model-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false);
            using var client = factory.CreateClient();
            var firstId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
            using var firstAsset = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{firstId}/model-profile") ?? throw new InvalidOperationException();
            var hash = firstAsset.RootElement.GetProperty("contentHash").GetString()!;

            var project = await client.PostAsJsonAsync("/api/projects", new { name = "Second production", production = "Models", sequenceCode = "MDL-02", sequenceName = "Empty sequence", framesPerSecond = 24, aspectRatio = "2.40:1", deliveryWidth = 2304, deliveryHeight = 960 });
            project.EnsureSuccessStatusCode();
            using var projectJson = await project.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
            (await client.PostAsync($"/api/projects/{projectJson.RootElement.GetProperty("id").GetGuid()}/activate", null)).EnsureSuccessStatusCode();

            // The other project cannot reach the first project's model at all.
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assets/{firstId}/model-profile")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assets/{firstId}/revisions")).StatusCode);

            // Importing the same bytes there creates its own record rather than
            // borrowing one, while the content-addressed file is stored once.
            var secondId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
            Assert.NotEqual(firstId, secondId);
            using var secondAsset = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{secondId}/model-profile") ?? throw new InvalidOperationException();
            Assert.Equal(hash, secondAsset.RootElement.GetProperty("contentHash").GetString());

            var stored = Directory.GetFiles(dataRoot, $"{hash}.glb", SearchOption.AllDirectories);
            Assert.Single(stored);
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task ARevisionMustBeTheSameKindOfThingItRevises()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");

        using var assets = await client.GetFromJsonAsync<JsonDocument>("/api/assets") ?? throw new InvalidOperationException();
        var image = assets.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == "Image");
        if (image.ValueKind != JsonValueKind.Object) return;

        var mixed = await client.PostAsJsonAsync($"/api/assets/{modelId}/revisions", new
        {
            assetId = image.GetProperty("id").GetGuid(), prompt = "A picture pretending to be a model.", engine = "Imported"
        });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);

        // The model's stack is untouched by the refusal.
        using var stack = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{modelId}/revisions") ?? throw new InvalidOperationException();
        Assert.Equal(1, stack.RootElement.GetArrayLength());
    }

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
