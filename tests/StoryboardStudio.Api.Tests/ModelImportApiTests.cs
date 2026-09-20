using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The model import path through the real service and the real asset root: what
/// gets stored, what the artist reads back, and what a refusal leaves behind.
/// </summary>
public sealed class ModelImportApiTests
{
    [Fact]
    public async Task ASupportedGlbImportsOnceAndReportsItsRealProfile()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var imported = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        using var asset = await imported.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var assetId = asset.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Model", asset.RootElement.GetProperty("kind").GetString());

        using var profile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{assetId}/model-profile") ?? throw new InvalidOperationException();
        var data = profile.RootElement;
        Assert.Equal("2.0", data.GetProperty("specificationVersion").GetString());
        Assert.Equal(16, data.GetProperty("vertexCount").GetInt32());
        Assert.Equal(24, data.GetProperty("triangleCount").GetInt32());
        Assert.Equal(2, data.GetProperty("materials").GetArrayLength());
        Assert.Equal("Block body", data.GetProperty("materials")[0].GetProperty("name").GetString());
        Assert.Equal([2d, 1d, 0.75d], data.GetProperty("dimensions").EnumerateArray().Select(x => x.GetDouble()).ToArray());
        Assert.Equal($"/api/assets/{assetId}/content", data.GetProperty("contentUrl").GetString());

        // The supported subset and its ceilings are reported, not implied.
        var limits = data.GetProperty("limits");
        Assert.Equal(GlbLimit("maxVertices"), limits.GetProperty("maxVertices").GetInt32());
        Assert.Empty(limits.GetProperty("supportedRequiredExtensions").EnumerateArray());

        // The same bytes are one asset, not two.
        var again = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block-copy.glb");
        using var duplicate = await again.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Equal(assetId, duplicate.RootElement.GetProperty("id").GetGuid());
        Assert.Single(await ModelAssetsAsync(client));

        // The bytes are actually served back for the viewer to load.
        var content = await client.GetAsync($"/api/assets/{assetId}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(ModelFixtures.AsymmetricBlock().Length, (await content.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task ARefusedModelLeavesNoAssetBehind()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var truncated = ModelFixtures.AsymmetricBlock().AsSpan(0, 400).ToArray();
        var refusedTruncation = await ImportAsync(client, truncated, "truncated.glb");
        Assert.Equal(HttpStatusCode.BadRequest, refusedTruncation.StatusCode);

        var refusedGarbage = await ImportAsync(client, "this is not a model at all"u8.ToArray(), "notes.txt");
        Assert.Equal(HttpStatusCode.BadRequest, refusedGarbage.StatusCode);

        var external = ModelFixtures.Mutate(document =>
            document["images"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["uri"] = "https://example.test/skin.png" }));
        var refusedExternal = await ImportAsync(client, external, "external.glb");
        Assert.Equal(HttpStatusCode.BadRequest, refusedExternal.StatusCode);
        var problem = await refusedExternal.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Contains("external", problem!.RootElement.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Nothing importable, and nothing half-imported, survives a refusal.
        Assert.Empty(await ModelAssetsAsync(client));
    }

    [Fact]
    public async Task AModelIsNotAnImageOrAShotPlacement()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var imported = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        using var asset = await imported.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var assetId = asset.RootElement.GetProperty("id").GetGuid();

        using var snapshot = await client.GetFromJsonAsync<JsonDocument>("/api/studio") ?? throw new InvalidOperationException();
        var shotId = snapshot.RootElement.GetProperty("shots")[0].GetProperty("id").GetGuid();
        var placement = await client.PostAsJsonAsync($"/api/assets/{assetId}/placements", new { shotId, role = "Image guide" });
        Assert.Equal(HttpStatusCode.BadRequest, placement.StatusCode);

        // And an image asset has no model profile.
        var image = await client.GetFromJsonAsync<JsonDocument>("/api/assets") ?? throw new InvalidOperationException();
        var imageAsset = image.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("kind").GetString() == "Image");
        if (imageAsset.ValueKind == JsonValueKind.Object)
        {
            var wrongKind = await client.GetAsync($"/api/assets/{imageAsset.GetProperty("id").GetGuid()}/model-profile");
            Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        }
    }

    private static int GlbLimit(string name) => name switch
    {
        "maxVertices" => 1_500_000,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement[]> ModelAssetsAsync(HttpClient client)
    {
        using var assets = await client.GetFromJsonAsync<JsonDocument>("/api/assets") ?? throw new InvalidOperationException();
        return [.. assets.RootElement.EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "Model").Select(x => x.Clone())];
    }
}
