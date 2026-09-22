using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

public sealed class AssetDirectorApiTests
{
    [Fact]
    public async Task ImageObservationIsBoundToItsContentAndNotesWithoutChangingProductionState()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var image = await ImportAsync(client, png, "direction.png", "image/png", "images");
        using var before = await ReadAsync(client, "/api/studio");
        using var context = await ReadAsync(client, ContextUrl(image));
        Assert.True(context.RootElement.GetProperty("ok").GetBoolean());
        var data = context.RootElement.GetProperty("data");
        Assert.Equal(image.Id, data.GetProperty("subject").GetProperty("assetId").GetGuid());
        Assert.Equal(image.ContentHash, data.GetProperty("visual").GetProperty("contentHash").GetString());
        var token = data.GetProperty("stateToken").GetString()!;
        using var observation = await ReadAsync(client, ObservationUrl(image, token));
        Assert.Equal("asset_observation", observation.RootElement.GetProperty("code").GetString());

        (await client.PostAsJsonAsync($"/api/assets/{image.Id}/review-notes", new { x = .25, y = .75, body = "Keep this corner clear." })).EnsureSuccessStatusCode();
        using var stale = await ReadAsync(client, ObservationUrl(image, token));
        Assert.Equal("stale_context", stale.RootElement.GetProperty("code").GetString());
        using var refreshed = await ReadAsync(client, ContextUrl(image));
        Assert.Single(refreshed.RootElement.GetProperty("data").GetProperty("annotations").EnumerateArray());
        using var wrongBytes = await ReadAsync(client, ContextUrl(image with { ContentHash = new string('0', 64) }));
        Assert.Equal("stale_asset", wrongBytes.RootElement.GetProperty("code").GetString());
        using var missingToken = await ReadAsync(client, ObservationUrl(image, ""));
        Assert.Equal("invalid_state_token", missingToken.RootElement.GetProperty("code").GetString());

        using var after = await ReadAsync(client, "/api/studio");
        Assert.Equal(before.RootElement.GetProperty("jobs").GetArrayLength(), after.RootElement.GetProperty("jobs").GetArrayLength());
        var saved = await client.GetFromJsonAsync<AssetSummary[]>($"/api/assets");
        Assert.Null(saved!.Single(x => x.Id == image.Id).PreparationAcceptedAt);
    }

    [Fact]
    public async Task OlderModelRevisionIsIdentifiedExactlyAndCannotLeakAcrossProjects()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var older = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "block.glb", "model/gltf-binary", "models");
        var newer = await ImportAsync(client, ModelFixtures.AsymmetricPost(), "post.glb", "model/gltf-binary", "models");
        (await client.PostAsJsonAsync($"/api/assets/{older.Id}/revisions", new { assetId = newer.Id, prompt = "Taller", engine = "Imported" })).EnsureSuccessStatusCode();
        using var context = await ReadAsync(client, ContextUrl(older));
        var data = context.RootElement.GetProperty("data");
        Assert.Equal(older.Id, data.GetProperty("subject").GetProperty("assetId").GetGuid());
        Assert.Equal(1, data.GetProperty("subject").GetProperty("displayedVersion").GetInt32());
        Assert.False(data.GetProperty("subject").GetProperty("isCurrentRevision").GetBoolean());
        using var observation = await ReadAsync(client, ObservationUrl(older, data.GetProperty("stateToken").GetString()!));
        Assert.Equal("observation_unavailable", observation.RootElement.GetProperty("code").GetString());
        var create = await client.PostAsJsonAsync("/api/projects", new { name = "Asset isolation", production = "Test", sequenceCode = "ISO-01", sequenceName = "Empty", framesPerSecond = 24, aspectRatio = "16:9", deliveryWidth = 1920, deliveryHeight = 1080 });
        create.EnsureSuccessStatusCode();
        using var project = (await create.Content.ReadFromJsonAsync<JsonDocument>())!;
        (await client.PostAsync($"/api/projects/{project.RootElement.GetProperty("id").GetGuid()}/activate", null)).EnsureSuccessStatusCode();
        using var foreign = await ReadAsync(client, ContextUrl(older));
        Assert.Equal("asset_not_found", foreign.RootElement.GetProperty("code").GetString());
    }

    private static string ContextUrl(AssetSummary asset) => $"/api/webmcp/director/assets/{asset.Id}/context?contentHash={asset.ContentHash}&directorMode=true";
    private static string ObservationUrl(AssetSummary asset, string token) => $"/api/webmcp/director/assets/{asset.Id}/observation?contentHash={asset.ContentHash}&stateToken={token}";
    private static async Task<JsonDocument> ReadAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<JsonDocument>(url))!;

    private static async Task<AssetSummary> ImportAsync(HttpClient client, byte[] bytes, string name, string mime, string route)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/assets/{route}") { Content = form };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
    }
}
