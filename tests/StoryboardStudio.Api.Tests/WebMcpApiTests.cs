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
            authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), idempotencyKey = key
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
        var created = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "A pending direction.", rationale = "Stale-write proof.", desiredMediaType = "video", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), idempotencyKey = Guid.NewGuid().ToString("N") });
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
        var oversized = await PostEnvelopeAsync(client, "/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = new string('x', 1001), rationale = "", desiredMediaType = "image", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), idempotencyKey = Guid.NewGuid().ToString("N") });
        Assert.Equal("invalid_direction", oversized.RootElement.GetProperty("code").GetString());

        var unknown = await client.PostAsJsonAsync("/api/webmcp/proposals", new { shotId = shot.Id, expectedVersion = shot.Version, creativeDirection = "Safe", rationale = "", desiredMediaType = "image", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(), idempotencyKey = Guid.NewGuid().ToString("N"), arbitraryHttp = "https://evil.example" });
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
