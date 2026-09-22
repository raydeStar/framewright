using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Tests;

public sealed class PortableProjectTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AWorkingPackageImportsAsASeparateEditableProjectWithFreshIds()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var source = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio") ?? throw new InvalidOperationException();
        var modelId = await ImportModelAsync(client, ModelFixtures.AsymmetricBlock(), "portable-block.glb");
        var revisedModelId = await ImportModelAsync(client, ModelFixtures.DenseProp(), "portable-block-revised.glb");
        var revision = await client.PostAsJsonAsync($"/api/assets/{modelId}/revisions",
            new AddAssetRevisionRequest(revisedModelId, "Add denser detail.", "Portable project test"));
        revision.EnsureSuccessStatusCode();
        var sceneId = await CreateSceneAsync(client, "Portable workshop");
        var instanceId = Guid.NewGuid();
        var saved = await client.PutAsJsonAsync($"/api/scenes/{sceneId}", new
        {
            expectedVersion = 1,
            name = "Portable workshop",
            camera = new { yaw = 1.1, pitch = 0.35, distance = 7.5, target = (double[])[0d, 0.75, 0d], fieldOfView = 38d },
            environment = new { keyIntensity = 2.8, keyYaw = 0.5, keyPitch = 0.9, ambientIntensity = 1.05 },
            instances = new[]
            {
                new { id = instanceId, assetId = modelId, name = "Workshop block", position = (double[])[1d, 0d, -2d], rotation = (double[])[0d, 0.4, 0d], scale = (double[])[1d, 1d, 1d] }
            }
        });
        saved.EnsureSuccessStatusCode();

        var package = await client.GetByteArrayAsync("/api/export/working-package");
        var first = await ImportPackageAsync(client, package, "portable-workshop.zip");
        first.EnsureSuccessStatusCode();
        var imported = await first.Content.ReadFromJsonAsync<PortableProjectImportSummary>() ?? throw new InvalidOperationException();

        Assert.NotEqual(source.Project.Id, imported.ProjectId);
        Assert.True(imported.IdsRemapped);
        Assert.Equal(1, imported.SceneCount);
        Assert.True(imported.AssetCount > 0);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var sourceModel = await db.Assets.IgnoreQueryFilters().SingleAsync(a => a.Id == modelId);
            var importedModel = await db.Assets.IgnoreQueryFilters().SingleAsync(
                x => x.ProjectId == imported.ProjectId && x.ContentHash == sourceModel.ContentHash);
            Assert.NotEqual(modelId, importedModel.Id);
            Assert.NotNull(sourceModel.RevisionFamilyId);
            Assert.NotNull(importedModel.RevisionFamilyId);
            Assert.NotEqual(sourceModel.RevisionFamilyId, importedModel.RevisionFamilyId);
            var importedFamily = await db.Assets.IgnoreQueryFilters()
                .Where(x => x.ProjectId == imported.ProjectId && x.RevisionFamilyId == importedModel.RevisionFamilyId)
                .ToListAsync();
            Assert.Equal(2, importedFamily.Count);
            Assert.Contains(importedFamily, x => x.ParentAssetId == importedModel.Id);
            var importedScene = await db.Scenes.IgnoreQueryFilters().SingleAsync(x => x.ProjectId == imported.ProjectId && x.Name == "Portable workshop");
            Assert.NotEqual(sceneId, importedScene.Id);
            var importedInstance = await db.SceneInstances.IgnoreQueryFilters().SingleAsync(x => x.SceneId == importedScene.Id);
            Assert.NotEqual(instanceId, importedInstance.Id);
            Assert.Equal(importedModel.Id, importedInstance.AssetId);
            Assert.Equal(1d, importedInstance.PositionX);
            Assert.Equal(-2d, importedInstance.PositionZ);
        }

        var activated = await client.PostAsync($"/api/projects/{imported.ProjectId}/activate", null);
        activated.EnsureSuccessStatusCode();
        var reopened = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio") ?? throw new InvalidOperationException();
        Assert.Equal(imported.ProjectId, reopened.Project.Id);
        var scenes = await client.GetFromJsonAsync<SceneListItem[]>("/api/scenes") ?? throw new InvalidOperationException();
        var importedSceneId = Assert.Single(scenes, x => x.Name == "Portable workshop").Id;
        using var scene = await client.GetFromJsonAsync<JsonDocument>($"/api/scenes/{importedSceneId}") ?? throw new InvalidOperationException();
        var instance = Assert.Single(scene.RootElement.GetProperty("instances").EnumerateArray());
        Assert.Equal("Workshop block", instance.GetProperty("name").GetString());
        var modelContent = await client.GetAsync(instance.GetProperty("contentUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, modelContent.StatusCode);

        // Re-importing the same immutable manifest is legal in another project.
        // Content bytes deduplicate on disk while every relational identity is fresh.
        var second = await ImportPackageAsync(client, package, "portable-workshop-again.zip");
        second.EnsureSuccessStatusCode();
        var importedAgain = await second.Content.ReadFromJsonAsync<PortableProjectImportSummary>() ?? throw new InvalidOperationException();
        Assert.NotEqual(imported.ProjectId, importedAgain.ProjectId);
        Assert.NotEqual(imported.Name, importedAgain.Name);
    }

    [Fact]
    public async Task AChecksumFailureCreatesNoPartialProject()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        var before = (await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? []).Length;
        var package = await client.GetByteArrayAsync("/api/export/working-package");
        var corrupt = RewriteEntry(package, "production-manifest.json", bytes =>
        {
            var changed = bytes.ToArray();
            changed[^2] ^= 0x01;
            return changed;
        });

        var response = await ImportPackageAsync(client, corrupt, "corrupt.zip");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("checksum", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var after = (await client.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? []).Length;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ASelfConsistentPackageCannotSmuggleMalformedOrMisboundModelBytes()
    {
        byte[] package;
        using (var sourceFactory = new StudioApiFactory())
        using (var sourceClient = sourceFactory.CreateClient())
        {
            await ImportModelAsync(sourceClient, ModelFixtures.AsymmetricBlock(), "valid-before-export.glb");
            package = await sourceClient.GetByteArrayAsync("/api/export/working-package");
        }
        using var targetFactory = new StudioApiFactory();
        using var targetClient = targetFactory.CreateClient();
        var before = (await targetClient.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? []).Length;

        var malformed = ForgeModelAsset(package, Encoding.UTF8.GetBytes("this is not a GLB"), rewriteContentIdentity: true);
        var malformedResponse = await ImportPackageAsync(targetClient, malformed, "forged-model.zip");
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.Contains("GLB validation", await malformedResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // A different, valid GLB is still a forgery when the manifest keeps the
        // original content address. Updating the ZIP inventory must not bless it.
        var misbound = ForgeModelAsset(package, ModelFixtures.DenseProp(), rewriteContentIdentity: false);
        var misboundResponse = await ImportPackageAsync(targetClient, misbound, "misbound-model.zip");
        Assert.Equal(HttpStatusCode.BadRequest, misboundResponse.StatusCode);
        Assert.Contains("content hash", await misboundResponse.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var after = (await targetClient.GetFromJsonAsync<ProjectListItem[]>("/api/projects") ?? []).Length;
        Assert.Equal(before, after);
    }

    private static async Task<Guid> CreateSceneAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/scenes", new { name });
        response.EnsureSuccessStatusCode();
        using var scene = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return scene.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ImportModelAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var asset = await response.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        return asset.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> ImportPackageAsync(HttpClient client, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "file", fileName);
        return client.PostAsync("/api/projects/import", content);
    }

    private static byte[] RewriteEntry(byte[] package, string path, Func<byte[], byte[]> rewrite)
    {
        var stream = new MemoryStream();
        stream.Write(package);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(path) ?? throw new InvalidOperationException();
            byte[] original;
            using (var input = entry.Open())
            using (var buffer = new MemoryStream()) { input.CopyTo(buffer); original = buffer.ToArray(); }
            entry.Delete();
            var replacement = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var output = replacement.Open();
            output.Write(rewrite(original));
        }
        return stream.ToArray();
    }

    private static byte[] ForgeModelAsset(byte[] package, byte[] forgedBytes, bool rewriteContentIdentity)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                files.Add(entry.FullName, buffer.ToArray());
            }
        }

        var manifest = JsonNode.Parse(files["production-manifest.json"])!.AsObject();
        var model = manifest["assets"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(node => string.Equals(node["kind"]!.GetValue<string>(), "Model", StringComparison.Ordinal));
        var oldPath = model["archivePath"]!.GetValue<string>();
        var hash = Convert.ToHexString(SHA256.HashData(forgedBytes)).ToLowerInvariant();
        var newPath = rewriteContentIdentity ? $"assets/{hash}.glb" : oldPath;
        if (rewriteContentIdentity) model["contentHash"] = hash;
        model["bytes"] = forgedBytes.LongLength;
        model["archivePath"] = newPath;
        files.Remove(oldPath);
        files[newPath] = forgedBytes;
        files["production-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        files.Remove("package-inventory.json");
        files["package-inventory.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            generatedAt = DateTimeOffset.UtcNow,
            entries = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                path = pair.Key,
                bytes = pair.Value.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
            }).ToArray(),
        }, Json);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }
}
