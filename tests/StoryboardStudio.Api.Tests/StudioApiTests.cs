using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

public class StudioApiFactory : WebApplicationFactory<Program>
{
    private readonly bool deleteDataRoot;
    private readonly string? assetRoot;
    private readonly bool startGenerationWorker;
    private readonly IVideoMediaProbe? videoMediaProbe;
    /// <summary>Lets a test stand in for a collaborator the application talks to.</summary>
    private readonly Action<IServiceCollection>? configureServices;
    public string DataRoot { get; }
    public string DisabledCodexExecutable => Path.Combine(DataRoot, "codex-disabled-for-tests.exe");
    public string SkeletonProfilePath => Path.Combine(DataRoot, "rig-profiles");

    public StudioApiFactory()
        : this(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true) { }

    internal StudioApiFactory(Action<IServiceCollection> configureServices)
        : this(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true,
            configureServices: configureServices) { }

    internal StudioApiFactory(IVideoMediaProbe videoMediaProbe)
        : this(Path.Combine(Path.GetTempPath(), "storyboard-studio-tests", Guid.NewGuid().ToString("N")), true, videoMediaProbe: videoMediaProbe) { }

    internal StudioApiFactory(
        string dataRoot,
        bool deleteDataRoot,
        string? assetRoot = null,
        bool startGenerationWorker = true,
        IVideoMediaProbe? videoMediaProbe = null,
        Action<IServiceCollection>? configureServices = null)
    {
        this.configureServices = configureServices;
        DataRoot = Path.GetFullPath(dataRoot);
        this.deleteDataRoot = deleteDataRoot;
        this.assetRoot = assetRoot is null ? null : Path.GetFullPath(assetRoot);
        this.startGenerationWorker = startGenerationWorker;
        this.videoMediaProbe = videoMediaProbe;
        Directory.CreateDirectory(DataRoot);
        TestRigProfiles.WriteTo(SkeletonProfilePath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // WebApplication's Windows defaults include the Event Log provider. A
        // test host must never attempt to create/read a machine event source;
        // test runners already capture assertion failures and their own output.
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:1",
            ["Integrations:ComfyUi:SubmissionEnabled"] = "false",
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "false",
            // IntegrationDiscoveryService has advisory paths that resolve a
            // process directly. Point those paths at an intentionally absent
            // executable so ordinary API tests can never inherit a developer's
            // signed-in Codex CLI or spend a minute waiting for it.
            ["Integrations:Codex:Executable"] = DisabledCodexExecutable,
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "false",
            ["Integrations:ReferenceAssetCompiler:SkeletonProfilePath"] = SkeletonProfilePath,
            ["QwenTts:Enabled"] = "false",
            ["Studio:Backups:Enabled"] = "false",
            ["Studio:DataRoot"] = DataRoot,
            ["Studio:AssetRoot"] = assetRoot
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<StudioDbContext>();
            services.RemoveAll<DbContextOptions<StudioDbContext>>();
            services.AddDbContext<StudioDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(DataRoot, "test.db")};Pooling=False"));
            services.RemoveAll<ICodexRuntime>();
            services.AddSingleton<ICodexRuntime, UnavailableTestCodexRuntime>();
            // Most API tests use tiny container-signature fixtures rather than
            // encoded movies. Keep those tests deterministic; focused probe
            // tests below exercise the real metadata parser and mismatch gate.
            services.RemoveAll<IVideoMediaProbe>();
            services.AddSingleton(videoMediaProbe ?? new AcceptingTestVideoMediaProbe());
            services.AddSingleton<SerialProbeGenerationAdapter>();
            services.AddSingleton<IGenerationAdapter>(provider => provider.GetRequiredService<SerialProbeGenerationAdapter>());
            if (!startGenerationWorker)
            {
                var workerDescriptors = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(GenerationJobWorker))
                    .ToArray();
                foreach (var descriptor in workerDescriptors) services.Remove(descriptor);
            }
            // Last, so a test's stand-in wins over the application's own
            // registration of the same collaborator.
            configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (deleteDataRoot && Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
    }
}

internal sealed class AcceptingTestVideoMediaProbe : IVideoMediaProbe
{
    public Task<VideoMediaValidation> ValidateAsync(
        string path,
        VideoMediaExpectation expectation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new VideoMediaMetadata(
            expectation.Width,
            expectation.Height,
            expectation.FramesPerSecond,
            expectation.FrameCount,
            expectation.FrameCount / (double)expectation.FramesPerSecond);
        return Task.FromResult(new VideoMediaValidation(true, "Test fixture matches the requested stream contract.", metadata));
    }
}

internal sealed class ControllableTestVideoMediaProbe : IVideoMediaProbe
{
    public bool Accept { get; set; }

    public Task<VideoMediaValidation> ValidateAsync(
        string path,
        VideoMediaExpectation expectation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Accept)
            return Task.FromResult(new VideoMediaValidation(
                false,
                $"The rendered video is 24 fps and 124 frames; the project requires {expectation.FramesPerSecond} fps and {expectation.FrameCount} frames."));
        var metadata = new VideoMediaMetadata(
            expectation.Width,
            expectation.Height,
            expectation.FramesPerSecond,
            expectation.FrameCount,
            expectation.FrameCount / (double)expectation.FramesPerSecond);
        return Task.FromResult(new VideoMediaValidation(true, "Exact encoded stream contract.", metadata));
    }
}

public sealed class SerialProbeGenerationAdapter : IGenerationAdapter
{
    private int active;
    private int executions;
    private int maximum;
    private readonly object contextGate = new();
    private GenerationExecutionContext? lastContext;
    public int ExecutionCount => Volatile.Read(ref executions);
    public int MaximumConcurrency => Volatile.Read(ref maximum);
    public GenerationExecutionContext? LastContext { get { lock (contextGate) return lastContext; } }

    public GenerationAdapterSummary Describe() => new(
        "serial-probe", "Serial queue probe", "Test image adapter", "Connected",
        "Deliberately slow test adapter.", true, [GenerationRoute.FastDraft], [GenerationPurpose.Draft, GenerationPurpose.Video]);

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        lock (contextGate) lastContext = context;
        Interlocked.Increment(ref executions);
        var nowActive = Interlocked.Increment(ref active);
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (observed >= nowActive || Interlocked.CompareExchange(ref maximum, nowActive, observed) == observed) break;
        }
        try
        {
            await context.ReportAsync(45, "Serial probe rendering", cancellationToken);
            await Task.Delay(300, cancellationToken);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var unique = png.Concat(context.JobId.ToByteArray()).ToArray();
            return new(new MemoryStream(unique), $"{context.JobId:N}.png", "image/png", false);
        }
        finally { Interlocked.Decrement(ref active); }
    }
}

/// <summary>
/// Deliberately inert Codex boundary for ordinary API tests. Adapter contract
/// tests exercise Codex with their own explicit fake; the shared web host never
/// starts a workstation process or reads a real login.
/// </summary>
public sealed class UnavailableTestCodexRuntime : ICodexRuntime
{
    public CodexRuntimeStatus Inspect() => new(false, false, "Codex is disabled in the ordinary test host.");

    public Task<CodexProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
        => Task.FromResult(new CodexProcessResult(false, -1, "", "Codex is disabled in the ordinary test host."));
}

public sealed class VisualAuditTestCodexRuntime : ICodexRuntime
{
    public CodexRuntimeStatus Inspect() => new(true, true, "Logged in using ChatGPT");

    public Task<CodexProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        Assert.Contains("--image", arguments);
        Assert.Contains("strict visual continuity auditor", standardInput ?? string.Empty, StringComparison.Ordinal);
        return Task.FromResult(new CodexProcessResult(true, 0, """
            {
              "summary": "The generated frame contradicts the required staging.",
              "findings": [
                {
                  "category": "subject-count",
                  "severity": "Block",
                  "title": "Two wardens are missing",
                  "contractExpectation": "Two visibly distinct full-body wardens stand behind Mara.",
                  "observedImage": "No wardens are visible.",
                  "confidence": 0.99,
                  "suggestedRoute": "RebuildFromSketch",
                  "fixInstruction": "Rebuild from the saved composition with two distinct full-body wardens behind Mara."
                }
              ],
              "adoptedShotProposal": {
                "description": "Mara studies a lantern alone.",
                "action": "Mara leans toward the lantern.",
                "constraints": ["Mara is the only visible person"],
                "referenceIds": [],
                "rationale": "The wardens would be removed from this shot only."
              }
            }
            """, ""));
    }
}

public sealed class MixedVisualAuditTestCodexRuntime : ICodexRuntime
{
    public CodexRuntimeStatus Inspect() => new(true, true, "Logged in using ChatGPT");

    public Task<CodexProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
        => Task.FromResult(new CodexProcessResult(true, 0, """
            {
              "summary": "The cast and prop need separate decisions.",
              "findings": [
                {
                  "category": "subject-count", "severity": "Block", "title": "Wardens are absent",
                  "contractExpectation": "Two wardens stand behind Mara.", "observedImage": "No wardens are visible.",
                  "confidence": 0.99, "suggestedRoute": "RebuildFromSketch", "fixInstruction": "Restore both wardens."
                },
                {
                  "category": "prop", "severity": "Block", "title": "Lantern is oversized",
                  "contractExpectation": "The lantern is palm-sized.", "observedImage": "The lantern is table-sized.",
                  "confidence": 0.99, "suggestedRoute": "RebuildFromSketch", "fixInstruction": "Make the lantern palm-sized."
                }
              ],
              "adoptedShotProposal": {
                "description": "Mara studies a large lantern alone.", "action": "Mara studies.",
                "constraints": [], "referenceIds": [], "rationale": "Accept the absent guards and oversized lantern."
              }
            }
            """, ""));
}

public sealed class StudioApiTests : IClassFixture<StudioApiFactory>
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly HttpClient client;
    private readonly StudioApiFactory factory;

    public StudioApiTests(StudioApiFactory factory) { this.factory = factory; client = factory.CreateClient(); }

    [Fact]
    public async Task VisualAuditIsBoundToTheImageAndContractAndRequiresExplicitReconciliation()
    {
        await using var isolated = new StudioApiFactory();
        await using var audited = isolated.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICodexRuntime>();
            services.AddSingleton<ICodexRuntime, VisualAuditTestCodexRuntime>();
        }));
        using var auditedClient = audited.CreateClient();
        var image = await UploadImageAsync(auditedClient, TinyPng, "visual-audit.png");
        var created = await (await auditedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "QA-VA", "Visual audit", "Mara kneels while two wardens stand behind her.", 120,
            "35mm centered wide", "Mara studies a palm-size lantern.", [],
            ["Exactly two visibly distinct full-body wardens stand behind Mara."], image.Id))).Content.ReadFromJsonAsync<ShotSummary>();
        Assert.NotNull(created);

        var response = await auditedClient.PostAsJsonAsync($"/api/shots/{created.Id}/visual-audit", new RunVisualAuditRequest());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var audit = await response.Content.ReadFromJsonAsync<ShotVisualAuditSummary>();
        Assert.NotNull(audit);
        Assert.Equal("Blocked", audit.GateState);
        var finding = Assert.Single(audit.Findings);
        Assert.Equal(VisualCorrectionRoute.RebuildFromSketch, finding.SuggestedRoute);

        var persisted = await auditedClient.GetFromJsonAsync<ShotVisualAuditSummary>($"/api/shots/{created.Id}/visual-audit");
        Assert.Equal(audit.Id, persisted!.Id);
        var planResponse = await auditedClient.PostAsJsonAsync(
            $"/api/shots/{created.Id}/visual-audits/{audit.Id}/reconcile",
            new ReconcileVisualAuditRequest([new(finding.Id, VisualReconciliationAction.FixImage, "Keep the wardens behind the dais.")]));
        planResponse.EnsureSuccessStatusCode();
        var plan = await planResponse.Content.ReadFromJsonAsync<VisualReconciliationPlan>();
        Assert.NotNull(plan);
        Assert.True(plan.RequiresImageGeneration);
        Assert.Equal(VisualCorrectionRoute.RebuildFromSketch, plan.ImageRoute);
        Assert.Contains("Keep the wardens behind the dais", plan.GenerationDirection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdoptImagePreservesTheFullSceneAndAddsANarrowGenerationOverride()
    {
        await using var isolated = new StudioApiFactory();
        await using var audited = isolated.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICodexRuntime>();
            services.AddSingleton<ICodexRuntime, VisualAuditTestCodexRuntime>();
        }));
        using var auditedClient = audited.CreateClient();
        var image = await UploadImageAsync(auditedClient, TinyPng.Concat(new byte[] { 0x04 }).ToArray(), "visual-adopt.png");
        var created = await (await auditedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "QA-ADOPT", "Adopt visual", "Mara kneels while two wardens stand behind her.", 120,
            "35mm centered wide", "Mara studies a palm-size lantern.", [],
            ["Exactly two visibly distinct full-body wardens stand behind Mara."], image.Id))).Content.ReadFromJsonAsync<ShotSummary>();
        var auditResponse = await auditedClient.PostAsJsonAsync($"/api/shots/{created!.Id}/visual-audit", new RunVisualAuditRequest());
        Assert.True(auditResponse.IsSuccessStatusCode, await auditResponse.Content.ReadAsStringAsync());
        var audit = await auditResponse.Content.ReadFromJsonAsync<ShotVisualAuditSummary>();
        var finding = Assert.Single(audit!.Findings);

        var applied = await auditedClient.PostAsJsonAsync(
            $"/api/shots/{created.Id}/visual-audits/{audit.Id}/reconcile",
            new ReconcileVisualAuditRequest([new(finding.Id, VisualReconciliationAction.AdoptImage)], ApplyContract: true));
        applied.EnsureSuccessStatusCode();
        var plan = await applied.Content.ReadFromJsonAsync<VisualReconciliationPlan>();
        Assert.True(plan!.ContractApplied);
        Assert.Equal("Mara kneels while two wardens stand behind her.", plan.UpdatedShot!.Description);
        Assert.Equal("Mara studies a palm-size lantern.", plan.UpdatedShot.Action);
        Assert.Contains("Exactly two visibly distinct full-body wardens stand behind Mara.", plan.UpdatedShot.Constraints);
        Assert.Contains(plan.UpdatedShot.Constraints, rule =>
            rule.StartsWith("APPROVED VISUAL OVERRIDE [subject-count]: No wardens are visible.", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, (await auditedClient.GetAsync($"/api/shots/{created.Id}/visual-audit")).StatusCode);

        var sketch = await (await auditedClient.PutAsJsonAsync($"/api/shots/{created.Id}/sketch",
            new SaveSketchRequest(0, "Iterate from the full scene.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await auditedClient.PostAsJsonAsync($"/api/shots/{created.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(plan.UpdatedShot.Version, sketch!.Revision, GenerationRoute.FastDraft)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Contains(plan.UpdatedShot.Description, manifest!.CreativeBrief, StringComparison.Ordinal);
        Assert.Contains("APPROVED SHOT-SPECIFIC VISUAL OVERRIDES", manifest.CreativeBrief, StringComparison.Ordinal);
        Assert.Contains("No wardens are visible", manifest.CreativeBrief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedReconciliationAppliesAcceptedOverridesAndReturnsOneCombinedRepair()
    {
        await using var isolated = new StudioApiFactory();
        await using var audited = isolated.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICodexRuntime>();
            services.AddSingleton<ICodexRuntime, MixedVisualAuditTestCodexRuntime>();
        }));
        using var auditedClient = audited.CreateClient();
        var image = await UploadImageAsync(auditedClient, TinyPng.Concat(new byte[] { 0x05 }).ToArray(), "visual-mixed.png");
        var created = await (await auditedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "QA-MIXED", "Mixed visual choices", "Mara kneels with two wardens beside a small lantern.", 120,
            "35mm centered wide", "Mara studies the lantern.", [],
            ["Two wardens stand behind Mara.", "The lantern is palm-sized."], image.Id))).Content.ReadFromJsonAsync<ShotSummary>();
        var audit = await (await auditedClient.PostAsJsonAsync($"/api/shots/{created!.Id}/visual-audit", new RunVisualAuditRequest()))
            .Content.ReadFromJsonAsync<ShotVisualAuditSummary>();
        Assert.Equal(2, audit!.Findings.Count);

        var absentWardens = audit.Findings.Single(finding => finding.Category == "subject-count");
        var oversizedLantern = audit.Findings.Single(finding => finding.Category == "prop");
        var response = await auditedClient.PostAsJsonAsync(
            $"/api/shots/{created.Id}/visual-audits/{audit.Id}/reconcile",
            new ReconcileVisualAuditRequest([
                new(absentWardens.Id, VisualReconciliationAction.AdoptImage, "The empty background is intentional."),
                new(oversizedLantern.Id, VisualReconciliationAction.FixImage, "Keep it within one hand.")
            ], ApplyContract: true));
        response.EnsureSuccessStatusCode();
        var plan = await response.Content.ReadFromJsonAsync<VisualReconciliationPlan>();

        Assert.True(plan!.ContractApplied);
        Assert.True(plan.RequiresImageGeneration);
        Assert.Equal(VisualCorrectionRoute.RebuildFromSketch, plan.ImageRoute);
        Assert.Equal(created.Description, plan.UpdatedShot!.Description);
        Assert.Contains(plan.UpdatedShot.Constraints, rule => rule.Contains("No wardens are visible", StringComparison.Ordinal));
        Assert.Contains("Make the lantern palm-sized", plan.GenerationDirection, StringComparison.Ordinal);
        Assert.DoesNotContain("Restore both wardens", plan.GenerationDirection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthAndSeededSnapshotAreAvailable()
    {
        var health = await client.GetAsync("/health");
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotNull(snapshot);
        Assert.False(snapshot.DemoMode);
        Assert.True(snapshot.Shots.Count >= 6);
        Assert.Equal("SH-010", snapshot.Shots[0].Code);
        Assert.True(snapshot.References.Count >= 9);
        Assert.Contains(snapshot.References, item => item.Id == "ennix");
        var pairing = await client.GetFromJsonAsync<PairingStatusSummary>("/api/pairing/status");
        Assert.False(pairing!.LanEnabled);
        Assert.Null(pairing.PairingCode);
    }

    [Fact]
    public async Task HealthAndRuntimeExposeHonestUnstampedBuildIdentity()
    {
        using var health = await client.GetAsync("/health/live");
        using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStreamAsync());
        var runtime = await client.GetFromJsonAsync<RuntimeReadinessSummary>("/api/runtime/status");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var build = healthJson.RootElement.GetProperty("build");
        Assert.Equal("development", build.GetProperty("version").GetString());
        Assert.Equal("unknown", build.GetProperty("commit").GetString());
        Assert.Equal("unknown", build.GetProperty("builtAtUtc").GetString());
        Assert.Equal("development", build.GetProperty("channel").GetString());
        Assert.NotNull(runtime);
        Assert.Equal("development", runtime.Build.Version);
        Assert.Equal("unknown", runtime.Build.Commit);
    }

    [Fact]
    public void BuildIdentityReadsAnImmutableStampedArtifact()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"Framewright.Stamped.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)]);
        Assert.NotNull(constructor);
        foreach (var pair in new Dictionary<string, string>
        {
            ["FramewrightVersion"] = "0.1.0-rc.1",
            ["FramewrightCommit"] = "0123456789abcdef",
            ["FramewrightBuiltAtUtc"] = "2026-08-31T09:00:00.0000000-06:00",
            ["FramewrightChannel"] = "release-candidate"
        })
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [pair.Key, pair.Value]));
        }

        var build = new BuildIdentityService(assembly).Current;

        Assert.Equal("0.1.0-rc.1", build.Version);
        Assert.Equal("0123456789abcdef", build.Commit);
        Assert.Equal("2026-08-31T15:00:00.0000000+00:00", build.BuiltAtUtc);
        Assert.Equal("release-candidate", build.Channel);
    }

    [Fact]
    public async Task DiagnosticBundleIsChecksummedAndExcludesCreativeContentAndSecrets()
    {
        using var response = await client.GetAsync("/api/maintenance/diagnostics");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "diagnostics-manifest.json");
        using var manifest = JsonDocument.Parse(manifestEntry.Open());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var item in manifest.RootElement.GetProperty("entries").EnumerateArray())
        {
            var path = item.GetProperty("path").GetString();
            var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == path);
            using var content = new MemoryStream();
            await entry.Open().CopyToAsync(content);
            Assert.Equal(item.GetProperty("bytes").GetInt64(), content.Length);
            Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(content.ToArray())).ToLowerInvariant());
        }

        var text = string.Join('\n', archive.Entries.Where(entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal)).Select(entry =>
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }));
        Assert.DoesNotContain("OPENAI_API_KEY", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creativeBrief", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestJson", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The Aerie Sequence", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.DataRoot, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(archive.Entries, entry => entry.FullName == "workflows.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "voice-lock.json");
    }

    [Fact]
    public async Task PairedTabletCannotDownloadWorkstationDiagnostics()
    {
        var remoteAddress = IPAddress.Parse("172.31.240.77");
        using var remoteBase = new StudioApiFactory();
        using var remoteFactory = remoteBase.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:AllowLan"] = "true"
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(remoteAddress)));
        });
        using var remoteClient = remoteFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var pairing = remoteFactory.Services.GetRequiredService<PairingService>();
        var code = pairing.Status(loopback: true, paired: false).PairingCode;
        Assert.False(string.IsNullOrWhiteSpace(code));
        using var claim = await remoteClient.PostAsJsonAsync("/api/pairing/claim", new PairingClaimRequest(code!));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using var response = await remoteClient.GetAsync("/api/maintenance/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DraftWorkflowLabelTracksTextAndSketchRoutesFromTheConfiguredLibrary()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = snapshot!.Shots[0];

        var text = await client.GetFromJsonAsync<DraftWorkflowSummary>($"/api/shots/{shot.Id}/draft-workflow?useSketch=false");
        var sketch = await client.GetFromJsonAsync<DraftWorkflowSummary>($"/api/shots/{shot.Id}/draft-workflow?useSketch=true");

        Assert.Equal("ComfyUI Text Draft", text!.Name);
        Assert.Equal("Text to image", text.Mode);
        Assert.False(text.UsesComposition);
        Assert.Equal("ComfyUI Image Edit", sketch!.Name);
        Assert.Equal("Sketch guided", sketch.Mode);
        Assert.True(sketch.UsesComposition);
    }

    [Fact]
    public async Task ProjectContractAndShotOrderAreDurableAndValidated()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.NotNull(snapshot);
        var projectResponse = await client.PutAsJsonAsync("/api/project", new UpdateProjectRequest(
            snapshot.Project.UpdatedAt, "Aerie editorial", "Skychasers local", "SQ-11", "Court revision", 25, "2.39:1",
            2304, 960,
            "Painterly cel-shaded animation with graphic silhouettes.",
            snapshot.Project.WorldCanon,
            snapshot.Project.PromptDirectives,
            "No photoreal live-action people or glossy game rendering."));
        Assert.Equal(HttpStatusCode.OK, projectResponse.StatusCode);
        var saved = await projectResponse.Content.ReadFromJsonAsync<ProjectSummary>();
        Assert.Equal(25, saved!.FramesPerSecond);
        Assert.Contains("cel-shaded", saved.VisualStyle);

        var original = snapshot.Shots.Select(x => x.Id).ToArray();
        var reversed = original.Reverse().ToArray();
        var reorder = await client.PutAsJsonAsync("/api/shots/order", new ReorderShotsRequest(reversed));
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        var refreshed = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.Equal(reversed, refreshed!.Shots.Select(x => x.Id));
        var invalid = await client.PutAsJsonAsync("/api/shots/order", new ReorderShotsRequest(reversed.Skip(1).ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/shots/order", new ReorderShotsRequest(original))).StatusCode);
    }

    [Fact]
    public async Task CredentialAndAudioCapabilityEndpointsExposeStatusNotSecrets()
    {
        var credentialResponse = await client.GetAsync("/api/credentials/openai");
        var credentialJson = await credentialResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, credentialResponse.StatusCode);
        Assert.DoesNotContain("apiKey", credentialJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENAI_API_KEY", credentialJson, StringComparison.Ordinal);
        var synthesis = await client.GetFromJsonAsync<VoiceSynthesisStatus>("/api/voice/synthesis");
        Assert.False(synthesis!.CanSynthesize);
        var mastering = await client.GetFromJsonAsync<AudioMasteringStatus>("/api/export/audio/status");
        Assert.NotNull(mastering);
        Assert.Equal(mastering.ToolAvailable && mastering.ClipsWithMedia > 0, mastering.CanMix);
        Assert.False(string.IsNullOrWhiteSpace(mastering.Detail));
    }

    [Fact]
    public async Task ConfiguredContainerGatewayIsTreatedAsTheTrustedWorkstationButOtherLANClientsAreNot()
    {
        var gatewayAddress = IPAddress.Parse("172.31.240.1");
        using var trustedBase = new StudioApiFactory();
        using var trustedFactory = trustedBase.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:AllowLan"] = "true",
                ["Studio:TrustedProxyAddresses:0"] = gatewayAddress.ToString()
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(gatewayAddress)));
        });
        using var trustedClient = trustedFactory.CreateClient();

        var trustedPairing = await trustedClient.GetFromJsonAsync<PairingStatusSummary>("/api/pairing/status");
        Assert.True(trustedPairing!.LanEnabled);
        Assert.False(string.IsNullOrWhiteSpace(trustedPairing.PairingCode));
        Assert.Equal(HttpStatusCode.OK, (await trustedClient.PostAsync("/api/pairing/start", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trustedClient.GetAsync("/api/credentials/openai")).StatusCode);

        var arbitraryLanAddress = IPAddress.Parse("172.31.240.77");
        using var untrustedBase = new StudioApiFactory();
        using var untrustedFactory = untrustedBase.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:AllowLan"] = "true",
                ["Studio:TrustedProxyAddresses:0"] = gatewayAddress.ToString()
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(arbitraryLanAddress)));
        });
        using var untrustedClient = untrustedFactory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await untrustedClient.PostAsync("/api/pairing/start", content: null)).StatusCode);
    }

    [Fact]
    public async Task MusicGenerationRejectsAnUnapprovedRemoteYuE2Endpoint()
    {
        using var remoteFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["YuE2:Endpoint"] = "https://render.example.test",
                ["YuE2:AllowRemote"] = "false",
                ["YuE2:Enabled"] = "true"
            })));
        using var remoteClient = remoteFactory.CreateClient();

        var status = await remoteClient.GetFromJsonAsync<MusicGenerationStatus>("/api/music/status");

        Assert.NotNull(status);
        Assert.False(status.CanRender);
        Assert.False(status.EndpointAllowed);
        Assert.Contains("AllowRemote", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaUploadLargerThanFrameworkDefaultReachesStreamingAssetValidation()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var bytes = new byte[30_100_000];
        "RIFF"u8.CopyTo(bytes);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Audio");
        request.Headers.Add("X-Storyboard-Studio", "1");
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("audio/wav");
        multipart.Add(file, "file", "large-contract.wav");
        request.Content = multipart;

        using var response = await isolatedClient.SendAsync(request);
        var imported = await response.Content.ReadFromJsonAsync<AssetSummary>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(imported);
        Assert.Equal(bytes.LongLength, imported.Bytes);
        Assert.Equal(AssetKind.Audio, imported.Kind);
    }

    [Fact]
    public async Task AssetLibraryOrganizesMetadataCollectionsArchiveAndShotPlacements()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "door-study.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var importedResponse = await isolatedClient.SendAsync(upload);
        var imported = await importedResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, importedResponse.StatusCode);
        Assert.Equal("door-study", imported!.DisplayName);
        Assert.Equal("Imported", imported.Source);
        Assert.False(imported.IsArchived);

        var collectionResponse = await isolatedClient.PostAsJsonAsync("/api/asset-collections", new CreateAssetCollectionRequest("Architecture", "#73b7cf"));
        var collection = await collectionResponse.Content.ReadFromJsonAsync<AssetCollectionSummary>();
        Assert.Equal(HttpStatusCode.OK, collectionResponse.StatusCode);

        var updateResponse = await isolatedClient.PutAsJsonAsync($"/api/assets/{imported.Id}", new UpdateAssetRequest("Aerie door study", collection!.Id, ["architecture", "door"], "Use for threshold proportions."));
        var updated = await updateResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal(collection.Id, updated!.CollectionId);
        Assert.Contains("door", Assert.IsAssignableFrom<IReadOnlyList<string>>(updated.Tags));

        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = snapshot!.Shots[0];
        var placementResponse = await isolatedClient.PostAsJsonAsync($"/api/assets/{updated.Id}/placements", new CreateAssetPlacementRequest(shot.Id, "Image guide"));
        var placement = await placementResponse.Content.ReadFromJsonAsync<AssetPlacementSummary>();
        Assert.Equal(HttpStatusCode.OK, placementResponse.StatusCode);
        Assert.Equal(shot.Code, placement!.ShotCode);
        var placements = await isolatedClient.GetFromJsonAsync<List<AssetPlacementSummary>>($"/api/asset-placements?shotId={shot.Id}");
        Assert.Contains(placements!, item => item.AssetId == updated.Id && item.Role == "Image guide");

        Assert.Equal(HttpStatusCode.OK, (await isolatedClient.PostAsync($"/api/assets/{updated.Id}/archive", null)).StatusCode);
        Assert.DoesNotContain((await isolatedClient.GetFromJsonAsync<List<AssetSummary>>("/api/assets"))!, item => item.Id == updated.Id);
        Assert.Contains((await isolatedClient.GetFromJsonAsync<List<AssetSummary>>("/api/assets?includeArchived=true"))!, item => item.Id == updated.Id && item.IsArchived);
        Assert.Equal(HttpStatusCode.OK, (await isolatedClient.PostAsync($"/api/assets/{updated.Id}/restore", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await isolatedClient.DeleteAsync($"/api/asset-collections/{collection.Id}")).StatusCode);
        var unfiled = Assert.Single((await isolatedClient.GetFromJsonAsync<List<AssetSummary>>("/api/assets"))!, item => item.Id == updated.Id);
        Assert.Null(unfiled.CollectionId);
    }

    [Fact]
    public async Task AssetImageGenerationReusesTheAdapterWithoutMutatingAShot()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "shape-guide.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var composition = await (await isolatedClient.SendAsync(upload)).Content.ReadFromJsonAsync<AssetSummary>();
        var before = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shotBefore = before!.Shots[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/generate-image")
        {
            Content = JsonContent.Create(new GenerateAssetImageRequest(
                "Aerie threshold concept", "A severe limestone threshold with readable proportions.",
                GenerationRoute.FastDraft, LocalProofGenerationAdapter.AdapterId,
                composition!.Id, [composition.Id]))
        };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await isolatedClient.SendAsync(request);
        var job = await response.Content.ReadFromJsonAsync<JobSummary>();
        StudioSnapshot? after = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            after = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            job = after!.Jobs.Single(item => item.Id == job!.Id);
            if (job.State is JobState.Completed or JobState.Failed) break;
        }
        var generated = Assert.Single((await isolatedClient.GetFromJsonAsync<List<AssetSummary>>("/api/assets"))!,
            item => item.Id == job!.OutputAssetId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobState.Completed, job!.State);
        Assert.Equal("Asset", job.WorkType);
        Assert.Equal("Aerie threshold concept", generated.DisplayName);
        Assert.Contains("generated", generated!.Tags!);
        Assert.Contains("Local composition proof", generated.Tags!);
        Assert.NotNull(generated.RevisionFamilyId);
        Assert.Equal(1, generated.RevisionNumber);
        Assert.True(generated.IsCurrentRevision);
        var shotAfter = Assert.Single(after!.Shots, shot => shot.Id == shotBefore.Id);
        Assert.Equal(shotBefore.Version, shotAfter.Version);
        Assert.Equal(shotBefore.CurrentAssetId, shotAfter.CurrentAssetId);
    }

    [Fact]
    public async Task QueuedAssetImageExecutesTheEnqueueTimeWorldAndReferencePacketAfterMetadataChanges()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "storyboard-studio-frozen-asset-tests", Guid.NewGuid().ToString("N"));
        using var isolatedFactory = new StudioApiFactory(dataRoot, deleteDataRoot: true, startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var compositionBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var referenceBytes = compositionBytes.Concat(new byte[] { 0x01 }).ToArray();

        async Task<AssetSummary> Upload(byte[] bytes, string fileName)
        {
            using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images")
            {
                Content = CreateImageUpload(bytes, fileName)
            };
            upload.Headers.Add("X-Storyboard-Studio", "1");
            var response = await isolatedClient.SendAsync(upload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
        }

        var composition = await Upload(compositionBytes, "enqueue-composition.png");
        var reference = await Upload(referenceBytes, "enqueue-identity.png");
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.NotNull(snapshot);

        JobSummary queued;
        await using (var enqueueScope = isolatedFactory.Services.CreateAsyncScope())
        {
            enqueueScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(snapshot.Project.Id);
            var result = await enqueueScope.ServiceProvider.GetRequiredService<AssetImageGenerationService>().EnqueueAsync(
                new GenerateAssetImageRequest(
                    "Frozen reference proof",
                    "Preserve the exact selected identity while applying the original world style.",
                    GenerationRoute.FastDraft,
                    "serial-probe",
                    composition.Id,
                    ReferenceBindings: [new(reference.Id, "Identity")]),
                CancellationToken.None);
            Assert.Equal(RepositoryResultKind.Ok, result.Kind);
            queued = result.Value!;
        }

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            mutationScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(snapshot.Project.Id);
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var storedJob = await db.Jobs.AsNoTracking().SingleAsync(candidate => candidate.Id == queued.Id);
            using (var packet = JsonDocument.Parse(storedJob.RequestJson!))
            {
                Assert.Equal(1, packet.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Contains(snapshot.Project.VisualStyle, packet.RootElement.GetProperty("worldPrompt").GetString(), StringComparison.Ordinal);
                Assert.Equal(composition.ContentHash, packet.RootElement.GetProperty("composition").GetProperty("contentHash").GetString());
                var frozenReferences = packet.RootElement.GetProperty("references");
                Assert.Equal(1, frozenReferences.GetArrayLength());
                Assert.Equal(reference.ContentHash, frozenReferences[0].GetProperty("asset").GetProperty("contentHash").GetString());
                Assert.Equal("Identity", frozenReferences[0].GetProperty("role").GetString());
            }
            var project = await db.Projects.SingleAsync(candidate => candidate.Id == snapshot.Project.Id);
            project.VisualStyle = "MUTATED STYLE MUST NOT REACH PROVIDER";
            project.WorldCanon = "MUTATED WORLD MUST NOT REACH PROVIDER";
            project.PromptDirectives = "MUTATED PROMPT DIRECTIVE";
            project.NegativeDirectives = "MUTATED NEGATIVE DIRECTIVE";
            var mutableComposition = await db.Assets.SingleAsync(candidate => candidate.Id == composition.Id);
            mutableComposition.DisplayName = "Mutated composition name";
            mutableComposition.OriginalFileName = "mutated-composition.jpg";
            mutableComposition.MimeType = "image/jpeg";
            var mutableReference = await db.Assets.SingleAsync(candidate => candidate.Id == reference.Id);
            mutableReference.DisplayName = "Mutated identity name";
            mutableReference.OriginalFileName = "mutated-identity.jpg";
            mutableReference.MimeType = "image/jpeg";
            await db.SaveChangesAsync();
        }

        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<AssetImageGenerationService>()
                .RunAsync(queued.Id, CancellationToken.None);
        }

        var adapter = isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>();
        var context = Assert.IsType<GenerationExecutionContext>(adapter.LastContext);
        Assert.Equal(1, adapter.ExecutionCount);
        Assert.Contains(snapshot.Project.VisualStyle, context.Prompt, StringComparison.Ordinal);
        Assert.Contains(snapshot.Project.WorldCanon, context.Prompt, StringComparison.Ordinal);
        Assert.Contains(reference.DisplayName, context.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MUTATED", context.Prompt, StringComparison.Ordinal);
        Assert.Equal(composition.ContentHash, context.Composition!.ContentHash);
        Assert.Equal(composition.OriginalFileName, context.Composition.OriginalFileName);
        Assert.Equal(composition.MimeType, context.Composition.MimeType);
        var frozenReference = Assert.Single(context.ReferenceImages);
        Assert.Equal(reference.ContentHash, frozenReference.Asset.ContentHash);
        Assert.Equal(reference.DisplayName, frozenReference.Binding.Name);
        Assert.Equal(reference.OriginalFileName, frozenReference.Asset.OriginalFileName);
        Assert.Equal(reference.MimeType, frozenReference.Asset.MimeType);
        Assert.Equal("Identity", frozenReference.Binding.Category);
        Assert.Equal(64, context.ManifestHash.Length);
        var completed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(job => job.Id == queued.Id);
        Assert.Equal(JobState.Completed, completed.State);
    }

    [Fact]
    public async Task QueuedAssetImageFailsBeforeProviderWhenFrozenContentHashChanges()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "storyboard-studio-frozen-hash-tests", Guid.NewGuid().ToString("N"));
        using var isolatedFactory = new StudioApiFactory(dataRoot, deleteDataRoot: true, startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images")
        {
            Content = CreateImageUpload(png, "hash-bound-composition.png")
        };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var composition = await (await isolatedClient.SendAsync(upload)).Content.ReadFromJsonAsync<AssetSummary>();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");

        JobSummary queued;
        await using (var enqueueScope = isolatedFactory.Services.CreateAsyncScope())
        {
            enqueueScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(snapshot!.Project.Id);
            var result = await enqueueScope.ServiceProvider.GetRequiredService<AssetImageGenerationService>().EnqueueAsync(
                new GenerateAssetImageRequest(
                    "Frozen hash proof",
                    "Never substitute different pixels after this request is queued.",
                    GenerationRoute.FastDraft,
                    "serial-probe",
                    composition!.Id),
                CancellationToken.None);
            queued = result.Value!;
        }

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            mutationScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(snapshot!.Project.Id);
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var mutableComposition = await db.Assets.SingleAsync(candidate => candidate.Id == composition!.Id);
            mutableComposition.ContentHash = new string('f', 64);
            await db.SaveChangesAsync();
        }

        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<AssetImageGenerationService>()
                .RunAsync(queued.Id, CancellationToken.None);
        }

        var adapter = isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>();
        Assert.Equal(0, adapter.ExecutionCount);
        Assert.Null(adapter.LastContext);
        var failed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(job => job.Id == queued.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("enqueue-time content hash", failed.Error, StringComparison.OrdinalIgnoreCase);

        // Restoring the exact bytes makes an explicit retry safe, but later
        // metadata/world edits still must not rebuild its original packet.
        await using (var restoreScope = isolatedFactory.Services.CreateAsyncScope())
        {
            restoreScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(snapshot!.Project.Id);
            var db = restoreScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var project = await db.Projects.SingleAsync(candidate => candidate.Id == snapshot.Project.Id);
            project.VisualStyle = "MUTATED RETRY STYLE MUST NOT REACH PROVIDER";
            var mutableComposition = await db.Assets.SingleAsync(candidate => candidate.Id == composition!.Id);
            mutableComposition.ContentHash = composition.ContentHash;
            mutableComposition.DisplayName = "Mutated retry composition";
            await db.SaveChangesAsync();
        }

        var retryResponse = await isolatedClient.PostAsync($"/api/jobs/{queued.Id}/retry", null);
        var retry = await retryResponse.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        Assert.Equal(2, retry!.Attempt);
        await using (var retryScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await retryScope.ServiceProvider.GetRequiredService<AssetImageGenerationService>()
                .RunAsync(retry.Id, CancellationToken.None);
        }

        Assert.Equal(1, adapter.ExecutionCount);
        var retryContext = Assert.IsType<GenerationExecutionContext>(adapter.LastContext);
        Assert.Contains(snapshot.Project.VisualStyle, retryContext.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("MUTATED RETRY", retryContext.Prompt, StringComparison.Ordinal);
        Assert.Equal(composition.DisplayName, retryContext.Composition!.DisplayName);
        var completedRetry = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(job => job.Id == retry.Id);
        Assert.Equal(JobState.Completed, completedRetry.State);
    }

    [Fact]
    public async Task AssetImageJobsAreDurableSerialAndSurviveNavigation()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        async Task<JobSummary> Queue(string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/generate-image")
            {
                Content = JsonContent.Create(new GenerateAssetImageRequest(
                    name, $"Create a reusable reference for {name}.", GenerationRoute.FastDraft, "serial-probe"))
            };
            request.Headers.Add("X-Storyboard-Studio", "1");
            var response = await isolatedClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JobSummary>())!;
        }

        var first = await Queue("Queued reference one");
        var second = await Queue("Queued reference two");
        // Navigation is just another request; neither generation belongs to the
        // page that submitted it or to that request's cancellation token.
        Assert.Equal(HttpStatusCode.OK, (await isolatedClient.GetAsync("/api/studio")).StatusCode);

        StudioSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(100);
            snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            var relevant = snapshot!.Jobs.Where(job => job.Id == first.Id || job.Id == second.Id).ToArray();
            if (relevant.Length == 2 && relevant.All(job => job.State is JobState.Completed or JobState.Failed)) break;
        }

        var completed = snapshot!.Jobs.Where(job => job.Id == first.Id || job.Id == second.Id).ToArray();
        Assert.Equal(2, completed.Length);
        Assert.All(completed, job => Assert.Equal(JobState.Completed, job.State));
        Assert.All(completed, job => Assert.Equal("Asset", job.WorkType));
        Assert.Equal(2, completed.Select(job => job.OutputAssetId).Distinct().Count());
        Assert.Equal(1, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().MaximumConcurrency);
    }

    [Fact]
    public async Task QueuedAuthorityImagePromotesServerSideAfterTheBrowserLeaves()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var created = await (await isolatedClient.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Queue witness", "Character", "A patient queue witness.", "Keep the silver eyes.", "#758ea3")))
            .Content.ReadFromJsonAsync<ReferenceSummary>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/generate-image")
        {
            Content = JsonContent.Create(new GenerateAssetImageRequest(
                "Queue witness revision", "Refine the character while preserving identity.", GenerationRoute.FastDraft,
                "serial-probe", AuthorityTarget: new AuthorityGenerationTarget(
                    created!.Id, created.Version, "A refined patient queue witness.", "Keep the silver eyes.")))
        };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await isolatedClient.SendAsync(request);
        var job = await response.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Leave the authority editor entirely. Promotion is owned by the server.
        _ = await isolatedClient.GetFromJsonAsync<List<AssetSummary>>("/api/assets");
        ReferenceSummary? authority = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(100);
            authority = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.References.Single(x => x.Id == created.Id);
            if (authority.Version > created.Version) break;
        }
        Assert.Equal(created.Version + 1, authority!.Version);
        Assert.NotNull(authority.ImageAssetId);
        Assert.Equal(job!.Id, (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(x => x.Id == job.Id).Id);
    }

    [Fact]
    public async Task ImageAssetsKeepOrderedRevisionHistoryAndCanRestoreAnOlderWorkingImage()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var originalBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var revisionBytes = originalBytes.Concat(new byte[] { 0x00 }).ToArray();

        using var originalUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(originalBytes, "ennix-study.png") };
        originalUpload.Headers.Add("X-Storyboard-Studio", "1");
        var original = await (await isolatedClient.SendAsync(originalUpload)).Content.ReadFromJsonAsync<AssetSummary>();
        using var revisionUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(revisionBytes, "ennix-study-revised.png") };
        revisionUpload.Headers.Add("X-Storyboard-Studio", "1");
        var revision = await (await isolatedClient.SendAsync(revisionUpload)).Content.ReadFromJsonAsync<AssetSummary>();

        var addResponse = await isolatedClient.PostAsJsonAsync($"/api/assets/{original!.Id}/revisions", new AddAssetRevisionRequest(revision!.Id, "Move the eyeline left.", "Imported test"));
        var added = await addResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        Assert.Equal(2, added!.RevisionNumber);
        Assert.True(added.IsCurrentRevision);
        Assert.Equal("Move the eyeline left.", added.RevisionPrompt);

        var history = await isolatedClient.GetFromJsonAsync<List<AssetSummary>>($"/api/assets/{added.Id}/revisions");
        Assert.Collection(history!,
            current => { Assert.Equal(added.Id, current.Id); Assert.True(current.IsCurrentRevision); Assert.Equal(2, current.RevisionNumber); },
            first => { Assert.Equal(original.Id, first.Id); Assert.False(first.IsCurrentRevision); Assert.Equal(1, first.RevisionNumber); });

        var chooseResponse = await isolatedClient.PostAsync($"/api/assets/{original.Id}/make-current", null);
        var chosen = await chooseResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, chooseResponse.StatusCode);
        Assert.True(chosen!.IsCurrentRevision);
        var restoredHistory = await isolatedClient.GetFromJsonAsync<List<AssetSummary>>($"/api/assets/{original.Id}/revisions");
        Assert.True(Assert.Single(restoredHistory!, item => item.Id == original.Id).IsCurrentRevision);
        Assert.False(Assert.Single(restoredHistory!, item => item.Id == revision.Id).IsCurrentRevision);
    }

    [Fact]
    public async Task MaintenanceBackupIsIntegrityCheckedPortableAndSecretFree()
    {
        // Keep the archive snapshot independent of unfinished work from the
        // long-lived class fixture's generation queue.
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var status = await isolatedClient.GetFromJsonAsync<BackupStatus>("/api/maintenance/status");
        Assert.NotNull(status);
        Assert.Equal("ok", status.DatabaseIntegrity, ignoreCase: true);
        Assert.Contains("provider keys are excluded", status.Policy, StringComparison.OrdinalIgnoreCase);

        var response = await isolatedClient.GetAsync("/api/maintenance/backup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("database/storyboard-studio.db"));
        var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "backup-manifest.json");
        await using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        var excludes = manifest.RootElement.GetProperty("excludes").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("API keys", excludes);
        Assert.Contains("external ComfyUI workflows and models", excludes);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));

        await using var scope = isolatedFactory.Services.CreateAsyncScope();
        var retained = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateScheduledAsync(CancellationToken.None);
        Assert.True(File.Exists(retained.Path));
        using (var retainedArchive = ZipFile.OpenRead(retained.Path))
        {
            Assert.NotNull(retainedArchive.GetEntry("database/storyboard-studio.db"));
            Assert.NotNull(retainedArchive.GetEntry("backup-manifest.json"));
        }
        var after = await isolatedClient.GetFromJsonAsync<BackupStatus>("/api/maintenance/status");
        Assert.True(after!.RetainedBackups >= 1);
        Assert.NotNull(after.LatestBackupAt);
    }

    [Fact]
    public async Task ImageReviewNotesAreBoundToTheExactAssetAndSupportMoveAndResolve()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "mara-review.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var asset = await (await isolatedClient.SendAsync(upload)).Content.ReadFromJsonAsync<AssetSummary>();

        var createdResponse = await isolatedClient.PostAsJsonAsync($"/api/assets/{asset!.Id}/review-notes",
            new CreateAssetReviewNoteRequest(1.3, -.2, "The eyebrow notch moved to the wrong side."));
        var created = await createdResponse.Content.ReadFromJsonAsync<AssetReviewNoteSummary>();
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        Assert.NotNull(created);
        Assert.Equal(asset.Id, created.AssetId);
        Assert.Equal(1, created.X);
        Assert.Equal(0, created.Y);
        Assert.Equal("Open", created.State);

        var listed = await isolatedClient.GetFromJsonAsync<AssetReviewNoteSummary[]>($"/api/assets/{asset.Id}/review-notes");
        Assert.Equal(created.Id, Assert.Single(listed!).Id);

        var movedResponse = await isolatedClient.PutAsJsonAsync($"/api/asset-review-notes/{created.Id}/position", new MoveCommentRequest(.42, .36));
        var moved = await movedResponse.Content.ReadFromJsonAsync<AssetReviewNoteSummary>();
        Assert.Equal(.42, moved!.X);
        Assert.Equal(.36, moved.Y);

        var resolvedResponse = await isolatedClient.PostAsync($"/api/asset-review-notes/{created.Id}/resolve", null);
        var resolved = await resolvedResponse.Content.ReadFromJsonAsync<AssetReviewNoteSummary>();
        Assert.Equal(HttpStatusCode.OK, resolvedResponse.StatusCode);
        Assert.Equal("Resolved", resolved!.State);

        var missing = await isolatedClient.PostAsJsonAsync($"/api/assets/{Guid.NewGuid()}/review-notes",
            new CreateAssetReviewNoteRequest(.5, .5, "No image owns this note."));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CommentIsBoundToTheCurrentVersionAndCoordinatesAreClamped()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");

        var response = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/comments", new CreateCommentRequest(1.4, -0.2, "Keep the eyeline below the arch."));
        var comment = await response.Content.ReadFromJsonAsync<CommentSummary>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(comment);
        Assert.Equal(shot.Version, comment.Version);
        Assert.Equal(1, comment.X);
        Assert.Equal(0, comment.Y);

        var movedResponse = await client.PutAsJsonAsync($"/api/comments/{comment.Id}/position", new MoveCommentRequest(.35, .72));
        var moved = await movedResponse.Content.ReadFromJsonAsync<CommentSummary>();
        Assert.Equal(HttpStatusCode.OK, movedResponse.StatusCode);
        Assert.NotNull(moved);
        Assert.Equal(.35, moved.X);
        Assert.Equal(.72, moved.Y);
    }

    [Fact]
    public async Task ReferencePinFreezesAuthorityVersionAndRemovalResolvesTheSpatialInstruction()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var createdReference = await isolatedClient.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Pinned role test", "Role", "A uniformed test guard role.", "Every guard keeps the approved uniform and equipment; faces remain distinct.", "#7b765f"));
        var reference = await createdReference.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.NotNull(reference);
        var attach = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}", new UpdateShotRequest(
            shot.UpdatedAt, shot.Title, shot.Description, shot.DurationFrames, shot.Camera, shot.Action,
            [.. shot.ReferenceIds, reference.Id], shot.Constraints));
        shot = (await attach.Content.ReadFromJsonAsync<ShotSummary>())!;

        var pinResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/comments",
            new CreateCommentRequest(.31, .42, "Put a full-body guard here, standing at attention.", reference.Id));
        var pin = await pinResponse.Content.ReadFromJsonAsync<CommentSummary>();
        Assert.Equal(HttpStatusCode.OK, pinResponse.StatusCode);
        Assert.NotNull(pin);
        Assert.Equal(reference.Id, pin.ReferenceId);
        Assert.Equal(reference.Version, pin.ReferenceVersion);
        var secondPinResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/comments",
            new CreateCommentRequest(.70, .55, "Put another guard here, slouched over.", reference.Id));
        Assert.Equal(HttpStatusCode.OK, secondPinResponse.StatusCode);

        var savedResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(0, "Preserve the composition.", ExampleSketch()));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifestResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft));
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        Assert.NotNull(manifest);
        Assert.Contains("REFERENCE PLACEMENTS", manifest.CreativeBrief);
        Assert.Contains($"{reference.Name} v{reference.Version}", manifest.CreativeBrief);
        Assert.Contains("31% across / 42% down", manifest.CreativeBrief);
        Assert.Contains("REQUIRED CAST COUNT: Render exactly 2 distinct", manifest.CreativeBrief);
        Assert.Contains("REQUIRED FRAMING: Use a wide or full-body composition", manifest.CreativeBrief);
        var frozenAuthority = Assert.Single(manifest.Authorities, authority => authority.Id == reference.Id);
        Assert.True(frozenAuthority.IsPinned);
        Assert.Equal(2, frozenAuthority.Placements!.Count);
        Assert.Equal("Put a full-body guard here, standing at attention.", frozenAuthority.Placements[0].Body);
        Assert.Contains("31% across / 42% down", frozenAuthority.Placements[0].Instruction);

        var updateResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}", new UpdateShotRequest(
            shot.UpdatedAt, shot.Title, shot.Description, shot.DurationFrames, shot.Camera, shot.Action,
            shot.ReferenceIds.Where(id => id != reference.Id).ToArray(), shot.Constraints));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var after = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var storedPin = Assert.Single(after!.Comments, comment => comment.Id == pin.Id);
        Assert.Equal("Resolved", storedPin.State);

        var nextVersion = await isolatedClient.PostAsJsonAsync($"/api/references/{reference.Id}/versions",
            new CreateReferenceVersionRequest(reference.Version, reference.Description, reference.LockedConstraint, reference.ImageAssetId));
        Assert.Equal(HttpStatusCode.OK, nextVersion.StatusCode);
        var deleteFrozenVersion = await isolatedClient.DeleteAsync($"/api/references/{reference.Id}/versions/{reference.Version}");
        Assert.Equal(HttpStatusCode.Conflict, deleteFrozenVersion.StatusCode);
        Assert.Contains("frozen manifest", await deleteFrozenVersion.Content.ReadAsStringAsync());
        var deleteFrozenAuthority = await isolatedClient.DeleteAsync($"/api/references/{reference.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deleteFrozenAuthority.StatusCode);
        Assert.Contains("frozen manifest", await deleteFrozenAuthority.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ShotCardCanPrepareATextDraftWithoutEverCreatingASketch()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var reference = snapshot!.References[0];
        var create = await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-999", "Arrival at dusk", "A courier arrives below the Aerie at blue hour.",
            96, "35mm · Moderate wide", "The courier stops and looks up.", [reference.Id], ["Red satchel remains visible"]));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(shot);
        Assert.Equal(HttpStatusCode.NoContent, (await isolatedClient.GetAsync($"/api/shots/{shot.Id}/sketch")).StatusCode);

        var prepared = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, 0, GenerationRoute.FastDraft));
        var manifest = await prepared.Content.ReadFromJsonAsync<GenerationManifestSummary>();

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        Assert.NotNull(manifest);
        Assert.Equal(Guid.Empty, manifest.SketchId);
        Assert.Equal(0, manifest.SketchRevision);
        Assert.Null(manifest.CompositionAssetId);
        Assert.Contains("courier arrives below the Aerie", manifest.CreativeBrief);
        Assert.Contains("The courier stops and looks up", manifest.CreativeBrief);
        Assert.Contains("35mm", manifest.CreativeBrief);
        Assert.Contains("Red satchel remains visible", manifest.Constraints);
        Assert.Contains(manifest.Authorities, authority => authority.Id == reference.Id);
    }

    [Fact]
    public async Task FeedbackManifestCanExplicitlyAvoidASavedSketchComposition()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = snapshot!.Shots.Single(item => item.Code == "SH-020");

        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images")
        {
            Content = CreateImageUpload(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="), "saved-sketch.png")
        };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var asset = await (await isolatedClient.SendAsync(upload)).Content.ReadFromJsonAsync<AssetSummary>();
        Assert.NotNull(asset);

        var savedResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(0, "Original sketch composition must not be reused for feedback.", ExampleSketch(), CompositionAssetId: asset.Id));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(sketch);

        const string feedbackBrief = "Apply both open notes.\n\nFEEDBACK-GUIDED DRAFT: Generate from shot context without using the saved sketch.";
        var preparedResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision, GenerationRoute.FastDraft,
                GenerationPurpose.Draft, CreativeBriefOverride: feedbackBrief, AllowSketchCompositionFallback: false));
        var manifest = await preparedResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();

        Assert.Equal(HttpStatusCode.OK, preparedResponse.StatusCode);
        Assert.NotNull(manifest);
        Assert.Null(manifest.CompositionAssetId);
        Assert.Contains(feedbackBrief, manifest.CreativeBrief);
        Assert.Contains("PROJECT WORLD SETTINGS", manifest.CreativeBrief);
        Assert.Contains("subject's anatomical left and right", manifest.CreativeBrief);
        Assert.Contains("character-face and wardrobe authorities as separate compatible layers", manifest.CreativeBrief);
    }

    [Fact]
    public async Task RatificationRejectsAStaleVersion()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = snapshot!.Shots[0];

        var response = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/ratify", new RatifyRequest(shot.Version + 10, "stale client"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ProductionFinalCanUseAWorkingDraftWithoutRatification()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-020");

        var existingResponse = await isolatedClient.GetAsync($"/api/shots/{shot.Id}/sketch");
        var existing = existingResponse.StatusCode == HttpStatusCode.OK
            ? await existingResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>()
            : null;
        var savedResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(existing?.Revision ?? 0, "Blocked final promotion.", ExampleSketch()));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var response = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.PrecisionDraft, GenerationPurpose.Final));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var manifest = await response.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(GenerationPurpose.Final, manifest!.Purpose);
    }

    [Fact]
    public async Task RatificationRequiresFeedbackDispositionAndArchivesAnImmutableManifest()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-060");
        var comment = Assert.Single(snapshot.Comments, item => item.ShotId == shot.Id && item.Version == shot.Version && item.State == "Open");

        var blocked = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/ratify", new RatifyRequest(shot.Version, "approval gate test"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var resolved = await client.PostAsync($"/api/comments/{comment.Id}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        var approved = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/ratify", new RatifyRequest(shot.Version, "all feedback resolved"));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var versions = await client.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{shot.Id}/versions");
        var authority = Assert.Single(versions!);
        Assert.Equal(shot.Version, authority.Version);
        Assert.Equal(ApprovalState.Ratified, authority.Approval);
        Assert.Equal(64, authority.ManifestHash.Length);

        var candidates = await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        var current = Assert.Single(candidates!, item => item.IsCurrent);
        Assert.Equal(shot.Version, current.Version);
        Assert.Equal(ApprovalState.Ratified, current.Approval);
    }

    [Fact]
    public async Task SketchDocumentsAreDurableRevisionCheckedAndHashed()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var existingResponse = await client.GetAsync($"/api/shots/{shot.Id}/sketch");
        var existing = existingResponse.StatusCode == HttpStatusCode.OK
            ? await existingResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>()
            : null;
        var request = new SaveSketchRequest(existing?.Revision ?? 0, "Locked profile with a low insignia eyeline.", ExampleSketch());

        var savedResponse = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch", request);
        var saved = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var loaded = await client.GetFromJsonAsync<SketchDocumentSummary>($"/api/shots/{shot.Id}/sketch");

        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        Assert.NotNull(saved);
        Assert.NotNull(loaded);
        Assert.Equal((existing?.Revision ?? 0) + 1, saved.Revision);
        Assert.Equal(saved.ContentHash, loaded.ContentHash);
        Assert.Equal(64, saved.ContentHash.Length);
        Assert.Single(loaded.Content.Strokes);

        var duplicateResponse = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch", request with { ExpectedRevision = saved.Revision });
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.Equal(saved.Revision, duplicate!.Revision);

        var stale = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch", request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task PoseableBlockersAreDurableValidatedAndCompiledIntoGenerationDirection()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var wardrobe = Assert.Single(snapshot!.References, reference => reference.Id == "guards");
        var shotResponse = await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-BLOCK", "Ceremonial stance", "Two ceremonial guards hold the entrance.", 72,
            "35mm equivalent · full-body two-shot · locked", "Hold at attention.", [wardrobe.Id], ["Both guards remain entirely visible."]));
        var shot = await shotResponse.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.NotNull(shot);

        var human = new SketchObject(
            "guard-left", "Human", .28, .54, .14, .5, 0, "#302b27", "Left ceremonial guard",
            "Attention", "Front", "Broad", "Unique person", true, null, wardrobe.Id,
            [new SketchJoint("head", .5, .1), new SketchJoint("neck", .5, .2), new SketchJoint("pelvis", .5, .55)]);
        var content = new SketchContent([], [], [human]);
        var saveResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Stage the guard exactly where blocked.", content));
        var saved = await saveResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.NotNull(saved);
        var loadedHuman = Assert.Single(saved.Content.Objects!);
        Assert.Equal("Attention", loadedHuman.Pose);
        Assert.Equal(wardrobe.Id, loadedHuman.WardrobeReferenceId);
        Assert.True(loadedHuman.FullBody);

        var manifestResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, saved.Revision, GenerationRoute.FastDraft));
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        Assert.NotNull(manifest);
        Assert.Contains("BLOCKING OBJECT CONTRACT", manifest.CreativeBrief);
        Assert.Contains("attention pose", manifest.CreativeBrief);
        Assert.Contains("distinct original person", manifest.CreativeBrief);
        Assert.Contains("entire figure visible from head to toe", manifest.CreativeBrief);
        Assert.Contains(wardrobe.Name, manifest.CreativeBrief);

        var invalidHuman = human with { Id = "invalid", IdentityMode = "Exact character", CharacterReferenceId = null };
        var invalidResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(saved.Revision, "Invalid exact identity.", new SketchContent([], [], [invalidHuman])));
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
    }

    [Fact]
    public async Task CustomPosePresetsAreProjectScopedDurableAndDeletable()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var joints = new[] { new SketchJoint("head", .5, .08), new SketchJoint("rightWrist", .88, .24), new SketchJoint("pelvis", .5, .54) };

        var create = await isolatedClient.PostAsJsonAsync("/api/pose-presets", new CreatePosePresetRequest("Ceremonial salute", joints));
        var saved = await create.Content.ReadFromJsonAsync<PosePresetSummary>();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(saved);
        Assert.Equal(joints.Length, saved.Joints.Count);

        var listed = await isolatedClient.GetFromJsonAsync<PosePresetSummary[]>("/api/pose-presets");
        Assert.Contains(listed!, item => item.Id == saved.Id && item.Name == "Ceremonial salute");

        var duplicate = await isolatedClient.PostAsJsonAsync("/api/pose-presets", new CreatePosePresetRequest("ceremonial salute", joints));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var invalid = await isolatedClient.PostAsJsonAsync("/api/pose-presets", new CreatePosePresetRequest("Broken", [new SketchJoint("head", 2, .5)]));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var deleted = await isolatedClient.DeleteAsync($"/api/pose-presets/{saved.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty((await isolatedClient.GetFromJsonAsync<PosePresetSummary[]>("/api/pose-presets"))!);
    }

    [Fact]
    public async Task FrameMarkupIsDurableAndBoundToAnExactCandidateVersion()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-040");
        var strokes = new[] { new SketchStroke("markup-1", [new SketchPoint(.1, .2, .5), new SketchPoint(.4, .6, .8)], "#f2d4a7", 4) };

        var response = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/versions/{shot.Version}/markup", new SaveFrameMarkupRequest(0, strokes));
        var saved = await response.Content.ReadFromJsonAsync<FrameMarkupSummary>();
        var loaded = await client.GetFromJsonAsync<FrameMarkupSummary>($"/api/shots/{shot.Id}/versions/{shot.Version}/markup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(saved);
        Assert.Equal(shot.Version, saved.Version);
        Assert.Equal(saved.ContentHash, loaded!.ContentHash);
        Assert.Single(loaded.Strokes);

        var stale = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/versions/{shot.Version}/markup", new SaveFrameMarkupRequest(0, []));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var unknownVersion = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/versions/{shot.Version + 999}/markup", new SaveFrameMarkupRequest(0, strokes));
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);
    }

    [Fact]
    public async Task FrameMarkupCanBeFrozenAsNonDestructiveGenerationGuidance()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-040");
        var sketchResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(0, "Preserve the current frame and correct the marked hand.", ExampleSketch()));
        var sketch = await sketchResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(sketch);

        var strokes = new[] { new SketchStroke("guide-1", [new SketchPoint(.2, .3, .5), new SketchPoint(.4, .5, .8)], "#f2d4a7", 4) };
        var markupResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/versions/{shot.Version}/markup", new SaveFrameMarkupRequest(0, strokes));
        var markup = await markupResponse.Content.ReadFromJsonAsync<FrameMarkupSummary>();
        Assert.NotNull(markup);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "marked-guide.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var uploadResponse = await isolatedClient.SendAsync(upload);
        var asset = await uploadResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.NotNull(asset);

        const string guideBrief = "Correct the marked hand.\n\nFRAME EDIT GUIDE: Gold ink is spatial guidance only and must not survive.";
        var preparedResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision, GenerationRoute.FastDraft, GenerationPurpose.Draft, asset.Id, guideBrief, markup.Revision));
        var manifest = await preparedResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();

        Assert.Equal(HttpStatusCode.OK, preparedResponse.StatusCode);
        Assert.NotNull(manifest);
        Assert.Equal(asset.Id, manifest.CompositionAssetId);
        Assert.Contains(guideBrief, manifest.CreativeBrief);
        Assert.Contains("VISUAL LANGUAGE", manifest.CreativeBrief);
        Assert.False(manifest.ProviderCallMade);
    }

    [Fact]
    public async Task ContinuityPreflightSeparatesMetadataProofFromVisualReviewAndBlocksGeneratedMusic()
    {
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-078", "Unsafe audio prompt", "The guard enters under swelling background music.", 48,
            "35mm locked", "Track left to right with the score.", ["guards", "style"], ["Two guards only"]));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        var report = await client.GetFromJsonAsync<ShotContinuityReport>($"/api/shots/{shot!.Id}/continuity");

        Assert.NotNull(report);
        Assert.Equal("Blocked", report.GateState);
        Assert.Contains(report.Checks, check => check.Category == "audio" && check.State == ContinuityCheckState.Block);
        Assert.Contains(report.Checks, check => check.RequiresVisualReview && check.State == ContinuityCheckState.Info);
        Assert.Contains("never claims to have inspected pixels", report.ScopeNote);

        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var offCamera = Assert.Single(snapshot!.Shots, item => item.Code == "SH-050");
        var protectedReport = await client.GetFromJsonAsync<ShotContinuityReport>($"/api/shots/{offCamera.Id}/continuity");
        Assert.Contains(protectedReport!.Checks, check => check.Category == "world-crop" && check.State == ContinuityCheckState.Pass);
        Assert.Contains(protectedReport.Checks, check => check.Category == "audio" && check.State == ContinuityCheckState.Pass);
    }

    [Fact]
    public async Task TimelineAudioAndVoiceProfilesAreRealEditableRecordsWithConsentGates()
    {
        var voiceSampleBytes = new byte[44]; "RIFF"u8.CopyTo(voiceSampleBytes); "WAVE"u8.CopyTo(voiceSampleBytes.AsSpan(8));
        using var sampleUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Audio") { Content = CreateMediaUpload(voiceSampleBytes, "ennix-approved.wav", "audio/wav") };
        sampleUpload.Headers.Add("X-Storyboard-Studio", "1");
        var sampleResponse = await client.SendAsync(sampleUpload);
        var voiceSample = await sampleResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, sampleResponse.StatusCode);

        var rejectedClone = await client.PostAsJsonAsync("/api/voice-profiles", new CreateVoiceProfileRequest(
            "Unsafe clone", VoiceProfileKind.ConsentedClone, "External voice service", "voice-1", "Ennix", false, "", "ennix", voiceSample!.Id));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedClone.StatusCode);

        var voiceResponse = await client.PostAsJsonAsync("/api/voice-profiles", new CreateVoiceProfileRequest(
            "Ennix editorial preset", VoiceProfileKind.ProviderPreset, "External voice service", "preset-warm-1", "Ennix", false, "", "ennix", voiceSample.Id));
        var voice = await voiceResponse.Content.ReadFromJsonAsync<VoiceProfileSummary>();
        Assert.Equal(HttpStatusCode.OK, voiceResponse.StatusCode);
        Assert.Null(voice!.ConsentedAt);
        Assert.Equal("ennix", voice.CharacterReferenceId);
        Assert.Equal(voiceSample.Id, voice.SampleAssetId);
        Assert.Contains("no person's voice was cloned", voice.ConsentAttestation);

        var wav = new byte[44]; "RIFF"u8.CopyTo(wav); "WAVE"u8.CopyTo(wav.AsSpan(8));
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Audio") { Content = CreateMediaUpload(wav, "guide.wav", "audio/wav") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var uploadResponse = await client.SendAsync(upload);
        var asset = await uploadResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        Assert.Equal(AssetKind.Audio, asset!.Kind);

        var clipResponse = await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(
            TimelineTrackKind.Voice, "Ennix line 1", 24, 72, 0, .85, asset.Id, voice.Id, "We cross at first light."));
        var clip = await clipResponse.Content.ReadFromJsonAsync<TimelineClipSummary>();
        Assert.Equal(HttpStatusCode.OK, clipResponse.StatusCode);
        Assert.Equal(voice.Id, clip!.VoiceProfileId);

        var update = await client.PutAsJsonAsync($"/api/timeline/clips/{clip.Id}", new SaveTimelineClipRequest(
            clip.Track, clip.Label, 30, clip.DurationFrames, .25, .6, clip.AssetId, clip.VoiceProfileId, clip.Text, clip.UpdatedAt));
        var updated = await update.Content.ReadFromJsonAsync<TimelineClipSummary>();
        Assert.Equal(30, updated!.StartFrame);
        Assert.Equal(.6, updated.Volume);

        var stale = await client.PutAsJsonAsync($"/api/timeline/clips/{clip.Id}", new SaveTimelineClipRequest(
            clip.Track, clip.Label, 40, clip.DurationFrames, 0, 1, clip.AssetId, clip.VoiceProfileId, clip.Text, clip.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task VideoManifestAcceptsWorkingImageEndpointsAndKeepsH3DispatchProtected()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "final.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1"); var uploadResponse = await isolatedClient.SendAsync(upload); var asset = await uploadResponse.Content.ReadFromJsonAsync<AssetSummary>();
        var endpointPng = png.Concat(new byte[] { 0 }).ToArray();
        using var endpointUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(endpointPng, "landing.png") };
        endpointUpload.Headers.Add("X-Storyboard-Studio", "1"); var endpointResponse = await isolatedClient.SendAsync(endpointUpload); var endpointAsset = await endpointResponse.Content.ReadFromJsonAsync<AssetSummary>();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"); var shot = Assert.Single(snapshot!.Shots, x => x.Code == "SH-040");
        var firstCandidateId = Guid.NewGuid(); var lastCandidateId = Guid.NewGuid();
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>(); var record = await db.Shots.SingleAsync(x => x.Id == shot.Id); record.Stage = "Final"; record.Approval = "Working"; record.CurrentAssetId = asset!.Id;
            // Still providers may return different source canvases. H3 keeps the
            // originals immutable and center-crops both into one project canvas.
            var firstAsset = await db.Assets.SingleAsync(x => x.Id == asset.Id); firstAsset.Width = 1672; firstAsset.Height = 941;
            var lastAsset = await db.Assets.SingleAsync(x => x.Id == endpointAsset!.Id); lastAsset.Width = 1920; lastAsset.Height = 1080;
            db.CandidateVersions.AddRange(
                new CandidateVersionRecord { Id = firstCandidateId, ShotId = shot.Id, Version = 901, Stage = "Final", Approval = "Working", IsCurrent = false, AssetId = asset.Id, CreatedAt = DateTimeOffset.UtcNow },
                new CandidateVersionRecord { Id = lastCandidateId, ShotId = shot.Id, Version = 902, Stage = "Draft", Approval = "Working", IsCurrent = false, AssetId = endpointAsset!.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var saveEndpoints = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints", new UpdateVideoEndpointsRequest(
            shot.UpdatedAt, firstCandidateId, lastCandidateId));
        var savedShot = await saveEndpoints.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.Equal(HttpStatusCode.OK, saveEndpoints.StatusCode);
        Assert.Equal(firstCandidateId, savedShot!.VideoFirstFrameCandidateId);
        Assert.Equal(lastCandidateId, savedShot.VideoLastFrameCandidateId);
        var referencedDelete = await isolatedClient.DeleteAsync($"/api/shots/{shot.Id}/candidates/{lastCandidateId}");
        Assert.Equal(HttpStatusCode.Conflict, referencedDelete.StatusCode);
        Assert.Contains("selected video endpoint", await referencedDelete.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var prepared = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare", new PrepareVideoManifestRequest(
            shot.Version, "A controlled lateral cross, then settle.", ConfirmEndpointCompatibility: true,
            FirstFrameCandidateId: firstCandidateId, LastFrameCandidateId: lastCandidateId,
            Quality: VideoQuality.Medium, TakeId: "take-contract-1"));
        var manifest = await prepared.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode); Assert.Equal(GenerationPurpose.Video, manifest!.Purpose); Assert.Equal(asset!.Id, manifest.CompositionAssetId); Assert.Equal(endpointAsset!.Id, manifest.LastFrameAssetId); Assert.Contains("Do not generate background music", manifest.CreativeBrief); Assert.False(manifest.ProviderCallMade);
        Assert.Equal(VideoQuality.Medium, manifest.VideoQuality); Assert.Equal("take-contract-1", manifest.VideoTakeId); Assert.NotNull(manifest.VideoSeed);
        var max = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare", new PrepareVideoManifestRequest(
            shot.Version, "A controlled lateral cross, then settle.", ConfirmEndpointCompatibility: true,
            FirstFrameCandidateId: firstCandidateId, Quality: VideoQuality.Max, TakeId: "take-contract-2"));
        Assert.Equal(HttpStatusCode.BadRequest, max.StatusCode);
        // Video submission is enabled but has no curated H3 workflow yet, so the
        // adapter reports NeedsSetup rather than Protected. What must stay true is
        // the invariant, not the label: H3 cannot dispatch, and trying conflicts.
        var adapters = await isolatedClient.GetFromJsonAsync<List<GenerationAdapterSummary>>("/api/generation/adapters"); Assert.Contains(adapters!, x => x.Id == "comfyui-h3-video" && !x.CanDispatch && x.State is "Protected" or "NeedsSetup");
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest.Id}/dispatch", new DispatchManifestRequest(manifest.ManifestHash, "comfyui-h3-video")); Assert.Equal(HttpStatusCode.Conflict, dispatch.StatusCode);

        var sourceJobId = Guid.NewGuid();
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var storedManifest = await db.GenerationManifests.SingleAsync(x => x.Id == manifest.Id);
            storedManifest.State = ManifestState.Completed.ToString();
            using (var packet = JsonDocument.Parse(storedManifest.ManifestJson))
            {
                var frozenShot = packet.RootElement.GetProperty("shot");
                Assert.Equal(shot.Description, frozenShot.GetProperty("description").GetString());
                Assert.Equal(shot.DurationFrames, frozenShot.GetProperty("durationFrames").GetInt32());
                Assert.Equal(snapshot.Project.FramesPerSecond, packet.RootElement.GetProperty("projectFormat").GetProperty("framesPerSecond").GetInt32());
            }
            var videoAsset = new AssetRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = StudioDefaults.ProjectId,
                Kind = AssetKind.Video.ToString(),
                OriginalFileName = "reviewed-high.mp4",
                MimeType = "video/mp4",
                Bytes = 24,
                ContentHash = new string('b', 64),
                StoragePath = "reviewed-high.mp4",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Assets.Add(videoAsset);
            db.Jobs.Add(new JobRecord
            {
                Id = sourceJobId,
                ShotId = shot.Id,
                ShotCode = shot.Code,
                Kind = "Video",
                State = JobState.Completed.ToString(),
                Progress = 100,
                Phase = "Review ready",
                Backend = "ComfyUI · H3",
                CreatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ManifestId = manifest.Id,
                AdapterId = "comfyui-h3-video",
                OutputAssetId = videoAsset.Id
            });
            var liveCandidate = await db.CandidateVersions.SingleAsync(x => x.ShotId == shot.Id && x.IsCurrent);
            liveCandidate.SourceManifestId = storedManifest.Id;
            await db.SaveChangesAsync();
            var repository = scope.ServiceProvider.GetRequiredService<StudioRepository>();
            var liveShot = await db.Shots.SingleAsync(x => x.Id == shot.Id);
            liveShot.Description += " This edit happened after the review render.";
            liveShot.DurationFrames += 13;
            await db.SaveChangesAsync();
            var stalePromotion = await repository.PrepareVideoPromotionManifestAsync(shot.Id, sourceJobId, shot.Version, CancellationToken.None);
            Assert.Equal(RepositoryResultKind.Conflict, stalePromotion.Kind);
            Assert.Contains("description, timing", stalePromotion.Error, StringComparison.OrdinalIgnoreCase);
            liveShot.Description = shot.Description;
            liveShot.DurationFrames = shot.DurationFrames;
            await db.SaveChangesAsync();
            var promoted = await repository.PrepareVideoPromotionManifestAsync(shot.Id, sourceJobId, shot.Version, CancellationToken.None);
            Assert.Equal(RepositoryResultKind.Ok, promoted.Kind);
            Assert.Equal(VideoQuality.Max, promoted.Value!.VideoQuality);
            Assert.Equal(manifest.VideoSeed, promoted.Value.VideoSeed);
            Assert.Equal(manifest.VideoTakeId, promoted.Value.VideoTakeId);
            Assert.Equal(sourceJobId, promoted.Value.PromotedFromJobId);

            var promotedRecord = await db.GenerationManifests.SingleAsync(x => x.Id == promoted.Value.Id);
            using var sourcePacket = JsonDocument.Parse(storedManifest.ManifestJson);
            using var promotedPacket = JsonDocument.Parse(promotedRecord.ManifestJson);
            var sourceCanvas = sourcePacket.RootElement.GetProperty("videoCanvas");
            var promotedCanvas = promotedPacket.RootElement.GetProperty("videoCanvas");
            var expectedMax = ExpectedVideoCanvas(snapshot.Project, VideoQuality.Max);
            Assert.Equal(expectedMax.Width, promotedCanvas.GetProperty("width").GetInt32());
            Assert.Equal(expectedMax.Height, promotedCanvas.GetProperty("height").GetInt32());
            Assert.NotEqual(sourceCanvas.GetProperty("width").GetInt32(), promotedCanvas.GetProperty("width").GetInt32());

            var ratifiedShot = await db.Shots.SingleAsync(x => x.Id == shot.Id);
            ratifiedShot.Stage = nameof(ShotStage.Video);
            ratifiedShot.Approval = nameof(ApprovalState.Ratified);
            ratifiedShot.ContinuityState = "Clear";
            ratifiedShot.ProductionVideoJobId = sourceJobId;
            ratifiedShot.ProductionVideoAssetId = videoAsset.Id;
            ratifiedShot.UpdatedAt = DateTimeOffset.UtcNow;
            var currentCandidate = await db.CandidateVersions.SingleOrDefaultAsync(x => x.ShotId == shot.Id && x.IsCurrent);
            if (currentCandidate is not null) currentCandidate.Approval = nameof(ApprovalState.Ratified);
            await db.SaveChangesAsync();
        }

        var ratifiedSnapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var ratifiedLiveShot = Assert.Single(ratifiedSnapshot!.Shots, item => item.Id == shot.Id);
        var changeEndpoints = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/video-endpoints",
            new UpdateVideoEndpointsRequest(ratifiedLiveShot.UpdatedAt, firstCandidateId, null));
        var reopened = await changeEndpoints.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.Equal(HttpStatusCode.OK, changeEndpoints.StatusCode);
        Assert.Equal(ApprovalState.Working, reopened!.Approval);
        Assert.Equal("Review", reopened.ContinuityState);
        Assert.Null(reopened.ProductionVideoJobId);
        Assert.Null(reopened.ProductionVideoAssetId);

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var currentCandidate = await db.CandidateVersions.SingleOrDefaultAsync(x => x.ShotId == shot.Id && x.IsCurrent);
            if (currentCandidate is not null) Assert.Equal(nameof(ApprovalState.Working), currentCandidate.Approval);
        }

        static (int Width, int Height) ExpectedVideoCanvas(ProjectSummary project, VideoQuality quality)
        {
            var targetPixels = quality switch { VideoQuality.Medium => 560_000d, VideoQuality.High or VideoQuality.Max => 750_000d, _ => 400_000d };
            var aspect = (double)project.DeliveryWidth / project.DeliveryHeight;
            var height = Math.Max(32, (int)Math.Round(Math.Sqrt(targetPixels / aspect) / 32d) * 32);
            var width = Math.Max(32, (int)Math.Round(height * aspect / 32d) * 32);
            return (width, height);
        }
    }

    [Fact]
    public async Task WorkingExportContainsCanonTimelinePolicyInventoryAndContentAddressedAssets()
    {
        var response = await client.GetAsync("/api/export/working-package"); var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Working export returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); var manifestEntry = archive.GetEntry("production-manifest.json"); Assert.NotNull(manifestEntry);
        using var json = JsonDocument.Parse(manifestEntry!.Open()); var root = json.RootElement;
        Assert.Equal(5, root.GetProperty("schemaVersion").GetInt32()); Assert.Equal("WorkingCopy", root.GetProperty("packageKind").GetString());
        Assert.True(root.GetProperty("policy").GetProperty("audioIsSeparate").GetBoolean()); Assert.False(root.GetProperty("policy").GetProperty("generatedBackgroundMusicAllowed").GetBoolean()); Assert.True(root.GetProperty("shots").GetArrayLength() >= 6); Assert.True(root.GetProperty("timeline").GetArrayLength() >= 2);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("scenes").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneInstances").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneAnnotations").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneProposals").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneBlockoutPlans").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneBlockoutItems").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sceneShotBindings").ValueKind);
        foreach (var asset in root.GetProperty("assets").EnumerateArray()) Assert.NotNull(archive.GetEntry(asset.GetProperty("archivePath").GetString()!));
        Assert.NotNull(archive.GetEntry("package-inventory.json"));
    }

    [Fact]
    public async Task ProductionExportExposesActionableReadinessAndRefusesAnUnreviewedDelivery()
    {
        var readiness = await client.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.NotNull(readiness);
        Assert.False(readiness.CanExportProduction);
        Assert.NotEmpty(readiness.Blockers);

        var response = await client.GetAsync("/api/export/production-package");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("The production package is blocked by unresolved delivery checks.", problem.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProductionReadinessCountsOnlyOpenNotesOnTheCurrentShotRevision()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var baseline = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.NotNull(baseline);

        Guid currentNoteId;
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var shot = await db.Shots.OrderBy(item => item.SortOrder).FirstAsync();
            currentNoteId = Guid.NewGuid();
            db.Comments.AddRange(
                new CommentRecord
                {
                    Id = Guid.NewGuid(),
                    ProjectId = shot.ProjectId,
                    ShotId = shot.Id,
                    Version = Math.Max(0, shot.Version - 1),
                    X = 0.2,
                    Y = 0.2,
                    Body = "Historical note retained with the superseded revision.",
                    State = "Open",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new CommentRecord
                {
                    Id = currentNoteId,
                    ProjectId = shot.ProjectId,
                    ShotId = shot.Id,
                    Version = shot.Version,
                    X = 0.4,
                    Y = 0.4,
                    Body = "Current revision still needs this correction.",
                    State = "Open",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var withNotes = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.Equal(baseline.OpenNoteCount + 1, withNotes!.OpenNoteCount);

        var resolved = await isolatedClient.PostAsync($"/api/comments/{currentNoteId}/resolve", content: null);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var afterResolve = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.Equal(baseline.OpenNoteCount, afterResolve!.OpenNoteCount);
    }

    [Fact]
    public async Task ProductionReadinessRejectsTamperedGenerationAndRatifiedShotEvidence()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var shot = await db.Shots.OrderBy(item => item.SortOrder)
                .FirstAsync(item => item.Approval == nameof(ApprovalState.Ratified));
            const string originalPacket = "{}";
            var manifest = new GenerationManifestRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = shot.ProjectId,
                ShotId = shot.Id,
                ShotCode = shot.Code,
                ShotVersion = shot.Version,
                SketchId = Guid.Empty,
                SketchRevision = 0,
                Route = GenerationRoute.FastDraft.ToString(),
                Purpose = GenerationPurpose.Draft.ToString(),
                State = ManifestState.Completed.ToString(),
                CreativeBrief = "Immutable evidence probe.",
                AuthoritiesJson = "[]",
                ConstraintsJson = "[]",
                ManifestJson = originalPacket,
                ManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalPacket))).ToLowerInvariant(),
                ProviderCallMade = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.GenerationManifests.Add(manifest);
            await db.SaveChangesAsync();

            manifest.ManifestJson = "{\"tampered\":true}";
            shot.Description += " This mutation bypassed the revision workflow.";
            await db.SaveChangesAsync();
        }

        var readiness = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.False(readiness!.CanExportProduction);
        Assert.Contains(readiness.Blockers, blocker =>
            blocker.Contains("generation manifest", StringComparison.OrdinalIgnoreCase) &&
            blocker.Contains("stored hash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(readiness.Blockers, blocker =>
            blocker.Contains("exact immutable archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VideoRatificationRequiresTheBoundMaxTakeAndExportPreservesItsIdentity()
    {
        var mediaProbe = new ControllableTestVideoMediaProbe();
        using var isolatedFactory = new StudioApiFactory(mediaProbe);
        using var isolatedClient = isolatedFactory.CreateClient();
        var lowBytes = new byte[24];
        "ftyp"u8.CopyTo(lowBytes.AsSpan(4));
        lowBytes[^1] = 1;
        var maxBytes = new byte[24];
        "ftyp"u8.CopyTo(maxBytes.AsSpan(4));
        maxBytes[^1] = 2;

        using var lowUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Video")
        {
            Content = CreateMediaUpload(lowBytes, "review-low.mp4", "video/mp4")
        };
        lowUpload.Headers.Add("X-Storyboard-Studio", "1");
        var lowResponse = await isolatedClient.SendAsync(lowUpload);
        var lowAsset = await lowResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, lowResponse.StatusCode);

        using var maxUpload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Video")
        {
            Content = CreateMediaUpload(maxBytes, "production-max.mp4", "video/mp4")
        };
        maxUpload.Headers.Add("X-Storyboard-Studio", "1");
        var maxResponse = await isolatedClient.SendAsync(maxUpload);
        var maxAsset = await maxResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, maxResponse.StatusCode);

        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var lowManifestId = Guid.NewGuid();
        var maxManifestId = Guid.NewGuid();
        var lowJobId = Guid.NewGuid();
        var maxJobId = Guid.NewGuid();

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var record = await db.Shots.SingleAsync(x => x.Id == shot.Id);
            record.Stage = ShotStage.Video.ToString();
            record.Approval = ApprovalState.Working.ToString();
            record.ProductionVideoJobId = lowJobId;
            record.ProductionVideoAssetId = lowAsset!.Id;
            db.GenerationManifests.AddRange(
                // This deliberately lies about quality while retaining the Low
                // canvas. Ratification must inspect the frozen canvas, not trust
                // a Max label that an older or malformed packet could carry.
                VideoManifest(lowManifestId, record, snapshot.Project, VideoQuality.Max, VideoQuality.Low),
                VideoManifest(maxManifestId, record, snapshot.Project, VideoQuality.Max, VideoQuality.Max));
            db.Jobs.AddRange(
                CompletedVideoJob(lowJobId, record, lowManifestId, lowAsset.Id),
                CompletedVideoJob(maxJobId, record, maxManifestId, maxAsset!.Id));
            await db.SaveChangesAsync();
        }

        var rejected = await isolatedClient.PostAsJsonAsync(
            $"/api/shots/{shot.Id}/ratify",
            new RatifyRequest(shot.Version, "A proxy is not a production master."));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("Max-quality", await rejected.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var record = await db.Shots.SingleAsync(x => x.Id == shot.Id);
            record.ProductionVideoJobId = maxJobId;
            record.ProductionVideoAssetId = maxAsset!.Id;
            await db.SaveChangesAsync();
        }

        var encodedMismatch = await isolatedClient.PostAsJsonAsync(
            $"/api/shots/{shot.Id}/ratify",
            new RatifyRequest(shot.Version, "The label says Max, but the encoded stream is stale."));
        Assert.Equal(HttpStatusCode.Conflict, encodedMismatch.StatusCode);
        Assert.Contains("encoded stream", await encodedMismatch.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        mediaProbe.Accept = true;
        var approved = await isolatedClient.PostAsJsonAsync(
            $"/api/shots/{shot.Id}/ratify",
            new RatifyRequest(shot.Version, "Reviewed Max take selected for delivery."));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var archivedVersions = await isolatedClient.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{shot.Id}/versions");
        var archived = Assert.Single(archivedVersions!, version => version.Version == shot.Version);
        Assert.Equal(maxJobId, archived.ProductionVideoJobId);
        Assert.Equal(maxAsset!.Id, archived.ProductionVideoAssetId);

        var packageResponse = await isolatedClient.GetAsync("/api/export/working-package");
        Assert.Equal(HttpStatusCode.OK, packageResponse.StatusCode);
        using var package = new ZipArchive(
            new MemoryStream(await packageResponse.Content.ReadAsByteArrayAsync()),
            ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(package.GetEntry("production-manifest.json")!.Open());
        var exportedShot = Assert.Single(
            manifest.RootElement.GetProperty("shots").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == shot.Id);
        Assert.Equal(maxJobId, exportedShot.GetProperty("productionVideoJobId").GetGuid());
        Assert.Equal(maxAsset!.Id, exportedShot.GetProperty("productionVideoAssetId").GetGuid());
        var exportedAsset = Assert.Single(
            manifest.RootElement.GetProperty("assets").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == maxAsset.Id);
        Assert.Equal(maxAsset.ContentHash, exportedAsset.GetProperty("contentHash").GetString());

        var formatChange = await isolatedClient.PutAsJsonAsync("/api/project", new UpdateProjectRequest(
            snapshot.Project.UpdatedAt,
            snapshot.Project.Name,
            snapshot.Project.Production,
            snapshot.Project.SequenceCode,
            snapshot.Project.SequenceName,
            snapshot.Project.FramesPerSecond + 1,
            snapshot.Project.AspectRatio,
            snapshot.Project.DeliveryWidth,
            snapshot.Project.DeliveryHeight,
            snapshot.Project.VisualStyle,
            snapshot.Project.WorldCanon,
            snapshot.Project.PromptDirectives,
            snapshot.Project.NegativeDirectives,
            snapshot.Project.ColorSpace,
            snapshot.Project.AudioSampleRate));
        Assert.Equal(HttpStatusCode.OK, formatChange.StatusCode);

        var afterFormatChange = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var reopened = Assert.Single(afterFormatChange!.Shots, item => item.Id == shot.Id);
        Assert.Equal(ApprovalState.Working, reopened.Approval);
        Assert.Null(reopened.ProductionVideoJobId);
        Assert.Null(reopened.ProductionVideoAssetId);
        var readiness = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.Contains(readiness!.Blockers, blocker =>
            blocker.Contains("current project delivery format", StringComparison.OrdinalIgnoreCase) &&
            blocker.Contains(shot.Code, StringComparison.OrdinalIgnoreCase));
        var archivedAfterFormatChange = await isolatedClient.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{shot.Id}/versions");
        var preservedAuthority = Assert.Single(archivedAfterFormatChange!, version => version.Version == shot.Version);
        Assert.Equal(maxJobId, preservedAuthority.ProductionVideoJobId);
        Assert.Equal(maxAsset.Id, preservedAuthority.ProductionVideoAssetId);

        static GenerationManifestRecord VideoManifest(
            Guid id,
            ShotRecord shotRecord,
            ProjectSummary project,
            VideoQuality quality,
            VideoQuality canvasQuality) => new()
        {
            Id = id,
            ShotId = shotRecord.Id,
            ShotCode = shotRecord.Code,
            ShotVersion = shotRecord.Version,
            SketchId = Guid.Empty,
            SketchRevision = 0,
            Route = GenerationRoute.FastDraft.ToString(),
            Purpose = GenerationPurpose.Video.ToString(),
            State = ManifestState.Completed.ToString(),
            CreativeBrief = "A measured production take.",
            AuthoritiesJson = "[]",
            ConstraintsJson = "[]",
            ManifestJson = VideoManifestJson(project, shotRecord, quality, canvasQuality),
            ManifestHash = id.ToString("N").PadRight(64, '0'),
            ProviderCallMade = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        static string VideoManifestJson(ProjectSummary project, ShotRecord shotRecord, VideoQuality quality, VideoQuality canvasQuality)
        {
            var targetPixels = canvasQuality switch { VideoQuality.Medium => 560_000d, VideoQuality.High or VideoQuality.Max => 750_000d, _ => 400_000d };
            var aspect = (double)project.DeliveryWidth / project.DeliveryHeight;
            var height = Math.Max(32, (int)Math.Round(Math.Sqrt(targetPixels / aspect) / 32d) * 32);
            var width = Math.Max(32, (int)Math.Round(height * aspect / 32d) * 32);
            return JsonSerializer.Serialize(new
            {
                projectFormat = new
                {
                    project.AspectRatio,
                    project.FramesPerSecond,
                    project.DeliveryWidth,
                    project.DeliveryHeight,
                    project.ColorSpace,
                    project.AudioSampleRate
                },
                shot = new
                {
                    shotRecord.Id,
                    shotRecord.Code,
                    version = shotRecord.Version,
                    shotRecord.Description,
                    shotRecord.DurationFrames,
                    shotRecord.Camera,
                    shotRecord.Action
                },
                videoCanvas = new { width, height },
                videoQuality = quality.ToString()
            }, WebJsonOptions);
        }

        static JobRecord CompletedVideoJob(Guid id, ShotRecord shotRecord, Guid manifestId, Guid assetId) => new()
        {
            Id = id,
            ShotId = shotRecord.Id,
            ShotCode = shotRecord.Code,
            Kind = "Video",
            State = JobState.Completed.ToString(),
            Progress = 100,
            Phase = "Review ready",
            Backend = "ComfyUI · H3",
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ManifestId = manifestId,
            AdapterId = "comfyui-h3-video",
            OutputAssetId = assetId,
            ProviderRequestId = id.ToString("N")
        };
    }

    [Fact]
    public async Task RatifiedShotArchivePreservesCompleteIntentAndEndpointsAfterLaterEdits()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var seeded = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var firstEndpoint = Guid.NewGuid();
        var lastEndpoint = Guid.NewGuid();
        const string originalTitle = "The immutable turn";
        const string originalDescription = "The Magistrate sees the seal and stops before speaking.";
        const string originalCamera = "65mm - locked profile";
        const string originalAction = "Turn on the final beat, then hold for twelve frames.";
        const int originalDuration = 137;
        DateTimeOffset expectedUpdatedAt;
        int ratifiedVersion;

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var shot = await db.Shots.SingleAsync(x => x.Id == seeded.Id);
            shot.Title = originalTitle;
            shot.Description = originalDescription;
            shot.DurationFrames = originalDuration;
            shot.Camera = originalCamera;
            shot.Action = originalAction;
            shot.VideoFirstFrameCandidateId = firstEndpoint;
            shot.VideoLastFrameCandidateId = lastEndpoint;
            shot.Approval = ApprovalState.Ratified.ToString();
            shot.UpdatedAt = DateTimeOffset.UtcNow;
            expectedUpdatedAt = shot.UpdatedAt;
            ratifiedVersion = shot.Version;
            db.ShotVersions.Add(StudioDatabaseInitializer.CreateAuthority(shot, shot.UpdatedAt));
            await db.SaveChangesAsync();
        }

        var updateResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{seeded.Id}", new UpdateShotRequest(
            expectedUpdatedAt,
            "The revised turn",
            "The Magistrate crosses the court after speaking.",
            96,
            "40mm - moving two-shot",
            "Cross on the dialogue and settle beside the guard.",
            seeded.ReferenceIds,
            seeded.Constraints));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var versions = await isolatedClient.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{seeded.Id}/versions");
        var archived = Assert.Single(versions!, item => item.Version == ratifiedVersion);
        Assert.Equal(seeded.Code, archived.Code);
        Assert.Equal(originalTitle, archived.Title);
        Assert.Equal(originalDescription, archived.Description);
        Assert.Equal(originalDuration, archived.DurationFrames);
        Assert.Equal(originalCamera, archived.Camera);
        Assert.Equal(originalAction, archived.Action);
        Assert.Equal(firstEndpoint, archived.VideoFirstFrameCandidateId);
        Assert.Equal(lastEndpoint, archived.VideoLastFrameCandidateId);

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var edited = await db.Shots.SingleAsync(x => x.Id == seeded.Id);
            var editedAuthority = StudioDatabaseInitializer.CreateAuthority(edited, edited.UpdatedAt);
            Assert.NotEqual(archived.ManifestHash, editedAuthority.ManifestHash);

            var audits = await db.AuditEvents.AsNoTracking()
                .Where(x => x.Type == "ShotUpdated" && x.TargetId == seeded.Id.ToString())
                .ToListAsync();
            var audit = audits.OrderByDescending(x => x.CreatedAt).First();
            Assert.Contains(originalTitle, audit.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("The revised turn", audit.PayloadJson, StringComparison.Ordinal);
        }

        var packageResponse = await isolatedClient.GetAsync("/api/export/working-package");
        Assert.Equal(HttpStatusCode.OK, packageResponse.StatusCode);
        using var package = new ZipArchive(
            new MemoryStream(await packageResponse.Content.ReadAsByteArrayAsync()),
            ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(package.GetEntry("production-manifest.json")!.Open());
        var exported = Assert.Single(
            manifest.RootElement.GetProperty("shotVersions").EnumerateArray(),
            item => item.GetProperty("shotId").GetGuid() == seeded.Id &&
                    item.GetProperty("version").GetInt32() == ratifiedVersion);
        Assert.Equal(originalTitle, exported.GetProperty("title").GetString());
        Assert.Equal(originalDescription, exported.GetProperty("description").GetString());
        Assert.Equal(originalDuration, exported.GetProperty("durationFrames").GetInt32());
        Assert.Equal(originalCamera, exported.GetProperty("camera").GetString());
        Assert.Equal(originalAction, exported.GetProperty("action").GetString());
        Assert.Equal(firstEndpoint, exported.GetProperty("videoFirstFrameCandidateId").GetGuid());
        Assert.Equal(lastEndpoint, exported.GetProperty("videoLastFrameCandidateId").GetGuid());
    }

    [Fact]
    public async Task ChangingWorldLawsReopensRatifiedVisualsAndInvalidatesBoundVideo()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var selected = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var productionJobId = Guid.NewGuid();
        var productionAssetId = Guid.NewGuid();
        var sourceManifestId = Guid.NewGuid();

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var shot = await db.Shots.SingleAsync(x => x.Id == selected.Id);
            shot.Stage = ShotStage.Video.ToString();
            shot.Approval = ApprovalState.Ratified.ToString();
            shot.ProductionVideoJobId = productionJobId;
            shot.ProductionVideoAssetId = productionAssetId;
            shot.UpdatedAt = DateTimeOffset.UtcNow;
            var candidate = await db.CandidateVersions.SingleAsync(x => x.ShotId == selected.Id && x.IsCurrent);
            candidate.Stage = shot.Stage;
            candidate.Approval = shot.Approval;
            candidate.SourceManifestId = sourceManifestId;
            var references = await db.References.AsNoTracking()
                .Where(reference => selected.ReferenceIds.Contains(reference.Id))
                .ToDictionaryAsync(reference => reference.Id);
            var authorities = selected.ReferenceIds.Select(id =>
            {
                var reference = references[id];
                return new AuthorityBinding(reference.Id, reference.Name, reference.Category, reference.CurrentVersion);
            }).ToArray();
            var constraints = selected.Constraints.ToArray();
            const string creativeBrief = "A review take frozen under the original world settings.";
            var packet = JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                worldSettings = new
                {
                    snapshot.Project.VisualStyle,
                    snapshot.Project.WorldCanon,
                    snapshot.Project.PromptDirectives,
                    snapshot.Project.NegativeDirectives
                },
                shot = new { shot.Id, shot.Code, version = shot.Version, shot.Camera, shot.Action },
                creativeBrief,
                authorities,
                constraints,
                videoQuality = VideoQuality.Low.ToString(),
                videoSeed = 42L,
                videoTakeId = "old-world-take"
            }, WebJsonOptions);
            db.GenerationManifests.Add(new GenerationManifestRecord
            {
                Id = sourceManifestId,
                ShotId = shot.Id,
                ShotCode = shot.Code,
                ShotVersion = shot.Version,
                SketchId = Guid.Empty,
                SketchRevision = 0,
                Route = GenerationRoute.FastDraft.ToString(),
                Purpose = GenerationPurpose.Video.ToString(),
                State = ManifestState.Completed.ToString(),
                CreativeBrief = creativeBrief,
                AuthoritiesJson = JsonSerializer.Serialize(authorities, WebJsonOptions),
                ConstraintsJson = JsonSerializer.Serialize(constraints, WebJsonOptions),
                ManifestJson = packet,
                ManifestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packet))).ToLowerInvariant(),
                ProviderCallMade = true,
                CreatedAt = shot.UpdatedAt
            });
            db.Jobs.Add(new JobRecord
            {
                Id = productionJobId,
                ShotId = shot.Id,
                ShotCode = shot.Code,
                Kind = "Video",
                State = JobState.Completed.ToString(),
                Progress = 100,
                Phase = "Review ready",
                Backend = "Frozen old-world test take",
                CreatedAt = shot.UpdatedAt,
                CompletedAt = shot.UpdatedAt,
                ManifestId = sourceManifestId,
                OutputAssetId = productionAssetId
            });
            db.ShotVersions.Add(StudioDatabaseInitializer.CreateAuthority(shot, shot.UpdatedAt));
            await db.SaveChangesAsync();
        }

        var beforeChange = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var project = beforeChange!.Project;
        var response = await isolatedClient.PutAsJsonAsync("/api/project", new UpdateProjectRequest(
            project.UpdatedAt,
            project.Name,
            project.Production,
            project.SequenceCode,
            project.SequenceName,
            project.FramesPerSecond,
            project.AspectRatio,
            project.DeliveryWidth,
            project.DeliveryHeight,
            project.VisualStyle + " Preserve the new charcoal edge treatment.",
            project.WorldCanon,
            project.PromptDirectives,
            project.NegativeDirectives,
            project.ColorSpace,
            project.AudioSampleRate));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var afterChange = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var reopened = Assert.Single(afterChange!.Shots, item => item.Id == selected.Id);
        Assert.Equal(ApprovalState.Working, reopened.Approval);
        Assert.Null(reopened.ProductionVideoJobId);
        Assert.Null(reopened.ProductionVideoAssetId);
        Assert.True(reopened.Version > selected.Version);
        var archivedVersions = await isolatedClient.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{selected.Id}/versions");
        var archived = Assert.Single(archivedVersions!, item => item.Version == selected.Version);
        Assert.Equal(productionJobId, archived.ProductionVideoJobId);
        Assert.Equal(productionAssetId, archived.ProductionVideoAssetId);

        await using (var scope = isolatedFactory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<StudioRepository>();
            var stalePromotion = await repository.PrepareVideoPromotionManifestAsync(
                selected.Id, productionJobId, reopened.Version, CancellationToken.None);
            Assert.Equal(RepositoryResultKind.Conflict, stalePromotion.Kind);
            Assert.Contains("current shot revision", stalePromotion.Error, StringComparison.OrdinalIgnoreCase);
        }

        var readiness = await isolatedClient.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status");
        Assert.Contains(readiness!.Blockers, blocker =>
            blocker.Contains(selected.Code, StringComparison.OrdinalIgnoreCase) &&
            blocker.Contains("Ratify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreparedManifestFreezesExactInputsWithoutDispatchingAProvider()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var currentResponse = await client.GetAsync($"/api/shots/{shot.Id}/sketch");
        var current = currentResponse.StatusCode == HttpStatusCode.OK
            ? await currentResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>()
            : null;
        var savedResponse = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(current?.Revision ?? 0, "Preserve the ratified Magistrate profile and two clasps.", ExampleSketch()));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(sketch);

        var response = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision, GenerationRoute.FastDraft));
        var manifest = await response.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var duplicateResponse = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision, GenerationRoute.FastDraft));
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var stored = await client.GetFromJsonAsync<List<GenerationManifestSummary>>($"/api/shots/{shot.Id}/manifests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(manifest);
        Assert.Equal(ManifestState.Prepared, manifest.State);
        Assert.False(manifest.ProviderCallMade);
        Assert.Equal(64, manifest.ManifestHash.Length);
        Assert.Equal(manifest.Id, duplicate!.Id);
        Assert.Contains(manifest.Authorities, item => item.Id == "magistrate" && item.Version == 3);
        Assert.Contains(stored!, item => item.Id == manifest.Id && item.ManifestHash == manifest.ManifestHash);
    }

    [Fact]
    public async Task ProtectedRedirectDoesNotMutateTheSavedCreativeBrief()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var savedResponse = await isolatedClient.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(0, "Hold the profile and preserve the two clasps.", ExampleSketch()));
        var saved = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(saved);

        var redirect = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/redirect",
            new RedirectShotRequest("Push the camera closer."));
        var after = await isolatedClient.GetFromJsonAsync<SketchDocumentSummary>($"/api/shots/{shot.Id}/sketch");

        Assert.Equal(HttpStatusCode.Conflict, redirect.StatusCode);
        Assert.NotNull(after);
        Assert.Equal(saved.Revision, after.Revision);
        Assert.Equal(saved.CreativeBrief, after.CreativeBrief);
    }

    [Fact]
    public async Task ManifestPreparationRejectsStaleShotOrSketchInputs()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var shot = Assert.Single(snapshot!.Shots, item => item.Code == "SH-030");
        var currentResponse = await client.GetAsync($"/api/shots/{shot.Id}/sketch");
        var current = currentResponse.StatusCode == HttpStatusCode.OK
            ? await currentResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>()
            : null;
        var savedResponse = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(current?.Revision ?? 0, "Stale-input contract test.", ExampleSketch()));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(sketch);

        var staleShot = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version + 1, sketch.Revision, GenerationRoute.PrecisionDraft));
        var staleSketch = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision + 1, GenerationRoute.PrecisionDraft));

        Assert.Equal(HttpStatusCode.Conflict, staleShot.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleSketch.StatusCode);
    }

    [Fact]
    public async Task FrozenManifestDispatchCreatesADurableOutputWithoutAProviderCall()
    {
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-076", "Adapter proof", "A safe adapter proof frame.", 48,
            "35mm · locked", "Hold composition.", ["style"], ["No generated background music"]));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.NotNull(shot);
        var savedResponse = await client.PutAsJsonAsync($"/api/shots/{shot.Id}/sketch",
            new SaveSketchRequest(0, "Render a safe composition proof.", ExampleSketch()));
        var sketch = await savedResponse.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        Assert.NotNull(sketch);
        var prepare = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch.Revision, GenerationRoute.FastDraft));
        var manifest = await prepare.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.NotNull(manifest);

        var preflight = await client.GetFromJsonAsync<GenerationPreflightSummary>($"/api/manifests/{manifest.Id}/preflight?adapterId=local-proof");
        Assert.NotNull(preflight);
        Assert.True(preflight.Ready);
        Assert.Equal("local-proof", preflight.WorkflowId);
        Assert.True(preflight.DeliveryWidth >= 320);
        Assert.Contains(preflight.Checks, check => check.Title == "Provider is ready" && check.State == "Pass");

        var adapters = await client.GetFromJsonAsync<List<GenerationAdapterSummary>>("/api/generation/adapters");
        Assert.Contains(adapters!, x => x.Id == "local-proof" && x.CanDispatch);
        Assert.Contains(adapters!, x => x.Id == "openai-gpt-image" && !x.CanDispatch && x.State == "NeedsSetup");
        var dispatch = await client.PostAsJsonAsync($"/api/manifests/{manifest.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "local-proof"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);
        Assert.Equal(manifest.Id, job!.ManifestId);

        StudioSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            job = snapshot!.Jobs.Single(x => x.Id == job.Id);
            if (job.State is JobState.Completed or JobState.Failed) break;
        }

        Assert.Equal(JobState.Completed, job.State);
        Assert.NotNull(job.OutputAssetId);
        Assert.Null(job.ProviderRequestId);
        var after = snapshot!.Shots.Single(x => x.Id == shot.Id);
        Assert.Equal(shot.Version + 1, after.Version);
        Assert.Equal(ShotStage.Draft, after.Stage);
        Assert.Equal(job.OutputAssetId, after.CurrentAssetId);
        var output = await client.GetByteArrayAsync(job.OutputAssetUrl!);
        Assert.True(output.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));

        var candidates = await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        Assert.Equal(2, candidates!.Count);
        var current = Assert.Single(candidates, item => item.IsCurrent);
        var superseded = Assert.Single(candidates, item => !item.IsCurrent);
        Assert.Equal(after.Version, current.Version);
        Assert.Equal(job.OutputAssetId, current.AssetId);
        Assert.Equal(manifest.Id, current.SourceManifestId);
        Assert.Equal(shot.Version, superseded.Version);
        Assert.NotNull(superseded.SupersededAt);

        var manifests = await client.GetFromJsonAsync<List<GenerationManifestSummary>>($"/api/shots/{shot.Id}/manifests");
        var completed = Assert.Single(manifests!);
        Assert.Equal(ManifestState.Completed, completed.State);
        Assert.False(completed.ProviderCallMade);

        var duplicate = await client.PostAsJsonAsync($"/api/manifests/{manifest.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "local-proof"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var endpointPrepare = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(
                after.Version,
                sketch.Revision,
                GenerationRoute.FastDraft,
                GenerationPurpose.Draft,
                current.AssetId,
                "Move the subject to the doorway while preserving the shot's established look.",
                AllowSketchCompositionFallback: false,
                VideoEndpointRole: "LastFrame",
                VideoEndpointSourceCandidateId: current.Id));
        var endpointManifest = await endpointPrepare.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(HttpStatusCode.OK, endpointPrepare.StatusCode);
        Assert.NotNull(endpointManifest);
        Assert.Contains("clearly readable at full-frame review", endpointManifest.CreativeBrief);
        Assert.Contains("Do not return a near-duplicate", endpointManifest.CreativeBrief);
        Assert.DoesNotContain("BLOCKING OBJECT CONTRACT", endpointManifest.CreativeBrief);
        Assert.DoesNotContain("facing front", endpointManifest.CreativeBrief, StringComparison.OrdinalIgnoreCase);

        var endpointDispatch = await client.PostAsJsonAsync($"/api/manifests/{endpointManifest.Id}/dispatch",
            new DispatchManifestRequest(endpointManifest.ManifestHash, "local-proof"));
        var endpointJob = await endpointDispatch.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, endpointDispatch.StatusCode);
        Assert.NotNull(endpointJob);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            endpointJob = snapshot!.Jobs.Single(x => x.Id == endpointJob.Id);
            if (endpointJob.State is JobState.Completed or JobState.Failed) break;
        }

        Assert.Equal(JobState.Completed, endpointJob.State);
        var endpointShot = snapshot!.Shots.Single(x => x.Id == shot.Id);
        var endpointCandidates = await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        var endpointCurrent = Assert.Single(endpointCandidates!, item => item.IsCurrent);
        Assert.Equal(current.Id, endpointShot.VideoFirstFrameCandidateId);
        Assert.Equal(endpointCurrent.Id, endpointShot.VideoLastFrameCandidateId);
        Assert.Equal(endpointManifest.Id, endpointCurrent.SourceManifestId);
    }

    [Theory]
    [InlineData("manifest-json")]
    [InlineData("creative-brief")]
    [InlineData("authorities-json")]
    [InlineData("constraints-json")]
    public async Task FrozenManifestRejectsHashOrDuplicateColumnTamperingBeforeProvider(string target)
    {
        using var isolatedFactory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "storyboard-studio-manifest-integrity-tests", Guid.NewGuid().ToString("N")),
            deleteDataRoot: true,
            startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-PACKET", "Manifest packet integrity", "Reject every mutable duplicate before dispatch.", 48,
            "35mm equivalent · locked", "Hold.", ["style"], ["No continuity drift."])))
            .Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render the frozen packet exactly.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var record = await db.GenerationManifests.SingleAsync(candidate => candidate.Id == manifest.Id);
            switch (target)
            {
                case "manifest-json": record.ManifestJson += " "; break;
                case "creative-brief": record.CreativeBrief += " MUTATED"; break;
                case "authorities-json": record.AuthoritiesJson = "[]"; break;
                case "constraints-json": record.ConstraintsJson = "[]"; break;
                default: throw new InvalidOperationException($"Unknown integrity target {target}.");
            }
            await db.SaveChangesAsync();
        }

        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<GenerationOrchestrator>()
                .RunAsync(job!.Id, CancellationToken.None);
        }

        Assert.Equal(0, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().ExecutionCount);
        var failed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(candidate => candidate.Id == job!.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("provider was not called", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrozenCompositionHashMismatchFailsTerminalAndCannotBeRetried()
    {
        using var isolatedFactory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "storyboard-studio-composition-integrity-tests", Guid.NewGuid().ToString("N")),
            deleteDataRoot: true,
            startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var composition = await UploadImageAsync(isolatedClient, png, "frozen-composition.png");
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-COMP", "Composition integrity", "Reject a substituted composition.", 48,
            "35mm equivalent · locked", "Hold.", [], []))).Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render the exact composition guide.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(
                shot.Version,
                sketch!.Revision,
                GenerationRoute.FastDraft,
                CompositionAssetId: composition.Id,
                AllowSketchCompositionFallback: false)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.NotNull(manifest!.CompositionAssetId);
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var mutableComposition = await db.Assets.SingleAsync(candidate => candidate.Id == manifest.CompositionAssetId);
            mutableComposition.ContentHash = new string('f', 64);
            await db.SaveChangesAsync();
        }
        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<GenerationOrchestrator>()
                .RunAsync(job!.Id, CancellationToken.None);
        }

        var adapter = isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>();
        Assert.Equal(0, adapter.ExecutionCount);
        var failed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(candidate => candidate.Id == job!.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("composition image row", failed.Error, StringComparison.OrdinalIgnoreCase);
        var retry = await isolatedClient.PostAsync($"/api/jobs/{job.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(0, adapter.ExecutionCount);
    }

    [Fact]
    public async Task FrozenVideoLastFrameBytesAreRehashedBeforeProviderDispatch()
    {
        using var isolatedFactory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "storyboard-studio-last-frame-integrity-tests", Guid.NewGuid().ToString("N")),
            deleteDataRoot: true,
            startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var firstFrame = await UploadImageAsync(isolatedClient, png, "integrity-first.png");
        var lastFrame = await UploadImageAsync(isolatedClient, png.Concat(new byte[] { 0x02 }).ToArray(), "integrity-last.png");
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-LAST", "Last-frame integrity", "Two immutable endpoints.", 120,
            "35mm equivalent · locked", "Measured motion.", [], [], firstFrame.Id)))
            .Content.ReadFromJsonAsync<ShotSummary>();
        var firstCandidate = Assert.Single((await isolatedClient.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot!.Id}/candidates"))!, item => item.IsCurrent);
        var lastCandidateId = Guid.NewGuid();
        await using (var setupScope = isolatedFactory.Services.CreateAsyncScope())
        {
            setupScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(StudioDefaults.ProjectId);
            var db = setupScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            db.CandidateVersions.Add(new CandidateVersionRecord
            {
                Id = lastCandidateId,
                ProjectId = StudioDefaults.ProjectId,
                ShotId = shot.Id,
                Version = shot.Version + 1,
                Stage = ShotStage.Draft.ToString(),
                Approval = ApprovalState.Working.ToString(),
                IsCurrent = false,
                AssetId = lastFrame.Id,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var manifestResponse = await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
            new PrepareVideoManifestRequest(shot.Version, "A measured glance across the room.", true, firstCandidate.Id, lastCandidateId));
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            mutationScope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(StudioDefaults.ProjectId);
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var store = mutationScope.ServiceProvider.GetRequiredService<AssetStore>();
            var row = await db.Assets.SingleAsync(candidate => candidate.Id == lastFrame.Id);
            await File.WriteAllBytesAsync(store.ResolveContentPath(row), "substituted-last-frame"u8.ToArray());
        }
        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<GenerationOrchestrator>()
                .RunAsync(job!.Id, CancellationToken.None);
        }

        Assert.Equal(0, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().ExecutionCount);
        var failed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(candidate => candidate.Id == job!.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("last-frame image bytes", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrozenAuthorityProjectBindingRejectsCrossProjectAssetSubstitution()
    {
        using var isolatedFactory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "storyboard-studio-authority-integrity-tests", Guid.NewGuid().ToString("N")),
            deleteDataRoot: true,
            startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var image = await UploadImageAsync(isolatedClient, png, "authority-project.png");
        var authority = await (await isolatedClient.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Integrity courier", "Character", "The exact courier identity.", "Keep the amber eyes.", "#725d88", image.Id)))
            .Content.ReadFromJsonAsync<ReferenceSummary>();
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-AUTH", "Authority integrity", "The courier waits by the hatch.", 48,
            "50mm equivalent · locked", "Hold.", [authority!.Id], []))).Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render the exact authority.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var row = await db.Assets.SingleAsync(candidate => candidate.Id == image.Id);
            row.ProjectId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }
        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<GenerationOrchestrator>()
                .RunAsync(job!.Id, CancellationToken.None);
        }

        Assert.Equal(0, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().ExecutionCount);
        var failed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(candidate => candidate.Id == job!.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("authority Integrity courier", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prepared project", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrozenAuthorityResolutionIsScopedWhenTwoProjectsShareSlugAndVersion()
    {
        using var isolatedFactory = new StudioApiFactory(
            Path.Combine(Path.GetTempPath(), "storyboard-studio-authority-scope-tests", Guid.NewGuid().ToString("N")),
            deleteDataRoot: true,
            startGenerationWorker: false);
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var image = await UploadImageAsync(isolatedClient, png, "same-slug-authority.png");
        var authority = await (await isolatedClient.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Shared slug witness", "Character", "The project-local witness.", "Keep the blue scarf.", "#54768a", image.Id)))
            .Content.ReadFromJsonAsync<ReferenceSummary>();
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-SCOPE", "Authority scope", "Resolve only this project's witness.", 48,
            "50mm equivalent · locked", "Hold.", [authority!.Id], []))).Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render the project-local authority.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();

        await using (var mutationScope = isolatedFactory.Services.CreateAsyncScope())
        {
            var db = mutationScope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var original = await db.ReferenceVersions.SingleAsync(candidate => candidate.ReferenceId == authority.Id && candidate.Version == authority.Version);
            db.ReferenceVersions.Add(new ReferenceVersionRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                ReferenceId = original.ReferenceId,
                Version = original.Version,
                Description = "A different project's colliding authority.",
                LockedConstraint = "Never select this row.",
                ImageAssetId = null,
                ContentHash = new string('a', 64),
                RatifiedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await using (var executionScope = isolatedFactory.Services.CreateAsyncScope())
        {
            await executionScope.ServiceProvider.GetRequiredService<GenerationOrchestrator>()
                .RunAsync(job!.Id, CancellationToken.None);
        }

        var adapter = isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>();
        Assert.Equal(1, adapter.ExecutionCount);
        var completed = (await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(candidate => candidate.Id == job!.Id);
        Assert.Equal(JobState.Completed, completed.State);
        Assert.Contains("project-local witness", adapter.LastContext!.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("different project's", adapter.LastContext.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerationIsRejectedBeforeProviderWhenAssetStorageIsUnavailable()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "storyboard-studio-readiness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var blockedAssetRoot = Path.Combine(dataRoot, "asset-root-blocked-by-a-file");
        await File.WriteAllTextAsync(blockedAssetRoot, "This file deliberately occupies the configured asset directory.");
        using var isolatedFactory = new StudioApiFactory(dataRoot, deleteDataRoot: true, blockedAssetRoot);
        using var isolatedClient = isolatedFactory.CreateClient();

        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-STORAGE", "Storage readiness proof", "Never call the provider when output cannot be saved.", 48,
            "35mm equivalent · locked", "Hold.", ["style"], []))).Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render only when durable output storage is writable.", ExampleSketch())))
            .Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var manifest = await (await isolatedClient.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft)))
            .Content.ReadFromJsonAsync<GenerationManifestSummary>();

        var dispatch = await isolatedClient.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch",
            new DispatchManifestRequest(manifest.ManifestHash, "serial-probe"));
        var error = await dispatch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, dispatch.StatusCode);
        Assert.Contains("asset-root", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file occupies", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().ExecutionCount);

        var snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.DoesNotContain(snapshot!.Jobs, job => job.ManifestId == manifest.Id);
        var manifests = await isolatedClient.GetFromJsonAsync<List<GenerationManifestSummary>>($"/api/shots/{shot.Id}/manifests");
        Assert.Equal(ManifestState.Prepared, Assert.Single(manifests!).State);
    }

    [Fact]
    public async Task TwoRequestsFromOneShotReturnAsReviewableSiblingCandidates()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var shot = await (await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-SIB", "Sibling proof", "Two valid directions begin from one frame.", 48,
            "35mm equivalent · locked", "Hold.", ["style"], []))).Content.ReadFromJsonAsync<ShotSummary>();
        var sketch = await (await isolatedClient.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render sibling variants.", ExampleSketch()))).Content.ReadFromJsonAsync<SketchDocumentSummary>();

        async Task<GenerationManifestSummary> Prepare(string direction) => (await (await isolatedClient.PostAsJsonAsync(
            $"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft,
                CreativeBriefOverride: direction))).Content.ReadFromJsonAsync<GenerationManifestSummary>())!;
        var left = await Prepare("Place the subject on frame left.");
        var right = await Prepare("Place the subject on frame right.");
        var firstResponse = await isolatedClient.PostAsJsonAsync($"/api/manifests/{left.Id}/dispatch",
            new DispatchManifestRequest(left.ManifestHash, "serial-probe"));
        var secondResponse = await isolatedClient.PostAsJsonAsync($"/api/manifests/{right.Id}/dispatch",
            new DispatchManifestRequest(right.ManifestHash, "serial-probe"));
        var first = await firstResponse.Content.ReadFromJsonAsync<JobSummary>();
        var second = await secondResponse.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);

        StudioSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(100);
            snapshot = await isolatedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            var related = snapshot!.Jobs.Where(job => job.Id == first!.Id || job.Id == second!.Id).ToArray();
            if (related.Length == 2 && related.All(job => job.State == JobState.Completed)) break;
        }

        var candidates = await isolatedClient.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        Assert.Contains(candidates!, item => item.SourceManifestId == left.Id);
        Assert.Contains(candidates!, item => item.SourceManifestId == right.Id);
        Assert.Single(candidates!, item => item.IsCurrent);
        Assert.Equal("Variant ready for comparison", snapshot!.Jobs.Single(job => job.Id == second!.Id).Phase);
        Assert.Equal(1, isolatedFactory.Services.GetRequiredService<SerialProbeGenerationAdapter>().MaximumConcurrency);
    }

    [Fact]
    public async Task FailedGenerationCanBeRetriedWithoutRewritingItsHistory()
    {
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-RETRY", "Retry proof", "A frozen packet that needs a second attempt.", 48,
            "35mm equivalent · locked", "Hold.", ["style"], []));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        var saved = await client.PutAsJsonAsync($"/api/shots/{shot!.Id}/sketch",
            new SaveSketchRequest(0, "Render the retry proof.", ExampleSketch()));
        var sketch = await saved.Content.ReadFromJsonAsync<SketchDocumentSummary>();
        var prepare = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare",
            new PrepareGenerationManifestRequest(shot.Version, sketch!.Revision, GenerationRoute.FastDraft));
        var manifest = await prepare.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var failedId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var record = await db.GenerationManifests.SingleAsync(item => item.Id == manifest!.Id);
            record.State = ManifestState.Failed.ToString();
            db.Jobs.Add(new JobRecord
            {
                Id = failedId,
                ProjectId = StudioDefaults.ProjectId,
                ShotId = shot.Id,
                ShotCode = shot.Code,
                Kind = "Draft frame",
                State = JobState.Failed.ToString(),
                Progress = 100,
                Phase = "Generation failed",
                Backend = "Local composition proof",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                Error = "Synthetic first-attempt failure.",
                ManifestId = manifest!.Id,
                AdapterId = "local-proof",
                Attempt = 1
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/jobs/{failedId}/retry", null);
        var retry = await response.Content.ReadFromJsonAsync<JobSummary>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, retry!.Attempt);
        Assert.Equal(failedId, retry.RetryOfJobId);
        Assert.Equal(manifest!.Id, retry.ManifestId);

        StudioSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            retry = snapshot!.Jobs.Single(item => item.Id == retry.Id);
            if (retry.State is JobState.Completed or JobState.Failed) break;
        }

        Assert.Equal(JobState.Completed, retry.State);
        var original = snapshot!.Jobs.Single(item => item.Id == failedId);
        Assert.Equal(JobState.Failed, original.State);
        Assert.Equal("Synthetic first-attempt failure.", original.Error);
    }

    [Fact]
    public async Task RegeneratingARatifiedDraftSwapsInANewWorkingImageAndPreservesTheAuthorityVersion()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "ratified-source.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var uploadResponse = await client.SendAsync(upload);
        var source = await uploadResponse.Content.ReadFromJsonAsync<AssetSummary>();
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-RAT-REGEN", "Ratified regeneration", "Replace a face from an exact authority note.", 48,
            "50mm equivalent · close-up · locked", "Hold the pose.", ["style"], [], source!.Id));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        var ratify = await client.PostAsJsonAsync($"/api/shots/{shot!.Id}/ratify", new RatifyRequest(shot.Version, "Approve source before reference edit"));
        Assert.Equal(HttpStatusCode.OK, ratify.StatusCode);

        var prepare = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/manifests/prepare", new PrepareGenerationManifestRequest(
            shot.Version, 0, GenerationRoute.FastDraft, GenerationPurpose.Draft, source.Id,
            "Apply the exact reference note.\n\nCURRENT FRAME EDIT: preserve everything else.", AllowSketchCompositionFallback: false));
        var manifest = await prepare.Content.ReadFromJsonAsync<GenerationManifestSummary>();
        var dispatch = await client.PostAsJsonAsync($"/api/manifests/{manifest!.Id}/dispatch", new DispatchManifestRequest(manifest.ManifestHash, "local-proof"));
        var job = await dispatch.Content.ReadFromJsonAsync<JobSummary>();
        var jobId = job!.Id;
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);

        StudioSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(100);
            snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            job = snapshot!.Jobs.Single(item => item.Id == jobId);
            if (job.State is JobState.Completed or JobState.Failed) break;
        }

        Assert.Equal(JobState.Completed, job!.State);
        var current = snapshot!.Shots.Single(item => item.Id == shot.Id);
        Assert.Equal(shot.Version + 1, current.Version);
        Assert.Equal(ApprovalState.Working, current.Approval);
        Assert.Equal(job.OutputAssetId, current.CurrentAssetId);
        var candidates = await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        var working = Assert.Single(candidates!, item => item.IsCurrent);
        Assert.Equal(current.Version, working.Version);
        Assert.Equal(manifest.Id, working.SourceManifestId);
        var authorities = await client.GetFromJsonAsync<List<ShotVersionSummary>>($"/api/shots/{shot.Id}/versions");
        var preserved = Assert.Single(authorities!);
        Assert.Equal(shot.Version, preserved.Version);
        Assert.Equal(ApprovalState.Ratified, preserved.Approval);
        Assert.Equal(source.Id, preserved.AssetId);
    }

    [Fact]
    public async Task EarlierImageCandidateIsCopiedForwardWithoutRewritingHistory()
    {
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-077", "Revision choice", "Choose between two valid drafts.", 48,
            "50mm · profile", "Hold.", ["style"], ["Preserve the profile"]));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.NotNull(shot);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "revision.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var asset = await (await client.SendAsync(upload)).Content.ReadFromJsonAsync<AssetSummary>();
        Assert.NotNull(asset);

        Guid earlierId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var record = await db.Shots.SingleAsync(x => x.Id == shot.Id);
            var earlier = await db.CandidateVersions.SingleAsync(x => x.ShotId == shot.Id && x.IsCurrent);
            earlier.IsCurrent = false; earlier.AssetId = asset.Id; earlier.SupersededAt = DateTimeOffset.UtcNow;
            earlierId = earlier.Id;
            record.Version = 2; record.Stage = ShotStage.Draft.ToString(); record.CurrentAssetId = asset.Id; record.UpdatedAt = DateTimeOffset.UtcNow;
            db.CandidateVersions.Add(new CandidateVersionRecord { Id = Guid.NewGuid(), ShotId = shot.Id, Version = 2, Stage = ShotStage.Draft.ToString(), Approval = ApprovalState.Working.ToString(), IsCurrent = true, AssetId = asset.Id, CreatedAt = record.UpdatedAt });
            await db.SaveChangesAsync();
        }

        var choose = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/candidates/{earlierId}/copy-forward", new PromoteCandidateRequest(2));
        var promoted = await choose.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.Equal(HttpStatusCode.OK, choose.StatusCode);
        Assert.Equal(3, promoted!.Version);
        Assert.Equal(asset.Id, promoted.CurrentAssetId);
        Assert.Equal(ApprovalState.Working, promoted.Approval);

        var candidates = await client.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        Assert.Equal(3, candidates!.Count);
        Assert.Equal(3, Assert.Single(candidates, item => item.IsCurrent).Version);
        Assert.Contains(candidates, item => item.Id == earlierId && item.Version == 1 && !item.IsCurrent);
        Assert.Contains(candidates, item => item.Version == 2 && !item.IsCurrent);

        var stale = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/candidates/{earlierId}/copy-forward", new PromoteCandidateRequest(2));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task ImageAssetsAreValidatedContentAddressedAndDeduplicated()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        var missingGuard = await client.PostAsync("/api/assets/images", CreateImageUpload(png, "pixel.png"));
        Assert.Equal(HttpStatusCode.Forbidden, missingGuard.StatusCode);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "pixel.png") };
        firstRequest.Headers.Add("X-Storyboard-Studio", "1");
        var firstResponse = await client.SendAsync(firstRequest);
        var first = await firstResponse.Content.ReadFromJsonAsync<AssetSummary>();

        using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "renamed.png") };
        duplicateRequest.Headers.Add("X-Storyboard-Studio", "1");
        var duplicateResponse = await client.SendAsync(duplicateRequest);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<AssetSummary>();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.NotNull(first);
        Assert.Equal(first.Id, duplicate!.Id);
        Assert.Equal("image/png", first.MimeType);
        Assert.Equal(1, first.Width);
        Assert.Equal(1, first.Height);
        Assert.Equal(64, first.ContentHash.Length);

        var content = await client.GetByteArrayAsync(first.ContentUrl);
        Assert.Equal(png, content);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<AssetStore>();
        var record = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == first.Id);
        var expectedAssetRoot = Path.GetFullPath(Path.Combine(factory.DataRoot, "assets")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedAssetRoot, store.ResolveContentPath(record), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImageAssetUploadRejectsExtensionSpoofing()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images")
        {
            Content = CreateImageUpload("not an image"u8.ToArray(), "fake.png")
        };
        request.Headers.Add("X-Storyboard-Studio", "1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("valid PNG and JPEG", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthoritiesAreServerBackedAndNewVersionsAreImmutable()
    {
        var create = await client.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Cloud skiff", "Prop", "A narrow brass-hulled courier skiff.", "Three fins; no exposed propeller.", "#8a674d"));
        var authority = await create.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(authority);
        Assert.Equal(1, authority.Version);

        var versionResponse = await client.PostAsJsonAsync($"/api/references/{authority.Id}/versions", new CreateReferenceVersionRequest(
            authority.Version, "A narrow oxidized-brass courier skiff.", "Three fins; no exposed propeller; red pennant.", null));
        var updated = await versionResponse.Content.ReadFromJsonAsync<ReferenceSummary>();
        var versions = await client.GetFromJsonAsync<List<ReferenceVersionSummary>>($"/api/references/{authority.Id}/versions");

        Assert.Equal(2, updated!.Version);
        Assert.Equal(2, versions!.Count);
        Assert.Equal(2, versions[0].Version);
        Assert.NotEqual(versions[0].ContentHash, versions[1].ContentHash);

        var stale = await client.PostAsJsonAsync($"/api/references/{authority.Id}/versions", new CreateReferenceVersionRequest(
            1, "stale", "stale", null));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task AuthorityStackNeverReusesVersionNumbersAndSupportsCopyForward()
    {
        var create = await client.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Version clock relic", "Prop", "A test relic in its original state.", "The triangular aperture never changes.", "#735f91"));
        var v1 = await create.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var createV2 = await client.PostAsJsonAsync($"/api/references/{v1!.Id}/versions", new CreateReferenceVersionRequest(
            1, "The same relic with a weathered shell.", "The triangular aperture never changes.", null));
        var v2 = await createV2.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(2, v2!.Version);

        var deleteV2 = await client.DeleteAsync($"/api/references/{v1.Id}/versions/2");
        var rolledBack = await deleteV2.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(HttpStatusCode.OK, deleteV2.StatusCode);
        Assert.Equal(1, rolledBack!.Version);

        var createAfterDelete = await client.PostAsJsonAsync($"/api/references/{v1.Id}/versions", new CreateReferenceVersionRequest(
            1, "The relic now carries a matte graphite shell.", "The triangular aperture never changes.", null));
        var v3 = await createAfterDelete.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(3, v3!.Version);

        var promoteV1 = await client.PostAsync($"/api/references/{v1.Id}/versions/1/promote", null);
        var v4 = await promoteV1.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(HttpStatusCode.OK, promoteV1.StatusCode);
        Assert.Equal(4, v4!.Version);
        Assert.Equal(v1.Description, v4.Description);

        var update = await client.PutAsJsonAsync($"/api/references/{v1.Id}", new UpdateReferenceRequest(
            "Version clock artifact", "prop", "#AA7733"));
        var renamed = await update.Content.ReadFromJsonAsync<ReferenceSummary>();
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Version clock artifact", renamed!.Name);
        Assert.Equal("Prop", renamed.Category);
        Assert.Equal("#aa7733", renamed.Accent);

        var history = await client.GetFromJsonAsync<List<ReferenceVersionSummary>>($"/api/references/{v1.Id}/versions");
        Assert.Equal([4, 3, 1], history!.Select(item => item.Version).ToArray());
    }

    [Fact]
    public async Task AuthorityDeletionIsBlockedWhileAShotCitesIt()
    {
        var createAuthority = await client.PostAsJsonAsync("/api/references", new CreateReferenceRequest(
            "Deletion guard emblem", "Prop", "A small enamel emblem used by the guard test.", "Exactly one white bar.", "#516d81"));
        var authority = await createAuthority.Content.ReadFromJsonAsync<ReferenceSummary>();
        var createShot = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-GUARD", "Authority deletion guard", "A test frame that cites the emblem.", 24,
            "50mm · locked", "Hold.", [authority!.Id], ["Exactly one white bar"]));
        Assert.Equal(HttpStatusCode.OK, createShot.StatusCode);

        var delete = await client.DeleteAsync($"/api/references/{authority.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("SH-GUARD", await delete.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ShotSlotsCanBeCreatedAndUpdatedWithAuthorityValidation()
    {
        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        var create = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-070", "Threshold answer", "Ennix answers from the threshold.", 72,
            "50mm · locked", "One breath, then the answer.", ["ennix", "aerie", "style"], ["Green eyes if visible"]));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(shot);
        Assert.Equal(snapshot!.Shots.Max(x => x.SortOrder) + 1, shot.SortOrder);

        var update = await client.PutAsJsonAsync($"/api/shots/{shot.Id}", new UpdateShotRequest(
            shot.UpdatedAt, "Threshold reply", shot.Description, 84, shot.Camera, shot.Action,
            shot.ReferenceIds, [.. shot.Constraints, "Sky bridge remains world canon"]));
        var updated = await update.Content.ReadFromJsonAsync<ShotSummary>();
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Threshold reply", updated!.Title);
        Assert.Equal(84, updated.DurationFrames);

        var stale = await client.PutAsJsonAsync($"/api/shots/{shot.Id}", new UpdateShotRequest(
            shot.UpdatedAt, "Stale update", shot.Description, 84, shot.Camera, shot.Action, shot.ReferenceIds, shot.Constraints));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var invalid = await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-071", "Unknown canon", "Invalid reference test.", 24, "35mm", "Hold.", ["not-a-real-authority"], []));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task DroppedImageCreatesAShotWithARealCurrentDraftCandidate()
    {
        using var isolatedFactory = new StudioApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = CreateImageUpload(png, "dropped-board-frame.png") };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var uploadResponse = await isolatedClient.SendAsync(upload);
        var asset = await uploadResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        var create = await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-DROP", "Dropped frame", "An imported composition becomes the opening draft.", 72,
            "35mm equivalent · medium wide · locked", "Hold the imported blocking.", [], [], asset!.Id));
        var shot = await create.Content.ReadFromJsonAsync<ShotSummary>();

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal(ShotStage.Draft, shot!.Stage);
        Assert.Equal(asset.Id, shot.CurrentAssetId);
        var candidates = await isolatedClient.GetFromJsonAsync<List<CandidateVersionSummary>>($"/api/shots/{shot.Id}/candidates");
        var current = Assert.Single(candidates!, item => item.IsCurrent);
        Assert.Equal(ShotStage.Draft, current.Stage);
        Assert.Equal(asset.Id, current.AssetId);

        var invalid = await isolatedClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
            "SH-BAD-DROP", "Missing dropped frame", "The image ID is invalid.", 24,
            "35mm equivalent · locked", "Hold.", [], [], Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static MultipartFormDataContent CreateImageUpload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(file, "file", fileName);
        return content;
    }

    private static async Task<AssetSummary> UploadImageAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images")
        {
            Content = CreateImageUpload(bytes, fileName)
        };
        upload.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
    }

    private static MultipartFormDataContent CreateMediaUpload(byte[] bytes, string fileName, string mimeType)
    {
        var content = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType); content.Add(file, "file", fileName); return content;
    }

    private static SketchContent ExampleSketch() => new(
        [new SketchStroke("stroke-1", [new SketchPoint(.2, .3, .4), new SketchPoint(.6, .7, .8)], "#302b27", 4)],
        [new SketchLabel("label-1", .45, .2, "eyeline")]);

    private sealed class RemoteAddressStartupFilter(IPAddress remoteAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continueRequest) =>
            {
                context.Connection.RemoteIpAddress = remoteAddress;
                await continueRequest();
            });
            next(application);
        };
    }
}
