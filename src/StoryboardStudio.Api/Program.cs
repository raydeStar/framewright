using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

var packagedRoot = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = packagedRoot });

// The content root above is chosen for static files, and it is not always the
// directory holding appsettings.json — launching from an unexpected working
// directory produced a config-less service where every integration silently
// reported "submission is off" with no error anywhere. Load the settings that
// ship beside the binary as a base layer so configuration never depends on cwd.
// Inserted at the front so environment variables and command line still win.
if (!string.Equals(Path.TrimEndingDirectorySeparator(builder.Environment.ContentRootPath),
                   Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
{
    var beside = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
        .Build();
    builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.ChainedConfigurationSource { Configuration = beside });
}
// One workstation's own answers: where its Blender lives, which studio tree
// holds the generation weights, and whether its owner has commissioned a route.
// None of that belongs in a tracked file — the paths name somebody's machine,
// and a commissioned route committed to a repository would arrive switched on
// for everyone who cloned it. Optional, and last, so it wins over the shipped
// defaults while environment variables and the command line still win over it.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5179");

var dataRoot = StudioPaths.ResolveDataRoot(builder.Configuration, builder.Environment);
Directory.CreateDirectory(dataRoot);
var databasePath = Path.Combine(dataRoot, "storyboard-studio.db");
var legacyDatabasePath = Path.Combine(dataRoot, "storyboard-studio.demo.db");
if (builder.Configuration.GetConnectionString("Studio") is null && !File.Exists(databasePath) && File.Exists(legacyDatabasePath))
{
    File.Copy(legacyDatabasePath, databasePath);
}
var connectionString = builder.Configuration.GetConnectionString("Studio")
    ?? $"Data Source={databasePath};Pooling=False";

// The active project is process state and the scope is per-unit-of-work, so both
// must be registered before the context that reads them in its query filters.
builder.Services.AddSingleton<ActiveProjectRegistry>();
builder.Services.AddScoped<IProjectScope, ProjectScope>();
builder.Services.AddDbContext<StudioDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<StudioRepository>();
builder.Services.AddScoped<AssetStore>();
builder.Services.AddSingleton<IVideoMediaProbe, FfprobeVideoMediaProbe>();
builder.Services.AddScoped<AuthorityLibraryService>();
builder.Services.AddScoped<ContinuityService>();
builder.Services.AddScoped<WebMcpStoryboardService>();
builder.Services.AddScoped<SceneService>();
builder.Services.AddScoped<SceneDirectionService>();
builder.Services.AddScoped<SceneBlockoutService>();
builder.Services.AddScoped<SceneMotionService>();
builder.Services.AddSingleton<ICompilerGateway, CompilerGateway>();
builder.Services.AddScoped<ModelGenerationService>();
builder.Services.AddScoped<VisualConsistencyService>();
builder.Services.AddScoped<TimelineService>();
builder.Services.AddScoped<QwenVoiceService>();
builder.Services.AddScoped<VoiceSynthesisService>();
builder.Services.AddScoped<AudioMasteringService>();
builder.Services.AddScoped<ProductionExportService>();
builder.Services.AddSingleton<IProviderCredentialStore, ProviderCredentialStore>();
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<ICodexRuntime, CodexRuntime>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<DiagnosticBundleService>();
builder.Services.AddHostedService<ScheduledBackupWorker>();
builder.Services.AddSingleton<GenerationJobSignal>();
builder.Services.AddHostedService<GenerationJobWorker>();
builder.Services.AddScoped<GenerationJobLeaseService>();
builder.Services.AddSingleton<BuildIdentityService>();
builder.Services.AddScoped<RuntimeReadinessService>();
builder.Services.AddScoped<GenerationOrchestrator>();
builder.Services.AddScoped<GenerationPreflightService>();
builder.Services.AddScoped<MusicGenerationService>();
builder.Services.AddScoped<MusicCompositionService>();
builder.Services.AddScoped<AudioGenerationJobService>();
builder.Services.AddScoped<AssetImageGenerationService>();
builder.Services.AddSingleton(sp => new WorkflowLibrary(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<IGenerationAdapter, LocalProofGenerationAdapter>();
builder.Services.AddSingleton<IGenerationAdapter, ComfyUiGenerationAdapter>();
builder.Services.AddSingleton<IGenerationAdapter, ComfyUiVideoGenerationAdapter>();
builder.Services.AddSingleton<IGenerationAdapter, OpenAiImageGenerationAdapter>();
builder.Services.AddSingleton<IGenerationAdapter, CodexImageGenerationAdapter>();
builder.Services.AddHttpClient("integration-probe", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("comfyui-generation", client => client.Timeout = TimeSpan.FromMinutes(12));
builder.Services.AddHttpClient("openai-generation", client => client.Timeout = TimeSpan.FromMinutes(8));
builder.Services.AddHttpClient("qwen-voice-worker-health", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("qwen-voice-worker", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient("yue2-health", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("yue2-generation", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<IntegrationDiscoveryService>();
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FramewrightMcpTools>();
builder.Services.AddProblemDetails();
// FormOptions defaults to roughly 128 MB and Kestrel defaults to roughly
// 30 MB. Media imports deliberately support up to 500 MB, so the form parser
// and request feature must agree with the app's own streaming limits.
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = AssetStore.MaxMediaRequestBytes);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    if (context.Request.Path.StartsWithSegments("/mcp") &&
        !trustedLocal)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Framewright MCP is workstation-local." });
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    var requestSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (requestSize is { IsReadOnly: false } && HttpMethods.IsPost(context.Request.Method))
    {
        if (context.Request.Path.Equals("/api/assets/images", StringComparison.OrdinalIgnoreCase))
            requestSize.MaxRequestBodySize = AssetStore.MaxImageRequestBytes;
        else if (context.Request.Path.StartsWithSegments("/api/assets/media", StringComparison.OrdinalIgnoreCase))
            requestSize.MaxRequestBodySize = AssetStore.MaxMediaRequestBytes;
        else if (context.Request.Path.Equals("/api/assets/models", StringComparison.OrdinalIgnoreCase))
            requestSize.MaxRequestBodySize = AssetStore.MaxModelRequestBytes;
    }
    var pairing = context.RequestServices.GetRequiredService<PairingService>();
    var allowLan = app.Configuration.GetValue("Studio:AllowLan", false);
    var remoteAddress = context.Connection.RemoteIpAddress;
    var loopback = IsTrustedLocalCaller(remoteAddress, app.Configuration);
    if (!allowLan && !loopback)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Framewright is loopback-only until tablet pairing is explicitly enabled." });
        return;
    }
    var pairingPath = context.Request.Path.StartsWithSegments("/api/pairing");
    if (allowLan && !loopback && context.Request.Path.StartsWithSegments("/api") && !pairingPath && !pairing.Validate(context.Request.Cookies[PairingService.CookieName]))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Pair this tablet with the workstation before opening production data." });
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=(), tools=(self)");
    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' blob:");
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/health", (BuildIdentityService build) => Results.Ok(new { status = "live", mode = "local-first", time = DateTimeOffset.UtcNow, build = build.Current }));
app.MapGet("/health/live", (BuildIdentityService build) => Results.Ok(new { status = "live", time = DateTimeOffset.UtcNow, build = build.Current }));
app.MapGet("/health/ready", async Task<IResult> (
    RuntimeReadinessService readiness,
    CancellationToken cancellationToken) =>
{
    var status = await readiness.InspectAsync(cancellationToken);
    return status.Status == "NotReady"
        ? Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(status);
});
app.MapGet("/api/runtime/status", async (
    RuntimeReadinessService readiness,
    CancellationToken cancellationToken) => Results.Ok(await readiness.InspectAsync(cancellationToken)));
app.MapGet("/api/pairing/status", (HttpContext context, PairingService pairing) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    return Results.Ok(pairing.Status(trustedLocal, trustedLocal || pairing.Validate(context.Request.Cookies[PairingService.CookieName])));
});
app.MapPost("/api/pairing/start", (HttpContext context, PairingService pairing) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    return trustedLocal ? Results.Ok(pairing.Rotate(true)) : Results.StatusCode(StatusCodes.Status403Forbidden);
});
app.MapPost("/api/pairing/revoke", (HttpContext context, PairingService pairing) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    return trustedLocal ? Results.Ok(pairing.RevokeAll(true)) : Results.StatusCode(StatusCodes.Status403Forbidden);
});
app.MapPost("/api/pairing/claim", (PairingClaimRequest request, HttpContext context, PairingService pairing) =>
{
    var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"; var token = pairing.Claim(request.Code, clientKey);
    if (token is null) return Results.BadRequest(new { error = "The pairing code is invalid, expired, or temporarily rate-limited." });
    context.Response.Cookies.Append(PairingService.CookieName, token, new CookieOptions { HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(12), IsEssential = true });
    return Results.Ok(pairing.Status(false, true));
});
app.MapGet("/api/studio", async (StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetSnapshotAsync(cancellationToken)));
app.MapPut("/api/project", async Task<IResult> (UpdateProjectRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.UpdateProjectAsync(request, cancellationToken)));

// Project selection is server-side state rather than a header or route prefix, so
// the snapshot shape stays intact and the whole front end keeps working unchanged.
app.MapGet("/api/projects", async (StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.ListProjectsAsync(cancellationToken)));
app.MapPost("/api/projects", async Task<IResult> (CreateProjectRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.CreateProjectAsync(request, cancellationToken)));
app.MapPost("/api/projects/{projectId:guid}/activate", async Task<IResult> (
    Guid projectId, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.ActivateProjectAsync(projectId, cancellationToken)));
// Refused while active, and refused for the last project. Ratified evidence needs
// the same explicit acceptance a shot deletion does.
app.MapDelete("/api/projects/{projectId:guid}", async Task<IResult> (
    Guid projectId, bool? acceptRatifiedLoss, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.DeleteProjectAsync(projectId, acceptRatifiedLoss ?? false, cancellationToken)));
app.MapPut("/api/shots/order", async Task<IResult> (ReorderShotsRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.ReorderShotsAsync(request, cancellationToken)));
app.MapGet("/api/shots/{shotId:guid}/continuity", async Task<IResult> (Guid shotId, ContinuityService continuity, CancellationToken cancellationToken) =>
{
    var report = await continuity.EvaluateAsync(shotId, cancellationToken);
    return report is null ? Results.NotFound() : Results.Ok(report);
});
app.MapGet("/api/integrations", async (IntegrationDiscoveryService discovery, CancellationToken cancellationToken)
    => Results.Ok(await discovery.InspectAsync(cancellationToken)));
app.MapGet("/api/credentials/openai", (HttpContext context, IProviderCredentialStore credentials) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    return trustedLocal ? Results.Ok(credentials.GetOpenAiStatus()) : Results.StatusCode(StatusCodes.Status403Forbidden);
});
app.MapPost("/api/credentials/openai", (SaveCredentialRequest request, HttpContext context, IProviderCredentialStore credentials) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    if (!trustedLocal || context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { credentials.SaveOpenAiApiKey(request.ApiKey); return Results.Ok(credentials.GetOpenAiStatus()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (PlatformNotSupportedException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/credentials/openai", (HttpContext context, IProviderCredentialStore credentials) =>
{
    var trustedLocal = IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration);
    if (!trustedLocal || context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    credentials.DeleteOpenAiApiKey();
    return Results.Ok(credentials.GetOpenAiStatus());
});
app.MapGet("/api/generation/adapters", (GenerationOrchestrator orchestrator) => Results.Ok(orchestrator.ListAdapters()));
// A failure the artist has seen stays seen. Dismissal used to live in the page,
// so every reload brought back every failure a project had ever had, for ever.
app.MapPost("/api/jobs/{jobId:guid}/acknowledge", async Task<IResult> (
    Guid jobId, StudioDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
{
    var job = await db.Jobs.SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken);
    if (job is null) return Results.NotFound();
    // Only a terminal state can be acknowledged: work still running is not
    // something to wave away, and hiding it would hide a job still holding a
    // lease.
    if (job.State != JobState.Failed.ToString() && job.State != JobState.Cancelled.ToString())
        return Results.BadRequest(new { error = "Only a failed or cancelled job can be acknowledged." });
    job.AcknowledgedAt ??= clock.GetUtcNow();
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/jobs/{jobId:guid}", async Task<IResult> (
    Guid jobId, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.JobAsync(jobId, cancellationToken)));

app.MapPost("/api/jobs/{jobId:guid}/retry", async Task<IResult> (
    Guid jobId, GenerationOrchestrator orchestrator, AssetImageGenerationService assetGeneration,
    AudioGenerationJobService audioGeneration, StudioDbContext db, GenerationJobSignal queue,
    CancellationToken cancellationToken) =>
{
    var workType = await db.Jobs.Where(x => x.Id == jobId).Select(x => x.WorkType).SingleOrDefaultAsync(cancellationToken);
    var result = string.Equals(workType, "Asset", StringComparison.Ordinal)
        ? await assetGeneration.RetryAsync(jobId, cancellationToken)
        : string.Equals(workType, AudioGenerationJobService.VoiceWorkType, StringComparison.Ordinal)
            || string.Equals(workType, AudioGenerationJobService.MusicWorkType, StringComparison.Ordinal)
            || string.Equals(workType, AudioGenerationJobService.VoiceDesignWorkType, StringComparison.Ordinal)
        ? await audioGeneration.RetryAsync(jobId, cancellationToken)
        : await orchestrator.RetryAsync(jobId, cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});
app.MapMcp("/mcp");
app.MapGet("/api/workflows", (WorkflowLibrary library) => Results.Ok(library.ValidateAll().Select(workflow => new
{
    workflow.Id,
    workflow.Name,
    workflow.Kind,
    workflow.Summary,
    workflow.Valid,
    workflow.Problems,
    capabilities = workflow.Capabilities ?? [],
    workflow.MaxReferenceImages,
    workflow.Outputs
})));
app.MapGet("/api/manifests/{manifestId:guid}/preflight", async Task<IResult> (
    Guid manifestId, string adapterId, GenerationPreflightService preflight, CancellationToken cancellationToken) =>
    ToHttpResult(await preflight.InspectAsync(manifestId, adapterId, cancellationToken)));
app.MapGet("/api/shots/{shotId:guid}/draft-workflow", async Task<IResult> (
    Guid shotId, bool? useSketch, bool? useCurrentFrame, StudioRepository repository, WorkflowLibrary library,
    IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var snapshot = await repository.GetSnapshotAsync(cancellationToken);
    var shot = snapshot.Shots.SingleOrDefault(item => item.Id == shotId);
    if (shot is null) return Results.NotFound();
    var sketch = await repository.GetSketchAsync(shotId, cancellationToken);
    var usesComposition = useSketch ?? sketch?.CompositionAssetId is not null;
    var hasVisualReferences = shot.ReferenceIds.Any(referenceId =>
        snapshot.References.Any(reference => reference.Id == referenceId && reference.ImageAssetId is not null));
    var configKey = useCurrentFrame == true
        ? "Integrations:ComfyUi:ExternalCurrentFrameWorkflowPath"
        : usesComposition
        ? "Integrations:ComfyUi:ExternalWorkflowPath"
        : hasVisualReferences
            ? "Integrations:ComfyUi:ExternalReferenceWorkflowPath"
            : "Integrations:ComfyUi:ExternalTextWorkflowPath";
    var configured = configuration[configKey] ?? string.Empty;
    var configuredFile = Path.GetFileName(configured.Replace('\\', Path.DirectorySeparatorChar));
    var entry = library.LoadIndex().FirstOrDefault(item =>
        item.ConfigKey.Equals(configKey, StringComparison.OrdinalIgnoreCase)
        && item.File.Equals(configuredFile, StringComparison.OrdinalIgnoreCase));
    var fallbackName = string.IsNullOrWhiteSpace(configuredFile)
        ? "Workflow not configured"
        : Path.GetFileNameWithoutExtension(configuredFile).Replace('-', ' ');
    return Results.Ok(new DraftWorkflowSummary(
        entry?.Id ?? fallbackName.Replace(' ', '-').ToLowerInvariant(),
        entry?.Name ?? fallbackName,
        useCurrentFrame == true ? "Current frame edit" : usesComposition ? "Sketch guided" : hasVisualReferences ? "Authority guided" : "Text to image",
        entry?.Summary ?? "Configured ComfyUI workflow.",
        usesComposition,
        entry?.Capabilities ?? [],
        entry?.MaxReferenceImages ?? 0));
});
app.MapGet("/api/music/status", async (MusicGenerationService music, CancellationToken cancellationToken)
    => Results.Ok(await music.StatusAsync(cancellationToken)));
app.MapGet("/api/music/compositions", async (MusicCompositionService compositions, CancellationToken cancellationToken)
    => Results.Ok(await compositions.ListAsync(cancellationToken)));
app.MapGet("/api/music/compositions/{compositionId:guid}", async Task<IResult> (
    Guid compositionId, MusicCompositionService compositions, CancellationToken cancellationToken)
    => ToHttpResult(await compositions.GetAsync(compositionId, cancellationToken)));
app.MapPost("/api/music/compositions", async Task<IResult> (
    CreateMusicCompositionRequest request, MusicCompositionService compositions,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    return ToHttpResult(await compositions.CreateAsync(request, cancellationToken));
});
app.MapPost("/api/music/compositions/{compositionId:guid}/revisions", async Task<IResult> (
    Guid compositionId, CreateMusicRevisionRequest request, MusicCompositionService compositions,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    return ToHttpResult(await compositions.ReviseAsync(compositionId, request, cancellationToken));
});
app.MapPost("/api/music/compositions/{compositionId:guid}/edit", async Task<IResult> (
    Guid compositionId, EditMusicCompositionRequest request, MusicCompositionService compositions,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    return ToHttpResult(await compositions.EditWithCodexAsync(compositionId, request, cancellationToken));
});
app.MapPost("/api/music/revisions/{revisionId:guid}/render", async Task<IResult> (
    Guid revisionId, AudioGenerationJobService audioGeneration,
    GenerationJobSignal queue, HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var result = await audioGeneration.EnqueueMusicAsync(new RenderMusicRequest(revisionId), cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});

app.MapGet("/api/assets", async (bool? includeArchived, AssetStore assets, CancellationToken cancellationToken)
    => Results.Ok(await assets.ListAsync(includeArchived == true, cancellationToken)));
app.MapGet("/api/assets/{assetId:guid}/revisions", async Task<IResult> (Guid assetId, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.ListRevisionsAsync(assetId, cancellationToken)));
app.MapPost("/api/assets/{assetId:guid}/revisions", async Task<IResult> (Guid assetId, AddAssetRevisionRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.AddRevisionAsync(assetId, request, cancellationToken)));
app.MapPost("/api/assets/{assetId:guid}/make-current", async Task<IResult> (Guid assetId, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.PromoteRevisionAsync(assetId, cancellationToken)));
app.MapPost("/api/assets/generate-image", async Task<IResult> (
    GenerateAssetImageRequest request, AssetImageGenerationService generation, GenerationJobSignal queue,
    HttpContext context, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    var result = await generation.EnqueueAsync(request, cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});
app.MapPut("/api/assets/{assetId:guid}", async Task<IResult> (Guid assetId, UpdateAssetRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.UpdateAsync(assetId, request, cancellationToken)));
app.MapPost("/api/assets/{assetId:guid}/archive", async Task<IResult> (Guid assetId, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.SetArchivedAsync(assetId, true, cancellationToken)));
app.MapPost("/api/assets/{assetId:guid}/restore", async Task<IResult> (Guid assetId, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.SetArchivedAsync(assetId, false, cancellationToken)));
app.MapGet("/api/asset-collections", async (AssetStore assets, CancellationToken cancellationToken)
    => Results.Ok(await assets.ListCollectionsAsync(cancellationToken)));
app.MapPost("/api/asset-collections", async Task<IResult> (CreateAssetCollectionRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.CreateCollectionAsync(request, cancellationToken)));
app.MapPut("/api/asset-collections/{collectionId:guid}", async Task<IResult> (Guid collectionId, UpdateAssetCollectionRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.UpdateCollectionAsync(collectionId, request, cancellationToken)));
app.MapDelete("/api/asset-collections/{collectionId:guid}", async (Guid collectionId, AssetStore assets, CancellationToken cancellationToken)
    => await assets.DeleteCollectionAsync(collectionId, cancellationToken) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/asset-placements", async (Guid? assetId, Guid? shotId, AssetStore assets, CancellationToken cancellationToken)
    => Results.Ok(await assets.ListPlacementsAsync(assetId, shotId, cancellationToken)));
app.MapPost("/api/assets/{assetId:guid}/placements", async Task<IResult> (Guid assetId, CreateAssetPlacementRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.PlaceAsync(assetId, request, cancellationToken)));
app.MapDelete("/api/asset-placements/{placementId:guid}", async (Guid placementId, AssetStore assets, CancellationToken cancellationToken)
    => await assets.RemovePlacementAsync(placementId, cancellationToken) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/export/production/status", async (
    ProductionExportService export,
    CancellationToken cancellationToken) => Results.Ok(await export.InspectAsync(cancellationToken)));
app.MapGet("/api/export/production-package", async Task<IResult> (
    ProductionExportService export,
    CancellationToken cancellationToken) =>
{
    try
    {
        var package = await export.CreateAsync(allowWorkingCopy: false, cancellationToken);
        return Results.File(package.Content, "application/zip", package.FileName);
    }
    catch (ProductionExportBlockedException exception)
    {
        return Results.Conflict(new
        {
            error = exception.Message,
            readiness = exception.Readiness
        });
    }
});
app.MapGet("/api/export/working-package", async (
    ProductionExportService export,
    CancellationToken cancellationToken) =>
{
    var package = await export.CreateAsync(allowWorkingCopy: true, cancellationToken);
    return Results.File(package.Content, "application/zip", package.FileName);
});
app.MapGet("/api/export/audio/status", async (AudioMasteringService mastering, CancellationToken cancellationToken) => Results.Ok(await mastering.StatusAsync(cancellationToken)));
app.MapGet("/api/export/audio-mix", async Task<IResult> (AudioMasteringService mastering, CancellationToken cancellationToken) =>
{
    var result = await mastering.CreateAsync(cancellationToken);
    return result.Kind == RepositoryResultKind.Ok && result.Value is not null
        ? Results.File(result.Value.Content, "audio/wav", result.Value.FileName, enableRangeProcessing: false)
        : ToHttpResult(result);
});
app.MapGet("/api/maintenance/status", async (BackupService backup, CancellationToken cancellationToken) => Results.Ok(await backup.StatusAsync(cancellationToken)));
app.MapGet("/api/maintenance/backup", async (BackupService backup, CancellationToken cancellationToken) => { var package = await backup.CreateAsync(cancellationToken); return Results.File(package.Content, "application/zip", package.FileName); });
app.MapGet("/api/maintenance/diagnostics", async Task<IResult> (
    HttpContext context,
    DiagnosticBundleService diagnostics,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedLocalCaller(context.Connection.RemoteIpAddress, app.Configuration))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var package = await diagnostics.CreateAsync(cancellationToken);
    return Results.File(package.Content, "application/zip", package.FileName);
});
// Browser WebMCP calls these narrow, project-scoped routes. They deliberately
// return domain failures as stable envelopes and never dispatch a provider.
app.MapGet("/api/webmcp/context", async (Guid? selectedShotId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ContextAsync(selectedShotId, cancellationToken)));
app.MapGet("/api/scenes", async (SceneService scenes, CancellationToken cancellationToken)
    => Results.Ok(await scenes.ListAsync(cancellationToken)));
app.MapPost("/api/scenes", async Task<IResult> (CreateSceneRequest request, SceneService scenes, CancellationToken cancellationToken)
    => ToHttpResult(await scenes.CreateAsync(request, cancellationToken)));
app.MapGet("/api/scenes/{sceneId:guid}", async Task<IResult> (Guid sceneId, SceneService scenes, CancellationToken cancellationToken)
    => ToHttpResult(await scenes.GetAsync(sceneId, cancellationToken)));
app.MapPut("/api/scenes/{sceneId:guid}", async Task<IResult> (Guid sceneId, SaveSceneRequest request, SceneService scenes, CancellationToken cancellationToken)
    => ToHttpResult(await scenes.SaveAsync(sceneId, request, cancellationToken)));
app.MapGet("/api/scenes/{sceneId:guid}/annotations", async Task<IResult> (Guid sceneId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.ListAnnotationsAsync(sceneId, cancellationToken)));
app.MapPost("/api/scenes/{sceneId:guid}/annotations", async Task<IResult> (Guid sceneId, CreateSceneAnnotationRequest request, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.AddAnnotationAsync(sceneId, request, cancellationToken)));
app.MapPost("/api/scene-annotations/{annotationId:guid}/resolve", async Task<IResult> (Guid annotationId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.ResolveAnnotationAsync(annotationId, cancellationToken)));
app.MapGet("/api/webmcp/director/scene-context", async (Guid sceneId, Guid? instanceId, Guid? referenceAssetId, bool? directorMode, SceneDirectionService direction, CancellationToken cancellationToken)
    => Results.Ok(await direction.ContextAsync(sceneId, instanceId, referenceAssetId, directorMode ?? false, cancellationToken)));
app.MapPost("/api/webmcp/scene-proposals", async (CreateSceneProposalRequest request, SceneDirectionService direction, CancellationToken cancellationToken)
    => Results.Ok(await direction.ProposeAsync(request, cancellationToken)));
app.MapGet("/api/scenes/{sceneId:guid}/proposals", async Task<IResult> (Guid sceneId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.ListProposalsAsync(sceneId, cancellationToken)));
app.MapPost("/api/scene-proposals/{proposalId:guid}/accept", async Task<IResult> (Guid proposalId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.AcceptAsync(proposalId, cancellationToken)));
app.MapPost("/api/scene-proposals/{proposalId:guid}/reject", async Task<IResult> (Guid proposalId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.RejectAsync(proposalId, cancellationToken)));
app.MapPost("/api/scene-proposals/{proposalId:guid}/apply", async Task<IResult> (Guid proposalId, SceneDirectionService direction, CancellationToken cancellationToken)
    => ToHttpResult(await direction.ApplyAsync(proposalId, cancellationToken)));
app.MapGet("/api/models/generation/readiness", async (ModelGenerationService models, CancellationToken cancellationToken)
    => Results.Ok(await models.PreflightAsync(cancellationToken)));
app.MapPost("/api/models/generation", async Task<IResult> (
    CreateModelGenerationRequest request, ModelGenerationService models, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var queued = await models.EnqueueAsync(request, cancellationToken);
    if (queued.Kind == RepositoryResultKind.Ok && queued.Value is not null)
        await queue.QueueAsync(queued.Value.Id, cancellationToken);
    return ToHttpResult(queued);
});
// Preparing a derivative of a model already in the library: the same
// compiler and the same commissioning as generation, a different route, and a
// different question about what this workstation can do.
app.MapGet("/api/models/preparation/readiness", async (ModelGenerationService models, CancellationToken cancellationToken)
    => Results.Ok(await models.PreparationPreflightAsync(cancellationToken)));
app.MapPost("/api/models/preparation", async Task<IResult> (
    CreateModelPreparationRequest request, ModelGenerationService models, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var queued = await models.EnqueuePreparationAsync(request, cancellationToken);
    if (queued.Kind == RepositoryResultKind.Ok && queued.Value is not null)
        await queue.QueueAsync(queued.Value.Id, cancellationToken);
    return ToHttpResult(queued);
});
// The fixed views of the source and of the derivative, which is what the
// comparison is made of. Served from the job's own workspace rather than
// imported into the library: sixteen pictures of one decision are evidence,
// not assets somebody wants to browse.
app.MapGet("/api/jobs/{jobId:guid}/preparation-evidence", async Task<IResult> (
    Guid jobId, ModelGenerationService models, CancellationToken cancellationToken)
    => ToHttpResult(await models.PreparationEvidenceAsync(jobId, cancellationToken)));
app.MapGet("/api/jobs/{jobId:guid}/preparation-views/{step:int}/{file}", async Task<IResult> (
    Guid jobId, int step, string file, ModelGenerationService models, CancellationToken cancellationToken) =>
{
    var found = await models.PreparationViewFileAsync(jobId, step, file, cancellationToken);
    if (found.Kind != RepositoryResultKind.Ok) return ToHttpResult(found);
    return Results.File(found.Value.Path, found.Value.ContentType);
});
// The gate nothing automatic may pass. Every compiler receipt says a person
// still has to look; this is where looking is written down.
app.MapPost("/api/assets/{assetId:guid}/preparation-acceptance", async Task<IResult> (
    Guid assetId, SetPreparationAcceptanceRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.SetPreparationAcceptanceAsync(assetId, request, cancellationToken)));
app.MapPost("/api/webmcp/scene-blockouts", async (ProposeSceneBlockoutRequest request, SceneBlockoutService blockouts, CancellationToken cancellationToken)
    => Results.Ok(await blockouts.ProposeAsync(request, cancellationToken)));
app.MapGet("/api/scene-blockouts", async Task<IResult> (Guid? referenceAssetId, SceneBlockoutService blockouts, CancellationToken cancellationToken)
    => ToHttpResult(await blockouts.ListAsync(referenceAssetId, cancellationToken)));
app.MapGet("/api/scene-blockouts/{planId:guid}", async Task<IResult> (Guid planId, SceneBlockoutService blockouts, CancellationToken cancellationToken)
    => ToHttpResult(await blockouts.GetAsync(planId, cancellationToken)));
app.MapPost("/api/scene-blockouts/{planId:guid}/reject", async Task<IResult> (Guid planId, SceneBlockoutService blockouts, CancellationToken cancellationToken)
    => ToHttpResult(await blockouts.RejectAsync(planId, cancellationToken)));
app.MapPost("/api/scene-blockouts/{planId:guid}/apply", async Task<IResult> (Guid planId, ApplySceneBlockoutRequest? request, SceneBlockoutService blockouts, SceneService scenes, CancellationToken cancellationToken)
    => ToHttpResult(await blockouts.ApplyAsync(planId, request ?? new ApplySceneBlockoutRequest(null), scenes, cancellationToken)));
app.MapGet("/api/scenes/{sceneId:guid}/instances/{instanceId:guid}/motion-sample", async Task<IResult> (
    Guid sceneId, Guid instanceId, double? time, SceneMotionService motion, CancellationToken cancellationToken)
    => ToHttpResult(await motion.SampleAsync(sceneId, instanceId, time ?? 0, cancellationToken)));
app.MapGet("/api/scenes/{sceneId:guid}/blockout", async Task<IResult> (Guid sceneId, SceneBlockoutService blockouts, CancellationToken cancellationToken)
    => ToHttpResult(await blockouts.ForSceneAsync(sceneId, cancellationToken)));
app.MapGet("/api/webmcp/director/context", async (Guid shotId, int? displayedVersion, bool? archivedPreview, bool? directorMode, string? tool, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.DirectorContextAsync(shotId, displayedVersion, archivedPreview ?? false, directorMode ?? false, tool, cancellationToken)));
app.MapGet("/api/webmcp/director/observation", async (Guid shotId, int? displayedVersion, bool? archivedPreview, string? stateToken, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.DirectorObservationAsync(shotId, displayedVersion, archivedPreview ?? false, stateToken, cancellationToken)));
app.MapGet("/api/webmcp/shots", async (int? offset, int? limit, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListShotsAsync(offset ?? 0, limit ?? 10, cancellationToken)));
app.MapGet("/api/webmcp/shots/{shotId:guid}", async (Guid shotId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ShotDetailsAsync(shotId, cancellationToken)));
app.MapGet("/api/webmcp/shots/{shotId:guid}/continuity", async (Guid shotId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ContinuityAsync(shotId, cancellationToken)));
app.MapPost("/api/webmcp/proposals", async (CreateShotRevisionProposalRequest request, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ProposeAsync(request, cancellationToken)));
app.MapGet("/api/webmcp/proposals", async (Guid? shotId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListProposalsAsync(shotId, cancellationToken)));
app.MapPut("/api/webmcp/proposals/{proposalId:guid}", async (Guid proposalId, EditShotRevisionProposalRequest request, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.EditAsync(proposalId, request, cancellationToken)));
app.MapPost("/api/webmcp/proposals/{proposalId:guid}/accept", async (Guid proposalId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.AcceptAsync(proposalId, cancellationToken)));
app.MapPost("/api/webmcp/proposals/{proposalId:guid}/reject", async (Guid proposalId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.RejectAsync(proposalId, cancellationToken)));
app.MapPost("/api/webmcp/proposals/{proposalId:guid}/apply", async (Guid proposalId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ApplyAsync(proposalId, cancellationToken)));
app.MapGet("/api/webmcp/jobs/{jobId:guid}", async (Guid jobId, WebMcpStoryboardService service, CancellationToken cancellationToken)
    => Results.Ok(await service.JobStatusAsync(jobId, cancellationToken)));
app.MapGet("/api/shots/{shotId:guid}/visual-audit", async Task<IResult> (
    Guid shotId, VisualConsistencyService visualConsistency, CancellationToken cancellationToken) =>
{
    var audit = await visualConsistency.GetCurrentAsync(shotId, cancellationToken);
    return audit is null ? Results.NoContent() : Results.Ok(audit);
});
app.MapPost("/api/shots/{shotId:guid}/visual-audit", async Task<IResult> (
    Guid shotId, RunVisualAuditRequest request, VisualConsistencyService visualConsistency, CancellationToken cancellationToken) =>
    ToHttpResult(await visualConsistency.RunAsync(shotId, request.Force, cancellationToken)));
app.MapPost("/api/shots/{shotId:guid}/visual-audits/{auditId:guid}/reconcile", async Task<IResult> (
    Guid shotId, Guid auditId, ReconcileVisualAuditRequest request, VisualConsistencyService visualConsistency, CancellationToken cancellationToken) =>
    ToHttpResult(await visualConsistency.ReconcileAsync(shotId, auditId, request, cancellationToken)));

app.MapPost("/api/references", async Task<IResult> (
    CreateReferenceRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.CreateReferenceAsync(request, cancellationToken)));

// The cross-project authority library. Imports are copies with provenance, never
// live links: editing a library authority must not change a frame already delivered
// in another production.
app.MapGet("/api/library", async (AuthorityLibraryService library, CancellationToken cancellationToken)
    => Results.Ok(await library.ListAsync(cancellationToken)));
app.MapGet("/api/library/{libraryId:guid}/versions", async (Guid libraryId, AuthorityLibraryService library, CancellationToken cancellationToken)
    => Results.Ok(await library.GetVersionsAsync(libraryId, cancellationToken)));
// Served from the library so previewing does not require the viewing project to
// already own a copy of the image.
app.MapGet("/api/library/{libraryId:guid}/versions/{version:int}/image", async Task<IResult> (
    Guid libraryId, int version, HttpContext context, AuthorityLibraryService library, CancellationToken cancellationToken) =>
{
    var resolved = await library.ResolveImageAsync(libraryId, version, cancellationToken);
    if (resolved is null) return Results.NotFound();
    context.Response.Headers.ETag = $"\"{resolved.Value.ContentHash}\"";
    context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    return Results.File(resolved.Value.Path, resolved.Value.MimeType, enableRangeProcessing: true);
});
app.MapPost("/api/library/{libraryId:guid}/import", async Task<IResult> (
    Guid libraryId, AuthorityLibraryService library, CancellationToken cancellationToken)
    => ToHttpResult(await library.ImportAsync(libraryId, cancellationToken)));
app.MapDelete("/api/library/{libraryId:guid}", async Task<IResult> (
    Guid libraryId, AuthorityLibraryService library, CancellationToken cancellationToken) =>
{
    var result = await library.DeleteAsync(libraryId, cancellationToken);
    return result.Kind == RepositoryResultKind.Ok ? Results.NoContent() : ToHttpResult(result);
});
// Promote-up: most characters start inside one project and only prove reusable later.
app.MapPost("/api/references/{referenceId}/promote-to-library", async Task<IResult> (
    string referenceId, AuthorityLibraryService library, CancellationToken cancellationToken)
    => ToHttpResult(await library.PromoteAsync(referenceId, cancellationToken)));
// Appends the newer library version to this project's stack; never edits the
// imported version, so frozen manifests keep citing exactly what they rendered.
app.MapPost("/api/references/{referenceId}/pull-library-update", async Task<IResult> (
    string referenceId, AuthorityLibraryService library, CancellationToken cancellationToken)
    => ToHttpResult(await library.PullUpdateAsync(referenceId, cancellationToken)));
static IResult AuthorityResult<T>(RepositoryResult<T> result) => result.Kind switch
{
    RepositoryResultKind.NotFound => Results.NotFound(),
    RepositoryResultKind.Conflict => Results.Conflict(new { error = result.Error }),
    RepositoryResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
    _ => Results.Ok(result.Value)
};

app.MapPut("/api/references/{referenceId}", async Task<IResult> (
    string referenceId, UpdateReferenceRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => AuthorityResult(await repository.UpdateReferenceAsync(referenceId, request, cancellationToken)));

app.MapDelete("/api/references/{referenceId}", async Task<IResult> (
    string referenceId, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var result = await repository.DeleteReferenceAsync(referenceId, cancellationToken);
    return result.Kind == RepositoryResultKind.Ok ? Results.NoContent() : AuthorityResult(result);
});

// Top of the stack is what ships, so promotion re-issues the chosen version at
// the top instead of moving a live marker.
app.MapPost("/api/references/{referenceId}/versions/{version:int}/promote", async Task<IResult> (
    string referenceId, int version, StudioRepository repository, CancellationToken cancellationToken)
    => AuthorityResult(await repository.PromoteReferenceVersionAsync(referenceId, version, cancellationToken)));

app.MapDelete("/api/references/{referenceId}/versions/{version:int}", async Task<IResult> (
    string referenceId, int version, StudioRepository repository, CancellationToken cancellationToken)
    => AuthorityResult(await repository.DeleteReferenceVersionAsync(referenceId, version, cancellationToken)));

app.MapGet("/api/references/{referenceId}/versions", async (
    string referenceId, StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetReferenceVersionsAsync(referenceId, cancellationToken)));
app.MapPost("/api/references/{referenceId}/versions", async Task<IResult> (
    string referenceId, CreateReferenceVersionRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.CreateReferenceVersionAsync(referenceId, request, cancellationToken)));

app.MapPost("/api/shots", async Task<IResult> (
    CreateShotRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.CreateShotAsync(request, cancellationToken)));
// Remove a slot. Ratified evidence is refused unless the caller accepts the
// loss explicitly, which the board turns into a stronger confirmation.
app.MapDelete("/api/shots/{shotId:guid}", async Task<IResult> (
    Guid shotId, bool? acceptRatifiedLoss, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var result = await repository.DeleteShotAsync(shotId, acceptRatifiedLoss ?? false, cancellationToken);
    return result.Kind switch
    {
        RepositoryResultKind.NotFound => Results.NotFound(),
        RepositoryResultKind.Conflict => Results.Conflict(new { error = result.Error }),
        RepositoryResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
        _ => Results.Ok(result.Value)
    };
});

app.MapPost("/api/shots/{shotId:guid}/duplicate", async Task<IResult> (
    Guid shotId, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var result = await repository.DuplicateShotAsync(shotId, cancellationToken);
    return result.Kind switch
    {
        RepositoryResultKind.NotFound => Results.NotFound(),
        RepositoryResultKind.Conflict => Results.Conflict(new { error = result.Error }),
        RepositoryResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
        _ => Results.Ok(result.Value)
    };
});

app.MapPut("/api/shots/{shotId:guid}", async Task<IResult> (
    Guid shotId, UpdateShotRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.UpdateShotAsync(shotId, request, cancellationToken)));

app.MapPost("/api/assets/images", async Task<IResult> (
    HttpContext context, AssetStore assets, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1")
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "A multipart image upload is required." });
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { error = "The multipart field 'file' is required." });
    return ToHttpResult(await assets.ImportImageAsync(file, cancellationToken));
}).DisableAntiforgery();

app.MapPost("/api/assets/models", async Task<IResult> (
    HttpContext context, AssetStore assets, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "A multipart model upload is required." });
    var form = await context.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { error = "The multipart field 'file' is required." });
    return ToHttpResult(await assets.ImportModelAsync(file, cancellationToken));
}).DisableAntiforgery();

app.MapGet("/api/assets/{assetId:guid}/model-profile", async Task<IResult> (
    Guid assetId, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.ModelProfileAsync(assetId, cancellationToken)));

app.MapPost("/api/assets/{assetId:guid}/rig-pose", async Task<IResult> (
    Guid assetId, RigPoseRequest request, AssetStore assets, CancellationToken cancellationToken)
    => ToHttpResult(await assets.RigPoseAsync(assetId, request, cancellationToken)));

app.MapPost("/api/assets/media", async Task<IResult> (
    HttpContext context, AssetKind kind, AssetStore assets, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers["X-Storyboard-Studio"] != "1") return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (kind is not (AssetKind.Audio or AssetKind.Video)) return Results.BadRequest(new { error = "Media kind must be Audio or Video." });
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "A multipart media upload is required." });
    var form = await context.Request.ReadFormAsync(cancellationToken); var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest(new { error = "The multipart field 'file' is required." });
    return ToHttpResult(await assets.ImportMediaAsync(file, kind, cancellationToken));
}).DisableAntiforgery();

app.MapGet("/api/timeline/clips", async (TimelineService timeline, CancellationToken cancellationToken) => Results.Ok(await timeline.ListClipsAsync(cancellationToken)));
app.MapPost("/api/timeline/clips", async Task<IResult> (SaveTimelineClipRequest request, TimelineService timeline, CancellationToken cancellationToken) => ToHttpResult(await timeline.CreateClipAsync(request, cancellationToken)));
app.MapPut("/api/timeline/clips/{clipId:guid}", async Task<IResult> (Guid clipId, SaveTimelineClipRequest request, TimelineService timeline, CancellationToken cancellationToken) => ToHttpResult(await timeline.UpdateClipAsync(clipId, request, cancellationToken)));
app.MapDelete("/api/timeline/clips/{clipId:guid}", async Task<IResult> (Guid clipId, TimelineService timeline, CancellationToken cancellationToken) => await timeline.DeleteClipAsync(clipId, cancellationToken) ? Results.NoContent() : Results.NotFound());
app.MapGet("/api/voice-profiles", async (TimelineService timeline, CancellationToken cancellationToken) => Results.Ok(await timeline.ListVoiceProfilesAsync(cancellationToken)));
app.MapPost("/api/voice-profiles", async Task<IResult> (CreateVoiceProfileRequest request, TimelineService timeline, CancellationToken cancellationToken) => ToHttpResult(await timeline.CreateVoiceProfileAsync(request, cancellationToken)));
app.MapPost("/api/references/{referenceId}/voice-auditions", async Task<IResult> (
    string referenceId, DesignVoiceAuditionsRequest request,
    AudioGenerationJobService audioGeneration, GenerationJobSignal queue,
    CancellationToken cancellationToken) =>
{
    var result = await audioGeneration.EnqueueVoiceDesignAsync(referenceId, request, cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});
app.MapGet("/api/jobs/{jobId:guid}/voice-auditions", async Task<IResult> (
    Guid jobId, StudioDbContext db, CancellationToken cancellationToken) =>
{
    var resultJson = await db.Jobs.AsNoTracking()
        .Where(job => job.Id == jobId && job.WorkType == AudioGenerationJobService.VoiceDesignWorkType
            && job.State == JobState.Completed.ToString())
        .Select(job => job.ResultJson)
        .SingleOrDefaultAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(resultJson)) return Results.NotFound();
    try
    {
        return Results.Ok(JsonSerializer.Deserialize<IReadOnlyList<VoiceAuditionSummary>>(
            resultJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []);
    }
    catch (JsonException)
    {
        return Results.Problem("The completed voice audition result packet is invalid.", statusCode: StatusCodes.Status500InternalServerError);
    }
});
app.MapGet("/api/voice/synthesis", (VoiceSynthesisService synthesis) => Results.Ok(synthesis.Status()));
app.MapPost("/api/timeline/clips/{clipId:guid}/synthesize", async Task<IResult> (
    Guid clipId, SynthesizeVoiceRequest request, AudioGenerationJobService audioGeneration,
    GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var result = await audioGeneration.EnqueueVoiceAsync(clipId, request, cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});

app.MapGet("/api/assets/{assetId:guid}/content", async Task<IResult> (
    Guid assetId, HttpContext context, AssetStore assets, CancellationToken cancellationToken) =>
{
    var asset = await assets.GetAsync(assetId, cancellationToken);
    if (asset is null) return Results.NotFound();
    var path = assets.ResolveContentPath(asset);
    if (!File.Exists(path)) return Results.Problem("The asset record exists but its content is unavailable.", statusCode: StatusCodes.Status410Gone);
    context.Response.Headers.ETag = $"\"{asset.ContentHash}\"";
    context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    return Results.File(path, asset.MimeType, enableRangeProcessing: true);
});

app.MapGet("/api/assets/{assetId:guid}/review-notes", async (
    Guid assetId, StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetAssetReviewNotesAsync(assetId, cancellationToken)));

app.MapPost("/api/assets/{assetId:guid}/review-notes", async Task<IResult> (
    Guid assetId, CreateAssetReviewNoteRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.AddAssetReviewNoteAsync(assetId, request, cancellationToken)));

app.MapPut("/api/asset-review-notes/{noteId:guid}/position", async Task<IResult> (
    Guid noteId, MoveCommentRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.MoveAssetReviewNoteAsync(noteId, request, cancellationToken)));

app.MapPost("/api/asset-review-notes/{noteId:guid}/resolve", async Task<IResult> (
    Guid noteId, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.ResolveAssetReviewNoteAsync(noteId, cancellationToken)));

app.MapPost("/api/shots/{shotId:guid}/comments", async Task<IResult> (
    Guid shotId, CreateCommentRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Body)) return TypedResults.BadRequest(new { error = "Comment text is required." });
    var result = await repository.AddCommentAsync(shotId, request, cancellationToken);
    return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
});

app.MapPut("/api/comments/{commentId:guid}/position", async Task<IResult> (
    Guid commentId, MoveCommentRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
{
    if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
        return TypedResults.BadRequest(new { error = "Comment coordinates must be finite numbers." });
    var result = await repository.MoveCommentAsync(commentId, request, cancellationToken);
    return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
});

app.MapPost("/api/comments/{commentId:guid}/resolve", async Task<IResult> (
    Guid commentId, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var result = await repository.ResolveCommentAsync(commentId, cancellationToken);
    return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
});

app.MapGet("/api/shots/{shotId:guid}/versions", async (Guid shotId, StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetVersionsAsync(shotId, cancellationToken)));
app.MapGet("/api/shots/{shotId:guid}/candidates", async (Guid shotId, StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetCandidatesAsync(shotId, cancellationToken)));
app.MapPut("/api/shots/{shotId:guid}/video-endpoints", async Task<IResult> (
    Guid shotId, UpdateVideoEndpointsRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.UpdateVideoEndpointsAsync(shotId, request, cancellationToken)));

// Bin a draft that went nowhere. The live candidate and anything ratified are
// append-only evidence and are refused; this only removes discarded attempts.
app.MapDelete("/api/shots/{shotId:guid}/candidates/{candidateId:guid}", async Task<IResult> (
    Guid shotId, Guid candidateId, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var result = await repository.DeleteCandidateAsync(shotId, candidateId, cancellationToken);
    return result.Kind switch
    {
        RepositoryResultKind.NotFound => Results.NotFound(),
        RepositoryResultKind.Conflict => Results.Conflict(new { error = result.Error }),
        RepositoryResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
        _ => Results.NoContent()
    };
});

// Choosing an earlier draft never rewrites history. It copies that exact image
// into a fresh working version and makes the copy the live head.
app.MapPost("/api/shots/{shotId:guid}/candidates/{candidateId:guid}/copy-forward", async Task<IResult> (
    Guid shotId, Guid candidateId, PromoteCandidateRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.PromoteCandidateAsync(shotId, candidateId, request, cancellationToken)));

/// One-keystroke iteration: "do this differently". Records the direction against
/// the shot's brief, freezes a fresh manifest, and dispatches it — so the artist
/// types a sentence and a new candidate comes back, without touching the lab.
app.MapPost("/api/shots/{shotId:guid}/redirect", async Task<IResult> (
    Guid shotId, RedirectShotRequest request, StudioRepository repository,
    GenerationOrchestrator orchestrator, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var direction = (request.Direction ?? string.Empty).Trim();
    if (direction.Length is 0 or > 2_000) return Results.BadRequest(new { error = "A change note must contain 1 to 2,000 characters." });

    var adapterId = request.AdapterId ?? ComfyUiGenerationAdapter.AdapterId;
    var adapter = orchestrator.ListAdapters().SingleOrDefault(item => item.Id == adapterId);
    if (adapter is null || !adapter.Routes.Contains(GenerationRoute.FastDraft) || !adapter.Purposes.Contains(GenerationPurpose.Draft))
        return Results.BadRequest(new { error = "The selected adapter cannot generate a fast draft." });
    if (!adapter.CanDispatch)
        return Results.Conflict(new { error = adapter.Detail });

    var sketch = await repository.GetSketchAsync(shotId, cancellationToken);
    if (sketch is null) return Results.BadRequest(new { error = "This shot has no composition yet. Open the sketch lab once before iterating." });

    var shot = (await repository.GetSnapshotAsync(cancellationToken)).Shots.SingleOrDefault(x => x.Id == shotId);
    if (shot is null) return Results.NotFound();

    if (request.GuidanceAssetId is not null)
    {
        if (request.MarkupRevision is null) return Results.BadRequest(new { error = "The annotation guide is missing its markup revision." });
        var markup = await repository.GetFrameMarkupAsync(shotId, shot.Version, cancellationToken);
        if (markup is null || markup.Revision != request.MarkupRevision)
            return Results.Conflict(new { error = "The frame markup changed while the edit guide was being prepared. Try Send change again." });
    }

    // Keep only the latest direction so repeated passes do not stack notes forever.
    // This creates a manifest override; it never mutates the reusable sketch brief.
    const string marker = "\n\nREQUESTED CHANGE: ";
    var index = sketch.CreativeBrief.IndexOf(marker, StringComparison.Ordinal);
    var baseBrief = index >= 0 ? sketch.CreativeBrief[..index] : sketch.CreativeBrief;
    var creativeBrief = baseBrief + marker + direction;
    if (request.GuidanceAssetId is not null)
        creativeBrief += "\n\nFRAME EDIT GUIDE: The attached image is the current generated frame with temporary gold markup. Use the gold strokes only to locate the requested change. Do not reproduce, preserve, stylize, or bake any markup into the output.";
    else if (shot.CurrentAssetId is not null)
        creativeBrief += $"\n\nCURRENT FRAME EDIT: Use the attached current generated frame v{shot.Version} as the visual starting point. Preserve all content not named by the requested change; do not restart from the original sketch.";
    var prepared = await repository.PrepareManifestAsync(shotId, new PrepareGenerationManifestRequest(
        shot.Version, sketch.Revision, GenerationRoute.FastDraft, GenerationPurpose.Draft,
        request.GuidanceAssetId ?? shot.CurrentAssetId, creativeBrief, request.MarkupRevision), cancellationToken);
    if (prepared.Kind != RepositoryResultKind.Ok || prepared.Value is null)
        return Results.BadRequest(new { error = prepared.Error ?? "Could not freeze a manifest for this change." });

    var dispatched = await orchestrator.DispatchAsync(prepared.Value.Id, new DispatchManifestRequest(
        prepared.Value.ManifestHash, adapterId), cancellationToken);
    if (dispatched.Kind != RepositoryResultKind.Ok || dispatched.Value is null)
        return Results.Conflict(new { error = dispatched.Error ?? "Could not dispatch this change." });

    await queue.QueueAsync(dispatched.Value.Id, cancellationToken);
    return Results.Accepted($"/api/studio", dispatched.Value);
});

app.MapGet("/api/shots/{shotId:guid}/sketch", async Task<IResult> (
    Guid shotId, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var sketch = await repository.GetSketchAsync(shotId, cancellationToken);
    return sketch is null ? Results.NoContent() : Results.Ok(sketch);
});

app.MapPut("/api/shots/{shotId:guid}/sketch", async Task<IResult> (
    Guid shotId, SaveSketchRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.SaveSketchAsync(shotId, request, cancellationToken)));
app.MapGet("/api/pose-presets", async (StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.ListPosePresetsAsync(cancellationToken)));
app.MapPost("/api/pose-presets", async Task<IResult> (CreatePosePresetRequest request, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.CreatePosePresetAsync(request, cancellationToken)));
app.MapDelete("/api/pose-presets/{id:guid}", async Task<IResult> (Guid id, StudioRepository repository, CancellationToken cancellationToken)
    => ToHttpResult(await repository.DeletePosePresetAsync(id, cancellationToken)));

app.MapGet("/api/shots/{shotId:guid}/versions/{version:int}/markup", async Task<IResult> (
    Guid shotId, int version, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var markup = await repository.GetFrameMarkupAsync(shotId, version, cancellationToken);
    return markup is null ? Results.NoContent() : Results.Ok(markup);
});

app.MapPut("/api/shots/{shotId:guid}/versions/{version:int}/markup", async Task<IResult> (
    Guid shotId, int version, SaveFrameMarkupRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.SaveFrameMarkupAsync(shotId, version, request, cancellationToken)));

app.MapGet("/api/shots/{shotId:guid}/manifests", async (
    Guid shotId, StudioRepository repository, CancellationToken cancellationToken)
    => Results.Ok(await repository.GetManifestsAsync(shotId, cancellationToken)));

app.MapPost("/api/shots/{shotId:guid}/manifests/prepare", async Task<IResult> (
    Guid shotId, PrepareGenerationManifestRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.PrepareManifestAsync(shotId, request, cancellationToken)));

/// The normal artist-facing draft action. A saved composition is optional and
/// the selected adapter determines whether this is a fast local pass or a
/// precision pass through Codex/OpenAI.
app.MapPost("/api/shots/{shotId:guid}/generate-draft", async Task<IResult> (
    Guid shotId, GenerateDraftRequest request, StudioRepository repository,
    GenerationOrchestrator orchestrator, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var adapterId = request.AdapterId ?? ComfyUiGenerationAdapter.AdapterId;
    var adapter = orchestrator.ListAdapters().SingleOrDefault(item => item.Id == adapterId);
    var route = adapter?.Routes.Contains(GenerationRoute.FastDraft) == true
        ? GenerationRoute.FastDraft
        : adapter?.Routes.Contains(GenerationRoute.PrecisionDraft) == true
            ? GenerationRoute.PrecisionDraft
            : (GenerationRoute?)null;
    if (adapter is null || route is null || !adapter.Purposes.Contains(GenerationPurpose.Draft))
        return Results.BadRequest(new { error = "The selected adapter cannot generate an image draft." });
    if (!adapter.CanDispatch) return Results.Conflict(new { error = adapter.Detail });

    var sketch = await repository.GetSketchAsync(shotId, cancellationToken);
    var compositionAssetId = request.CompositionAssetId
        ?? (request.AllowSketchCompositionFallback ? sketch?.CompositionAssetId : null);
    var prepared = await repository.PrepareManifestAsync(shotId, new PrepareGenerationManifestRequest(
        request.ExpectedShotVersion,
        sketch?.Revision ?? 0,
        route.Value,
        GenerationPurpose.Draft,
        compositionAssetId,
        request.CreativeBriefOverride,
        request.MarkupRevision,
        request.AllowSketchCompositionFallback), cancellationToken);
    if (prepared.Kind != RepositoryResultKind.Ok || prepared.Value is null) return ToHttpResult(prepared);

    var dispatched = await orchestrator.DispatchAsync(prepared.Value.Id,
        new DispatchManifestRequest(prepared.Value.ManifestHash, adapterId), cancellationToken);
    if (dispatched.Kind != RepositoryResultKind.Ok || dispatched.Value is null) return ToHttpResult(dispatched);
    await queue.QueueAsync(dispatched.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{dispatched.Value.Id}", dispatched.Value);
});
app.MapPost("/api/shots/{shotId:guid}/video-manifests/prepare", async Task<IResult> (
    Guid shotId, PrepareVideoManifestRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
    ToHttpResult(await repository.PrepareVideoManifestAsync(shotId, request, cancellationToken)));

app.MapPost("/api/shots/{shotId:guid}/video-jobs/{jobId:guid}/promote", async Task<IResult> (
    Guid shotId, Guid jobId, PromoteVideoTakeRequest request, StudioRepository repository,
    GenerationOrchestrator orchestrator, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var prepared = await repository.PrepareVideoPromotionManifestAsync(shotId, jobId, request.ExpectedShotVersion, cancellationToken);
    if (prepared.Kind != RepositoryResultKind.Ok || prepared.Value is null) return ToHttpResult(prepared);
    var dispatched = await orchestrator.DispatchAsync(prepared.Value.Id,
        new DispatchManifestRequest(prepared.Value.ManifestHash, request.AdapterId), cancellationToken);
    if (dispatched.Kind != RepositoryResultKind.Ok || dispatched.Value is null) return ToHttpResult(dispatched);
    await queue.QueueAsync(dispatched.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{dispatched.Value.Id}", dispatched.Value);
});

app.MapPost("/api/manifests/{manifestId:guid}/dispatch", async Task<IResult> (
    Guid manifestId, DispatchManifestRequest request, GenerationOrchestrator orchestrator, GenerationJobSignal queue, CancellationToken cancellationToken) =>
{
    var result = await orchestrator.DispatchAsync(manifestId, request, cancellationToken);
    if (result.Kind != RepositoryResultKind.Ok || result.Value is null) return ToHttpResult(result);
    await queue.QueueAsync(result.Value.Id, cancellationToken);
    return Results.Accepted($"/api/jobs/{result.Value.Id}", result.Value);
});

app.MapPost("/api/shots/{shotId:guid}/ratify", async Task<IResult> (
    Guid shotId, RatifyRequest request, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var (shot, error) = await repository.RatifyAsync(shotId, request, cancellationToken);
    if (shot is not null) return TypedResults.Ok(shot);
    return error is null ? TypedResults.NotFound() : TypedResults.Conflict(new { error });
});

app.MapPost("/api/codex/assist", async (CodexAssistRequest request, IntegrationDiscoveryService discovery, StudioRepository repository, CancellationToken cancellationToken) =>
{
    var snapshot = await repository.GetSnapshotAsync(cancellationToken);
    return Results.Ok(await discovery.AskCodexAsync(request, snapshot, cancellationToken));
});

/// The project interview. Codex proposes an editable contract from four
/// plain-language answers; this endpoint writes nothing. Creating the project and
/// its starter authorities goes through the ordinary reviewed endpoints, so the
/// advise/ratify boundary holds here as it does everywhere else.
app.MapPost("/api/codex/project-interview", async Task<IResult> (
    ProjectInterviewRequest request, IntegrationDiscoveryService discovery, CancellationToken cancellationToken) =>
{
    var answers = new[] { request.Kind, request.Look, request.Cast, request.Locked };
    if (answers.All(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new { error = "Answer at least one question so there is something to work from." });
    if (answers.Any(answer => (answer ?? string.Empty).Trim().Length > 600))
        return Results.BadRequest(new { error = "Keep each answer to 600 characters or fewer." });
    return Results.Ok(await discovery.ProposeProjectSetupAsync(request, cancellationToken));
});

app.MapPost("/api/codex/shot-intent", async Task<IResult> (SuggestShotIntentRequest request, IntegrationDiscoveryService discovery, StudioRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 5_000)
        return Results.BadRequest(new { error = "Describe the shot in 1 to 5,000 characters." });
    var snapshot = await repository.GetSnapshotAsync(cancellationToken);
    return Results.Ok(await discovery.SuggestShotIntentAsync(request, snapshot, cancellationToken));
});

app.MapPost("/api/shots/{shotId:guid}/codex/improve-generation-direction", async Task<IResult> (
    Guid shotId, ImproveGenerationDirectionRequest request, IntegrationDiscoveryService discovery, StudioRepository repository, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Direction) || request.Direction.Trim().Length > 2_500)
        return Results.BadRequest(new { error = "Write a generation direction between 1 and 2,500 characters." });
    var snapshot = await repository.GetSnapshotAsync(cancellationToken);
    if (!snapshot.Shots.Any(item => item.Id == shotId)) return Results.NotFound();
    return Results.Ok(await discovery.ImproveGenerationDirectionAsync(shotId, request, snapshot, cancellationToken));
});

app.MapFallbackToFile("index.html");

await StudioDatabaseInitializer.InitializeAsync(app.Services);
await app.RunAsync();

static bool IsTrustedLocalCaller(IPAddress? remoteAddress, IConfiguration configuration)
{
    if (remoteAddress is null || IPAddress.IsLoopback(remoteAddress)) return true;
    var normalized = remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress;
    return configuration.GetSection("Studio:TrustedProxyAddresses").GetChildren()
        .Select(entry => entry.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Any(value => IPAddress.TryParse(value, out var trusted) && normalized.Equals(trusted.IsIPv4MappedToIPv6 ? trusted.MapToIPv4() : trusted));
}

static IResult ToHttpResult<T>(RepositoryResult<T> result) => result.Kind switch
{
    RepositoryResultKind.Ok => Results.Ok(result.Value),
    RepositoryResultKind.NotFound => Results.NotFound(),
    RepositoryResultKind.Invalid => Results.BadRequest(new { error = result.Error }),
    RepositoryResultKind.Conflict => Results.Conflict(new { error = result.Error }),
    RepositoryResultKind.Unavailable => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
};

public partial class Program;
