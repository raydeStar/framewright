using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Persistence;

public static class StudioDatabaseInitializer
{
    private const string ProductionBaselineMigration = "20260830-production-baseline-v1";
    private const string DurableGenerationMigration = "20260830-durable-generation-v2";
    private const string AudioGenerationMigration = "20260830-audio-generation-v3";
    private const string ProductionVideoBindingMigration = "20260830-production-video-binding-v4";
    private const string RatifiedShotIntentMigration = "20260830-ratified-shot-intent-v5";
    private const string VisualConsistencyAuditMigration = "20260831-visual-consistency-audit-v6";
    private const string WebMcpProposalMigration = "20260903-webmcp-shot-proposals-v7";
    private const string YuE2CompositionMigration = "20260918-yue2-compositions-v8";
    private const string YuE2ArtifactManifestMigration = "20260918-yue2-artifact-manifests-v9";
    private const string DirectorProposalApplyMigration = "20260919-director-proposal-apply-v10";
    private const string ModelSceneMigration = "20260919-model-scenes-v11";
    private const string SceneDirectionMigration = "20260919-scene-direction-v12";
    private const string SceneBlockoutMigration = "20260919-scene-blockout-v13";
    private const string SceneMotionMigration = "20260919-scene-motion-v14";
    private const string AcknowledgedFailureMigration = "20260920-acknowledged-failures-v15";
    private const string PreparedDerivativeMigration = "20260920-prepared-derivatives-v16";
    private const string SceneShotBindingMigration = "20260921-scene-shot-bindings-v17";
    private const string PortableProjectMigration = "20260922-portable-projects-v18";

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var databasePath = db.Database.GetDbConnection().DataSource;
        var existingDatabase = !string.IsNullOrWhiteSpace(databasePath)
            && File.Exists(databasePath)
            && new FileInfo(databasePath).Length > 0;

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureMigrationLedgerAsync(db, cancellationToken);

        var migrationBackupCreated = false;
        if (!await HasMigrationAsync(db, ProductionBaselineMigration, cancellationToken))
        {
            if (existingDatabase)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, ProductionBaselineMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, ProductionBaselineMigration, async () =>
            {
                await EnsureFoundationTablesAsync(db, cancellationToken);
                await EnsureProjectAsync(db, cancellationToken);
                await EnsureAssetReviewNotesAsync(db, cancellationToken);
                await EnsureProjectScopeColumnsAsync(db, cancellationToken);
            }, cancellationToken);
        }

        if (!await HasMigrationAsync(db, DurableGenerationMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, DurableGenerationMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, DurableGenerationMigration,
                () => EnsureDurableGenerationColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, AudioGenerationMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, AudioGenerationMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, AudioGenerationMigration,
                () => EnsureAudioGenerationColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, ProductionVideoBindingMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, ProductionVideoBindingMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, ProductionVideoBindingMigration,
                () => EnsureProductionVideoBindingColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, RatifiedShotIntentMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, RatifiedShotIntentMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, RatifiedShotIntentMigration,
                () => EnsureRatifiedShotIntentColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, VisualConsistencyAuditMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, VisualConsistencyAuditMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, VisualConsistencyAuditMigration,
                () => EnsureVisualConsistencyAuditTableAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, WebMcpProposalMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, WebMcpProposalMigration, cancellationToken);
            }

            await RunMigrationAsync(db, WebMcpProposalMigration,
                () => EnsureWebMcpProposalTableAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, YuE2CompositionMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, YuE2CompositionMigration, cancellationToken);
            }

            await RunMigrationAsync(db, YuE2CompositionMigration,
                () => EnsureYuE2CompositionTablesAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, YuE2ArtifactManifestMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, YuE2ArtifactManifestMigration, cancellationToken);
            }

            await RunMigrationAsync(db, YuE2ArtifactManifestMigration,
                () => EnsureYuE2ArtifactManifestColumnAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, DirectorProposalApplyMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, DirectorProposalApplyMigration, cancellationToken);
            }

            await RunMigrationAsync(db, DirectorProposalApplyMigration,
                () => EnsureDirectorProposalApplyColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, ModelSceneMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, ModelSceneMigration, cancellationToken);
            }

            await RunMigrationAsync(db, ModelSceneMigration,
                () => EnsureModelSceneTablesAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, SceneDirectionMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, SceneDirectionMigration, cancellationToken);
            }

            await RunMigrationAsync(db, SceneDirectionMigration,
                () => EnsureSceneDirectionTablesAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, SceneBlockoutMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, SceneBlockoutMigration, cancellationToken);
            }

            await RunMigrationAsync(db, SceneBlockoutMigration,
                () => EnsureSceneBlockoutSchemaAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, SceneMotionMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, SceneMotionMigration, cancellationToken);
            }

            await RunMigrationAsync(db, SceneMotionMigration,
                () => EnsureSceneMotionColumnsAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, AcknowledgedFailureMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, AcknowledgedFailureMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, AcknowledgedFailureMigration,
                () => EnsureColumnAsync(db, "Jobs", "AcknowledgedAt", "TEXT NULL", cancellationToken),
                cancellationToken);
        }

        if (!await HasMigrationAsync(db, PreparedDerivativeMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, PreparedDerivativeMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, PreparedDerivativeMigration, async () =>
            {
                await EnsureColumnAsync(db, "Assets", "PreparationAcceptedAt", "TEXT NULL", cancellationToken);
                await EnsureColumnAsync(db, "Assets", "PreparationAcceptedBy", "TEXT NOT NULL DEFAULT ''", cancellationToken);
                await EnsureColumnAsync(db, "Assets", "PreparationAcceptanceNote", "TEXT NOT NULL DEFAULT ''", cancellationToken);
                await EnsureColumnAsync(db, "Assets", "PreparationTopologyChanged", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            }, cancellationToken);
        }

        if (!await HasMigrationAsync(db, SceneShotBindingMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, SceneShotBindingMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, SceneShotBindingMigration,
                () => EnsureSceneShotBindingTableAsync(db, cancellationToken), cancellationToken);
        }

        if (!await HasMigrationAsync(db, PortableProjectMigration, cancellationToken))
        {
            if (existingDatabase && !migrationBackupCreated)
            {
                await CreatePreMigrationBackupAsync(db, databasePath, PortableProjectMigration, cancellationToken);
                migrationBackupCreated = true;
            }

            await RunMigrationAsync(db, PortableProjectMigration, async () =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP INDEX IF EXISTS \"IX_GenerationManifests_ManifestHash\";", cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_GenerationManifests_ProjectId_ManifestHash\" ON \"GenerationManifests\" (\"ProjectId\", \"ManifestHash\");",
                    cancellationToken);
            }, cancellationToken);
        }

        await RestoreActiveProjectAsync(db, scope.ServiceProvider, cancellationToken);
        await SeedReferencesAsync(db, cancellationToken);

        if (await db.Shots.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            await BackfillAuthoritiesAsync(db, cancellationToken);
            await BackfillCandidatesAsync(db, cancellationToken);
            await SeedTimelineAsync(db, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var shots = new[]
        {
            CreateShot("SH-010", "Aerie approach", "Ennix enters the mist line as the Aerie resolves above the canyon.", "Draft", "Ratified", 6, 96, 1, 1, "Clear", "24mm · low approach", "Slow ascent; hold the reveal.", ["ennix", "aerie", "style"], ["Green eyes if visible", "Sky bridge remains world canon", "No modern structures"], now),
            CreateShot("SH-020", "The chain court", "Remora crosses beneath the three ceremonial chains toward the Magistrate.", "Draft", "Working", 4, 120, 2, 2, "Review", "35mm · centered dolly", "Measured walk; chains drift independently.", ["remora", "magistrate", "chain-court", "style"], ["Exactly three chains visible", "Remora keeps the small cut through her anatomical left eyebrow", "Magistrate remains elevated"], now.AddMinutes(-12)),
            CreateShot("SH-030", "Magistrate turns", "The Magistrate recognizes the insignia and turns into a hard profile.", "Sketch", "Working", 2, 72, 3, 3, "Clear", "70mm · locked profile", "Turn on the dialogue beat; no camera motion.", ["magistrate", "wardrobe", "style"], ["Gold collar with two clasps", "Right eye stays obscured", "No smile"], now.AddMinutes(-28)),
            CreateShot("SH-040", "Guard interruption", "Two guards break the axis from frame right as the court reacts.", "Final", "Ratified", 3, 84, 4, 4, "Clear", "28mm · lateral track", "Fast cross, then settle into a triangular block.", ["guards", "chain-court", "style"], ["Two guards only", "Spears remain vertical", "Preserve screen direction"], now.AddHours(-1)),
            CreateShot("SH-050", "Bridge decision", "Ennix stops at the threshold; the distant sky bridge is implied beyond the crop.", "Video", "Working", 2, 144, 5, 5, "Warning", "50mm · gentle push", "Breath and fabric motion only; do not force final endpoint.", ["ennix", "aerie", "style"], ["Sky bridge may be off-camera", "No background music", "Keep chain count from previous shot"], now.AddHours(-2)),
            CreateShot("SH-060", "Remora resolves", "Remora lowers the prop but does not release it.", "Draft", "NeedsWork", 7, 96, 6, 6, "Review", "85mm · eye level", "Minimal hand motion; land on eye line.", ["remora", "prop", "style"], ["Prop stays in anatomical left hand", "Small cut through anatomical left eyebrow", "Amber wardrobe piping"], now.AddHours(-3))
        };

        db.Shots.AddRange(shots);
        db.Comments.AddRange(
            new CommentRecord { Id = Guid.NewGuid(), ShotId = shots[1].Id, Version = 4, X = .68, Y = .33, Body = "Keep the third chain readable against the haze.", State = "Open", CreatedAt = now.AddMinutes(-9) },
            new CommentRecord { Id = Guid.NewGuid(), ShotId = shots[1].Id, Version = 4, X = .32, Y = .52, Body = "Remora's silhouette needs a cleaner shoulder break.", State = "Open", CreatedAt = now.AddMinutes(-7) },
            new CommentRecord { Id = Guid.NewGuid(), ShotId = shots[5].Id, Version = 7, X = .44, Y = .45, Body = "The prop has shifted to the wrong hand.", State = "Open", CreatedAt = now.AddHours(-1) });

        await db.SaveChangesAsync(cancellationToken);
        await BackfillAuthoritiesAsync(db, cancellationToken);
        await BackfillCandidatesAsync(db, cancellationToken);
        await SeedTimelineAsync(db, cancellationToken);
    }

    /// <summary>
    /// Records every schema transition that is allowed to touch an artist's
    /// workspace. The older initializer grew as a collection of startup repairs;
    /// those repairs are now one idempotent baseline migration, wrapped in a
    /// transaction and preceded by a verified SQLite safety copy. Future schema
    /// work must add a new immutable migration id instead of silently extending
    /// this one.
    /// </summary>
    private static async Task EnsureMigrationLedgerAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SchemaMigrations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SchemaMigrations" PRIMARY KEY,
                "Checksum" TEXT NOT NULL,
                "AppliedAt" TEXT NOT NULL
            );
            """, cancellationToken);
    }

    private static async Task<bool> HasMigrationAsync(
        StudioDbContext db,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Checksum\" FROM \"SchemaMigrations\" WHERE \"Id\" = $id LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = migrationId;
        command.Parameters.Add(parameter);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null or DBNull)
        {
            return false;
        }
        var recorded = Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        var expected = MigrationChecksum(migrationId);
        if (!string.Equals(recorded, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Schema migration '{migrationId}' no longer matches the migration that was applied. " +
                $"Recorded {recorded}; expected {expected}. " +
                "Restore the pre-migration backup or add a new migration; never rewrite production history.");
        }

        return true;
    }

    private static async Task RunMigrationAsync(
        StudioDbContext db,
        string migrationId,
        Func<Task> apply,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await apply();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SchemaMigrations" ("Id", "Checksum", "AppliedAt")
                VALUES ({migrationId}, {MigrationChecksum(migrationId)}, {DateTimeOffset.UtcNow});
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string MigrationChecksum(string migrationId)
    {
        var immutableContract = migrationId switch
        {
            ProductionBaselineMigration => "foundation-projects-review-project-scope-retry-metadata",
            DurableGenerationMigration => "job-idempotency-lease-expiry-next-attempt-delivery-count-active-index",
            AudioGenerationMigration => "job-result-packet-for-durable-multi-output-audio",
            ProductionVideoBindingMigration => "shot-and-ratified-version-production-video-job-and-asset-binding",
            RatifiedShotIntentMigration => "ratified-shot-code-title-description-duration-camera-action-and-video-endpoints",
            VisualConsistencyAuditMigration => "versioned-asset-and-contract-bound-vision-audit-with-reconciliation-decisions",
            WebMcpProposalMigration => "durable-project-scoped-human-gated-shot-revision-proposals-with-sort-key",
            DirectorProposalApplyMigration => "proposal-observed-context-token-preserved-constraints-and-single-apply-record",
            ModelSceneMigration => "project-scoped-editable-scenes-with-versioned-camera-lighting-and-model-revision-instances",
            SceneDirectionMigration => "revision-bound-scene-annotations-and-single-instance-human-gated-proposals",
            SceneBlockoutMigration => "reference-bound-blockout-plans-and-placeholder-scene-objects",
            SceneMotionMigration => "per-instance-clip-bindings-and-rigid-part-pivot-motion",
            AcknowledgedFailureMigration => "a-failure-an-artist-has-seen-stays-seen",
            PreparedDerivativeMigration => "per-asset-preparation-acceptance-and-topology-change-that-cannot-be-inherited",
            SceneShotBindingMigration => "immutable-scene-snapshot-shot-camera-timing-delivery-and-reviewed-still-binding",
            PortableProjectMigration => "round-trippable-project-package-with-project-local-manifest-hash-identity",
            YuE2CompositionMigration => "provider-independent-immutable-music-compositions-revisions-and-render-associations",
            YuE2ArtifactManifestMigration => "music-revision-plan-artifact-manifest-linked-to-worker-output",
            _ => throw new InvalidOperationException($"Schema migration '{migrationId}' has no frozen checksum contract.")
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"Framewright|schema|{migrationId}|{immutableContract}")))
            .ToLowerInvariant();
    }

    private static async Task CreatePreMigrationBackupAsync(
        StudioDbContext db,
        string databasePath,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            ?? throw new InvalidOperationException("The Studio database has no parent directory.");
        var backupDirectory = Path.Combine(databaseDirectory, "schema-backups");
        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N")[..8];
        var finalPath = Path.Combine(backupDirectory, $"framewright-before-{migrationId}-{timestamp:yyyyMMdd-HHmmss}-{nonce}.db");
        var temporaryPath = finalPath + ".partial";
        try
        {
            var source = (SqliteConnection)db.Database.GetDbConnection();
            if (source.State != System.Data.ConnectionState.Open)
            {
                await source.OpenAsync(cancellationToken);
            }

            await using (var destination = new SqliteConnection($"Data Source={temporaryPath};Pooling=False"))
            {
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            await using (var verification = new SqliteConnection($"Data Source={temporaryPath};Mode=ReadOnly;Pooling=False"))
            {
                await verification.OpenAsync(cancellationToken);
                await using var command = verification.CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The automatic pre-migration backup failed SQLite verification: {result ?? "unknown"}.");
                }
            }

            File.Move(temporaryPath, finalPath);
            await using var content = new FileStream(
                finalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var checksum = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken)).ToLowerInvariant();
            await File.WriteAllTextAsync(finalPath + ".sha256", $"{checksum}  {Path.GetFileName(finalPath)}{Environment.NewLine}", cancellationToken);

            foreach (var stale in Directory.EnumerateFiles(backupDirectory, "framewright-before-*.db", SearchOption.TopDirectoryOnly)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(3))
            {
                File.Delete(stale.FullName);
                var sidecar = stale.FullName + ".sha256";
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Adds the project column to every table that carries production evidence.
    ///
    /// <c>EnsureCreatedAsync</c> creates missing tables but never alters existing
    /// ones, so without this the live database would keep an unscoped schema while
    /// the model claimed otherwise — the query filters would then fail at runtime
    /// on a missing column rather than silently, but they would still fail.
    /// Existing rows default to the first project so nothing is orphaned.
    /// </summary>
    private static async Task EnsureProjectScopeColumnsAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        string[] scoped =
        [
            "Shots", "Comments", "AssetReviewNotes", "Jobs", "ShotVersions", "SketchDocuments", "GenerationManifests",
            "ReferenceVersions", "CandidateVersions", "FrameMarkups", "TimelineClips", "VoiceProfiles"
        ];
        // The column default is deliberately an empty string rather than the first
        // project's id. EF stores a Guid as upper-case TEXT and SQLite compares TEXT
        // case-sensitively, so a hand-written literal here would produce rows that
        // look correct in the file and yet match no query filter at all — every
        // upgraded row silently invisible. The backfill below binds a real Guid
        // instead, so the provider writes it in exactly the form EF later reads.
        foreach (var table in scoped)
        {
            await EnsureColumnAsync(db, table, "ProjectId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        }
        // Jobs predate durable retry/resume metadata. Keep this as an additive
        // upgrade so an artist's existing render history remains intact.
        await EnsureColumnAsync(db, "Jobs", "Attempt", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "RetryOfJobId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "LastHeartbeatAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "WorkType", "TEXT NOT NULL DEFAULT 'Shot'", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "RequestJson", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "IsActive", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // Adopt every row that does not point at a project that actually exists:
        // freshly added columns, rows left by a build that had the column but no
        // stamping, and rows orphaned by a deleted project. Matching against
        // Projects rather than a list of sentinel values also repairs a mismatched
        // text encoding, because a wrongly-cased id will not join either.
        const string adoptOrphanedRows = """
            UPDATE "Shots"               SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "Comments"            SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "AssetReviewNotes"    SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "Jobs"                SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "ShotVersions"        SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "SketchDocuments"     SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "GenerationManifests" SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "ReferenceVersions"   SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "CandidateVersions"   SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "FrameMarkups"        SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "TimelineClips"       SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "VoiceProfiles"       SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");

            -- These three already carried ProjectId before the upgrade, but they are
            -- swept too: an orphan here is just as invisible as one anywhere else.
            UPDATE "References"          SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "Assets"              SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            UPDATE "AssetCollections"    SET "ProjectId" = {0} WHERE "ProjectId" NOT IN (SELECT "Id" FROM "Projects");
            """;
        await db.Database.ExecuteSqlRawAsync(adoptOrphanedRows, [StudioDefaults.ProjectId], cancellationToken);

        // Each replacement is created before its predecessor is dropped, so an
        // interrupted upgrade never leaves a table without its uniqueness guarantee.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Shots_ProjectId_Code" ON "Shots" ("ProjectId", "Code");
            DROP INDEX IF EXISTS "IX_Shots_Code";

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_VoiceProfiles_ProjectId_Name" ON "VoiceProfiles" ("ProjectId", "Name");
            DROP INDEX IF EXISTS "IX_VoiceProfiles_Name";

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReferenceVersions_ProjectId_ReferenceId_Version" ON "ReferenceVersions" ("ProjectId", "ReferenceId", "Version");
            DROP INDEX IF EXISTS "IX_ReferenceVersions_ReferenceId_Version";
            """, cancellationToken);

        // The tables most often read by project, per the plan's measured counts.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_Shots_ProjectId"               ON "Shots"               ("ProjectId");
            CREATE INDEX IF NOT EXISTS "IX_CandidateVersions_ProjectId"   ON "CandidateVersions"   ("ProjectId");
            CREATE INDEX IF NOT EXISTS "IX_Comments_ProjectId"            ON "Comments"            ("ProjectId");
            CREATE INDEX IF NOT EXISTS "IX_AssetReviewNotes_ProjectId"    ON "AssetReviewNotes"    ("ProjectId");
            CREATE INDEX IF NOT EXISTS "IX_GenerationManifests_ProjectId" ON "GenerationManifests" ("ProjectId");
            """, cancellationToken);

        await EnsureReferenceIdentityAsync(db, cancellationToken);
        await EnsureLibraryTablesAsync(db, cancellationToken);
    }

    /// <summary>
    /// V2 of the production schema. Kept separate from the frozen project-scope
    /// baseline so a workstation that already recorded v1 cannot silently skip
    /// the durable queue columns introduced later.
    /// </summary>
    private static async Task EnsureDurableGenerationColumnsAsync(
        StudioDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "Jobs", "IdempotencyKey", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "LeaseOwner", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "LeaseExpiresAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "NextAttemptAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "DeliveryCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_Jobs_State_NextAttemptAt_CreatedAt"
                ON "Jobs" ("State", "NextAttemptAt", "CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_Jobs_IdempotencyKey"
                ON "Jobs" ("IdempotencyKey");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_Jobs_ActiveIdempotencyKey"
                ON "Jobs" ("IdempotencyKey")
                WHERE "IdempotencyKey" IS NOT NULL AND "State" IN ('Queued', 'Running');
            """, cancellationToken);
    }

    /// <summary>
    /// V3 adds a durable result packet for multi-output jobs such as character
    /// voice auditions. The frozen request remains untouched while every result
    /// asset and seed survives browser navigation and process restart.
    /// </summary>
    private static Task EnsureAudioGenerationColumnsAsync(
        StudioDbContext db,
        CancellationToken cancellationToken)
        => EnsureColumnAsync(db, "Jobs", "ResultJson", "TEXT NULL", cancellationToken);

    private static async Task EnsureProductionVideoBindingColumnsAsync(
        StudioDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "Shots", "ProductionVideoJobId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Shots", "ProductionVideoAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "ProductionVideoJobId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "ProductionVideoAssetId", "TEXT NULL", cancellationToken);
    }

    /// <summary>
    /// V5 closes a historical-evidence gap in the original authority archive.
    /// A ratified frame is not reproducible from pixels and reference ids alone:
    /// its intent, timing, camera, action, and selected motion endpoints are part
    /// of the artistic decision. Existing sparse rows are repaired from the
    /// current shot as the only evidence older builds retained; every authority
    /// written after this migration is a complete immutable snapshot.
    /// </summary>
    private static async Task EnsureRatifiedShotIntentColumnsAsync(
        StudioDbContext db,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "ShotVersions", "Code", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "Title", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "Description", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "DurationFrames", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "Camera", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "Action", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "VideoFirstFrameCandidateId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "VideoLastFrameCandidateId", "TEXT NULL", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "ShotVersions"
            SET "Code" = COALESCE((SELECT s."Code" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "Code"),
                "Title" = COALESCE((SELECT s."Title" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "Title"),
                "Description" = COALESCE((SELECT s."Description" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "Description"),
                "DurationFrames" = COALESCE((SELECT s."DurationFrames" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "DurationFrames"),
                "Camera" = COALESCE((SELECT s."Camera" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "Camera"),
                "Action" = COALESCE((SELECT s."Action" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"), "Action"),
                "VideoFirstFrameCandidateId" = (SELECT s."VideoFirstFrameCandidateId" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId"),
                "VideoLastFrameCandidateId" = (SELECT s."VideoLastFrameCandidateId" FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId")
            WHERE EXISTS (SELECT 1 FROM "Shots" s WHERE s."Id" = "ShotVersions"."ShotId");
            """, cancellationToken);

        var historical = await db.ShotVersions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var authority in historical)
        {
            authority.ManifestHash = ComputeAuthorityHash(authority);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds spatial review notes without asking artists to rebuild an existing
    /// local database. EnsureCreated does not add tables after first launch.
    /// </summary>
    private static async Task EnsureAssetReviewNotesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AssetReviewNotes" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AssetReviewNotes" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "AssetId" TEXT NOT NULL,
                "X" REAL NOT NULL,
                "Y" REAL NOT NULL,
                "Body" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AssetReviewNotes_AssetId_CreatedAt" ON "AssetReviewNotes" ("AssetId", "CreatedAt");
            """, cancellationToken);
    }

    /// <summary>
    /// Creates the project-independent library tables and the provenance columns an
    /// imported authority carries. Written as explicit DDL for the same reason the
    /// rest of this file is: EnsureCreatedAsync never touches an existing database.
    /// </summary>
    private static async Task EnsureLibraryTablesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "LibraryAuthorities" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LibraryAuthorities" PRIMARY KEY,
                "Slug" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "CurrentVersion" INTEGER NOT NULL,
                "LastIssuedVersion" INTEGER NOT NULL,
                "Accent" TEXT NOT NULL,
                "VisualVariant" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LibraryAuthorities_Slug" ON "LibraryAuthorities" ("Slug");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LibraryAuthorities_Name" ON "LibraryAuthorities" ("Name");

            CREATE TABLE IF NOT EXISTS "LibraryAuthorityVersions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_LibraryAuthorityVersions" PRIMARY KEY,
                "LibraryAuthorityId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "Description" TEXT NOT NULL,
                "LockedConstraint" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "RatifiedAt" TEXT NOT NULL,
                "OriginProjectId" TEXT NULL,
                "ImageContentHash" TEXT NULL,
                "ImageStoragePath" TEXT NULL,
                "ImageMimeType" TEXT NULL,
                "ImageBytes" INTEGER NULL,
                "ImageWidth" INTEGER NULL,
                "ImageHeight" INTEGER NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LibraryAuthorityVersions_LibraryAuthorityId_Version" ON "LibraryAuthorityVersions" ("LibraryAuthorityId", "Version");
            """, cancellationToken);

        await EnsureColumnAsync(db, "References", "OriginLibraryId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "References", "OriginVersion", "INTEGER NULL", cancellationToken);
    }

    /// <summary>
    /// Moves References onto a composite (ProjectId, Id) primary key.
    ///
    /// Reference ids are readable slugs, so a bare string key means two projects
    /// can never both hold a character called <c>ennix</c>. SQLite cannot alter a
    /// primary key, so this rebuilds the table — the one destructive step in the
    /// upgrade, hence the pragma guard: it runs only while the old single-column
    /// key is still in place, and the copy happens inside a transaction.
    /// </summary>
    private static async Task EnsureReferenceIdentityAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);

        var keyColumns = new List<string>();
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(\"References\")";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(5) > 0) keyColumns.Add(reader.GetString(1));
            }
        }
        if (keyColumns.Count != 1 || !string.Equals(keyColumns[0], "Id", StringComparison.OrdinalIgnoreCase)) return;

        await db.Database.ExecuteSqlRawAsync("""
            PRAGMA foreign_keys=off;

            CREATE TABLE "References_upgraded" (
                "ProjectId" TEXT NOT NULL,
                "Id" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "CurrentVersion" INTEGER NOT NULL,
                "LastIssuedVersion" INTEGER NOT NULL DEFAULT 0,
                "Status" TEXT NOT NULL,
                "Accent" TEXT NOT NULL,
                "VisualVariant" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_References" PRIMARY KEY ("ProjectId", "Id")
            );

            INSERT INTO "References_upgraded"
                ("ProjectId", "Id", "Name", "Category", "CurrentVersion", "LastIssuedVersion", "Status", "Accent", "VisualVariant", "CreatedAt", "UpdatedAt")
            SELECT "ProjectId", "Id", "Name", "Category", "CurrentVersion", "LastIssuedVersion", "Status", "Accent", "VisualVariant", "CreatedAt", "UpdatedAt"
            FROM "References";

            DROP TABLE "References";
            ALTER TABLE "References_upgraded" RENAME TO "References";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_References_ProjectId_Name" ON "References" ("ProjectId", "Name");
            CREATE INDEX IF NOT EXISTS "IX_References_ProjectId" ON "References" ("ProjectId");

            PRAGMA foreign_keys=on;
            """, cancellationToken);
    }

    /// <summary>
    /// Reinstates the project the artist last worked in, so a restart does not
    /// silently drop them back into the first project.
    /// </summary>
    private static async Task RestoreActiveProjectAsync(StudioDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var registry = services.GetRequiredService<Services.ActiveProjectRegistry>();
        var active = await db.Projects.AsNoTracking().Where(x => x.IsActive).Select(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (active != Guid.Empty) { registry.Set(active); return; }

        // No project is marked yet, either because this is the first run after the
        // upgrade or because the marked project was deleted. Adopt the oldest one.
        // Ordered in memory because SQLite cannot ORDER BY a DateTimeOffset, which
        // is why the rest of this codebase sorts timestamps client-side too.
        var fallback = (await db.Projects.ToListAsync(cancellationToken)).OrderBy(x => x.UpdatedAt).FirstOrDefault();
        if (fallback is null) return;
        fallback.IsActive = true;
        await db.SaveChangesAsync(cancellationToken);
        registry.Set(fallback.Id);
    }

    private static async Task SeedTimelineAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        if (await db.TimelineClips.IgnoreQueryFilters().AnyAsync(cancellationToken)) return;
        var now = DateTimeOffset.UtcNow;
        db.TimelineClips.AddRange(
            new TimelineClipRecord { Id = Guid.NewGuid(), Track = "Dialogue", Label = "The bridge remembers", StartFrame = 82, DurationFrames = 112, TrimStartSeconds = 0, Volume = 1, Text = "The bridge remembers.", CreatedAt = now, UpdatedAt = now },
            new TimelineClipRecord { Id = Guid.NewGuid(), Track = "Music", Label = "Temp score - separate overlay", StartFrame = 28, DurationFrames = 500, TrimStartSeconds = 0, Volume = .72, Text = "Separate music overlay; never included in video prompts.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureFoundationTablesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ShotVersions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ShotVersions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "Stage" TEXT NOT NULL,
                "Approval" TEXT NOT NULL,
                "VisualVariant" INTEGER NOT NULL,
                "ReferenceIdsJson" TEXT NOT NULL,
                "ConstraintsJson" TEXT NOT NULL,
                "ManifestHash" TEXT NOT NULL,
                "RatifiedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShotVersions_ShotId_Version" ON "ShotVersions" ("ShotId", "Version");

            CREATE TABLE IF NOT EXISTS "SketchDocuments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SketchDocuments" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL,
                "CreativeBrief" TEXT NOT NULL,
                "ContentJson" TEXT NOT NULL,
                "UnderlayAssetId" TEXT NULL,
                "CompositionAssetId" TEXT NULL,
                "ContentHash" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SketchDocuments_ShotId" ON "SketchDocuments" ("ShotId");

            CREATE TABLE IF NOT EXISTS "PosePresets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PosePresets" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT COLLATE NOCASE NOT NULL,
                "JointsJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PosePresets_ProjectId_Name" ON "PosePresets" ("ProjectId", "Name");

            CREATE TABLE IF NOT EXISTS "GenerationManifests" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_GenerationManifests" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "ShotCode" TEXT NOT NULL,
                "ShotVersion" INTEGER NOT NULL,
                "SketchId" TEXT NOT NULL,
                "SketchRevision" INTEGER NOT NULL,
                "Route" TEXT NOT NULL,
                "Purpose" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "CreativeBrief" TEXT NOT NULL,
                "AuthoritiesJson" TEXT NOT NULL,
                "ConstraintsJson" TEXT NOT NULL,
                "ManifestJson" TEXT NOT NULL,
                "ManifestHash" TEXT NOT NULL,
                "ProviderCallMade" INTEGER NOT NULL,
                "CompositionAssetId" TEXT NULL,
                "CompositionAssetHash" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_GenerationManifests_ShotId_CreatedAt" ON "GenerationManifests" ("ShotId", "CreatedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_GenerationManifests_ProjectId_ManifestHash" ON "GenerationManifests" ("ProjectId", "ManifestHash");

            CREATE TABLE IF NOT EXISTS "Assets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Assets" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "OriginalFileName" TEXT NOT NULL,
                "MimeType" TEXT NOT NULL,
                "Bytes" INTEGER NOT NULL,
                "Width" INTEGER NULL,
                "Height" INTEGER NULL,
                "DurationSeconds" REAL NULL,
                "ContentHash" TEXT NOT NULL,
                "StoragePath" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL DEFAULT '',
                "CollectionId" TEXT NULL,
                "TagsJson" TEXT NOT NULL DEFAULT '[]',
                "Notes" TEXT NOT NULL DEFAULT '',
                "Source" TEXT NOT NULL DEFAULT 'Imported',
                "IsArchived" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAt" TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00',
                "RevisionFamilyId" TEXT NULL,
                "RevisionNumber" INTEGER NULL,
                "IsCurrentRevision" INTEGER NOT NULL DEFAULT 0,
                "ParentAssetId" TEXT NULL,
                "RevisionPrompt" TEXT NOT NULL DEFAULT '',
                "RevisionEngine" TEXT NOT NULL DEFAULT ''
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Assets_ProjectId_ContentHash" ON "Assets" ("ProjectId", "ContentHash");

            CREATE TABLE IF NOT EXISTS "AssetCollections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AssetCollections" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Color" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AssetCollections_ProjectId_Name" ON "AssetCollections" ("ProjectId", "Name");

            CREATE TABLE IF NOT EXISTS "AssetPlacements" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AssetPlacements" PRIMARY KEY,
                "AssetId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "Role" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AssetPlacements_AssetId_ShotId_Role" ON "AssetPlacements" ("AssetId", "ShotId", "Role");

            CREATE TABLE IF NOT EXISTS "References" (
                "ProjectId" TEXT NOT NULL,
                "Id" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "CurrentVersion" INTEGER NOT NULL,
                "LastIssuedVersion" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "Accent" TEXT NOT NULL,
                "VisualVariant" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                CONSTRAINT "PK_References" PRIMARY KEY ("ProjectId", "Id")
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_References_ProjectId_Name" ON "References" ("ProjectId", "Name");

            CREATE TABLE IF NOT EXISTS "ReferenceVersions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ReferenceVersions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ReferenceId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "Description" TEXT NOT NULL,
                "LockedConstraint" TEXT NOT NULL,
                "ImageAssetId" TEXT NULL,
                "ContentHash" TEXT NOT NULL,
                "RatifiedAt" TEXT NOT NULL
            );
            -- The project-scoped unique indexes are created by
            -- EnsureProjectScopeColumnsAsync, not here: on an existing database
            -- these tables predate the ProjectId column, so indexing it at this
            -- point fails before the column has been added.

            CREATE TABLE IF NOT EXISTS "CandidateVersions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_CandidateVersions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "Stage" TEXT NOT NULL,
                "Approval" TEXT NOT NULL,
                "IsCurrent" INTEGER NOT NULL,
                "AssetId" TEXT NULL,
                "SourceManifestId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "SupersededAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_CandidateVersions_ShotId_Version" ON "CandidateVersions" ("ShotId", "Version");

            CREATE TABLE IF NOT EXISTS "FrameMarkups" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FrameMarkups" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "Revision" INTEGER NOT NULL,
                "StrokesJson" TEXT NOT NULL,
                "ContentHash" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FrameMarkups_ShotId_Version" ON "FrameMarkups" ("ShotId", "Version");

            CREATE TABLE IF NOT EXISTS "TimelineClips" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TimelineClips" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Track" TEXT NOT NULL,
                "Label" TEXT NOT NULL,
                "StartFrame" INTEGER NOT NULL,
                "DurationFrames" INTEGER NOT NULL,
                "TrimStartSeconds" REAL NOT NULL,
                "Volume" REAL NOT NULL,
                "AssetId" TEXT NULL,
                "VoiceProfileId" TEXT NULL,
                "Text" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_TimelineClips_Track_StartFrame" ON "TimelineClips" ("Track", "StartFrame");

            CREATE TABLE IF NOT EXISTS "VoiceProfiles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_VoiceProfiles" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Provider" TEXT NOT NULL,
                "ProviderVoiceId" TEXT NOT NULL,
                "CharacterName" TEXT NOT NULL,
                "CharacterReferenceId" TEXT NULL,
                "SampleAssetId" TEXT NULL,
                "ConsentAttestation" TEXT NOT NULL,
                "ConsentedAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await EnsureColumnAsync(db, "SketchDocuments", "UnderlayAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "SketchDocuments", "CompositionAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "GenerationManifests", "CompositionAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "GenerationManifests", "CompositionAssetHash", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "GenerationManifests", "LastFrameAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "GenerationManifests", "LastFrameAssetHash", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Shots", "CurrentAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Shots", "VideoFirstFrameCandidateId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Shots", "VideoLastFrameCandidateId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotVersions", "AssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "ManifestId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "AdapterId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "OutputAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Jobs", "ProviderRequestId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Comments", "ReferenceId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Comments", "ReferenceVersion", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(db, "References", "LastIssuedVersion", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "DisplayName", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "CollectionId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "TagsJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "Notes", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "Source", "TEXT NOT NULL DEFAULT 'Imported'", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "IsArchived", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "RevisionFamilyId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "RevisionNumber", "INTEGER NULL", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "IsCurrentRevision", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "ParentAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "RevisionPrompt", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Assets", "RevisionEngine", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Assets_RevisionFamilyId_RevisionNumber\" ON \"Assets\" (\"RevisionFamilyId\", \"RevisionNumber\")", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "VisualStyle", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "WorldCanon", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "PromptDirectives", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "NegativeDirectives", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DeliveryWidth", "INTEGER NOT NULL DEFAULT 2304", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "DeliveryHeight", "INTEGER NOT NULL DEFAULT 960", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "ColorSpace", "TEXT NOT NULL DEFAULT 'Rec.709'", cancellationToken);
        await EnsureColumnAsync(db, "Projects", "AudioSampleRate", "INTEGER NOT NULL DEFAULT 48000", cancellationToken);
        await EnsureColumnAsync(db, "VoiceProfiles", "CharacterReferenceId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "VoiceProfiles", "SampleAssetId", "TEXT NULL", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Assets\" SET \"DisplayName\" = \"OriginalFileName\" WHERE \"DisplayName\" = ''", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Assets\" SET \"UpdatedAt\" = \"CreatedAt\" WHERE \"UpdatedAt\" = '0001-01-01T00:00:00+00:00'", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"References\" SET \"LastIssuedVersion\" = \"CurrentVersion\" WHERE \"LastIssuedVersion\" < \"CurrentVersion\"", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Projects\" SET \"VisualStyle\" = {StudioDefaults.VisualStyle} WHERE \"VisualStyle\" = ''", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Projects\" SET \"WorldCanon\" = {StudioDefaults.WorldCanon} WHERE \"WorldCanon\" = ''", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Projects\" SET \"PromptDirectives\" = {StudioDefaults.PromptDirectives} WHERE \"PromptDirectives\" = ''", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Projects\" SET \"NegativeDirectives\" = {StudioDefaults.NegativeDirectives} WHERE \"NegativeDirectives\" = ''", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Jobs_ManifestId\" ON \"Jobs\" (\"ManifestId\")", cancellationToken);
    }

    private static async Task EnsureWebMcpProposalTableAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ShotRevisionProposals" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ShotRevisionProposals" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "BaseVersion" INTEGER NOT NULL,
                "BaseUpdatedAt" TEXT NOT NULL,
                "CreativeDirection" TEXT NOT NULL,
                "Rationale" TEXT NOT NULL,
                "DesiredMediaType" TEXT NOT NULL,
                "AuthorityIdsJson" TEXT NOT NULL,
                "NoteIdsJson" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedAtUnixMs" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "DecidedAt" TEXT NULL,
                "AppliedAt" TEXT NULL,
                "ObservedStateToken" TEXT NULL,
                "PreservedConstraintsJson" TEXT NOT NULL DEFAULT '[]'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ShotRevisionProposals_ProjectId_IdempotencyKey"
                ON "ShotRevisionProposals" ("ProjectId", "IdempotencyKey");
            CREATE INDEX IF NOT EXISTS "IX_ShotRevisionProposals_ShotId_State_CreatedAtUnixMs"
                ON "ShotRevisionProposals" ("ShotId", "State", "CreatedAtUnixMs");
            """, cancellationToken);
    }

    /// <summary>
    /// V10 records what a browser-agent proposal promised to preserve, which
    /// view it was reading, and the single moment the artist applied it. The
    /// columns are additive so an existing workstation keeps every proposal it
    /// already staged.
    /// </summary>
    private static async Task EnsureDirectorProposalApplyColumnsAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "ShotRevisionProposals", "AppliedAt", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotRevisionProposals", "ObservedStateToken", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "ShotRevisionProposals", "PreservedConstraintsJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
    }

    /// <summary>
    /// V11 adds editable scenes. Instances are separate rows so two objects can
    /// share one model revision and still be moved independently, and the scene
    /// version supports refusing a save that was built on a stale read.
    /// </summary>
    private static async Task EnsureModelSceneTablesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Scenes" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Scenes" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "CameraYaw" REAL NOT NULL,
                "CameraPitch" REAL NOT NULL,
                "CameraDistance" REAL NOT NULL,
                "CameraTargetX" REAL NOT NULL,
                "CameraTargetY" REAL NOT NULL,
                "CameraTargetZ" REAL NOT NULL,
                "CameraFieldOfView" REAL NOT NULL,
                "KeyLightIntensity" REAL NOT NULL,
                "KeyLightYaw" REAL NOT NULL,
                "KeyLightPitch" REAL NOT NULL,
                "AmbientLightIntensity" REAL NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_Scenes_ProjectId_UpdatedAt" ON "Scenes" ("ProjectId", "UpdatedAt");

            CREATE TABLE IF NOT EXISTS "SceneInstances" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneInstances" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "SceneId" TEXT NOT NULL,
                "AssetId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "PositionX" REAL NOT NULL,
                "PositionY" REAL NOT NULL,
                "PositionZ" REAL NOT NULL,
                "RotationX" REAL NOT NULL,
                "RotationY" REAL NOT NULL,
                "RotationZ" REAL NOT NULL,
                "ScaleX" REAL NOT NULL,
                "ScaleY" REAL NOT NULL,
                "ScaleZ" REAL NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SceneInstances_SceneId_SortOrder" ON "SceneInstances" ("SceneId", "SortOrder");
            """, cancellationToken);
    }

    /// <summary>
    /// V12 adds scene direction: notes anchored in an instance's local space
    /// against the exact revision they were measured on, and proposals that
    /// target one instance and remember which view they were built from.
    /// </summary>
    private static async Task EnsureSceneDirectionTablesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SceneAnnotations" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneAnnotations" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "SceneId" TEXT NOT NULL,
                "InstanceId" TEXT NOT NULL,
                "AssetId" TEXT NOT NULL,
                "AnchorX" REAL NOT NULL,
                "AnchorY" REAL NOT NULL,
                "AnchorZ" REAL NOT NULL,
                "CameraYaw" REAL NOT NULL,
                "CameraPitch" REAL NOT NULL,
                "CameraDistance" REAL NOT NULL,
                "CameraTargetX" REAL NOT NULL,
                "CameraTargetY" REAL NOT NULL,
                "CameraTargetZ" REAL NOT NULL,
                "Body" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SceneAnnotations_SceneId_InstanceId_State"
                ON "SceneAnnotations" ("SceneId", "InstanceId", "State");

            CREATE TABLE IF NOT EXISTS "SceneProposals" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneProposals" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "SceneId" TEXT NOT NULL,
                "InstanceId" TEXT NOT NULL,
                "BaseSceneVersion" INTEGER NOT NULL,
                "ObservedStateToken" TEXT NOT NULL,
                "Direction" TEXT NOT NULL,
                "Rationale" TEXT NOT NULL,
                "PositionJson" TEXT NULL,
                "RotationJson" TEXT NULL,
                "ScaleJson" TEXT NULL,
                "State" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedAtUnixMs" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "DecidedAt" TEXT NULL,
                "AppliedAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SceneProposals_ProjectId_IdempotencyKey"
                ON "SceneProposals" ("ProjectId", "IdempotencyKey");
            """, cancellationToken);
    }

    /// <summary>
    /// V13 adds construction plans read off a reference, and lets a scene object
    /// be a placeholder rather than a model revision. Existing instances keep
    /// their model, so a scene saved before this migration opens unchanged.
    /// </summary>
    private static async Task EnsureSceneBlockoutSchemaAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        // A placeholder has no model revision, so AssetId has to become nullable.
        // SQLite cannot relax NOT NULL in place, so the table is rebuilt inside
        // this migration transaction and every existing placement is copied over
        // with its identity, model, and transform intact.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "SceneInstances_v13" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneInstances" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "SceneId" TEXT NOT NULL,
                "AssetId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "PositionX" REAL NOT NULL,
                "PositionY" REAL NOT NULL,
                "PositionZ" REAL NOT NULL,
                "RotationX" REAL NOT NULL,
                "RotationY" REAL NOT NULL,
                "RotationZ" REAL NOT NULL,
                "ScaleX" REAL NOT NULL,
                "ScaleY" REAL NOT NULL,
                "ScaleZ" REAL NOT NULL,
                "PlaceholderShape" TEXT NULL,
                "PlaceholderSizeX" REAL NOT NULL DEFAULT 0,
                "PlaceholderSizeY" REAL NOT NULL DEFAULT 0,
                "PlaceholderSizeZ" REAL NOT NULL DEFAULT 0,
                "Role" TEXT NULL,
                "SourcePlanId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT INTO "SceneInstances_v13" (
                "Id", "ProjectId", "SceneId", "AssetId", "Name", "SortOrder",
                "PositionX", "PositionY", "PositionZ",
                "RotationX", "RotationY", "RotationZ",
                "ScaleX", "ScaleY", "ScaleZ", "CreatedAt", "UpdatedAt")
            SELECT
                "Id", "ProjectId", "SceneId", "AssetId", "Name", "SortOrder",
                "PositionX", "PositionY", "PositionZ",
                "RotationX", "RotationY", "RotationZ",
                "ScaleX", "ScaleY", "ScaleZ", "CreatedAt", "UpdatedAt"
            FROM "SceneInstances";
            DROP TABLE "SceneInstances";
            ALTER TABLE "SceneInstances_v13" RENAME TO "SceneInstances";
            CREATE INDEX IF NOT EXISTS "IX_SceneInstances_SceneId_SortOrder" ON "SceneInstances" ("SceneId", "SortOrder");
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SceneBlockoutPlans" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneBlockoutPlans" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ReferenceAssetId" TEXT NOT NULL,
                "ReferenceContentHash" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "CameraYaw" REAL NOT NULL,
                "CameraPitch" REAL NOT NULL,
                "CameraDistance" REAL NOT NULL,
                "CameraTargetX" REAL NOT NULL,
                "CameraTargetY" REAL NOT NULL,
                "CameraTargetZ" REAL NOT NULL,
                "CameraFieldOfView" REAL NOT NULL,
                "AssumptionsJson" TEXT NOT NULL,
                "UncertaintiesJson" TEXT NOT NULL,
                "IdempotencyKey" TEXT NOT NULL,
                "SceneId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedAtUnixMs" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "DecidedAt" TEXT NULL,
                "AppliedAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SceneBlockoutPlans_ProjectId_IdempotencyKey"
                ON "SceneBlockoutPlans" ("ProjectId", "IdempotencyKey");
            CREATE INDEX IF NOT EXISTS "IX_SceneBlockoutPlans_ReferenceAssetId_CreatedAtUnixMs"
                ON "SceneBlockoutPlans" ("ReferenceAssetId", "CreatedAtUnixMs");

            CREATE TABLE IF NOT EXISTS "SceneBlockoutItems" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneBlockoutItems" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "PlanId" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "Role" TEXT NOT NULL,
                "MatchAssetId" TEXT NULL,
                "PlaceholderShape" TEXT NULL,
                "PlaceholderSizeX" REAL NOT NULL,
                "PlaceholderSizeY" REAL NOT NULL,
                "PlaceholderSizeZ" REAL NOT NULL,
                "PositionX" REAL NOT NULL,
                "PositionY" REAL NOT NULL,
                "PositionZ" REAL NOT NULL,
                "RotationX" REAL NOT NULL,
                "RotationY" REAL NOT NULL,
                "RotationZ" REAL NOT NULL,
                "ScaleX" REAL NOT NULL,
                "ScaleY" REAL NOT NULL,
                "ScaleZ" REAL NOT NULL,
                "MotionIntent" TEXT NOT NULL,
                "Confidence" TEXT NOT NULL,
                "Note" TEXT NOT NULL,
                "InstanceId" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SceneBlockoutItems_PlanId_SortOrder"
                ON "SceneBlockoutItems" ("PlanId", "SortOrder");
            """, cancellationToken);
    }

    /// <summary>
    /// V14 gives every scene object its own playback settings and its own rigid
    /// motion. They live on the instance rather than on the clip, which is what
    /// lets two objects share one clip and still be trimmed and scrubbed apart.
    /// </summary>
    private static async Task EnsureSceneMotionColumnsAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(db, "SceneInstances", "ClipAssetId", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipName", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipStart", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipEnd", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipSpeed", "REAL NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipTime", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipLoop", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "ClipRootMotion", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionAxis", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionPivotX", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionPivotY", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionPivotZ", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionFrom", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionTo", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionSeconds", "REAL NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, "SceneInstances", "MotionPingPong", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }

    private static async Task EnsureSceneShotBindingTableAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SceneShotBindings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SceneShotBindings" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "SceneId" TEXT NOT NULL,
                "SceneName" TEXT NOT NULL,
                "SceneVersion" INTEGER NOT NULL,
                "ShotId" TEXT NOT NULL,
                "ShotCode" TEXT NOT NULL,
                "ShotVersion" INTEGER NOT NULL,
                "CameraJson" TEXT NOT NULL,
                "StartTime" REAL NOT NULL,
                "EndTime" REAL NOT NULL,
                "StillTime" REAL NOT NULL,
                "DeliveryWidth" INTEGER NOT NULL,
                "DeliveryHeight" INTEGER NOT NULL,
                "FramesPerSecond" INTEGER NOT NULL,
                "ColorSpace" TEXT NOT NULL,
                "SnapshotJson" TEXT NOT NULL,
                "SnapshotHash" TEXT NOT NULL,
                "StillAssetId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_SceneShotBindings_SceneId_CreatedAt"
                ON "SceneShotBindings" ("SceneId", "CreatedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SceneShotBindings_ShotId_ShotVersion"
                ON "SceneShotBindings" ("ShotId", "ShotVersion");
            """, cancellationToken);
    }

    private static async Task EnsureYuE2CompositionTablesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "MusicCompositions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MusicCompositions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "CurrentRevisionId" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_MusicCompositions_ProjectId_UpdatedAt"
                ON "MusicCompositions" ("ProjectId", "UpdatedAt");

            CREATE TABLE IF NOT EXISTS "MusicCompositionRevisions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MusicCompositionRevisions" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "CompositionId" TEXT NOT NULL,
                "RevisionNumber" INTEGER NOT NULL,
                "ParentRevisionId" TEXT NULL,
                "EditSummary" TEXT NOT NULL,
                "CompositionJson" TEXT NOT NULL,
            "AbcNotation" TEXT NOT NULL,
            "ContentHash" TEXT NOT NULL,
            "PlanArtifactManifestJson" TEXT NOT NULL DEFAULT '{{}}',
            "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MusicCompositionRevisions_CompositionId_RevisionNumber"
                ON "MusicCompositionRevisions" ("CompositionId", "RevisionNumber");

            CREATE TABLE IF NOT EXISTS "MusicRenders" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_MusicRenders" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "CompositionRevisionId" TEXT NOT NULL,
                "JobId" TEXT NOT NULL,
                "AssetId" TEXT NOT NULL,
                "Renderer" TEXT NOT NULL,
                "SettingsJson" TEXT NOT NULL,
                "ArtifactManifestJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_MusicRenders_CompositionRevisionId_CreatedAt"
                ON "MusicRenders" ("CompositionRevisionId", "CreatedAt");
            """, cancellationToken);
    }

    private static Task EnsureYuE2ArtifactManifestColumnAsync(StudioDbContext db, CancellationToken cancellationToken)
        => EnsureColumnAsync(db, "MusicCompositionRevisions", "PlanArtifactManifestJson", "TEXT NOT NULL DEFAULT '{}'", cancellationToken);

    private static async Task EnsureVisualConsistencyAuditTableAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ShotVisualAudits" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ShotVisualAudits" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ShotId" TEXT NOT NULL,
                "ShotVersion" INTEGER NOT NULL,
                "AssetId" TEXT NOT NULL,
                "AssetHash" TEXT NOT NULL,
                "ContractHash" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "GateState" TEXT NOT NULL,
                "Summary" TEXT NOT NULL,
                "FindingsJson" TEXT NOT NULL,
                "DecisionsJson" TEXT NOT NULL,
                "AdoptedShotProposalJson" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ShotVisualAudits_ShotId_ShotVersion_AssetId_ContractHash"
                ON "ShotVisualAudits" ("ShotId", "ShotVersion", "AssetId", "ContractHash");
            """, cancellationToken);
    }

    private static async Task EnsureProjectAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Projects" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Production" TEXT NOT NULL,
                "SequenceCode" TEXT NOT NULL,
                "SequenceName" TEXT NOT NULL,
                "FramesPerSecond" INTEGER NOT NULL,
                "AspectRatio" TEXT NOT NULL,
                "DeliveryWidth" INTEGER NOT NULL DEFAULT 2304,
                "DeliveryHeight" INTEGER NOT NULL DEFAULT 960,
                "ColorSpace" TEXT NOT NULL DEFAULT 'Rec.709',
                "AudioSampleRate" INTEGER NOT NULL DEFAULT 48000,
                "VisualStyle" TEXT NOT NULL,
                "WorldCanon" TEXT NOT NULL,
                "PromptDirectives" TEXT NOT NULL,
                "NegativeDirectives" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL DEFAULT 0,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);
        if (await db.Projects.AnyAsync(cancellationToken)) return;
        db.Projects.Add(new ProjectRecord
        {
            Id = StoryboardStudio.Core.StudioDefaults.ProjectId,
            IsActive = true,
            Name = "The Aerie Sequence",
            Production = "Skychasers - local production",
            SequenceCode = "SQ-01",
            SequenceName = "The Court Above",
            FramesPerSecond = 24,
            AspectRatio = "2.39:1",
            DeliveryWidth = 2304,
            DeliveryHeight = 960,
            ColorSpace = "Rec.709",
            AudioSampleRate = 48000,
            VisualStyle = StudioDefaults.VisualStyle,
            WorldCanon = StudioDefaults.WorldCanon,
            PromptDirectives = StudioDefaults.PromptDirectives,
            NegativeDirectives = StudioDefaults.NegativeDirectives,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedReferencesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        if (await db.References.IgnoreQueryFilters().AnyAsync(cancellationToken)) return;
        var now = DateTimeOffset.UtcNow;
        var seeds = new[]
        {
            ("ennix", "Ennix", "Character", "Quiet intensity, close-cropped dark hair, weathered flight coat.", 5, "#cf8d63", "Green eyes; narrow scar at right brow.", 1),
            ("remora", "Remora", "Character", "Compact silhouette, amber piping, left-cheek scar.", 4, "#8ea6cc", "Scar stays left; prop defaults to left hand.", 2),
            ("magistrate", "Magistrate", "Character", "Severe profile, layered ivory and oxidized-gold ceremonial dress.", 3, "#c9b784", "Right eye obscured in formal profile.", 3),
            ("guards", "Court guards", "Character", "Paired vertical silhouettes with long ceremonial spears.", 2, "#7e8a91", "Travel in pairs unless the shot states otherwise.", 4),
            ("aerie", "The Aerie", "Location", "Cliff-borne civic complex, compressed arches, mist and sky bridge.", 7, "#88a39c", "Sky bridge remains canon when off-camera.", 5),
            ("chain-court", "Chain court", "Location", "Open tribunal chamber held by three ceremonial chains.", 4, "#a7a098", "Exactly three primary chains.", 6),
            ("wardrobe", "Magistrate formal", "Wardrobe", "Ivory layers, gold collar, two asymmetric clasps.", 2, "#b8a56c", "Two clasps; no blue fabric.", 7),
            ("prop", "Remora's seal", "Prop", "Dark alloy seal with a narrow luminous edge.", 3, "#c4765a", "Held in Remora's left hand until SH-080.", 8),
            ("style", "Skychasers style", "Style", "Painterly cinematic realism, restrained color, sculpted haze.", 6, "#7f92b7", "No glossy game-render surfaces or neon sci-fi bloom.", 9)
        };
        foreach (var seed in seeds)
        {
            db.References.Add(new ReferenceRecord
            {
                Id = seed.Item1,
                ProjectId = StoryboardStudio.Core.StudioDefaults.ProjectId,
                Name = seed.Item2,
                Category = seed.Item3,
                CurrentVersion = seed.Item5,
                LastIssuedVersion = seed.Item5,
                Status = "Authority",
                Accent = seed.Item6,
                VisualVariant = seed.Item8,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.ReferenceVersions.Add(new ReferenceVersionRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = StoryboardStudio.Core.StudioDefaults.ProjectId,
                ReferenceId = seed.Item1,
                Version = seed.Item5,
                Description = seed.Item4,
                LockedConstraint = seed.Item7,
                ContentHash = HashReference(seed.Item1, seed.Item5, seed.Item4, seed.Item7, null),
                RatifiedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string HashReference(string id, int version, string description, string constraint, string? assetHash)
    {
        var canonical = JsonSerializer.Serialize(new { id, version, description, constraint, assetHash });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task EnsureColumnAsync(StudioDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
        }
        await reader.DisposeAsync();
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    // Startup backfills sweep every project, not just the active one, so a project
    // the artist has not opened yet still gets its evidence rows on upgrade.
    private static async Task BackfillAuthoritiesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.ShotVersions.IgnoreQueryFilters().Select(x => new { x.ShotId, x.Version }).ToListAsync(cancellationToken);
        var keys = existing.Select(x => (x.ShotId, x.Version)).ToHashSet();
        var ratified = await db.Shots.IgnoreQueryFilters().Where(x => x.Approval == "Ratified").ToListAsync(cancellationToken);
        foreach (var shot in ratified.Where(shot => !keys.Contains((shot.Id, shot.Version))))
        {
            db.ShotVersions.Add(CreateAuthority(shot, shot.UpdatedAt));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task BackfillCandidatesAsync(StudioDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.CandidateVersions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var keys = existing.Select(x => (x.ShotId, x.Version)).ToHashSet();
        var shots = await db.Shots.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var shot in shots)
        {
            foreach (var candidate in existing.Where(x => x.ShotId == shot.Id)) candidate.IsCurrent = candidate.Version == shot.Version;
            if (keys.Contains((shot.Id, shot.Version))) continue;
            foreach (var candidate in existing.Where(x => x.ShotId == shot.Id))
            {
                candidate.IsCurrent = false;
                candidate.SupersededAt ??= shot.UpdatedAt;
            }
            db.CandidateVersions.Add(new CandidateVersionRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = shot.ProjectId,
                ShotId = shot.Id,
                Version = shot.Version,
                Stage = shot.Stage,
                Approval = shot.Approval,
                IsCurrent = true,
                AssetId = shot.CurrentAssetId,
                CreatedAt = shot.UpdatedAt
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public static ShotVersionRecord CreateAuthority(ShotRecord shot, DateTimeOffset ratifiedAt)
    {
        var authority = new ShotVersionRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = shot.ProjectId,
            ShotId = shot.Id,
            Version = shot.Version,
            Code = shot.Code,
            Title = shot.Title,
            Description = shot.Description,
            Stage = shot.Stage,
            Approval = "Ratified",
            DurationFrames = shot.DurationFrames,
            VisualVariant = shot.VisualVariant,
            Camera = shot.Camera,
            Action = shot.Action,
            ReferenceIdsJson = shot.ReferenceIdsJson,
            ConstraintsJson = shot.ConstraintsJson,
            ManifestHash = string.Empty,
            AssetId = shot.CurrentAssetId,
            VideoFirstFrameCandidateId = shot.VideoFirstFrameCandidateId,
            VideoLastFrameCandidateId = shot.VideoLastFrameCandidateId,
            ProductionVideoJobId = shot.ProductionVideoJobId,
            ProductionVideoAssetId = shot.ProductionVideoAssetId,
            RatifiedAt = ratifiedAt
        };
        authority.ManifestHash = ComputeAuthorityHash(authority);
        return authority;
    }

    internal static string ComputeAuthorityHash(ShotVersionRecord authority)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            authority.ShotId,
            authority.Version,
            authority.Code,
            authority.Title,
            authority.Description,
            authority.Stage,
            authority.DurationFrames,
            authority.VisualVariant,
            authority.Camera,
            authority.Action,
            authority.ReferenceIdsJson,
            authority.ConstraintsJson,
            authority.AssetId,
            authority.VideoFirstFrameCandidateId,
            authority.VideoLastFrameCandidateId,
            authority.ProductionVideoJobId,
            authority.ProductionVideoAssetId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static ShotRecord CreateShot(string code, string title, string description, string stage, string approval, int version, int duration, int sortOrder, int variant, string continuity, string camera, string action, string[] refs, string[] constraints, DateTimeOffset updatedAt)
        => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Title = title,
            Description = description,
            Stage = stage,
            Approval = approval,
            Version = version,
            DurationFrames = duration,
            SortOrder = sortOrder,
            VisualVariant = variant,
            ContinuityState = continuity,
            Camera = camera,
            Action = action,
            ReferenceIdsJson = JsonSerializer.Serialize(refs),
            ConstraintsJson = JsonSerializer.Serialize(constraints),
            UpdatedAt = updatedAt
        };
}
