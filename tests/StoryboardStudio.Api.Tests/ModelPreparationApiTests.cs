using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// One model already in the library, prepared into a derivative a browser can
/// carry — and left for a person to look at before anything uses it.
///
/// This is the opposite handling from generation, and the difference is the
/// point. A generated mesh has no topology worth keeping, so it is rebuilt and
/// repainted. A model already here is the other way round: its UVs, materials
/// and shell are what somebody approved, so it is adopted unchanged, collapsed
/// with them riding along, and shown beside its source in fixed views.
///
/// No GPU and no compiler are involved. The stand-in answers exactly as told,
/// including the one stage whose output is a directory rather than a file —
/// because a stub that wrote a file there would hide every place this studio
/// still assumes a step's answer is a file.
/// </summary>
public sealed class ModelPreparationApiTests
{
    private sealed class ControlledCompiler : ICompilerGateway
    {
        public CompilerCapabilities Capabilities { get; set; } = Ready;

        /// <summary>What the export stage delivers. A different mesh from the
        /// source, because a derivative that is byte-identical proves nothing.</summary>
        public byte[] Payload { get; set; } = ModelFixtures.DensePropRuntime();

        public string? FailingStage { get; set; }
        public int Runs { get; private set; }
        public List<(string Stage, string Source)> Calls { get; } = [];
        public Dictionary<string, IReadOnlyDictionary<string, string>> Options { get; } = [];

        public static CompilerStage Stage(string name) => new(
            name, name is "reduce-mesh" or "review-views" ? "powershell" : "blender",
            $"The {name} stage.", $"reference-asset-compiler.{name}.v1", true, [],
            OutputSuffix: name switch
            {
                "adopt-mesh" => ".blend",
                // A directory, not a file. The compiler says so by naming no
                // suffix at all, and this studio has to cope with it.
                "review-views" => "",
                _ => ".glb",
            });

        public static CompilerCapabilities Ready => new(
            Installed: true, Commissioned: true, Version: "reference-asset-compiler 0.1.2",
            Checkout: "C:/checkout", Blender: "C:/blender.exe",
            Stages: [Stage("adopt-mesh"), Stage("reduce-mesh"), Stage("review-views"),
                     Stage("browser-payload")]);

        public Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);

        public async Task<CompilerStageRun> RunStageAsync(
            string stage, string sourcePath, string outputPath, string reportPath,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? options = null)
        {
            Runs += 1;
            Calls.Add((stage, Path.GetFileName(sourcePath)));
            Options[stage] = options ?? new Dictionary<string, string>();
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            if (FailingStage == stage)
                return new CompilerStageRun(false, stage, 1, 0.1, null, null,
                    $"The {stage} stage refused.", ["refused"]);

            string receipt;
            if (stage == "review-views")
            {
                // The real stage renders four views in two passes into a
                // directory and writes a manifest binding every picture to the
                // hash of the mesh it is a picture of.
                Directory.CreateDirectory(outputPath);
                var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
                var of = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var views = new List<object>();
                foreach (var pass in (string[])["beauty", "matcap"])
                    foreach (var view in (string[])["front", "three-quarter", "side", "back"])
                    {
                        var name = $"{pass}-{view}.png";
                        var picture = System.Text.Encoding.UTF8.GetBytes($"picture of {of} {name}");
                        await File.WriteAllBytesAsync(Path.Combine(outputPath, name), picture, cancellationToken);
                        views.Add(new
                        {
                            view,
                            pass,
                            file = name,
                            sha256 = Convert.ToHexStringLower(SHA256.HashData(picture)),
                        });
                    }
                receipt = JsonSerializer.Serialize(new
                {
                    schema = "reference-asset-compiler.review-views.v1",
                    source_sha256 = of,
                    views,
                    judged = false,
                });
                await File.WriteAllTextAsync(Path.Combine(outputPath, "views.json"), receipt, cancellationToken);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllBytesAsync(outputPath, Payload, cancellationToken);
                receipt = JsonSerializer.Serialize(new
                {
                    schema = $"reference-asset-compiler.{stage}.v1",
                    status = "mechanical_pass",
                    production_grade = false,
                });
            }
            await File.WriteAllTextAsync(reportPath, receipt, cancellationToken);
            return new CompilerStageRun(true, stage, 0, 0.5, outputPath, receipt, null, ["done"]);
        }
    }

    private static StudioApiFactory Factory(ControlledCompiler compiler) =>
        new(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true,
            startGenerationWorker: false,
            configureServices: services => services.AddSingleton<ICompilerGateway>(compiler));

    [Fact]
    public async Task APreparedDerivativeJoinsItsSourceAndLeavesTheSourceTheCurrentOne()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "field-prop.glb", ModelFixtures.DenseProp());

        var queued = await client.PostAsJsonAsync("/api/models/preparation",
            new { sourceAssetId = source.Id, name = "Field prop (runtime)", triangleBudget = 2000 });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        var job = await queued.Content.ReadFromJsonAsync<JobSummary>() ?? throw new InvalidOperationException();
        Assert.Equal("Runtime derivative", job.Kind);

        var finished = await RunAsync(factory, job.Id);
        Assert.Equal(JobState.Completed, finished.State);
        Assert.NotNull(finished.OutputAssetId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var before = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
        var after = await db.Assets.SingleAsync(asset => asset.Id == finished.OutputAssetId);

        // One revision stack, the derivative second, citing the exact bytes it
        // came from.
        Assert.NotNull(before.RevisionFamilyId);
        Assert.Equal(before.RevisionFamilyId, after.RevisionFamilyId);
        Assert.Equal(source.Id, after.ParentAssetId);
        Assert.Equal(2, after.RevisionNumber);
        Assert.Contains(source.ContentHash[..12], after.RevisionPrompt, StringComparison.Ordinal);
        // And it says what the reduction cost, in the terms the decision is
        // made in. The lineage sentence written afterwards used to overwrite
        // this with a vaguer one that had no numbers in it at all.
        Assert.Contains("5,000 triangles reduced to 800", after.RevisionPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated from", after.RevisionPrompt, StringComparison.Ordinal);
        // The note beside it still names where it came from, in a preparation's
        // own words rather than a generation's.
        Assert.Contains("Prepared for runtime from", after.Notes, StringComparison.Ordinal);
        // A model's first revision is not an "Original image".
        Assert.Equal("Original model", before.RevisionPrompt);

        // And the source is still what everything reaches for. Nobody has
        // looked at the derivative yet, so it does not get to replace anything.
        Assert.True(before.IsCurrentRevision);
        Assert.False(after.IsCurrentRevision);
        Assert.Null(after.PreparationAcceptedAt);
    }

    [Fact]
    public async Task TheSourceIsReadTwiceAndChangedNever()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "untouched.glb", ModelFixtures.DenseProp());
        var storedBefore = await File.ReadAllBytesAsync(StoredPath(factory, source));

        var job = await QueueAsync(client, source.Id, "Untouched (runtime)");
        await RunAsync(factory, job.Id);

        // Two steps read the original rather than the step before them: the
        // source's own fixed views, and the adoption. Without that the source's
        // views would be views of an evidence directory.
        var read = compiler.Calls.Where(call => call.Source.StartsWith("untouched", StringComparison.Ordinal)
            || call.Source.EndsWith(".glb", StringComparison.Ordinal) && call.Stage is "review-views" or "adopt-mesh").ToArray();
        Assert.Equal(["review-views", "adopt-mesh", "reduce-mesh", "browser-payload", "review-views"],
            compiler.Calls.Select(call => call.Stage).ToArray());
        Assert.Equal(compiler.Calls[0].Source, compiler.Calls[1].Source);
        Assert.NotEqual(compiler.Calls[0].Source, compiler.Calls[4].Source);

        // The source file itself is byte-identical afterwards. A preparation
        // that edited its own source would not be a preparation.
        Assert.Equal(storedBefore, await File.ReadAllBytesAsync(StoredPath(factory, source)));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var after = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
        Assert.Equal(source.ContentHash, after.ContentHash);
        Assert.Equal(source.Bytes, after.Bytes);
    }

    [Fact]
    public async Task TheAdoptionKeepsWhatWasReviewedAndTheCollapseSaysWhatItIs()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "surface.glb", ModelFixtures.DenseProp());

        var job = await QueueAsync(client, source.Id, "Surface (runtime)", triangleBudget: 2500);
        await RunAsync(factory, job.Id);

        // A mesh with no UV layer is refused by name rather than delivered as
        // a derivative nobody can paint.
        Assert.True(compiler.Options["adopt-mesh"].ContainsKey("require-uvs"));

        // And the collapse is asked for as a derivative of something already
        // reviewed, which is what keeps its materials and stops it being
        // judged as a candidate production authority it was never going to be.
        Assert.Equal("2500", compiler.Options["reduce-mesh"]["triangle-budget"]);
        Assert.True(compiler.Options["reduce-mesh"].ContainsKey("runtime-derivative"));
        Assert.Equal("", compiler.Options["reduce-mesh"]["runtime-derivative"]);
    }

    [Fact]
    public async Task BothSetsOfFixedViewsAreServedAndBoundToTheBytesTheyArePicturesOf()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "compared.glb", ModelFixtures.DenseProp());

        var job = await QueueAsync(client, source.Id, "Compared (runtime)");
        var finished = await RunAsync(factory, job.Id);

        var evidence = await client.GetFromJsonAsync<ModelPreparationEvidence>(
            $"/api/jobs/{job.Id}/preparation-evidence") ?? throw new InvalidOperationException();
        Assert.Equal(source.Id, evidence.SourceAssetId);
        Assert.Equal(finished.OutputAssetId, evidence.DerivativeAssetId);
        Assert.NotNull(evidence.Source);
        Assert.NotNull(evidence.Derivative);

        // Four views in two passes, of each, so a good front view cannot
        // conceal a broken side.
        Assert.Equal(8, evidence.Source!.Views.Length);
        Assert.Equal(8, evidence.Derivative!.Views.Length);
        Assert.Equal(["back", "front", "side", "three-quarter"],
            evidence.Derivative.Views.Select(view => view.View).Distinct().Order().ToArray());

        // Each manifest names the hash of the mesh it is of, and they are
        // different meshes — which is the whole reason for comparing them.
        Assert.NotEqual(evidence.Source.SourceSha256, evidence.Derivative.SourceSha256);
        Assert.NotEmpty(evidence.Source.SourceSha256);

        var picture = await client.GetAsync(evidence.Derivative.Views[0].Url);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);

        // A name the manifest does not list is not evidence, whatever is on
        // disk beside it.
        var invented = await client.GetAsync($"/api/jobs/{job.Id}/preparation-views/1/render.log");
        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);
        var traversal = await client.GetAsync($"/api/jobs/{job.Id}/preparation-views/1/..%2F..%2Fappsettings.json");
        Assert.NotEqual(HttpStatusCode.OK, traversal.StatusCode);
    }

    [Fact]
    public async Task AWorkerThatFailsLeavesTheSourceUsableAndDeliversNoDerivative()
    {
        var compiler = new ControlledCompiler { FailingStage = "reduce-mesh" };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "kept.glb", ModelFixtures.DenseProp());

        var job = await QueueAsync(client, source.Id, "Kept (runtime)");
        var finished = await RunAsync(factory, job.Id);

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Null(finished.OutputAssetId);
        Assert.Contains("reduce-mesh", finished.Error, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();

        // No half-derivative in the library, and no revision stack invented
        // around a model that never got one.
        Assert.Empty(await db.Assets.Where(asset => asset.Source == "Prepared derivative").ToArrayAsync());
        var kept = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
        Assert.False(kept.IsArchived);
        Assert.Equal(source.ContentHash, kept.ContentHash);

        // And the source is still loadable, which is the only sense in which
        // "usable" can be checked rather than asserted.
        var profile = await client.GetAsync($"/api/assets/{source.Id}/model-profile");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    [Fact]
    public async Task AcceptingTheSourceDoesNotAcceptWhatWasPreparedFromIt()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "approved.glb", ModelFixtures.DenseProp());

        // The source is accepted first, and has been for as long as anything
        // here can remember.
        var acceptedSource = await client.PostAsJsonAsync($"/api/assets/{source.Id}/preparation-acceptance",
            new { accepted = true, note = "Looked at it. Good." });
        Assert.Equal(HttpStatusCode.OK, acceptedSource.StatusCode);

        var job = await QueueAsync(client, source.Id, "Approved (runtime)");
        var finished = await RunAsync(factory, job.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var derivative = await db.Assets.SingleAsync(asset => asset.Id == finished.OutputAssetId);
        var parent = await db.Assets.SingleAsync(asset => asset.Id == source.Id);

        // Acceptance is a fact about one asset, so a new revision is a new row
        // and starts with none of it. Nothing had to remember to clear it.
        Assert.NotNull(parent.PreparationAcceptedAt);
        Assert.Null(derivative.PreparationAcceptedAt);
        Assert.Equal("", derivative.PreparationAcceptedBy);

        // The topology moved, so a rig or an anchor agreed against the old
        // surface could not follow it even if something wanted to.
        Assert.True(derivative.PreparationTopologyChanged);
    }

    [Fact]
    public async Task AcceptingADerivativeIsRecordedAndIsWhatMakesItCurrent()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "decided.glb", ModelFixtures.DenseProp());
        var job = await QueueAsync(client, source.Id, "Decided (runtime)");
        var finished = await RunAsync(factory, job.Id);

        var accepted = await client.PostAsJsonAsync(
            $"/api/assets/{finished.OutputAssetId}/preparation-acceptance",
            new { accepted = true, note = "Silhouette holds at this budget." });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var summary = await accepted.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
        Assert.NotNull(summary.PreparationAcceptedAt);
        Assert.Contains("Silhouette holds", summary.PreparationAcceptanceNote, StringComparison.Ordinal);
        Assert.True(summary.IsCurrentRevision);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var parent = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
        Assert.False(parent.IsCurrentRevision);

        // Both revisions are still there and still openable, which is what a
        // stack is for: a derivative is not a replacement.
        foreach (var id in (Guid[])[source.Id, finished.OutputAssetId!.Value])
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/assets/{id}/model-profile")).StatusCode);
        var family = await client.GetFromJsonAsync<AssetSummary[]>($"/api/assets/{source.Id}/revisions")
            ?? throw new InvalidOperationException();
        Assert.Equal(2, family.Length);
    }

    [Fact]
    public async Task ARefusalHasToSayWhatIsWrongWithIt()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "refused.glb", ModelFixtures.DenseProp());
        var job = await QueueAsync(client, source.Id, "Refused (runtime)");
        var finished = await RunAsync(factory, job.Id);

        var silent = await client.PostAsJsonAsync(
            $"/api/assets/{finished.OutputAssetId}/preparation-acceptance",
            new { accepted = false, note = "" });
        Assert.Equal(HttpStatusCode.BadRequest, silent.StatusCode);

        var refused = await client.PostAsJsonAsync(
            $"/api/assets/{finished.OutputAssetId}/preparation-acceptance",
            new { accepted = false, note = "The blade edge breaks up at this budget." });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var summary = await refused.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();

        // Refusing records the reason rather than deleting the evidence: the
        // reason is how the next attempt is asked for differently.
        Assert.Null(summary.PreparationAcceptedAt);
        Assert.Contains("blade edge", summary.PreparationAcceptanceNote, StringComparison.Ordinal);
        Assert.False(summary.IsCurrentRevision);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.True((await db.Assets.SingleAsync(asset => asset.Id == source.Id)).IsCurrentRevision);
    }

    [Fact]
    public async Task ABudgetThatWouldNotReduceAnythingIsRefusedBeforeAnyWorkIsQueued()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "already-small.glb", ModelFixtures.DenseProp());

        var refused = await client.PostAsJsonAsync("/api/models/preparation",
            new { sourceAssetId = source.Id, name = "No smaller", triangleBudget = 6_000 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The number the artist needs to hear is the one their model has, not
        // a stage exit code an hour later.
        var said = await refused.Content.ReadAsStringAsync();
        Assert.Contains("smaller than its source", said, StringComparison.Ordinal);
        Assert.Equal(0, compiler.Runs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Empty(await db.Jobs.Where(job => job.WorkType == "Model").ToArrayAsync());
    }

    [Fact]
    public async Task AnImageIsNotSomethingAPreparationCanStartFrom()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var picture = await ImportImageAsync(client, "not-a-model.png");

        var refused = await client.PostAsJsonAsync("/api/models/preparation",
            new { sourceAssetId = picture.Id, name = "Wrong kind" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
    }

    [Fact]
    public async Task AStageThisCompilerDoesNotOfferRefusesThePreparationByName()
    {
        var compiler = new ControlledCompiler
        {
            // A compiler that can generate but has never heard of adoption.
            Capabilities = ControlledCompiler.Ready with
            {
                Stages = [ControlledCompiler.Stage("reduce-mesh"),
                          ControlledCompiler.Stage("review-views"),
                          ControlledCompiler.Stage("browser-payload")],
            },
        };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "unsupported.glb", ModelFixtures.DenseProp());

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/preparation/readiness")
            ?? throw new InvalidOperationException();
        Assert.False(readiness.RootElement.GetProperty("canRun").GetBoolean());
        Assert.Contains("adopt-mesh", readiness.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var refused = await client.PostAsJsonAsync("/api/models/preparation",
            new { sourceAssetId = source.Id, name = "Never prepared", triangleBudget = 2000 });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
    }

    [Fact]
    public async Task AnInterruptedPreparationAdoptsTheEvidenceItAlreadyRendered()
    {
        var compiler = new ControlledCompiler { FailingStage = "reduce-mesh" };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "resumed.glb", ModelFixtures.DenseProp());

        var job = await QueueAsync(client, source.Id, "Resumed (runtime)");
        Assert.Equal(JobState.Failed, (await RunAsync(factory, job.Id)).State);
        var firstAttempt = compiler.Calls.Count;

        // The views the first attempt rendered are a directory, and the
        // compiler refuses to render into one that already exists. A resume
        // that could not recognise a directory as an answer would re-ask for
        // them and fail on evidence it wrote itself.
        compiler.FailingStage = null;
        await RetryAsync(factory, job.Id);
        var finished = await RunAsync(factory, job.Id);

        Assert.Equal(JobState.Completed, finished.State);
        var rerun = compiler.Calls.Skip(firstAttempt).Select(call => call.Stage).ToArray();
        Assert.DoesNotContain("review-views", rerun.Take(1));
        Assert.Equal(["reduce-mesh", "browser-payload", "review-views"], rerun);

        // And the adopted evidence is still readable, bound to the same bytes.
        var evidence = await client.GetFromJsonAsync<ModelPreparationEvidence>(
            $"/api/jobs/{job.Id}/preparation-evidence") ?? throw new InvalidOperationException();
        Assert.Equal(8, evidence.Source!.Views.Length);
        Assert.Equal(8, evidence.Derivative!.Views.Length);
    }

    [Fact]
    public async Task TheEvidenceIsReachableFromTheDerivativeAndNotOnlyFromTheJob()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "tomorrow.glb", ModelFixtures.DenseProp());
        var job = await QueueAsync(client, source.Id, "Tomorrow (runtime)");
        var finished = await RunAsync(factory, job.Id);

        // A comparison that lives only in the page that started it is not a
        // review gate: the artist closes the screen, comes back to decide, and
        // the pictures the decision rests on are gone.
        var evidence = await client.GetFromJsonAsync<ModelPreparationEvidence>(
            $"/api/assets/{finished.OutputAssetId}/preparation-evidence") ?? throw new InvalidOperationException();
        Assert.Equal(job.Id, evidence.JobId);
        Assert.Equal(8, evidence.Source!.Views.Length);
        Assert.Equal(8, evidence.Derivative!.Views.Length);

        // The source has no evidence of its own, which is right: the thing
        // being judged is the derivative. It says so plainly rather than
        // refusing, because most models were never prepared from anything and
        // a 404 on every one of them is console noise, not information.
        var none = await client.GetFromJsonAsync<ModelPreparationEvidence>(
            $"/api/assets/{source.Id}/preparation-evidence") ?? throw new InvalidOperationException();
        Assert.Equal(Guid.Empty, none.JobId);
        Assert.Null(none.Source);
        Assert.Null(none.Derivative);
    }

    [Fact]
    public async Task ANoteAnchoredToTheOldSurfaceGoesStaleRatherThanMovingToTheNewOne()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "placed.glb", ModelFixtures.DenseProp());

        // The reviewed model is placed in a scene and annotated on its surface.
        var scene = await client.PostAsJsonAsync("/api/scenes", new { name = "Anchor test" });
        scene.EnsureSuccessStatusCode();
        var made = await scene.Content.ReadFromJsonAsync<SceneSummary>() ?? throw new InvalidOperationException();
        var instanceId = Guid.NewGuid();
        var camera = new SceneCameraSummary(0.6, 0.3, 4, [0, 0.5, 0], 45);
        var saved = await client.PutAsJsonAsync($"/api/scenes/{made.Id}", new
        {
            expectedVersion = made.Version,
            name = made.Name,
            camera,
            environment = new SceneEnvironmentSummary(1, 0.5, 0.6, 0.3),
            instances = new[]
            {
                new
                {
                    id = instanceId, assetId = source.Id, name = "The prop",
                    position = new[] { 0.0, 0.0, 0.0 },
                    rotation = new[] { 0.0, 0.0, 0.0 },
                    scale = new[] { 1.0, 1.0, 1.0 },
                },
            },
        });
        saved.EnsureSuccessStatusCode();

        var noted = await client.PostAsJsonAsync($"/api/scenes/{made.Id}/annotations", new
        {
            instanceId,
            anchor = new[] { 0.4, 0.2, 0.1 },
            camera,
            body = "This ridge reads too soft from the front.",
        });
        noted.EnsureSuccessStatusCode();

        var before = await client.GetFromJsonAsync<SceneAnnotationSummary[]>($"/api/scenes/{made.Id}/annotations")
            ?? throw new InvalidOperationException();
        Assert.False(before.Single().Stale);

        // Now prepare a derivative and pin the same instance to it.
        var job = await QueueAsync(client, source.Id, "Placed (runtime)");
        var finished = await RunAsync(factory, job.Id);
        var current = await client.GetFromJsonAsync<SceneSummary>($"/api/scenes/{made.Id}")
            ?? throw new InvalidOperationException();
        var repinned = await client.PutAsJsonAsync($"/api/scenes/{made.Id}", new
        {
            expectedVersion = current.Version,
            name = current.Name,
            camera,
            environment = new SceneEnvironmentSummary(1, 0.5, 0.6, 0.3),
            instances = new[]
            {
                new
                {
                    id = instanceId, assetId = finished.OutputAssetId, name = "The prop",
                    position = new[] { 0.0, 0.0, 0.0 },
                    rotation = new[] { 0.0, 0.0, 0.0 },
                    scale = new[] { 1.0, 1.0, 1.0 },
                },
            },
        });
        repinned.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var derivative = await db.Assets.SingleAsync(asset => asset.Id == finished.OutputAssetId);
        Assert.True(derivative.PreparationTopologyChanged);

        // The anchor was measured against geometry that no longer exists at
        // that point, so the note is reported stale rather than quietly moved
        // to a spot on the new surface that means something else.
        var after = await client.GetFromJsonAsync<SceneAnnotationSummary[]>($"/api/scenes/{made.Id}/annotations")
            ?? throw new InvalidOperationException();
        var note = after.Single();
        Assert.True(note.Stale);
        Assert.Equal(source.Id, note.AssetId);
        Assert.Contains("ridge reads too soft", note.Body, StringComparison.Ordinal);

        // And nothing about the source's standing came with it. The derivative
        // is in the scene because a person put it there, not because anything
        // decided it had inherited the old surface's approval.
        Assert.Null(derivative.PreparationAcceptedAt);
    }

    [Fact]
    public async Task WhatSurvivedTheReductionIsMeasuredFromTheStoredBytes()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "integrity.glb", ModelFixtures.DenseProp());
        var job = await QueueAsync(client, source.Id, "Integrity (runtime)");
        var finished = await RunAsync(factory, job.Id);

        var before = await client.GetFromJsonAsync<ModelProfileSummary>(
            $"/api/assets/{source.Id}/model-profile") ?? throw new InvalidOperationException();
        var after = await client.GetFromJsonAsync<ModelProfileSummary>(
            $"/api/assets/{finished.OutputAssetId}/model-profile") ?? throw new InvalidOperationException();

        // Fewer triangles is what was asked for.
        Assert.True(after.TriangleCount < before.TriangleCount);

        // The UV map is not, and until this was reported a derivative that had
        // lost its map looked exactly like one that kept it.
        Assert.NotNull(before.UvChannels);
        Assert.NotNull(after.UvChannels);
        Assert.Equal([0], before.UvChannels!);
        Assert.Equal([0], after.UvChannels!);
        Assert.Equal(0, before.PrimitivesWithoutUvs);
        Assert.Equal(0, after.PrimitivesWithoutUvs);
        Assert.Equal(before.Materials.Length, after.Materials.Length);

        // And a model that genuinely has no map says so. Without a mesh like
        // this in the comparison, a reader that simply claimed every primitive
        // carried channel 0 would be indistinguishable from one that looked.
        var bare = await ImportModelAsync(client, "no-uvs.glb", ModelFixtures.AsymmetricBlock());
        var bareProfile = await client.GetFromJsonAsync<ModelProfileSummary>(
            $"/api/assets/{bare.Id}/model-profile") ?? throw new InvalidOperationException();
        Assert.Empty(bareProfile.UvChannels!);
        Assert.Equal(bareProfile.PrimitiveCount, bareProfile.PrimitivesWithoutUvs);
        Assert.True(bareProfile.PrimitivesWithoutUvs > 0);
    }

    private static async Task RetryAsync(StudioApiFactory factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var job = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        job.State = JobState.Queued.ToString();
        job.Error = null;
        job.CompletedAt = null;
        await db.SaveChangesAsync();
    }

    private static async Task<JobSummary> QueueAsync(
        HttpClient client, Guid sourceAssetId, string name, int triangleBudget = 2000)
    {
        var queued = await client.PostAsJsonAsync("/api/models/preparation",
            new { sourceAssetId, name, triangleBudget });
        queued.EnsureSuccessStatusCode();
        return await queued.Content.ReadFromJsonAsync<JobSummary>() ?? throw new InvalidOperationException();
    }

    private static async Task<JobSummary> RunAsync(StudioApiFactory factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelGenerationService>();
        var result = await models.RunAsync(jobId, CancellationToken.None);
        return result.Value ?? throw new InvalidOperationException(result.Error);
    }

    private static string StoredPath(StudioApiFactory factory, AssetSummary asset)
    {
        using var scope = factory.Services.CreateScope();
        var assets = scope.ServiceProvider.GetRequiredService<AssetStore>();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = db.Assets.AsNoTracking().Single(candidate => candidate.Id == asset.Id);
        return assets.StoredFilePath(record.StoragePath)
            ?? throw new InvalidOperationException("The stored model is missing.");
    }

    private static async Task<AssetSummary> ImportModelAsync(HttpClient client, string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }

    private static async Task<AssetSummary> ImportImageAsync(HttpClient client, string fileName)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent([.. png, .. System.Text.Encoding.UTF8.GetBytes(fileName)]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AssetSummary>() ?? throw new InvalidOperationException();
    }
}
