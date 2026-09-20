using Microsoft.Data.Sqlite;
using StoryboardStudio.Api.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task ReadinessFailsWhenTheLatestProductionMigrationIsNotRecorded()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-schema-readiness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");

        try
        {
            using var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var removeLatestLedgerEntry = connection.CreateCommand();
                removeLatestLedgerEntry.CommandText = "DELETE FROM \"SchemaMigrations\" WHERE \"Id\" = '20260830-ratified-shot-intent-v5';";
                Assert.Equal(1, await removeLatestLedgerEntry.ExecuteNonQueryAsync());
            }

            var response = await client.GetAsync("/health/ready");
            var readiness = await response.Content.ReadFromJsonAsync<RuntimeReadinessSummary>();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("NotReady", readiness!.Status);
            var database = Assert.Single(readiness.Checks, check => check.Id == "database");
            Assert.Equal("Failed", database.State);
            Assert.Contains("schema migrations", database.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DurableGenerationV2UpgradesADatabaseThatAlreadyRecordedV1()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-schema-upgrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");

        try
        {
            using (var baselineFactory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var baselineClient = baselineFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await baselineClient.GetAsync("/health/ready")).StatusCode);
            }

            await using (var legacy = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var downgrade = legacy.CreateCommand();
                downgrade.CommandText = """
                    DROP INDEX IF EXISTS "UX_Jobs_ActiveIdempotencyKey";
                    DROP INDEX IF EXISTS "IX_Jobs_IdempotencyKey";
                    DROP INDEX IF EXISTS "IX_Jobs_State_NextAttemptAt_CreatedAt";
                    DELETE FROM "SchemaMigrations" WHERE "Id" = '20260830-durable-generation-v2';
                    ALTER TABLE "Jobs" DROP COLUMN "IdempotencyKey";
                    ALTER TABLE "Jobs" DROP COLUMN "LeaseOwner";
                    ALTER TABLE "Jobs" DROP COLUMN "LeaseExpiresAt";
                    ALTER TABLE "Jobs" DROP COLUMN "NextAttemptAt";
                    ALTER TABLE "Jobs" DROP COLUMN "DeliveryCount";
                    """;
                await downgrade.ExecuteNonQueryAsync();
            }

            using (var upgradedFactory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var upgradedClient = upgradedFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await upgradedClient.GetAsync("/health/ready")).StatusCode);
            }

            await using var verified = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verified.OpenAsync();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspectColumns = verified.CreateCommand())
            {
                inspectColumns.CommandText = "PRAGMA table_info(\"Jobs\");";
                await using var reader = await inspectColumns.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            }

            Assert.Contains("IdempotencyKey", columns);
            Assert.Contains("LeaseOwner", columns);
            Assert.Contains("LeaseExpiresAt", columns);
            Assert.Contains("NextAttemptAt", columns);
            Assert.Contains("DeliveryCount", columns);
            Assert.Contains("ResultJson", columns);

            var shotColumns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspectShotColumns = verified.CreateCommand())
            {
                inspectShotColumns.CommandText = "PRAGMA table_info(\"Shots\");";
                await using var reader = await inspectShotColumns.ExecuteReaderAsync();
                while (await reader.ReadAsync()) shotColumns.Add(reader.GetString(1));
            }
            Assert.Contains("ProductionVideoJobId", shotColumns);
            Assert.Contains("ProductionVideoAssetId", shotColumns);

            var versionColumns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspectVersionColumns = verified.CreateCommand())
            {
                inspectVersionColumns.CommandText = "PRAGMA table_info(\"ShotVersions\");";
                await using var reader = await inspectVersionColumns.ExecuteReaderAsync();
                while (await reader.ReadAsync()) versionColumns.Add(reader.GetString(1));
            }
            Assert.Contains("ProductionVideoJobId", versionColumns);
            Assert.Contains("ProductionVideoAssetId", versionColumns);
            Assert.Contains("Code", versionColumns);
            Assert.Contains("Title", versionColumns);
            Assert.Contains("Description", versionColumns);
            Assert.Contains("DurationFrames", versionColumns);
            Assert.Contains("Camera", versionColumns);
            Assert.Contains("Action", versionColumns);
            Assert.Contains("VideoFirstFrameCandidateId", versionColumns);
            Assert.Contains("VideoLastFrameCandidateId", versionColumns);

            await using var inspectLedger = verified.CreateCommand();
            inspectLedger.CommandText = """
                SELECT COUNT(*)
                FROM "SchemaMigrations"
                WHERE "Id" IN (
                    '20260830-production-baseline-v1',
                    '20260830-durable-generation-v2',
                    '20260830-audio-generation-v3',
                    '20260830-production-video-binding-v4',
                    '20260830-ratified-shot-intent-v5');
                """;
            Assert.Equal(5L, (long)(await inspectLedger.ExecuteScalarAsync() ?? 0L));

            var safetyCopies = Directory.GetFiles(
                Path.Combine(dataRoot, "schema-backups"),
                "framewright-before-20260830-durable-generation-v2-*.db");
            Assert.Single(safetyCopies);
            Assert.True(File.Exists(safetyCopies[0] + ".sha256"));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }
    [Fact]
    public async Task DirectorProposalApplyV10UpgradesAWorkstationThatAlreadyStagedProposals()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-schema-proposal-apply", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");

        try
        {
            Guid proposalId;
            using (var baselineFactory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var baselineClient = baselineFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await baselineClient.GetAsync("/health/ready")).StatusCode);
                using var snapshot = await baselineClient.GetFromJsonAsync<JsonDocument>("/api/studio") ?? throw new InvalidOperationException();
                var shot = snapshot.RootElement.GetProperty("shots")[0];
                var shotId = shot.GetProperty("id").GetGuid();
                using var context = await baselineClient.GetFromJsonAsync<JsonDocument>($"/api/webmcp/director/context?shotId={shotId}") ?? throw new InvalidOperationException();
                var body = new
                {
                    shotId, expectedVersion = shot.GetProperty("version").GetInt32(),
                    creativeDirection = "A direction staged before the upgrade.", rationale = "Migration proof.",
                    desiredMediaType = "image", authorityIds = Array.Empty<string>(), noteIds = Array.Empty<Guid>(),
                    preservedConstraints = Array.Empty<string>(),
                    observedStateToken = context.RootElement.GetProperty("data").GetProperty("stateToken").GetString(),
                    idempotencyKey = Guid.NewGuid().ToString("N")
                };
                var response = await baselineClient.PostAsJsonAsync("/api/webmcp/proposals", body);
                response.EnsureSuccessStatusCode();
                using var created = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
                proposalId = created.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            }

            // Reproduce a workstation that recorded v9 and never saw v10.
            await using (var legacy = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var downgrade = legacy.CreateCommand();
                downgrade.CommandText = """
                    DELETE FROM "SchemaMigrations" WHERE "Id" = '20260919-director-proposal-apply-v10';
                    ALTER TABLE "ShotRevisionProposals" DROP COLUMN "AppliedAt";
                    ALTER TABLE "ShotRevisionProposals" DROP COLUMN "ObservedStateToken";
                    ALTER TABLE "ShotRevisionProposals" DROP COLUMN "PreservedConstraintsJson";
                    """;
                await downgrade.ExecuteNonQueryAsync();
            }

            using (var upgradedFactory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var upgradedClient = upgradedFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await upgradedClient.GetAsync("/health/ready")).StatusCode);
                using var proposals = await upgradedClient.GetFromJsonAsync<JsonDocument>("/api/webmcp/proposals") ?? throw new InvalidOperationException();
                var survivor = proposals.RootElement.GetProperty("data").EnumerateArray()
                    .Single(x => x.GetProperty("id").GetGuid() == proposalId);
                Assert.Equal("A direction staged before the upgrade.", survivor.GetProperty("creativeDirection").GetString());
                Assert.Equal(0, survivor.GetProperty("preservedConstraints").GetArrayLength());
                Assert.Equal(JsonValueKind.Null, survivor.GetProperty("appliedAt").ValueKind);
            }

            await using var verified = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verified.OpenAsync();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspect = verified.CreateCommand())
            {
                inspect.CommandText = "PRAGMA table_info(\"ShotRevisionProposals\");";
                await using var reader = await inspect.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            }
            Assert.Contains("AppliedAt", columns);
            Assert.Contains("ObservedStateToken", columns);
            Assert.Contains("PreservedConstraintsJson", columns);

            await using var ledger = verified.CreateCommand();
            ledger.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Id\" = '20260919-director-proposal-apply-v10';";
            Assert.Equal(1L, (long)(await ledger.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }
}
