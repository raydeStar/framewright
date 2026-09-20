using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A reference becomes a construction plan the artist can argue with, and only
/// then a scene. The plan is bound to the exact picture it was read from, it is
/// bounded in size, it says what it could not see, and approving it builds
/// placeholders and library matches only: never a generated asset, and never a
/// provider job.
/// </summary>
public sealed class SceneBlockoutApiTests
{
    [Fact]
    public async Task APlanBecomesAnEditableBlockoutWhoseCorrectionsAndReasoningSurviveAReopen()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-blockout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid sceneId, planId, referenceId, modelId;
            Guid chairId;
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                modelId = await ImportModelAsync(client, ModelFixtures.AsymmetricBlock(), "chair.glb");
                var (id, hash) = await ImportReferenceAsync(client, TinyPng, "chain-court.png");
                referenceId = id;

                using var proposed = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", new
                {
                    referenceAssetId = referenceId, observedReferenceHash = hash,
                    title = "Chain court blockout",
                    summary = "Three objects read off the court reference: a seat, a standing figure, and the floor.",
                    camera = new { yaw = 1.1, pitch = 0.4, distance = 8.0, target = new[] { 0d, 1d, 0d }, fieldOfView = 35d },
                    assumptions = new[] { "The floor is flat.", "The figure is about 1.8 metres tall." },
                    uncertainties = new[] { "The right third of the reference is behind the chain." },
                    items = new object[]
                    {
                        new { role = "Magistrate chair", matchAssetId = modelId, position = new[] { 0d, 0d, -1.5 }, rotation = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d }, motionIntent = "Static.", confidence = "Certain", note = "Matches the block in the library." },
                        new { role = "Standing figure", shape = "Cylinder", size = new[] { 0.5, 1.8, 0.5 }, position = new[] { 1.2, 0.9, 0d }, rotation = new[] { 0d, 0.4, 0d }, scale = new[] { 1d, 1d, 1d }, motionIntent = "Walks toward the chair.", confidence = "Approximate", note = "Height guessed from the doorway." },
                        new { role = "Court floor", shape = "Plane", size = new[] { 12d, 0.1, 12d }, position = new[] { 0d, 0d, 0d }, rotation = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d }, motionIntent = "", confidence = "Occluded", note = "Far edge is not visible." },
                    },
                    idempotencyKey = "blockout-round-trip",
                });
                Assert.True(proposed.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("blockout_proposed", proposed.RootElement.GetProperty("code").GetString());
                var plan = proposed.RootElement.GetProperty("data");
                planId = plan.GetProperty("id").GetGuid();
                Assert.Equal("Pending", plan.GetProperty("state").GetString());
                Assert.Equal(3, plan.GetProperty("items").GetArrayLength());
                Assert.False(plan.GetProperty("referenceChanged").GetBoolean());

                // Proposing builds nothing at all.
                using var before = await client.GetFromJsonAsync<JsonDocument>("/api/scenes") ?? throw new InvalidOperationException();
                Assert.Empty(before.RootElement.EnumerateArray().ToArray());

                var applied = await client.PostAsJsonAsync($"/api/scene-blockouts/{planId}/apply", new { sceneName = "Chain court blockout" });
                Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
                using var scene = await applied.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                sceneId = scene.RootElement.GetProperty("id").GetGuid();
                var instances = scene.RootElement.GetProperty("instances").EnumerateArray().ToArray();
                Assert.Equal(3, instances.Length);

                // Three distinct objects: one real model, two different stand-ins.
                var chair = instances.Single(x => x.GetProperty("name").GetString() == "Magistrate chair");
                var figure = instances.Single(x => x.GetProperty("name").GetString() == "Standing figure");
                var floor = instances.Single(x => x.GetProperty("name").GetString() == "Court floor");
                chairId = chair.GetProperty("id").GetGuid();
                Assert.Equal(modelId, chair.GetProperty("assetId").GetGuid());
                Assert.Equal(JsonValueKind.Null, chair.GetProperty("placeholder").ValueKind);
                Assert.Equal("Cylinder", figure.GetProperty("placeholder").GetProperty("shape").GetString());
                Assert.Equal("Plane", floor.GetProperty("placeholder").GetProperty("shape").GetString());
                Assert.Equal(JsonValueKind.Null, figure.GetProperty("assetId").ValueKind);
                Assert.Equal([0.5, 1.8, 0.5], Numbers(figure.GetProperty("placeholder"), "size"));
                Assert.NotEqual(figure.GetProperty("id").GetGuid(), floor.GetProperty("id").GetGuid());

                // The camera the plan proposed is the camera the scene opens on.
                Assert.Equal(8.0, scene.RootElement.GetProperty("camera").GetProperty("distance").GetDouble(), 6);

                // No provider job was created by proposing or by applying.
                using (var probe = factory.Services.CreateScope())
                {
                    var db = probe.ServiceProvider.GetRequiredService<StudioDbContext>();
                    Assert.Equal(0, await db.Jobs.IgnoreQueryFilters().CountAsync());
                    Assert.Equal(0, await db.GenerationManifests.IgnoreQueryFilters().CountAsync());
                }

                // The artist corrects one placement and the framing, and nothing
                // else in the blockout moves.
                var correction = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
                {
                    expectedVersion = 1, name = "Chain court blockout",
                    camera = new { yaw = 1.1, pitch = 0.4, distance = 5.25, target = new[] { 0d, 1d, 0d }, fieldOfView = 35d },
                    environment = new { keyIntensity = 2.2, keyYaw = 0.8, keyPitch = 0.9, ambientIntensity = 1.4 },
                    instances = instances.Select(instance => new
                    {
                        id = instance.GetProperty("id").GetGuid(),
                        assetId = instance.GetProperty("assetId").ValueKind == JsonValueKind.Null
                            ? (Guid?)null : instance.GetProperty("assetId").GetGuid(),
                        placeholder = instance.GetProperty("placeholder").ValueKind == JsonValueKind.Null ? null : new
                        {
                            shape = instance.GetProperty("placeholder").GetProperty("shape").GetString(),
                            size = Numbers(instance.GetProperty("placeholder"), "size"),
                        },
                        name = instance.GetProperty("name").GetString(),
                        position = instance.GetProperty("id").GetGuid() == chairId
                            ? new[] { 2.75, 0d, -1.5 }
                            : Numbers(instance, "position"),
                        rotation = Numbers(instance, "rotation"),
                        scale = Numbers(instance, "scale"),
                    }).ToArray(),
                });
                Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
                var instances = scene.RootElement.GetProperty("instances").EnumerateArray().ToArray();
                var chair = instances.Single(x => x.GetProperty("id").GetGuid() == chairId);
                var figure = instances.Single(x => x.GetProperty("name").GetString() == "Standing figure");

                // The one correction survived; the untouched object did not move,
                // and the camera correction is independent of it.
                Assert.Equal([2.75, 0d, -1.5], Numbers(chair, "position"));
                Assert.Equal([1.2, 0.9, 0d], Numbers(figure, "position"));
                Assert.Equal("Cylinder", figure.GetProperty("placeholder").GetProperty("shape").GetString());
                Assert.Equal(5.25, scene.RootElement.GetProperty("camera").GetProperty("distance").GetDouble(), 6);

                // Every object still names the role and plan it came from.
                Assert.Equal("Magistrate chair", chair.GetProperty("role").GetString());
                Assert.Equal(planId, figure.GetProperty("planId").GetGuid());

                // The reference and the assumptions are still inspectable.
                using var plan = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}/blockout") ?? throw new InvalidOperationException();
                Assert.Equal(referenceId, plan.RootElement.GetProperty("referenceAssetId").GetGuid());
                Assert.Equal("Applied", plan.RootElement.GetProperty("state").GetString());
                Assert.Contains("The floor is flat.", plan.RootElement.GetProperty("assumptions").EnumerateArray().Select(x => x.GetString()));
                Assert.Contains("behind the chain", string.Join(" ", plan.RootElement.GetProperty("uncertainties").EnumerateArray().Select(x => x.GetString())));
                var planned = plan.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal("Occluded", planned.Single(x => x.GetProperty("role").GetString() == "Court floor").GetProperty("confidence").GetString());
                Assert.Equal("Walks toward the chair.", planned.Single(x => x.GetProperty("role").GetString() == "Standing figure").GetProperty("motionIntent").GetString());
                // Each planned object names the scene object it became.
                Assert.Equal(chairId, planned.Single(x => x.GetProperty("role").GetString() == "Magistrate chair").GetProperty("instanceId").GetGuid());
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task APlanMustBeBoundedAndMustSayWhatEachObjectIs()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var modelId = await ImportModelAsync(client, ModelFixtures.AsymmetricBlock(), "block.glb");
        var (referenceId, hash) = await ImportReferenceAsync(client, TinyPng, "court.png");

        Assert.Equal("empty_plan", await CodeAsync(client, Plan(referenceId, hash, [], "empty")));
        Assert.Equal("plan_too_large", await CodeAsync(client, Plan(referenceId, hash,
            [.. Enumerable.Range(0, 13).Select(index => Placeholder($"Object {index}"))], "too-large")));

        // An object is a library match or a stand-in, never both and never neither.
        Assert.Equal("invalid_placeholder", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Confused", matchAssetId = modelId, shape = "Box", size = new[] { 1d, 1d, 1d } }], "both")));
        Assert.Equal("invalid_placeholder", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Nothing at all" }], "neither")));
        Assert.Equal("invalid_placeholder", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Unknown shape", shape = "Torus", size = new[] { 1d, 1d, 1d } }], "shape")));
        Assert.Equal("invalid_placeholder", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Collapsed", shape = "Box", size = new[] { 0d, 1d, 1d } }], "size")));

        // A match has to be a model this project actually holds.
        Assert.Equal("match_not_found", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Ghost", matchAssetId = Guid.NewGuid() }], "ghost")));
        Assert.Equal("match_not_found", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Picture", matchAssetId = referenceId }], "picture")));

        // Confidence is a stated vocabulary, not free text.
        Assert.Equal("invalid_confidence", await CodeAsync(client, Plan(referenceId, hash,
            [new { role = "Vague", shape = "Box", size = new[] { 1d, 1d, 1d }, confidence = "probably" }], "confidence")));

        // The reference is a picture, not a model.
        Assert.Equal("reference_not_an_image", await CodeAsync(client, Plan(modelId, hash,
            [Placeholder("Object")], "not-an-image")));
        Assert.Equal("reference_not_found", await CodeAsync(client, Plan(Guid.NewGuid(), hash,
            [Placeholder("Object")], "no-reference")));

        // Nothing was built by any of those refusals.
        using var scenes = await client.GetFromJsonAsync<JsonDocument>("/api/scenes") ?? throw new InvalidOperationException();
        Assert.Empty(scenes.RootElement.EnumerateArray().ToArray());
        using var plans = await client.GetFromJsonAsync<JsonDocument>("/api/scene-blockouts") ?? throw new InvalidOperationException();
        Assert.Empty(plans.RootElement.EnumerateArray().ToArray());
    }

    [Fact]
    public async Task APlanReadFromAReferenceThatHasSinceChangedIsRefused()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (referenceId, hash) = await ImportReferenceAsync(client, TinyPng, "court.png");

        Assert.Equal("stale_reference", await CodeAsync(client, Plan(referenceId, "not-the-hash-it-was-read-from", [Placeholder("Figure")], "stale")));

        using var accepted = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", Plan(referenceId, hash, [Placeholder("Figure")], "replayable"));
        Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean());
        var planId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        // The same key replays the same plan rather than staging a second one.
        using var replay = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", Plan(referenceId, hash, [Placeholder("Figure")], "replayable"));
        Assert.Equal("blockout_replayed", replay.RootElement.GetProperty("code").GetString());
        Assert.Equal(planId, replay.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        using var plans = await client.GetFromJsonAsync<JsonDocument>($"/api/scene-blockouts?referenceAssetId={referenceId}") ?? throw new InvalidOperationException();
        Assert.Single(plans.RootElement.EnumerateArray().ToArray());
    }

    [Fact]
    public async Task ApprovalIsOnceOnlyAndARejectedPlanBuildsNothing()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (referenceId, hash) = await ImportReferenceAsync(client, TinyPng, "court.png");

        using var first = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", Plan(referenceId, hash, [Placeholder("Figure")], "approve-once"));
        var approvedId = first.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var second = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", Plan(referenceId, hash, [Placeholder("Crate")], "rejected"));
        var rejectedId = second.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var built = await client.PostAsJsonAsync($"/api/scene-blockouts/{approvedId}/apply", new { sceneName = "Court" });
        Assert.Equal(HttpStatusCode.OK, built.StatusCode);
        using var scene = await built.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var sceneId = scene.RootElement.GetProperty("id").GetGuid();

        // Approving again hands back the scene that already exists.
        var again = await client.PostAsJsonAsync($"/api/scene-blockouts/{approvedId}/apply", new { sceneName = "Court" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var repeat = await again.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Equal(sceneId, repeat.RootElement.GetProperty("id").GetGuid());
        Assert.Single(repeat.RootElement.GetProperty("instances").EnumerateArray().ToArray());

        // An approved plan cannot be withdrawn after it built something.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/scene-blockouts/{approvedId}/reject", null)).StatusCode);

        var rejected = await client.PostAsync($"/api/scene-blockouts/{rejectedId}/reject", null);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/scene-blockouts/{rejectedId}/apply", new { sceneName = "Never" })).StatusCode);

        // Exactly one scene exists: the one the artist approved.
        using var scenes = await client.GetFromJsonAsync<JsonDocument>("/api/scenes") ?? throw new InvalidOperationException();
        Assert.Single(scenes.RootElement.EnumerateArray().ToArray());
    }

    [Fact]
    public async Task AnotherProjectCannotSeeOrApproveThisProjectsPlan()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (referenceId, hash) = await ImportReferenceAsync(client, TinyPng, "court.png");
        using var proposed = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", Plan(referenceId, hash, [Placeholder("Figure")], "scoped"));
        var planId = proposed.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var project = await client.PostAsJsonAsync("/api/projects", new { name = "Other production", production = "Scenes", sequenceCode = "SCN-09", sequenceName = "Empty sequence", framesPerSecond = 24, aspectRatio = "2.40:1", deliveryWidth = 2304, deliveryHeight = 960 });
        project.EnsureSuccessStatusCode();
        using var projectJson = await project.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        (await client.PostAsync($"/api/projects/{projectJson.RootElement.GetProperty("id").GetGuid()}/activate", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/scene-blockouts/{planId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/scene-blockouts/{planId}/apply", new { sceneName = "Hijacked" })).StatusCode);
        using var plans = await client.GetFromJsonAsync<JsonDocument>("/api/scene-blockouts") ?? throw new InvalidOperationException();
        Assert.Empty(plans.RootElement.EnumerateArray().ToArray());
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static object Placeholder(string role) =>
        new { role, shape = "Box", size = new[] { 1d, 1d, 1d } };

    private static object Plan(Guid referenceAssetId, string hash, object[] items, string key) => new
    {
        referenceAssetId, observedReferenceHash = hash,
        title = "Court blockout", summary = "What the reference seems to call for.",
        items, idempotencyKey = key,
    };

    private static async Task<string?> CodeAsync(HttpClient client, object body)
    {
        using var envelope = await PostEnvelopeAsync(client, "/api/webmcp/scene-blockouts", body);
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        return envelope.RootElement.GetProperty("code").GetString();
    }

    private static double[] Numbers(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(x => x.GetDouble())];

    private static async Task<JsonDocument> PostEnvelopeAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
    }

    private static async Task<(Guid Id, string ContentHash)> ImportReferenceAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var asset = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return (asset.RootElement.GetProperty("id").GetGuid(), asset.RootElement.GetProperty("contentHash").GetString()!);
    }

    private static async Task<Guid> ImportModelAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var asset = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return asset.RootElement.GetProperty("id").GetGuid();
    }
}
