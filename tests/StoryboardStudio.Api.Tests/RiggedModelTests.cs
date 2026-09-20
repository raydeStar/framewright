using StoryboardStudio.Api.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A rig is only ever described as far as it has been checked. A known-good
/// humanoid fixture reports its profile, its skeleton, and its bind data; a
/// skeleton whose bones are named something else is an unknown skeleton rather
/// than an almost-humanoid; a broken skin fails with the reason; and a static
/// prop simply has no skeleton, which is not a failure.
/// </summary>
public sealed class RiggedModelTests
{
    [Fact]
    public void AKnownGoodHumanoidReportsItsProfileSkeletonAndBindData()
    {
        var result = GlbModelInspector.Inspect(ModelFixtures.RiggedFigure());
        Assert.True(result.Ok, result.Error);
        var rig = result.Profile!.Rig;

        Assert.True(rig.HasSkeleton);
        Assert.True(rig.ProfileMatched);
        Assert.Equal("humanoid-a", rig.ProfileId);
        Assert.Empty(rig.MissingBones);
        Assert.Empty(rig.UnexpectedBones);
        Assert.Equal(1, rig.SkinCount);
        Assert.Equal(17, rig.BoneCount);
        Assert.Equal(136, rig.SkinnedVertexCount);
        Assert.Equal(136, rig.SkinWeightsChecked);
        Assert.True(rig.TransformsFinite);
        Assert.True(rig.BindPoseValid);
        Assert.True(rig.SkinWeightsValid);
        Assert.Empty(rig.Findings);
        Assert.True(rig.AnimationReady);

        // The skeleton's own identity, computed the way the Reference Asset
        // Compiler computes it. The compiler's Python asserts the same string
        // for this same file; that agreement is what lets a clip bind to a rig
        // that matches no documented profile.
        Assert.Equal("123be375436770f873a157e54ca00299d417bbb08a79a0abe6d39e0ace223ea7", rig.Fingerprint);

        // The hierarchy is read from the file, not guessed from the names.
        var head = rig.Bones.Single(bone => bone.Name == "Head");
        Assert.Equal("Neck", head.Parent);
        Assert.Equal(4, head.Depth);
        Assert.Null(rig.Bones.Single(bone => bone.Name == "Hips").Parent);

        // Rest positions compose down the hierarchy: the head rests at the sum
        // of every translation above it.
        Assert.Equal([0d, 1.65d, 0d], head.RestWorldPosition);
        // The figure is asymmetric, so a mirrored import could not pass here.
        var leftHand = rig.Bones.Single(bone => bone.Name == "LeftHand");
        var rightHand = rig.Bones.Single(bone => bone.Name == "RightHand");
        Assert.Equal(0.71, leftHand.RestWorldPosition[0], 4);
        Assert.Equal(-0.67, rightHand.RestWorldPosition[0], 4);
    }

    [Fact]
    public void TheFingerprintReadsTheValuesTheFileStoresRatherThanTheRoundedOnes()
    {
        // The bone summaries round to four decimals for display. A fingerprint
        // taken from those would agree with itself and disagree with the
        // compiler, so this moves one bone below that rounding and requires the
        // fingerprint to notice. The fixture's own transforms are exact at four
        // decimals, which is why this case has to be made deliberately.
        var base_ = GlbModelInspector.Inspect(ModelFixtures.RiggedFigure());
        var nudged = GlbModelInspector.Inspect(MutateRig(document =>
        {
            var node = document["nodes"]![6]!;
            var translation = node["translation"]!.AsArray();
            translation[0] = translation[0]!.GetValue<double>() + 0.00001;
        }));

        Assert.True(nudged.Ok, nudged.Error);
        Assert.NotNull(base_.Profile!.Rig.Fingerprint);
        Assert.NotEqual(base_.Profile!.Rig.Fingerprint, nudged.Profile!.Rig.Fingerprint);
        // A tenth of that is below the quantization step and is the same rig.
        var imperceptible = GlbModelInspector.Inspect(MutateRig(document =>
        {
            var node = document["nodes"]![6]!;
            var translation = node["translation"]!.AsArray();
            translation[0] = translation[0]!.GetValue<double>() + 0.0000001;
        }));
        Assert.Equal(base_.Profile!.Rig.Fingerprint, imperceptible.Profile!.Rig.Fingerprint);
    }

    [Fact]
    public void ArbitrarilyNamedBonesAreAnUnknownSkeletonRatherThanAnAlmostHumanoid()
    {
        var result = GlbModelInspector.Inspect(ModelFixtures.RiggedWrongProfile());
        Assert.True(result.Ok, result.Error);
        var rig = result.Profile!.Rig;

        // The skin itself is sound; what fails is the claim that this is a
        // humanoid anything downstream may animate.
        Assert.True(rig.HasSkeleton);
        Assert.True(rig.SkinWeightsValid);
        Assert.True(rig.BindPoseValid);
        Assert.False(rig.ProfileMatched);
        Assert.Equal("unknown", rig.ProfileId);
        Assert.Equal("Unknown skeleton", rig.ProfileName);
        Assert.False(rig.AnimationReady);
        Assert.Equal(17, rig.MissingBones.Length);
        Assert.Contains("Hips", rig.MissingBones);
        Assert.Contains(rig.Findings, finding => finding.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void ABrokenSkinFailsWithTheReasonAndIsNeverAnimationReady()
    {
        var result = GlbModelInspector.Inspect(ModelFixtures.RiggedBrokenSkin());
        Assert.True(result.Ok, result.Error);
        var rig = result.Profile!.Rig;

        // The profile still matches; the skin does not hold up.
        Assert.True(rig.ProfileMatched);
        Assert.False(rig.SkinWeightsValid);
        Assert.False(rig.AnimationReady);
        Assert.Contains(rig.Findings, finding => finding.Contains("weighted to no bone", StringComparison.Ordinal));
    }

    [Fact]
    public void AStaticPropHasNoSkeletonAndThatIsNotAFailure()
    {
        var result = GlbModelInspector.Inspect(ModelFixtures.AsymmetricBlock());
        Assert.True(result.Ok, result.Error);
        var rig = result.Profile!.Rig;

        Assert.False(rig.HasSkeleton);
        Assert.Equal("none", rig.ProfileId);
        Assert.Empty(rig.Bones);
        Assert.Empty(rig.Findings);
        Assert.False(rig.AnimationReady);
    }

    [Fact]
    public void ARigThatCannotBeResolvedIsReportedRatherThanAssumedSound()
    {
        // A joint that is not a node in the file.
        var danglingJoint = MutateRig(document => document["skins"]![0]!["joints"] = new JsonArray(4096));
        var dangling = GlbModelInspector.Inspect(danglingJoint);
        Assert.True(dangling.Ok, dangling.Error);
        Assert.False(dangling.Profile!.Rig.AnimationReady);
        Assert.Contains(dangling.Profile!.Rig.Findings, finding => finding.Contains("not nodes in this file", StringComparison.Ordinal));

        // A bind matrix set that does not match the joint count.
        var shortBind = MutateRig(document => document["accessors"]![4]!["count"] = 3);
        var bind = GlbModelInspector.Inspect(shortBind);
        Assert.True(bind.Ok, bind.Error);
        Assert.False(bind.Profile!.Rig.BindPoseValid);
        Assert.False(bind.Profile!.Rig.AnimationReady);

        // A skin with no bind matrices at all.
        var noBind = MutateRig(document => document["skins"]![0]!.AsObject().Remove("inverseBindMatrices"));
        var missing = GlbModelInspector.Inspect(noBind);
        Assert.True(missing.Ok, missing.Error);
        Assert.False(missing.Profile!.Rig.BindPoseValid);
        Assert.Contains(missing.Profile!.Rig.Findings, finding => finding.Contains("no inverse bind matrices", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARigSurvivesAReopenAndPosesDeterministicallyAgainstItsExactRevision()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-rig", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid assetId, propId;
            string contentHash;
            double[] posedHand;

            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                assetId = await ImportAsync(client, ModelFixtures.RiggedFigure(), "rigged-figure.glb");
                propId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");

                using var profile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{assetId}/model-profile") ?? throw new InvalidOperationException();
                contentHash = profile.RootElement.GetProperty("contentHash").GetString()!;
                var rig = profile.RootElement.GetProperty("rig");
                Assert.True(rig.GetProperty("animationReady").GetBoolean());
                Assert.Equal(17, rig.GetProperty("boneCount").GetInt32());
                Assert.Single(profile.RootElement.GetProperty("materials").EnumerateArray().ToArray());

                // Raising the character's left arm a quarter turn about Z moves
                // the hand, and moves nothing on the other side.
                var posed = await PoseAsync(client, assetId, new { bone = "LeftUpperArm", rotation = new[] { 0d, 0d, 1.5707963268 } });
                Assert.Equal(HttpStatusCode.OK, posed.StatusCode);
                using var pose = await posed.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                Assert.Equal(contentHash, pose.RootElement.GetProperty("contentHash").GetString());
                var joints = pose.RootElement.GetProperty("joints").EnumerateArray().ToArray();
                var leftHand = joints.Single(joint => joint.GetProperty("bone").GetString() == "LeftHand");
                var rightHand = joints.Single(joint => joint.GetProperty("bone").GetString() == "RightHand");
                posedHand = Numbers(leftHand, "position");

                Assert.NotEqual(Numbers(leftHand, "restPosition"), posedHand);
                Assert.Equal(Numbers(rightHand, "restPosition"), Numbers(rightHand, "position"));
                // The arm swung up: the hand is now above the shoulder it hangs from.
                Assert.True(posedHand[1] > joints.Single(joint => joint.GetProperty("bone").GetString() == "Chest").GetProperty("position")[1].GetDouble());

                // A static prop is refused honestly rather than posed as if it had bones.
                var prop = await PoseAsync(client, propId, new { bone = "Hips", rotation = new[] { 0d, 0d, 0d } });
                Assert.Equal(HttpStatusCode.BadRequest, prop.StatusCode);
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = reopened.CreateClient())
            {
                using var profile = await client.GetFromJsonAsync<JsonDocument>($"/api/assets/{assetId}/model-profile") ?? throw new InvalidOperationException();
                var rig = profile.RootElement.GetProperty("rig");

                // Mesh, skeleton, bind data, and materials all reopen as they were.
                Assert.Equal(contentHash, profile.RootElement.GetProperty("contentHash").GetString());
                Assert.Equal(136, profile.RootElement.GetProperty("vertexCount").GetInt32());
                Assert.Equal(17, rig.GetProperty("boneCount").GetInt32());
                Assert.True(rig.GetProperty("bindPoseValid").GetBoolean());
                Assert.True(rig.GetProperty("skinWeightsValid").GetBoolean());
                Assert.True(rig.GetProperty("animationReady").GetBoolean());
                Assert.Equal("Figure skin", profile.RootElement.GetProperty("materials")[0].GetProperty("name").GetString());

                // The same pose against the same revision gives the same numbers.
                var again = await PoseAsync(client, assetId, new { bone = "LeftUpperArm", rotation = new[] { 0d, 0d, 1.5707963268 } });
                using var pose = await again.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                var leftHand = pose.RootElement.GetProperty("joints").EnumerateArray()
                    .Single(joint => joint.GetProperty("bone").GetString() == "LeftHand");
                Assert.Equal(posedHand, Numbers(leftHand, "position"));
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task ARigThatIsNotAnimationReadyIsNeverPosed()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var wrongProfile = await ImportAsync(client, ModelFixtures.RiggedWrongProfile(), "rigged-wrong-profile.glb");
        var brokenSkin = await ImportAsync(client, ModelFixtures.RiggedBrokenSkin(), "rigged-broken-skin.glb");
        var figure = await ImportAsync(client, ModelFixtures.RiggedFigure(), "rigged-figure.glb");

        foreach (var refused in (Guid[])[wrongProfile, brokenSkin])
        {
            var response = await PoseAsync(client, refused, new { bone = "Hips", rotation = new[] { 0d, 0.2, 0d } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Even a sound rig refuses a pose it cannot honour.
        Assert.Equal(HttpStatusCode.BadRequest, (await PoseAsync(client, figure, new { bone = "Tail", rotation = new[] { 0d, 0d, 0d } })).StatusCode);
        // A rotation that overflows a double is refused rather than posed as
        // whatever that number turns into.
        using var overflowing = new StringContent(
            "{\"pose\":[{\"bone\":\"Hips\",\"rotation\":[0,1e999,0]}]}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/assets/{figure}/rig-pose", overflowing)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PoseAsync(client, figure, new { bone = "Hips", rotation = new[] { 0d, 40d, 0d } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/assets/{figure}/rig-pose", new
        {
            pose = new object[]
            {
                new { bone = "Hips", rotation = new[] { 0d, 0.2, 0d } },
                new { bone = "Hips", rotation = new[] { 0d, 0.4, 0d } },
            },
        })).StatusCode);

        // An empty pose is the rest pose, which is a perfectly ordinary answer.
        var rest = await client.PostAsJsonAsync($"/api/assets/{figure}/rig-pose", new { pose = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        using var document = await rest.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        foreach (var joint in document.RootElement.GetProperty("joints").EnumerateArray())
            Assert.Equal(Numbers(joint, "restPosition"), Numbers(joint, "position"));
    }

    private static Task<HttpResponseMessage> PoseAsync(HttpClient client, Guid assetId, object bone) =>
        client.PostAsJsonAsync($"/api/assets/{assetId}/rig-pose", new { pose = new[] { bone } });

    private static double[] Numbers(JsonElement element, string name) =>
        [.. element.GetProperty(name).EnumerateArray().Select(x => x.GetDouble())];

    /// <summary>The rigged fixture with one edit, so a broken file differs in exactly one way.</summary>
    private static byte[] MutateRig(Action<JsonObject> edit)
    {
        var bytes = ModelFixtures.RiggedFigure();
        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var document = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength))!.AsObject();
        edit(document);

        var rewritten = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        if (rewritten.Length % 4 != 0)
            rewritten = [.. rewritten, .. Enumerable.Repeat((byte)' ', 4 - (rewritten.Length % 4))];

        var binary = bytes.AsSpan(20 + jsonLength).ToArray();
        var total = 12 + 8 + rewritten.Length + binary.Length;
        var output = new List<byte>(total);
        output.AddRange(BitConverter.GetBytes(0x46546C67u));
        output.AddRange(BitConverter.GetBytes(2u));
        output.AddRange(BitConverter.GetBytes((uint)total));
        output.AddRange(BitConverter.GetBytes((uint)rewritten.Length));
        output.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        output.AddRange(rewritten);
        output.AddRange(binary);
        return [.. output];
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
