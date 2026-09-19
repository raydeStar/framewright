using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed record BackupStatus(
    string DatabaseIntegrity,
    int AssetCount,
    long AssetBytes,
    string Policy,
    bool Scheduled,
    int RetainedBackups,
    DateTimeOffset? LatestBackupAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    string LastResult,
    string? LastError,
    bool BackupRootWritable,
    bool BackupOverdue,
    string BackupHealth);

public sealed record ScheduledBackupResult(string Path, long Bytes, DateTimeOffset CreatedAt);

public sealed class BackupService(StudioDbContext db, IProjectScope projectScope, IConfiguration configuration, IWebHostEnvironment environment, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions BackupJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string dataRoot = StudioPaths.ResolveDataRoot(configuration, environment);
    private readonly string assetRoot = StudioPaths.ResolveAssetRoot(configuration, environment);
    private readonly string backupRoot = string.IsNullOrWhiteSpace(configuration["Studio:Backups:Root"])
        ? Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "backups")
        : Path.GetFullPath(configuration["Studio:Backups:Root"]!);
    private string StatePath => Path.Combine(dataRoot, "backup-status.json");

    public async Task<BackupStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var integrity = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "unknown";
        // A backup copies the whole database file, so its counts must describe every
        // project's assets — scoping them to the active project would understate
        // what the archive actually contains.
        var retainedInspection = InspectRetainedBackups();
        var retained = retainedInspection.Files;
        var latest = retained.OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault();
        DateTimeOffset? latestAt = latest is null ? null : new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero);
        var scheduled = configuration.GetValue("Studio:Backups:Enabled", false);
        var interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Studio:Backups:IntervalHours", 24), 1, 168));
        var state = await ReadStateAsync(cancellationToken);
        var lastSuccess = Latest(state.LastSuccessAt, latestAt);
        var backupRootCheck = scheduled
            ? RuntimePathPreflight.InspectWritableDirectory(backupRoot)
            : new RuntimePathCheck(true, "Scheduled backups are disabled.");
        var overdue = scheduled && (lastSuccess is null || timeProvider.GetUtcNow() - lastSuccess.Value >= interval);
        var lastResult = state.LastResult ?? (latestAt is null ? "NeverRun" : "Succeeded");
        var backupRootOperational = backupRootCheck.Writable && retainedInspection.Error is null;
        var health = !scheduled ? "Disabled"
            : !backupRootOperational ? "Failed"
            : string.Equals(lastResult, "Failed", StringComparison.OrdinalIgnoreCase) ? "Failed"
            : overdue ? "Overdue"
            : string.Equals(lastResult, "Running", StringComparison.OrdinalIgnoreCase) ? "Running"
            : "Healthy";
        return new(
            integrity,
            await db.Assets.IgnoreQueryFilters().CountAsync(cancellationToken),
            await db.Assets.IgnoreQueryFilters().SumAsync(x => (long?)x.Bytes, cancellationToken) ?? 0,
            "Backups contain the SQLite authority log, content-addressed assets, and a SHA-256 inventory. Environment secrets and provider keys are excluded.",
            scheduled,
            retained.Length,
            latestAt,
            state.LastAttemptAt,
            lastSuccess,
            lastResult,
            state.LastError ?? retainedInspection.Error ?? (backupRootCheck.Writable ? null : backupRootCheck.Detail),
            backupRootOperational,
            overdue,
            health);
    }

    public async Task<(Stream Content, string FileName)> CreateAsync(CancellationToken cancellationToken)
    {
        var timestamp = timeProvider.GetUtcNow();
        var backupRoot = Path.Combine(Path.GetTempPath(), "storyboard-studio-backups");
        Directory.CreateDirectory(backupRoot);
        var nonce = Guid.NewGuid().ToString("N");
        var databaseCopy = Path.Combine(backupRoot, $"storyboard-studio-{nonce}.db");
        var archivePath = Path.Combine(backupRoot, $"framewright-backup-{timestamp:yyyyMMdd-HHmmss}-{nonce}.zip");
        try
        {
            var source = (SqliteConnection)db.Database.GetDbConnection(); if (source.State != System.Data.ConnectionState.Open) await source.OpenAsync(cancellationToken);
            await using (var destination = new SqliteConnection($"Data Source={databaseCopy};Pooling=False")) { await destination.OpenAsync(cancellationToken); source.BackupDatabase(destination); }
            await EnsureSqliteIntegrityAsync(databaseCopy, cancellationToken);

            var content = new List<BackupSource>
            {
                new("database/storyboard-studio.db", databaseCopy, CompressionLevel.Optimal)
            };
            if (Directory.Exists(assetRoot))
            {
                content.AddRange(Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories)
                    .Select(path => new
                    {
                        Path = path,
                        Relative = Path.GetRelativePath(assetRoot, path).Replace(Path.DirectorySeparatorChar, '/')
                    })
                    .Where(item => !item.Relative.Equals(".staging", StringComparison.OrdinalIgnoreCase)
                                   && !item.Relative.StartsWith(".staging/", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.Relative, StringComparer.Ordinal)
                    .Select(item => new BackupSource($"assets/{item.Relative}", item.Path, CompressionLevel.NoCompression)));
            }

            var inventory = new List<BackupManifestEntry>(content.Count);
            foreach (var item in content)
            {
                await using var input = OpenSequentialRead(item.SourcePath);
                inventory.Add(new(item.ArchivePath, input.Length, await ComputeSha256Async(input, cancellationToken)));
            }

            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81_920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in content)
                    archive.CreateEntryFromFile(item.SourcePath, item.ArchivePath, item.Compression);

                var metadata = archive.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
                await using var metadataStream = metadata.Open();
                await JsonSerializer.SerializeAsync(metadataStream, new BackupManifest(
                    SchemaVersion: 2,
                    Product: "Framewright",
                    CreatedAt: timestamp,
                    ActiveProjectId: projectScope.ProjectId,
                    Includes: ["SQLite database", "content-addressed assets", "SHA-256 content inventory"],
                    Excludes: ["environment variables", "API keys", "provider credentials", "external ComfyUI workflows and models"],
                    Entries: inventory), BackupJson, cancellationToken);
            }
            await VerifyArchiveAsync(archivePath, cancellationToken);
            File.Delete(databaseCopy); var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.DeleteOnClose); return (stream, Path.GetFileName(archivePath));
        }
        catch
        {
            TryDelete(databaseCopy);
            TryDelete(archivePath);
            throw;
        }
    }

    public async Task<ScheduledBackupResult> CreateScheduledAsync(CancellationToken cancellationToken)
    {
        var attemptedAt = timeProvider.GetUtcNow();
        await TryWriteStateAsync(new(attemptedAt, null, "Running", null), CancellationToken.None);
        string? stagingPath = null;
        try
        {
            Directory.CreateDirectory(backupRoot);
            var package = await CreateAsync(cancellationToken);
            await using var content = package.Content;
            var finalPath = Path.Combine(backupRoot, package.FileName);
            stagingPath = finalPath + ".partial";
            await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await content.CopyToAsync(output, cancellationToken);
            await VerifyArchiveAsync(stagingPath, cancellationToken);
            File.Move(stagingPath, finalPath);
            PruneScheduledBackups();
            var file = new FileInfo(finalPath);
            var completedAt = timeProvider.GetUtcNow();
            await TryWriteStateAsync(new(attemptedAt, completedAt, "Succeeded", null), CancellationToken.None);
            return new(finalPath, file.Length, completedAt);
        }
        catch (Exception error)
        {
            if (stagingPath is not null) TryDelete(stagingPath);
            var result = error is OperationCanceledException ? "Canceled" : "Failed";
            await TryWriteStateAsync(new(attemptedAt, null, result, ConciseError(error)), CancellationToken.None);
            throw;
        }
    }

    private void PruneScheduledBackups()
    {
        var retention = Math.Clamp(configuration.GetValue("Studio:Backups:RetentionCount", 7), 1, 90);
        foreach (var old in Directory.EnumerateFiles(backupRoot, "framewright-backup-*.zip", SearchOption.TopDirectoryOnly)
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(retention))
            TryDelete(old.FullName);
    }

    private static async Task VerifyArchiveAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var metadata = archive.GetEntry("backup-manifest.json")
            ?? throw new InvalidDataException("The backup did not contain its manifest.");
        BackupManifest manifest;
        await using (var stream = metadata.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, BackupJson, cancellationToken)
                ?? throw new InvalidDataException("The backup manifest was empty.");

        if (manifest.SchemaVersion != 2 || !string.Equals(manifest.Product, "Framewright", StringComparison.Ordinal))
            throw new InvalidDataException("The backup manifest version or product was not recognized.");
        if (manifest.Entries.Count == 0 || manifest.Entries.All(item => item.Path != "database/storyboard-studio.db"))
            throw new InvalidDataException("The backup manifest did not inventory its SQLite database.");

        var manifestEntries = new Dictionary<string, BackupManifestEntry>(StringComparer.Ordinal);
        foreach (var item in manifest.Entries)
        {
            ValidateArchivePath(item.Path);
            if (item.Bytes < 0 || item.Sha256.Length != 64 || item.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException($"The backup manifest entry is invalid: {item.Path}");
            if (!manifestEntries.TryAdd(item.Path, item))
                throw new InvalidDataException($"The backup manifest contains a duplicate path: {item.Path}");
        }

        var contentEntries = archive.Entries
            .Where(entry => entry.FullName != "backup-manifest.json" && !entry.FullName.EndsWith('/'))
            .ToArray();
        if (contentEntries.Length != manifestEntries.Count)
            throw new InvalidDataException("The backup content did not match its manifest inventory.");

        foreach (var entry in contentEntries)
        {
            if (!manifestEntries.TryGetValue(entry.FullName, out var expected))
                throw new InvalidDataException($"The backup contains an unlisted entry: {entry.FullName}");
            if (entry.Length != expected.Bytes)
                throw new InvalidDataException($"The backup entry length changed: {entry.FullName}");
            await using var stream = entry.Open();
            var actualHash = await ComputeSha256Async(stream, cancellationToken);
            if (!actualHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The backup entry checksum failed: {entry.FullName}");
        }
    }

    private async Task<BackupAttemptState> ReadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(StatePath)) return new(null, null, null, null);
            await using var stream = OpenSequentialRead(StatePath);
            return await JsonSerializer.DeserializeAsync<BackupAttemptState>(stream, BackupJson, cancellationToken)
                ?? new(null, null, "StateUnavailable", "The backup status file was empty.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, null, "StateUnavailable", $"The backup status file could not be read: {ConciseError(error)}");
        }
    }

    private async Task TryWriteStateAsync(BackupAttemptState state, CancellationToken cancellationToken)
    {
        var temporaryPath = StatePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(dataRoot);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8_192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, state, BackupJson, cancellationToken);
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        catch (IOException)
        {
            // The backup itself remains more important than its telemetry file.
            // RuntimePathPreflight will still surface an unwritable data root.
        }
        catch (UnauthorizedAccessException)
        {
            // The backup itself remains more important than its telemetry file.
            // RuntimePathPreflight will still surface an unwritable data root.
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task EnsureSqliteIntegrityAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The SQLite backup failed its integrity check: {result ?? "unknown"}");
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();

    private static FileStream OpenSequentialRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private (FileInfo[] Files, string? Error) InspectRetainedBackups()
    {
        try
        {
            var files = Directory.Exists(backupRoot)
                ? Directory.EnumerateFiles(backupRoot, "framewright-backup-*.zip", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .ToArray()
                : [];
            return (files, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ([], $"The backup directory could not be inspected: {ConciseError(error)}");
        }
    }

    private static void ValidateArchivePath(string path)
    {
        var segments = path.Split('/');
        var allowedRoot = path.StartsWith("assets/", StringComparison.Ordinal) || path == "database/storyboard-studio.db";
        if (!allowedRoot || (path.Length > 0 && path[0] == '/') || path.Contains('\\')
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            throw new InvalidDataException($"The backup manifest contains an unsafe path: {path}");
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second)
        => first is null ? second : second is null ? first : first > second ? first : second;

    private static string ConciseError(Exception error)
    {
        var message = $"{error.GetType().Name}: {error.Message}";
        return message.Length <= 500 ? message : message[..500];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record BackupSource(string ArchivePath, string SourcePath, CompressionLevel Compression);
    private sealed record BackupManifestEntry(string Path, long Bytes, string Sha256);
    private sealed record BackupManifest(
        int SchemaVersion,
        string Product,
        DateTimeOffset CreatedAt,
        Guid ActiveProjectId,
        IReadOnlyList<string> Includes,
        IReadOnlyList<string> Excludes,
        IReadOnlyList<BackupManifestEntry> Entries);
    private sealed record BackupAttemptState(
        DateTimeOffset? LastAttemptAt,
        DateTimeOffset? LastSuccessAt,
        string? LastResult,
        string? LastError);
}

/// <summary>Maintains a small, verified rolling backup set without asking the
/// artist to remember an operations ritual at the end of a creative session.</summary>
public sealed partial class ScheduledBackupWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ScheduledBackupWorker> logger) : BackgroundService
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Scheduled Framewright backup verified at {Path} ({Bytes} bytes). The archive gremlins remain unemployed.")]
    private static partial void LogBackupVerified(ILogger logger, string path, long bytes);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Scheduled Framewright backup failed. Existing backups were left untouched.")]
    private static partial void LogBackupFailure(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Studio:Backups:Enabled", false)) return;
        var interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("Studio:Backups:IntervalHours", 24), 1, 168));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
                var status = await backup.StatusAsync(stoppingToken);
                if (status.LatestBackupAt is null || timeProvider.GetUtcNow() - status.LatestBackupAt >= interval)
                {
                    var created = await backup.CreateScheduledAsync(stoppingToken);
                    LogBackupVerified(logger, created.Path, created.Bytes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                LogBackupFailure(logger, error);
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

}
