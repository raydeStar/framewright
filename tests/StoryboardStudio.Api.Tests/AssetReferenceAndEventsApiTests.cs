using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A model names the image it was made from, set on upload or afterwards and
/// carried by its revisions; and open pages hear about library changes as
/// they are saved, without reloading.
/// </summary>
public sealed class AssetReferenceAndEventsApiTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task AModelUploadCanNameItsReferenceImageById()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var concept = await AssetUploads.ImageAsync(client, "SM_Gear (req 1.2).png");

        var model = await UploadModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Gear (req 1.2).glb", referenceAssetId: concept.Id.ToString());
        Assert.Equal(concept.Id, model.ReferenceAssetId);
        var listed = await client.GetFromJsonAsync<AssetSummary[]>("/api/assets") ?? throw new InvalidOperationException();
        Assert.Equal(concept.Id, listed.Single(asset => asset.Id == model.Id).ReferenceAssetId);
    }

    [Fact]
    public async Task AModelUploadCanCarryItsReferenceImageAlongside()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var model = await UploadModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Pot.glb",
            referenceImage: ([.. Png, .. "SM_Pot concept"u8.ToArray()], "SM_Pot (req 2.0).png"));
        Assert.NotNull(model.ReferenceAssetId);
        var listed = await client.GetFromJsonAsync<AssetSummary[]>("/api/assets") ?? throw new InvalidOperationException();
        var image = listed.Single(asset => asset.Id == model.ReferenceAssetId);
        Assert.Equal(AssetKind.Image, image.Kind);
        Assert.Equal("SM_Pot (req 2.0)", image.DisplayName);
    }

    [Fact]
    public async Task ABadReferenceIsRefusedBeforeTheModelIsStored()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var other = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricPost(), "SM_Other.glb");

        var unknown = await PostModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Lost.glb", referenceAssetId: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var notImage = await PostModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Lost.glb", referenceAssetId: other.Id.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);
        Assert.Contains("has to be an image", await notImage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var garbled = await PostModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Lost.glb", referenceAssetId: "not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, garbled.StatusCode);

        // None of those left a model behind.
        var listed = await client.GetFromJsonAsync<AssetSummary[]>("/api/assets") ?? throw new InvalidOperationException();
        Assert.Single(listed, asset => asset.Kind == AssetKind.Model);
    }

    [Fact]
    public async Task AReferenceCanBeLinkedLaterAndRevisionsKeepIt()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var concept = await AssetUploads.ImageAsync(client, "SM_Drum (req 3.1).png");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Drum.glb");
        Assert.Null(model.ReferenceAssetId);

        var linked = await SetReferenceAsync(client, model.Id, concept.Id);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(concept.Id, (await linked.Content.ReadFromJsonAsync<AssetSummary>())!.ReferenceAssetId);

        // A reworked model was still made from the same concept.
        var reworked = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricPost(), "SM_Drum_v2.glb");
        var revision = await client.PostAsJsonAsync($"/api/assets/{model.Id}/revisions", new AddAssetRevisionRequest(reworked.Id));
        Assert.Equal(HttpStatusCode.OK, revision.StatusCode);
        Assert.Equal(concept.Id, (await revision.Content.ReadFromJsonAsync<AssetSummary>())!.ReferenceAssetId);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetReferenceAsync(client, concept.Id, concept.Id)).StatusCode);
        var cleared = await SetReferenceAsync(client, model.Id, null);
        Assert.Null((await cleared.Content.ReadFromJsonAsync<AssetSummary>())!.ReferenceAssetId);
        Assert.Equal(HttpStatusCode.NotFound, (await SetReferenceAsync(client, Guid.NewGuid(), concept.Id)).StatusCode);
    }

    [Fact]
    public async Task OpenPagesHearWhenAModelOrARevisionArrives()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync("/api/assets/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("retry: 3000", await reader.ReadLineAsync(timeout.Token));

        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "SM_Live.glb");
        var added = await NextAssetEventAsync(reader, model.Id, timeout.Token);
        Assert.Contains("\"type\":\"asset-added\"", added, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Model\"", added, StringComparison.Ordinal);

        var reworked = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricPost(), "SM_Live_v2.glb");
        var revision = await client.PostAsJsonAsync($"/api/assets/{model.Id}/revisions", new AddAssetRevisionRequest(reworked.Id));
        var revised = await NextAssetEventAsync(reader, reworked.Id, timeout.Token, "revision-added");
        Assert.Contains("\"revisionNumber\":2", revised, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<string> NextAssetEventAsync(StreamReader reader, Guid assetId, CancellationToken cancellationToken, string type = "")
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line.Contains(assetId.ToString(), StringComparison.OrdinalIgnoreCase) && line.Contains(type, StringComparison.Ordinal)) return line;
        }
        throw new InvalidOperationException("The event stream ended before the event arrived.");
    }

    private static async Task<HttpResponseMessage> SetReferenceAsync(HttpClient client, Guid assetId, Guid? referenceId) =>
        await client.PutAsJsonAsync($"/api/assets/{assetId}/reference", new SetAssetReferenceRequest(referenceId));

    private static async Task<AssetSummary> UploadModelAsync(HttpClient client, byte[] glb, string fileName,
        string? referenceAssetId = null, (byte[] Bytes, string Name)? referenceImage = null)
    {
        var response = await PostModelAsync(client, glb, fileName, referenceAssetId, referenceImage);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }

    private static async Task<HttpResponseMessage> PostModelAsync(HttpClient client, byte[] glb, string fileName,
        string? referenceAssetId = null, (byte[] Bytes, string Name)? referenceImage = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(glb);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        if (referenceAssetId is not null) content.Add(new StringContent(referenceAssetId), "referenceAssetId");
        if (referenceImage is { } image)
        {
            var png = new ByteArrayContent(image.Bytes);
            png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(png, "referenceImage", image.Name);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }
}
