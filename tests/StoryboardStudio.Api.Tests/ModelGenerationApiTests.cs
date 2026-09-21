using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// One reference image, frozen into a request, compiled into a model candidate.
///
/// No GPU and no compiler are involved: the gateway is a controlled stand-in
/// that hands back a real GLB, which is what lets the application's own
/// behaviour be proved — lineage, refusal, restart, reconciliation, staleness
/// and duplicate delivery — without spending an hour of hardware to learn it.
/// </summary>
public sealed class ModelGenerationApiTests
{
    /// <summary>A compiler that answers exactly as told, and counts its calls.</summary>
    private sealed class ControlledCompiler : ICompilerGateway
    {
        public CompilerCapabilities Capabilities { get; set; } = Ready;
        public byte[]? Payload { get; set; } = ModelFixtures.RiggedFigure();
        public string? Failure { get; set; }
        public int Runs { get; private set; }

        /// <summary>
        /// Every stage asked for and what it was told to read, in order. A
        /// count alone cannot tell a route that ran twice from one that ran
        /// two different steps, which is the distinction these tests turn on.
        /// </summary>
        public List<(string Stage, string Source)> Calls { get; } = [];

        /// <summary>A stage that refuses, while the rest of the route works.</summary>
        public string? FailingStage { get; set; }

        /// <summary>
        /// A stage that writes its receipt and then dies before its output —
        /// the other order of the same interruption, and the one that can pair
        /// a new receipt with an old output if the remains were never cleared.
        /// </summary>
        public string? ReceiptThenDieStage { get; set; }

        public static CompilerStage Stage(string name) =>
            new(name, name is "geometry" or "remesh" or "uv-unwrap" or "texture"
                    ? "powershell" : "blender",
                $"The {name} stage.", $"reference-asset-compiler.{name}.v1", true, [],
                // A staged mesh is a .blend where everything else is a .glb,
                // and the stage that reads it refuses anything else.
                OutputSuffix: name switch
                {
                    "stage-mesh" => ".blend",
                    "uv-unwrap" => ".obj",
                    _ => ".glb",
                },
                Colours: name == "glass"
                    ? [new CompilerColour("teal", "teal or cyan"), new CompilerColour("amber", "amber or orange")]
                    : null);

        public static CompilerCapabilities Ready => new(
            Installed: true, Commissioned: true, Version: "reference-asset-compiler 0.1.2",
            Checkout: "C:/checkout", Blender: "C:/blender.exe",
            Stages: [Stage("geometry"), Stage("stage-mesh"), Stage("remesh"),
                     Stage("uv-unwrap"), Stage("texture"), Stage("glass"),
                     Stage("browser-payload"), Stage("paint-head"), Stage("compress-textures")]);

        /// <summary>A compiler from before heads could be painted on their own.</summary>
        public static CompilerCapabilities WithoutHero => Ready with
        {
            Stages = [.. Ready.Stages.Where(stage => stage.Stage is not ("paint-head" or "compress-textures"))],
        };

        public Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);

        /// <summary>Per stage, what it was told beyond its three paths.</summary>
        public Dictionary<string, IReadOnlyDictionary<string, string>> Options { get; } = [];

        public async Task<CompilerStageRun> RunStageAsync(
            string stage, string sourcePath, string outputPath, string reportPath,
            CancellationToken cancellationToken,
            IEnumerable<KeyValuePair<string, string>>? options = null)
        {
            Runs += 1;
            Calls.Add((stage, Path.GetFileName(sourcePath)));
            Options[stage] = (options ?? []).ToDictionary(item => item.Key, item => item.Value);
            if (ReceiptThenDieStage == stage)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, "{\"schema\":\"from-the-second-attempt\"}", cancellationToken);
                return new CompilerStageRun(false, stage, 1, 0.1, null, null,
                    $"The {stage} stage died after writing its receipt.", ["died"]);
            }
            if (Failure is not null || FailingStage == stage)
            {
                // A stage that dies partway leaves a truncated file behind, as
                // a real exporter does. Whether that counts as an answer is
                // decided by the run's own verdict, not by the file existing.
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllBytesAsync(outputPath, "truncated"u8.ToArray(), cancellationToken);
                return new CompilerStageRun(false, stage, 1, 0.1, null, null,
                    Failure ?? $"The {stage} stage refused.", ["refused"]);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, Payload!, cancellationToken);
            var receipt = JsonSerializer.Serialize(new
            {
                schema = $"reference-asset-compiler.{stage}.v1",
                payload = outputPath,
                payload_sha256 = "stand-in",
            });
            await File.WriteAllTextAsync(reportPath, receipt, cancellationToken);
            return new CompilerStageRun(true, stage, 0, 0.5, outputPath, receipt, null, ["exported"]);
        }
    }

    /// <summary>
    /// The queue worker is off by default here: these tests drive RunAsync
    /// directly, and a worker racing them would make "how many times did the
    /// compiler run" a question about timing rather than about behaviour. One
    /// test below turns it on deliberately.
    /// </summary>
    private static StudioApiFactory Factory(ControlledCompiler compiler, bool worker = false) =>
        new(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true,
            startGenerationWorker: worker,
            configureServices: services => services.AddSingleton<ICompilerGateway>(compiler));

    [Fact]
    public async Task AFrozenReferenceBecomesAModelCandidateThatNamesWhereItCameFrom()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "figure-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Field scout", size = "knee" });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Model", job.RootElement.GetProperty("workType").GetString());
        Assert.Equal("Queued", job.RootElement.GetProperty("state").GetString());

        var finished = await RunAsync(factory, jobId);
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(100, finished.Progress);
        Assert.NotNull(finished.OutputAssetId);

        // The model is a real, inspectable library model, not a blob.
        using var profile = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/assets/{finished.OutputAssetId}/model-profile") ?? throw new InvalidOperationException();
        Assert.Equal(17, profile.RootElement.GetProperty("rig").GetProperty("boneCount").GetInt32());

        // Lineage: the exact reference, its exact bytes, and the compiler that
        // did it, readable by the artist and recorded on the job.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var asset = await db.Assets.SingleAsync(candidate => candidate.Id == finished.OutputAssetId);
        Assert.Contains("figure-reference", asset.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(reference.ContentHash[..12], asset.Notes, StringComparison.Ordinal);
        Assert.Contains("0.1.2", asset.Notes, StringComparison.Ordinal);

        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.Equal(reference.Id, result.RootElement.GetProperty("sourceAssetId").GetGuid());
        Assert.Equal(reference.ContentHash, result.RootElement.GetProperty("frozenSourceHash").GetString());
        Assert.False(result.RootElement.GetProperty("staleSource").GetBoolean());
    }

    [Fact]
    public async Task AMissingCapabilityBlocksBeforeAnythingIsSubmitted()
    {
        var compiler = new ControlledCompiler
        {
            Capabilities = CompilerCapabilities.Absent("The Reference Asset Compiler was not found."),
        };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "blocked-reference.png");

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/generation/readiness")
            ?? throw new InvalidOperationException();
        Assert.False(readiness.RootElement.GetProperty("canRun").GetBoolean());
        Assert.False(readiness.RootElement.GetProperty("installed").GetBoolean());

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Never built", size = "knee" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);

        // Nothing was queued, so nothing is waiting to surprise anyone later.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Empty(await db.Jobs.Where(job => job.WorkType == "Model").ToArrayAsync());
    }

    [Fact]
    public async Task AnUncommissionedRouteIsReadyButRefusesToSubmit()
    {
        var compiler = new ControlledCompiler
        {
            Capabilities = ControlledCompiler.Ready with { Commissioned = false },
        };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "uncommissioned-reference.png");

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/generation/readiness")
            ?? throw new InvalidOperationException();
        // Capability and permission are different answers.
        Assert.True(readiness.RootElement.GetProperty("installed").GetBoolean());
        Assert.False(readiness.RootElement.GetProperty("commissioned").GetBoolean());
        Assert.False(readiness.RootElement.GetProperty("canRun").GetBoolean());
        Assert.Contains("not commissioned", readiness.RootElement.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Not yet", size = "knee" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
    }

    [Fact]
    public async Task MissingGlassStageRefusesBeforeQueuingTheExpensiveRoute()
    {
        var compiler = new ControlledCompiler
        {
            Capabilities = ControlledCompiler.Ready with
            {
                Stages = [.. ControlledCompiler.Ready.Stages.Where(stage => stage.Stage != "glass")],
            },
        };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "glass-preflight.png");
        var response = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Unavailable glazing", size = "knee", glassColour = "teal" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, compiler.Runs);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StudioDbContext>().Jobs
            .Where(job => job.WorkType == "Model").ToArrayAsync());
    }

    [Fact]
    public async Task InvalidOutputNeverBecomesAUsableModel()
    {
        var compiler = new ControlledCompiler { Payload = "this is not a GLB"u8.ToArray() };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "broken-output-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Broken", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var finished = await RunAsync(factory, job.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Null(finished.OutputAssetId);
        Assert.Contains("GLB", finished.Error!, StringComparison.OrdinalIgnoreCase);

        // The library is untouched: a refused candidate is not a model.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Empty(await db.Assets.Where(asset => asset.Kind == "Model").ToArrayAsync());
    }

    [Fact]
    public async Task AnInterruptedRunIsReconciledRatherThanSilentlyRepeated()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "interrupted-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Interrupted", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        var first = await RunAsync(factory, jobId);
        Assert.Equal(JobState.Completed, first.State);
        // The whole route: a mesh from the picture, its real size, a runtime
        // budget, and only then a file a browser can load.
        Assert.Equal(6, compiler.Runs);

        // The delivery arrives a second time. No stage is run again and no
        // second model appears.
        var again = await RunAsync(factory, jobId);
        Assert.Equal(first.OutputAssetId, again.OutputAssetId);
        Assert.Equal(6, compiler.Runs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Single(await db.Assets.Where(asset => asset.Kind == "Model").ToArrayAsync());
        Assert.Equal(2, (await db.Jobs.SingleAsync(candidate => candidate.Id == jobId)).DeliveryCount);

        // Nor does the second delivery write the story down twice. Content
        // addressing already stops a duplicate asset; this is what stops a
        // duplicate record of where it came from.
        var delivered = await db.Assets.SingleAsync(asset => asset.Id == first.OutputAssetId);
        var mentions = delivered.Notes.Split("Generated from").Length - 1;
        Assert.Equal(1, mentions);
    }

    [Fact]
    public async Task HalfAnAnswerIsRunAgainOutLoudRatherThanQuietly()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "half-answer-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Half an answer", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // A stage that died between writing its output and writing its receipt
        // leaves exactly this behind, for the step it was on.
        var workspace = Path.Combine(factory.DataRoot, "generated-models", jobId.ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllBytesAsync(
            Path.Combine(workspace, "step-1-geometry.glb"), "half written"u8.ToArray());

        var finished = await RunAsync(factory, jobId);

        // It is run again, because half an answer is not an answer, and the
        // rerun is on the record rather than hidden inside a phase that passed.
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(6, compiler.Runs);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.True(result.RootElement.GetProperty("rerunAfterIncompleteAnswer").GetBoolean());

        // And the delivered model is the compiler's, not the half-written file.
        using var profile = await client.GetFromJsonAsync<JsonDocument>(
            $"/api/assets/{finished.OutputAssetId}/model-profile") ?? throw new InvalidOperationException();
        Assert.Equal(17, profile.RootElement.GetProperty("rig").GetProperty("boneCount").GetInt32());
    }

    [Fact]
    public async Task AStaleSourceLeavesTheOutputAsAnOlderSourceCandidate()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "moving-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "From an older frame", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // The reference moves on after the request was frozen.
        using (var moving = factory.Services.CreateScope())
        {
            var db = moving.ServiceProvider.GetRequiredService<StudioDbContext>();
            var asset = await db.Assets.SingleAsync(candidate => candidate.Id == reference.Id);
            asset.ContentHash = new string('a', 64);
            await db.SaveChangesAsync();
        }

        var finished = await RunAsync(factory, jobId);

        // It is still a candidate, and it says which source it came from.
        Assert.Equal(JobState.Completed, finished.State);
        Assert.NotNull(finished.OutputAssetId);
        Assert.Contains("older", finished.Phase, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db2.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.True(result.RootElement.GetProperty("staleSource").GetBoolean());
        var asset2 = await db2.Assets.SingleAsync(candidate => candidate.Id == finished.OutputAssetId);
        Assert.Contains("has changed since", asset2.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARestartResumesTheSameJobIdentity()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-model-gen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid jobId;
            var compiler = new ControlledCompiler();
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false,
                startGenerationWorker: false,
                configureServices: services => services.AddSingleton<ICompilerGateway>(compiler)))
            using (var client = factory.CreateClient())
            {
                var reference = await ImportImageAsync(client, "restart-reference.png");
                var queued = await client.PostAsJsonAsync("/api/models/generation",
                    new { sourceAssetId = reference.Id, name = "Survives a restart", size = "knee" });
                using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                jobId = job.RootElement.GetProperty("id").GetGuid();
            }

            // Close the application and open it again on the same data root.
            using (var reopened = new StudioApiFactory(dataRoot, deleteDataRoot: false,
                startGenerationWorker: false,
                configureServices: services => services.AddSingleton<ICompilerGateway>(compiler)))
            {
                using var scope = reopened.Services.CreateScope();
                var models = scope.ServiceProvider.GetRequiredService<ModelGenerationService>();
                var recoverable = await models.RecoverableJobsAsync(CancellationToken.None);

                // The same job, by the same identity, not a fresh one.
                Assert.Contains(jobId, recoverable);
                var finished = await models.RunAsync(jobId, CancellationToken.None);
                Assert.Equal(jobId, finished.Value!.Id);
                Assert.Equal(JobState.Completed, finished.Value.State);
            }
        }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task TheQueueCarriesTheWorkWithoutAnybodyWatching()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler, worker: true);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "unattended-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Unattended", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // Nobody calls RunAsync here. The worker claims the lease, runs the
        // stage and delivers, which is what lets an artist leave the screen.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JobRecord? finished = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            finished = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == jobId);
            if (finished is not null && finished.State is "Completed" or "Failed") break;
            await Task.Delay(100);
        }

        Assert.NotNull(finished);
        Assert.Equal("Completed", finished!.State);
        Assert.NotNull(finished.OutputAssetId);
        Assert.Equal(100, finished.Progress);
    }

    /// <summary>
    /// A picture is not a mesh and a mesh is not a payload. The payload export
    /// reads a mesh, so asking it to read an image was never going to work; the
    /// route is two stages, and the second reads what the first wrote.
    /// </summary>
    [Fact]
    public async Task TheRouteMakesAMeshFromThePictureAndAPayloadFromTheMesh()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "two-stage-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Two stages", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        var finished = await RunAsync(factory, jobId);

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(["geometry", "stage-mesh", "remesh", "uv-unwrap", "texture", "browser-payload"],
            compiler.Calls.Select(call => call.Stage));
        // The order is not the whole claim: each step must read what the one
        // before it wrote, not the picture the generator read.
        Assert.EndsWith(".png", compiler.Calls[0].Source, StringComparison.Ordinal);
        Assert.Equal("step-1-geometry.glb", compiler.Calls[1].Source);
        // Each step is named for what the stage actually writes. Naming a
        // staged mesh .glb produced a file the reducer refused to open, on a
        // machine where every other part of the route had already worked.
        Assert.Equal("step-2-stage-mesh.blend", compiler.Calls[2].Source);
        Assert.Equal("step-3-remesh.glb", compiler.Calls[3].Source);
        Assert.Equal("step-4-uv-unwrap.obj", compiler.Calls[4].Source);
        Assert.Equal("step-5-texture.glb", compiler.Calls[5].Source);

        // The artist's size reaches the stage that applies it, and nothing
        // else invents one: a guessed size would silently invalidate every
        // measurement the reduction gate makes afterwards.
        Assert.Equal("knee", compiler.Options["stage-mesh"]["size"]);
        // A browser studio asks for browser-scale geometry rather than the
        // generator's production default, which costs minutes and millions of
        // triangles that the reduction throws away again.
        Assert.Equal("256", compiler.Options["geometry"]["octree-resolution"]);
        Assert.Equal("20000", compiler.Options["remesh"]["triangle-budget"]);
        // The painter is conditioned on the same picture the geometry came
        // from, and only the caller knows which picture an asset is of.
        Assert.EndsWith(".png", compiler.Options["texture"]["reference"], StringComparison.Ordinal);
        // A generated prop is unfolded as the triangle mesh it already is.
        Assert.True(compiler.Options["uv-unwrap"].ContainsKey("allow-triangulated-glb"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.Equal(["geometry", "stage-mesh", "remesh", "uv-unwrap", "texture", "browser-payload"],
            result.RootElement.GetProperty("route").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("knee", result.RootElement.GetProperty("size").GetString());
        // Each step's receipt is recorded, not just the last one's: "the route
        // succeeded" hides which half of it actually ran.
        var steps = result.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(6, steps.Length);
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(
            step.GetProperty("receiptSha256").GetString())));
    }

    [Fact]
    public async Task AHeroIsRebuiltLargerPaintedLargerAndHasItsHeadPaintedTwice()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "hero-reference.png");

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/generation/readiness")
            ?? throw new InvalidOperationException();
        // Offered in the artist's terms, and only where the compiler can do it.
        Assert.Equal(["set", "hero"],
            readiness.RootElement.GetProperty("details").EnumerateArray()
                .Select(choice => choice.GetProperty("detail").GetString()));

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Hero", size = "head", detail = "hero" });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var finished = await RunAsync(factory, job.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(["geometry", "stage-mesh", "remesh", "uv-unwrap", "texture", "paint-head",
                      "browser-payload", "compress-textures"],
            compiler.Calls.Select(call => call.Stage));
        // The head is painted on the body's own paint, and what is compressed
        // is exactly what would otherwise have been delivered.
        Assert.Equal("step-5-texture.glb", compiler.Calls[5].Source);
        Assert.Equal("step-6-paint-head.glb", compiler.Calls[6].Source);
        Assert.Equal("step-7-browser-payload.glb", compiler.Calls[7].Source);

        // Every number a hero stands for reaches the stage that applies it.
        Assert.Equal("384", compiler.Options["geometry"]["octree-resolution"]);
        Assert.Equal("80000", compiler.Options["remesh"]["triangle-budget"]);
        Assert.Equal("72000", compiler.Options["remesh"]["target-triangles"]);
        Assert.Equal("640", compiler.Options["remesh"]["voxel-resolution"]);
        Assert.Equal("2", compiler.Options["remesh"]["smooth-iterations"]);
        Assert.Equal("4096", compiler.Options["texture"]["atlas"]);
        Assert.Equal("4096", compiler.Options["paint-head"]["atlas"]);
        Assert.Equal("0.78", compiler.Options["paint-head"]["head-from"]);
        // The head pass crops the same picture itself; nobody hands it a crop.
        Assert.Equal(compiler.Options["texture"]["reference"], compiler.Options["paint-head"]["reference"]);
        Assert.Equal("4096", compiler.Options["compress-textures"]["colour-size"]);
        Assert.Equal("2048", compiler.Options["compress-textures"]["data-size"]);
        Assert.Equal("92", compiler.Options["compress-textures"]["quality"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == finished.Id);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.Equal("hero", result.RootElement.GetProperty("detail").GetString());
        // The delivered model says what it was made as.
        var delivered = await db.Assets.SingleAsync(asset => asset.Id == finished.OutputAssetId);
        Assert.Contains("Made as a hero", delivered.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetDressingIsTheDefaultAndStillPaintsLossless()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "set-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Set", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var finished = await RunAsync(factory, job.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(JobState.Completed, finished.State);
        Assert.DoesNotContain("paint-head", compiler.Calls.Select(call => call.Stage));
        Assert.DoesNotContain("compress-textures", compiler.Calls.Select(call => call.Stage));
        // A 2048 sheet, but through the runner that writes it lossless: the
        // painter's own JPEG was the first of three lossy passes.
        Assert.Equal("2048", compiler.Options["texture"]["atlas"]);
        Assert.Equal("256", compiler.Options["geometry"]["octree-resolution"]);
    }

    [Fact]
    public async Task AHeroIsRefusedWhereTheCompilerCannotPaintAHeadOnItsOwn()
    {
        var compiler = new ControlledCompiler { Capabilities = ControlledCompiler.WithoutHero };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "no-hero-reference.png");

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/generation/readiness")
            ?? throw new InvalidOperationException();
        Assert.True(readiness.RootElement.GetProperty("canRun").GetBoolean());
        Assert.Equal(["set"],
            readiness.RootElement.GetProperty("details").EnumerateArray()
                .Select(choice => choice.GetProperty("detail").GetString()));

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Hero", size = "head", detail = "hero" });
        // Refused before anything is queued, by name, rather than an hour in.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("paint-head", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, compiler.Runs);

        var nonsense = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Hero", size = "head", detail = "cinematic" });
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
    }

    [Fact]
    public async Task AStepAlreadyFinishedIsNotRunAgainAfterAnInterruption()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "resume-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Resumed", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // The job reached the end of the first step and died before the second.
        // Both halves of that step's answer are on disk, so it is whole.
        var workspace = Path.Combine(factory.DataRoot, "generated-models", jobId.ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllBytesAsync(
            Path.Combine(workspace, "step-1-geometry.glb"), ModelFixtures.RiggedFigure());
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "step-1-geometry.json"), "{\"schema\":\"stand-in\"}");

        var finished = await RunAsync(factory, jobId);

        Assert.Equal(JobState.Completed, finished.State);
        // The whole point of keeping a result per step: the generator is the
        // expensive one, and asking a GPU to build the same mesh a second time
        // is the cost of resuming badly.
        Assert.Equal(["stage-mesh", "remesh", "uv-unwrap", "texture", "browser-payload"],
            compiler.Calls.Select(call => call.Stage));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        var steps = result.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        // Adopted rather than run, and the delivery says which it was.
        Assert.True(steps[0].GetProperty("adopted").GetBoolean());
        Assert.False(steps[1].GetProperty("adopted").GetBoolean());
        Assert.Equal(6, steps.Length);
        Assert.False(result.RootElement.GetProperty("rerunAfterIncompleteAnswer").GetBoolean());
    }

    /// <summary>
    /// The reason a half-answer is cleared rather than merely rerun over.
    ///
    /// Two interruptions in a row can leave an output from one attempt beside a
    /// receipt from the next. Both files exist, so the reconcile path would
    /// read them as one whole answer and adopt them — a mesh and a receipt that
    /// describe different runs, agreeing with nothing. Clearing the remains
    /// before the rerun is what makes that pair impossible to assemble.
    /// </summary>
    [Fact]
    public async Task AnOutputFromOneAttemptCannotPairWithAReceiptFromTheNext()
    {
        var compiler = new ControlledCompiler { ReceiptThenDieStage = "geometry" };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "mismatched-pair-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Mismatched", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // What the first interruption left: an output and no receipt.
        var workspace = Path.Combine(factory.DataRoot, "generated-models", jobId.ToString("N"));
        var output = Path.Combine(workspace, "step-1-geometry.glb");
        Directory.CreateDirectory(workspace);
        await File.WriteAllBytesAsync(output, "from the first attempt"u8.ToArray());

        // The second attempt writes its receipt and then dies.
        var finished = await RunAsync(factory, jobId);
        Assert.Equal(JobState.Failed, finished.State);

        // The first attempt's output must not still be sitting there beside the
        // second attempt's receipt, because the next run would adopt the two of
        // them together and never notice they came from different runs.
        var receipt = Path.Combine(workspace, "step-1-geometry.json");
        Assert.True(File.Exists(receipt), "the second attempt wrote its receipt");
        Assert.False(File.Exists(output), "the first attempt's output survived the rerun");
    }

    /// <summary>
    /// A generator normalises, so its output arrives about two metres tall
    /// whatever the subject, and the reduction gate measures in millimetres.
    /// The same lantern was rejected at 1.99 m and passed at 0.42 m. Defaulting
    /// a size here would not be a convenience; it would turn every measurement
    /// after it into a number about nothing.
    /// </summary>
    [Fact]
    public async Task AModelWithNoSizeIsRefusedRatherThanGuessedAt()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "sizeless-reference.png");

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "No size given" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
        Assert.Contains("how big", await refused.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAdjustmentThatMeansADifferentSizeIsRefused()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "over-adjusted-reference.png");

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Wildly adjusted", size = "knee", sizeAdjust = 40.0 });

        // Accepting it would record "knee" against a height nothing like a
        // knee, which is worse than refusing.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
    }

    /// <summary>
    /// Nothing in a mesh says which faces are glass: a pane and the frame
    /// around it are the same surface. The paint says so, so the artist names
    /// the colour — and a model with none, which is most of them, gets no
    /// glazing step at all rather than a stage guessing which colour was a
    /// window and turning an ordinary painted surface see-through.
    /// </summary>
    [Fact]
    public async Task GlassIsOnlyInTheRouteWhenTheArtistSaysThereIsGlass()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "glazed-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Glazed", size = "knee", glassColour = "teal" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        var finished = await RunAsync(factory, jobId);

        Assert.Equal(JobState.Completed, finished.State);
        // Glazing sits after the paint it reads and before the export, because
        // it needs the colours and the export needs the result.
        Assert.Equal(
            ["geometry", "stage-mesh", "remesh", "uv-unwrap", "texture", "glass", "browser-payload"],
            compiler.Calls.Select(call => call.Stage));
        Assert.Equal("teal", compiler.Options["glass"]["colour"]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var record = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
        using var result = JsonDocument.Parse(record.ResultJson!);
        Assert.Equal("teal", result.RootElement.GetProperty("glassColour").GetString());
    }

    [Fact]
    public async Task AModelWithNoGlassNeverRunsTheGlazingStage()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "solid-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Solid", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();

        var finished = await RunAsync(factory, job.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(JobState.Completed, finished.State);
        // Most props have no glass, and running the stage anyway would mean
        // guessing a colour on a model that has no panes to find.
        Assert.DoesNotContain("glass", compiler.Calls.Select(call => call.Stage));
        Assert.Equal(6, compiler.Runs);
    }

    [Fact]
    public async Task AColourTheCompilerDoesNotOfferIsRefusedBeforeAnythingRuns()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "chartreuse-reference.png");

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Impossible", size = "knee", glassColour = "chartreuse" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
        var message = await refused.Content.ReadAsStringAsync();
        // Named with what is actually on offer, from the compiler's own list.
        Assert.Contains("teal", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure the artist has seen stays seen. Dismissal used to live in the
    /// page, so every reload raised every failure the project had ever had —
    /// on every screen, for ever, with no way to stop it.
    /// </summary>
    [Fact]
    public async Task AFailureTheArtistHasSeenStaysSeen()
    {
        var compiler = new ControlledCompiler { Failure = "The stage refused." };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "acknowledged-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Doomed", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();
        var failed = await RunAsync(factory, jobId);
        Assert.Equal(JobState.Failed, failed.State);

        var acknowledged = await client.PostAsync($"/api/jobs/{jobId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);

        // It survives the reload that used to bring it back.
        using var snapshot = await client.GetFromJsonAsync<JsonDocument>("/api/studio")
            ?? throw new InvalidOperationException();
        var seen = snapshot.RootElement.GetProperty("jobs").EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetGuid() == jobId);
        Assert.NotEqual(JsonValueKind.Null, seen.GetProperty("acknowledgedAt").ValueKind);
    }

    [Fact]
    public async Task WorkStillRunningCannotBeWavedAway()
    {
        var compiler = new ControlledCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "running-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Underway", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();

        var refused = await client.PostAsync(
            $"/api/jobs/{job.RootElement.GetProperty("id").GetGuid()}/acknowledge", null);

        // Hiding a queued job would hide work that is still holding a lease.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task AStageMissingFromTheCompilerRefusesTheWholeRouteAndNamesIt()
    {
        var compiler = new ControlledCompiler
        {
            // A machine with Blender but no geometry weights: the common case,
            // and the second half of the route would work perfectly on it.
            Capabilities = ControlledCompiler.Ready with
            {
                Stages =
                [
                    new CompilerStage("geometry", "powershell", "Needs a GPU.",
                        "reference-asset-compiler.geometry-candidate.v1", false, ["legacy-root"]),
                    ControlledCompiler.Stage("stage-mesh"),
                    ControlledCompiler.Stage("remesh"),
                    ControlledCompiler.Stage("uv-unwrap"),
                    ControlledCompiler.Stage("texture"),
                    ControlledCompiler.Stage("browser-payload"),
                ],
            },
        };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "no-weights-reference.png");

        using var readiness = await client.GetFromJsonAsync<JsonDocument>("/api/models/generation/readiness")
            ?? throw new InvalidOperationException();

        Assert.False(readiness.RootElement.GetProperty("canRun").GetBoolean());
        // Name the step that is the problem: "something is missing" sends an
        // artist looking at the wrong half of an install.
        var detail = readiness.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("geometry", detail, StringComparison.Ordinal);
        Assert.Contains("legacy-root", detail, StringComparison.Ordinal);
        Assert.Contains("legacy-root",
            readiness.RootElement.GetProperty("missing").EnumerateArray().Select(item => item.GetString()));

        var refused = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Never built", size = "knee" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        // Nothing ran, including the half of the route that could have.
        Assert.Equal(0, compiler.Runs);
    }

    [Fact]
    public async Task AFailedFirstStepNeverReachesTheSecond()
    {
        var compiler = new ControlledCompiler { FailingStage = "geometry" };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "failing-first-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Stops early", size = "knee" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();

        var finished = await RunAsync(factory, job.RootElement.GetProperty("id").GetGuid());

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Null(finished.OutputAssetId);
        // Feeding the next step the truncated file the failed stage left behind
        // would turn one honest failure into a confusing one.
        Assert.Equal(["geometry"], compiler.Calls.Select(call => call.Stage));
        // The compiler's own reason, not a guess made from the file system.
        // A stage that dies partway does leave a file, so the run's verdict is
        // what decides whether there is an answer — the file's existence is not.
        Assert.Contains("refused", finished.Error!, StringComparison.Ordinal);
        Assert.Contains("geometry", finished.Error!, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Empty(await db.Assets.Where(asset => asset.Kind == "Model").ToArrayAsync());
    }

    private static async Task<JobSummary> RunAsync(StudioApiFactory factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var models = scope.ServiceProvider.GetRequiredService<ModelGenerationService>();
        var result = await models.RunAsync(jobId, CancellationToken.None);
        return result.Value ?? throw new InvalidOperationException(result.Error);
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
        return await response.Content.ReadFromJsonAsync<AssetSummary>()
            ?? throw new InvalidOperationException();
    }
}
