using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The authority library, per docs/MULTI_PROJECT_PLAN.md step 5.
///
/// The property that matters most is negative: an import is a <b>copy with
/// provenance</b>, never a live link. If a library edit could reach into a project
/// that already imported it, a frame delivered against v4 would silently start
/// meaning something else — which is exactly what the frozen-manifest design exists
/// to prevent. Several of these tests exist only to hold that line.
/// </summary>
public sealed class AuthorityLibraryTests
{
    [Fact]
    public async Task PromotingThenImportingCopiesAnAuthorityIntoAnotherProject()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();
        var home = await ActiveProjectIdAsync(client);

        var localId = await CreateAuthorityAsync(client, "Harbour Pilot", "A deck officer who brings the barge in.");
        Assert.Empty(await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library") ?? []);

        var promoted = await client.PostAsync($"/api/references/{localId}/promote-to-library", null);
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var libraryEntry = await promoted.Content.ReadFromJsonAsync<LibraryAuthoritySummary>();
        Assert.Equal("Harbour Pilot", libraryEntry!.Name);
        Assert.Equal(1, libraryEntry.Version);

        // The library is project-independent: a different project must see it.
        var second = await CreateProjectAsync(client, "Second Production", "SQ-77");
        await ActivateAsync(client, second);
        var visible = await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library");
        var entry = Assert.Single(visible!);
        Assert.Equal(libraryEntry.Id, entry.Id);
        Assert.Null(entry.ImportedAsReferenceId);
        Assert.False(entry.UpdateAvailable);

        // ...but its authorities are not in that project until imported.
        var beforeImport = await SnapshotAsync(client);
        Assert.Empty(beforeImport.References);

        var imported = await client.PostAsync($"/api/library/{entry.Id}/import", null);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var reference = await imported.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal("Harbour Pilot", reference!.Name);
        // A fresh authority in this production starts its own stack at v1, whatever
        // version it was taken from — its manifests will cite these numbers.
        Assert.Equal(1, reference.Version);
        Assert.Equal("harbour-pilot", reference.Id);

        var afterImport = await SnapshotAsync(client);
        Assert.Equal([reference.Id], afterImport.References.Select(x => x.Id));

        // The first project is untouched by the second project's import.
        await ActivateAsync(client, home);
        var homeState = await SnapshotAsync(client);
        Assert.Single(homeState.References, x => x.Id == localId);
    }

    [Fact]
    public async Task ALibraryUpdateNeverReachesAProjectThatAlreadyImportedIt()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();
        var origin = await ActiveProjectIdAsync(client);

        var localId = await CreateAuthorityAsync(client, "Signal Lantern", "A brass lantern used for deck signals.");
        var libraryId = (await (await client.PostAsync($"/api/references/{localId}/promote-to-library", null))
            .Content.ReadFromJsonAsync<LibraryAuthoritySummary>())!.Id;

        var consumer = await CreateProjectAsync(client, "Consumer Production", "SQ-88");
        await ActivateAsync(client, consumer);
        var importedId = (await (await client.PostAsync($"/api/library/{libraryId}/import", null))
            .Content.ReadFromJsonAsync<ReferenceSummary>())!.Id;
        var importedVersions = await client.GetFromJsonAsync<ReferenceVersionSummary[]>($"/api/references/{importedId}/versions");
        Assert.Single(importedVersions!);
        var frozenDescription = importedVersions![0].Description;

        // The origin project revises the authority and promotes v2 to the library.
        await ActivateAsync(client, origin);
        var revised = await client.PostAsJsonAsync($"/api/references/{localId}/versions", new CreateReferenceVersionRequest(
            1, "A brass lantern, now with a cracked green lens.", "The lens crack stays on the lantern's left face.", null));
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        var promotedAgain = await client.PostAsync($"/api/references/{localId}/promote-to-library", null);
        Assert.Equal(HttpStatusCode.OK, promotedAgain.StatusCode);
        Assert.Equal(2, (await promotedAgain.Content.ReadFromJsonAsync<LibraryAuthoritySummary>())!.Version);

        // The consumer's imported copy must be byte-for-byte what it imported.
        await ActivateAsync(client, consumer);
        var stillFrozen = await client.GetFromJsonAsync<ReferenceVersionSummary[]>($"/api/references/{importedId}/versions");
        Assert.Single(stillFrozen!);
        Assert.Equal(frozenDescription, stillFrozen![0].Description);
        Assert.DoesNotContain("cracked green lens", stillFrozen[0].Description);

        // What it does get is a signal, as an explicit act it can choose to take.
        var listed = Assert.Single(await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library") ?? []);
        Assert.True(listed.UpdateAvailable);
        Assert.Equal(1, listed.ImportedVersion);
        Assert.Equal(2, listed.Version);
    }

    [Fact]
    public async Task PullingALibraryUpdateAppendsAVersionAndLeavesHistoryIntact()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();
        var origin = await ActiveProjectIdAsync(client);

        var localId = await CreateAuthorityAsync(client, "Deck Crane", "A folding crane on the aft deck.");
        var libraryId = (await (await client.PostAsync($"/api/references/{localId}/promote-to-library", null))
            .Content.ReadFromJsonAsync<LibraryAuthoritySummary>())!.Id;

        var consumer = await CreateProjectAsync(client, "Crane Consumer", "SQ-99");
        await ActivateAsync(client, consumer);
        var importedId = (await (await client.PostAsync($"/api/library/{libraryId}/import", null))
            .Content.ReadFromJsonAsync<ReferenceSummary>())!.Id;

        await ActivateAsync(client, origin);
        await client.PostAsJsonAsync($"/api/references/{localId}/versions", new CreateReferenceVersionRequest(
            1, "A folding crane, repainted in oxidised copper.", "The crane arm folds to port, never to starboard.", null));
        await client.PostAsync($"/api/references/{localId}/promote-to-library", null);

        await ActivateAsync(client, consumer);
        var pulled = await client.PostAsync($"/api/references/{importedId}/pull-library-update", null);
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        var head = await pulled.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(2, head!.Version);
        Assert.Contains("oxidised copper", head.Description);

        // Appended, not edited: v1 still says what it said when it was imported, so a
        // manifest frozen against it stays explainable.
        var versions = await client.GetFromJsonAsync<ReferenceVersionSummary[]>($"/api/references/{importedId}/versions");
        Assert.Equal(2, versions!.Length);
        var first = versions.Single(x => x.Version == 1);
        Assert.DoesNotContain("oxidised copper", first.Description);

        // The signal clears once the project holds the library head.
        var listed = Assert.Single(await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library") ?? []);
        Assert.False(listed.UpdateAvailable);
        Assert.Equal(2, listed.ImportedVersion);

        var second = await client.PostAsync($"/api/references/{importedId}/pull-library-update", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PromotingTwiceAddsALibraryVersionRatherThanADuplicate()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();

        var localId = await CreateAuthorityAsync(client, "Tide Chart", "A laminated chart of the harbour approaches.");
        await client.PostAsync($"/api/references/{localId}/promote-to-library", null);
        await client.PostAsJsonAsync($"/api/references/{localId}/versions", new CreateReferenceVersionRequest(
            1, "A laminated chart, annotated in grease pencil.", "Annotations stay in the lower right quadrant.", null));
        await client.PostAsync($"/api/references/{localId}/promote-to-library", null);

        var entry = Assert.Single(await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library") ?? []);
        Assert.Equal(2, entry.Version);
        var versions = await client.GetFromJsonAsync<LibraryAuthorityVersionSummary[]>($"/api/library/{entry.Id}/versions");
        Assert.Equal(2, versions!.Length);
        // The trail records which production each version came from.
        Assert.All(versions, version => Assert.NotNull(version.OriginProjectId));
    }

    [Fact]
    public async Task ImportingTheSameAuthorityTwiceIsRefusedAndAnUnrelatedNameClashIsExplained()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();

        var localId = await CreateAuthorityAsync(client, "Hull Plate", "A riveted hull plate detail.");
        var libraryId = (await (await client.PostAsync($"/api/references/{localId}/promote-to-library", null))
            .Content.ReadFromJsonAsync<LibraryAuthoritySummary>())!.Id;

        // The promoting project is already linked to the library entry, so importing
        // it back would duplicate the authority it was promoted from.
        var duplicate = await client.PostAsync($"/api/library/{libraryId}/import", null);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var clashing = await CreateProjectAsync(client, "Clashing Production", "SQ-66");
        await ActivateAsync(client, clashing);
        await CreateAuthorityAsync(client, "Hull Plate", "An unrelated authority that happens to share the name.");
        var refused = await client.PostAsync($"/api/library/{libraryId}/import", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("unrelated authority named Hull Plate", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeletingALibraryAuthorityLeavesEveryImportedCopyWorking()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();

        var localId = await CreateAuthorityAsync(client, "Mooring Bollard", "A cast bollard on the quay.");
        var libraryId = (await (await client.PostAsync($"/api/references/{localId}/promote-to-library", null))
            .Content.ReadFromJsonAsync<LibraryAuthoritySummary>())!.Id;

        var consumer = await CreateProjectAsync(client, "Bollard Consumer", "SQ-55");
        await ActivateAsync(client, consumer);
        var importedId = (await (await client.PostAsync($"/api/library/{libraryId}/import", null))
            .Content.ReadFromJsonAsync<ReferenceSummary>())!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/library/{libraryId}")).StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<LibraryAuthoritySummary[]>("/api/library") ?? []);

        // The copy survives, because it was always a copy. Only the ability to pull
        // further updates is gone.
        var snapshot = await SnapshotAsync(client);
        Assert.Contains(snapshot.References, x => x.Id == importedId);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/references/{importedId}/pull-library-update", null)).StatusCode);
    }

    [Fact]
    public async Task AnAuthorityCreatedInAProjectCannotPullFromALibraryItNeverCameFrom()
    {
        using var factory = new ProjectIsolationFactory();
        var client = factory.CreateClient();
        var localId = await CreateAuthorityAsync(client, "Local Only", "Never promoted anywhere.");
        var response = await client.PostAsync($"/api/references/{localId}/pull-library-update", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("no library origin", await response.Content.ReadAsStringAsync());
    }

    private static async Task<StudioSnapshot> SnapshotAsync(HttpClient client)
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static async Task<Guid> ActiveProjectIdAsync(HttpClient client) => (await SnapshotAsync(client)).Project.Id;

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name, string sequenceCode)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            name, "Library harness", sequenceCode, "Library sequence", 24, "2.39:1", 2304, 960));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProjectSummary>())!.Id;
    }

    private static async Task ActivateAsync(HttpClient client, Guid projectId)
        => Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/projects/{projectId}/activate", null)).StatusCode);

    private static async Task<string> CreateAuthorityAsync(HttpClient client, string name, string description)
    {
        var response = await client.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            name, "Prop", description, "Stays exactly as described here.", "#8ea6cc", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReferenceSummary>())!.Id;
    }
}
