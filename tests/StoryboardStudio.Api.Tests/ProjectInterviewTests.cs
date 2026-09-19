using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The project interview proposes; it never applies.
///
/// These tests hold that boundary and one property that is easy to lose: whatever
/// Codex returns, the proposal must satisfy the same contract the project endpoints
/// enforce. A proposal the artist cannot then save would be worse than no proposal,
/// because the failure would surface as a validation error on their own edit.
/// </summary>
public sealed class ProjectInterviewTests(StudioApiFactory factory) : IClassFixture<StudioApiFactory>
{
    private static readonly string[] SupportedAuthorityCategories =
        ["Character", "Role", "Wardrobe", "Pose", "Location", "Architecture", "Prop", "Style", "World"];
    private readonly HttpClient client = factory.CreateClient();

    private async Task<HttpResponseMessage> PostInterviewAsync(ProjectInterviewRequest request)
    {
        // The shared test host deliberately points Codex at a nonexistent test
        // executable. This upper bound catches any future regression that tries
        // the developer's real login and waits through the production timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await client.PostAsJsonAsync("/api/codex/project-interview", request, timeout.Token);
    }

    private async Task<ProjectInterviewProposal> InterviewAsync(ProjectInterviewRequest request)
    {
        using var response = await PostInterviewAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = await response.Content.ReadFromJsonAsync<ProjectInterviewProposal>();
        Assert.NotNull(proposal);
        Assert.False(proposal.Live);
        Assert.Contains("Codex could not be reached", proposal.Detail, StringComparison.OrdinalIgnoreCase);
        return proposal;
    }

    [Fact]
    public async Task InterviewProposesAContractThatTheCreateEndpointAccepts()
    {
        var proposal = await InterviewAsync(new ProjectInterviewRequest(
            "A thirty second launch spot for a harbour survey airship.",
            "Painterly cel animation, restrained palette, heavy sea haze.",
            "Two deck officers and the airship itself.",
            "The hull registration must always read HV-9."));

        // Nothing may be written by proposing. Codex is unavailable in the test
        // environment, so this also pins the fallback path's behaviour.
        var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects");
        Assert.Single(projects!);
        Assert.DoesNotContain(projects!, item => item.Name == proposal.Name);

        Assert.False(string.IsNullOrWhiteSpace(proposal.Name));
        Assert.False(string.IsNullOrWhiteSpace(proposal.Detail));
        Assert.InRange(proposal.FramesPerSecond, 1, 120);
        Assert.Equal(0, proposal.DeliveryWidth % 16);
        Assert.Equal(0, proposal.DeliveryHeight % 16);
        Assert.InRange(proposal.DeliveryWidth, 320, 7680);
        Assert.InRange(proposal.DeliveryHeight, 180, 4320);
        // Every proposed authority has to be one the create-reference endpoint would
        // take, or "create the starter authorities" becomes a dead button.
        Assert.All(proposal.StarterAuthorities, authority =>
        {
            Assert.Contains(authority.Category, SupportedAuthorityCategories);
            Assert.Matches("^#[0-9a-fA-F]{6}$", authority.Accent);
            Assert.False(string.IsNullOrWhiteSpace(authority.Description));
            Assert.False(string.IsNullOrWhiteSpace(authority.LockedConstraint));
        });

        // The real assertion: hand the proposal straight to the create endpoint.
        var created = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            $"{proposal.Name} {Guid.NewGuid():N}"[..40], proposal.Production, proposal.SequenceCode, proposal.SequenceName,
            proposal.FramesPerSecond, proposal.AspectRatio, proposal.DeliveryWidth, proposal.DeliveryHeight,
            proposal.VisualStyle, proposal.WorldCanon, proposal.PromptDirectives, proposal.NegativeDirectives));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task InterviewKeepsTheArtistsOwnWordingWhenCodexIsUnavailable()
    {
        var proposal = await InterviewAsync(new ProjectInterviewRequest(
            "Harbour survey airship spot", "Heavy sea haze and cel shading", "", ""));

        // Whether or not Codex ran, the answers must survive into something the
        // artist recognises rather than being replaced by boilerplate.
        Assert.Contains("haze", proposal.VisualStyle, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(proposal.Rationale));
    }

    [Fact]
    public async Task InterviewReportsADeliveryCanvasConsistentWithItsOwnRationale()
    {
        var proposal = await InterviewAsync(new ProjectInterviewRequest(
            "A cinematic short at 2048 by 864.", "Hand-painted gouache.", "One keeper.", "Nothing yet."));

        // A canvas that already satisfies the contract must be returned untouched.
        // Normalising a valid pair into a "tidier" one made the returned numbers
        // disagree with the rationale text describing them.
        var parts = proposal.AspectRatio.Split(':');
        var named = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
                  / double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var actual = (double)proposal.DeliveryWidth / proposal.DeliveryHeight;
        Assert.True(Math.Abs(named - actual) / named <= .01,
            $"{proposal.DeliveryWidth}x{proposal.DeliveryHeight} does not match {proposal.AspectRatio}");
    }

    [Fact]
    public async Task InterviewRefusesAnEmptyOrOversizedAnswerSet()
    {
        using var empty = await PostInterviewAsync(new ProjectInterviewRequest("", "", "", ""));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var oversized = await PostInterviewAsync(new ProjectInterviewRequest(new string('x', 601), "", "", ""));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }
}
