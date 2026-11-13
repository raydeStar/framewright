using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Sound review: a MOSS list says which sounds should exist, MOSS run
/// manifests say which takes do, and a person accepts a take or rejects the
/// sound. Accepted takes export under the game's cue names.
/// </summary>
public sealed class SfxReviewApiTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(), "framewright-sfx-tests", Guid.NewGuid().ToString("N"));

    public SfxReviewApiTests() => Directory.CreateDirectory(workspace);

    public void Dispose()
    {
        try { Directory.Delete(workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ListedSoundsShowTheirTakesAndUngeneratedOnesSaySo()
    {
        var list = WriteList(("Jump", "Player", false), ("SparkFreed", "Signature", false), ("AmbienceHub", "Ambience", true));
        WriteRun("2026-11-13T10-00-00Z", ("Jump", "Player", 1, false), ("Jump", "Player", 2, false), ("AmbienceHub", "Ambience", 1, true), ("Stray", "World", 1, false));
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var board = await SetSourcesAsync(client, list, null);
        Assert.Null(board.ListError);
        // List order first (it is priority order), then sounds MOSS made that
        // the list does not mention.
        Assert.Equal(["Jump", "SparkFreed", "AmbienceHub", "Stray"], board.Sounds.Select(sound => sound.Name));
        var jump = board.Sounds[0];
        Assert.True(jump.Generated);
        Assert.Equal(2, jump.Takes.Length);
        Assert.Equal([1, 2], jump.Takes.Select(take => take.TakeNumber));
        Assert.Equal("SFX_Jump.wav", jump.CueFileName);
        Assert.False(board.Sounds[1].Generated);
        Assert.True(board.Sounds[2].Loop);
        Assert.False(board.Sounds[3].OnList);
        Assert.Equal(new SfxReviewCounts(4, 3, 0, 0, 1), board.Counts);

        // The take plays from where MOSS wrote it.
        var audio = await client.GetAsync(jump.Takes[1].AudioUrl);
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        Assert.Equal("audio/wav", audio.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TakeBytes("Jump", 2), await audio.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sfx-review/takes/{new string('0', 64)}/audio")).StatusCode);
    }

    [Fact]
    public async Task AcceptingNamesTheTakeAndRejectingKeepsTheNote()
    {
        var list = WriteList(("Jump", "Player", false), ("Hit", "Combat", false), ("Wake", "World", false));
        WriteRun("run-a", ("Jump", "Player", 1, false), ("Jump", "Player", 2, false), ("Hit", "Combat", 1, false));
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var board = await SetSourcesAsync(client, list, null);
        var secondJump = board.Sounds[0].Takes[1].Sha256;

        // Two takes: which one was liked has to be said.
        var unnamed = await client.PostAsJsonAsync("/api/sfx-review/sounds/Jump/decision",
            new SetSfxReviewDecisionRequest(AssetReviewDecision.Approved, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);

        var accepted = await DecideAsync(client, "jump", AssetReviewDecision.Approved, "Springy, keep it.", secondJump);
        Assert.Equal(AssetReviewDecision.Approved, accepted.Decision);
        Assert.Equal(secondJump, accepted.AcceptedTakeSha256);
        Assert.Equal("Springy, keep it.", accepted.Note);
        Assert.NotNull(accepted.DecidedAt);

        // A rejection's note is optional.
        var rejected = await DecideAsync(client, "Hit", AssetReviewDecision.ChangesRequested, "", null);
        Assert.Equal(AssetReviewDecision.ChangesRequested, rejected.Decision);
        Assert.Equal("", rejected.Note);
        var withNote = await DecideAsync(client, "Hit", AssetReviewDecision.ChangesRequested, "Too thin; more metal clank.", null);
        Assert.Equal("Too thin; more metal clank.", withNote.Note);

        // Nothing generated, nothing to accept; an unknown sound is not found.
        var ungenerated = await client.PostAsJsonAsync("/api/sfx-review/sounds/Wake/decision",
            new SetSfxReviewDecisionRequest(AssetReviewDecision.Approved, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, ungenerated.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/sfx-review/sounds/Nope/decision",
            new SetSfxReviewDecisionRequest(AssetReviewDecision.ChangesRequested, "x", null));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Back to Pending clears the note, the time and the take.
        var pending = await DecideAsync(client, "Jump", AssetReviewDecision.Pending, "ignored", null);
        Assert.Equal(AssetReviewDecision.Pending, pending.Decision);
        Assert.Equal("", pending.Note);
        Assert.Null(pending.DecidedAt);
        Assert.Null(pending.AcceptedTakeSha256);

        var listed = await client.GetFromJsonAsync<SfxReviewBoard>("/api/sfx-review") ?? throw new InvalidOperationException();
        Assert.Equal(new SfxReviewCounts(3, 1, 0, 1, 1), listed.Counts);
        Assert.Equal("Too thin; more metal clank.", listed.Sounds.Single(sound => sound.Name == "Hit").Note);
    }

    [Fact]
    public async Task ANewerTakeAnswersARejection()
    {
        var list = WriteList(("Hit", "Combat", false));
        WriteRun("run-a", ("Hit", "Combat", 1, false));
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        await SetSourcesAsync(client, list, null);
        await DecideAsync(client, "Hit", AssetReviewDecision.ChangesRequested, "More clank.", null);

        // MOSS runs again after the rejection.
        WriteRun("run-b", ("Hit", "Combat", 1, false), stamp: DateTimeOffset.UtcNow.AddMinutes(5), salt: "regenerated");
        var board = await client.GetFromJsonAsync<SfxReviewBoard>("/api/sfx-review") ?? throw new InvalidOperationException();
        var hit = board.Sounds.Single();
        Assert.Equal(AssetReviewDecision.Pending, hit.Decision);
        Assert.Equal(AssetReviewDecision.ChangesRequested, hit.StoredDecision);
        Assert.Equal("More clank.", hit.AnswersNote);
        Assert.Equal([false, true], hit.Takes.Select(take => take.AfterRejection));
        Assert.Equal(1, board.Counts.Pending);
    }

    [Fact]
    public async Task ExportCopiesAcceptedTakesUnderTheirCueNames()
    {
        var list = WriteList(("Jump", "Player", false), ("HubTheme", "Music", true), ("Hit", "Combat", false));
        WriteRun("run-a", ("Jump", "Player", 1, false), ("Jump", "Player", 2, false), ("HubTheme", "Music", 1, true), ("Hit", "Combat", 1, false));
        var export = Path.Combine(workspace, "approved");
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var board = await SetSourcesAsync(client, list, export);
        await DecideAsync(client, "Jump", AssetReviewDecision.Approved, "", board.Sounds[0].Takes[1].Sha256);
        await DecideAsync(client, "HubTheme", AssetReviewDecision.Approved, "", null);
        await DecideAsync(client, "Hit", AssetReviewDecision.ChangesRequested, "No.", null);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sfx-review/export");
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SfxExportResult>() ?? throw new InvalidOperationException();
        Assert.Equal(["SFX_Jump.wav", "MUS_HubTheme.wav"], result.Written.Select(file => file.CueFileName));
        Assert.Equal(TakeBytes("Jump", 2), await File.ReadAllBytesAsync(Path.Combine(export, "SFX_Jump.wav")));
        Assert.False(File.Exists(Path.Combine(export, "SFX_Hit.wav")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        Assert.Equal(2, manifest.RootElement.GetProperty("files").GetArrayLength());

        // Without the page's own header, nothing is exported.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/sfx-review/export", null)).StatusCode);
    }

    [Fact]
    public async Task SourcesRefuseMissingFilesAndExportsIntoAnUnrealProject()
    {
        var list = WriteList(("Jump", "Player", false));
        var project = Path.Combine(workspace, "Game");
        Directory.CreateDirectory(Path.Combine(project, "Content"));
        await File.WriteAllTextAsync(Path.Combine(project, "Game.uproject"), "{}");
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var missing = await PutSourcesAsync(client, new SetSfxReviewSourcesRequest(Path.Combine(workspace, "nope.json"), null, null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var intoProject = await PutSourcesAsync(client, new SetSfxReviewSourcesRequest(list, null, Path.Combine(project, "Content", "Audio")));
        Assert.Equal(HttpStatusCode.BadRequest, intoProject.StatusCode);
        Assert.Contains("Unreal project", await intoProject.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // With no take folder named, the list's own folder is searched.
        var board = await SetSourcesAsync(client, list, null);
        Assert.Equal([workspace], board.Sources.TakeRoots);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string WriteList(params (string Name, string Category, bool Loop)[] sounds)
    {
        var path = Path.Combine(workspace, "sfx-list.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            defaults = new { steps = 100 },
            sounds = sounds.Select(sound => new { name = sound.Name, category = sound.Category, prompt = $"A {sound.Name} sound.", seconds = 1.0, variations = 2, loop = sound.Loop }),
        }));
        return path;
    }

    private void WriteRun(string runId, params (string Name, string Category, int Take, bool Loop)[] takes) =>
        WriteRun(runId, takes, DateTimeOffset.UtcNow.AddMinutes(-5), "");

    private void WriteRun(string runId, (string Name, string Category, int Take, bool Loop) take, DateTimeOffset stamp, string salt) =>
        WriteRun(runId, [take], stamp, salt);

    private void WriteRun(string runId, (string Name, string Category, int Take, bool Loop)[] takes, DateTimeOffset stamp, string salt)
    {
        var run = Path.Combine(workspace, "runs", runId);
        var files = new List<object>();
        foreach (var take in takes)
        {
            var relative = $"{take.Category}/SFX_{take.Name}_{take.Take:00}.wav";
            var full = Path.Combine(run, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = TakeBytes(take.Name, take.Take, salt);
            File.WriteAllBytes(full, bytes);
            files.Add(new
            {
                name = take.Name, category = take.Category, file = relative, prompt = $"A {take.Name} sound.",
                seed = 1000 + take.Take, loop = take.Loop, final_duration = 0.8,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                timestamp_utc = stamp.ToString("O"),
            });
        }
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "sfx-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "framewright.sfx.v1", created_at = stamp.ToString("O"), files, failed = Array.Empty<object>(), status = "completed",
        }));
    }

    private static byte[] TakeBytes(string name, int take, string salt = "") => System.Text.Encoding.UTF8.GetBytes($"RIFF-fake-wave:{name}:{take}:{salt}");

    private static async Task<HttpResponseMessage> PutSourcesAsync(HttpClient client, SetSfxReviewSourcesRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/sfx-review/sources") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static async Task<SfxReviewBoard> SetSourcesAsync(HttpClient client, string list, string? export)
    {
        var response = await PutSourcesAsync(client, new SetSfxReviewSourcesRequest(list, null, export));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SfxReviewBoard>() ?? throw new InvalidOperationException();
    }

    private static async Task<SfxSoundSummary> DecideAsync(HttpClient client, string name, AssetReviewDecision decision, string note, string? take)
    {
        var response = await client.PostAsJsonAsync($"/api/sfx-review/sounds/{name}/decision", new SetSfxReviewDecisionRequest(decision, note, take));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SfxSoundSummary>() ?? throw new InvalidOperationException();
    }
}
