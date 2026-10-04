using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Handing approved assets to a game: an engine-neutral bundle of files and a
/// manifest, written as a new folder under a configured destination or as a
/// zip, carrying only what a person approved.
/// </summary>
public sealed class ShippingApiTests
{
    private const string TargetName = "Aether Wars";

    /// <summary>A studio with one shipping destination inside its own data root.</summary>
    private sealed class Studio : IDisposable
    {
        public StudioApiFactory Factory { get; }
        public HttpClient Client { get; }
        public string TargetPath { get; }

        public Studio(bool createTarget = true, bool configureTarget = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N"));
            TargetPath = Path.Combine(root, "game", "Content", "Framewright");
            if (createTarget) Directory.CreateDirectory(TargetPath);
            Factory = new StudioApiFactory(root, true, settings: configureTarget
                ? new Dictionary<string, string?>
                {
                    [$"{ShippingService.TargetsSection}:0:Name"] = TargetName,
                    [$"{ShippingService.TargetsSection}:0:Path"] = TargetPath,
                }
                : null);
            Client = Factory.CreateClient();
        }

        public void Dispose() { Client.Dispose(); Factory.Dispose(); }
    }

    [Fact]
    public async Task ApprovedAssetsInACollectionShipAsAVersionedBundleWithAManifest()
    {
        using var studio = new Studio();
        var client = studio.Client;
        var collection = await CollectionAsync(client, "Tavern props");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "floor-brazier.glb");
        var image = await AssetUploads.ImageAsync(client, "tavern-sign.png");
        var audio = await AssetUploads.AudioAsync(client, "hearth-loop.wav");
        var sentBack = await AssetUploads.ImageAsync(client, "weak-effect.png");
        await FileAsync(client, model, collection, "Floor brazier", ["prop", "fire"], "Lit at night.");
        await FileAsync(client, image, collection, "Tavern sign");
        await FileAsync(client, audio, collection, "Hearth loop");
        await FileAsync(client, sentBack, collection, "Weak effect");
        await DecideAsync(client, model.Id, AssetReviewDecision.Approved, "Ready for the tavern.");
        await DecideAsync(client, image.Id, AssetReviewDecision.Approved, null);
        await DecideAsync(client, sentBack.Id, AssetReviewDecision.ChangesRequested, "The glow reads weak.");

        // Before anything is written, the artist sees what will go and why not.
        var preview = await PostOkAsync<ShipmentPreview>(client, "/api/shipping/preview", new ShipAssetsRequest(collection, null));
        Assert.Equal("Tavern props", preview.SourceName);
        Assert.Equal(2, preview.ShipCount);
        Assert.Equal(2, preview.SkippedCount);
        Assert.Equal("Pending review", preview.Items.Single(item => item.RevisionId == audio.Id).Reason);
        Assert.Equal("Sent back: The glow reads weak.", preview.Items.Single(item => item.RevisionId == sentBack.Id).Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(studio.TargetPath));

        var receipt = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship",
            new ShipAssetsRequest(collection, null, Target: TargetName));
        Assert.Equal(TargetName, receipt.Target);
        Assert.Equal(2, receipt.ItemCount);
        Assert.Equal(2, receipt.SkippedCount);
        // A new folder per shipment, named for the source and when it shipped.
        Assert.Equal(Path.Combine(studio.TargetPath, "tavern-props"), Path.GetDirectoryName(receipt.Folder));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z$", Path.GetFileName(receipt.Folder));
        // Built beside its final name and moved in whole: no half bundle remains.
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(studio.TargetPath, "tavern-props")),
            folder => Path.GetFileName(folder).StartsWith(".partial", StringComparison.Ordinal));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(receipt.Folder!, ShippingService.ManifestFileName)));
        var root = manifest.RootElement;
        Assert.Equal("framewright.bundle.v1", root.GetProperty("schema").GetString());
        Assert.Equal(receipt.ShipmentId, root.GetProperty("shipmentId").GetGuid());
        Assert.Equal("collection", root.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("Tavern props", root.GetProperty("source").GetProperty("name").GetString());
        Assert.False(root.GetProperty("includePending").GetBoolean());
        Assert.Equal("metres", root.GetProperty("units").GetProperty("length").GetString());
        Assert.Equal("Framewright", root.GetProperty("generator").GetProperty("name").GetString());

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var brazier = items.Single(item => item.GetProperty("revisionId").GetGuid() == model.Id);
        Assert.Equal(model.Id, brazier.GetProperty("assetId").GetGuid());
        Assert.Equal("Floor brazier", brazier.GetProperty("displayName").GetString());
        Assert.Equal("Model", brazier.GetProperty("kind").GetString());
        Assert.Equal("Tavern props", brazier.GetProperty("collection").GetString());
        var file = brazier.GetProperty("file").GetString()!;
        Assert.StartsWith("files/models/floor-brazier-", file, StringComparison.Ordinal);
        Assert.EndsWith(".glb", file, StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(receipt.Folder!, file));
        // The hash is of the bytes in the bundle, and they are the library's bytes.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), brazier.GetProperty("sha256").GetString());
        Assert.Equal(model.ContentHash, brazier.GetProperty("sha256").GetString(), ignoreCase: true);
        Assert.Equal(bytes.LongLength, brazier.GetProperty("bytes").GetInt64());
        Assert.Equal([2.0, 1.0, 0.75], brazier.GetProperty("dimensionsMetres").EnumerateArray().Select(value => value.GetDouble()));
        Assert.Equal(24, brazier.GetProperty("triangleCount").GetInt32());
        Assert.Equal(["prop", "fire"], brazier.GetProperty("tags").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Lit at night.", brazier.GetProperty("notes").GetString());
        Assert.Equal("Approved", brazier.GetProperty("review").GetProperty("decision").GetString());
        Assert.Equal("Ready for the tavern.", brazier.GetProperty("review").GetProperty("note").GetString());

        var sign = items.Single(item => item.GetProperty("revisionId").GetGuid() == image.Id);
        Assert.StartsWith("files/images/tavern-sign-", sign.GetProperty("file").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, sign.GetProperty("widthPixels").GetInt32());
        Assert.False(sign.TryGetProperty("triangleCount", out _));

        var skipped = root.GetProperty("skipped").EnumerateArray().ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.Contains(skipped, item => item.GetProperty("reason").GetString() == "Pending review");

        // The studio keeps its own receipt of what went where.
        using var scope = studio.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var shipped = await db.AuditEvents.SingleAsync(item => item.Type == "AssetsShipped");
        Assert.Contains(receipt.ShipmentId.ToString(), shipped.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShippingAgainWritesANewFolderAndNeverTouchesTheFirst()
    {
        using var studio = new Studio();
        var client = studio.Client;
        var collection = await CollectionAsync(client, "Props");
        var image = await AssetUploads.ImageAsync(client, "barrel.png");
        await FileAsync(client, image, collection, "Barrel");
        await DecideAsync(client, image.Id, AssetReviewDecision.Approved, null);

        var first = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship", new ShipAssetsRequest(collection, null, Target: TargetName));
        var firstManifest = await File.ReadAllBytesAsync(Path.Combine(first.Folder!, ShippingService.ManifestFileName));
        var second = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship", new ShipAssetsRequest(collection, null, Target: TargetName));

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.NotEqual(first.ShipmentId, second.ShipmentId);
        Assert.Equal(firstManifest, await File.ReadAllBytesAsync(Path.Combine(first.Folder!, ShippingService.ManifestFileName)));
        Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(studio.TargetPath, "props")).Count());
    }

    [Fact]
    public async Task IncludingPendingShipsPendingWorkButNeverWhatWasSentBack()
    {
        using var studio = new Studio();
        var client = studio.Client;
        var collection = await CollectionAsync(client, "Draft drop");
        var pending = await AssetUploads.ImageAsync(client, "pending.png");
        var sentBack = await AssetUploads.ImageAsync(client, "sent-back.png");
        await FileAsync(client, pending, collection, "Pending");
        await FileAsync(client, sentBack, collection, "Sent back");
        await DecideAsync(client, sentBack.Id, AssetReviewDecision.ChangesRequested, "Needs a touch-up.");

        var receipt = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship",
            new ShipAssetsRequest(collection, null, IncludePending: true, Target: TargetName));

        Assert.Equal(1, receipt.ItemCount);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(receipt.Folder!, ShippingService.ManifestFileName)));
        Assert.True(manifest.RootElement.GetProperty("includePending").GetBoolean());
        var item = Assert.Single(manifest.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(pending.Id, item.GetProperty("revisionId").GetGuid());
        Assert.Equal("Pending", item.GetProperty("review").GetProperty("decision").GetString());
        Assert.Equal("Sent back: Needs a touch-up.",
            Assert.Single(manifest.RootElement.GetProperty("skipped").EnumerateArray()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task NothingApprovedShipsNothingAndWritesNothing()
    {
        using var studio = new Studio();
        var client = studio.Client;
        var collection = await CollectionAsync(client, "Not yet");
        var image = await AssetUploads.ImageAsync(client, "not-yet.png");
        await FileAsync(client, image, collection, "Not yet");

        var refused = await client.PostAsJsonAsync("/api/shipping/ship", new ShipAssetsRequest(collection, null, Target: TargetName));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("is approved yet", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var zip = await client.PostAsJsonAsync("/api/shipping/zip", new ShipAssetsRequest(collection, null));
        Assert.Equal(HttpStatusCode.BadRequest, zip.StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(studio.TargetPath));

        // Neither a collection nor a selection, or both, is not a shipment.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/shipping/preview", new ShipAssetsRequest(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/shipping/preview", new ShipAssetsRequest(collection, [image.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/shipping/preview", new ShipAssetsRequest(Guid.NewGuid(), null))).StatusCode);
    }

    [Fact]
    public async Task OnlyAConfiguredDestinationThatExistsIsWrittenTo()
    {
        using (var missing = new Studio(createTarget: false))
        {
            var targets = await missing.Client.GetFromJsonAsync<ShippingTargetSummary[]>("/api/shipping/targets")
                ?? throw new InvalidOperationException();
            var target = Assert.Single(targets);
            Assert.Equal(TargetName, target.Name);
            Assert.False(target.Available);
            Assert.Contains("does not exist", target.Problem, StringComparison.Ordinal);

            var collection = await CollectionAsync(missing.Client, "Anything");
            var image = await AssetUploads.ImageAsync(missing.Client, "anything.png");
            await FileAsync(missing.Client, image, collection, "Anything");
            await DecideAsync(missing.Client, image.Id, AssetReviewDecision.Approved, null);
            var refused = await missing.Client.PostAsJsonAsync("/api/shipping/ship", new ShipAssetsRequest(collection, null, Target: TargetName));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            // A destination is never conjured up: the folder still does not exist.
            Assert.False(Directory.Exists(missing.TargetPath));

            // And the browser can only name a destination, never supply one.
            var unknown = await missing.Client.PostAsJsonAsync("/api/shipping/ship",
                new ShipAssetsRequest(collection, null, Target: missing.TargetPath));
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        using var none = new Studio(configureTarget: false);
        Assert.Empty(await none.Client.GetFromJsonAsync<ShippingTargetSummary[]>("/api/shipping/targets") ?? []);
    }

    [Fact]
    public async Task AZipCarriesTheSameBundleWhenNoDestinationIsConfigured()
    {
        using var studio = new Studio(configureTarget: false);
        var client = studio.Client;
        var collection = await CollectionAsync(client, "Zip props");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "zip-crate.glb");
        await FileAsync(client, model, collection, "Zip crate");
        await DecideAsync(client, model.Id, AssetReviewDecision.Approved, null);

        var response = await client.PostAsJsonAsync("/api/shipping/zip", new ShipAssetsRequest(collection, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("1", response.Headers.GetValues("X-Framewright-Bundle-Items").Single());
        Assert.StartsWith("zip-props-", response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);

        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var manifestEntry = zip.GetEntry(ShippingService.ManifestFileName) ?? throw new InvalidOperationException("no manifest");
        using var manifest = await JsonDocument.ParseAsync(manifestEntry.Open());
        var item = Assert.Single(manifest.RootElement.GetProperty("items").EnumerateArray());
        var entry = zip.GetEntry(item.GetProperty("file").GetString()!) ?? throw new InvalidOperationException("no file");
        using var copy = new MemoryStream();
        await using (var stream = entry.Open()) await stream.CopyToAsync(copy);
        Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(copy.ToArray())));
    }

    [Fact]
    public async Task ASelectionShipsEachAssetsCurrentRevisionUnderItsFirstRevisionsId()
    {
        using var studio = new Studio();
        var client = studio.Client;
        var first = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "stable-crate.glb");
        await DecideAsync(client, first.Id, AssetReviewDecision.Approved, null);
        var archived = await AssetUploads.ImageAsync(client, "old-sign.png");
        await DecideAsync(client, archived.Id, AssetReviewDecision.Approved, null);
        (await client.PostAsync($"/api/assets/{archived.Id}/archive", null)).EnsureSuccessStatusCode();

        var before = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship",
            new ShipAssetsRequest(null, [first.Id, archived.Id], Target: TargetName));
        Assert.Equal(1, before.ItemCount);
        Assert.Equal(1, before.SkippedCount);
        Assert.Equal("selection", Path.GetFileName(Path.GetDirectoryName(before.Folder)));

        var second = await AssetUploads.ModelAsync(client, ModelFixtures.DenseProp(), "stable-crate-v2.glb");
        var revised = await client.PostAsJsonAsync($"/api/assets/{first.Id}/revisions",
            new AddAssetRevisionRequest(second.Id, "Denser lid", "Imported"));
        revised.EnsureSuccessStatusCode();
        await DecideAsync(client, second.Id, AssetReviewDecision.Approved, null);

        // Naming the old revision ships the asset as it is now, under the id
        // the game saw first, so an importer updates rather than duplicates.
        var after = await PostOkAsync<ShipmentReceipt>(client, "/api/shipping/ship",
            new ShipAssetsRequest(null, [first.Id], Target: TargetName));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(after.Folder!, ShippingService.ManifestFileName)));
        var item = Assert.Single(manifest.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(first.Id, item.GetProperty("assetId").GetGuid());
        Assert.Equal(second.Id, item.GetProperty("revisionId").GetGuid());
        Assert.Equal(2, item.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("selection", manifest.RootElement.GetProperty("source").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("Floor brazier", "floor-brazier")]
    [InlineData("Café sign / Big!", "cafe-sign-big")]
    [InlineData("  ", "fallback")]
    [InlineData("中文", "fallback")]
    public void NamesBecomePortableFolderAndFileNames(string name, string expected) =>
        Assert.Equal(expected, ShippingService.Slug(name, "fallback"));

    private static async Task<Guid> CollectionAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/asset-collections", new { name, color = "#73b7cf" });
        response.EnsureSuccessStatusCode();
        using var created = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return created.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task FileAsync(HttpClient client, AssetSummary asset, Guid collectionId, string name,
        string[]? tags = null, string notes = "")
    {
        var response = await client.PutAsJsonAsync($"/api/assets/{asset.Id}", new { displayName = name, collectionId, tags = tags ?? [], notes });
        response.EnsureSuccessStatusCode();
    }

    private static async Task DecideAsync(HttpClient client, Guid assetId, AssetReviewDecision decision, string? note) =>
        (await client.PostAsJsonAsync($"/api/assets/{assetId}/review", new SetAssetReviewDecisionRequest(decision, note))).EnsureSuccessStatusCode();

    private static async Task<T> PostOkAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidOperationException();
    }
}
