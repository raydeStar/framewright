using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

public sealed record ProductionExportReadiness(
    bool CanExportProduction,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    int ShotCount,
    int RatifiedShotCount,
    int OpenNoteCount,
    int ActiveJobCount,
    DateTimeOffset ExaminedAt);

public sealed class ProductionExportBlockedException(ProductionExportReadiness readiness)
    : InvalidOperationException("The production package is blocked by unresolved delivery checks.")
{
    public ProductionExportReadiness Readiness { get; } = readiness;
}

public sealed class ProductionExportService(
    StudioDbContext db,
    AssetStore assets,
    IProjectScope projectScope,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ProductionExportReadiness> InspectAsync(CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var shots = await db.Shots.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        // Review notes are revision instructions. A superseded revision can
        // retain its unresolved historical notes without permanently blocking
        // the current production package; only notes on each live shot version
        // participate in readiness. Asset notes remain bound to immutable asset
        // bytes, so every open asset note is still actionable.
        var currentOpenShotNotes = await (
            from comment in db.Comments.AsNoTracking()
            join shot in db.Shots.AsNoTracking()
                on new { comment.ShotId, comment.Version }
                equals new { ShotId = shot.Id, Version = shot.Version }
            where comment.State == "Open"
            select comment.Id).CountAsync(cancellationToken);
        var openNotes = currentOpenShotNotes
            + await db.AssetReviewNotes.AsNoTracking().CountAsync(x => x.State == "Open", cancellationToken);
        var activeJobs = await db.Jobs.AsNoTracking()
            .CountAsync(x => x.State == nameof(JobState.Queued) || x.State == nameof(JobState.Running), cancellationToken);
        var timeline = await db.TimelineClips.AsNoTracking().ToListAsync(cancellationToken);
        var assetRecords = await db.Assets.AsNoTracking().ToListAsync(cancellationToken);
        var completedJobs = await db.Jobs.AsNoTracking()
            .Where(x => x.State == nameof(JobState.Completed) && x.Kind == "Video")
            .ToListAsync(cancellationToken);
        var generationManifests = await db.GenerationManifests.AsNoTracking().ToListAsync(cancellationToken);
        var shotAuthorities = await db.ShotVersions.AsNoTracking().ToListAsync(cancellationToken);
        var videoManifestIds = completedJobs.Where(x => x.ManifestId is not null).Select(x => x.ManifestId!.Value).ToArray();
        var videoManifests = generationManifests
            .Where(x => videoManifestIds.Contains(x.Id))
            .ToDictionary(x => x.Id);
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (shots.Count == 0) blockers.Add("The project has no shots to deliver.");

        var unratified = shots.Where(x => x.Approval != nameof(ApprovalState.Ratified)).Select(x => x.Code).ToArray();
        if (unratified.Length > 0)
            blockers.Add($"Ratify the current version of {unratified.Length} shot(s): {string.Join(", ", unratified)}.");

        var corruptManifests = generationManifests.Where(manifest => !ManifestEvidenceIsValid(manifest)).ToArray();
        if (corruptManifests.Length > 0)
            blockers.Add($"Repair {corruptManifests.Length} generation manifest(s) whose immutable JSON or duplicate evidence columns no longer match the stored hash.");

        var corruptShotAuthorities = shotAuthorities
            .Where(authority => !string.Equals(
                authority.ManifestHash,
                StudioDatabaseInitializer.ComputeAuthorityHash(authority),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (corruptShotAuthorities.Length > 0)
            blockers.Add($"Repair {corruptShotAuthorities.Length} ratified shot archive(s) whose immutable intent no longer matches the stored hash.");

        var missingCurrentAuthority = shots
            .Where(shot => shot.Approval == nameof(ApprovalState.Ratified) &&
                !shotAuthorities.Any(authority => AuthorityMatchesCurrentShot(authority, shot)))
            .Select(shot => shot.Code)
            .ToArray();
        if (missingCurrentAuthority.Length > 0)
            blockers.Add($"Re-ratify {missingCurrentAuthority.Length} shot(s) whose current approval has no exact immutable archive: {string.Join(", ", missingCurrentAuthority)}.");

        var missingFrames = shots.Where(x => x.CurrentAssetId is null).Select(x => x.Code).ToArray();
        if (missingFrames.Length > 0)
            blockers.Add($"Attach a delivered frame to {missingFrames.Length} shot(s): {string.Join(", ", missingFrames)}.");

        var missingVideos = shots.Where(x => x.Stage != nameof(ShotStage.Video)).Select(x => x.Code).ToArray();
        if (missingVideos.Length > 0)
            blockers.Add($"Approve a production video for {missingVideos.Length} shot(s): {string.Join(", ", missingVideos)}.");

        var validVideoAssets = assetRecords.Where(x => x.Kind == nameof(AssetKind.Video)).Select(x => x.Id).ToHashSet();
        var invalidVideoBindings = shots.Where(shot =>
        {
            if (shot.Stage != nameof(ShotStage.Video)) return false;
            var job = shot.ProductionVideoJobId is { } jobId
                ? completedJobs.SingleOrDefault(candidate => candidate.Id == jobId &&
                    candidate.ShotId == shot.Id && candidate.OutputAssetId == shot.ProductionVideoAssetId)
                : null;
            return job is null || shot.ProductionVideoAssetId is null || !validVideoAssets.Contains(shot.ProductionVideoAssetId.Value) ||
                job.ManifestId is null || !videoManifests.TryGetValue(job.ManifestId.Value, out var manifest) ||
                !ProductionVideoContract.IsMaxForProject(manifest, project, shot);
        }).Select(x => x.Code).ToArray();
        if (invalidVideoBindings.Length > 0)
            blockers.Add($"Bind a completed Max-quality video take matching the current project delivery format to {invalidVideoBindings.Length} shot(s): {string.Join(", ", invalidVideoBindings)}.");

        var silentDeliveryClips = timeline
            .Where(x => (x.Track == nameof(TimelineTrackKind.Voice) || x.Track == nameof(TimelineTrackKind.Music)) && x.AssetId is null)
            .Select(x => x.Label)
            .ToArray();
        if (silentDeliveryClips.Length > 0)
            blockers.Add($"Attach rendered media to {silentDeliveryClips.Length} voice/music cue(s): {string.Join(", ", silentDeliveryClips)}.");

        if (openNotes > 0) blockers.Add($"Resolve {openNotes} open review note(s).");
        if (activeJobs > 0) blockers.Add($"Wait for {activeJobs} active generation job(s) to reach a terminal state.");

        var corruptAssets = await FindMissingOrCorruptAssetsAsync(assetRecords, cancellationToken);
        if (corruptAssets.Count > 0)
            blockers.Add($"Repair {corruptAssets.Count} missing or hash-mismatched media file(s): {string.Join(", ", corruptAssets.Take(4))}.");

        var guideDialogue = timeline.Count(x => x.Track == nameof(TimelineTrackKind.Dialogue) && x.AssetId is null);
        if (guideDialogue > 0)
            warnings.Add($"{guideDialogue} dialogue guide cue(s) have no recorded media; they remain editorial text only.");

        return new ProductionExportReadiness(
            blockers.Count == 0,
            blockers,
            warnings,
            shots.Count,
            shots.Count(x => x.Approval == nameof(ApprovalState.Ratified)),
            openNotes,
            activeJobs,
            timeProvider.GetUtcNow());
    }

    public async Task<(Stream Content, string FileName)> CreateAsync(
        bool allowWorkingCopy,
        CancellationToken cancellationToken)
    {
        var readiness = await InspectAsync(cancellationToken);
        if (!allowWorkingCopy && !readiness.CanExportProduction)
            throw new ProductionExportBlockedException(readiness);

        var exportedAt = timeProvider.GetUtcNow();
        var projectRecord = await db.Projects.AsNoTracking()
            .SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var shots = await db.Shots.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var candidates = await db.CandidateVersions.AsNoTracking()
            .OrderBy(x => x.ShotId).ThenBy(x => x.Version).ToListAsync(cancellationToken);
        var shotVersions = await db.ShotVersions.AsNoTracking()
            .OrderBy(x => x.ShotId).ThenBy(x => x.Version).ToListAsync(cancellationToken);
        var authorities = await db.References.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var authorityVersions = await db.ReferenceVersions.AsNoTracking()
            .OrderBy(x => x.ReferenceId).ThenBy(x => x.Version).ToListAsync(cancellationToken);
        var manifests = (await db.GenerationManifests.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToArray();
        var jobs = (await db.Jobs.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToArray();
        var comments = (await db.Comments.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToArray();
        var reviewNotes = (await db.AssetReviewNotes.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToArray();
        var markups = (await db.FrameMarkups.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.UpdatedAt).ToArray();
        var clips = await db.TimelineClips.AsNoTracking().OrderBy(x => x.Track).ThenBy(x => x.StartFrame).ToListAsync(cancellationToken);
        var voices = await db.VoiceProfiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var collections = await db.AssetCollections.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var assetRecords = await db.Assets.AsNoTracking().OrderBy(x => x.ContentHash).ToListAsync(cancellationToken);
        var activeAssetIds = assetRecords.Select(x => x.Id).ToHashSet();
        var placements = (await db.AssetPlacements.AsNoTracking()
            .Where(x => activeAssetIds.Contains(x.AssetId))
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .ToArray();
        var scenes = await db.Scenes.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var sceneInstances = await db.SceneInstances.AsNoTracking()
            .OrderBy(x => x.SceneId).ThenBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var sceneAnnotations = (await db.SceneAnnotations.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ToArray();
        var sceneProposals = (await db.SceneProposals.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ToArray();
        var sceneBlockoutPlans = (await db.SceneBlockoutPlans.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ToArray();
        var sceneBlockoutItems = await db.SceneBlockoutItems.AsNoTracking()
            .OrderBy(x => x.PlanId).ThenBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var sceneShotBindings = (await db.SceneShotBindings.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ToArray();

        var verifiedAssets = new List<VerifiedExportAsset>(assetRecords.Count);
        foreach (var asset in assetRecords)
            verifiedAssets.Add(await VerifyAssetAsync(asset, assets.ResolveContentPath(asset), cancellationToken));

        var payload = new
        {
            // v4 adds the complete editable scene graph and immutable
            // scene-to-shot bindings. Every referenced model/still is already
            // part of the content-addressed asset inventory below.
            schemaVersion = 4,
            packageKind = readiness.CanExportProduction ? "Production" : "WorkingCopy",
            exportedAt,
            readiness,
            project = new
            {
                projectRecord.Id,
                projectRecord.Name,
                projectRecord.Production,
                projectRecord.SequenceCode,
                projectRecord.SequenceName,
                projectRecord.FramesPerSecond,
                projectRecord.AspectRatio,
                projectRecord.DeliveryWidth,
                projectRecord.DeliveryHeight,
                projectRecord.ColorSpace,
                projectRecord.AudioSampleRate,
                projectRecord.VisualStyle,
                projectRecord.WorldCanon,
                projectRecord.PromptDirectives,
                projectRecord.NegativeDirectives,
                projectRecord.UpdatedAt
            },
            policy = new
            {
                audioIsSeparate = true,
                generatedBackgroundMusicAllowed = false,
                authorityVersionsAreImmutable = true,
                visualContinuityRequiresReview = true,
                productionExportRequiresRatifiedCurrentFrames = true,
                productionExportRequiresBoundMaxVideoTakes = true,
                everyMediaFileIsContentVerified = true
            },
            shots,
            shotVersions,
            candidates,
            authorities,
            authorityVersions,
            generationManifests = manifests.Select(x => new
            {
                x.Id,
                x.ShotId,
                x.ShotCode,
                x.ShotVersion,
                x.SketchId,
                x.SketchRevision,
                x.Route,
                x.Purpose,
                x.State,
                x.CreativeBrief,
                x.AuthoritiesJson,
                x.ConstraintsJson,
                x.ManifestJson,
                x.ManifestHash,
                x.ProviderCallMade,
                x.CompositionAssetId,
                x.CompositionAssetHash,
                x.LastFrameAssetId,
                x.LastFrameAssetHash,
                x.CreatedAt
            }),
            generationJobs = jobs.Select(x => new
            {
                x.Id,
                x.ShotId,
                x.Kind,
                x.State,
                x.Backend,
                x.ManifestId,
                x.AdapterId,
                x.OutputAssetId,
                x.ProviderRequestId,
                x.Attempt,
                x.RetryOfJobId,
                x.WorkType,
                x.CreatedAt,
                x.CompletedAt
            }),
            review = new { comments, assetNotes = reviewNotes, markups },
            timeline = clips,
            voiceProfiles = voices.Select(x => new
            {
                x.Id,
                x.Name,
                x.Kind,
                x.Provider,
                x.ProviderVoiceId,
                x.CharacterName,
                x.CharacterReferenceId,
                x.SampleAssetId,
                x.ConsentAttestation,
                x.ConsentedAt,
                x.CreatedAt
            }),
            assetCollections = collections,
            assetPlacements = placements,
            scenes,
            sceneInstances,
            sceneAnnotations,
            sceneProposals,
            sceneBlockoutPlans,
            sceneBlockoutItems,
            sceneShotBindings,
            assets = verifiedAssets.Select(x => new
            {
                x.Record.Id,
                x.Record.Kind,
                x.Record.OriginalFileName,
                x.Record.MimeType,
                x.Record.Bytes,
                x.Record.Width,
                x.Record.Height,
                x.Record.DurationSeconds,
                x.Record.ContentHash,
                x.Record.DisplayName,
                x.Record.Source,
                x.Record.RevisionFamilyId,
                x.Record.RevisionNumber,
                x.Record.ParentAssetId,
                x.Record.RevisionPrompt,
                x.Record.RevisionEngine,
                x.ArchivePath
            })
        };

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"framewright-export-{Guid.NewGuid():N}.zip");
        FileStream? package = null;
        try
        {
            package = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntryAsync(archive, "production-manifest.json", manifestBytes, CompressionLevel.Optimal, cancellationToken);
                foreach (var asset in verifiedAssets)
                {
                    var entry = archive.CreateEntry(asset.ArchivePath, CompressionLevel.NoCompression);
                    await using var output = entry.Open();
                    await using var input = new FileStream(
                        asset.SourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81_920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken);
                }

                var inventory = new
                {
                    schemaVersion = 1,
                    generatedAt = exportedAt,
                    entries = new[]
                    {
                        new { path = "production-manifest.json", bytes = (long)manifestBytes.Length, sha256 = HashBytes(manifestBytes) }
                    }.Concat(verifiedAssets.Select(x => new { path = x.ArchivePath, bytes = x.Record.Bytes, sha256 = x.Record.ContentHash }))
                };
                var inventoryBytes = JsonSerializer.SerializeToUtf8Bytes(inventory, Json);
                await WriteEntryAsync(archive, "package-inventory.json", inventoryBytes, CompressionLevel.Optimal, cancellationToken);
            }

            await package.FlushAsync(cancellationToken);
            package.Position = 0;
            var label = readiness.CanExportProduction ? "production" : "working-copy";
            return (package, $"framewright-{label}-{exportedAt:yyyyMMdd-HHmmss}.zip");
        }
        catch
        {
            if (package is not null) await package.DisposeAsync();
            TryDelete(temporaryPath);
            throw;
        }
    }

    public Task<(Stream Content, string FileName)> CreateAsync(CancellationToken cancellationToken)
        => CreateAsync(allowWorkingCopy: false, cancellationToken);

    private async Task<IReadOnlyList<string>> FindMissingOrCorruptAssetsAsync(
        IReadOnlyList<AssetRecord> assetRecords,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var asset in assetRecords)
        {
            try
            {
                await VerifyAssetAsync(asset, assets.ResolveContentPath(asset), cancellationToken);
            }
            catch (InvalidDataException)
            {
                failures.Add(asset.DisplayName.Length > 0 ? asset.DisplayName : asset.OriginalFileName);
            }
        }

        return failures;
    }

    private static async Task<VerifiedExportAsset> VerifyAssetAsync(
        AssetRecord asset,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new InvalidDataException($"Asset {asset.Id} is missing from storage.");

        var file = new FileInfo(sourcePath);
        if (file.Length != asset.Bytes)
            throw new InvalidDataException($"Asset {asset.Id} has {file.Length} bytes; the catalog records {asset.Bytes}.");

        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(hash, asset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset {asset.Id} failed its SHA-256 verification.");

        return new VerifiedExportAsset(asset, sourcePath, ArchivePath(asset));
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string path,
        byte[] content,
        CompressionLevel compression,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, compression);
        await using var output = entry.Open();
        await output.WriteAsync(content, cancellationToken);
    }

    private static string ArchivePath(AssetRecord asset)
        => $"assets/{asset.ContentHash}{Path.GetExtension(asset.StoragePath).ToLowerInvariant()}";

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool ManifestEvidenceIsValid(GenerationManifestRecord manifest)
    {
        if (!string.Equals(
            manifest.ManifestHash,
            HashBytes(Encoding.UTF8.GetBytes(manifest.ManifestJson)),
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var packet = JsonNode.Parse(manifest.ManifestJson) as JsonObject;
            if (packet is null) return false;
            if (packet["creativeBrief"] is JsonValue creativeBrief &&
                !string.Equals(creativeBrief.GetValue<string>(), manifest.CreativeBrief, StringComparison.Ordinal))
                return false;
            if (packet["authorities"] is JsonNode authorities &&
                !JsonNode.DeepEquals(authorities, JsonNode.Parse(manifest.AuthoritiesJson)))
                return false;
            if (packet["constraints"] is JsonNode constraints &&
                !JsonNode.DeepEquals(constraints, JsonNode.Parse(manifest.ConstraintsJson)))
                return false;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool AuthorityMatchesCurrentShot(ShotVersionRecord authority, ShotRecord shot)
        => authority.ShotId == shot.Id &&
            authority.Version == shot.Version &&
            authority.Approval == nameof(ApprovalState.Ratified) &&
            string.Equals(authority.ManifestHash, StudioDatabaseInitializer.ComputeAuthorityHash(authority), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(authority.Code, shot.Code, StringComparison.Ordinal) &&
            string.Equals(authority.Title, shot.Title, StringComparison.Ordinal) &&
            string.Equals(authority.Description, shot.Description, StringComparison.Ordinal) &&
            string.Equals(authority.Stage, shot.Stage, StringComparison.Ordinal) &&
            authority.DurationFrames == shot.DurationFrames &&
            authority.VisualVariant == shot.VisualVariant &&
            string.Equals(authority.Camera, shot.Camera, StringComparison.Ordinal) &&
            string.Equals(authority.Action, shot.Action, StringComparison.Ordinal) &&
            string.Equals(authority.ReferenceIdsJson, shot.ReferenceIdsJson, StringComparison.Ordinal) &&
            string.Equals(authority.ConstraintsJson, shot.ConstraintsJson, StringComparison.Ordinal) &&
            authority.AssetId == shot.CurrentAssetId &&
            authority.VideoFirstFrameCandidateId == shot.VideoFirstFrameCandidateId &&
            authority.VideoLastFrameCandidateId == shot.VideoLastFrameCandidateId &&
            authority.ProductionVideoJobId == shot.ProductionVideoJobId &&
            authority.ProductionVideoAssetId == shot.ProductionVideoAssetId;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record VerifiedExportAsset(AssetRecord Record, string SourcePath, string ArchivePath);
}
