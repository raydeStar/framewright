using System.Net;
using System.Net.Http.Json;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Video endpoints made by moving the camera across one image: the windows
/// become working revisions beside the live frame and are bound as Start and
/// Last Frame together, with nothing generated.
/// </summary>
public sealed class CameraMoveEndpointTests
{
    [Fact]
    public async Task ATruckBindsTwoNewWindowsWithoutTouchingTheLiveFrame()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (shot, source) = await ShotWithImageAsync(client, "SH-TRK");
        var left = await WindowAsync(client, "truck-left-window.png");
        var right = await WindowAsync(client, "truck-right-window.png");

        var response = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints/camera-move",
            new CameraMoveEndpointsRequest(shot.UpdatedAt, source.Id, "TruckRight", 15, left.Id, right.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = (await response.Content.ReadFromJsonAsync<ShotSummary>())!;
        Assert.Equal(shot.Version, moved.Version);
        Assert.Equal(shot.CurrentAssetId, moved.CurrentAssetId);
        var candidates = (await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates"))!;
        var start = Assert.Single(candidates, item => item.Id == moved.VideoFirstFrameCandidateId);
        var end = Assert.Single(candidates, item => item.Id == moved.VideoLastFrameCandidateId);
        Assert.Equal(left.Id, start.AssetId);
        Assert.Equal(right.Id, end.AssetId);
        Assert.All([start, end], window =>
        {
            Assert.False(window.IsCurrent);
            Assert.Equal(ApprovalState.Working, window.Approval);
            Assert.True(window.Version > shot.Version);
        });
        Assert.True(Assert.Single(candidates, item => item.IsCurrent).Id == source.Id);

        // The bound windows drive the frozen video packet exactly.
        var manifest = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
            new PrepareVideoManifestRequest(moved.Version, "A slow truck right at constant framing.", true, start.Id, end.Id));
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var packet = (await manifest.Content.ReadFromJsonAsync<GenerationManifestSummary>())!;
        Assert.Equal(left.Id, packet.CompositionAssetId);
        Assert.Equal(right.Id, packet.LastFrameAssetId);
    }

    [Fact]
    public async Task APushInStartsOnTheSourceAndAPullBackEndsOnIt()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (shot, source) = await ShotWithImageAsync(client, "SH-PSH");
        var closer = await WindowAsync(client, "push-in-window.png");

        var pushed = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints/camera-move",
            new CameraMoveEndpointsRequest(shot.UpdatedAt, source.Id, "PushIn", 20, EndAssetId: closer.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;
        Assert.Equal(source.Id, pushed.VideoFirstFrameCandidateId);
        Assert.NotEqual(source.Id, pushed.VideoLastFrameCandidateId);

        var opening = await WindowAsync(client, "pull-back-window.png");
        var pulled = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints/camera-move",
            new CameraMoveEndpointsRequest(pushed.UpdatedAt, source.Id, "PullBack", 20, StartAssetId: opening.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;
        Assert.Equal(source.Id, pulled.VideoLastFrameCandidateId);
        var candidates = (await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates"))!;
        Assert.Equal(opening.Id, Assert.Single(candidates, item => item.Id == pulled.VideoFirstFrameCandidateId).AssetId);
    }

    [Fact]
    public async Task ACameraMoveIsRefusedWhenItIsStaleIncompleteOrNotAnImage()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (shot, source) = await ShotWithImageAsync(client, "SH-BAD");
        var window = await WindowAsync(client, "refused-window.png");
        var square = await AssetUploads.ImageAsync(client, "square-window.png");
        var video = await AssetUploads.VideoAsync(client, "not-a-window.mp4");
        async Task<HttpStatusCode> MoveAsync(CameraMoveEndpointsRequest request)
            => (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints/camera-move", request)).StatusCode;

        Assert.Equal(HttpStatusCode.Conflict, await MoveAsync(new(shot.UpdatedAt.AddSeconds(-1), source.Id, "PushIn", 15, EndAssetId: window.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "Orbit", 15, EndAssetId: window.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "PushIn", 15, StartAssetId: window.Id, EndAssetId: window.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "TruckLeft", 15, EndAssetId: window.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "PushIn", 90, EndAssetId: window.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "PushIn", 15, EndAssetId: video.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "PushIn", 15, EndAssetId: source.AssetId)));
        Assert.Equal(HttpStatusCode.BadRequest, await MoveAsync(new(shot.UpdatedAt, source.Id, "PushIn", 15, EndAssetId: square.Id)));

        // Nothing was bound or added by any refusal.
        var after = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Shots.Single(item => item.Id == shot.Id);
        Assert.Null(after.VideoLastFrameCandidateId);
        Assert.Single((await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates"))!);
    }

    /// <summary>A real PNG in the project's delivery shape, as the browser crops it.</summary>
    private static async Task<AssetSummary> WindowAsync(HttpClient client, string fileName)
    {
        var project = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Project;
        var png = StoryboardStudio.Api.Services.ProofPngRenderer.Render(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileName))),
            project.DeliveryWidth / 4, project.DeliveryHeight / 4);
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
    }

    private static async Task<(ShotSummary Shot, CandidateVersionSummary Source)> ShotWithImageAsync(HttpClient client, string code)
    {
        var image = await AssetUploads.ImageAsync(client, $"{code}-source.png");
        var shot = (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            code, "Camera move", "A deck at dusk.", 48, "35mm · locked", "Hold.", [], [], image.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;
        var source = Assert.Single((await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates"))!, item => item.IsCurrent);
        return (shot, source);
    }
}
