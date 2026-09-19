using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Globalization;

namespace StoryboardStudio.Api.Tests;

public sealed class GenerationJobLeaseServiceTests
{
    [Fact]
    public async Task TwoWorkerHostsSharingOneLedgerCannotClaimTheSameActiveLease()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-two-workers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "queue.db");
        var projectId = Guid.NewGuid();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-30T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            await using var workerA = CreateFileContext(databasePath, projectId);
            await workerA.Database.EnsureCreatedAsync();
            var job = NewSharedJob(projectId, DateTimeOffset.Parse("2026-08-30T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            workerA.Jobs.Add(job);
            await workerA.SaveChangesAsync();

            await using var workerB = CreateFileContext(databasePath, projectId);
            var leaseA = await new GenerationJobLeaseService(workerA, clock)
                .ClaimNextAsync("desktop-host-a", CancellationToken.None);
            var leaseB = await new GenerationJobLeaseService(workerB, clock)
                .ClaimNextAsync("desktop-host-b", CancellationToken.None);

            Assert.NotNull(leaseA);
            Assert.Equal(job.Id, leaseA!.JobId);
            Assert.Null(leaseB);

            workerA.ChangeTracker.Clear();
            var stored = await workerA.Jobs.IgnoreQueryFilters().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(JobState.Running.ToString(), stored.State);
            Assert.Equal("desktop-host-a", stored.LeaseOwner);
            Assert.Equal(1, stored.DeliveryCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaimNextIsFifoAndNeverReturnsAnActivelyLeasedJobTwice()
    {
        await using var fixture = await LeaseFixture.CreateAsync();
        var first = fixture.NewJob(DateTimeOffset.Parse("2026-08-30T12:00:00Z", CultureInfo.InvariantCulture));
        var second = fixture.NewJob(DateTimeOffset.Parse("2026-08-30T12:00:01Z", CultureInfo.InvariantCulture));
        fixture.Db.Jobs.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();

        var service = new GenerationJobLeaseService(fixture.Db, fixture.Clock);
        var claimedFirst = await service.ClaimNextAsync("worker-a", CancellationToken.None);
        var claimedSecond = await service.ClaimNextAsync("worker-b", CancellationToken.None);
        var exhausted = await service.ClaimNextAsync("worker-c", CancellationToken.None);

        Assert.NotNull(claimedFirst);
        Assert.NotNull(claimedSecond);
        Assert.Equal(first.Id, claimedFirst!.JobId);
        Assert.Equal(second.Id, claimedSecond!.JobId);
        Assert.Null(exhausted);

        await fixture.Db.Entry(first).ReloadAsync();
        await fixture.Db.Entry(second).ReloadAsync();
        Assert.Equal("worker-a", first.LeaseOwner);
        Assert.Equal("worker-b", second.LeaseOwner);
        Assert.Equal(1, first.DeliveryCount);
        Assert.Equal(1, second.DeliveryCount);
    }

    [Fact]
    public async Task ClaimNextReclaimsOnlyAnExpiredLease()
    {
        await using var fixture = await LeaseFixture.CreateAsync();
        var expired = fixture.NewJob(DateTimeOffset.Parse("2026-08-30T12:00:00Z", CultureInfo.InvariantCulture));
        expired.State = JobState.Running.ToString();
        expired.LeaseOwner = "dead-worker";
        expired.LeaseExpiresAt = fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(1);
        fixture.Db.Jobs.Add(expired);
        await fixture.Db.SaveChangesAsync();

        var service = new GenerationJobLeaseService(fixture.Db, fixture.Clock);
        var reclaimed = await service.ClaimNextAsync("recovery-worker", CancellationToken.None);

        Assert.NotNull(reclaimed);
        Assert.Equal(expired.Id, reclaimed!.JobId);
        await fixture.Db.Entry(expired).ReloadAsync();
        Assert.Equal("recovery-worker", expired.LeaseOwner);
        Assert.Equal(1, expired.DeliveryCount);
    }

    [Fact]
    public async Task FailEscapedWorkerErrorMakesTheFailureVisibleAndRequiresExplicitRetry()
    {
        await using var fixture = await LeaseFixture.CreateAsync();
        var job = fixture.NewJob(DateTimeOffset.Parse("2026-08-30T12:00:00Z", CultureInfo.InvariantCulture));
        fixture.Db.Jobs.Add(job);
        await fixture.Db.SaveChangesAsync();
        var service = new GenerationJobLeaseService(fixture.Db, fixture.Clock);
        var lease = await service.ClaimNextAsync("worker-a", CancellationToken.None);

        await service.FailEscapedWorkerErrorAsync(
            lease!,
            new InvalidOperationException("the clockwork owl misplaced a cog"),
            CancellationToken.None);

        await fixture.Db.Entry(job).ReloadAsync();
        Assert.Equal(JobState.Failed.ToString(), job.State);
        Assert.Equal(100, job.Progress);
        Assert.Contains("clockwork owl", job.Error);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
    }

    private sealed class LeaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly Guid projectId;

        private LeaseFixture(
            SqliteConnection connection,
            StudioDbContext db,
            FixedTimeProvider clock,
            Guid projectId)
        {
            this.connection = connection;
            this.projectId = projectId;
            Db = db;
            Clock = clock;
        }

        public StudioDbContext Db { get; }
        public FixedTimeProvider Clock { get; }

        public static async Task<LeaseFixture> CreateAsync()
        {
            var projectId = Guid.NewGuid();
            var registry = new ActiveProjectRegistry();
            registry.Set(projectId);
            var scope = new ProjectScope(registry);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<StudioDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new StudioDbContext(options, scope);
            await db.Database.EnsureCreatedAsync();
            var fixture = new LeaseFixture(
                connection,
                db,
                new FixedTimeProvider(DateTimeOffset.Parse("2026-08-30T13:00:00Z", CultureInfo.InvariantCulture)),
                projectId);
            return fixture;
        }

        public JobRecord NewJob(DateTimeOffset createdAt) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ShotId = Guid.NewGuid(),
            ShotCode = "SH-QUEUE",
            Kind = "Draft frame",
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Queued",
            Backend = "Proof",
            CreatedAt = createdAt,
            LastHeartbeatAt = createdAt,
            WorkType = "Shot"
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static StudioDbContext CreateFileContext(string databasePath, Guid projectId)
    {
        var registry = new ActiveProjectRegistry();
        registry.Set(projectId);
        var scope = new ProjectScope(registry);
        var options = new DbContextOptionsBuilder<StudioDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new StudioDbContext(options, scope);
    }

    private static JobRecord NewSharedJob(Guid projectId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ShotId = Guid.NewGuid(),
        ShotCode = "SH-TWO-WORKERS",
        Kind = "Draft frame",
        State = JobState.Queued.ToString(),
        Progress = 0,
        Phase = "Queued",
        Backend = "Proof",
        CreatedAt = createdAt,
        LastHeartbeatAt = createdAt,
        WorkType = "Shot"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
