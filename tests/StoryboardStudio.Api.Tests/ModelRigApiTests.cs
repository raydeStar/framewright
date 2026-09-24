using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// M14: one prepared humanoid rigged through the compiler's rig stage, and
/// left as a candidate for a person to judge by its pose suite.
///
/// No Blender runs here. The stand-in answers the way `rac run-stage rig`
/// does: a rigged GLB, a receipt that names its evidence directory, and a
/// views.json of pose renders beside it. The source is the rigged fixture with
/// its skin stripped, so the triangles match and only the skeleton differs.
/// </summary>
public sealed class ModelRigApiTests
{
    private sealed class RigCompiler : ICompilerGateway
    {
        public byte[] Payload { get; set; } = ModelFixtures.RiggedFigure();
        public string? FailingStage { get; set; }
        /// <summary>Where the receipt says the evidence is. Null means beside the output, as the real stage does.</summary>
        public string? EvidenceOverride { get; set; }
        public List<string> Calls { get; } = [];

        private static CompilerStage Stage(string name) => new(
            name, name is "review-views" or "rig" ? "powershell" : "blender",
            $"The {name} stage.", $"reference-asset-compiler.{name}.v1", true, [],
            OutputSuffix: name == "review-views" ? "" : ".glb");

        public Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CompilerCapabilities(
                Installed: true, Commissioned: true, Version: "reference-asset-compiler 0.1.3",
                Checkout: "C:/checkout", Blender: "C:/blender.exe",
                Stages: [Stage("review-views"), Stage("rig"), Stage("browser-payload")]));

        public Task<CompilerAnimationExport> ExportAnimationsAsync(
            string sourcePath, IReadOnlyList<string> clips, CancellationToken cancellationToken) =>
            Task.FromResult(new CompilerAnimationExport(null, "Not used here."));

        public async Task<CompilerStageRun> RunStageAsync(
            string stage, string sourcePath, string outputPath, string reportPath,
            CancellationToken cancellationToken, IEnumerable<KeyValuePair<string, string>>? options = null)
        {
            Calls.Add(stage);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            if (FailingStage == stage)
                return new CompilerStageRun(false, stage, 1, 0.1, null, null,
                    "The humanoid could not be rigged: derive humanoid landmarks failed", ["refused"]);

            string receipt;
            if (stage == "review-views")
            {
                Directory.CreateDirectory(outputPath);
                var of = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath, cancellationToken)));
                var views = await WriteViewsAsync(outputPath, of, [("front", "beauty"), ("side", "beauty")], cancellationToken);
                receipt = JsonSerializer.Serialize(new { schema = "reference-asset-compiler.review-views.v1", source_sha256 = of, views });
                await File.WriteAllTextAsync(Path.Combine(outputPath, "views.json"), receipt, cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(outputPath, Payload, cancellationToken);
                var of = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath, cancellationToken)));
                var attempt = Path.Combine(Path.GetDirectoryName(outputPath)!,
                    Path.GetFileNameWithoutExtension(outputPath) + "-rig-attempt001");
                var evidence = EvidenceOverride ?? Path.Combine(attempt, "evidence");
                Directory.CreateDirectory(evidence);
                var views = await WriteViewsAsync(evidence, of,
                    [("front", "elbows_bent"), ("side", "knees_bent"), ("front", "landmarks")], cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(evidence, "views.json"),
                    JsonSerializer.Serialize(new { schema = "reference-asset-compiler.rig-evidence.v1", source_sha256 = of, views }),
                    cancellationToken);
                receipt = JsonSerializer.Serialize(new
                {
                    schema = "reference-asset-compiler.rig-candidate.v1",
                    source_sha256 = of,
                    skeleton_profile = "ue5_manny_browser",
                    gate = new { passed = true },
                    deformation = new { passed = true },
                    evidence_directory = evidence,
                    production_grade = false,
                    requires_deformation_review = true,
                });
            }
            await File.WriteAllTextAsync(reportPath, receipt, cancellationToken);
            return new CompilerStageRun(true, stage, 0, 0.5, outputPath, receipt, null, ["done"]);
        }

        private static async Task<List<object>> WriteViewsAsync(
            string directory, string of, (string View, string Pass)[] wanted, CancellationToken cancellationToken)
        {
            var views = new List<object>();
            foreach (var (view, pass) in wanted)
            {
                var name = $"{pass}-{view}.png";
                var picture = Encoding.UTF8.GetBytes($"picture of {of} {name}");
                await File.WriteAllBytesAsync(Path.Combine(directory, name), picture, cancellationToken);
                views.Add(new { view, pass, file = name, sha256 = Convert.ToHexStringLower(SHA256.HashData(picture)) });
            }
            return views;
        }
    }

    [Fact]
    public async Task AHumanoidComesBackAsACandidateRevisionWithItsPoseSuite()
    {
        var compiler = new RigCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "hero.glb", Unrigged());

        var queued = await client.PostAsJsonAsync("/api/models/rig", new { sourceAssetId = source.Id, name = "Hero rig" });
        queued.EnsureSuccessStatusCode();
        var job = await queued.Content.ReadFromJsonAsync<JobSummary>() ?? throw new InvalidOperationException();
        Assert.Equal("Rig", job.Kind);
        var finished = await RunAsync(factory, job.Id);

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(["review-views", "rig", "review-views"], compiler.Calls);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var rigged = await db.Assets.SingleAsync(asset => asset.Id == finished.OutputAssetId);
            var original = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
            // Beside the source, not over it, and not accepted by anything but a person.
            Assert.Equal(original.RevisionFamilyId, rigged.RevisionFamilyId);
            Assert.False(rigged.IsCurrentRevision);
            Assert.True(original.IsCurrentRevision);
            Assert.Null(rigged.PreparationAcceptedAt);
            Assert.StartsWith("Rigged from hero", rigged.RevisionPrompt);
            Assert.Contains("bones on the UE5 Manny browser skeleton", rigged.RevisionPrompt);
        }

        var evidence = await client.GetFromJsonAsync<ModelPreparationEvidence>($"/api/jobs/{job.Id}/preparation-evidence")
            ?? throw new InvalidOperationException();
        Assert.NotNull(evidence.Source);
        Assert.NotNull(evidence.Derivative);
        Assert.NotNull(evidence.Deformation);
        Assert.Equal(["elbows_bent", "knees_bent", "landmarks"], evidence.Deformation!.Views.Select(view => view.Pass).ToArray());
        var pose = await client.GetAsync(evidence.Deformation.Views[0].Url);
        Assert.Equal(HttpStatusCode.OK, pose.StatusCode);
        Assert.StartsWith("picture of", Encoding.UTF8.GetString(await pose.Content.ReadAsByteArrayAsync()));
        // A name the manifest does not list is not evidence, whatever is on disk.
        var unlisted = evidence.Deformation.Views[0].Url.Replace("elbows_bent-front.png", "views.json", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(unlisted)).StatusCode);
    }

    [Fact]
    public async Task AModelThatAlreadyHasASkeletonIsNotRiggedAgain()
    {
        var compiler = new RigCompiler();
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "already.glb", ModelFixtures.RiggedFigure());

        var refused = await client.PostAsJsonAsync("/api/models/rig", new { sourceAssetId = source.Id, name = "Again" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already has a skeleton", await refused.Content.ReadAsStringAsync());
        Assert.Empty(compiler.Calls);
    }

    [Fact]
    public async Task ARigThatArrivesWithoutASkeletonIsRefusedAndAddsNothing()
    {
        var compiler = new RigCompiler { Payload = Unrigged() };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "hero.glb", Unrigged());
        var job = await QueueAsync(client, source.Id);

        var finished = await RunAsync(factory, job.Id);

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Contains("without a skeleton", finished.Error);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        Assert.Single(await db.Assets.Where(asset => asset.Kind == "Model").ToArrayAsync());
    }

    [Fact]
    public async Task EvidenceOutsideTheJobsWorkspaceIsNeverServed()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "framewright-foreign-evidence", Guid.NewGuid().ToString("N"));
        var compiler = new RigCompiler { EvidenceOverride = elsewhere };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        try
        {
            var source = await ImportModelAsync(client, "hero.glb", Unrigged());
            var job = await QueueAsync(client, source.Id);
            Assert.Equal(JobState.Completed, (await RunAsync(factory, job.Id)).State);

            var evidence = await client.GetFromJsonAsync<ModelPreparationEvidence>($"/api/jobs/{job.Id}/preparation-evidence")
                ?? throw new InvalidOperationException();
            // The receipt named a directory this job does not own, so there is
            // no pose suite to show rather than somebody else's pictures.
            Assert.Null(evidence.Deformation);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/jobs/{job.Id}/preparation-views/2/elbows_bent-front.png")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task ARefusedRigSaysWhyAndLeavesTheSourceAsItWas()
    {
        var compiler = new RigCompiler { FailingStage = "rig" };
        using var factory = Factory(compiler);
        using var client = factory.CreateClient();
        var source = await ImportModelAsync(client, "hero.glb", Unrigged());
        var job = await QueueAsync(client, source.Id);

        var finished = await RunAsync(factory, job.Id);

        Assert.Equal(JobState.Failed, finished.State);
        Assert.Contains("landmarks", finished.Error);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var original = await db.Assets.SingleAsync(asset => asset.Id == source.Id);
        Assert.Null(original.RevisionFamilyId);
        Assert.Single(await db.Assets.Where(asset => asset.Kind == "Model").ToArrayAsync());
    }

    /// <summary>The rigged fixture with its skin removed: the same triangles, no skeleton.</summary>
    private static byte[] Unrigged()
    {
        var bytes = ModelFixtures.RiggedFigure();
        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var document = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength))!.AsObject();
        document.Remove("skins");
        document.Remove("animations");
        foreach (var node in document["nodes"]!.AsArray()) node!.AsObject().Remove("skin");
        foreach (var mesh in document["meshes"]!.AsArray())
            foreach (var primitive in mesh!["primitives"]!.AsArray())
            {
                var attributes = primitive!["attributes"]!.AsObject();
                attributes.Remove("JOINTS_0");
                attributes.Remove("WEIGHTS_0");
            }
        var rewritten = Encoding.UTF8.GetBytes(document.ToJsonString());
        if (rewritten.Length % 4 != 0) rewritten = [.. rewritten, .. Enumerable.Repeat((byte)' ', 4 - rewritten.Length % 4)];
        var binary = bytes.AsSpan(20 + jsonLength).ToArray();
        var output = new List<byte>();
        output.AddRange(BitConverter.GetBytes(0x46546C67u));
        output.AddRange(BitConverter.GetBytes(2u));
        output.AddRange(BitConverter.GetBytes((uint)(12 + 8 + rewritten.Length + binary.Length)));
        output.AddRange(BitConverter.GetBytes((uint)rewritten.Length));
        output.AddRange(BitConverter.GetBytes(0x4E4F534Au));
        output.AddRange(rewritten);
        output.AddRange(binary);
        return [.. output];
    }

    private static StudioApiFactory Factory(RigCompiler compiler) =>
        new(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true,
            startGenerationWorker: false,
            configureServices: services => services.AddSingleton<ICompilerGateway>(compiler));

    private static async Task<JobSummary> QueueAsync(HttpClient client, Guid sourceAssetId)
    {
        var queued = await client.PostAsJsonAsync("/api/models/rig", new { sourceAssetId, name = "Hero rig" });
        queued.EnsureSuccessStatusCode();
        return await queued.Content.ReadFromJsonAsync<JobSummary>() ?? throw new InvalidOperationException();
    }

    private static async Task<JobSummary> RunAsync(StudioApiFactory factory, Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ModelGenerationService>().RunAsync(jobId, CancellationToken.None);
        return result.Value ?? throw new InvalidOperationException(result.Error);
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
}
