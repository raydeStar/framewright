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

        public static CompilerCapabilities Ready => new(
            Installed: true, Commissioned: true, Version: "reference-asset-compiler 0.1.2",
            Checkout: "C:/checkout", Blender: "C:/blender.exe",
            Stages: [new CompilerStage("browser-payload", "blender", "Export a browser GLB.",
                "reference-asset-compiler.browser-payload.v1", true, [])]);

        public Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);

        public async Task<CompilerStageRun> RunStageAsync(
            string stage, string sourcePath, string outputPath, string reportPath, CancellationToken cancellationToken)
        {
            Runs += 1;
            if (Failure is not null)
                return new CompilerStageRun(false, stage, 1, 0.1, null, null, Failure, ["refused"]);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, Payload!, cancellationToken);
            var receipt = JsonSerializer.Serialize(new
            {
                schema = "reference-asset-compiler.browser-payload.v1",
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
            new { sourceAssetId = reference.Id, name = "Field scout" });
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
            new { sourceAssetId = reference.Id, name = "Never built" });
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
            new { sourceAssetId = reference.Id, name = "Not yet" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0, compiler.Runs);
    }

    [Fact]
    public async Task InvalidOutputNeverBecomesAUsableModel()
    {
        var compiler = new ControlledCompiler { Payload = "this is not a GLB"u8.ToArray() };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var reference = await ImportImageAsync(client, "broken-output-reference.png");

        var queued = await client.PostAsJsonAsync("/api/models/generation",
            new { sourceAssetId = reference.Id, name = "Broken" });
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
            new { sourceAssetId = reference.Id, name = "Interrupted" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        var first = await RunAsync(factory, jobId);
        Assert.Equal(JobState.Completed, first.State);
        Assert.Equal(1, compiler.Runs);

        // The delivery arrives a second time. The stage is not run again and no
        // second model appears.
        var again = await RunAsync(factory, jobId);
        Assert.Equal(first.OutputAssetId, again.OutputAssetId);
        Assert.Equal(1, compiler.Runs);

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
            new { sourceAssetId = reference.Id, name = "Half an answer" });
        using var job = await queued.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var jobId = job.RootElement.GetProperty("id").GetGuid();

        // A stage that died between writing its payload and writing its receipt
        // leaves exactly this behind.
        var workspace = Path.Combine(factory.DataRoot, "generated-models", jobId.ToString("N"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllBytesAsync(Path.Combine(workspace, "payload.glb"), "half written"u8.ToArray());

        var finished = await RunAsync(factory, jobId);

        // It is run again, because half an answer is not an answer, and the
        // rerun is on the record rather than hidden inside a phase that passed.
        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(1, compiler.Runs);
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
            new { sourceAssetId = reference.Id, name = "From an older frame" });
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
                    new { sourceAssetId = reference.Id, name = "Survives a restart" });
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
            new { sourceAssetId = reference.Id, name = "Unattended" });
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
