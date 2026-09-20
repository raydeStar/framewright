using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// One clip, two characters, and two sets of playback settings. Scrubbing to a
/// known time has one right answer, a clip for another skeleton is refused
/// before anything plays, a rigid part turns about the pivot it declared, and a
/// static prop stays exactly where it was put.
/// </summary>
public sealed class SceneMotionApiTests
{
    [Fact]
    public async Task TwoCharactersShareOneClipWithTheirOwnPlaybackSettingsAcrossARestart()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-motion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid sceneId, leadId = Guid.NewGuid(), doubleId = Guid.NewGuid();
            double[] leadHandAtHalf;

            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var characterId = await ImportAsync(client, ModelFixtures.RiggedFigure(), "rigged-figure.glb");
                var clipId = await ImportAsync(client, ModelFixtures.ClipArmRaise(), "clip-arm-raise.glb");
                sceneId = await CreateSceneAsync(client, "Two doubles");

                // The clip is read from the file, not declared by the caller.
                using var clipProfile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{clipId}/model-profile") ?? throw new InvalidOperationException();
                var clips = clipProfile.RootElement.GetProperty("clips").EnumerateArray().ToArray();
                Assert.Equal(2, clips.Length);
                var armRaise = clips.Single(clip => clip.GetProperty("name").GetString() == "Arm raise");
                Assert.Equal(2, armRaise.GetProperty("duration").GetDouble(), 4);
                Assert.True(armRaise.GetProperty("supported").GetBoolean());
                Assert.False(armRaise.GetProperty("movesRoot").GetBoolean());
                Assert.True(clips.Single(clip => clip.GetProperty("name").GetString() == "Step forward").GetProperty("movesRoot").GetBoolean());

                // One clip, two objects, two sets of settings.
                var saved = await SaveAsync(client, sceneId, 1, [
                    Instance(leadId, characterId, "Lead", [-2, 0, 0], Clip(clipId, "Arm raise", 0, 2, 1, 0, false)),
                    Instance(doubleId, characterId, "Double", [2, 0, 0], Clip(clipId, "Arm raise", 0.5, 1.5, 2, 0.5, true)),
                ]);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

                // Scrubbing to a known time has one right answer. At one second
                // the arm is half way through its quarter turn.
                using var half = await SampleAsync(client, sceneId, leadId, 1);
                Assert.Equal("Character", half.RootElement.GetProperty("kind").GetString());
                leadHandAtHalf = Numbers(Joint(half, "LeftHand"), "position");
                var restHand = Numbers(Joint(half, "LeftHand"), "restPosition");
                Assert.NotEqual(restHand, leadHandAtHalf);
                // The other arm never moves: this clip touches one bone.
                Assert.Equal(Numbers(Joint(half, "RightHand"), "restPosition"), Numbers(Joint(half, "RightHand"), "position"));

                // At the end of the clip the hand is directly above the shoulder
                // it hangs from, which is what a quarter turn about Z means.
                using var end = await SampleAsync(client, sceneId, leadId, 2);
                var raised = Numbers(Joint(end, "LeftHand"), "position");
                Assert.True(raised[1] > Numbers(Joint(end, "Chest"), "position")[1]);
                Assert.Equal(Numbers(Joint(end, "LeftUpperArm"), "position")[0], raised[0], 3);

                // The double is trimmed, sped, and looping, so the same wall
                // time is a different place in the same clip.
                using var doubleHalf = await SampleAsync(client, sceneId, doubleId, 1);
                Assert.NotEqual(leadHandAtHalf, Numbers(Joint(doubleHalf, "LeftHand"), "position"));
                // Looping is what makes a time past the trim an ordinary answer
                // rather than a refusal.
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/scenes/{sceneId}/instances/{doubleId}/motion-sample?time=9")).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/scenes/{sceneId}/instances/{leadId}/motion-sample?time=9")).StatusCode);
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
                var instances = scene.RootElement.GetProperty("instances").EnumerateArray().ToArray();
                var lead = instances.Single(instance => instance.GetProperty("id").GetGuid() == leadId).GetProperty("clip");
                var stunt = instances.Single(instance => instance.GetProperty("id").GetGuid() == doubleId).GetProperty("clip");

                // Bindings and timing reopen exactly as they were saved.
                Assert.Equal("Arm raise", lead.GetProperty("clipName").GetString());
                Assert.Equal(1, lead.GetProperty("speed").GetDouble(), 4);
                Assert.False(lead.GetProperty("loop").GetBoolean());
                Assert.Equal(0.5, stunt.GetProperty("start").GetDouble(), 4);
                Assert.Equal(1.5, stunt.GetProperty("end").GetDouble(), 4);
                Assert.Equal(2, stunt.GetProperty("speed").GetDouble(), 4);
                Assert.True(stunt.GetProperty("loop").GetBoolean());
                Assert.Equal("Hold", stunt.GetProperty("rootMotion").GetString());

                // And the same scrub gives the same pose it did before.
                using var half = await SampleAsync(client, sceneId, leadId, 1);
                Assert.Equal(leadHandAtHalf, Numbers(Joint(half, "LeftHand"), "position"));
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task RootMotionReachesTheSceneExactlyOnceUnderTheStatedPolicy()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var characterId = await ImportAsync(client, ModelFixtures.RiggedFigure(), "rigged-figure.glb");
        var clipId = await ImportAsync(client, ModelFixtures.ClipArmRaise(), "clip-arm-raise.glb");
        var sceneId = await CreateSceneAsync(client, "Root motion");
        var holdId = Guid.NewGuid();
        var offsetId = Guid.NewGuid();

        var saved = await SaveAsync(client, sceneId, 1, [
            Instance(holdId, characterId, "Held", [0, 0, 0], Clip(clipId, "Step forward", 0, 2, 1, 0, false, "Hold")),
            Instance(offsetId, characterId, "Travelling", [0, 0, 0], Clip(clipId, "Step forward", 0, 2, 1, 0, false, "Offset")),
        ]);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Hold keeps the character where the artist put it. The hips stay at
        // their rest position and the object does not move.
        using var held = await SampleAsync(client, sceneId, holdId, 2);
        Assert.Equal([0d, 0d, 0d], Numbers(held.RootElement, "rootOffset"));
        Assert.Equal([0d, 0d, 0d], Numbers(held.RootElement, "position"));
        Assert.Equal(Numbers(Joint(held, "Hips"), "restPosition"), Numbers(Joint(held, "Hips"), "position"));

        // Offset reports the same movement once, as an offset to the object,
        // and takes it out of the pose so it is never applied twice.
        using var travelling = await SampleAsync(client, sceneId, offsetId, 2);
        Assert.Equal([0d, 0d, 0.6d], Numbers(travelling.RootElement, "rootOffset"));
        Assert.Equal([0d, 0d, 0.6d], Numbers(travelling.RootElement, "position"));
        Assert.Equal(Numbers(Joint(travelling, "Hips"), "restPosition"), Numbers(Joint(travelling, "Hips"), "position"));
        Assert.Equal("Offset", travelling.RootElement.GetProperty("rootMotion").GetString());

        // Half way through, half the movement, once.
        using var halfway = await SampleAsync(client, sceneId, offsetId, 1);
        Assert.Equal(0.3, Numbers(halfway.RootElement, "position")[2], 4);
    }

    [Fact]
    public async Task AClipForAnotherSkeletonOrAnImpossibleTrimIsRefusedBeforeAnythingPlays()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var characterId = await ImportAsync(client, ModelFixtures.RiggedFigure(), "rigged-figure.glb");
        var clipId = await ImportAsync(client, ModelFixtures.ClipArmRaise(), "clip-arm-raise.glb");
        var strangerId = await ImportAsync(client, ModelFixtures.ClipWrongSkeleton(), "clip-wrong-skeleton.glb");
        var propId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "Refusals");
        var instanceId = Guid.NewGuid();

        // A clip whose bones this skeleton does not have.
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(strangerId, "Arm raise", 0, 2, 1, 0, false)));
        // A clip name that is not in that file.
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Backflip", 0, 2, 1, 0, false)));
        // Trims and speeds that cannot be played.
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Arm raise", 0, 5, 1, 0, false)));
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Arm raise", 1.5, 1.5, 1, 1.5, false)));
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Arm raise", 0, 2, 40, 0, false)));
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Arm raise", 0, 1, 1, 1.8, false)));
        await Refused(client, sceneId, Instance(instanceId, characterId, "Lead", [0, 0, 0], Clip(clipId, "Arm raise", 0, 2, 1, 0, false, "Whatever")));
        // A model with no skeleton cannot play a clip at all.
        await Refused(client, sceneId, Instance(instanceId, propId, "Crate", [0, 0, 0], Clip(clipId, "Arm raise", 0, 2, 1, 0, false)));

        // Nothing was written by any of those refusals.
        using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        Assert.Equal(1, scene.RootElement.GetProperty("version").GetInt32());
        Assert.Empty(scene.RootElement.GetProperty("instances").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task ARigidPartTurnsAboutItsDeclaredPivotAndAStaticPropDoesNotMove()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var propId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var sceneId = await CreateSceneAsync(client, "A door and a crate");
        var doorId = Guid.NewGuid();
        var crateId = Guid.NewGuid();

        // The door is hinged at its own left edge and swings a quarter turn.
        var saved = await SaveAsync(client, sceneId, 1, [
            Motion(doorId, propId, "Door", [4, 0, 0], new { pivot = new[] { -1d, 0d, 0d }, axis = "Y", fromRadians = 0d, toRadians = 1.5707963268, seconds = 2d, pingPong = false }),
            Instance(crateId, propId, "Crate", [0, 0, 0], null),
        ]);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var shut = await SampleAsync(client, sceneId, doorId, 0);
        Assert.Equal("RigidPart", shut.RootElement.GetProperty("kind").GetString());
        Assert.Equal([4d, 0d, 0d], Numbers(shut.RootElement, "position"));

        using var open = await SampleAsync(client, sceneId, doorId, 2);
        var position = Numbers(open.RootElement, "position");
        var rotation = Numbers(open.RootElement, "rotation");
        Assert.Equal(1.5708, rotation[1], 3);
        Assert.NotEqual([4d, 0d, 0d], position);

        // The hinge is the one point that does not move: the pivot in world
        // terms is where it was before the swing.
        var hinge = position[0] + Math.Cos(rotation[1]) * -1 + Math.Sin(rotation[1]) * 0;
        var hingeZ = position[2] - Math.Sin(rotation[1]) * -1;
        Assert.Equal(3d, hinge, 3);
        Assert.Equal(0d, hingeZ, 3);

        // A static prop has no motion and reports the placement it was given,
        // at every time anyone asks about.
        using var crate = await SampleAsync(client, sceneId, crateId, 1.75);
        Assert.Equal("Static", crate.RootElement.GetProperty("kind").GetString());
        Assert.Equal([0d, 0d, 0d], Numbers(crate.RootElement, "position"));

        // A time beyond the declared duration, a pivot outside the world, an
        // axis that is not an axis, and a swing that ends where it began are
        // each refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/scenes/{sceneId}/instances/{doorId}/motion-sample?time=5")).StatusCode);
        await Refused(client, sceneId, Motion(Guid.NewGuid(), propId, "Bad axis", [0, 0, 0], new { pivot = new[] { 0d, 0d, 0d }, axis = "W", fromRadians = 0d, toRadians = 1d, seconds = 1d, pingPong = false }), 2);
        await Refused(client, sceneId, Motion(Guid.NewGuid(), propId, "Still", [0, 0, 0], new { pivot = new[] { 0d, 0d, 0d }, axis = "Y", fromRadians = 1d, toRadians = 1d, seconds = 1d, pingPong = false }), 2);
        await Refused(client, sceneId, Motion(Guid.NewGuid(), propId, "Forever", [0, 0, 0], new { pivot = new[] { 0d, 0d, 0d }, axis = "Y", fromRadians = 0d, toRadians = 1d, seconds = 0d, pingPong = false }), 2);
    }

    private static JsonElement Joint(JsonDocument sample, string bone) =>
        sample.RootElement.GetProperty("joints").EnumerateArray().Single(joint => joint.GetProperty("bone").GetString() == bone);

    private static double[] Numbers(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(x => x.GetDouble())];

    private static object Clip(Guid clipAssetId, string clipName, double start, double end, double speed, double time, bool loop, string rootMotion = "Hold") =>
        new { clipAssetId, clipName, start, end, speed, time, loop, rootMotion };

    private static object Instance(Guid id, Guid assetId, string name, double[] position, object? clip) =>
        new { id, assetId, name, position, rotation = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d }, clip };

    private static object Motion(Guid id, Guid assetId, string name, double[] position, object motion) =>
        new { id, assetId, name, position, rotation = new[] { 0d, 0d, 0d }, scale = new[] { 1d, 1d, 1d }, motion };

    private static async Task Refused(HttpClient client, Guid sceneId, object instance, int expectedVersion = 1)
    {
        var response = await SaveAsync(client, sceneId, expectedVersion, [instance]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid sceneId, int expectedVersion, object[] instances) =>
        client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion, name = "Motion scene",
            camera = new { yaw = 0.9, pitch = 0.4, distance = 8d, target = new[] { 0d, 1d, 0d }, fieldOfView = 38d },
            environment = new { keyIntensity = 2.2, keyYaw = 0.8, keyPitch = 0.9, ambientIntensity = 1.4 },
            instances,
        });

    private static async Task<JsonDocument> SampleAsync(HttpClient client, Guid sceneId, Guid instanceId, double time)
    {
        var response = await client.GetAsync($"/api/scenes/{sceneId}/instances/{instanceId}/motion-sample?time={time.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
    }

    private static async Task<Guid> CreateSceneAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/scenes", new { name });
        response.EnsureSuccessStatusCode();
        using var scene = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return scene.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ImportAsync(HttpClient client, byte[] bytes, string fileName)
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
