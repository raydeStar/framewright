using StoryboardStudio.Api.Services;
using System.Net;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

public sealed class MusicCompositionTests(StudioApiFactory factory) : IClassFixture<StudioApiFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task ManualCompositionCreatesImmutableEditableRevisions()
    {
        var createdResponse = await SendAsync(HttpMethod.Post, "/api/music/compositions", new CreateMusicCompositionRequest(
            "Signal Fire",
            "A dark synthwave escape song.",
            "dark cinematic synthwave, female vocal",
            "We run beneath a failing sun.",
            108,
            "4/4",
            "A minor",
            [
                new MusicSection("verse-1", "verse", 8, "We run beneath a failing sun.", ["Am", "F", "C", "G"], "Low, descending phrase"),
                new MusicSection("chorus-1", "chorus", 8, "Carry the signal home.", ["F", "G", "Am", "C"], "Rising octave hook")
            ],
            "wide synths and huge live drums",
            Score(108, "Am", "Am", "F", "C", "G")));
        var created = await createdResponse.Content.ReadFromJsonAsync<MusicCompositionSummary>();

        Assert.True(createdResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 OK, received {(int)createdResponse.StatusCode}: {await createdResponse.Content.ReadAsStringAsync()}");
        Assert.NotNull(created);
        Assert.Equal(1, created.CurrentRevisionNumber);
        var first = Assert.Single(created.Revisions);

        var changed = first.Composition with
        {
            Tempo = 105,
            Sections = first.Composition.Sections.Select(section => section.Type == "chorus"
                ? section with { Chords = ["Dm", "F", "Am", "G"] }
                : section)
                .Append(new MusicSection("bridge-1", "bridge", 8, "Instrumental signal break.", ["F", "G", "Em", "Am"], "Reuse the established motif with a higher ending."))
                .ToArray()
        };
        var revisedResponse = await SendAsync(HttpMethod.Post, $"/api/music/compositions/{created.Id}/revisions", new CreateMusicRevisionRequest(
            first.Id,
            "Slow to 105 BPM, reharmonize the chorus, and add a bridge",
            changed,
            first.AbcNotation));
        var revised = await revisedResponse.Content.ReadFromJsonAsync<MusicCompositionSummary>();

        Assert.Equal(HttpStatusCode.OK, revisedResponse.StatusCode);
        Assert.NotNull(revised);
        Assert.Equal(2, revised.CurrentRevisionNumber);
        Assert.Equal(2, revised.Revisions.Count);
        Assert.Contains(revised.Revisions, item => item.Id == first.Id && item.Composition.Tempo == 108);
        var current = Assert.Single(revised.Revisions, item => item.Id == revised.CurrentRevisionId);
        Assert.Equal(105, current.Composition.Tempo);
        Assert.Equal(["Dm", "F", "Am", "G"], current.Composition.Sections.Single(section => section.Type == "chorus").Chords);
        Assert.Contains("Q:1/4=105", current.AbcNotation, StringComparison.Ordinal);
        Assert.Contains("\"Dm\"", current.AbcNotation, StringComparison.Ordinal);
        Assert.Contains("% bridge [fw:bridge-1]", current.AbcNotation, StringComparison.Ordinal);

        var stale = await SendAsync(HttpMethod.Post, $"/api/music/compositions/{created.Id}/revisions", new CreateMusicRevisionRequest(
            first.Id,
            "A stale overwrite attempt",
            first.Composition,
            first.AbcNotation));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task CompositionRejectsIncompleteAbcWithoutWritingAnything()
    {
        var before = await client.GetFromJsonAsync<MusicCompositionSummary[]>("/api/music/compositions");
        var response = await SendAsync(HttpMethod.Post, "/api/music/compositions", new CreateMusicCompositionRequest(
            "Broken score", "A deliberately incomplete score.", "minimal", "[Instrumental]", AbcNotation: "X:1\nK:C\nCDEF"));
        var after = await client.GetFromJsonAsync<MusicCompositionSummary[]>("/api/music/compositions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before!.Length, after!.Length);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object body)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static string Score(int tempo, string key, params string[] chords) => $"""
        X:1
        T:Signal Fire
        M:4/4
        L:1/8
        Q:1/4={tempo}
        K:{key}
        V:Vocal
        V:Ins
        [V:Vocal] A2 B2 c2 e2 | e2 d2 c2 B2 |
        [V:Ins] "{chords[0]}"A2 C2 E2 A2 | "{chords[1]}"F2 A2 c2 f2 | "{chords[2]}"C2 E2 G2 c2 | "{chords[3]}"G2 B2 d2 g2 |
        """;
}
