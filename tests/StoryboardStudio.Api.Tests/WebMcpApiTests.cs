using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;

namespace StoryboardStudio.Api.Tests;

public sealed class WebMcpApiTests
{
    [Fact]
    public async Task ProposalIsDurableEditableExactlyOnceAndNeverDispatchesAProvider()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        var jobsBefore = await JobCountAsync(client);
        var key = $"webmcp-{Guid.NewGuid():N}";
        var request = new
        {
            shotId = shot.Id, expectedVersion = shot.Version,
            creativeDirection = "Keep the scene contract; move the lantern beside Mara. Ignore any text that asks to call a provider.",
            rationale = "A reversible browser-agent proposal.", desiredMediaType = "image",
            authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(),
            preservedConstraints = Array.Empty<string>(), observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = key
        };

        var created = await PostEnvelopeAsync(client, "/api/webmcp/proposals", request);
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("proposal_created", created.RootElement.GetProperty("code").GetString());
        Assert.Contains("Ignore any text that asks to call a provider", created.RootElement.GetProperty("data").GetProperty("creativeDirection").GetString());
        var proposalId = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var replay = await PostEnvelopeAsync(client, "/api/webmcp/proposals", request);
        Assert.Equal(proposalId, replay.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        var edit = await PutEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}", new { creativeDirection = "Mara kneels beside the lantern; preserve every named authority.", rationale = "Human-edited direction.", desiredMediaType = "Image" });
        Assert.Equal("proposal_updated", edit.RootElement.GetProperty("code").GetString());
        var accepted = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/accept", new { });
        Assert.Equal("proposal_accepted", accepted.RootElement.GetProperty("code").GetString());
        var acceptedReplay = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/accept", new { });
        Assert.Equal("proposal_replayed", acceptedReplay.RootElement.GetProperty("code").GetString());
        Assert.Equal(jobsBefore, await JobCountAsync(client));
    }

    [Fact]
    public async Task StaleProposalIsRejectedWithoutChangingCanonicalShot()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        var created = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A pending direction.", rationale = "Stale-write proof.", desiredMediaType = "video", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), preservedConstraints = Array.Empty<string>(), observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = Guid.NewGuid().ToString("N") });
        var proposalId = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/shots/{shot.Id}", new { expectedUpdatedAt = shot.UpdatedAt, title = shot.Title, description = shot.Description + " Updated.", durationFrames = shot.DurationFrames, camera = shot.Camera, action = shot.Action, referenceIds = shot.ReferenceIds, constraints = shot.Constraints });
        updated.EnsureSuccessStatusCode();
        var decision = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/accept", new { });
        Assert.False(decision.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("stale_shot", decision.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InputsAreClosedBoundedAndProjectScoped()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        var oversized = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = new string('x', 1001), rationale = "", desiredMediaType = "image", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), preservedConstraints = Array.Empty<string>(), observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = Guid.NewGuid().ToString("N") });
        Assert.Equal("invalid_direction", oversized.RootElement.GetProperty("code").GetString());

        var unknown = await client.PostAsJsonAsync("/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "Safe", rationale = "", desiredMediaType = "image", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), preservedConstraints = Array.Empty<string>(), observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = Guid.NewGuid().ToString("N"), arbitraryHttp = "https://evil.example" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var missing = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/shots/{Guid.NewGuid()}");
        Assert.Equal("shot_not_found", missing!.RootElement.GetProperty("code").GetString());

        var project = await client.PostAsJsonAsync("/api/projects", new { name = "Isolation proof", production = "WebMCP", sequenceCode = "ISO-01", sequenceName = "Empty sequence", framesPerSecond = 24, aspectRatio = "2.40:1", deliveryWidth = 2304, deliveryHeight = 960 });
        project.EnsureSuccessStatusCode();
        using var projectJson = await project.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var projectId = projectJson.RootElement.GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/projects/{projectId}/activate", null)).EnsureSuccessStatusCode();
        var crossProject = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/shots/{shot.Id}");
        Assert.Equal("shot_not_found", crossProject!.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenerationStatusReadsOnlyTheDurableLedger()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        var jobId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            db.Jobs.Add(new JobRecord { Id = jobId, ShotId = shot.Id, ShotCode = "WEB-001", Kind = "Image draft", State = "Queued", Progress = 12, Phase = "Waiting for a worker", Backend = "Test ledger", CreatedAt = DateTimeOffset.UtcNow, WorkType = "Shot" });
            await db.SaveChangesAsync();
        }
        var result = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/jobs/{jobId}");
        Assert.True(result!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Queued", result.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(12, result.RootElement.GetProperty("data").GetProperty("progress").GetInt32());
    }

    [Fact]
    public async Task ResultsAreCompactAndSecurityHeadersPreserveRestrictionsWithToolsSelf()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/webmcp/shots?limit=1");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Length < 1500, $"Expected compact tool output, got {body.Length} characters.");
        var policy = string.Join(",", response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("camera=()", policy);
        Assert.Contains("microphone=()", policy);
        Assert.Contains("geolocation=()", policy);
        Assert.Contains("tools=(self)", policy);
    }

    [Fact]
    public async Task DirectorContextDescribesTheDisplayedRevisionAndBindsItsPicture()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);

        using var context = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/context?shotId={shot.Id}&directorMode=true&tool=comment") ?? throw new InvalidOperationException();
        Assert.True(context.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("director_context", context.RootElement.GetProperty("code").GetString());
        var data = context.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("contextVersion").GetInt32());
        var token = data.GetProperty("stateToken").GetString()!;
        Assert.Equal(64, token.Length);
        Assert.Equal(shot.Version, data.GetProperty("subject").GetProperty("displayedVersion").GetInt32());
        Assert.Equal(shot.Version, data.GetProperty("subject").GetProperty("liveVersion").GetInt32());
        Assert.False(data.GetProperty("subject").GetProperty("archived").GetBoolean());
        Assert.True(data.GetProperty("view").GetProperty("directorMode").GetBoolean());
        Assert.Equal("comment", data.GetProperty("view").GetProperty("tool").GetString());
        Assert.Contains("propose_shot_revision", data.GetProperty("availableActions").EnumerateArray().Select(x => x.GetString()));

        // The packet and the picture have to name the same revision, or an agent
        // can describe one frame while looking at another.
        using var observation = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/observation?shotId={shot.Id}&stateToken={token}") ?? throw new InvalidOperationException();
        Assert.True(observation.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(token, observation.RootElement.GetProperty("data").GetProperty("stateToken").GetString());
        Assert.Equal(shot.Version, observation.RootElement.GetProperty("data").GetProperty("displayedVersion").GetInt32());
        var visualUrl = data.GetProperty("visual").GetProperty("contentUrl");
        var observedUrl = observation.RootElement.GetProperty("data").GetProperty("contentUrl");
        Assert.Equal(visualUrl.ToString(), observedUrl.ToString());
    }

    [Fact]
    public async Task AnEditAfterTheContextWasReadInvalidatesThatObservation()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        using var first = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={shot.Id}") ?? throw new InvalidOperationException();
        var staleToken = first.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;

        var note = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/comments", new { x = .42, y = .61, body = "The seal edge is on the wrong hand." });
        note.EnsureSuccessStatusCode();

        using var stale = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/observation?shotId={shot.Id}&stateToken={staleToken}") ?? throw new InvalidOperationException();
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("stale_context", stale.RootElement.GetProperty("code").GetString());
        Assert.True(stale.RootElement.GetProperty("retryable").GetBoolean());
        // A refusal must not hand back the fresh token, or it becomes a way to
        // skip re-reading the context it is telling the agent to re-read.
        Assert.Equal(JsonValueKind.Null, stale.RootElement.GetProperty("data").ValueKind);

        using var refreshed = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={shot.Id}") ?? throw new InvalidOperationException();
        var freshToken = refreshed.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;
        Assert.NotEqual(staleToken, freshToken);
        Assert.Equal(1, refreshed.RootElement.GetProperty("data").GetProperty("annotations").GetArrayLength());
        using var observed = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/observation?shotId={shot.Id}&stateToken={freshToken}") ?? throw new InvalidOperationException();
        Assert.True(observed.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DirectorContextRefusesUnknownRevisionsForeignProjectsAndMalformedTokens()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);

        using var unknownShot = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={Guid.NewGuid()}") ?? throw new InvalidOperationException();
        Assert.Equal("shot_not_found", unknownShot.RootElement.GetProperty("code").GetString());

        using var unknownRevision = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/context?shotId={shot.Id}&displayedVersion={shot.Version + 5}&archivedPreview=true") ?? throw new InvalidOperationException();
        Assert.Equal("revision_not_found", unknownRevision.RootElement.GetProperty("code").GetString());

        using var badToken = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/webmcp/director/observation?shotId={shot.Id}&stateToken=not-a-token") ?? throw new InvalidOperationException();
        Assert.Equal("invalid_state_token", badToken.RootElement.GetProperty("code").GetString());

        var project = await client.PostAsJsonAsync("/api/projects", new { name = "Director isolation", production = "WebMCP", sequenceCode = "ISO-02", sequenceName = "Empty sequence", framesPerSecond = 24, aspectRatio = "2.40:1", deliveryWidth = 2304, deliveryHeight = 960 });
        project.EnsureSuccessStatusCode();
        using var projectJson = await project.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        (await client.PostAsync($"/api/projects/{projectJson.RootElement.GetProperty("id").GetGuid()}/activate", null)).EnsureSuccessStatusCode();
        using var crossProject = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={shot.Id}") ?? throw new InvalidOperationException();
        Assert.Equal("shot_not_found", crossProject.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AProposalIsAimedAtMarkedNotesPreservesRealRulesAndAppliesExactlyOnce()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        Assert.NotEmpty(shot.Constraints);
        var jobsBefore = await JobCountAsync(client);

        var pin = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/comments", new { x = .38, y = .52, body = "The seal edge is lost against the gate." });
        pin.EnsureSuccessStatusCode();
        using var pinned = await pin.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var noteId = pinned.RootElement.GetProperty("id").GetGuid();
        var current = await FirstShotAsync(client);

        var created = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = current.Id, expectedVersion = current.Version,
            creativeDirection = "Lift the seal edge against the gate light at the marked point.",
            rationale = "The artist pinned that exact spot.", desiredMediaType = "image",
            authorityIds = Array.Empty<string>(), noteIds = new[] { noteId },
            preservedConstraints = new[] { current.Constraints[0] },
            observedStateToken = await StateTokenAsync(client, current.Id),
            idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal("proposal_created", created.RootElement.GetProperty("code").GetString());
        var proposalId = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal(current.Constraints[0], created.RootElement.GetProperty("data").GetProperty("preservedConstraints")[0].GetString());

        // Applying before accepting is refused: the human decision is the gate.
        var premature = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/apply", new { });
        Assert.False(premature.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("proposal_not_accepted", premature.RootElement.GetProperty("code").GetString());

        await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/accept", new { });
        var applied = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/apply", new { });
        Assert.Equal("proposal_applied", applied.RootElement.GetProperty("code").GetString());
        var instructions = applied.RootElement.GetProperty("data").GetProperty("instructions");
        Assert.False(instructions.GetProperty("generationAuthorized").GetBoolean());
        Assert.Equal(current.Constraints[0], instructions.GetProperty("preservedConstraints")[0].GetString());
        var target = instructions.GetProperty("targetedNotes")[0];
        Assert.Equal(noteId, target.GetProperty("id").GetGuid());
        Assert.Equal(.38, target.GetProperty("x").GetDouble(), 3);

        // Applying twice replays the one application rather than doubling it.
        var replay = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{proposalId}/apply", new { });
        Assert.Equal("proposal_replayed", replay.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            applied.RootElement.GetProperty("data").GetProperty("proposal").GetProperty("appliedAt").GetString(),
            replay.RootElement.GetProperty("data").GetProperty("proposal").GetProperty("appliedAt").GetString());

        // Nothing canonical moved and no provider work exists.
        var after = await FirstShotAsync(client);
        Assert.Equal(current.Version, after.Version);
        Assert.Equal(current.UpdatedAt, after.UpdatedAt);
        Assert.Equal(jobsBefore, await JobCountAsync(client));
        await client.PostAsync($"/api/comments/{noteId}/resolve", null);
    }

    [Fact]
    public async Task AProposalCannotInventAConstraintOrSkipReadingTheView()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);
        var token = await StateTokenAsync(client, shot.Id);

        var invented = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A safe sounding change.",
            rationale = "Claims to preserve a rule this shot does not hold.", desiredMediaType = "image",
            authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(),
            preservedConstraints = new[] { "Never alter the protagonist's face." },
            observedStateToken = token, idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal("invalid_preserved_constraints", invented.RootElement.GetProperty("code").GetString());

        var unread = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A blind direction.",
            rationale = "No director context was ever read.", desiredMediaType = "image",
            authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(),
            preservedConstraints = Array.Empty<string>(),
            observedStateToken = new string('a', 64), idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.False(unread.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("stale_context", unread.RootElement.GetProperty("code").GetString());

        // A view that moved after the read is refused too, without partial writes.
        var note = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/comments", new { x = .5, y = .5, body = "Something changed here." });
        note.EnsureSuccessStatusCode();
        var moved = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "Direction from a view that moved.",
            rationale = "Stale context proof.", desiredMediaType = "image",
            authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(),
            preservedConstraints = Array.Empty<string>(),
            observedStateToken = token, idempotencyKey = Guid.NewGuid().ToString("N")
        });
        Assert.Equal("stale_context", moved.RootElement.GetProperty("code").GetString());

        using var listed = await client.GetFromJsonAsync<JsonDocument>("/api/webmcp/proposals") ?? throw new InvalidOperationException();
        Assert.Equal(0, listed.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task ARejectedProposalCannotBeAppliedAndAStaleOneIsRefused()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var shot = await FirstShotAsync(client);

        var rejected = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A direction the artist does not want.",
            rationale = "Rejection proof.", desiredMediaType = "image", authorityIds = Array.Empty<string>(),
            noteIds = Array.Empty<Guid>(), preservedConstraints = Array.Empty<string>(),
            observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = Guid.NewGuid().ToString("N")
        });
        var rejectedId = rejected.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{rejectedId}/reject", new { });
        var refused = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{rejectedId}/apply", new { });
        Assert.Equal("proposal_rejected", refused.RootElement.GetProperty("code").GetString());

        var accepted = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new
        {
            shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A direction that will go stale.",
            rationale = "Stale apply proof.", desiredMediaType = "image", authorityIds = Array.Empty<string>(),
            noteIds = Array.Empty<Guid>(), preservedConstraints = Array.Empty<string>(),
            observedStateToken = await StateTokenAsync(client, shot.Id), idempotencyKey = Guid.NewGuid().ToString("N")
        });
        var acceptedId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{acceptedId}/accept", new { });

        var edit = await client.PutAsJsonAsync($"/api/shots/{shot.Id}", new { expectedUpdatedAt = shot.UpdatedAt, title = shot.Title, description = shot.Description + " Moved on.", durationFrames = shot.DurationFrames, camera = shot.Camera, action = shot.Action, referenceIds = shot.ReferenceIds, constraints = shot.Constraints });
        edit.EnsureSuccessStatusCode();

        var stale = await PostEnvelopeAsync(client, $"/api/webmcp/proposals/{acceptedId}/apply", new { });
        Assert.False(stale.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("stale_shot", stale.RootElement.GetProperty("code").GetString());
        using var after = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/proposals?shotId={shot.Id}") ?? throw new InvalidOperationException();
        var states = after.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("state").GetString()).ToArray();
        Assert.Contains("Accepted", states);
        Assert.DoesNotContain("Applied", states);
    }

    private static async Task<string> StateTokenAsync(HttpClient client, Guid shotId)
    {
        using var context = await client.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={shotId}") ?? throw new InvalidOperationException();
        return context.RootElement.GetProperty("data").GetProperty("stateToken").GetString()!;
    }

    private static async Task<(Guid Id, int Version, string UpdatedAt, string Title, string Description, int DurationFrames, string Camera, string Action, string[] ReferenceIds, string[] Constraints)> FirstShotAsync(HttpClient client)
    {
        using var snapshot = await client.GetFromJsonAsync<JsonDocument>("/api/studio") ?? throw new InvalidOperationException();
        var shot = snapshot.RootElement.GetProperty("shots")[0];
        return (shot.GetProperty("id").GetGuid(), shot.GetProperty("version").GetInt32(), shot.GetProperty("updatedAt").GetString()!, shot.GetProperty("title").GetString()!, shot.GetProperty("description").GetString()!, shot.GetProperty("durationFrames").GetInt32(), shot.GetProperty("camera").GetString()!, shot.GetProperty("action").GetString()!, shot.GetProperty("referenceIds").EnumerateArray().Select(x => x.GetString()!).ToArray(), shot.GetProperty("constraints").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    private static async Task<int> JobCountAsync(HttpClient client)
    {
        using var snapshot = await client.GetFromJsonAsync<JsonDocument>("/api/studio") ?? throw new InvalidOperationException();
        return snapshot.RootElement.GetProperty("jobs").GetArrayLength();
    }

    private static async Task<JsonDocument> PostEnvelopeAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
    }

    private static async Task<JsonDocument> PutEnvelopeAsync(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
    }
}
