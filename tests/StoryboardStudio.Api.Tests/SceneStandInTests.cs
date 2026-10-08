using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Hand-placed stand-ins, including a posable person. A pose is part of the
/// scene: it saves with the scene, survives a restart and a working package,
/// and a pose the studio does not know is refused before anything is written.
/// </summary>
public sealed class SceneStandInTests
{
    private static readonly SceneCameraSummary Camera = new(0.9, 0.42, 6, [0, 0.5, 0], 38);
    private static readonly SceneEnvironmentSummary Lighting = new(2.2, 0.8, 0.9, 1.4);

    [Fact]
    public async Task APersonKeepsItsPoseThroughARestartAndAWorkingPackage()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-stand-ins", Guid.NewGuid().ToString("N"));
        Guid sceneId;
        var seated = Guid.NewGuid();
        var unposed = Guid.NewGuid();
        var box = Guid.NewGuid();
        try
        {
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                sceneId = await CreateSceneAsync(client, "Council chamber");
                var saved = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(1, "Council chamber", Camera, Lighting,
                [
                    Stand(seated, "Magistrate", new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Seated")),
                    // A person saved without a pose stands in Neutral.
                    Stand(unposed, "Guard", new ScenePlaceholderSummary("Person", [0.5, 1.9, 0.3])),
                    Stand(box, "Desk", new ScenePlaceholderSummary("Box", [1.6, 0.8, 0.8])),
                ]));
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            }

            using (var restarted = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = restarted.CreateClient())
            {
                var scene = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{sceneId}"))!;
                Assert.Equal("Seated", Placeholder(scene, seated).Pose);
                Assert.Equal("Person", Placeholder(scene, seated).Shape);
                Assert.Equal("Neutral", Placeholder(scene, unposed).Pose);
                Assert.Equal(1.9, Placeholder(scene, unposed).Size[1]);
                Assert.Null(Placeholder(scene, box).Pose);
                // Other shapes carry no pose field at all, so their JSON, and any
                // snapshot frozen from it, reads exactly as before people existed.
                using var raw = (await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}"))!;
                var desk = raw.RootElement.GetProperty("instances").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "Desk");
                Assert.False(desk.GetProperty("placeholder").TryGetProperty("pose", out _));

                // The pose travels in the editable package and lands in a new
                // project with fresh identities.
                var package = await client.GetByteArrayAsync("/api/export/working-package");
                var content = new MultipartFormDataContent();
                var file = new ByteArrayContent(package);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Add(file, "file", "council-chamber.zip");
                var imported = await client.PostAsync("/api/projects/import", content);
                Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
                var summary = (await imported.Content.ReadFromJsonAsync<PortableProjectImportSummary>())!;
                (await client.PostAsync($"/api/projects/{summary.ProjectId}/activate", null)).EnsureSuccessStatusCode();
                var scenes = (await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes"))!;
                var copy = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{Assert.Single(scenes, x => x.Name == "Council chamber").Id}"))!;
                Assert.NotEqual(sceneId, copy.Id);
                var magistrate = Assert.Single(copy.Instances, x => x.Name == "Magistrate");
                Assert.NotEqual(seated, magistrate.Id);
                Assert.Equal("Seated", magistrate.Placeholder!.Pose);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task APoseTheStudioDoesNotKnowOrOneOnAnotherShapeIsRefusedWithoutWriting()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var sceneId = await CreateSceneAsync(client, "Refusals");
        var person = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(1, "Refusals", Camera, Lighting,
            [Stand(person, "Walker", new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Walking"))]))).EnsureSuccessStatusCode();

        foreach (var invalid in new[]
        {
            new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Dancing"),
            new ScenePlaceholderSummary("Box", [1, 1, 1], "Seated"),
            new ScenePlaceholderSummary("Person", [0.5, 0, 0.3], "Seated"),
        })
        {
            var refused = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(2, "Refusals", Camera, Lighting,
                [Stand(person, "Walker", invalid)]));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        var scene = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{sceneId}"))!;
        Assert.Equal(2, scene.Version);
        Assert.Equal("Walking", Placeholder(scene, person).Pose);
    }

    [Fact]
    public async Task AStillFreezesThePoseItWasRenderedWithEvenAfterThePoseChanges()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var studio = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!;
        var shotResponse = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SC-901", "Seated magistrate", "A posed stand-in becomes a review frame.", 48, "Unbound", "Hold.", [], []));
        shotResponse.EnsureSuccessStatusCode();
        var shot = (await shotResponse.Content.ReadFromJsonAsync<ShotSummary>())!;
        var sceneId = await CreateSceneAsync(client, "Pose freeze");
        var person = Guid.NewGuid();
        (await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(1, "Pose freeze", Camera, Lighting,
            [Stand(person, "Magistrate", new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Seated"))]))).EnsureSuccessStatusCode();

        using var content = new MultipartFormDataContent();
        var png = new byte[25];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), studio.Project.DeliveryWidth);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), studio.Project.DeliveryHeight);
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "scene-still.png");
        content.Add(new StringContent(shot.Id.ToString()), "shotId");
        content.Add(new StringContent("2"), "expectedSceneVersion");
        content.Add(new StringContent(shot.Version.ToString(CultureInfo.InvariantCulture)), "expectedShotVersion");
        content.Add(new StringContent("0"), "startTime");
        content.Add(new StringContent((48d / studio.Project.FramesPerSecond).ToString(CultureInfo.InvariantCulture)), "endTime");
        content.Add(new StringContent("0"), "stillTime");
        content.Add(new StringContent(JsonSerializer.Serialize(Camera)), "camera");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenes/{sceneId}/shot-stills") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var rendered = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, rendered.StatusCode);
        var binding = (await rendered.Content.ReadFromJsonAsync<SceneShotBindingSummary>())!;

        // The artist then stands the magistrate up. The still keeps citing the
        // seated figure it was actually rendered from.
        (await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new SaveSceneRequest(2, "Pose freeze", Camera, Lighting,
            [Stand(person, "Magistrate", new ScenePlaceholderSummary("Person", [0.5, 1.75, 0.3], "Neutral"))]))).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var stored = await db.SceneShotBindings.AsNoTracking().SingleAsync(x => x.Id == binding.Id);
        using var snapshot = JsonDocument.Parse(stored.SnapshotJson);
        var frozen = snapshot.RootElement.GetProperty("scene").GetProperty("instances")[0].GetProperty("placeholder");
        Assert.Equal("Person", frozen.GetProperty("shape").GetString());
        Assert.Equal("Seated", frozen.GetProperty("pose").GetString());
        Assert.Equal(binding.SnapshotHash, stored.SnapshotHash);
        var current = (await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{sceneId}"))!;
        Assert.Equal("Neutral", Placeholder(current, person).Pose);
    }

    private static SaveSceneInstanceRequest Stand(Guid id, string name, ScenePlaceholderSummary placeholder) =>
        new(id, null, name, [0, placeholder.Size[1] / 2, 0], [0, 0, 0], [1, 1, 1], placeholder);

    private static ScenePlaceholderSummary Placeholder(SceneSummary scene, Guid instanceId) =>
        Assert.Single(scene.Instances, x => x.Id == instanceId).Placeholder!;

    private static async Task<Guid> CreateSceneAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/scenes", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SceneSummary>())!.Id;
    }
}
