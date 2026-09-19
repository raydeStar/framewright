using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

public sealed class DiagnosticBundleService(
    StudioDbContext db,
    BuildIdentityService buildIdentity,
    RuntimeReadinessService readiness,
    IntegrationDiscoveryService integrations,
    BackupService backups,
    ProductionExportService production,
    WorkflowLibrary workflows,
    IWebHostEnvironment environment,
    TimeProvider timeProvider)
{
    private static readonly string[] IncludedEvidence = ["operational status", "redacted integration capability", "recent job outcomes", "workflow and voice lock hashes"];
    private static readonly string[] ExcludedEvidence = ["credentials", "prompts", "project names", "shot and authority content", "filenames", "filesystem paths", "images", "audio", "video"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<(Stream Content, string FileName)> CreateAsync(CancellationToken cancellationToken)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var runtime = await readiness.InspectAsync(cancellationToken);
        var integrationStatus = await integrations.InspectAsync(cancellationToken);
        var backup = await backups.StatusAsync(cancellationToken);
        var export = await production.InspectAsync(cancellationToken);
        var jobOutcomes = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .Select(job => new
            {
                job.Id,
                job.WorkType,
                job.Kind,
                job.State,
                job.Progress,
                job.Backend,
                job.Attempt,
                job.CreatedAt,
                job.CompletedAt,
                job.LastHeartbeatAt,
                HasError = job.Error != null
            })
            .ToListAsync(cancellationToken);
        var recentJobs = jobOutcomes
            .OrderByDescending(job => job.CreatedAt)
            .Take(100)
            .ToArray();

        var documents = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["backup.json"] = Serialize(new
            {
                backup.DatabaseIntegrity,
                backup.AssetCount,
                backup.AssetBytes,
                backup.Scheduled,
                backup.RetainedBackups,
                backup.LatestBackupAt,
                backup.LastAttemptAt,
                backup.LastSuccessAt,
                backup.LastResult,
                backup.BackupRootWritable,
                backup.BackupOverdue,
                backup.BackupHealth,
                HasError = backup.LastError != null
            }),
            ["build.json"] = Serialize(buildIdentity.Current),
            ["integrations.json"] = Serialize(integrationStatus.Select(item => new
            {
                item.Id,
                item.Name,
                item.State,
                item.Headline,
                item.CanInspect,
                item.CanSubmit,
                item.CheckedAt
            })),
            ["jobs.json"] = Serialize(recentJobs),
            ["production-readiness.json"] = Serialize(new
            {
                export.CanExportProduction,
                BlockerCount = export.Blockers.Count,
                WarningCount = export.Warnings.Count,
                export.ShotCount,
                export.RatifiedShotCount,
                export.OpenNoteCount,
                export.ActiveJobCount,
                export.ExaminedAt
            }),
            ["runtime.json"] = Serialize(new
            {
                runtime.Status,
                Checks = runtime.Checks.Select(check => new { check.Id, check.State, check.Required }),
                runtime.Queue,
                runtime.CheckedAt,
                runtime.Build
            }),
            ["voice-lock.json"] = Serialize(HashOptionalFile(FindPackagedFile("tools", "voice", "models.lock.json"))),
            ["workflows.json"] = Serialize(HashWorkflows())
        };

        var inventory = documents.Select(item => new
        {
            path = item.Key,
            bytes = item.Value.LongLength,
            sha256 = Sha256(item.Value)
        }).ToArray();
        documents["diagnostics-manifest.json"] = Serialize(new
        {
            schemaVersion = 1,
            product = "Framewright",
            generatedAt,
            build = buildIdentity.Current,
            includes = IncludedEvidence,
            excludes = ExcludedEvidence,
            entries = inventory
        });

        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var document in documents)
            {
                var entry = archive.CreateEntry(document.Key, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(document.Value, cancellationToken);
            }
        }
        output.Position = 0;
        var safeCommit = buildIdentity.Current.Commit.Length >= 12
            ? buildIdentity.Current.Commit[..12]
            : buildIdentity.Current.Commit;
        return (output, $"framewright-diagnostics-{safeCommit}-{generatedAt:yyyyMMdd-HHmmss}.zip");
    }

    private object[] HashWorkflows()
    {
        var root = workflows.Root;
        var files = workflows.LoadIndex()
            .Select(entry => entry.File)
            .Append("library.json")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return files.Select(file =>
        {
            var path = Path.Combine(root, file);
            return (object)new
            {
                file = Path.GetFileName(file),
                present = File.Exists(path),
                bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                sha256 = File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : "unavailable"
            };
        }).ToArray();
    }

    private static object HashOptionalFile(string path)
        => new
        {
            file = Path.GetFileName(path),
            present = File.Exists(path),
            bytes = File.Exists(path) ? new FileInfo(path).Length : 0,
            sha256 = File.Exists(path) ? Sha256(File.ReadAllBytes(path)) : "unavailable"
        };

    private string FindPackagedFile(params string[] segments)
    {
        var probe = new DirectoryInfo(environment.ContentRootPath);
        while (probe is not null)
        {
            var candidate = Path.Combine([probe.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            probe = probe.Parent;
        }
        return Path.Combine([environment.ContentRootPath, .. segments]);
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
