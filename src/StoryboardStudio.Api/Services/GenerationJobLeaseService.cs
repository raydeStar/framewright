using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

public sealed record GenerationJobLease(
    Guid JobId,
    Guid ProjectId,
    string WorkType,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt);

public sealed record GenerationQueueStatus(
    int Queued,
    int Running,
    int Failed,
    DateTimeOffset? OldestQueuedAt,
    DateTimeOffset CheckedAt);

/// <summary>
/// Claims persisted generation work with one atomic SQLite statement. The
/// in-memory channel is merely a wake-up bell; this ledger remains authoritative
/// across browser navigation, process restarts, and accidental duplicate wakeups.
/// </summary>
public sealed class GenerationJobLeaseService(
    StudioDbContext db,
    TimeProvider timeProvider)
{
    // Short enough for another workstation process to recover an abandoned job,
    // long enough to survive ordinary scheduling pauses. The worker renews this
    // lease every minute while provider work is active.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<GenerationJobLease?> ClaimNextAsync(
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var now = timeProvider.GetUtcNow();
        var expires = now + LeaseDuration;
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Jobs"
            SET "State" = 'Running',
                "LeaseOwner" = $owner,
                "LeaseExpiresAt" = $expires,
                "LastHeartbeatAt" = $now,
                "DeliveryCount" = "DeliveryCount" + 1
            WHERE "Id" = (
                SELECT "Id"
                FROM "Jobs"
                WHERE (
                    "State" = 'Queued'
                    AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= $now)
                ) OR (
                    "State" = 'Running'
                    AND "LeaseExpiresAt" IS NOT NULL
                    AND "LeaseExpiresAt" <= $now
                )
                ORDER BY "CreatedAt", "Id"
                LIMIT 1
            )
            AND (
                ("State" = 'Queued' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= $now))
                OR ("State" = 'Running' AND "LeaseExpiresAt" IS NOT NULL AND "LeaseExpiresAt" <= $now)
            )
            RETURNING "Id", "ProjectId", "WorkType";
            """;
        AddParameter(command, "$owner", leaseOwner);
        AddParameter(command, "$expires", expires);
        AddParameter(command, "$now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            leaseOwner,
            expires);
    }

    public async Task ExtendAsync(
        GenerationJobLease lease,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.Id == lease.JobId
                && job.State == JobState.Running.ToString()
                && job.LeaseOwner == lease.LeaseOwner)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.LeaseExpiresAt, now + LeaseDuration)
                .SetProperty(job => job.LastHeartbeatAt, now), cancellationToken);
    }

    public async Task ReleaseAsync(
        GenerationJobLease lease,
        CancellationToken cancellationToken)
    {
        await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.Id == lease.JobId && job.LeaseOwner == lease.LeaseOwner)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task FailEscapedWorkerErrorAsync(
        GenerationJobLease lease,
        Exception error,
        CancellationToken cancellationToken)
    {
        var message = $"The durable worker stopped outside the provider adapter boundary: {error.Message}";
        if (message.Length > 1_000)
        {
            message = message[..1_000];
        }

        var now = timeProvider.GetUtcNow();
        await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.Id == lease.JobId && job.LeaseOwner == lease.LeaseOwner)
            .ExecuteUpdateAsync(update => update
                .SetProperty(job => job.State, JobState.Failed.ToString())
                .SetProperty(job => job.Progress, 100)
                .SetProperty(job => job.Phase, "Worker failed; explicit retry required")
                .SetProperty(job => job.Error, message)
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.LastHeartbeatAt, now)
                .SetProperty(job => job.LeaseOwner, (string?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken);
    }

    public async Task<GenerationQueueStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var jobs = db.Jobs.IgnoreQueryFilters().AsNoTracking();
        var queued = await jobs.CountAsync(job => job.State == JobState.Queued.ToString(), cancellationToken);
        var running = await jobs.CountAsync(job => job.State == JobState.Running.ToString(), cancellationToken);
        var failed = await jobs.CountAsync(job => job.State == JobState.Failed.ToString(), cancellationToken);
        var queuedAt = await jobs
            .Where(job => job.State == JobState.Queued.ToString())
            .Select(job => job.CreatedAt)
            .ToListAsync(cancellationToken);
        var oldest = queuedAt.Count == 0 ? (DateTimeOffset?)null : queuedAt.Min();
        return new(queued, running, failed, oldest, timeProvider.GetUtcNow());
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
