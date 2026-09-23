using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The sample label tells a first-time artist that the production they landed
/// in is a demo. It must never land on work that is theirs: an upgraded
/// workstation, a project they created, or a copy they imported.
/// </summary>
public sealed class SampleProjectTests
{
    [Fact]
    public async Task AFreshDatabaseLabelsOnlyTheSeededDemoAsTheSample()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio") ?? throw new InvalidOperationException();
        Assert.True(snapshot.Project.IsSample);

        var created = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            "Lighthouse", "Lighthouse", "SQ-01", "Sequence 1", 24, "16:9", 1920, 1080));
        created.EnsureSuccessStatusCode();
        var summary = await created.Content.ReadFromJsonAsync<ProjectSummary>() ?? throw new InvalidOperationException();
        Assert.False(summary.IsSample);

        var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? throw new InvalidOperationException();
        Assert.True(Assert.Single(projects, x => x.Id == StudioDefaults.ProjectId).IsSample);
        Assert.False(Assert.Single(projects, x => x.Id == summary.Id).IsSample);
    }

    [Fact]
    public async Task AnUpgradedWorkstationKeepsEveryProjectAsTheArtistsOwn()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-sample-upgrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");
        try
        {
            using (var baseline = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var baselineClient = baseline.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await baselineClient.GetAsync("/health/ready")).StatusCode);
            }

            // Reproduce a workstation from before v20: no sample column, no ledger
            // row. Its first project is exactly what an artist may have renamed and
            // filled with their own work.
            await using (var legacy = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var downgrade = legacy.CreateCommand();
                downgrade.CommandText = """
                    DELETE FROM "SchemaMigrations" WHERE "Id" = '20260923-sample-project-flag-v20';
                    ALTER TABLE "Projects" DROP COLUMN "IsSample";
                    """;
                await downgrade.ExecuteNonQueryAsync();
            }

            using var upgraded = new StudioApiFactory(dataRoot, deleteDataRoot: false);
            using var client = upgraded.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? throw new InvalidOperationException();
            Assert.All(projects, project => Assert.False(project.IsSample));

            await using var verified = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verified.OpenAsync();
            await using var ledger = verified.CreateCommand();
            ledger.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Id\" = '20260923-sample-project-flag-v20';";
            Assert.Equal(1L, (long)(await ledger.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReseedingAnEmptiedWorkstationDoesNotRelabelTheArtistsProject()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-sample-reseed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "test.db");
        try
        {
            using (var baseline = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var baselineClient = baseline.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await baselineClient.GetAsync("/health/ready")).StatusCode);
            }

            // The artist has made the first project their own and deleted every
            // shot. The next start re-seeds demo shots, as it always has; that must
            // not turn their project back into "the sample".
            await using (var owned = new SqliteConnection($"Data Source={databasePath};Pooling=False;Foreign Keys=False"))
            {
                await owned.OpenAsync();
                await using var claim = owned.CreateCommand();
                claim.CommandText = """
                    UPDATE "Projects" SET "IsSample" = 0, "Name" = 'The Lantern Trial';
                    DELETE FROM "Shots";
                    """;
                await claim.ExecuteNonQueryAsync();
            }

            using var restarted = new StudioApiFactory(dataRoot, deleteDataRoot: false);
            using var client = restarted.CreateClient();
            var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? throw new InvalidOperationException();
            var project = Assert.Single(projects, x => x.Id == StudioDefaults.ProjectId);
            Assert.Equal("The Lantern Trial", project.Name);
            Assert.False(project.IsSample);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AnImportedCopyOfTheSampleBelongsToTheArtist()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        // Packages are untrusted input. Exports never carry the flag, so claim it
        // by hand, re-hashing the manifest as a determined editor would.
        var package = ClaimSample(await client.GetByteArrayAsync("/api/export/working-package"));

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", "sample-copy.zip");
        var response = await client.PostAsync("/api/projects/import", content);
        response.EnsureSuccessStatusCode();
        var imported = await response.Content.ReadFromJsonAsync<PortableProjectImportSummary>() ?? throw new InvalidOperationException();

        var projects = await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? throw new InvalidOperationException();
        Assert.False(Assert.Single(projects, x => x.Id == imported.ProjectId).IsSample);
        Assert.True(Assert.Single(projects, x => x.Id == StudioDefaults.ProjectId).IsSample);
    }

    private static byte[] ClaimSample(byte[] package)
    {
        using var output = new MemoryStream();
        output.Write(package);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            var manifestEntry = archive.GetEntry("production-manifest.json") ?? throw new InvalidOperationException();
            JsonNode manifest;
            using (var reader = manifestEntry.Open()) manifest = JsonNode.Parse(reader) ?? throw new InvalidOperationException();
            manifest["project"]!["isSample"] = true;
            var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            manifestEntry.Delete();
            using (var writer = archive.CreateEntry("production-manifest.json").Open()) writer.Write(manifestBytes);

            var inventoryEntry = archive.GetEntry("package-inventory.json") ?? throw new InvalidOperationException();
            JsonNode inventory;
            using (var reader = inventoryEntry.Open()) inventory = JsonNode.Parse(reader) ?? throw new InvalidOperationException();
            var listed = FindFileEntry(inventory, "production-manifest.json") ?? throw new InvalidOperationException("The inventory does not list the manifest.");
            listed["bytes"] = manifestBytes.LongLength;
            listed["sha256"] = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            inventoryEntry.Delete();
            using (var writer = archive.CreateEntry("package-inventory.json").Open()) writer.Write(Encoding.UTF8.GetBytes(inventory.ToJsonString()));
        }
        return output.ToArray();
    }

    private static JsonObject? FindFileEntry(JsonNode? node, string path) => node switch
    {
        JsonObject obj when obj["path"]?.GetValue<string>() == path => obj,
        JsonObject obj => obj.Select(pair => FindFileEntry(pair.Value, path)).FirstOrDefault(found => found is not null),
        JsonArray array => array.Select(item => FindFileEntry(item, path)).FirstOrDefault(found => found is not null),
        _ => null
    };
}
