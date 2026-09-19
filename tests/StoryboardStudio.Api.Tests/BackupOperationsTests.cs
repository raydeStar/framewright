using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

public sealed class BackupOperationsTests
{
    [Fact]
    public async Task ScheduledBackupPersistsChecksumInventoryAndSuccessState()
    {
        await using var harness = await BackupHarness.CreateAsync();
        var backup = harness.Service;

        var created = await backup.CreateScheduledAsync(CancellationToken.None);

        using (var archive = ZipFile.OpenRead(created.Path))
        {
            var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "backup-manifest.json");
            await using var manifestStream = manifestEntry.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestStream);
            Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            var inventory = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var database = Assert.Single(inventory, item => item.GetProperty("path").GetString() == "database/storyboard-studio.db");
            Assert.Equal(64, database.GetProperty("sha256").GetString()!.Length);

            var databaseEntry = archive.GetEntry("database/storyboard-studio.db");
            Assert.NotNull(databaseEntry);
            await using var databaseStream = databaseEntry!.Open();
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(databaseStream)).ToLowerInvariant();
            Assert.Equal(database.GetProperty("sha256").GetString(), actualHash);
        }

        var status = await backup.StatusAsync(CancellationToken.None);
        Assert.Equal("Succeeded", status.LastResult);
        Assert.Equal("Healthy", status.BackupHealth);
        Assert.True(status.BackupRootWritable);
        Assert.False(status.BackupOverdue);
        Assert.NotNull(status.LastAttemptAt);
        Assert.NotNull(status.LastSuccessAt);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task ScheduledBackupPersistsFailureWhenTargetIsNotADirectory()
    {
        await using var harness = await BackupHarness.CreateAsync(useBlockedBackupRoot: true);
        var blockedRoot = harness.BackupRoot;
        await File.WriteAllTextAsync(blockedRoot, "This file deliberately occupies the configured backup directory path.");
        var backup = harness.Service;

        await Assert.ThrowsAnyAsync<IOException>(() => backup.CreateScheduledAsync(CancellationToken.None));

        var status = await backup.StatusAsync(CancellationToken.None);
        Assert.Equal("Failed", status.LastResult);
        Assert.Equal("Failed", status.BackupHealth);
        Assert.False(status.BackupRootWritable);
        Assert.True(status.BackupOverdue);
        Assert.NotNull(status.LastAttemptAt);
        Assert.NotNull(status.LastError);
        Assert.Contains("IOException", status.LastError!, StringComparison.Ordinal);
    }

    private sealed class BackupHarness : IAsyncDisposable
    {
        private readonly StudioDbContext db;

        private BackupHarness(string dataRoot, string backupRoot, StudioDbContext db, BackupService service)
        {
            DataRoot = dataRoot;
            BackupRoot = backupRoot;
            this.db = db;
            Service = service;
        }

        public string DataRoot { get; }
        public string BackupRoot { get; }
        public BackupService Service { get; }

        public static async Task<BackupHarness> CreateAsync(bool useBlockedBackupRoot = false)
        {
            var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-backup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            var backupRoot = Path.Combine(dataRoot, useBlockedBackupRoot ? "blocked-backup-root" : "verified-backups");
            var projectScope = new TestProjectScope();
            var options = new DbContextOptionsBuilder<StudioDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataRoot, "backup-test.db")};Pooling=False")
                .Options;
            var db = new StudioDbContext(options, projectScope);
            await db.Database.EnsureCreatedAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:DataRoot"] = dataRoot,
                ["Studio:Backups:Root"] = backupRoot,
                ["Studio:Backups:Enabled"] = "true",
                ["Studio:Backups:IntervalHours"] = "24",
                ["Studio:Backups:RetentionCount"] = "2"
            }).Build();
            var environment = new TestEnvironment { ContentRootPath = dataRoot, WebRootPath = dataRoot };
            var service = new BackupService(db, projectScope, configuration, environment, TimeProvider.System);
            return new(dataRoot, backupRoot, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
        }
    }

    private sealed class TestProjectScope : IProjectScope
    {
        public Guid ProjectId { get; private set; } = StudioDefaults.ProjectId;
        public void Bind(Guid projectId) => ProjectId = projectId;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Framewright.BackupTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
