using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Library review on every kind of asset: a decision a person makes about one
/// revision (approve, send back with a reason, or leave pending), and notes
/// attached the way that kind is looked at.
/// </summary>
public sealed class AssetReviewApiTests
{
    [Fact]
    public async Task EveryKindCanBeApprovedOrSentBackAndTheDecisionTravelsOnTheAsset()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var assets = new[]
        {
            await AssetUploads.ImageAsync(client, "review-image.png"),
            await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "review-model.glb"),
            await AssetUploads.AudioAsync(client, "review-audio.wav"),
            await AssetUploads.VideoAsync(client, "review-video.mp4"),
        };
        Assert.Equal([AssetKind.Image, AssetKind.Model, AssetKind.Audio, AssetKind.Video], assets.Select(asset => asset.Kind));
        // Nothing is judged until a person judges it.
        Assert.All(assets, asset => Assert.Equal(AssetReviewDecision.Pending, asset.ReviewDecision));

        foreach (var asset in assets)
        {
            var approved = await DecideAsync(client, asset.Id, AssetReviewDecision.Approved, "Reads well in the tavern.");
            Assert.Equal(AssetReviewDecision.Approved, approved.ReviewDecision);
            Assert.Equal("Reads well in the tavern.", approved.ReviewNote);
            Assert.NotNull(approved.ReviewDecidedAt);

            // A send-back has to say what is wrong, or the next revision has
            // nothing to answer.
            var silent = await client.PostAsJsonAsync($"/api/assets/{asset.Id}/review",
                new SetAssetReviewDecisionRequest(AssetReviewDecision.ChangesRequested, "   "));
            Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);
            Assert.Contains("what needs to change", await silent.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var sentBack = await DecideAsync(client, asset.Id, AssetReviewDecision.ChangesRequested, "Improve the texture on the lid.");
            Assert.Equal(AssetReviewDecision.ChangesRequested, sentBack.ReviewDecision);
            Assert.Equal("Improve the texture on the lid.", sentBack.ReviewNote);
        }

        // The library lists the decision with the asset, which is what the
        // cards, the filter and shipping all read.
        var listed = await client.GetFromJsonAsync<AssetSummary[]>("/api/assets") ?? throw new InvalidOperationException();
        Assert.All(assets, asset => Assert.Equal(AssetReviewDecision.ChangesRequested,
            listed.Single(item => item.Id == asset.Id).ReviewDecision));

        // Back to Pending clears the reason and the time: undecided is undecided.
        var pending = await DecideAsync(client, assets[0].Id, AssetReviewDecision.Pending, "ignored");
        Assert.Equal(AssetReviewDecision.Pending, pending.ReviewDecision);
        Assert.Equal("", pending.ReviewNote);
        Assert.Null(pending.ReviewDecidedAt);

        // Decisions are recorded where looking is recorded.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Equal(9, await db.AuditEvents.CountAsync(item => item.Type == "AssetReviewDecided"));
    }

    [Fact]
    public async Task AnUnknownOrArchivedAssetIsNotDecided()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var missing = await client.PostAsJsonAsync($"/api/assets/{Guid.NewGuid()}/review",
            new SetAssetReviewDecisionRequest(AssetReviewDecision.Approved, null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var asset = await AssetUploads.ImageAsync(client, "archived-review.png");
        (await client.PostAsync($"/api/assets/{asset.Id}/archive", null)).EnsureSuccessStatusCode();
        var archived = await client.PostAsJsonAsync($"/api/assets/{asset.Id}/review",
            new SetAssetReviewDecisionRequest(AssetReviewDecision.Approved, null));
        Assert.Equal(HttpStatusCode.BadRequest, archived.StatusCode);

        var unknownDecision = await client.PostAsJsonAsync($"/api/assets/{asset.Id}/review",
            new { decision = "Shipped", note = "" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownDecision.StatusCode);
    }

    [Fact]
    public async Task ARevisionOfSomethingSentBackAnswersThatRequestAndStartsPending()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var first = await AssetUploads.ImageAsync(client, "lantern-v1.png");
        await DecideAsync(client, first.Id, AssetReviewDecision.ChangesRequested, "Warmer light on the left pane.");

        var second = await AssetUploads.ImageAsync(client, "lantern-v2.png");
        var answered = await AddRevisionAsync(client, first.Id, second.Id);

        // New bytes nobody has looked at: Pending, and visibly the answer to
        // the request rather than an unrelated new picture.
        Assert.Equal(AssetReviewDecision.Pending, answered.ReviewDecision);
        Assert.Equal(first.Id, answered.ReviewAnswersAssetId);
        Assert.Equal("Warmer light on the left pane.", answered.ReviewAnswersNote);
        Assert.True(answered.IsCurrentRevision);

        // A further revision of an answer nobody has judged answers the same request.
        var third = await AssetUploads.ImageAsync(client, "lantern-v3.png");
        var stillAnswering = await AddRevisionAsync(client, second.Id, third.Id);
        Assert.Equal(first.Id, stillAnswering.ReviewAnswersAssetId);

        // The send-back itself is history and stays as it was.
        var revisions = await client.GetFromJsonAsync<AssetSummary[]>($"/api/assets/{first.Id}/revisions")
            ?? throw new InvalidOperationException();
        Assert.Equal(AssetReviewDecision.ChangesRequested, revisions.Single(item => item.Id == first.Id).ReviewDecision);

        // Approving the answer keeps what it answered.
        var approved = await DecideAsync(client, third.Id, AssetReviewDecision.Approved, null);
        Assert.Equal(first.Id, approved.ReviewAnswersAssetId);

        // A revision of something approved answers nothing.
        var fourth = await AssetUploads.ImageAsync(client, "lantern-v4.png");
        var unrelated = await AddRevisionAsync(client, third.Id, fourth.Id);
        Assert.Null(unrelated.ReviewAnswersAssetId);
        Assert.Equal("", unrelated.ReviewAnswersNote);
        Assert.Equal(AssetReviewDecision.Pending, unrelated.ReviewDecision);
    }

    [Fact]
    public async Task NotesAttachTheWayEachKindIsLookedAt()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var image = await AssetUploads.ImageAsync(client, "notes-image.png");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "notes-model.glb");
        var audio = await AssetUploads.AudioAsync(client, "notes-audio.wav");
        var video = await AssetUploads.VideoAsync(client, "notes-video.mp4");

        // An image note is always pinned somewhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await NoteAsync(client, image.Id, new { body = "Somewhere?" })).StatusCode);
        var pin = await NoteOkAsync(client, image.Id, new { x = .25, y = .75, body = "The scar moved." });
        Assert.Equal(AssetReviewNoteAnchors.Point, pin.Anchor);
        Assert.Equal(.25, pin.X);

        // A model note may name the view it was written from, or nothing.
        var whole = await NoteOkAsync(client, model.Id, new { body = "Too glossy overall." });
        Assert.Equal(AssetReviewNoteAnchors.None, whole.Anchor);
        var viewed = await NoteOkAsync(client, model.Id, new { body = "The back panel is torn.", viewYaw = 7.0, viewPitch = 2.4 });
        Assert.Equal(AssetReviewNoteAnchors.View, viewed.Anchor);
        Assert.Equal(Math.Round(Math.IEEERemainder(7.0, 2 * Math.PI), 4), viewed.ViewYaw);
        Assert.Equal(1.5, viewed.ViewPitch);
        Assert.Equal(HttpStatusCode.BadRequest, (await NoteAsync(client, model.Id, new { body = "Half a view.", viewYaw = 1.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NoteAsync(client, model.Id, new { body = "A pin.", x = .5, y = .5 })).StatusCode);

        // Video and audio notes may name a moment.
        var moment = await NoteOkAsync(client, audio.Id, new { body = "The swell clips here.", timeSeconds = 1.25 });
        Assert.Equal(AssetReviewNoteAnchors.Time, moment.Anchor);
        Assert.Equal(1.25, moment.TimeSeconds);
        var heard = await NoteOkAsync(client, audio.Id, new { body = "Listen first; too loud overall." });
        Assert.Equal(AssetReviewNoteAnchors.None, heard.Anchor);
        Assert.Equal(HttpStatusCode.BadRequest, (await NoteAsync(client, audio.Id, new { body = "Before the start.", timeSeconds = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NoteAsync(client, video.Id, new { body = "A pin.", x = .5, y = .5 })).StatusCode);
        var shot = await NoteOkAsync(client, video.Id, new { body = "Weak effect on the swing.", timeSeconds = 0.5 });
        Assert.Equal(AssetReviewNoteAnchors.Time, shot.Anchor);

        // Only a pin can be dragged; every note can be resolved.
        var moved = await client.PutAsJsonAsync($"/api/asset-review-notes/{shot.Id}/position", new MoveCommentRequest(.1, .1));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        var resolved = await client.PostAsync($"/api/asset-review-notes/{viewed.Id}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        var listed = await client.GetFromJsonAsync<AssetReviewNoteSummary[]>($"/api/assets/{model.Id}/review-notes")
            ?? throw new InvalidOperationException();
        Assert.Equal(2, listed.Length);
        Assert.Equal("Resolved", listed.Single(note => note.Id == viewed.Id).State);
    }

    [Fact]
    public async Task DecisionsSurviveAPortableProjectRoundTrip()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var first = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "portable-review.glb");
        await DecideAsync(client, first.Id, AssetReviewDecision.ChangesRequested, "Too many triangles on the handle.");
        var second = await AssetUploads.ModelAsync(client, ModelFixtures.DenseProp(), "portable-review-v2.glb");
        var answer = await AddRevisionAsync(client, first.Id, second.Id);
        await DecideAsync(client, answer.Id, AssetReviewDecision.Approved, "Handle fixed.");

        var package = await client.GetByteArrayAsync("/api/export/working-package");
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "review-roundtrip.zip");
        var imported = await client.PostAsync("/api/projects/import", content);
        imported.EnsureSuccessStatusCode();
        var summary = await imported.Content.ReadFromJsonAsync<PortableProjectImportSummary>() ?? throw new InvalidOperationException();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var copies = await db.Assets.IgnoreQueryFilters().Where(asset => asset.ProjectId == summary.ProjectId).ToListAsync();
        var sentBack = copies.Single(asset => asset.ContentHash == first.ContentHash);
        var approved = copies.Single(asset => asset.ContentHash == second.ContentHash);
        Assert.Equal("ChangesRequested", sentBack.ReviewDecision);
        Assert.Equal("Too many triangles on the handle.", sentBack.ReviewNote);
        Assert.Equal("Approved", approved.ReviewDecision);
        // The link to what it answered is remapped to the imported copy, not
        // left pointing into the project it came from.
        Assert.Equal(sentBack.Id, approved.ReviewAnswersAssetId);
    }

    private static async Task<AssetSummary> DecideAsync(HttpClient client, Guid assetId, AssetReviewDecision decision, string? note)
    {
        var response = await client.PostAsJsonAsync($"/api/assets/{assetId}/review", new SetAssetReviewDecisionRequest(decision, note));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }

    private static async Task<AssetSummary> AddRevisionAsync(HttpClient client, Guid parentId, Guid assetId)
    {
        var response = await client.PostAsJsonAsync($"/api/assets/{parentId}/revisions",
            new AddAssetRevisionRequest(assetId, "Answering review", "Imported"));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }

    private static Task<HttpResponseMessage> NoteAsync(HttpClient client, Guid assetId, object body) =>
        client.PostAsJsonAsync($"/api/assets/{assetId}/review-notes", body);

    private static async Task<AssetReviewNoteSummary> NoteOkAsync(HttpClient client, Guid assetId, object body)
    {
        var response = await NoteAsync(client, assetId, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AssetReviewNoteSummary>() ?? throw new InvalidOperationException();
    }
}

/// <summary>Library imports of each kind, with bytes distinct per file name.</summary>
internal static class AssetUploads
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    /// <summary>A PNG made unique by its name trailing the image data, as other tests do.</summary>
    public static Task<AssetSummary> ImageAsync(HttpClient client, string fileName) =>
        UploadAsync(client, "/api/assets/images", [.. Png, .. System.Text.Encoding.UTF8.GetBytes(fileName)], fileName, "image/png");

    public static async Task<AssetSummary> ModelAsync(HttpClient client, byte[] glb, string fileName) =>
        await UploadAsync(client, "/api/assets/models", glb, fileName, "model/gltf-binary");

    public static Task<AssetSummary> AudioAsync(HttpClient client, string fileName)
    {
        var wav = new byte[64];
        "RIFF"u8.CopyTo(wav);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        System.Text.Encoding.UTF8.GetBytes(fileName).AsSpan(0, Math.Min(fileName.Length, 20)).CopyTo(wav.AsSpan(44));
        return UploadAsync(client, "/api/assets/media?kind=Audio", wav, fileName, "audio/wav");
    }

    public static Task<AssetSummary> VideoAsync(HttpClient client, string fileName)
    {
        var mp4 = new byte[48];
        "ftyp"u8.CopyTo(mp4.AsSpan(4));
        System.Text.Encoding.UTF8.GetBytes(fileName).AsSpan(0, Math.Min(fileName.Length, 24)).CopyTo(mp4.AsSpan(24));
        return UploadAsync(client, "/api/assets/media?kind=Video", mp4, fileName, "video/mp4");
    }

    private static async Task<AssetSummary> UploadAsync(HttpClient client, string url, byte[] bytes, string fileName, string mimeType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{fileName}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }
}
