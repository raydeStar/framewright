using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed record PortableProjectImportSummary(
    Guid ProjectId, string Name, int ShotCount, int AssetCount, int SceneCount,
    int SceneShotBindingCount, bool IdsRemapped, string Detail);

/// <summary>
/// Imports the editable project package produced by <see cref="ProductionExportService"/>.
///
/// A package is untrusted input. Every byte is inventoried and hashed before a
/// database row is staged, every archive path is constrained, and the imported
/// graph receives fresh database identities so it cannot collide with the
/// workspace it is joining. Immutable JSON evidence keeps its original ids and
/// hashes; database relations are remapped around it.
/// </summary>
public sealed class PortableProjectImportService(
    StudioDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ICompilerSkeletonProfiles skeletonProfiles,
    TimeProvider timeProvider)
{
    public const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxInventoryBytes = 16L * 1024 * 1024;
    private const long MaxManifestBytes = 64L * 1024 * 1024;
    private const int MaxEntries = 20_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string assetRoot = StudioPaths.ResolveAssetRoot(configuration, environment);

    public async Task<RepositoryResult<PortableProjectImportSummary>> ImportAsync(
        IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxPackageBytes)
            return RepositoryResult<PortableProjectImportSummary>.Invalid(
                $"Project packages must be between 1 byte and {MaxPackageBytes / (1024 * 1024)} MB.");

        var stagingRoot = Path.Combine(assetRoot, ".staging", $"project-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await using var input = file.OpenReadStream();
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var entries = IndexEntries(archive);
            if (entries.Count > MaxEntries)
                return RepositoryResult<PortableProjectImportSummary>.Invalid(
                    $"The package contains {entries.Count:N0} entries; the supported limit is {MaxEntries:N0}.");

            var inventory = await ReadRequiredAsync<PackageInventory>(
                entries, "package-inventory.json", MaxInventoryBytes, cancellationToken);
            if (inventory is null || inventory.SchemaVersion != 1 || inventory.Entries is null)
                return RepositoryResult<PortableProjectImportSummary>.Invalid("The package inventory is missing or unsupported.");
            var inventoryError = await VerifyInventoryAsync(entries, inventory, cancellationToken);
            if (inventoryError is not null)
                return RepositoryResult<PortableProjectImportSummary>.Invalid(inventoryError);

            var manifest = await ReadRequiredAsync<PortableProjectManifest>(
                entries, "production-manifest.json", MaxManifestBytes, cancellationToken);
            if (manifest is null || manifest.SchemaVersion != 5 || !string.Equals(manifest.Product, "Framewright", StringComparison.Ordinal))
                return RepositoryResult<PortableProjectImportSummary>.Invalid(
                    "Only a Framewright editable project package with schema version 5 can be imported.");
            if (manifest.Project is null)
                return RepositoryResult<PortableProjectImportSummary>.Invalid("The package has no project record.");
            if (!HasCompleteGraph(manifest))
                return RepositoryResult<PortableProjectImportSummary>.Invalid("The project package has a missing or null data collection.");
            if (manifest.Assets.Select(asset => asset.Id).Distinct().Count() != manifest.Assets.Length)
                return RepositoryResult<PortableProjectImportSummary>.Invalid("The package contains duplicate asset identities.");
            if (manifest.Assets.Select(asset => asset.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Assets.Length)
                return RepositoryResult<PortableProjectImportSummary>.Invalid("The package contains duplicate project asset hashes.");
            var assetError = ValidateAssetContracts(manifest.Assets, inventory.Entries.Select(item => item!).ToArray());
            if (assetError is not null)
                return RepositoryResult<PortableProjectImportSummary>.Invalid(assetError);

            var sourceProjectId = manifest.Project.Id;
            if (sourceProjectId == Guid.Empty || manifest.SourceProjectId != sourceProjectId || !AllRowsBelongTo(sourceProjectId, manifest))
                return RepositoryResult<PortableProjectImportSummary>.Invalid(
                    "The package mixes rows from different projects and cannot be imported safely.");

            var stagedAssets = await StageAssetsAsync(entries, manifest.Assets, stagingRoot, cancellationToken);
            var imported = await PersistAsync(manifest, stagedAssets, cancellationToken);
            return RepositoryResult<PortableProjectImportSummary>.Ok(imported);
        }
        catch (InvalidDataException error)
        {
            return RepositoryResult<PortableProjectImportSummary>.Invalid(error.Message);
        }
        catch (JsonException)
        {
            return RepositoryResult<PortableProjectImportSummary>.Invalid("The project manifest could not be read.");
        }
        catch (DbUpdateException)
        {
            return RepositoryResult<PortableProjectImportSummary>.Invalid(
                "The project package violates an editable-data constraint and was not imported.");
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private async Task<PortableProjectImportSummary> PersistAsync(
        PortableProjectManifest package,
        IReadOnlyDictionary<Guid, StagedAsset> stagedAssets,
        CancellationToken cancellationToken)
    {
        var sourceProjectId = package.Project!.Id;
        var projectId = Guid.NewGuid();
        var assetIds = NewIds(package.Assets.Select(x => x.Id));
        var revisionFamilyIds = NewIds(package.Assets
            .Where(x => x.RevisionFamilyId is not null)
            .Select(x => x.RevisionFamilyId!.Value)
            .Distinct());
        var collectionIds = NewIds(package.AssetCollections.Select(x => x.Id));
        var shotIds = NewIds(package.Shots.Select(x => x.Id));
        var candidateIds = NewIds(package.Candidates.Select(x => x.Id));
        var manifestIds = NewIds(package.GenerationManifests.Select(x => x.Id));
        var jobIds = NewIds(package.GenerationJobs.Select(x => x.Id));
        var sketchIds = NewIds(package.Sketches.Select(x => x.Id));
        var voiceIds = NewIds(package.VoiceProfiles.Select(x => x.Id));
        var sceneIds = NewIds(package.Scenes.Select(x => x.Id));
        var instanceIds = NewIds(package.SceneInstances.Select(x => x.Id));
        var planIds = NewIds(package.SceneBlockoutPlans.Select(x => x.Id));
        var planItemIds = NewIds(package.SceneBlockoutItems.Select(x => x.Id));
        var compositionIds = NewIds(package.MusicCompositions.Select(x => x.Id));
        var compositionRevisionIds = NewIds(package.MusicCompositionRevisions.Select(x => x.Id));

        var projectName = await AvailableProjectNameAsync(package.Project.Name, cancellationToken);
        var project = package.Project;
        project.Id = projectId;
        project.Name = projectName;
        project.IsActive = false;
        project.UpdatedAt = timeProvider.GetUtcNow();

        foreach (var row in package.AssetCollections) { row.Id = Required(collectionIds, row.Id, "collection"); row.ProjectId = projectId; }
        foreach (var row in package.Assets)
        {
            var sourceId = row.Id;
            var staged = stagedAssets[sourceId];
            row.Id = Required(assetIds, sourceId, "asset");
            row.ProjectId = projectId;
            row.CollectionId = Optional(collectionIds, row.CollectionId, "collection");
            row.RevisionFamilyId = Optional(revisionFamilyIds, row.RevisionFamilyId, "asset revision family");
            row.ParentAssetId = Optional(assetIds, row.ParentAssetId, "parent asset");
            row.StoragePath = staged.StoragePath;
        }
        foreach (var row in package.Shots)
        {
            var sourceId = row.Id; row.Id = Required(shotIds, sourceId, "shot"); row.ProjectId = projectId;
            row.CurrentAssetId = Optional(assetIds, row.CurrentAssetId, "current frame");
            row.VideoFirstFrameCandidateId = Optional(candidateIds, row.VideoFirstFrameCandidateId, "first-frame candidate");
            row.VideoLastFrameCandidateId = Optional(candidateIds, row.VideoLastFrameCandidateId, "last-frame candidate");
            row.ProductionVideoJobId = Optional(jobIds, row.ProductionVideoJobId, "video job");
            row.ProductionVideoAssetId = Optional(assetIds, row.ProductionVideoAssetId, "video asset");
        }
        foreach (var row in package.ShotVersions)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.ShotId = Required(shotIds, row.ShotId, "shot");
            row.AssetId = Optional(assetIds, row.AssetId, "shot-version asset");
            row.VideoFirstFrameCandidateId = Optional(candidateIds, row.VideoFirstFrameCandidateId, "first-frame candidate");
            row.VideoLastFrameCandidateId = Optional(candidateIds, row.VideoLastFrameCandidateId, "last-frame candidate");
            row.ProductionVideoJobId = Optional(jobIds, row.ProductionVideoJobId, "video job");
            row.ProductionVideoAssetId = Optional(assetIds, row.ProductionVideoAssetId, "video asset");
        }
        foreach (var row in package.Candidates)
        {
            var sourceId = row.Id; row.Id = Required(candidateIds, sourceId, "candidate"); row.ProjectId = projectId;
            row.ShotId = Required(shotIds, row.ShotId, "shot");
            row.AssetId = Optional(assetIds, row.AssetId, "candidate asset");
            row.SourceManifestId = Optional(manifestIds, row.SourceManifestId, "generation manifest");
        }
        foreach (var row in package.Comments) { row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.ShotId = Required(shotIds, row.ShotId, "shot"); }
        foreach (var row in package.AssetReviewNotes) { row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.AssetId = Required(assetIds, row.AssetId, "asset"); }
        foreach (var row in package.ShotVisualAudits)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.ShotId = Required(shotIds, row.ShotId, "shot");
            row.AssetId = Required(assetIds, row.AssetId, "asset");
        }
        foreach (var row in package.Sketches)
        {
            var sourceId = row.Id; row.Id = Required(sketchIds, sourceId, "sketch"); row.ProjectId = projectId;
            row.ShotId = Required(shotIds, row.ShotId, "shot");
            row.UnderlayAssetId = Optional(assetIds, row.UnderlayAssetId, "underlay asset");
            row.CompositionAssetId = Optional(assetIds, row.CompositionAssetId, "composition asset");
        }
        foreach (var row in package.PosePresets) { row.Id = Guid.NewGuid(); row.ProjectId = projectId; }
        foreach (var row in package.ShotRevisionProposals) { row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.ShotId = Required(shotIds, row.ShotId, "shot"); }
        foreach (var row in package.FrameMarkups) { row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.ShotId = Required(shotIds, row.ShotId, "shot"); }
        foreach (var row in package.GenerationManifests)
        {
            var sourceId = row.Id; row.Id = Required(manifestIds, sourceId, "manifest"); row.ProjectId = projectId;
            row.ShotId = RequiredOrEmpty(shotIds, row.ShotId, "shot"); row.SketchId = RequiredOrEmpty(sketchIds, row.SketchId, "sketch");
            row.CompositionAssetId = Optional(assetIds, row.CompositionAssetId, "composition asset");
            row.LastFrameAssetId = Optional(assetIds, row.LastFrameAssetId, "last-frame asset");
        }
        foreach (var row in package.GenerationJobs)
        {
            var sourceId = row.Id; row.Id = Required(jobIds, sourceId, "job"); row.ProjectId = projectId;
            row.ShotId = RequiredOrEmpty(shotIds, row.ShotId, "shot");
            row.ManifestId = Optional(manifestIds, row.ManifestId, "manifest");
            row.OutputAssetId = Optional(assetIds, row.OutputAssetId, "job output");
            row.RetryOfJobId = Optional(jobIds, row.RetryOfJobId, "prior job");
            if (row.State is "Queued" or "Running")
            {
                row.State = "Failed";
                row.Error = "Imported project: an in-flight external job is preserved as interrupted and was not resumed.";
                row.CompletedAt = timeProvider.GetUtcNow();
            }
            row.LeaseOwner = null; row.LeaseExpiresAt = null; row.NextAttemptAt = null;
        }
        foreach (var row in package.References) { row.ProjectId = projectId; }
        foreach (var row in package.ReferenceVersions)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId;
            row.ImageAssetId = Optional(assetIds, row.ImageAssetId, "authority image");
        }
        foreach (var row in package.Timeline)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId;
            row.AssetId = Optional(assetIds, row.AssetId, "timeline asset");
            row.VoiceProfileId = Optional(voiceIds, row.VoiceProfileId, "voice profile");
        }
        foreach (var row in package.VoiceProfiles)
        {
            var sourceId = row.Id; row.Id = Required(voiceIds, sourceId, "voice profile"); row.ProjectId = projectId;
            row.SampleAssetId = Optional(assetIds, row.SampleAssetId, "voice sample");
        }
        foreach (var row in package.AssetPlacements)
        {
            row.Id = Guid.NewGuid(); row.AssetId = Required(assetIds, row.AssetId, "asset"); row.ShotId = Required(shotIds, row.ShotId, "shot");
        }
        foreach (var row in package.Scenes) { var sourceId = row.Id; row.Id = Required(sceneIds, sourceId, "scene"); row.ProjectId = projectId; }
        foreach (var row in package.SceneInstances)
        {
            var sourceId = row.Id; row.Id = Required(instanceIds, sourceId, "scene instance"); row.ProjectId = projectId;
            row.SceneId = Required(sceneIds, row.SceneId, "scene"); row.AssetId = Optional(assetIds, row.AssetId, "model asset");
            row.ClipAssetId = Optional(assetIds, row.ClipAssetId, "clip asset"); row.SourcePlanId = Optional(planIds, row.SourcePlanId, "blockout plan");
        }
        foreach (var row in package.SceneAnnotations)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.SceneId = Required(sceneIds, row.SceneId, "scene");
            row.InstanceId = Required(instanceIds, row.InstanceId, "scene instance"); row.AssetId = Required(assetIds, row.AssetId, "model asset");
        }
        foreach (var row in package.SceneProposals)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.SceneId = Required(sceneIds, row.SceneId, "scene");
            row.InstanceId = Required(instanceIds, row.InstanceId, "scene instance");
        }
        foreach (var row in package.SceneBlockoutPlans)
        {
            var sourceId = row.Id; row.Id = Required(planIds, sourceId, "blockout plan"); row.ProjectId = projectId;
            row.ReferenceAssetId = Required(assetIds, row.ReferenceAssetId, "reference asset");
            row.SceneId = Optional(sceneIds, row.SceneId, "scene");
        }
        foreach (var row in package.SceneBlockoutItems)
        {
            var sourceId = row.Id; row.Id = Required(planItemIds, sourceId, "blockout item"); row.ProjectId = projectId;
            row.PlanId = Required(planIds, row.PlanId, "blockout plan"); row.MatchAssetId = Optional(assetIds, row.MatchAssetId, "matched asset");
            row.InstanceId = Optional(instanceIds, row.InstanceId, "scene instance");
        }
        foreach (var row in package.SceneShotBindings)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId; row.SceneId = Required(sceneIds, row.SceneId, "scene");
            row.ShotId = Required(shotIds, row.ShotId, "shot"); row.StillAssetId = Required(assetIds, row.StillAssetId, "still asset");
        }
        foreach (var row in package.MusicCompositions)
        {
            var sourceId = row.Id; row.Id = Required(compositionIds, sourceId, "music composition"); row.ProjectId = projectId;
            row.CurrentRevisionId = Required(compositionRevisionIds, row.CurrentRevisionId, "music revision");
        }
        foreach (var row in package.MusicCompositionRevisions)
        {
            var sourceId = row.Id; row.Id = Required(compositionRevisionIds, sourceId, "music revision"); row.ProjectId = projectId;
            row.CompositionId = Required(compositionIds, row.CompositionId, "music composition");
            row.ParentRevisionId = Optional(compositionRevisionIds, row.ParentRevisionId, "parent music revision");
        }
        foreach (var row in package.MusicRenders)
        {
            row.Id = Guid.NewGuid(); row.ProjectId = projectId;
            row.CompositionRevisionId = Required(compositionRevisionIds, row.CompositionRevisionId, "music revision");
            row.JobId = Required(jobIds, row.JobId, "job"); row.AssetId = Required(assetIds, row.AssetId, "music asset");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var materializedPaths = new List<string>();
        try
        {
            db.Projects.Add(project);
            db.AssetCollections.AddRange(package.AssetCollections);
            db.Assets.AddRange(package.Assets.Select(x => x.ToRecord()));
            db.Shots.AddRange(package.Shots); db.ShotVersions.AddRange(package.ShotVersions);
            db.CandidateVersions.AddRange(package.Candidates); db.Comments.AddRange(package.Comments);
            db.AssetReviewNotes.AddRange(package.AssetReviewNotes); db.ShotVisualAudits.AddRange(package.ShotVisualAudits);
            db.SketchDocuments.AddRange(package.Sketches); db.PosePresets.AddRange(package.PosePresets);
            db.ShotRevisionProposals.AddRange(package.ShotRevisionProposals); db.FrameMarkups.AddRange(package.FrameMarkups);
            db.GenerationManifests.AddRange(package.GenerationManifests); db.Jobs.AddRange(package.GenerationJobs);
            db.References.AddRange(package.References); db.ReferenceVersions.AddRange(package.ReferenceVersions);
            db.VoiceProfiles.AddRange(package.VoiceProfiles); db.TimelineClips.AddRange(package.Timeline);
            db.AssetPlacements.AddRange(package.AssetPlacements);
            db.Scenes.AddRange(package.Scenes); db.SceneInstances.AddRange(package.SceneInstances);
            db.SceneAnnotations.AddRange(package.SceneAnnotations); db.SceneProposals.AddRange(package.SceneProposals);
            db.SceneBlockoutPlans.AddRange(package.SceneBlockoutPlans); db.SceneBlockoutItems.AddRange(package.SceneBlockoutItems);
            db.SceneShotBindings.AddRange(package.SceneShotBindings);
            db.MusicCompositions.AddRange(package.MusicCompositions);
            db.MusicCompositionRevisions.AddRange(package.MusicCompositionRevisions); db.MusicRenders.AddRange(package.MusicRenders);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid(), Type = "PortableProjectImported", TargetType = "Project", TargetId = projectId.ToString(),
                PayloadJson = JsonSerializer.Serialize(new { sourceProjectId, projectName, package.ExportedAt, assets = package.Assets.Length, scenes = package.Scenes.Length }),
                CreatedAt = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
            MaterializeAssets(stagedAssets.Values, materializedPaths);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            DeleteMaterializedAssets(materializedPaths);
            throw;
        }

        return new(projectId, projectName, package.Shots.Length, package.Assets.Length, package.Scenes.Length,
            package.SceneShotBindings.Length, true,
            "Imported as a separate project with fresh workspace identities. Immutable source evidence and content hashes were preserved.");
    }

    private async Task<string> AvailableProjectNameAsync(string requested, CancellationToken cancellationToken)
    {
        var clean = string.IsNullOrWhiteSpace(requested) ? "Imported project" : requested.Trim();
        if (!await db.Projects.AnyAsync(x => x.Name == clean, cancellationToken)) return clean;
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = $"{clean} (imported {suffix})";
            if (!await db.Projects.AnyAsync(x => x.Name == candidate, cancellationToken)) return candidate;
        }
        throw new InvalidDataException("A unique project name could not be assigned to the import.");
    }

    private static Dictionary<Guid, Guid> NewIds(IEnumerable<Guid> sourceIds)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var id in sourceIds)
        {
            if (id == Guid.Empty || !result.TryAdd(id, Guid.NewGuid()))
                throw new InvalidDataException("The package contains an empty or duplicate identity.");
        }
        return result;
    }

    private static Guid Required(IReadOnlyDictionary<Guid, Guid> ids, Guid source, string kind)
        => ids.TryGetValue(source, out var mapped) ? mapped : throw new InvalidDataException($"The package references a missing {kind}.");

    private static Guid RequiredOrEmpty(IReadOnlyDictionary<Guid, Guid> ids, Guid source, string kind)
        => source == Guid.Empty ? Guid.Empty : Required(ids, source, kind);

    private static Guid? Optional(IReadOnlyDictionary<Guid, Guid> ids, Guid? source, string kind)
        => source is null ? null : Required(ids, source.Value, kind);

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var archiveEntryCount = 0;
        foreach (var entry in archive.Entries)
        {
            if (++archiveEntryCount > MaxEntries)
                throw new InvalidDataException($"The package contains more than the supported {MaxEntries:N0} entries.");
            if (entry.FullName.EndsWith('/')) continue;
            ValidateArchivePath(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException($"The package contains a duplicate entry: {entry.FullName}");
        }
        return entries;
    }

    private static async Task<T?> ReadRequiredAsync<T>(
        Dictionary<string, ZipArchiveEntry> entries, string path, long maxBytes, CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(path, out var entry)) throw new InvalidDataException($"The package is missing {path}.");
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"The package metadata entry {path} exceeds its supported size limit.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
    }

    private static async Task<string?> VerifyInventoryAsync(
        Dictionary<string, ZipArchiveEntry> entries,
        PackageInventory inventory,
        CancellationToken cancellationToken)
    {
        var inventoryEntries = inventory.Entries ?? [];
        if (inventoryEntries.Length == 0) return "The package inventory is empty.";
        try
        {
            if (inventoryEntries.Sum(item => checked(item?.Bytes ?? -1)) > MaxExpandedBytes)
                return $"The package expands beyond the supported {MaxExpandedBytes / (1024 * 1024)} MB limit.";
        }
        catch (OverflowException) { return "The package inventory size is invalid."; }
        var expected = new Dictionary<string, PackageInventoryEntry>(StringComparer.Ordinal);
        foreach (var item in inventoryEntries)
        {
            if (item is null) return "The package inventory contains a null entry.";
            ValidateArchivePath(item.Path);
            if (item.Bytes < 0 || !ValidHash(item.Sha256) || !expected.TryAdd(item.Path, item))
                return $"The package inventory contains an invalid entry: {item.Path}";
        }
        var contentEntries = entries.Where(pair => pair.Key != "package-inventory.json").ToArray();
        if (contentEntries.Length != expected.Count) return "The package content does not match its inventory.";
        foreach (var (path, entry) in contentEntries)
        {
            if (!expected.TryGetValue(path, out var item)) return $"The package contains an unlisted entry: {path}";
            if (entry.Length != item.Bytes) return $"The package entry length changed: {path}";
            await using var stream = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) return $"The package entry checksum failed: {path}";
        }
        return null;
    }

    private async Task<IReadOnlyDictionary<Guid, StagedAsset>> StageAssetsAsync(
        Dictionary<string, ZipArchiveEntry> entries,
        IReadOnlyList<PortableAssetRecord> assets,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, StagedAsset>();
        foreach (var asset in assets)
        {
            ValidateArchivePath(asset.ArchivePath);
            if (!asset.ArchivePath.StartsWith("assets/", StringComparison.Ordinal) || !ValidHash(asset.ContentHash))
                throw new InvalidDataException("An asset has an invalid archive path or content hash.");
            if (!entries.TryGetValue(asset.ArchivePath, out var entry) || entry.Length != asset.Bytes)
                throw new InvalidDataException($"The package is missing the bytes for asset {asset.Id}.");
            var extension = Path.GetExtension(asset.ArchivePath).ToLowerInvariant();
            var storagePath = $"{asset.ContentHash[..2]}/{asset.ContentHash}{extension}";
            var targetPath = Path.Combine(assetRoot, storagePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(targetPath))
            {
                await using var existing = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var existingHash = Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken)).ToLowerInvariant();
                if (existing.Length != asset.Bytes || !existingHash.Equals(asset.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Stored content at {storagePath} does not match the imported asset hash.");
                await ValidatePayloadAsync(asset, targetPath, cancellationToken);
                result.Add(asset.Id, new(storagePath, null, targetPath));
                continue;
            }
            var stagedPath = Path.Combine(stagingRoot, $"{asset.Id:N}{extension}");
            await using (var source = entry.Open())
            await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(output, cancellationToken);
            await ValidatePayloadAsync(asset, stagedPath, cancellationToken);
            result.Add(asset.Id, new(storagePath, stagedPath, targetPath));
        }
        return result;
    }

    private async Task ValidatePayloadAsync(PortableAssetRecord asset, string path, CancellationToken cancellationToken)
    {
        var kind = Enum.Parse<AssetKind>(asset.Kind);
        if (kind == AssetKind.Model)
        {
            var inspection = GlbModelInspector.Inspect(
                await File.ReadAllBytesAsync(path, cancellationToken),
                skeletonProfiles: skeletonProfiles.Load());
            if (!inspection.Ok)
                throw new InvalidDataException($"Model asset {asset.Id} failed GLB validation: {inspection.Error}");
            return;
        }
        var error = await AssetStore.ValidatePortablePayloadAsync(
            path, kind, asset.MimeType, asset.Width, asset.Height, cancellationToken);
        if (error is not null)
            throw new InvalidDataException($"{kind} asset {asset.Id} failed payload validation: {error}.");
    }

    private static void MaterializeAssets(IEnumerable<StagedAsset> stagedAssets, List<string> created)
    {
        foreach (var asset in stagedAssets.Where(asset => asset.StagedPath is not null))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(asset.TargetPath)!);
            if (File.Exists(asset.TargetPath)) continue;
            File.Move(asset.StagedPath!, asset.TargetPath);
            created.Add(asset.TargetPath);
        }
    }

    private static void DeleteMaterializedAssets(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool AllRowsBelongTo(Guid projectId, PortableProjectManifest package)
    {
        IEnumerable<Guid> ids = package.Assets.Select(x => x.ProjectId)
            .Concat(package.AssetCollections.Select(x => x.ProjectId)).Concat(package.Shots.Select(x => x.ProjectId))
            .Concat(package.ShotVersions.Select(x => x.ProjectId)).Concat(package.Candidates.Select(x => x.ProjectId))
            .Concat(package.Comments.Select(x => x.ProjectId)).Concat(package.AssetReviewNotes.Select(x => x.ProjectId))
            .Concat(package.ShotVisualAudits.Select(x => x.ProjectId)).Concat(package.Sketches.Select(x => x.ProjectId))
            .Concat(package.PosePresets.Select(x => x.ProjectId)).Concat(package.ShotRevisionProposals.Select(x => x.ProjectId))
            .Concat(package.FrameMarkups.Select(x => x.ProjectId)).Concat(package.GenerationManifests.Select(x => x.ProjectId))
            .Concat(package.GenerationJobs.Select(x => x.ProjectId)).Concat(package.References.Select(x => x.ProjectId))
            .Concat(package.ReferenceVersions.Select(x => x.ProjectId)).Concat(package.Timeline.Select(x => x.ProjectId))
            .Concat(package.VoiceProfiles.Select(x => x.ProjectId)).Concat(package.Scenes.Select(x => x.ProjectId))
            .Concat(package.SceneInstances.Select(x => x.ProjectId)).Concat(package.SceneAnnotations.Select(x => x.ProjectId))
            .Concat(package.SceneProposals.Select(x => x.ProjectId)).Concat(package.SceneBlockoutPlans.Select(x => x.ProjectId))
            .Concat(package.SceneBlockoutItems.Select(x => x.ProjectId)).Concat(package.SceneShotBindings.Select(x => x.ProjectId))
            .Concat(package.MusicCompositions.Select(x => x.ProjectId)).Concat(package.MusicCompositionRevisions.Select(x => x.ProjectId))
            .Concat(package.MusicRenders.Select(x => x.ProjectId));
        return ids.All(id => id == projectId);
    }

    private static string? ValidateAssetContracts(
        IEnumerable<PortableAssetRecord> assets,
        IEnumerable<PackageInventoryEntry> inventoryEntries)
    {
        var inventory = inventoryEntries.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            if (!Enum.TryParse<AssetKind>(asset.Kind, out var kind))
                return $"Asset {asset.Id} has an unsupported media kind.";
            var limit = kind switch
            {
                AssetKind.Image => AssetStore.MaxImageBytes,
                AssetKind.Model => AssetStore.MaxModelBytes,
                AssetKind.Audio or AssetKind.Video => AssetStore.MaxMediaBytes,
                _ => 0,
            };
            if (asset.Bytes <= 0 || asset.Bytes > limit)
                return $"Asset {asset.Id} exceeds the supported {kind} size contract.";
            var extension = Path.GetExtension(asset.ArchivePath).ToLowerInvariant();
            var expectedPath = $"assets/{asset.ContentHash}{extension}";
            if (!asset.ArchivePath.Equals(expectedPath, StringComparison.Ordinal))
                return $"Asset {asset.Id} is not stored at its own content address.";
            if (!inventory.TryGetValue(asset.ArchivePath, out var inventoried)
                || inventoried.Bytes != asset.Bytes
                || !inventoried.Sha256.Equals(asset.ContentHash, StringComparison.OrdinalIgnoreCase))
                return $"Asset {asset.Id} does not match its inventory content hash and length.";
            if (kind == AssetKind.Model
                && (extension != ".glb" || !string.Equals(asset.MimeType, "model/gltf-binary", StringComparison.OrdinalIgnoreCase)))
                return $"Model asset {asset.Id} is not labeled as a GLB payload.";
        }
        return null;
    }

    private static bool HasCompleteGraph(PortableProjectManifest package)
        => Present(package.Assets) && Present(package.AssetCollections) && Present(package.Shots)
           && Present(package.ShotVersions) && Present(package.Candidates) && Present(package.Comments)
           && Present(package.AssetReviewNotes) && Present(package.ShotVisualAudits) && Present(package.Sketches)
           && Present(package.PosePresets) && Present(package.ShotRevisionProposals) && Present(package.FrameMarkups)
           && Present(package.GenerationManifests) && Present(package.GenerationJobs) && Present(package.References)
           && Present(package.ReferenceVersions) && Present(package.Timeline) && Present(package.VoiceProfiles)
           && Present(package.AssetPlacements) && Present(package.Scenes) && Present(package.SceneInstances)
           && Present(package.SceneAnnotations) && Present(package.SceneProposals) && Present(package.SceneBlockoutPlans)
           && Present(package.SceneBlockoutItems) && Present(package.SceneShotBindings) && Present(package.MusicCompositions)
           && Present(package.MusicCompositionRevisions) && Present(package.MusicRenders);

    private static bool Present<T>(T[]? rows) where T : class
        => rows is not null && rows.All(row => row is not null);

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains('\\')
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"The package contains an unsafe archive path: {path}");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record StagedAsset(string StoragePath, string? StagedPath, string TargetPath);
    private sealed record PackageInventory(int SchemaVersion, DateTimeOffset GeneratedAt, PackageInventoryEntry?[]? Entries);
    private sealed record PackageInventoryEntry(string Path, long Bytes, string Sha256);

    private sealed class PortableProjectManifest
    {
        public int SchemaVersion { get; set; }
        public string Product { get; set; } = "";
        public DateTimeOffset ExportedAt { get; set; }
        public Guid SourceProjectId { get; set; }
        public ProjectRecord? Project { get; set; }
        public ShotRecord[] Shots { get; set; } = [];
        public ShotVersionRecord[] ShotVersions { get; set; } = [];
        public CandidateVersionRecord[] Candidates { get; set; } = [];
        public ReferenceRecord[] References { get; set; } = [];
        public ReferenceVersionRecord[] ReferenceVersions { get; set; } = [];
        public GenerationManifestRecord[] GenerationManifests { get; set; } = [];
        public JobRecord[] GenerationJobs { get; set; } = [];
        public CommentRecord[] Comments { get; set; } = [];
        public AssetReviewNoteRecord[] AssetReviewNotes { get; set; } = [];
        public ShotVisualAuditRecord[] ShotVisualAudits { get; set; } = [];
        public SketchDocumentRecord[] Sketches { get; set; } = [];
        public PosePresetRecord[] PosePresets { get; set; } = [];
        public ShotRevisionProposalRecord[] ShotRevisionProposals { get; set; } = [];
        public FrameMarkupRecord[] FrameMarkups { get; set; } = [];
        public TimelineClipRecord[] Timeline { get; set; } = [];
        public VoiceProfileRecord[] VoiceProfiles { get; set; } = [];
        public MusicCompositionRecord[] MusicCompositions { get; set; } = [];
        public MusicCompositionRevisionRecord[] MusicCompositionRevisions { get; set; } = [];
        public MusicRenderRecord[] MusicRenders { get; set; } = [];
        public AssetCollectionRecord[] AssetCollections { get; set; } = [];
        public AssetPlacementRecord[] AssetPlacements { get; set; } = [];
        public SceneRecord[] Scenes { get; set; } = [];
        public SceneInstanceRecord[] SceneInstances { get; set; } = [];
        public SceneAnnotationRecord[] SceneAnnotations { get; set; } = [];
        public SceneProposalRecord[] SceneProposals { get; set; } = [];
        public SceneBlockoutPlanRecord[] SceneBlockoutPlans { get; set; } = [];
        public SceneBlockoutItemRecord[] SceneBlockoutItems { get; set; } = [];
        public SceneShotBindingRecord[] SceneShotBindings { get; set; } = [];
        public PortableAssetRecord[] Assets { get; set; } = [];
    }

    private sealed class PortableAssetRecord
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string Kind { get; set; } = "";
        public string OriginalFileName { get; set; } = "";
        public string MimeType { get; set; } = "";
        public long Bytes { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        public string ContentHash { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string DisplayName { get; set; } = "";
        public Guid? CollectionId { get; set; }
        public string TagsJson { get; set; } = "[]";
        public string Notes { get; set; } = "";
        public string Source { get; set; } = "Imported";
        public bool IsArchived { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public Guid? RevisionFamilyId { get; set; }
        public int? RevisionNumber { get; set; }
        public bool IsCurrentRevision { get; set; }
        public Guid? ParentAssetId { get; set; }
        public string RevisionPrompt { get; set; } = "";
        public string RevisionEngine { get; set; } = "";
        public DateTimeOffset? PreparationAcceptedAt { get; set; }
        public string PreparationAcceptedBy { get; set; } = "";
        public string PreparationAcceptanceNote { get; set; } = "";
        public bool PreparationTopologyChanged { get; set; }
        public string StoragePath { get; set; } = "";

        public AssetRecord ToRecord() => new()
        {
            Id = Id, ProjectId = ProjectId, Kind = Kind, OriginalFileName = OriginalFileName, MimeType = MimeType,
            Bytes = Bytes, Width = Width, Height = Height, DurationSeconds = DurationSeconds, ContentHash = ContentHash,
            StoragePath = StoragePath, CreatedAt = CreatedAt, DisplayName = DisplayName, CollectionId = CollectionId,
            TagsJson = TagsJson, Notes = Notes, Source = Source, IsArchived = IsArchived, UpdatedAt = UpdatedAt,
            RevisionFamilyId = RevisionFamilyId, RevisionNumber = RevisionNumber, IsCurrentRevision = IsCurrentRevision,
            ParentAssetId = ParentAssetId, RevisionPrompt = RevisionPrompt, RevisionEngine = RevisionEngine,
            PreparationAcceptedAt = PreparationAcceptedAt, PreparationAcceptedBy = PreparationAcceptedBy,
            PreparationAcceptanceNote = PreparationAcceptanceNote, PreparationTopologyChanged = PreparationTopologyChanged
        };
    }
}
