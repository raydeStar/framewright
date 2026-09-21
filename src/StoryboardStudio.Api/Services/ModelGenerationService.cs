using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// One image revision, frozen into a request, compiled into a model candidate.
///
/// The artist picks a reference image; this freezes exactly which bytes of it
/// were chosen, queues an owned job, and later asks the Reference Asset
/// Compiler to turn it into a model. Nothing about that is instant: these
/// stages run for minutes, so the job is durable, resumable, and honest about
/// where it has got to, and the artist can close the screen.
///
/// The route is uncommissioned until the artist says otherwise: a capability
/// that exists is not permission to spend an hour of GPU on it.
/// </summary>
public sealed class ModelGenerationService(
    StudioDbContext db,
    AssetStore assets,
    ICompilerGateway compiler,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public const string ModelWorkType = "Model";
    public const string GeometryStage = "geometry";
    public const string StageMeshStage = "stage-mesh";
    public const string RemeshStage = "remesh";
    public const string UvUnwrapStage = "uv-unwrap";
    public const string TextureStage = "texture";
    public const string GlassStage = "glass";
    public const string BrowserPayloadStage = "browser-payload";
    public const string AdoptMeshStage = "adopt-mesh";
    public const string ReduceMeshStage = "reduce-mesh";
    public const string ReviewViewsStage = "review-views";

    /// <summary>Which of the two things a frozen packet is asking for.</summary>
    public const string GenerateWork = "generate";
    public const string PrepareWork = "prepare";

    /// <summary>
    /// What a reference image goes through to become something a browser can
    /// load. Four stages, because four different things happen and each can
    /// fail on its own terms.
    ///
    /// The generator makes a mesh from a picture, and that mesh is dense and
    /// sizeless: the first real run produced 2.4 million triangles at roughly
    /// two metres tall, whatever the subject. Staging gives it the size the
    /// artist said it is, which is what makes the reduction gate's millimetres
    /// mean anything. The remesh rebuilds it on a uniform grid and collapses
    /// that to a runtime budget, which is the difference between a clean
    /// surface and a creased one: a generator's output has no topology worth
    /// preserving, so compressing it only preserves the noise.
    ///
    /// What comes out of that is the right shape and the wrong colour: a
    /// generated mesh has none, and no UVs to put any on. Unwrapping gives it
    /// a map without moving a vertex, and the painter fills that map from the
    /// same reference the geometry came from. Only then is there a mesh worth
    /// exporting for a browser.
    /// </summary>
    public static readonly string[] ReferenceToModelRoute =
    [
        GeometryStage, StageMeshStage, RemeshStage,
        UvUnwrapStage, TextureStage, BrowserPayloadStage,
    ];

    /// <summary>
    /// What an existing library model goes through to become a derivative a
    /// browser or a runtime can carry.
    ///
    /// This is the opposite handling from generation, and getting that
    /// backwards is the mistake worth naming. A generated mesh has no topology
    /// worth keeping, so it is rebuilt on a grid and painted from scratch. A
    /// model already in this library is the other way round: its UVs, its
    /// materials and its shell are what somebody looked at and accepted, and a
    /// preparation that rebuilt any of them would be throwing that review away
    /// and calling the result a derivative.
    ///
    /// So it is adopted unchanged, collapsed with its UVs and materials riding
    /// along, and exported through the same gate a generated model goes
    /// through. The fixed views are rendered twice, from the original and from
    /// the derivative, because comparing the two is the review -- a
    /// derivative's views on their own show only that something rendered.
    /// </summary>
    private static readonly RouteStepPlan[] PreparationPlan =
    [
        // Read from the original, not from the step before: this is evidence
        // of what the source looked like, and there is no step before it.
        new(ReviewViewsStage, ReadsOriginal: true),
        new(AdoptMeshStage, ReadsOriginal: true),
        new(ReduceMeshStage),
        new(BrowserPayloadStage),
        new(ReviewViewsStage),
    ];

    /// <summary>Every stage a preparation needs, for the capability check.</summary>
    public static readonly string[] PreparationRoute =
        [.. PreparationPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>A step of a route before its output suffix is known.</summary>
    private sealed record RouteStepPlan(string Stage, bool ReadsOriginal = false);

    /// <summary>
    /// The route for one request. Glazing is only in it when the artist said
    /// this thing has glass, because most props have none and a stage that ran
    /// anyway would have to guess which colour was a window -- turning an
    /// ordinary painted surface see-through.
    /// </summary>
    private static string[] RouteFor(string? glassColour) =>
        string.IsNullOrWhiteSpace(glassColour)
            ? ReferenceToModelRoute
            : [.. ReferenceToModelRoute[..^1], GlassStage, ReferenceToModelRoute[^1]];

    /// <summary>What each step is doing, in words an artist reading a queue would use.</summary>
    private static readonly Dictionary<string, string> StagePhase = new(StringComparer.Ordinal)
    {
        [GeometryStage] = "Making a mesh from the reference",
        [StageMeshStage] = "Setting its real size",
        [RemeshStage] = "Rebuilding it evenly at a size a browser can carry",
        [UvUnwrapStage] = "Unfolding it so it can be painted",
        [TextureStage] = "Painting it from the reference",
        [GlassStage] = "Letting the glass through",
        [BrowserPayloadStage] = "Preparing the mesh for the browser",
        [AdoptMeshStage] = "Taking the reviewed mesh in, unchanged",
        [ReduceMeshStage] = "Collapsing it to a runtime budget",
        [ReviewViewsStage] = "Rendering the views a person judges it by",
    };

    /// <summary>
    /// The runtime budget for a prepared derivative when the artist named
    /// none. Half the V1 cohort ceiling, because a derivative that is the same
    /// size as its source is not a derivative of anything.
    /// </summary>
    private const int DefaultPreparedTriangleBudget = 10_000;

    /// <summary>
    /// The generator's own default octree is a production one, and this is a
    /// browser studio: 512 cost two minutes and 2.4 million triangles, 256
    /// costs forty-seven seconds and 592,000, and both are reduced to the same
    /// runtime budget afterwards. Asking for more detail than survives the
    /// reduction is spending hardware on something nobody will ever see.
    /// </summary>
    private const string BrowserOctreeResolution = "256";

    /// <summary>The V1 cohort contract's ceiling, which the library also enforces.</summary>
    private const string RuntimeTriangleBudget = "20000";

    // Version 2 carries a route where version 1 carried one stage name. A
    // version 1 packet is not migrated, because the single stage it names is
    // the payload export reading an image, which could never have produced a
    // model. Failing it honestly beats rerunning work that cannot succeed.
    // Version 6 carries what kind of work the packet is asking for. A version
    // 5 packet named no work at all, and defaulting one would be guessing at
    // an answer the artist gave; there are only ever a handful in flight, and
    // asking again costs a click.
    private const int FrozenPacketVersion = 6;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What the request froze. Stored on the job so a restart, a retry, or an
    /// argument six weeks later all read the same answer: these bytes, that
    /// stage, this compiler.
    /// </summary>
    private sealed record FrozenModelRequest(
        int PacketVersion, Guid SourceAssetId, string SourceContentHash, string SourceName,
        int SourceRevisionNumber, RouteStep[] Route, string? CompilerVersion, DateTimeOffset FrozenAt,
        string Size, double SizeAdjust, string? GlassColour,
        string Work = GenerateWork, int TriangleBudget = 0);

    /// <summary>
    /// What one step of the route actually did, kept so the delivery can say
    /// which steps ran, which were adopted from an interrupted attempt, and
    /// which had to be run a second time.
    /// </summary>
    /// <summary>
    /// One step of a frozen route: which stage, and what it writes. The shape
    /// is frozen with the route because a step's file has to be named before
    /// the stage runs, and a restart must name the same file it named before —
    /// otherwise a finished step looks unfinished and the expensive one is
    /// paid for twice.
    /// </summary>
    /// <param name="ReadsOriginal">
    /// True for a step that reads the frozen source rather than what the step
    /// before it wrote. A preparation needs two of them -- the source's own
    /// fixed views, and the adoption -- and without this a route can only ever
    /// be a chain, which would make the source's views a picture of the
    /// source's views.
    /// </param>
    private sealed record RouteStep(string Stage, string OutputSuffix, bool ReadsOriginal = false);

    /// <summary>
    /// A step's answer is present when its file is there -- or, for the one
    /// stage whose output is a directory of fixed views, when the directory
    /// is. A resume that could not recognise a directory would re-render
    /// evidence the compiler refuses to overwrite, and fail.
    /// </summary>
    private static bool Produced(string path) => File.Exists(path) || Directory.Exists(path);

    private sealed record StepOutcome(
        string Stage, string OutputPath, string? ReceiptJson, bool Adopted, bool RerunAfterPartial);

    /// <summary>Can this workstation do the work, and is it allowed to?</summary>
    public Task<ModelGenerationReadiness> PreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(ReferenceToModelRoute, "Model generation", cancellationToken);

    /// <summary>
    /// The same question for the preparation route, which shares this
    /// compiler and this commissioning but needs different stages. Asking
    /// about the route that will actually run is the difference between a
    /// refusal and a job that dies partway.
    /// </summary>
    public Task<ModelGenerationReadiness> PreparationPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(PreparationRoute, "Preparing a derivative", cancellationToken);

    private async Task<ModelGenerationReadiness> PreflightAsync(
        string[] route, string work, CancellationToken cancellationToken)
    {
        var capabilities = await compiler.DescribeAsync(cancellationToken);
        // Every step, not just the last one. A route whose first stage cannot
        // run is a route that cannot run, and saying so now is the difference
        // between a refusal and a job that dies partway.
        var canRun = route.All(capabilities.CanRun);
        var missing = route
            .Select(name => capabilities.Stages.FirstOrDefault(candidate => candidate.Stage == name))
            .SelectMany((found, index) => found is null
                ? [$"{route[index]} stage"]
                : found.Missing)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ModelGenerationReadiness(
            Installed: capabilities.Installed,
            Commissioned: capabilities.Commissioned,
            CanRun: canRun && capabilities.Commissioned,
            CompilerVersion: capabilities.Version,
            Checkout: capabilities.Checkout,
            Blender: capabilities.Blender,
            Missing: capabilities.Installed ? missing : ["compiler"],
            Detail: Explain(capabilities, canRun, route, work),
            Sizes: capabilities.Stages
                .FirstOrDefault(candidate => candidate.Stage == StageMeshStage)?.Sizes
                ?.Select(size => new ModelSizeChoice(size.Size, size.Description, size.Metres))
                .ToArray(),
            // What each stage writes, frozen into a request so a restart names
            // the same files and a finished step is recognised as finished.
            Suffixes: capabilities.Stages.ToDictionary(
                stage => stage.Stage, stage => stage.OutputSuffix, StringComparer.Ordinal),
            Colours: capabilities.Stages
                .FirstOrDefault(candidate => candidate.Stage == GlassStage)?.Colours
                ?.Select(colour => new ModelColourChoice(colour.Colour, colour.Description))
                .ToArray());
    }

    // The route is named in the artist's terms, because the two share this
    // compiler and this commissioning but are not the same offer, and "model
    // generation is ready" is the wrong sentence to read next to a button that
    // reduces something.
    private static string Explain(
        CompilerCapabilities capabilities, bool canRun, string[] route, string work)
    {
        if (!capabilities.Installed) return capabilities.Detail ?? "The Reference Asset Compiler is not available.";
        if (!canRun)
        {
            // Name the step that is the problem. "Something is missing" sends
            // an artist looking at the wrong half of an install.
            var blocked = route
                .Select(name => (Name: name, Stage: capabilities.Stages.FirstOrDefault(s => s.Stage == name)))
                .FirstOrDefault(step => step.Stage is null || !step.Stage.Available);
            if (blocked.Name is null) return "The compiler cannot run this route yet.";
            return blocked.Stage is null
                ? $"This compiler does not offer the {blocked.Name} stage."
                : $"The compiler cannot run the {blocked.Name} stage yet: {string.Join(", ", blocked.Stage.Missing)} missing.";
        }
        return capabilities.Commissioned
            ? $"Ready. {work} will run on this workstation."
            : "This route is not commissioned yet, so nothing will be submitted. Everything else about it is ready.";
    }

    /// <summary>
    /// Freezes the chosen image revision and queues the work. A capability that
    /// is missing blocks here, before anything is submitted, rather than an hour
    /// into a stage that was never going to run.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueAsync(
        CreateModelGenerationRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Image))
            return RepositoryResult<JobSummary>.Invalid("A model is generated from a reference image.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid("That reference is archived. Restore it before generating from it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("A generated model needs a name of 1 to 120 characters.");

        // A size is not optional and is not guessed. Every measurement the
        // reduction gate makes afterwards is taken against it, so a default
        // here would quietly turn a real verdict into a meaningless one.
        var size = (request.Size ?? "").Trim();
        if (size.Length == 0)
            return RepositoryResult<JobSummary>.Invalid(
                "Say roughly how big this is, as a height on a person — for example knee height.");
        if (request.SizeAdjust is { } adjust && (adjust < 0.25 || adjust > 4.0))
            return RepositoryResult<JobSummary>.Invalid(
                "A size adjustment that large means a different size entirely; pick the nearer one.");

        // Glass is opt-in and named, never inferred. An empty answer is the
        // ordinary one: most props have no glass at all.
        var glassColour = string.IsNullOrWhiteSpace(request.GlassColour) ? null : request.GlassColour.Trim();

        var readiness = await PreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);
        if (glassColour is not null
            && readiness.Colours is { Length: > 0 } offered
            && !offered.Any(colour => string.Equals(colour.Colour, glassColour, StringComparison.OrdinalIgnoreCase)))
            return RepositoryResult<JobSummary>.Invalid(
                $"The compiler does not offer {glassColour} as a glass colour. "
                + $"It offers: {string.Join(", ", offered.Select(colour => colour.Colour))}.");

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. RouteFor(glassColour).Select(stage => new RouteStep(
                stage,
                readiness.Suffixes?.GetValueOrDefault(stage) ?? ".glb"))],
            readiness.CompilerVersion, now, size, request.SizeAdjust ?? 1.0, glassColour,
            GenerateWork);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Model from reference",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen reference queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", ReferenceToModelRoute),
            RequestJson = requestJson,
            // The same frozen request queued twice is the same work, and the
            // job identity says so rather than two stages racing each other.
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Freezes an existing library model and queues its preparation.
    ///
    /// Nothing about the source is touched, here or later. What this produces
    /// is a new revision in the source's own stack, and the source stays the
    /// current one until a person says otherwise -- because a derivative
    /// nobody has looked at should not quietly become the thing every picker
    /// reaches for.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueuePreparationAsync(
        CreateModelPreparationRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("A derivative is prepared from a model.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid(
                "That model is archived. Restore it before preparing a derivative from it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("A prepared derivative needs a name of 1 to 120 characters.");

        var budget = request.TriangleBudget ?? DefaultPreparedTriangleBudget;
        // The compiler's reducer refuses a budget that does not reduce, and it
        // refuses one below a thousand. Saying so here means the artist hears
        // it in their own terms rather than as a stage that died.
        if (budget is < 1_000 or > 200_000)
            return RepositoryResult<JobSummary>.Invalid(
                "A runtime triangle budget is between 1,000 and 200,000.");

        var sourcePath = assets.StoredFilePath(source.StoragePath);
        if (sourcePath is null)
            return RepositoryResult<JobSummary>.Invalid("That model's file is missing from the asset root.");
        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        if (!inspection.Ok || inspection.Profile is null)
            return RepositoryResult<JobSummary>.Invalid(
                inspection.Error ?? "That model could not be read, so nothing can be prepared from it.");
        // A budget at or above what the source already has would ask the
        // reducer to grow a mesh, which it refuses. The number the artist
        // needs to hear is the one their model actually has.
        if (budget >= inspection.Profile.TriangleCount)
            return RepositoryResult<JobSummary>.Invalid(
                $"That model already has {inspection.Profile.TriangleCount:N0} triangles. "
                + "A derivative has to be smaller than its source; choose a lower budget.");

        var readiness = await PreparationPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. PreparationPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            // A preparation asks none of generation's questions: the size is
            // already the size, and the glass is already glass.
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: PrepareWork, TriangleBudget: budget);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Runtime derivative",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", PreparationPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Jobs a restart should pick up again. Their identity is unchanged, so a
    /// job that was running before the lights went out is the same job after.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking()
            .Where(job => job.WorkType == ModelWorkType
                && (job.State == "Queued" || job.State == "Running"))
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken);

    public async Task<RepositoryResult<JobSummary>> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(candidate => candidate.Id == jobId && candidate.WorkType == ModelWorkType, cancellationToken);
        if (job is null) return RepositoryResult<JobSummary>.NotFound();

        // A delivery that arrives twice does not produce two models. The job
        // already knows what it produced.
        if (job.OutputAssetId is not null)
        {
            job.DeliveryCount += 1;
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<JobSummary>.Ok(Map(job));
        }

        if (string.IsNullOrWhiteSpace(job.RequestJson))
            return await FailAsync(job, "This job no longer has its frozen request packet.", cancellationToken);
        FrozenModelRequest? packet;
        try { packet = JsonSerializer.Deserialize<FrozenModelRequest>(job.RequestJson, Json); }
        catch (JsonException) { packet = null; }
        if (packet is null || packet.PacketVersion != FrozenPacketVersion)
            return await FailAsync(job,
                "This job was frozen against an older route that no longer describes what it would do. "
                + "Ask for it again to queue it against the current one.", cancellationToken);
        if (packet.Route is not { Length: > 0 })
            return await FailAsync(job, "This job's frozen request names no stages to run.", cancellationToken);

        var preparing = string.Equals(packet.Work, PrepareWork, StringComparison.Ordinal);
        var sourceWord = preparing ? "model" : "reference";
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == packet.SourceAssetId, cancellationToken);
        if (source is null)
            return await FailAsync(job, $"The {sourceWord} this job was frozen against is no longer in the library.", cancellationToken);
        var sourcePath = assets.StoredFilePath(source.StoragePath);
        if (sourcePath is null)
            return await FailAsync(job, $"The {sourceWord} file is missing from the asset root.", cancellationToken);

        var workspace = Workspace(job.Id);
        Directory.CreateDirectory(workspace);

        // Percentages here are stage boundaries, not a clock. A bar that
        // interpolates against a guessed duration is a lie, and these stages'
        // durations are genuinely unknown.
        await ProgressAsync(job, JobState.Running, 10,
            preparing ? "Reading the frozen model" : "Reading the frozen reference", cancellationToken);

        // Each step reads what the one before it wrote, starting from the
        // frozen reference image. Every step keeps its own output and receipt,
        // which is what lets an interrupted job resume at the step it reached
        // instead of starting over -- and starting over here would mean asking
        // a GPU to build the same mesh a second time.
        var steps = packet.Route;
        var stepSource = sourcePath;
        var completed = new List<StepOutcome>(steps.Length);
        var rerunAfterPartial = false;

        for (var index = 0; index < steps.Length; index++)
        {
            var stage = steps[index].Stage;
            var outputPath = Path.Combine(workspace, $"step-{index + 1}-{stage}{steps[index].OutputSuffix}");
            var receiptPath = Path.Combine(workspace, $"step-{index + 1}-{stage}.json");
            var hasOutput = Produced(outputPath);
            var hasReceipt = File.Exists(receiptPath);
            // Each step gets an equal share of the span between reading the
            // reference and validating the result. Equal because the studio has
            // no honest basis for claiming one step is a given fraction of the
            // whole; a weighting invented here would be a guess wearing a number.
            var progress = 10 + (index * 70 / steps.Length);
            var describe = StagePhase.TryGetValue(stage, out var phrase) ? phrase : $"Running {stage}";
            var step = steps.Length > 1 ? $"Step {index + 1} of {steps.Length}: {describe}" : describe;

            CompilerStageRun run;
            if (hasOutput && hasReceipt)
            {
                // The ambiguous window: this job was interrupted after the step
                // wrote its answer but before the answer was recorded. Adopt
                // what is on disk rather than silently running it again.
                await ProgressAsync(job, JobState.Running, progress,
                    $"{step} — reconciling an interrupted run", cancellationToken);
                run = new CompilerStageRun(true, stage, 0, 0, outputPath,
                    await File.ReadAllTextAsync(receiptPath, cancellationToken), null,
                    ["Adopted an existing result."]);
                completed.Add(new StepOutcome(stage, outputPath, run.ReceiptJson, true, false));
            }
            else
            {
                // Half an answer is not an answer, and running a step again is
                // a decision rather than a detail: it is announced, the remains
                // of the interrupted attempt are cleared so they cannot later
                // be adopted as if they were whole, and it is recorded on the
                // delivery. Only this step is redone; steps already finished
                // keep their answers.
                var partial = hasOutput || hasReceipt;
                rerunAfterPartial |= partial;
                if (partial)
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    // The fixed views are a directory, and the compiler refuses
                    // to render into one that exists. Half of somebody's
                    // evidence is not evidence, so it goes with the receipt.
                    if (Directory.Exists(outputPath)) Directory.Delete(outputPath, recursive: true);
                    if (File.Exists(receiptPath)) File.Delete(receiptPath);
                }

                await ProgressAsync(job, JobState.Running, progress,
                    partial ? $"{step} — running again: the previous attempt left an incomplete answer" : step,
                    cancellationToken);
                run = await compiler.RunStageAsync(
                    stage, steps[index].ReadsOriginal ? sourcePath : stepSource,
                    outputPath, receiptPath, cancellationToken,
                    StageOptions(stage, packet, job, sourcePath));
                if (!run.Ok)
                    return await FailAsync(job,
                        run.Error ?? $"The {stage} stage did not produce a result.", cancellationToken);
                if (!Produced(outputPath))
                    return await FailAsync(job,
                        $"The {stage} stage reported success but wrote nothing.", cancellationToken);
                completed.Add(new StepOutcome(stage, outputPath, run.ReceiptJson, false, partial));
            }

            // A chain by default, because that is what almost every route is.
            // A step that says it reads the original keeps reading it, which is
            // how the source's own fixed views are views of the source rather
            // than of the previous step's evidence directory.
            stepSource = outputPath;
        }

        // The last step of a preparation is evidence, not a model. The model is
        // the last step that exported one.
        var payloadStep = completed.LastOrDefault(step => step.Stage == BrowserPayloadStage);
        if (payloadStep is null)
            return await FailAsync(job, "This job's route never exported a model.", cancellationToken);
        var payloadPath = payloadStep.OutputPath;
        await ProgressAsync(job, JobState.Running, 85, "Validating the candidate", cancellationToken);
        if (!File.Exists(payloadPath))
            return await FailAsync(job, "The compiler reported success but wrote no model.", cancellationToken);

        RepositoryResult<AssetSummary> imported;
        await using (var payload = File.OpenRead(payloadPath))
        {
            imported = await assets.ImportGeneratedModelAsync(
                payload, $"{job.ShotCode}.glb", payload.Length, cancellationToken,
                preparing ? "Prepared derivative" : "Generated model");
        }
        // An invalid model never becomes a usable one: the import validates the
        // container before it stores anything, and a refusal fails the job.
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
            return await FailAsync(job, imported.Error ?? "The generated model could not be validated.", cancellationToken);

        // The reference may have moved on while this ran. That does not spoil
        // the candidate; it makes it a candidate from an older source, and it
        // says so rather than implying it matches what is on screen now.
        var stale = !string.Equals(source.ContentHash, packet.SourceContentHash, StringComparison.OrdinalIgnoreCase);
        var topologyChanged = false;
        if (preparing)
        {
            // A derivative belongs in its source's own revision stack, citing
            // it, rather than arriving as an unrelated model that happens to
            // look similar.
            var stacked = await StackDerivativeAsync(
                source, imported.Value, packet, payloadPath, sourcePath, cancellationToken);
            if (stacked.Kind != RepositoryResultKind.Ok)
                return await FailAsync(job,
                    stacked.Error ?? "The derivative could not be recorded against its source.", cancellationToken);
            topologyChanged = stacked.Value;
        }
        await RecordLineageAsync(imported.Value.Id, packet, source, stale, cancellationToken);

        job.OutputAssetId = imported.Value.Id;
        job.DeliveryCount += 1;
        job.ResultJson = JsonSerializer.Serialize(new
        {
            sourceAssetId = packet.SourceAssetId,
            frozenSourceHash = packet.SourceContentHash,
            sourceHashAtDelivery = source.ContentHash,
            staleSource = stale,
            compilerVersion = packet.CompilerVersion,
            route = packet.Route.Select(step => step.Stage),
            work = packet.Work,
            size = packet.Size,
            sizeAdjust = packet.SizeAdjust,
            glassColour = packet.GlassColour,
            triangleBudget = packet.TriangleBudget == 0 ? (int?)null : packet.TriangleBudget,
            topologyChanged = preparing ? topologyChanged : (bool?)null,
            rerunAfterIncompleteAnswer = rerunAfterPartial,
            // Per step, because "the route succeeded" hides which half of it
            // actually ran on this attempt and which was picked up off disk.
            steps = completed.Select((step, index) => new
            {
                stage = step.Stage,
                // Named so evidence can be fetched later without a caller
                // having to know how a workspace lays its steps out.
                step = index + 1,
                adopted = step.Adopted,
                rerunAfterIncompleteAnswer = step.RerunAfterPartial,
                receiptSha256 = step.ReceiptJson is null ? null : Sha256(step.ReceiptJson),
            }).ToArray(),
            receiptSha256 = payloadStep.ReceiptJson is null ? null : Sha256(payloadStep.ReceiptJson!),
            payloadAssetId = imported.Value.Id,
            payloadContentHash = imported.Value.ContentHash,
        }, Json);
        await ProgressAsync(job, JobState.Completed, 100,
            stale
                ? $"Delivered from an older {sourceWord}"
                : preparing ? "Delivered, and waiting to be looked at" : "Delivered",
            cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// What each stage is told beyond its three paths.
    ///
    /// The compiler owns what a stage accepts and what each setting means.
    /// What belongs here is only what this studio knows and the compiler
    /// cannot: that it is a browser studio, and what the artist said.
    /// </summary>
    private static Dictionary<string, string> StageOptions(
        string stage, FrozenModelRequest packet, JobRecord job, string referencePath) => stage switch
    {
        GeometryStage => new()
        {
            ["octree-resolution"] = BrowserOctreeResolution,
            // Otherwise the workspace is named after the reference's content
            // hash, which is what the stored file is called.
            ["asset-name"] = job.ShotCode,
        },
        StageMeshStage => new()
        {
            ["size"] = packet.Size,
            ["size-adjust"] = packet.SizeAdjust.ToString(CultureInfo.InvariantCulture),
        },
        // Rebuilt on a uniform grid rather than collapsed: a generator's
        // surface has no topology worth preserving, and collapsing it keeps
        // the noise as slivers and spikes.
        RemeshStage => new() { ["triangle-budget"] = RuntimeTriangleBudget },
        // A generated prop is an approved static triangle mesh: it is unfolded
        // as it stands rather than welded or remeshed, which would change the
        // geometry the reduction gate already measured.
        UvUnwrapStage => new() { ["allow-triangulated-glb"] = "" },
        // The paint is conditioned on the same picture the geometry came from.
        TextureStage => new() { ["reference"] = referencePath },
        GlassStage => new() { ["colour"] = packet.GlassColour ?? "" },
        // A preparation exists to keep what a reviewed mesh already has, so a
        // mesh with no UV layer is refused by name rather than delivered as a
        // derivative nobody can paint.
        AdoptMeshStage => new() { ["require-uvs"] = "" },
        // The collapse, judged as a derivative of something already reviewed
        // rather than as a candidate production authority. The compiler owns
        // what that changes and records every allowance it makes.
        ReduceMeshStage => new()
        {
            ["triangle-budget"] = packet.TriangleBudget.ToString(CultureInfo.InvariantCulture),
            ["runtime-derivative"] = "",
        },
        _ => [],
    };

    /// <summary>
    /// Puts a delivered derivative in its source's own revision stack, and
    /// leaves the source the current one.
    ///
    /// The last part is the point. A revision added to a stack ordinarily
    /// becomes the current one, which is right when a person chose it and
    /// wrong here: nobody has looked at this yet. Until somebody does, every
    /// scene and every picker should go on reaching for the mesh that was
    /// reviewed, and the derivative should sit beside it waiting.
    ///
    /// Returns whether the derivative's topology differs from its source's.
    /// </summary>
    private async Task<RepositoryResult<bool>> StackDerivativeAsync(
        AssetRecord source, AssetSummary derivative, FrozenModelRequest packet,
        string derivativePath, string sourcePath, CancellationToken cancellationToken)
    {
        var before = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        var after = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(derivativePath, cancellationToken));
        var changed = before.Profile is null || after.Profile is null
            || before.Profile.TriangleCount != after.Profile.TriangleCount
            || before.Profile.VertexCount != after.Profile.VertexCount;

        var note = new StringBuilder()
            .Append("Runtime derivative of ").Append(packet.SourceName)
            .Append(" revision ").Append(packet.SourceRevisionNumber)
            .Append(" (").Append(packet.SourceContentHash[..12]).Append("…)");
        if (before.Profile is not null && after.Profile is not null)
            note.Append(": ").Append(before.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" triangles reduced to ")
                .Append(after.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture));
        note.Append('.');

        var added = await assets.AddRevisionAsync(source.Id,
            new AddAssetRevisionRequest(derivative.Id, note.ToString(),
                packet.CompilerVersion is null
                    ? "Reference Asset Compiler"
                    : $"Reference Asset Compiler {packet.CompilerVersion}"),
            cancellationToken);
        if (added.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<bool>.Invalid(added.Error ?? "The derivative could not join its source's revisions.");

        var stack = await db.Assets
            .Where(asset => asset.RevisionFamilyId != null
                && asset.RevisionFamilyId == db.Assets
                    .Where(parent => parent.Id == source.Id)
                    .Select(parent => parent.RevisionFamilyId).FirstOrDefault())
            .ToListAsync(cancellationToken);
        foreach (var member in stack) member.IsCurrentRevision = member.Id == source.Id;

        var stored = stack.SingleOrDefault(asset => asset.Id == derivative.Id);
        if (stored is not null)
        {
            stored.PreparationTopologyChanged = changed;
            // Unaccepted, explicitly. Nothing copies the source's acceptance
            // across, and a topology that moved could not carry it anyway.
            stored.PreparationAcceptedAt = null;
            stored.PreparationAcceptedBy = "";
            stored.PreparationAcceptanceNote = "";
        }
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(changed);
    }

    /// <summary>
    /// Where this model came from, written where an artist will read it rather
    /// than only in a job row.
    /// </summary>
    private async Task RecordLineageAsync(
        Guid assetId, FrozenModelRequest packet, AssetRecord source, bool stale, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);
        if (asset is null) return;
        var preparing = string.Equals(packet.Work, PrepareWork, StringComparison.Ordinal);
        var lineage = new StringBuilder()
            .Append(preparing ? "Prepared for runtime from " : "Generated from ").Append(packet.SourceName)
            .Append(" revision ").Append(packet.SourceRevisionNumber)
            .Append(" (").Append(packet.SourceContentHash[..12]).Append("…)")
            .Append(" by the Reference Asset Compiler")
            .Append(packet.CompilerVersion is null ? "" : " " + packet.CompilerVersion)
            .Append('.');
        if (stale)
            lineage.Append(" That reference has changed since; this model is a candidate from the older source (now ")
                .Append(source.ContentHash[..12]).Append("…).");
        asset.Notes = string.IsNullOrWhiteSpace(asset.Notes) ? lineage.ToString() : asset.Notes + "\n" + lineage;
        // A generated model's revision note is this sentence, because nothing
        // else has written one. A prepared derivative already has a better one --
        // it names what the reduction cost, in triangles -- and overwriting it
        // here threw that away.
        if (!preparing) asset.RevisionPrompt = lineage.ToString();
        asset.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ProgressAsync(
        JobRecord job, JobState state, int progress, string phase, CancellationToken cancellationToken)
    {
        job.State = state.ToString();
        job.Progress = progress;
        job.Phase = phase;
        job.LastHeartbeatAt = timeProvider.GetUtcNow();
        if (state == JobState.Completed) job.CompletedAt = job.LastHeartbeatAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<RepositoryResult<JobSummary>> FailAsync(
        JobRecord job, string error, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString();
        job.Phase = "Stopped";
        job.Error = error;
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastHeartbeatAt = job.CompletedAt;
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// The fixed views this job rendered, of the source and of the derivative,
    /// so a person can put them side by side.
    ///
    /// Comparing the two is the review. A derivative's views on their own show
    /// only that something rendered, which is why both are kept and why each
    /// manifest carries the hash of the exact bytes it is a picture of.
    /// </summary>
    public async Task<RepositoryResult<ModelPreparationEvidence>> PreparationEvidenceAsync(
        Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId && candidate.WorkType == ModelWorkType,
                cancellationToken);
        if (job is null) return RepositoryResult<ModelPreparationEvidence>.NotFound();
        if (string.IsNullOrWhiteSpace(job.RequestJson))
            return RepositoryResult<ModelPreparationEvidence>.Invalid("This job no longer has its frozen request packet.");

        FrozenModelRequest? packet;
        try { packet = JsonSerializer.Deserialize<FrozenModelRequest>(job.RequestJson, Json); }
        catch (JsonException) { packet = null; }
        if (packet is null || !string.Equals(packet.Work, PrepareWork, StringComparison.Ordinal))
            return RepositoryResult<ModelPreparationEvidence>.Invalid("This job is not a preparation.");

        var workspace = Workspace(job.Id);
        ModelPreparationViews? source = null;
        ModelPreparationViews? derivative = null;
        for (var index = 0; index < packet.Route.Length; index++)
        {
            if (packet.Route[index].Stage != ReviewViewsStage) continue;
            var read = ReadViews(job.Id, workspace, index + 1);
            if (read is null) continue;
            // The first views stage reads the original; the second reads what
            // the route produced. Which is which is the route's own answer,
            // not a guess from the order they happen to appear in.
            if (packet.Route[index].ReadsOriginal) source ??= read;
            else derivative ??= read;
        }
        return RepositoryResult<ModelPreparationEvidence>.Ok(new ModelPreparationEvidence(
            job.Id, packet.SourceAssetId, job.OutputAssetId, source, derivative));
    }

    private static ModelPreparationViews? ReadViews(Guid jobId, string workspace, int step)
    {
        var manifest = Path.Combine(workspace, $"step-{step}-{ReviewViewsStage}", "views.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var views = root.TryGetProperty("views", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray()
                    .Select(view => new ModelPreparationView(
                        view.GetProperty("view").GetString() ?? "",
                        view.GetProperty("pass").GetString() ?? "",
                        $"/api/jobs/{jobId}/preparation-views/{step}/{view.GetProperty("file").GetString()}",
                        view.TryGetProperty("sha256", out var hash) ? hash.GetString() ?? "" : ""))
                    .ToArray()
                : [];
            return new ModelPreparationViews(
                $"step-{step}-{ReviewViewsStage}",
                root.TryGetProperty("source_sha256", out var of) ? of.GetString() ?? "" : "",
                views);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One rendered view, read back from the workspace that produced it.
    ///
    /// The file name is checked against the manifest rather than against the
    /// filesystem: a caller asking for a name the manifest does not list is
    /// asking for something that is not evidence, whatever is on disk.
    /// </summary>
    public async Task<RepositoryResult<(string Path, string ContentType)>> PreparationViewFileAsync(
        Guid jobId, int step, string file, CancellationToken cancellationToken)
    {
        var evidence = await PreparationEvidenceAsync(jobId, cancellationToken);
        if (evidence.Kind != RepositoryResultKind.Ok || evidence.Value is null)
            return RepositoryResult<(string, string)>.NotFound();
        var wanted = $"/api/jobs/{jobId}/preparation-views/{step}/{file}";
        var listed = (evidence.Value.Source?.Views ?? [])
            .Concat(evidence.Value.Derivative?.Views ?? [])
            .Any(view => string.Equals(view.Url, wanted, StringComparison.Ordinal));
        if (!listed) return RepositoryResult<(string, string)>.NotFound();

        var path = Path.Combine(Workspace(jobId), $"step-{step}-{ReviewViewsStage}", file);
        return File.Exists(path)
            ? RepositoryResult<(string, string)>.Ok((path, "image/png"))
            : RepositoryResult<(string, string)>.Unavailable("That view is no longer on disk.");
    }

    /// <summary>
    /// One durable directory per job, so an interrupted run leaves its answer
    /// where the same job will look for it.
    /// </summary>
    private string Workspace(Guid jobId) => Path.Combine(
        StudioPaths.ResolveDataRoot(configuration, environment), "generated-models", jobId.ToString("N"));

    private static string IdempotencyKey(string requestJson, Guid jobId) =>
        Sha256($"model:{requestJson}:{jobId}");

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JobSummary Map(JobRecord job) => new(
        job.Id, job.ShotId, job.ShotCode, job.Kind,
        Enum.TryParse<JobState>(job.State, out var state) ? state : JobState.Queued,
        job.Progress, job.Phase, job.Backend, job.CreatedAt, job.CompletedAt, job.Error,
        job.ManifestId, job.AdapterId, job.OutputAssetId,
        job.OutputAssetId is null ? null : $"/api/assets/{job.OutputAssetId}/content",
        job.ProviderRequestId, job.Attempt, job.RetryOfJobId, job.LastHeartbeatAt, job.WorkType);
}
