using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

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
    public async Task AWorkingPackageRoundTripsTheMvpSceneGraphIntoAnIsolatedWorkspace()
    {
        byte[] package;
        Guid sourceProjectId, sourceShotId, sourceSceneId, sourcePropId, sourceCharacterId, sourceClipId;
        Guid sourceCharacterInstanceId, sourcePropInstanceId, sourceAnnotationId, sourceBindingId, sourceCandidateId;
        string sourceSnapshotHash, sourceDerivativeHash, sourceDataRoot;

        using (var sourceFactory = new StudioApiFactory())
        using (var sourceClient = sourceFactory.CreateClient())
        {
            sourceDataRoot = sourceFactory.DataRoot;
            var studio = await sourceClient.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                ?? throw new InvalidOperationException();
            sourceProjectId = studio.Project.Id;

            var shotResponse = await sourceClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
                "SC-980", "Portable motion proof", "A rig, clip, prop, note, and frozen still travel together.",
                48, "Unbound", "Hold for review.", [], []));
            shotResponse.EnsureSuccessStatusCode();
            var shot = await shotResponse.Content.ReadFromJsonAsync<ShotSummary>()
                ?? throw new InvalidOperationException();
            sourceShotId = shot.Id;

            sourcePropId = await ImportModelAsync(sourceClient, ModelFixtures.AsymmetricBlock(), "portable-prop.glb");
            var derivativeId = await ImportModelAsync(sourceClient, ModelFixtures.DensePropRuntime(), "portable-prop-runtime.glb");
            var revision = await sourceClient.PostAsJsonAsync($"/api/assets/{sourcePropId}/revisions",
                new AddAssetRevisionRequest(derivativeId, "Use the compiler-owned runtime derivative.", "Portable graph proof"));
            revision.EnsureSuccessStatusCode();
            sourceCharacterId = await ImportModelAsync(sourceClient, ModelFixtures.RiggedFigure(), "portable-character.glb");
            sourceClipId = await ImportModelAsync(sourceClient, ModelFixtures.ClipArmRaise(), "portable-arm-raise.glb");

            sourceSceneId = await CreateSceneAsync(sourceClient, "Portable animated workshop");
            sourceCharacterInstanceId = Guid.NewGuid();
            sourcePropInstanceId = Guid.NewGuid();
            var saved = await sourceClient.PutAsJsonAsync($"/api/scenes/{sourceSceneId}", new
            {
                expectedVersion = 1,
                name = "Portable animated workshop",
                camera = Camera(),
                environment = Environment(),
                instances = new object[]
                {
                    new
                    {
                        id = sourceCharacterInstanceId, assetId = sourceCharacterId, name = "Portable lead",
                        position = (double[])[-1.5, 0d, 0d], rotation = (double[])[0d, 0.2, 0d], scale = (double[])[1d, 1d, 1d],
                        clip = new { clipAssetId = sourceClipId, clipName = "Arm raise", start = 0d, end = 2d, speed = 1d, time = 0.5d, loop = false, rootMotion = "Hold" },
                    },
                    new
                    {
                        id = sourcePropInstanceId, assetId = derivativeId, name = "Portable door",
                        position = (double[])[2d, 0d, -1d], rotation = (double[])[0d, 0d, 0d], scale = (double[])[1d, 1d, 1d],
                        motion = new { pivot = (double[])[-1d, 0d, 0d], axis = "Y", fromRadians = 0d, toRadians = 1.2d, seconds = 2d, pingPong = false },
                    },
                },
            });
            saved.EnsureSuccessStatusCode();

            var noteResponse = await sourceClient.PostAsJsonAsync($"/api/scenes/{sourceSceneId}/annotations", new
            {
                instanceId = sourcePropInstanceId,
                anchor = (double[])[0.25, 0.75, 0.1],
                camera = Camera(),
                body = "Keep the hinge readable in the final framing.",
            });
            noteResponse.EnsureSuccessStatusCode();
            using (var note = await noteResponse.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException())
                sourceAnnotationId = note.RootElement.GetProperty("id").GetGuid();

            var shotCamera = new SceneCameraSummary(0.72, 0.31, 7.25, [0.1, 0.9, -0.4], 35);
            using var rendered = await RenderStillAsync(
                sourceClient, sourceSceneId, sourceShotId, expectedSceneVersion: 2, expectedShotVersion: 1,
                shotCamera, startTime: 0, endTime: 2, stillTime: 1,
                PngHeader(studio.Project.DeliveryWidth, studio.Project.DeliveryHeight));
            rendered.EnsureSuccessStatusCode();
            var binding = await rendered.Content.ReadFromJsonAsync<SceneShotBindingSummary>()
                ?? throw new InvalidOperationException();
            sourceBindingId = binding.Id;
            sourceSnapshotHash = binding.SnapshotHash;

            var candidates = await sourceClient.GetFromJsonAsync<CandidateVersionSummary[]>($"/api/shots/{sourceShotId}/candidates")
                ?? throw new InvalidOperationException();
            sourceCandidateId = candidates.Single(x => x.IsCurrent).Id;
            package = await sourceClient.GetByteArrayAsync("/api/export/working-package");

            using var scope = sourceFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            sourceDerivativeHash = (await db.Assets.AsNoTracking().SingleAsync(x => x.Id == derivativeId)).ContentHash;
        }

        using var targetFactory = new StudioApiFactory();
        using var targetClient = targetFactory.CreateClient();
        Assert.NotEqual(sourceDataRoot, targetFactory.DataRoot);
        var importedResponse = await ImportPackageAsync(targetClient, package, "portable-mvp-graph.zip");
        importedResponse.EnsureSuccessStatusCode();
        var imported = await importedResponse.Content.ReadFromJsonAsync<PortableProjectImportSummary>()
            ?? throw new InvalidOperationException();
        Assert.NotEqual(sourceProjectId, imported.ProjectId);
        Assert.True(imported.IdsRemapped);
        (await targetClient.PostAsync($"/api/projects/{imported.ProjectId}/activate", null)).EnsureSuccessStatusCode();

        var reopened = await targetClient.GetFromJsonAsync<StudioSnapshot>("/api/studio")
            ?? throw new InvalidOperationException();
        var importedShot = reopened.Shots.Single(x => x.Code == "SC-980");
        Assert.NotEqual(sourceShotId, importedShot.Id);
        Assert.Equal(2, importedShot.Version);

        var scenes = await targetClient.GetFromJsonAsync<SceneListItem[]>("/api/scenes")
            ?? throw new InvalidOperationException();
        var importedScene = scenes.Single(x => x.Name == "Portable animated workshop");
        Assert.NotEqual(sourceSceneId, importedScene.Id);
        using var scene = await targetClient.GetFromJsonAsync<JsonDocument>($"/api/scenes/{importedScene.Id}")
            ?? throw new InvalidOperationException();
        var instances = scene.RootElement.GetProperty("instances").EnumerateArray().ToArray();
        var character = instances.Single(x => x.GetProperty("name").GetString() == "Portable lead");
        var prop = instances.Single(x => x.GetProperty("name").GetString() == "Portable door");
        var importedCharacterInstanceId = character.GetProperty("id").GetGuid();
        var importedPropInstanceId = prop.GetProperty("id").GetGuid();
        var importedCharacterId = character.GetProperty("assetId").GetGuid();
        var importedClipId = character.GetProperty("clip").GetProperty("clipAssetId").GetGuid();
        var importedPropId = prop.GetProperty("assetId").GetGuid();
        Assert.NotEqual(sourceCharacterInstanceId, importedCharacterInstanceId);
        Assert.NotEqual(sourcePropInstanceId, importedPropInstanceId);
        Assert.NotEqual(sourceCharacterId, importedCharacterId);
        Assert.NotEqual(sourceClipId, importedClipId);
        Assert.Equal("Arm raise", character.GetProperty("clip").GetProperty("clipName").GetString());
        Assert.Equal(0.5, character.GetProperty("clip").GetProperty("time").GetDouble(), 4);
        Assert.Equal("Y", prop.GetProperty("motion").GetProperty("axis").GetString());
        Assert.Equal(1.2, prop.GetProperty("motion").GetProperty("toRadians").GetDouble(), 4);
        Assert.Equal(0.72, scene.RootElement.GetProperty("camera").GetProperty("yaw").GetDouble(), 4);

        using var rig = await targetClient.GetFromJsonAsync<JsonDocument>($"/api/assets/{importedCharacterId}/model-profile")
            ?? throw new InvalidOperationException();
        Assert.True(rig.RootElement.GetProperty("rig").GetProperty("animationReady").GetBoolean());
        using var clip = await targetClient.GetFromJsonAsync<JsonDocument>($"/api/assets/{importedClipId}/model-profile")
            ?? throw new InvalidOperationException();
        Assert.Contains(clip.RootElement.GetProperty("clips").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "Arm raise" && item.GetProperty("supported").GetBoolean());
        using var sample = await targetClient.GetFromJsonAsync<JsonDocument>(
            $"/api/scenes/{importedScene.Id}/instances/{importedCharacterInstanceId}/motion-sample?time=1")
            ?? throw new InvalidOperationException();
        Assert.Equal("Character", sample.RootElement.GetProperty("kind").GetString());
        Assert.NotEmpty(sample.RootElement.GetProperty("joints").EnumerateArray());

        using var notes = await targetClient.GetFromJsonAsync<JsonDocument>($"/api/scenes/{importedScene.Id}/annotations")
            ?? throw new InvalidOperationException();
        var importedNote = Assert.Single(notes.RootElement.EnumerateArray().ToArray());
        Assert.NotEqual(sourceAnnotationId, importedNote.GetProperty("id").GetGuid());
        Assert.Equal(importedPropInstanceId, importedNote.GetProperty("instanceId").GetGuid());
        Assert.Equal(importedPropId, importedNote.GetProperty("assetId").GetGuid());
        Assert.Equal("Keep the hinge readable in the final framing.", importedNote.GetProperty("body").GetString());

        var bindings = await targetClient.GetFromJsonAsync<SceneShotBindingSummary[]>($"/api/scenes/{importedScene.Id}/shot-stills")
            ?? throw new InvalidOperationException();
        var importedBinding = Assert.Single(bindings);
        Assert.NotEqual(sourceBindingId, importedBinding.Id);
        Assert.Equal(importedShot.Id, importedBinding.ShotId);
        Assert.Equal(sourceSnapshotHash, importedBinding.SnapshotHash);
        Assert.Equal(importedShot.CurrentAssetId, importedBinding.StillAssetId);
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync(importedBinding.StillAssetUrl)).StatusCode);
        var importedCandidates = await targetClient.GetFromJsonAsync<CandidateVersionSummary[]>($"/api/shots/{importedShot.Id}/candidates")
            ?? throw new InvalidOperationException();
        var importedCandidate = importedCandidates.Single(x => x.IsCurrent);
        Assert.NotEqual(sourceCandidateId, importedCandidate.Id);
        Assert.Equal(importedBinding.StillAssetId, importedCandidate.AssetId);

        using var targetScope = targetFactory.Services.CreateScope();
        var targetDb = targetScope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var importedDerivative = await targetDb.Assets.AsNoTracking().SingleAsync(x => x.Id == importedPropId);
        Assert.Equal(sourceDerivativeHash, importedDerivative.ContentHash);
        Assert.NotNull(importedDerivative.RevisionFamilyId);
        Assert.NotNull(importedDerivative.ParentAssetId);
        Assert.NotEqual(sourcePropId, importedDerivative.ParentAssetId);
        Assert.False(Path.IsPathRooted(importedDerivative.StoragePath));
        Assert.DoesNotContain(sourceDataRoot, importedDerivative.StoragePath, StringComparison.OrdinalIgnoreCase);
        var importedParent = await targetDb.Assets.AsNoTracking().SingleAsync(x => x.Id == importedDerivative.ParentAssetId);
        Assert.Equal(imported.ProjectId, importedParent.ProjectId);
        Assert.Equal(importedDerivative.RevisionFamilyId, importedParent.RevisionFamilyId);
    }

    [Fact]
    public async Task ACompletedSceneRenderKeepsFreshOperationalIdsAndPlayableOutputAfterImportRestart()
    {
        var encoder = new PortableSceneVideoEncoder();
        byte[] package;
        Guid sourceSceneId, sourceShotId, sourceBindingId, sourceJobId, sourceOutputAssetId;
        string sourceManifestHash, sourceSnapshotHash;

        using (var sourceFactory = new StudioApiFactory(services =>
        {
            services.RemoveAll<ISceneVideoEncoder>();
            services.AddSingleton<ISceneVideoEncoder>(encoder);
        }))
        using (var sourceClient = sourceFactory.CreateClient())
        {
            var studio = await sourceClient.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                ?? throw new InvalidOperationException();
            var shotResponse = await sourceClient.PostAsJsonAsync("/api/shots", new CreateShotRequest(
                "SC-981", "Portable rendered take", "A completed exact-frame take survives a portable restart.",
                4, "Bound scene camera", "Open the workshop door.", [], []));
            shotResponse.EnsureSuccessStatusCode();
            var shot = await shotResponse.Content.ReadFromJsonAsync<ShotSummary>()
                ?? throw new InvalidOperationException();
            sourceShotId = shot.Id;

            var propId = await ImportModelAsync(sourceClient, ModelFixtures.AsymmetricBlock(), "portable-render-prop.glb");
            sourceSceneId = await CreateSceneAsync(sourceClient, "Portable rendered workshop");
            var save = await sourceClient.PutAsJsonAsync($"/api/scenes/{sourceSceneId}", new
            {
                expectedVersion = 1,
                name = "Portable rendered workshop",
                camera = Camera(),
                environment = Environment(),
                instances = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), assetId = propId, name = "Rendered door",
                        position = (double[])[0d, 0d, 0d], rotation = (double[])[0d, 0d, 0d], scale = (double[])[1d, 1d, 1d],
                        motion = new { pivot = (double[])[-1d, 0d, 0d], axis = "Y", fromRadians = 0d, toRadians = 1.2d, seconds = 1d, pingPong = false },
                    },
                },
            });
            save.EnsureSuccessStatusCode();
            var saved = await save.Content.ReadFromJsonAsync<SceneSummary>() ?? throw new InvalidOperationException();
            var camera = new SceneCameraSummary(1.1, 0.45, 6.5, [0.5, 1, 0], 35);
            using var still = await RenderStillAsync(
                sourceClient, sourceSceneId, sourceShotId, saved.Version, shot.Version, camera,
                0, shot.DurationFrames / (double)studio.Project.FramesPerSecond, 0,
                PngHeader(studio.Project.DeliveryWidth, studio.Project.DeliveryHeight));
            still.EnsureSuccessStatusCode();
            var binding = await still.Content.ReadFromJsonAsync<SceneShotBindingSummary>()
                ?? throw new InvalidOperationException();
            sourceBindingId = binding.Id;
            sourceSnapshotHash = binding.SnapshotHash;

            var ratifiedStill = await sourceClient.PostAsJsonAsync(
                $"/api/shots/{sourceShotId}/ratify",
                new RatifyRequest(binding.ShotVersion, "Approved source still for portable render proof."));
            ratifiedStill.EnsureSuccessStatusCode();
            var prepare = await sourceClient.PostAsJsonAsync(
                $"/api/scenes/{sourceSceneId}/shot-renders",
                new PrepareSceneRenderRequest(sourceBindingId));
            Assert.True(prepare.IsSuccessStatusCode, await prepare.Content.ReadAsStringAsync());
            var render = await prepare.Content.ReadFromJsonAsync<SceneRenderSummary>()
                ?? throw new InvalidOperationException();
            sourceManifestHash = render.ManifestHash;
            foreach (var index in render.MissingFrames)
            {
                using var upload = await UploadRenderFrameAsync(
                    sourceClient, render.JobId, index, PngHeader(render.Width, render.Height));
                upload.EnsureSuccessStatusCode();
            }

            var complete = await sourceClient.PostAsync($"/api/scene-renders/{render.JobId}/complete", null);
            complete.EnsureSuccessStatusCode();
            var completed = await complete.Content.ReadFromJsonAsync<SceneRenderSummary>()
                ?? throw new InvalidOperationException();
            sourceJobId = completed.JobId;
            sourceOutputAssetId = completed.OutputAssetId ?? throw new InvalidOperationException();
            Assert.True(completed.Promoted);
            Assert.Equal(1, encoder.EncodeCount);

            var promoted = (await sourceClient.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                ?? throw new InvalidOperationException()).Shots.Single(item => item.Id == sourceShotId);
            var ratifiedVideo = await sourceClient.PostAsJsonAsync(
                $"/api/shots/{sourceShotId}/ratify",
                new RatifyRequest(promoted.Version, "Approved exact-frame take for portable recovery proof."));
            ratifiedVideo.EnsureSuccessStatusCode();
            package = await sourceClient.GetByteArrayAsync("/api/export/working-package");
        }

        var targetRoot = Path.Combine(Path.GetTempPath(), "framewright-portable-render", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetRoot);
        try
        {
            PortableProjectImportSummary imported;
            Guid importedSceneId, importedShotId, importedBindingId, importedJobId, importedOutputAssetId;
            using (var targetFactory = new StudioApiFactory(targetRoot, deleteDataRoot: false))
            using (var targetClient = targetFactory.CreateClient())
            {
                var importedResponse = await ImportPackageAsync(targetClient, package, "portable-rendered-take.zip");
                importedResponse.EnsureSuccessStatusCode();
                imported = await importedResponse.Content.ReadFromJsonAsync<PortableProjectImportSummary>()
                    ?? throw new InvalidOperationException();
                (await targetClient.PostAsync($"/api/projects/{imported.ProjectId}/activate", null)).EnsureSuccessStatusCode();

                using var scope = targetFactory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
                var importedScene = await db.Scenes.AsNoTracking().SingleAsync(row => row.Name == "Portable rendered workshop");
                var importedShot = await db.Shots.AsNoTracking().SingleAsync(row => row.Code == "SC-981");
                var importedBinding = await db.SceneShotBindings.AsNoTracking().SingleAsync(row => row.ShotId == importedShot.Id);
                var importedJob = await db.Jobs.AsNoTracking().SingleAsync(row => row.WorkType == SceneRenderService.WorkType);
                var importedManifest = await db.GenerationManifests.AsNoTracking().SingleAsync(row => row.Id == importedJob.ManifestId);
                var importedCandidate = await db.CandidateVersions.AsNoTracking().SingleAsync(row => row.ShotId == importedShot.Id && row.IsCurrent);

                importedSceneId = importedScene.Id;
                importedShotId = importedShot.Id;
                importedBindingId = importedBinding.Id;
                importedJobId = importedJob.Id;
                importedOutputAssetId = importedJob.OutputAssetId ?? throw new InvalidOperationException();
                Assert.NotEqual(sourceSceneId, importedSceneId);
                Assert.NotEqual(sourceShotId, importedShotId);
                Assert.NotEqual(sourceBindingId, importedBindingId);
                Assert.NotEqual(sourceJobId, importedJobId);
                Assert.NotEqual(sourceOutputAssetId, importedOutputAssetId);
                Assert.Equal(importedJobId, importedShot.ProductionVideoJobId);
                Assert.Equal(importedOutputAssetId, importedShot.ProductionVideoAssetId);
                Assert.Equal(ApprovalState.Ratified.ToString(), importedShot.Approval);
                Assert.Equal(ShotStage.Video.ToString(), importedShot.Stage);
                Assert.Equal(importedShotId, importedManifest.ShotId);
                Assert.Equal(importedBinding.StillAssetId, importedManifest.CompositionAssetId);
                Assert.Equal(importedManifest.Id, importedCandidate.SourceManifestId);
                Assert.Equal(sourceSnapshotHash, importedBinding.SnapshotHash);
            }

            // A fresh application host on the imported data root must resolve
            // the remapped operational packet and serve the verified movie.
            using var reopenedFactory = new StudioApiFactory(targetRoot, deleteDataRoot: false);
            using var reopenedClient = reopenedFactory.CreateClient();
            var reopened = await reopenedClient.GetFromJsonAsync<StudioSnapshot>("/api/studio")
                ?? throw new InvalidOperationException();
            Assert.Equal(imported.ProjectId, reopened.Project.Id);
            var reopenedShot = reopened.Shots.Single(row => row.Id == importedShotId);
            Assert.Equal(importedJobId, reopenedShot.ProductionVideoJobId);
            Assert.Equal(importedOutputAssetId, reopenedShot.ProductionVideoAssetId);

            var renderSummary = await reopenedClient.GetFromJsonAsync<SceneRenderSummary>($"/api/scene-renders/{importedJobId}")
                ?? throw new InvalidOperationException();
            Assert.Equal(JobState.Completed, renderSummary.State);
            Assert.True(renderSummary.Promoted);
            Assert.Empty(renderSummary.MissingFrames);
            Assert.Equal(importedSceneId, renderSummary.SceneId);
            Assert.Equal(importedShotId, renderSummary.ShotId);
            Assert.Equal(importedBindingId, renderSummary.BindingId);
            Assert.Equal(importedOutputAssetId, renderSummary.OutputAssetId);
            Assert.Equal(sourceManifestHash, renderSummary.ManifestHash);
            var movie = await reopenedClient.GetByteArrayAsync(renderSummary.OutputAssetUrl);
            Assert.Equal("ftypisom", Encoding.ASCII.GetString(movie, 4, 8));
            Assert.NotEmpty(await reopenedClient.GetByteArrayAsync("/api/export/working-package"));
        }
        finally
        {
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
        }
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

    private static object Camera() => new
    {
        yaw = 0.72,
        pitch = 0.31,
        distance = 7.25d,
        target = (double[])[0.1, 0.9, -0.4],
        fieldOfView = 35d,
    };

    private static object Environment() => new
    {
        keyIntensity = 2.4d,
        keyYaw = 0.65d,
        keyPitch = 0.85d,
        ambientIntensity = 1.1d,
    };

    private static async Task<HttpResponseMessage> RenderStillAsync(
        HttpClient client,
        Guid sceneId,
        Guid shotId,
        int expectedSceneVersion,
        int expectedShotVersion,
        SceneCameraSummary camera,
        double startTime,
        double endTime,
        double stillTime,
        byte[] png)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "portable-scene-still.png");
        content.Add(new StringContent(shotId.ToString()), "shotId");
        content.Add(new StringContent(expectedSceneVersion.ToString(CultureInfo.InvariantCulture)), "expectedSceneVersion");
        content.Add(new StringContent(expectedShotVersion.ToString(CultureInfo.InvariantCulture)), "expectedShotVersion");
        content.Add(new StringContent(startTime.ToString(CultureInfo.InvariantCulture)), "startTime");
        content.Add(new StringContent(endTime.ToString(CultureInfo.InvariantCulture)), "endTime");
        content.Add(new StringContent(stillTime.ToString(CultureInfo.InvariantCulture)), "stillTime");
        content.Add(new StringContent(JsonSerializer.Serialize(camera)), "camera");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scenes/{sceneId}/shot-stills") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> UploadRenderFrameAsync(
        HttpClient client,
        Guid jobId,
        int frameIndex,
        byte[] png)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", $"frame-{frameIndex:000000}.png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/scene-renders/{jobId}/frames/{frameIndex}") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(request);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[25];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
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

    private sealed class PortableSceneVideoEncoder : ISceneVideoEncoder
    {
        public int EncodeCount { get; private set; }

        public Task<SceneVideoEncoderReadiness> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SceneVideoEncoderReadiness(true, "portable-test-ffmpeg 1", "Portable encoder ready."));
        }

        public async Task<SceneVideoEncodeResult> EncodeAsync(
            SceneVideoEncodeRequest request,
            CancellationToken cancellationToken)
        {
            EncodeCount++;
            var bytes = new byte[16];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), 16);
            Encoding.ASCII.GetBytes("ftypisom").CopyTo(bytes, 4);
            await File.WriteAllBytesAsync(request.OutputPath, bytes, cancellationToken);
            return new(true, "portable-test-ffmpeg 1", "Portable movie encoded.");
        }
    }
}
