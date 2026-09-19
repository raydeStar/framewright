using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;

namespace StoryboardStudio.Api.Services;

public sealed record RuntimeReadinessCheck(
    string Id,
    string State,
    string Detail,
    bool Required);

public sealed record RuntimeReadinessSummary(
    string Status,
    IReadOnlyList<RuntimeReadinessCheck> Checks,
    GenerationQueueStatus Queue,
    DateTimeOffset CheckedAt,
    BuildIdentity Build);

/// <summary>
/// Answers whether Framewright can safely accept artist work. Provider reachability
/// remains integration telemetry rather than process health: an offline GPU must
/// not make the local library disappear, while an unwritable database or invalid
/// packaged workflow absolutely must stop new submissions.
/// </summary>
public sealed class RuntimeReadinessService(
    StudioDbContext db,
    GenerationJobLeaseService queue,
    WorkflowLibrary workflows,
    BuildIdentityService buildIdentity,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Enforces the required runtime contract at every durable generation
    /// boundary. The status panel is useful evidence, but it is not a lock:
    /// callers must invoke this immediately before enqueue and again in the
    /// worker before crossing a provider boundary. That second check covers a
    /// disk becoming read-only while a job waited in the durable queue.
    /// </summary>
    public async Task<RepositoryResult<bool>> AdmitGenerationAsync(CancellationToken cancellationToken)
    {
        var readiness = await InspectAsync(cancellationToken);
        var blockers = readiness.Checks
            .Where(check => check.Required && check.State == "Failed")
            .Select(check => $"{check.Id}: {check.Detail}")
            .ToArray();
        return blockers.Length == 0
            ? RepositoryResult<bool>.Ok(true)
            : RepositoryResult<bool>.Unavailable(
                "Generation is temporarily unavailable because required local storage or workflow checks failed. " +
                string.Join(" ", blockers));
    }

    public async Task<RuntimeReadinessSummary> InspectAsync(CancellationToken cancellationToken)
    {
        var checks = new List<RuntimeReadinessCheck>();
        await InspectDatabaseAsync(checks, cancellationToken);

        var dataRoot = StudioPaths.ResolveDataRoot(configuration, environment);
        AddPathCheck(checks, "data-root", dataRoot, required: true);
        AddPathCheck(checks, "asset-root", StudioPaths.ResolveAssetRoot(configuration, environment), required: true);

        IReadOnlyList<WorkflowValidation> workflowChecks;
        try
        {
            workflowChecks = workflows.ValidateAll();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        {
            workflowChecks = [];
            checks.Add(new("workflow-library", "Failed", $"Workflow validation could not run: {error.Message}", true));
        }
        // The validation exception above already recorded the one canonical
        // workflow check; do not add a contradictory Ready entry beneath it.
        if (checks.All(check => check.Id != "workflow-library"))
        {
            if (workflowChecks.Count == 0)
            {
                checks.Add(new("workflow-library", "Failed", $"No workflows were found at {workflows.IndexPath}.", true));
            }
            else
            {
                var invalid = workflowChecks.Where(workflow => !workflow.Valid).ToArray();
                checks.Add(invalid.Length == 0
                    ? new("workflow-library", "Ready", $"{workflowChecks.Count} packaged workflows passed structural validation.", true)
                    : new("workflow-library", "Failed", string.Join(" ", invalid.Select(workflow =>
                        $"{workflow.Name}: {string.Join("; ", workflow.Problems)}")), true));
            }
        }

        if (configuration.GetValue("Studio:Backups:Enabled", false))
        {
            var configured = configuration["Studio:Backups:Root"];
            var backupRoot = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(dataRoot, "backups")
                : Path.GetFullPath(configured);
            AddPathCheck(checks, "backup-root", backupRoot, required: true);
        }
        else
        {
            checks.Add(new("backup-root", "Disabled", "Scheduled backups are disabled for this runtime.", false));
        }

        var queueStatus = await queue.StatusAsync(cancellationToken);
        var staleBefore = timeProvider.GetUtcNow() - TimeSpan.FromHours(5);
        var runningHeartbeats = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .Where(job => job.State == "Running")
            .Select(job => job.LastHeartbeatAt)
            .ToListAsync(cancellationToken);
        var stale = runningHeartbeats.Count(heartbeat => heartbeat is not null && heartbeat < staleBefore);
        checks.Add(stale == 0
            ? new("generation-worker", "Ready", $"{queueStatus.Running} running and {queueStatus.Queued} queued generation jobs.", true)
            : new("generation-worker", "Failed", $"{stale} generation job(s) have not reported progress for more than five hours.", true));

        var requiredFailure = checks.Any(check => check.Required && check.State == "Failed");
        var degraded = checks.Any(check => check.State is "Failed" or "Degraded" or "Disabled");
        return new(
            requiredFailure ? "NotReady" : degraded ? "Degraded" : "Ready",
            checks,
            queueStatus,
            timeProvider.GetUtcNow(),
            buildIdentity.Current);
    }

    private async Task InspectDatabaseAsync(
        List<RuntimeReadinessCheck> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                checks.Add(new("database", "Failed", "SQLite could not be opened.", true));
                return;
            }

            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM "SchemaMigrations"
                WHERE "Id" IN (
                    '20260830-production-baseline-v1',
                    '20260830-durable-generation-v2',
                    '20260830-audio-generation-v3',
                    '20260830-production-video-binding-v4',
                    '20260830-ratified-shot-intent-v5'
                );
                """;
            var migrationCount = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            checks.Add(migrationCount == 5
                ? new("database", "Ready", "SQLite is reachable and all production schema migrations are recorded.", true)
                : new("database", "Failed", "SQLite opened, but one or more production schema migrations are not recorded.", true));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Data.Common.DbException or IOException)
        {
            checks.Add(new("database", "Failed", $"SQLite readiness failed: {error.Message}", true));
        }
    }

    private static void AddPathCheck(
        List<RuntimeReadinessCheck> checks,
        string id,
        string path,
        bool required)
    {
        var result = RuntimePathPreflight.InspectWritableDirectory(path);
        checks.Add(new(id, result.Writable ? "Ready" : "Failed", result.Detail, required));
    }
}
