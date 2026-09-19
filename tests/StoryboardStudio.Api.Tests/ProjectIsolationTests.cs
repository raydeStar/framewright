using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// A dedicated factory per test, not per class. The active project is server-side
/// process state, so a test that switches it would otherwise decide what the next
/// test sees — which is exactly the kind of cross-contamination these tests exist to
/// rule out.
/// </summary>
public sealed class ProjectIsolationFactory : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "storyboard-studio-isolation", Guid.NewGuid().ToString("N"));

    public ProjectIsolationFactory() => Directory.CreateDirectory(DataRoot);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:1",
            ["Integrations:ComfyUi:SubmissionEnabled"] = "false",
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "false",
            ["Studio:DataRoot"] = DataRoot
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<StudioDbContext>();
            services.RemoveAll<DbContextOptions<StudioDbContext>>();
            services.AddDbContext<StudioDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(DataRoot, "isolation.db")};Pooling=False"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
    }
}

/// <summary>
/// The gate for the multi-project scoping change, per docs/MULTI_PROJECT_PLAN.md
/// step 2. Two projects are populated and every read endpoint is asserted to return
/// only the active project's rows.
///
/// This exists because a partially scoped build silently mixes two productions'
/// data, and because approval evidence is append-only and manifests cite exact
/// authority versions, a shot in project B binding an authority version from
/// project A is corruption that cannot be cleanly unwound. Reviewing ~250 query
/// sites by eye is not a safeguard; this is.
/// </summary>
public sealed class ProjectIsolationTests
{
    private HttpClient client = null!;

    [Fact]
    public async Task TwoProjectsNeverSeeEachOthersEntities()
    {
        using var factory = new ProjectIsolationFactory();
        client = factory.CreateClient();

        // Project A is the seeded production. Give it one more piece of every kind
        // of evidence so there is something specific to leak.
        var projectA = await ActiveProjectIdAsync();
        var shotA = await CreateShotAsync("IS-010", "Isolation A");
        var sharedName = await CreateAuthorityAsync("Isolation authority A");
        // Reference ids are per-project slugs, so a same-named authority in B would
        // legitimately share this id. Cross-project reads need an id only A holds.
        var authorityA = await CreateAuthorityAsync("Only in project A");
        var commentA = await CreateCommentAsync(shotA.Id, "Note that belongs to A.");
        var manifestA = await PrepareManifestAsync(shotA.Id, shotA.Version);
        var clipA = await CreateClipAsync("Isolation clip A", 0);
        var poseAResponse = await client.PostAsJsonAsync("/api/pose-presets", new CreatePosePresetRequest("Shared salute", [new SketchJoint("wrist", .8, .2)]));
        var poseA = (await poseAResponse.Content.ReadFromJsonAsync<PosePresetSummary>())!;

        var projectB = await CreateProjectAsync("Isolation project B", "IS-B");
        Assert.NotEqual(projectA, projectB);

        // Creating a project must not move the artist off the board they are on.
        Assert.Equal(projectA, await ActiveProjectIdAsync());
        var stillA = await SnapshotAsync();
        Assert.Contains(stillA.Shots, x => x.Id == shotA.Id);

        await ActivateAsync(projectB);
        Assert.Equal(projectB, await ActiveProjectIdAsync());

        // A brand-new project starts genuinely empty. If any of these are non-zero,
        // an unscoped read is showing project A's production inside project B.
        var emptyB = await SnapshotAsync();
        Assert.Equal(projectB, emptyB.Project.Id);
        Assert.Empty(emptyB.Shots);
        Assert.Empty(emptyB.References);
        Assert.Empty(emptyB.Comments);
        Assert.Empty(emptyB.Jobs);
        Assert.Empty(await client.GetFromJsonAsync<AssetSummary[]>("/api/assets") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<TimelineClipSummary[]>("/api/timeline/clips") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<VoiceProfileSummary[]>("/api/voice-profiles") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<AssetCollectionSummary[]>("/api/asset-collections") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<AssetPlacementSummary[]>("/api/asset-placements") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<PosePresetSummary[]>("/api/pose-presets") ?? []);

        // A shot code and an authority name that are already taken in A must be
        // free in B. This is what the widened unique indexes and the composite
        // reference key buy.
        var shotB = await CreateShotAsync("IS-010", "Isolation B");
        var authorityB = await CreateAuthorityAsync("Isolation authority A");
        Assert.NotEqual(shotA.Id, shotB.Id);
        Assert.Equal(shotA.Code, shotB.Code);
        // Same name, same slug, different project: the composite reference key is
        // what makes this legal rather than a uniqueness violation.
        Assert.Equal(sharedName, authorityB);
        var commentB = await CreateCommentAsync(shotB.Id, "Note that belongs to B.");
        var manifestB = await PrepareManifestAsync(shotB.Id, shotB.Version);
        var clipB = await CreateClipAsync("Isolation clip B", 400);
        var poseBResponse = await client.PostAsJsonAsync("/api/pose-presets", new CreatePosePresetRequest("Shared salute", [new SketchJoint("wrist", .2, .8)]));
        var poseB = (await poseBResponse.Content.ReadFromJsonAsync<PosePresetSummary>())!;
        Assert.NotEqual(poseA.Id, poseB.Id);

        // Per-shot reads are addressed by id, so an unscoped one would happily
        // serve project A's rows to a caller inside project B.
        Assert.Empty(await client.GetFromJsonAsync<GenerationManifestSummary[]>($"/api/shots/{shotA.Id}/manifests") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<ShotVersionSummary[]>($"/api/shots/{shotA.Id}/versions") ?? []);
        Assert.Empty(await client.GetFromJsonAsync<CandidateVersionSummary[]>($"/api/shots/{shotA.Id}/candidates") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/shots/{shotA.Id}/continuity")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/shots/{shotA.Id}/sketch")).StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<ReferenceVersionSummary[]>($"/api/references/{authorityA}/versions") ?? []);

        // Writes addressed at another project's rows must be refused, not applied.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/comments/{commentA}/resolve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/shots/{shotA.Id}?acceptRatifiedLoss=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/timeline/clips/{clipA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/references/{authorityA}")).StatusCode);

        // What B does see is exactly and only its own.
        var loadedB = await SnapshotAsync();
        Assert.Equal([shotB.Id], loadedB.Shots.Select(x => x.Id));
        var continuityB = await client.GetFromJsonAsync<ShotContinuityReport>($"/api/shots/{shotB.Id}/continuity");
        Assert.NotNull(continuityB);
        Assert.Equal(shotB.Id, continuityB.ShotId);
        Assert.Equal([authorityB], loadedB.References.Select(x => x.Id));
        Assert.Equal([commentB], loadedB.Comments.Select(x => x.Id));
        Assert.Equal([clipB], (await client.GetFromJsonAsync<TimelineClipSummary[]>("/api/timeline/clips") ?? []).Select(x => x.Id));
        Assert.Equal([manifestB], (await client.GetFromJsonAsync<GenerationManifestSummary[]>($"/api/shots/{shotB.Id}/manifests") ?? []).Select(x => x.Id));
        Assert.Equal([poseB.Id], (await client.GetFromJsonAsync<PosePresetSummary[]>("/api/pose-presets") ?? []).Select(x => x.Id));
        var sharedVersionInB = await AuthorityVersionRowAsync(sharedName);

        // Now back the other way. A one-directional check would pass a build that
        // scoped reads to whichever project happened to be created last.
        await ActivateAsync(projectA);
        var loadedA = await SnapshotAsync();
        Assert.Equal(projectA, loadedA.Project.Id);
        Assert.DoesNotContain(shotB.Id, loadedA.Shots.Select(x => x.Id));
        Assert.Contains(shotA.Id, loadedA.Shots.Select(x => x.Id));
        Assert.Contains(authorityA, loadedA.References.Select(x => x.Id));
        Assert.DoesNotContain(commentB, loadedA.Comments.Select(x => x.Id));
        Assert.Contains(commentA, loadedA.Comments.Select(x => x.Id));
        Assert.Equal([poseA.Id], (await client.GetFromJsonAsync<PosePresetSummary[]>("/api/pose-presets") ?? []).Select(x => x.Id));

        // The shared slug resolves to a different row in each project. This is the
        // real disjointness for references: the id is per-project by design, so the
        // thing that must not be shared is the versioned authority record itself.
        var sharedVersionInA = await AuthorityVersionRowAsync(sharedName);
        Assert.NotEqual(sharedVersionInA, sharedVersionInB);

        var clipsA = (await client.GetFromJsonAsync<TimelineClipSummary[]>("/api/timeline/clips") ?? []).Select(x => x.Id).ToArray();
        Assert.Contains(clipA, clipsA);
        Assert.DoesNotContain(clipB, clipsA);

        var manifestsA = (await client.GetFromJsonAsync<GenerationManifestSummary[]>($"/api/shots/{shotA.Id}/manifests") ?? []).Select(x => x.Id).ToArray();
        Assert.Contains(manifestA, manifestsA);
        Assert.DoesNotContain(manifestB, manifestsA);

        // The ids are disjoint sets, which is the property the plan actually asked
        // for: not merely "B is empty" but that no entity appears in both.
        Assert.Empty(loadedA.Shots.Select(x => x.Id).Intersect(loadedB.Shots.Select(x => x.Id)));
        Assert.Empty(loadedA.Comments.Select(x => x.Id).Intersect(loadedB.Comments.Select(x => x.Id)));
    }

    [Fact]
    public async Task ProjectListingCountsEveryProjectAndDeletionProtectsTheActiveOne()
    {
        using var factory = new ProjectIsolationFactory();
        client = factory.CreateClient();

        var active = await ActiveProjectIdAsync();
        var spare = await CreateProjectAsync("Deletable project", "DL-1");

        var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects");
        Assert.NotNull(projects);
        Assert.Contains(projects, x => x.Id == active && x.IsActive);
        Assert.Contains(projects, x => x.Id == spare && !x.IsActive);
        // The switcher has to report other projects' sizes, so these counts are the
        // one read that deliberately crosses the scope.
        Assert.True(projects.Single(x => x.Id == active).AuthorityCount >= 9);
        Assert.Equal(0, projects.Single(x => x.Id == spare).ShotCount);

        var refused = await client.DeleteAsync($"/api/projects/{active}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var deleted = await client.DeleteAsync($"/api/projects/{spare}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var summary = await deleted.Content.ReadFromJsonAsync<ProjectDeletionSummary>();
        Assert.Equal("Deletable project", summary!.Name);
        Assert.DoesNotContain(await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? [], x => x.Id == spare);
    }

    [Fact]
    public async Task DeletingAProjectLeavesTheOtherProjectsEvidenceIntact()
    {
        using var factory = new ProjectIsolationFactory();
        client = factory.CreateClient();

        var home = await ActiveProjectIdAsync();
        var doomed = await CreateProjectAsync("Doomed project", "DM-1");

        await ActivateAsync(doomed);
        var doomedShot = await CreateShotAsync("DM-010", "Doomed shot");
        await CreateCommentAsync(doomedShot.Id, "This note dies with its project.");

        await ActivateAsync(home);
        var before = await SnapshotAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/projects/{doomed}")).StatusCode);

        var after = await SnapshotAsync();
        Assert.Equal(before.Shots.Select(x => x.Id), after.Shots.Select(x => x.Id));
        Assert.Equal(before.References.Select(x => x.Id), after.References.Select(x => x.Id));
        Assert.Equal(before.Comments.Select(x => x.Id), after.Comments.Select(x => x.Id));
    }

    [Fact]
    public async Task ProjectDeletionRollsBackEveryRowWhenAMidCascadeCommandFails()
    {
        using var factory = new ProjectIsolationFactory();
        client = factory.CreateClient();
        _ = await ActiveProjectIdAsync();
        var doomed = await CreateProjectAsync("Atomic deletion project", "AT-1");
        var shotId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var placementId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            db.Shots.Add(new ShotRecord
            {
                Id = shotId,
                ProjectId = doomed,
                Code = "AT-010",
                Title = "Evidence that must survive rollback",
                Description = "A deliberately injected SQLite failure interrupts deletion.",
                Stage = ShotStage.Sketch.ToString(),
                Approval = ApprovalState.Working.ToString(),
                Version = 1,
                DurationFrames = 48,
                SortOrder = 1,
                VisualVariant = 1,
                ContinuityState = "Pending",
                Camera = "50mm - locked",
                Action = "Hold.",
                ReferenceIdsJson = "[]",
                ConstraintsJson = "[]",
                UpdatedAt = now
            });
            db.Assets.Add(new AssetRecord
            {
                Id = assetId,
                ProjectId = doomed,
                Kind = AssetKind.Image.ToString(),
                OriginalFileName = "atomic-delete.png",
                MimeType = "image/png",
                Bytes = 1,
                Width = 1,
                Height = 1,
                ContentHash = new string('d', 64),
                StoragePath = "atomic-delete.png",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.AssetPlacements.Add(new AssetPlacementRecord
            {
                Id = placementId,
                AssetId = assetId,
                ShotId = shotId,
                Role = "Composition",
                CreatedAt = now
            });
            db.Jobs.Add(new JobRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = doomed,
                ShotId = shotId,
                ShotCode = "AT-010",
                Kind = "Image",
                State = JobState.Completed.ToString(),
                Progress = 100,
                Phase = "Complete",
                Backend = "Injected transaction test",
                CreatedAt = now,
                CompletedAt = now
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER "FailAtomicProjectDelete"
                BEFORE DELETE ON "Jobs"
                BEGIN
                    SELECT RAISE(ABORT, 'injected mid-delete failure');
                END;
                """);
        }

        var failed = await client.DeleteAsync($"/api/projects/{doomed}");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            Assert.True(await db.Projects.AnyAsync(x => x.Id == doomed));
            Assert.True(await db.Shots.IgnoreQueryFilters().AnyAsync(x => x.Id == shotId && x.ProjectId == doomed));
            Assert.True(await db.Assets.IgnoreQueryFilters().AnyAsync(x => x.Id == assetId && x.ProjectId == doomed));
            Assert.True(await db.Jobs.IgnoreQueryFilters().AnyAsync(x => x.ProjectId == doomed));
            Assert.True(await db.AssetPlacements.AnyAsync(x => x.Id == placementId));
            Assert.False(await db.AuditEvents.AnyAsync(x => x.Type == "ProjectDeleted" && x.TargetId == doomed.ToString()));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER \"FailAtomicProjectDelete\";");
        }

        var succeeded = await client.DeleteAsync($"/api/projects/{doomed}");
        Assert.Equal(HttpStatusCode.OK, succeeded.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            Assert.False(await db.Projects.AnyAsync(x => x.Id == doomed));
            Assert.False(await db.AssetPlacements.AnyAsync(x => x.Id == placementId));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.Type == "ProjectDeleted" && x.TargetId == doomed.ToString()));
        }
    }

    private async Task<Guid> ActiveProjectIdAsync() => (await SnapshotAsync()).Project.Id;

    /// <summary>The row id of an authority's head version in the active project.</summary>
    private async Task<Guid> AuthorityVersionRowAsync(string referenceId)
    {
        var versions = await client.GetFromJsonAsync<ReferenceVersionSummary[]>($"/api/references/{referenceId}/versions");
        return Assert.Single(versions ?? []).Id;
    }

    private async Task<StudioSnapshot> SnapshotAsync()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private async Task<Guid> CreateProjectAsync(string name, string sequenceCode)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            name, "Isolation harness", sequenceCode, "Isolation sequence", 24, "2.39:1", 2304, 960));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ProjectSummary>();
        return created!.Id;
    }

    private async Task ActivateAsync(Guid projectId)
        => Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/projects/{projectId}/activate", null)).StatusCode);

    private async Task<ShotSummary> CreateShotAsync(string code, string title)
    {
        var response = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            code, title, "A shot that exists only to be counted.", 48, "50mm · locked", "Nothing moves.", [], ["Stays inside its own project"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ShotSummary>())!;
    }

    private async Task<string> CreateAuthorityAsync(string name)
    {
        var response = await client.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            name, "Character", "An authority that exists only to be counted.", "Never crosses a project boundary.", "#8ea6cc", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReferenceSummary>())!.Id;
    }

    private async Task<Guid> CreateCommentAsync(Guid shotId, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/shots/{shotId}/comments", new CreateCommentRequest(.5, .5, body, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommentSummary>())!.Id;
    }

    private async Task<Guid> PrepareManifestAsync(Guid shotId, int shotVersion)
    {
        var response = await client.PostAsJsonAsync($"/api/shots/{shotId}/manifests/prepare", new PrepareGenerationManifestRequest(
            shotVersion, 0, GenerationRoute.FastDraft, GenerationPurpose.Draft, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GenerationManifestSummary>())!.Id;
    }

    private async Task<Guid> CreateClipAsync(string label, int startFrame)
    {
        var response = await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(
            TimelineTrackKind.Dialogue, label, startFrame, 96, 0, 1, null, null, "Scoped line.", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TimelineClipSummary>())!.Id;
    }
}
