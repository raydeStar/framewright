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
    public const string BrowserPayloadStage = "browser-payload";
    private const int FrozenPacketVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What the request froze. Stored on the job so a restart, a retry, or an
    /// argument six weeks later all read the same answer: these bytes, that
    /// stage, this compiler.
    /// </summary>
    private sealed record FrozenModelRequest(
        int PacketVersion, Guid SourceAssetId, string SourceContentHash, string SourceName,
        int SourceRevisionNumber, string Stage, string? CompilerVersion, DateTimeOffset FrozenAt);

    /// <summary>Can this workstation do the work, and is it allowed to?</summary>
    public async Task<ModelGenerationReadiness> PreflightAsync(CancellationToken cancellationToken)
    {
        var capabilities = await compiler.DescribeAsync(cancellationToken);
        var canRun = capabilities.CanRun(BrowserPayloadStage);
        var stage = capabilities.Stages.FirstOrDefault(candidate => candidate.Stage == BrowserPayloadStage);
        return new ModelGenerationReadiness(
            Installed: capabilities.Installed,
            Commissioned: capabilities.Commissioned,
            CanRun: canRun && capabilities.Commissioned,
            CompilerVersion: capabilities.Version,
            Checkout: capabilities.Checkout,
            Blender: capabilities.Blender,
            Missing: stage?.Missing ?? (capabilities.Installed ? [] : ["compiler"]),
            Detail: Explain(capabilities, canRun));
    }

    private static string Explain(CompilerCapabilities capabilities, bool canRun)
    {
        if (!capabilities.Installed) return capabilities.Detail ?? "The Reference Asset Compiler is not available.";
        if (!canRun)
        {
            var stage = capabilities.Stages.FirstOrDefault(candidate => candidate.Stage == BrowserPayloadStage);
            return stage is null
                ? "This compiler does not offer the browser payload stage."
                : $"The compiler cannot run that stage yet: {string.Join(", ", stage.Missing)} missing.";
        }
        return capabilities.Commissioned
            ? "Ready. Model generation will run on this workstation."
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

        var readiness = await PreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1, BrowserPayloadStage, readiness.CompilerVersion, now);
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
            AdapterId = BrowserPayloadStage,
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
            return await FailAsync(job, "This job's frozen request packet cannot be read by this build.", cancellationToken);

        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == packet.SourceAssetId, cancellationToken);
        if (source is null)
            return await FailAsync(job, "The reference this job was frozen against is no longer in the library.", cancellationToken);
        var sourcePath = assets.StoredFilePath(source.StoragePath);
        if (sourcePath is null)
            return await FailAsync(job, "The reference file is missing from the asset root.", cancellationToken);

        var workspace = Workspace(job.Id);
        Directory.CreateDirectory(workspace);
        var payloadPath = Path.Combine(workspace, "payload.glb");
        var receiptPath = Path.Combine(workspace, "receipt.json");

        // Percentages here are stage boundaries, not a clock. A bar that
        // interpolates against a guessed duration is a lie, and this stage's
        // duration is genuinely unknown.
        await ProgressAsync(job, JobState.Running, 10, "Reading the frozen reference", cancellationToken);

        CompilerStageRun run;
        var hasPayload = File.Exists(payloadPath);
        var hasReceipt = File.Exists(receiptPath);
        var rerunAfterPartial = false;
        if (hasPayload && hasReceipt)
        {
            // The ambiguous window: this job was interrupted after the stage
            // wrote its answer but before the answer was recorded. Adopt what
            // is already on disk rather than silently running it again.
            await ProgressAsync(job, JobState.Running, 70, "Reconciling an interrupted run", cancellationToken);
            run = new CompilerStageRun(true, packet.Stage, 0, 0, payloadPath,
                await File.ReadAllTextAsync(receiptPath, cancellationToken), null, ["Adopted an existing payload."]);
        }
        else
        {
            // Half an answer is not an answer, and running the stage again is a
            // decision rather than a detail: it is announced, the remains of the
            // interrupted attempt are cleared so they cannot be adopted later as
            // if they were whole, and the rerun is recorded on the delivery.
            rerunAfterPartial = hasPayload || hasReceipt;
            if (rerunAfterPartial)
            {
                foreach (var leftover in (string[])[payloadPath, receiptPath])
                    if (File.Exists(leftover)) File.Delete(leftover);
            }

            await ProgressAsync(job, JobState.Running, 40,
                rerunAfterPartial
                    ? "Running again: the previous attempt left an incomplete answer"
                    : "Compiling the model",
                cancellationToken);
            run = await compiler.RunStageAsync(packet.Stage, sourcePath, payloadPath, receiptPath, cancellationToken);
            if (!run.Ok)
                return await FailAsync(job, run.Error ?? "The compiler stage did not produce a model.", cancellationToken);
        }

        await ProgressAsync(job, JobState.Running, 85, "Validating the candidate", cancellationToken);
        if (!File.Exists(payloadPath))
            return await FailAsync(job, "The compiler reported success but wrote no model.", cancellationToken);

        RepositoryResult<AssetSummary> imported;
        await using (var payload = File.OpenRead(payloadPath))
        {
            imported = await assets.ImportGeneratedModelAsync(
                payload, $"{job.ShotCode}.glb", payload.Length, cancellationToken, "Generated model");
        }
        // An invalid model never becomes a usable one: the import validates the
        // container before it stores anything, and a refusal fails the job.
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
            return await FailAsync(job, imported.Error ?? "The generated model could not be validated.", cancellationToken);

        // The reference may have moved on while this ran. That does not spoil
        // the candidate; it makes it a candidate from an older source, and it
        // says so rather than implying it matches what is on screen now.
        var stale = !string.Equals(source.ContentHash, packet.SourceContentHash, StringComparison.OrdinalIgnoreCase);
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
            stage = packet.Stage,
            rerunAfterIncompleteAnswer = rerunAfterPartial,
            receiptSha256 = run.ReceiptJson is null ? null : Sha256(run.ReceiptJson),
            payloadAssetId = imported.Value.Id,
            payloadContentHash = imported.Value.ContentHash,
        }, Json);
        await ProgressAsync(job, JobState.Completed, 100,
            stale ? "Delivered from an older reference" : "Delivered", cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
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
        var lineage = new StringBuilder()
            .Append("Generated from ").Append(packet.SourceName)
            .Append(" revision ").Append(packet.SourceRevisionNumber)
            .Append(" (").Append(packet.SourceContentHash[..12]).Append("…)")
            .Append(" by the Reference Asset Compiler")
            .Append(packet.CompilerVersion is null ? "" : " " + packet.CompilerVersion)
            .Append('.');
        if (stale)
            lineage.Append(" That reference has changed since; this model is a candidate from the older source (now ")
                .Append(source.ContentHash[..12]).Append("…).");
        asset.Notes = string.IsNullOrWhiteSpace(asset.Notes) ? lineage.ToString() : asset.Notes + "\n" + lineage;
        asset.RevisionPrompt = lineage.ToString();
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
