using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// An agent's blockout plan can propose the same stand-ins the artist places by
/// hand: a person holding a pose, and a card showing one of the project's own
/// pictures. Staging still builds nothing, a card can only cite an image in the
/// active project, and what Build makes is indistinguishable from the
/// hand-placed equivalent through a restart and a working package.
/// </summary>
public sealed class SceneBlockoutStandInTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly SceneCameraSummary Camera = new(0.9, 0.42, 6, [0, 0.5, 0], 38);
    private static readonly SceneEnvironmentSummary Lighting = new(2.2, 0.8, 0.9, 1.4);

    [Fact]
    public async Task APlannedSeatedPersonAndImageCardBuildLikeHandPlacedOnesThroughARestartAndAPackage()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-blockout-standins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid builtId, handId, planId, backdropId;
            string backdropHash;
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var reference = await AssetUploads.ImageAsync(client, "tavern-reference.png");
                var backdrop = await AssetUploads.ImageAsync(client, "tavern-backdrop.png");
                backdropId = backdrop.Id;
                backdropHash = backdrop.ContentHash;

                var staged = await ProposeAsync(client, Plan(reference, "tavern-people-and-cards",
                    new { role = "Seated drinker", shape = "Person", size = (double[])[0.5, 1.75, 0.3], pose = "Seated", position = (double[])[0.4, 0.875, 0d], confidence = "Approximate" },
                    new { role = "Back wall picture", shape = "Card", size = (double[])[4d, 2d, 0.01], imageAssetId = backdrop.Id, position = (double[])[0d, 1d, -3d], confidence = "Certain" },
                    new { role = "Barkeep", shape = "Person", size = (double[])[0.5, 1.8, 0.3], position = (double[])[-1d, 0.9, -1d] }));
                planId = staged.Id;

                // The plan shows exactly what Build will make: the pose, a
                // person planned without one standing in Neutral, and the
                // picture by id, URL, and content hash.
                var drinkerPlan = staged.Items.Single(x => x.Role == "Seated drinker").Placeholder!;
                Assert.Equal("Seated", drinkerPlan.Pose);
                Assert.Equal("Neutral", staged.Items.Single(x => x.Role == "Barkeep").Placeholder!.Pose);
                var cardPlan = staged.Items.Single(x => x.Role == "Back wall picture").Placeholder!;
                Assert.Equal(backdrop.Id, cardPlan.ImageAssetId);
                Assert.Equal(backdrop.ContentHash, cardPlan.ImageContentHash);
                Assert.Equal($"/api/assets/{backdrop.Id}/content", cardPlan.ImageUrl);
                Assert.Null(cardPlan.Pose);

                // Staging built nothing and dispatched nothing.
                Assert.Empty((await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes"))!);
                using (var probe = factory.Services.CreateScope())
                {
                    var db = probe.ServiceProvider.GetRequiredService<StudioDbContext>();
                    Assert.Equal(0, await db.SceneInstances.IgnoreQueryFilters().CountAsync());
                    Assert.Equal(0, await db.Jobs.IgnoreQueryFilters().CountAsync());
                }

                var applied = await client.PostAsJsonAsync($"/api/scene-blockouts/{planId}/apply", new { sceneName = "Tavern from plan" });
                Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
                var built = (await applied.Content.ReadFromJsonAsync<SceneSummary>())!;
                builtId = built.Id;

                // The same stand-ins placed by hand, saved through the ordinary save.
                var created = await client.PostAsJsonAsync("/api/scenes", new { name = "Tavern by hand" });
                created.EnsureSuccessStatusCode();
                handId = (await created.Content.ReadFromJsonAsync<SceneSummary>())!.Id;
                (await client.PutAsJsonAsync($"/api/scenes/{handId}", new SaveSceneRequest(1, "Tavern by hand", Camera, Lighting,
                [
                    new(Guid.NewGuid(), null, "Seated drinker", [0.4, 0.875, 0], [0, 0, 0], [1, 1, 1], new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Seated")),
                    new(Guid.NewGuid(), null, "Back wall picture", [0, 1, -3], [0, 0, 0], [1, 1, 1], new ScenePlaceholderSummary("Card", [4, 2, 0.01], ImageAssetId: backdrop.Id)),
                    new(Guid.NewGuid(), null, "Barkeep", [-1, 0.9, -1], [0, 0, 0], [1, 1, 1], new ScenePlaceholderSummary("Person", [0.5, 1.8, 0.3])),
                ]))).EnsureSuccessStatusCode();

                // Library usage counts the built card as a use of the picture,
                // as it does the hand-placed one, and the staged plan did not.
                var usage = (await client.GetFromJsonAsync<AssetUsageSummary[]>("/api/assets/usage"))!;
                Assert.Equal(2, Assert.Single(usage, x => x.AssetId == backdrop.Id).Scenes);
            }

            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                var built = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{builtId}"))!;
                var hand = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{handId}"))!;
                Assert.Equal(3, built.Instances.Length);
                foreach (var planned in built.Instances)
                {
                    var placed = Assert.Single(hand.Instances, x => x.Name == planned.Name);
                    // Byte-for-byte the same stand-in description, pose and picture included.
                    Assert.Equal(JsonSerializer.Serialize(placed.Placeholder, Web), JsonSerializer.Serialize(planned.Placeholder, Web));
                    Assert.Equal(placed.Position, planned.Position);
                    Assert.Equal(planId, planned.PlanId);
                    Assert.Null(placed.PlanId);
                }
                Assert.Equal("Seated", built.Instances.Single(x => x.Name == "Seated drinker").Placeholder!.Pose);
                Assert.Equal(backdropHash, built.Instances.Single(x => x.Name == "Back wall picture").Placeholder!.ImageContentHash);

                // The applied plan still says what it proposed.
                var plan = (await client.GetFromJsonAsync<SceneBlockoutPlanSummary>($"/api/scenes/{builtId}/blockout", Web))!;
                Assert.Equal("Seated", plan.Items.Single(x => x.Role == "Seated drinker").Placeholder!.Pose);
                Assert.Equal(backdropId, plan.Items.Single(x => x.Role == "Back wall picture").Placeholder!.ImageAssetId);

                // A working package carries the plan's card to the new project's
                // own copy of the picture, the same copy the built card shows.
                var package = await client.GetByteArrayAsync("/api/export/working-package");
                var content = new MultipartFormDataContent();
                var file = new ByteArrayContent(package);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Add(file, "file", "tavern.zip");
                var imported = await client.PostAsync("/api/projects/import", content);
                Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
                var summary = (await imported.Content.ReadFromJsonAsync<PortableProjectImportSummary>())!;
                (await client.PostAsync($"/api/projects/{summary.ProjectId}/activate", null)).EnsureSuccessStatusCode();

                var scenes = (await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes"))!;
                var copyId = Assert.Single(scenes, x => x.Name == "Tavern from plan").Id;
                var copy = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{copyId}"))!;
                var copiedCard = copy.Instances.Single(x => x.Name == "Back wall picture").Placeholder!;
                var copiedPlan = (await client.GetFromJsonAsync<SceneBlockoutPlanSummary>($"/api/scenes/{copyId}/blockout", Web))!;
                var plannedCard = copiedPlan.Items.Single(x => x.Role == "Back wall picture").Placeholder!;
                Assert.NotEqual(backdropId, plannedCard.ImageAssetId);
                Assert.Equal(copiedCard.ImageAssetId, plannedCard.ImageAssetId);
                Assert.Equal(backdropHash, plannedCard.ImageContentHash);
                Assert.Equal("Seated", copiedPlan.Items.Single(x => x.Role == "Seated drinker").Placeholder!.Pose);
                var assets = (await client.GetFromJsonAsync<AssetSummary[]>("/api/assets"))!;
                Assert.Contains(assets, x => x.Id == plannedCard.ImageAssetId && x.ProjectId == summary.ProjectId);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task APlannedCardMustShowAnImageInThisProjectAndAPlannedPoseMustBeKnown()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var reference = await AssetUploads.ImageAsync(client, "refusals-reference.png");
        var image = await AssetUploads.ImageAsync(client, "refusals-picture.png");
        var model = await AssetUploads.ModelAsync(client, ModelFixtures.AsymmetricBlock(), "refusals-model.glb");

        var original = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Project.Id;
        var otherProject = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            "Other production", "Isolation", "OT-02", "Other sequence", 24, "2.39:1", 2304, 960));
        otherProject.EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/projects/{(await otherProject.Content.ReadFromJsonAsync<ProjectSummary>())!.Id}/activate", null)).EnsureSuccessStatusCode();
        var foreign = await AssetUploads.ImageAsync(client, "refusals-foreign.png");
        (await client.PostAsync($"/api/projects/{original}/activate", null)).EnsureSuccessStatusCode();

        // Each refusal sits behind a valid first object, so a partial write
        // would show up as a staged plan or a staged item.
        var valid = new { role = "Lead", shape = "Person", size = (double[])[0.5, 1.75, 0.3], pose = "Walking" };
        var refusals = new (string Code, object Item)[]
        {
            ("invalid_placeholder", new { role = "Blank card", shape = "Card", size = (double[])[4d, 2d, 0.01] }),
            ("card_image_not_found", new { role = "Model card", shape = "Card", size = (double[])[4d, 2d, 0.01], imageAssetId = model.Id }),
            ("card_image_not_found", new { role = "Ghost card", shape = "Card", size = (double[])[4d, 2d, 0.01], imageAssetId = Guid.NewGuid() }),
            ("card_image_not_found", new { role = "Foreign card", shape = "Card", size = (double[])[4d, 2d, 0.01], imageAssetId = foreign.Id }),
            ("invalid_placeholder", new { role = "Picture box", shape = "Box", size = (double[])[1d, 1d, 1d], imageAssetId = image.Id }),
            ("invalid_placeholder", new { role = "Posed box", shape = "Box", size = (double[])[1d, 1d, 1d], pose = "Seated" }),
            ("invalid_placeholder", new { role = "Dancer", shape = "Person", size = (double[])[0.5, 1.75, 0.3], pose = "Dancing" }),
            ("invalid_placeholder", new { role = "Posed match", matchAssetId = model.Id, pose = "Seated" }),
            ("invalid_placeholder", new { role = "Shapeless pose", pose = "Seated" }),
        };
        foreach (var (code, item) in refusals)
            Assert.Equal(code, await RefusedAsync(client, Plan(reference, $"refused-{Guid.NewGuid():N}", valid, item)));

        Assert.Empty((await client.GetFromJsonAsync<SceneBlockoutPlanSummary[]>("/api/scene-blockouts", Web))!);
        using var probe = factory.Services.CreateScope();
        var db = probe.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Equal(0, await db.SceneBlockoutItems.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.SceneBlockoutPlans.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task APlanWhoseCardPictureHasLeftTheProjectBuildsNothing()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var reference = await AssetUploads.ImageAsync(client, "vanish-reference.png");
        var image = await AssetUploads.ImageAsync(client, "vanish-picture.png");
        var staged = await ProposeAsync(client, Plan(reference, "vanishing-card",
            new { role = "Lead", shape = "Person", size = (double[])[0.5, 1.75, 0.3], pose = "Seated" },
            new { role = "Backdrop", shape = "Card", size = (double[])[4d, 2d, 0.01], imageAssetId = image.Id }));

        // Nothing in the application removes a library picture, so the row is
        // taken away underneath the plan, as a damaged or hand-edited database would.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            Assert.Equal(1, await db.Assets.IgnoreQueryFilters().Where(x => x.Id == image.Id).ExecuteDeleteAsync());
        }

        var refused = await client.PostAsJsonAsync($"/api/scene-blockouts/{staged.Id}/apply", new { sceneName = "Never built" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes"))!);
        var plan = (await client.GetFromJsonAsync<SceneBlockoutPlanSummary>($"/api/scene-blockouts/{staged.Id}", Web))!;
        Assert.Equal("Pending", plan.State);
        Assert.All(plan.Items, item => Assert.Null(item.InstanceId));
        // The plan still says which picture it wanted, and that it is gone.
        var card = plan.Items.Single(x => x.Role == "Backdrop").Placeholder!;
        Assert.Equal(image.Id, card.ImageAssetId);
        Assert.Null(card.ImageUrl);
    }

    private static object Plan(AssetSummary reference, string key, params object[] items) => new
    {
        referenceAssetId = reference.Id, observedReferenceHash = reference.ContentHash,
        title = "Tavern blockout", summary = "People and pictures read off the tavern reference.",
        items, idempotencyKey = key,
    };

    private static async Task<SceneBlockoutPlanSummary> ProposeAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/webmcp/scene-blockouts", body);
        response.EnsureSuccessStatusCode();
        using var envelope = (await response.Content.ReadFromJsonAsync<JsonDocument>())!;
        Assert.True(envelope.RootElement.GetProperty("ok").GetBoolean(), envelope.RootElement.GetProperty("message").GetString());
        return envelope.RootElement.GetProperty("data").Deserialize<SceneBlockoutPlanSummary>(Web)!;
    }

    private static async Task<string?> RefusedAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/webmcp/scene-blockouts", body);
        response.EnsureSuccessStatusCode();
        using var envelope = (await response.Content.ReadFromJsonAsync<JsonDocument>())!;
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        return envelope.RootElement.GetProperty("code").GetString();
    }
}
