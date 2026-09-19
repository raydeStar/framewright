using Microsoft.Data.Sqlite;
using StoryboardStudio.Api.Services;
using System.Net;
using System.Net.Http.Json;

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
}
