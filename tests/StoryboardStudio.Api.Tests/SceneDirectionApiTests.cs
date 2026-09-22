using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Directing one object in a scene. The whole point is that two identical props
/// are two different objects: a note, a context packet, and a proposal each name
/// an instance, so nothing can quietly land on the other one.
/// </summary>
public sealed class SceneDirectionApiTests
{
    [Fact]
    public async Task AProposalNamesOneInstanceAndApplyingItLeavesTheOtherAlone()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (sceneId, leftId, rightId, _) = await TwoIdenticalPropsAsync(client);

        // Read the context for the left prop only.
        using var context = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/scene-context?sceneId={sceneId}&instanceId={leftId}&directorMode=true&time=1.25") ?? throw new InvalidOperationException();
        var data = context.RootElement.GetProperty("data");
        Assert.Equal(leftId, data.GetProperty("selectedObject").GetProperty("instanceId").GetGuid());
        Assert.Equal("Left prop", data.GetProperty("selectedObject").GetProperty("name").GetString());
        Assert.Equal(2, data.GetProperty("objects").GetArrayLength());
        Assert.Equal(1.25, data.GetProperty("view").GetProperty("time").GetDouble());
        Assert.Contains("propose_scene_edit", data.GetProperty("availableActions").EnumerateArray().Select(x => x.GetString()));
        var token = data.GetProperty("stateToken").GetString()!;

        var wrongTime = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "Turn the left prop to face the gate.", rationale = "It reads as facing away from camera.",
            position = (double[]?)null, rotation = (double[])[0d, 1.2, 0d], scale = (double[]?)null,
            observedSceneTime = 2d,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("stale_context", wrongTime.RootElement.GetProperty("code").GetString());

        var proposal = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "Turn the left prop to face the gate.", rationale = "It reads as facing away from camera.",
            position = (double[]?)null, rotation = (double[])[0d, 1.2, 0d], scale = (double[]?)null,
            observedSceneTime = 1.25,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("proposal_created", proposal.RootElement.GetProperty("code").GetString());
        var proposalId = proposal.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal(leftId, proposal.RootElement.GetProperty("data").GetProperty("instanceId").GetGuid());

        // Staging changes nothing in the scene.
        using var untouched = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        Assert.Equal(2, untouched.RootElement.GetProperty("version").GetInt32());
        Assert.All(untouched.RootElement.GetProperty("instances").EnumerateArray().ToArray(),
            instance => Assert.Equal(0d, instance.GetProperty("rotation")[1].GetDouble()));

        (await client.PostAsync($"/api/scene-proposals/{proposalId}/accept", null)).EnsureSuccessStatusCode();
        var applied = await client.PostAsync($"/api/scene-proposals/{proposalId}/apply", null);
        applied.EnsureSuccessStatusCode();
        using var appliedProposal = await applied.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Equal("Applied", appliedProposal.RootElement.GetProperty("state").GetString());

        // The artist then saves through the ordinary validated scene save, which
        // is the only thing that moves anything.
        using var current = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var save = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion = current.RootElement.GetProperty("version").GetInt32(),
            name = current.RootElement.GetProperty("name").GetString(),
            camera = Camera(), environment = Environment(),
            instances = current.RootElement.GetProperty("instances").EnumerateArray().Select(instance => new
            {
                id = instance.GetProperty("id").GetGuid(),
                assetId = instance.GetProperty("assetId").GetGuid(),
                name = instance.GetProperty("name").GetString(),
                position = instance.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                rotation = instance.GetProperty("id").GetGuid() == leftId
                    ? (double[])[0d, 1.2, 0d]
                    : instance.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                scale = instance.GetProperty("scale").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            }).ToArray(),
        });
        save.EnsureSuccessStatusCode();

        using var after = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var left = after.RootElement.GetProperty("instances").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == leftId);
        var right = after.RootElement.GetProperty("instances").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == rightId);
        Assert.Equal(1.2, left.GetProperty("rotation")[1].GetDouble(), 6);
        // The identical prop next to it did not move.
        Assert.Equal(0d, right.GetProperty("rotation")[1].GetDouble());
        Assert.Equal([3d, 0d, 0d], right.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray());
    }

    [Fact]
    public async Task ANoteIsBoundToItsObjectAndRevisionAndGoesStaleWhenTheGeometryChanges()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (sceneId, leftId, rightId, modelId) = await TwoIdenticalPropsAsync(client);

        var note = await client.PostAsJsonAsync($"/api/scenes/{sceneId}/annotations", new
        {
            instanceId = leftId, anchor = (double[])[0.4, 0.8, 0.2], camera = Camera(),
            body = "This corner reads as broken from the gate angle.",
        });
        note.EnsureSuccessStatusCode();
        using var created = await note.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Equal(leftId, created.RootElement.GetProperty("instanceId").GetGuid());
        Assert.False(created.RootElement.GetProperty("stale").GetBoolean());
        Assert.Equal([0.4, 0.8, 0.2], created.RootElement.GetProperty("anchor").EnumerateArray().Select(x => x.GetDouble()).ToArray());

        // The note belongs to the left prop only.
        using var listed = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}/annotations") ?? throw new InvalidOperationException();
        Assert.Single(listed.RootElement.EnumerateArray().ToArray());
        Assert.DoesNotContain(listed.RootElement.EnumerateArray().ToArray(), x => x.GetProperty("instanceId").GetGuid() == rightId);

        // Pin the left prop to a different model revision: the geometry under
        // that anchor is not the geometry it was measured on.
        var second = await ImportAsync(client, ModelFixtures.AsymmetricPost(), "asymmetric-post.glb");
        Assert.NotEqual(modelId, second);
        using var current = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var swap = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion = current.RootElement.GetProperty("version").GetInt32(),
            name = "Two props", camera = Camera(), environment = Environment(),
            instances = current.RootElement.GetProperty("instances").EnumerateArray().Select(instance => new
            {
                id = instance.GetProperty("id").GetGuid(),
                assetId = instance.GetProperty("id").GetGuid() == leftId ? second : instance.GetProperty("assetId").GetGuid(),
                name = instance.GetProperty("name").GetString(),
                position = instance.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                rotation = instance.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                scale = instance.GetProperty("scale").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            }).ToArray(),
        });
        swap.EnsureSuccessStatusCode();

        using var afterSwap = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}/annotations") ?? throw new InvalidOperationException();
        var stale = Assert.Single(afterSwap.RootElement.EnumerateArray().ToArray());
        Assert.True(stale.GetProperty("stale").GetBoolean());
        // It is marked stale, not moved and not deleted.
        Assert.Equal([0.4, 0.8, 0.2], stale.GetProperty("anchor").EnumerateArray().Select(x => x.GetDouble()).ToArray());
        Assert.Equal(leftId, stale.GetProperty("instanceId").GetGuid());
    }

    [Fact]
    public async Task ASceneEditAfterTheContextWasReadInvalidatesTheProposal()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (sceneId, leftId, _, _) = await TwoIdenticalPropsAsync(client);

        using var context = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/scene-context?sceneId={sceneId}&instanceId={leftId}") ?? throw new InvalidOperationException();
        var staleToken = context.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;

        // The artist moves something and saves before the agent proposes.
        using var current = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var moved = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion = current.RootElement.GetProperty("version").GetInt32(),
            name = "Two props", camera = Camera(), environment = Environment(),
            instances = current.RootElement.GetProperty("instances").EnumerateArray().Select(instance => new
            {
                id = instance.GetProperty("id").GetGuid(),
                assetId = instance.GetProperty("assetId").GetGuid(),
                name = instance.GetProperty("name").GetString(),
                position = instance.GetProperty("id").GetGuid() == leftId
                    ? (double[])[-4d, 0d, 0d]
                    : instance.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                rotation = instance.GetProperty("rotation").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
                scale = instance.GetProperty("scale").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            }).ToArray(),
        });
        moved.EnsureSuccessStatusCode();

        var stale = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = staleToken,
            direction = "Nudge it left.", rationale = "Built on a view that has moved.",
            position = (double[])[1d, 0d, 0d], rotation = (double[]?)null, scale = (double[]?)null,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("stale_scene", stale.RootElement.GetProperty("code").GetString());

        // Nothing was staged, and the artist's move stands.
        using var proposals = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}/proposals") ?? throw new InvalidOperationException();
        Assert.Empty(proposals.RootElement.EnumerateArray().ToArray());
        using var after = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}") ?? throw new InvalidOperationException();
        var left = after.RootElement.GetProperty("instances").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == leftId);
        Assert.Equal(-4d, left.GetProperty("position")[0].GetDouble());
    }

    [Fact]
    public async Task ProposalsRefuseBlindWritesEmptyChangesAndForeignObjects()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (sceneId, leftId, _, _) = await TwoIdenticalPropsAsync(client);
        using var context = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/scene-context?sceneId={sceneId}&instanceId={leftId}") ?? throw new InvalidOperationException();
        var token = context.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;

        var blind = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = new string('a', 64),
            direction = "A direction with no context behind it.", rationale = "",
            position = (double[])[1d, 0d, 0d], rotation = (double[]?)null, scale = (double[]?)null,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("stale_context", blind.RootElement.GetProperty("code").GetString());

        var empty = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "Make it better.", rationale = "", position = (double[]?)null,
            rotation = (double[]?)null, scale = (double[]?)null, idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("empty_proposal", empty.RootElement.GetProperty("code").GetString());

        var collapsed = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "Flatten it.", rationale = "", position = (double[]?)null,
            rotation = (double[]?)null, scale = (double[])[0d, 1d, 1d], idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("invalid_transform", collapsed.RootElement.GetProperty("code").GetString());

        var foreign = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = Guid.NewGuid(), expectedSceneVersion = 2, observedStateToken = token,
            direction = "Move an object that is not here.", rationale = "",
            position = (double[])[1d, 0d, 0d], rotation = (double[]?)null, scale = (double[]?)null,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal("instance_not_found", foreign.RootElement.GetProperty("code").GetString());

        using var proposals = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{sceneId}/proposals") ?? throw new InvalidOperationException();
        Assert.Empty(proposals.RootElement.EnumerateArray().ToArray());
    }

    [Fact]
    public async Task ARejectedProposalCannotBeAppliedAndApplyingTwiceIsOneApplication()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var (sceneId, leftId, _, _) = await TwoIdenticalPropsAsync(client);
        using var context = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/scene-context?sceneId={sceneId}&instanceId={leftId}") ?? throw new InvalidOperationException();
        var token = context.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;

        var rejected = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "A direction the artist does not want.", rationale = "",
            position = (double[])[9d, 0d, 0d], rotation = (double[]?)null, scale = (double[]?)null,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        var rejectedId = rejected.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/scene-proposals/{rejectedId}/reject", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/scene-proposals/{rejectedId}/apply", null)).StatusCode);

        var accepted = await PostEnvelopeAsync(client, "/api/webmcp/scene-proposals", new
        {
            sceneId, instanceId = leftId, expectedSceneVersion = 2, observedStateToken = token,
            direction = "A direction the artist keeps.", rationale = "",
            position = (double[])[1d, 0d, 0d], rotation = (double[]?)null, scale = (double[]?)null,
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });
        var acceptedId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        // Applying before accepting is refused: the human decision is the gate.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/scene-proposals/{acceptedId}/apply", null)).StatusCode);
        (await client.PostAsync($"/api/scene-proposals/{acceptedId}/accept", null)).EnsureSuccessStatusCode();

        var first = await client.PostAsync($"/api/scene-proposals/{acceptedId}/apply", null);
        first.EnsureSuccessStatusCode();
        using var firstBody = await first.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var appliedAt = firstBody.RootElement.GetProperty("appliedAt").GetString();

        var second = await client.PostAsync($"/api/scene-proposals/{acceptedId}/apply", null);
        second.EnsureSuccessStatusCode();
        using var secondBody = await second.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        Assert.Equal(appliedAt, secondBody.RootElement.GetProperty("appliedAt").GetString());
    }

    private static object Camera() => new { yaw = 0.9, pitch = 0.42, distance = 8d, target = (double[])[0d, 0.5, 0d], fieldOfView = 38d };
    private static object Environment() => new { keyIntensity = 2.2, keyYaw = 0.8, keyPitch = 0.9, ambientIntensity = 1.4 };

    /// <summary>One model, placed twice. The whole milestone hangs on these being two objects.</summary>
    private static async Task<(Guid SceneId, Guid LeftId, Guid RightId, Guid ModelId)> TwoIdenticalPropsAsync(HttpClient client)
    {
        var modelId = await ImportAsync(client, ModelFixtures.AsymmetricBlock(), "asymmetric-block.glb");
        var created = await client.PostAsJsonAsync("/api/scenes", new { name = "Two props" });
        created.EnsureSuccessStatusCode();
        using var scene = await created.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var sceneId = scene.RootElement.GetProperty("id").GetGuid();

        var leftId = Guid.NewGuid();
        var rightId = Guid.NewGuid();
        var save = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion = 1, name = "Two props", camera = Camera(), environment = Environment(),
            instances = new object[]
            {
                new { id = leftId, assetId = modelId, name = "Left prop", position = (double[])[-3d, 0d, 0d], rotation = (double[])[0d, 0d, 0d], scale = (double[])[1d, 1d, 1d] },
                new { id = rightId, assetId = modelId, name = "Right prop", position = (double[])[3d, 0d, 0d], rotation = (double[])[0d, 0d, 0d], scale = (double[])[1d, 1d, 1d] },
            },
        });
        save.EnsureSuccessStatusCode();
        return (sceneId, leftId, rightId, modelId);
    }

    private static async Task<JsonDocument> PostEnvelopeAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
    }

    private static async Task<Guid> ImportAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var asset = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return asset.RootElement.GetProperty("id").GetGuid();
    }
}
