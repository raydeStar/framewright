using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StoryboardStudio.Api.Services;

internal sealed record GeneratedImageImport(AssetSummary Asset, bool Created);

public sealed class AssetStore
{
    public const long MaxImageBytes = 25 * 1024 * 1024;
    public const long MaxMediaBytes = 500 * 1024 * 1024;
    public static readonly long MaxModelBytes = GlbSupportProfile.Default.MaxBytes;
    public const long MultipartOverheadBytes = 1024 * 1024;
    public const long MaxImageRequestBytes = MaxImageBytes + MultipartOverheadBytes;
    public const long MaxMediaRequestBytes = MaxMediaBytes + MultipartOverheadBytes;
    public static readonly long MaxModelRequestBytes = MaxModelBytes + MultipartOverheadBytes;
    private const int MaxImageDimension = 32_768;
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private readonly StudioDbContext db;
    private readonly IProjectScope projectScope;
    private readonly TimeProvider timeProvider;
    private readonly string root;

    public AssetStore(StudioDbContext db, IProjectScope projectScope, TimeProvider timeProvider, IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.db = db;
        this.projectScope = projectScope;
        this.timeProvider = timeProvider;
        root = StudioPaths.ResolveAssetRoot(configuration, environment);
    }

    public async Task<RepositoryResult<AssetSummary>> ImportImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxImageBytes)
        {
            return RepositoryResult<AssetSummary>.Invalid($"Image files must be between 1 byte and {MaxImageBytes / (1024 * 1024)} MB.");
        }

        await using var input = file.OpenReadStream();
        return await ImportImageAsync(input, file.FileName, file.Length, "AssetImported", cancellationToken);
    }

    public async Task<RepositoryResult<AssetSummary>> ImportGeneratedImageAsync(
        Stream input, string fileName, long? expectedLength, CancellationToken cancellationToken)
        => await ImportImageAsync(input, fileName, expectedLength, "GenerationOutputImported", cancellationToken, "Generated image");

    /// <summary>
    /// Imports provider output while preserving whether content-addressed
    /// storage created a new logical asset. Callers that build revision stacks
    /// must not rename or re-parent an already existing asset row when a model
    /// returns identical bytes.
    /// </summary>
    internal async Task<RepositoryResult<GeneratedImageImport>> ImportGeneratedImageWithStatusAsync(
        Stream input, string fileName, long? expectedLength, CancellationToken cancellationToken)
    {
        var created = false;
        var result = await ImportImageAsync(
            input,
            fileName,
            expectedLength,
            "GenerationOutputImported",
            cancellationToken,
            "Generated image",
            value => created = value);
        return new(
            result.Value is null ? null : new GeneratedImageImport(result.Value, created),
            result.Kind,
            result.Error);
    }

    public async Task<RepositoryResult<AssetSummary>> ImportMediaAsync(IFormFile file, AssetKind kind, CancellationToken cancellationToken, string source = "Imported")
    {
        if (kind is not (AssetKind.Audio or AssetKind.Video)) return RepositoryResult<AssetSummary>.Invalid("Media imports must be audio or video.");
        if (file.Length is <= 0 or > MaxMediaBytes) return RepositoryResult<AssetSummary>.Invalid($"Media files must be between 1 byte and {MaxMediaBytes / (1024 * 1024)} MB.");
        if (!TryPrepareStorage(out var storageError)) return RepositoryResult<AssetSummary>.Unavailable(storageError);
        var temporaryPath = Path.Combine(root, ".staging", $"{Guid.NewGuid():N}.media");
        try
        {
            string contentHash; long bytes = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81_920);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        bytes += read; if (bytes > MaxMediaBytes) return RepositoryResult<AssetSummary>.Invalid($"Media files cannot exceed {MaxMediaBytes / (1024 * 1024)} MB.");
                        hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken); contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            var media = await InspectMediaAsync(temporaryPath, kind, cancellationToken);
            if (media is null) return RepositoryResult<AssetSummary>.Invalid(kind == AssetKind.Audio ? "Accepted audio containers are WAV, MP3, FLAC, Ogg, MP4/M4A, and WebM." : "Accepted video containers are MP4 and WebM.");
            var existing = await db.Assets.AsNoTracking().SingleOrDefaultAsync(asset => asset.ContentHash == contentHash, cancellationToken);
            if (existing is not null)
            {
                if (existing.Kind != kind.ToString()) return RepositoryResult<AssetSummary>.Conflict("The same bytes already exist under a different media kind.");
                // Older imports did not read FLAC STREAMINFO. Re-importing the same
                // bytes should enrich that asset rather than create a duplicate.
                if (existing.DurationSeconds is null && media.DurationSeconds is > 0)
                {
                    existing.DurationSeconds = media.DurationSeconds;
                    existing.UpdatedAt = timeProvider.GetUtcNow();
                    db.Assets.Update(existing);
                    await db.SaveChangesAsync(cancellationToken);
                }
                return RepositoryResult<AssetSummary>.Ok(Map(existing));
            }
            var directory = Path.Combine(root, contentHash[..2]); Directory.CreateDirectory(directory); var storedPath = Path.Combine(directory, $"{contentHash}{media.Extension}"); if (!File.Exists(storedPath)) File.Move(temporaryPath, storedPath);
            var originalName = NormalizeFileName(file.FileName); var now = timeProvider.GetUtcNow();
            var record = new AssetRecord { Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, Kind = kind.ToString(), OriginalFileName = originalName, MimeType = media.MimeType, Bytes = bytes, DurationSeconds = media.DurationSeconds, ContentHash = contentHash, StoragePath = Path.GetRelativePath(root, storedPath).Replace(Path.DirectorySeparatorChar, '/'), CreatedAt = now, DisplayName = Path.GetFileNameWithoutExtension(originalName), TagsJson = "[]", Notes = "", Source = NormalizeSource(source), IsArchived = false, UpdatedAt = now };
            db.Assets.Add(record); db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "MediaAssetImported", TargetType = "Asset", TargetId = record.Id.ToString(), PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { record.Kind, record.ContentHash, record.MimeType, record.Bytes }), CreatedAt = record.CreatedAt }); await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<AssetSummary>.Ok(Map(record));
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>
    /// Imports one self-contained GLB. The bytes are staged, hashed, and fully
    /// validated before any record exists, so a refused model leaves nothing
    /// behind that looks importable.
    /// </summary>
    public async Task<RepositoryResult<AssetSummary>> ImportModelAsync(IFormFile file, CancellationToken cancellationToken, string source = "Imported")
    {
        if (file.Length <= 0 || file.Length > MaxModelBytes)
            return RepositoryResult<AssetSummary>.Invalid($"Model files must be between 1 byte and {MaxModelBytes / (1024 * 1024)} MB.");
        if (!TryPrepareStorage(out var storageError)) return RepositoryResult<AssetSummary>.Unavailable(storageError);

        var temporaryPath = Path.Combine(root, ".staging", $"{Guid.NewGuid():N}.model");
        try
        {
            string contentHash;
            long bytes = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81_920);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        bytes += read;
                        if (bytes > MaxModelBytes) return RepositoryResult<AssetSummary>.Invalid($"Model files cannot exceed {MaxModelBytes / (1024 * 1024)} MB.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }

            var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(temporaryPath, cancellationToken));
            if (!inspection.Ok) return RepositoryResult<AssetSummary>.Invalid(inspection.Error!);

            var existing = await db.Assets.AsNoTracking().SingleOrDefaultAsync(asset => asset.ContentHash == contentHash, cancellationToken);
            if (existing is not null)
            {
                if (existing.Kind != nameof(AssetKind.Model)) return RepositoryResult<AssetSummary>.Conflict("The same bytes already exist under a different media kind.");
                return RepositoryResult<AssetSummary>.Ok(Map(existing));
            }

            var directory = Path.Combine(root, contentHash[..2]);
            Directory.CreateDirectory(directory);
            var storedPath = Path.Combine(directory, $"{contentHash}.glb");
            if (!File.Exists(storedPath)) File.Move(temporaryPath, storedPath);

            var originalName = NormalizeFileName(file.FileName);
            var now = timeProvider.GetUtcNow();
            var record = new AssetRecord
            {
                Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, Kind = nameof(AssetKind.Model),
                OriginalFileName = originalName, MimeType = "model/gltf-binary", Bytes = bytes,
                ContentHash = contentHash, StoragePath = Path.GetRelativePath(root, storedPath).Replace(Path.DirectorySeparatorChar, '/'),
                CreatedAt = now, DisplayName = Path.GetFileNameWithoutExtension(originalName), TagsJson = "[]", Notes = "",
                Source = NormalizeSource(source), IsArchived = false, UpdatedAt = now
            };
            db.Assets.Add(record);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid(), Type = "ModelAssetImported", TargetType = "Asset", TargetId = record.Id.ToString(),
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    record.ContentHash, record.Bytes, inspection.Profile!.VertexCount, inspection.Profile.TriangleCount,
                    inspection.Profile.Dimensions
                }),
                CreatedAt = record.CreatedAt
            });
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<AssetSummary>.Ok(Map(record));
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>
    /// A generated model enters through exactly the same gate an imported one
    /// does: the same ceiling, the same container validation, the same content
    /// addressing. A worker cannot put anything in the library that an artist
    /// could not have imported by hand.
    /// </summary>
    public async Task<RepositoryResult<AssetSummary>> ImportGeneratedModelAsync(
        Stream input, string fileName, long? expectedLength, CancellationToken cancellationToken,
        string source = "Generated model")
    {
        var length = expectedLength ?? (input.CanSeek ? input.Length - input.Position : -1);
        if (length <= 0 || length > MaxModelBytes)
            return RepositoryResult<AssetSummary>.Invalid(
                $"Model files must be between 1 byte and {MaxModelBytes / (1024 * 1024)} MB.");
        var file = new FormFile(input, input.CanSeek ? input.Position : 0, length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "model/gltf-binary",
        };
        return await ImportModelAsync(file, cancellationToken, source);
    }

    /// <summary>The stored file behind an asset, or null when it is missing.</summary>
    public string? StoredFilePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) return null;
        var path = Path.Combine(root, storagePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Whether a stored asset file is still present in the asset root. A scene
    /// uses this to say "this model is unavailable" instead of drawing nothing.
    /// </summary>
    public bool StoredFileExists(string storagePath) =>
        !string.IsNullOrWhiteSpace(storagePath)
        && File.Exists(Path.Combine(root, storagePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Re-reads the stored bytes so the profile can never drift from the model
    /// the viewer is about to show. Only the JSON chunk is parsed, so this stays
    /// cheap no matter how large the geometry payload is.
    /// </summary>
    public async Task<RepositoryResult<ModelProfileSummary>> ModelProfileAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<ModelProfileSummary>.NotFound();
        if (asset.Kind != nameof(AssetKind.Model)) return RepositoryResult<ModelProfileSummary>.Invalid("Only model assets have a geometry profile.");

        var path = Path.Combine(root, asset.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return RepositoryResult<ModelProfileSummary>.Unavailable("The stored model file is missing from the asset root.");

        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(path, cancellationToken));
        if (!inspection.Ok) return RepositoryResult<ModelProfileSummary>.Invalid(inspection.Error!);
        var profile = inspection.Profile!;
        var limits = GlbSupportProfile.Default;
        return RepositoryResult<ModelProfileSummary>.Ok(new ModelProfileSummary(
            asset.Id, asset.DisplayName, asset.ContentHash, asset.Bytes, $"/api/assets/{asset.Id}/content",
            profile.Container, profile.SpecificationVersion, profile.Generator,
            profile.NodeCount, profile.MeshCount, profile.PrimitiveCount, profile.VertexCount, profile.TriangleCount,
            profile.ImageCount, profile.EmbeddedTextureBytes, profile.BinaryChunkBytes,
            profile.DeclaredExtensions, profile.RequiredExtensions,
            [.. profile.Materials.Select(material => new ModelMaterialSummary(
                material.Name, material.Textured, material.AlphaMode, material.DoubleSided, material.Transmission))],
            profile.BoundsMin, profile.BoundsMax, profile.Dimensions,
            new ModelSupportLimits(limits.MaxBytes, limits.MaxVertices, limits.MaxTriangles, limits.MaxEmbeddedTextureBytes,
                limits.MaxNodes, limits.MaxMaterials, limits.MaxImages, limits.SupportedRequiredExtensions),
            Describe(profile.Rig),
            [.. profile.Clips.Select(Describe)],
            profile.UvChannels,
            profile.PrimitivesWithoutUvs));
    }

    /// <summary>One clip as the artist reads it, without its keyframes.</summary>
    internal static ModelClipSummary Describe(GlbClipSummary clip) => new(
        clip.Name, clip.Duration, clip.ChannelCount, clip.TargetBones, clip.Paths,
        clip.MovesRoot, clip.Supported, clip.Findings);

    /// <summary>The stored model's rig and clips, read from its own bytes.</summary>
    public async Task<(GlbRigProfile Rig, GlbClipSummary[] Clips)?> RigAndClipsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null || asset.Kind != nameof(AssetKind.Model)) return null;
        var path = Path.Combine(root, asset.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(path, cancellationToken));
        return inspection.Ok ? (inspection.Profile!.Rig, inspection.Profile!.Clips) : null;
    }

    /// <summary>
    /// The rig as the artist reads it. A static prop reports no skeleton, which
    /// is an ordinary answer rather than a failure.
    /// </summary>
    internal static ModelRigSummary Describe(GlbRigProfile rig) => new(
        rig.HasSkeleton, rig.ProfileId, rig.ProfileName, rig.ProfileMatched,
        rig.MissingBones, rig.UnexpectedBones,
        rig.SkinCount, rig.BoneCount, rig.SkinnedVertexCount, rig.MaxInfluencesPerVertex,
        [.. rig.Bones.Select(bone => new ModelBoneSummary(
            bone.Name, bone.Parent, bone.Depth,
            bone.RestTranslation, bone.RestRotation, bone.RestScale, bone.RestWorldPosition))],
        rig.TransformsFinite, rig.BindPoseValid, rig.SkinWeightsValid, rig.SkinWeightsChecked,
        rig.Findings, rig.AnimationReady, rig.Fingerprint);

    /// <summary>
    /// Where this exact model revision's bones land under one pose. Nothing is
    /// stored: the answer is recomputed from the stored bytes every time, so it
    /// can never drift from the mesh it describes.
    /// </summary>
    public async Task<RepositoryResult<RigPoseSummary>> RigPoseAsync(
        Guid assetId, RigPoseRequest request, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<RigPoseSummary>.NotFound();
        if (asset.Kind != nameof(AssetKind.Model)) return RepositoryResult<RigPoseSummary>.Invalid("Only model assets have a rig.");

        var path = Path.Combine(root, asset.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return RepositoryResult<RigPoseSummary>.Unavailable("The stored model file is missing from the asset root.");

        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(path, cancellationToken));
        if (!inspection.Ok) return RepositoryResult<RigPoseSummary>.Invalid(inspection.Error!);
        var rig = inspection.Profile!.Rig;
        if (!rig.HasSkeleton) return RepositoryResult<RigPoseSummary>.Invalid("This model is a static prop: it has no skeleton to pose.");
        if (!rig.AnimationReady)
            return RepositoryResult<RigPoseSummary>.Invalid("This rig is not animation-ready, so it is not posed. Read its findings first.");

        var pose = (request?.Pose ?? []).Select(bone => new RigPoseCalculator.BonePose(bone.Bone, bone.Rotation)).ToArray();
        if (pose.Length > rig.BoneCount) return RepositoryResult<RigPoseSummary>.Invalid("A pose cannot name more bones than the rig has.");

        var result = RigPoseCalculator.Apply(rig, pose);
        if (!result.Ok) return RepositoryResult<RigPoseSummary>.Invalid(result.Error!);
        return RepositoryResult<RigPoseSummary>.Ok(new RigPoseSummary(
            asset.Id, asset.ContentHash, rig.ProfileId,
            [.. result.Joints.Select(joint => new RigJointPlacementSummary(joint.Bone, joint.Parent, joint.Position, joint.RestPosition))]));
    }

    public async Task<RepositoryResult<AssetSummary>> ImportGeneratedMediaAsync(Stream input, string fileName, string mimeType, long? expectedLength, AssetKind kind, CancellationToken cancellationToken, string source = "Generated media")
    {
        var length = expectedLength ?? (input.CanSeek ? input.Length - input.Position : -1);
        if (length is <= 0 or > MaxMediaBytes) return RepositoryResult<AssetSummary>.Invalid($"Generated media must be between 1 byte and {MaxMediaBytes / (1024 * 1024)} MB.");
        var file = new FormFile(input, input.CanSeek ? input.Position : 0, length, "file", fileName) { Headers = new HeaderDictionary(), ContentType = mimeType };
        return await ImportMediaAsync(file, kind, cancellationToken, source);
    }

    private async Task<RepositoryResult<AssetSummary>> ImportImageAsync(
        Stream input,
        string fileName,
        long? expectedLength,
        string auditType,
        CancellationToken cancellationToken,
        string source = "Imported",
        Action<bool>? reportCreated = null)
    {
        if (expectedLength is <= 0 or > MaxImageBytes)
        {
            return RepositoryResult<AssetSummary>.Invalid($"Image files must be between 1 byte and {MaxImageBytes / (1024 * 1024)} MB.");
        }
        if (!TryPrepareStorage(out var storageError)) return RepositoryResult<AssetSummary>.Unavailable(storageError);

        var temporaryPath = Path.Combine(root, ".staging", $"{Guid.NewGuid():N}.upload");
        try
        {
            string contentHash;
            long bytes = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81_920);
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        bytes += read;
                        if (bytes > MaxImageBytes) return RepositoryResult<AssetSummary>.Invalid($"Image files cannot exceed {MaxImageBytes / (1024 * 1024)} MB.");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            var image = await InspectImageAsync(temporaryPath, cancellationToken);
            if (image is null) return RepositoryResult<AssetSummary>.Invalid("Only structurally valid PNG and JPEG images are accepted in this build.");

            var existing = await db.Assets.AsNoTracking().SingleOrDefaultAsync(
                asset => asset.ContentHash == contentHash,
                cancellationToken);
            if (existing is not null)
            {
                reportCreated?.Invoke(false);
                return RepositoryResult<AssetSummary>.Ok(Map(existing));
            }

            var directory = Path.Combine(root, contentHash[..2]);
            Directory.CreateDirectory(directory);
            var storedPath = Path.Combine(directory, $"{contentHash}{image.Extension}");
            if (!File.Exists(storedPath)) File.Move(temporaryPath, storedPath);

            var originalName = NormalizeFileName(fileName); var now = timeProvider.GetUtcNow();
            var record = new AssetRecord
            {
                Id = Guid.NewGuid(),
                ProjectId = projectScope.ProjectId,
                Kind = AssetKind.Image.ToString(),
                OriginalFileName = originalName,
                MimeType = image.MimeType,
                Bytes = bytes,
                Width = image.Width,
                Height = image.Height,
                ContentHash = contentHash,
                StoragePath = Path.GetRelativePath(root, storedPath).Replace(Path.DirectorySeparatorChar, '/'),
                CreatedAt = now,
                DisplayName = Path.GetFileNameWithoutExtension(originalName),
                TagsJson = "[]",
                Notes = "",
                Source = NormalizeSource(source),
                IsArchived = false,
                UpdatedAt = now
            };
            db.Assets.Add(record);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid(),
                Type = auditType,
                TargetType = "Asset",
                TargetId = record.Id.ToString(),
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { record.ContentHash, record.MimeType, record.Bytes, record.Width, record.Height }),
                CreatedAt = record.CreatedAt
            });
            await db.SaveChangesAsync(cancellationToken);
            reportCreated?.Invoke(true);
            return RepositoryResult<AssetSummary>.Ok(Map(record));
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<AssetRecord?> GetAsync(Guid assetId, CancellationToken cancellationToken)
        => await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);

    private bool TryPrepareStorage(out string error)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, ".staging"));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = $"Asset storage is unavailable at {root}. {exception.Message}";
            return false;
        }
    }

    public async Task<IReadOnlyList<AssetSummary>> ListAsync(bool includeArchived, CancellationToken cancellationToken)
        => (await db.Assets.AsNoTracking().Where(x => includeArchived || !x.IsArchived).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .Select(Map)
            .ToArray();

    public async Task<RepositoryResult<IReadOnlyList<AssetSummary>>> ListRevisionsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<IReadOnlyList<AssetSummary>>.NotFound();
        // Images and models both have reusable revision stacks. Audio and video
        // takes do not: their versions live on the shot, not on the media.
        if (asset.Kind != nameof(AssetKind.Image) && asset.Kind != nameof(AssetKind.Model))
            return RepositoryResult<IReadOnlyList<AssetSummary>>.Invalid("Only image and model assets have revision stacks.");
        if (asset.RevisionFamilyId is null) return RepositoryResult<IReadOnlyList<AssetSummary>>.Ok([Map(asset)]);
        var family = await db.Assets.AsNoTracking()
            .Where(x => x.RevisionFamilyId == asset.RevisionFamilyId)
            .OrderByDescending(x => x.RevisionNumber)
            .ToListAsync(cancellationToken);
        return RepositoryResult<IReadOnlyList<AssetSummary>>.Ok(family.Select(Map).ToArray());
    }

    public async Task<RepositoryResult<AssetSummary>> AddRevisionAsync(Guid parentAssetId, AddAssetRevisionRequest request, CancellationToken cancellationToken)
    {
        var parent = await db.Assets.SingleOrDefaultAsync(x => x.Id == parentAssetId, cancellationToken);
        var next = await db.Assets.SingleOrDefaultAsync(x => x.Id == request.AssetId, cancellationToken);
        if (parent is null || next is null) return RepositoryResult<AssetSummary>.NotFound();
        if (parent.Kind != nameof(AssetKind.Image) && parent.Kind != nameof(AssetKind.Model))
            return RepositoryResult<AssetSummary>.Invalid("Revision stacks accept image and model assets only.");
        // A revision has to be the same kind of thing it revises, or a stack
        // could quietly mix a picture into a model's history.
        if (parent.Kind != next.Kind)
            return RepositoryResult<AssetSummary>.Invalid($"A {parent.Kind.ToLowerInvariant()} revision must itself be a {parent.Kind.ToLowerInvariant()} asset.");
        if (parent.Id == next.Id) return RepositoryResult<AssetSummary>.Invalid("Choose a different image for the new revision.");
        var familyId = parent.RevisionFamilyId ?? Guid.NewGuid();
        if (next.RevisionFamilyId == familyId)
            return RepositoryResult<AssetSummary>.Conflict("That exact image is already present in this revision stack.");
        if (next.RevisionFamilyId is not null && next.RevisionFamilyId != familyId)
            return RepositoryResult<AssetSummary>.Conflict("That image already belongs to a different revision stack.");
        if (parent.RevisionFamilyId is null)
        {
            parent.RevisionFamilyId = familyId;
            parent.RevisionNumber = 1;
            parent.IsCurrentRevision = true;
            // A stack takes images and models both, and calling a model's first
            // revision "Original image" said the wrong word about it.
            parent.RevisionPrompt = string.IsNullOrWhiteSpace(parent.RevisionPrompt)
                ? $"Original {parent.Kind.ToLowerInvariant()}"
                : parent.RevisionPrompt;
            parent.RevisionEngine = string.IsNullOrWhiteSpace(parent.RevisionEngine) ? parent.Source : parent.RevisionEngine;
        }
        var family = await db.Assets.Where(x => x.RevisionFamilyId == familyId).ToListAsync(cancellationToken);
        if (family.All(x => x.Id != parent.Id)) family.Add(parent);
        foreach (var member in family) member.IsCurrentRevision = false;
        next.RevisionFamilyId = familyId;
        next.RevisionNumber = family.Max(x => x.RevisionNumber ?? 0) + 1;
        next.IsCurrentRevision = true;
        next.ParentAssetId = parent.Id;
        next.RevisionPrompt = NormalizeRevisionText(request.Prompt, 5_000, "Revision note");
        next.RevisionEngine = NormalizeRevisionText(request.Engine, 120, "Imported");
        next.DisplayName = parent.DisplayName;
        next.CollectionId = parent.CollectionId;
        next.TagsJson = parent.TagsJson;
        next.Notes = parent.Notes;
        next.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AssetRevisionAdded", "Asset", next.Id, new { familyId, next.RevisionNumber, next.ParentAssetId, next.RevisionEngine });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetSummary>.Ok(Map(next));
    }

    /// <summary>
    /// Records what a person decided about one asset.
    ///
    /// This is the gate nothing automatic may pass. Every reducing and painting
    /// stage in the compiler reports `mechanical_pass` and never approval, and
    /// every receipt says a person still has to look. This is where looking is
    /// written down.
    ///
    /// It is deliberately per-asset. A prepared derivative is a new revision
    /// and therefore a new row, so it starts unaccepted however long its parent
    /// has been approved, and nothing anywhere has to remember to clear
    /// anything. Accepting one is also what makes it the current revision:
    /// until somebody has looked, every scene and picker goes on reaching for
    /// the mesh that was reviewed.
    ///
    /// A refusal is recorded rather than deleted, because the reason a
    /// derivative was refused is the reason the next one is asked for
    /// differently.
    /// </summary>
    public async Task<RepositoryResult<AssetSummary>> SetPreparationAcceptanceAsync(
        Guid assetId, SetPreparationAcceptanceRequest request, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<AssetSummary>.NotFound();
        if (asset.Kind != nameof(AssetKind.Model))
            return RepositoryResult<AssetSummary>.Invalid("Only a model is accepted this way.");
        if (asset.IsArchived)
            return RepositoryResult<AssetSummary>.Invalid("That model is archived. Restore it before deciding about it.");

        var note = NormalizeRevisionText(request.Note, 2_000, "");
        if (!request.Accepted && string.IsNullOrWhiteSpace(note))
            return RepositoryResult<AssetSummary>.Invalid(
                "Say what is wrong with it. A refusal without a reason tells the next attempt nothing.");

        asset.PreparationAcceptanceNote = note;
        if (request.Accepted)
        {
            asset.PreparationAcceptedAt = timeProvider.GetUtcNow();
            // This studio has no accounts, so the only honest attribution is
            // where it happened. A name invented here would read like evidence
            // of something nobody checked.
            asset.PreparationAcceptedBy = "the artist at this workstation";
            if (asset.RevisionFamilyId is not null)
            {
                var family = await db.Assets
                    .Where(x => x.RevisionFamilyId == asset.RevisionFamilyId)
                    .ToListAsync(cancellationToken);
                foreach (var member in family) member.IsCurrentRevision = member.Id == asset.Id;
            }
        }
        else
        {
            asset.PreparationAcceptedAt = null;
            asset.PreparationAcceptedBy = "";
        }
        asset.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AssetPreparationDecided", "Asset", asset.Id, new
        {
            accepted = request.Accepted,
            asset.PreparationTopologyChanged,
            asset.RevisionFamilyId,
            asset.RevisionNumber,
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetSummary>.Ok(Map(asset));
    }

    public async Task<RepositoryResult<AssetSummary>> PromoteRevisionAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<AssetSummary>.NotFound();
        if (asset.RevisionFamilyId is null) return RepositoryResult<AssetSummary>.Invalid("This asset does not have a revision stack yet.");
        var family = await db.Assets.Where(x => x.RevisionFamilyId == asset.RevisionFamilyId).ToListAsync(cancellationToken);
        foreach (var member in family) member.IsCurrentRevision = member.Id == asset.Id;
        asset.IsArchived = false;
        asset.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AssetRevisionSelected", "Asset", asset.Id, new { asset.RevisionFamilyId, asset.RevisionNumber });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetSummary>.Ok(Map(asset));
    }

    public async Task<IReadOnlyList<AssetCollectionSummary>> ListCollectionsAsync(CancellationToken cancellationToken)
    {
        var collections = await db.AssetCollections.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(cancellationToken);
        var counts = await db.Assets.AsNoTracking().Where(x => !x.IsArchived && x.CollectionId != null).GroupBy(x => x.CollectionId!.Value).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        return collections.Select(x => new AssetCollectionSummary(x.Id, x.ProjectId, x.Name, x.Color, x.SortOrder, counts.GetValueOrDefault(x.Id), x.CreatedAt, x.UpdatedAt)).ToArray();
    }

    public async Task<RepositoryResult<AssetCollectionSummary>> CreateCollectionAsync(CreateAssetCollectionRequest request, CancellationToken cancellationToken)
    {
        var error = ValidateCollection(request.Name, request.Color); if (error is not null) return RepositoryResult<AssetCollectionSummary>.Invalid(error);
        var name = request.Name.Trim(); if (await db.AssetCollections.AnyAsync(x => x.Name == name, cancellationToken)) return RepositoryResult<AssetCollectionSummary>.Conflict("A collection with that name already exists.");
        var now = timeProvider.GetUtcNow(); var sortOrder = (await db.AssetCollections.MaxAsync(x => (int?)x.SortOrder, cancellationToken) ?? 0) + 1;
        var record = new AssetCollectionRecord { Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, Name = name, Color = request.Color, SortOrder = sortOrder, CreatedAt = now, UpdatedAt = now };
        db.AssetCollections.Add(record); AddAudit("AssetCollectionCreated", "AssetCollection", record.Id, new { record.Name, record.Color }); await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetCollectionSummary>.Ok(new(record.Id, record.ProjectId, record.Name, record.Color, record.SortOrder, 0, record.CreatedAt, record.UpdatedAt));
    }

    public async Task<RepositoryResult<AssetCollectionSummary>> UpdateCollectionAsync(Guid id, UpdateAssetCollectionRequest request, CancellationToken cancellationToken)
    {
        var error = ValidateCollection(request.Name, request.Color); if (error is not null) return RepositoryResult<AssetCollectionSummary>.Invalid(error);
        var record = await db.AssetCollections.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (record is null) return RepositoryResult<AssetCollectionSummary>.NotFound();
        var name = request.Name.Trim(); if (await db.AssetCollections.AnyAsync(x => x.Id != id && x.Name == name, cancellationToken)) return RepositoryResult<AssetCollectionSummary>.Conflict("A collection with that name already exists.");
        record.Name = name; record.Color = request.Color; record.UpdatedAt = timeProvider.GetUtcNow(); AddAudit("AssetCollectionUpdated", "AssetCollection", id, new { record.Name, record.Color }); await db.SaveChangesAsync(cancellationToken);
        var count = await db.Assets.CountAsync(x => x.CollectionId == id && !x.IsArchived, cancellationToken); return RepositoryResult<AssetCollectionSummary>.Ok(new(record.Id, record.ProjectId, record.Name, record.Color, record.SortOrder, count, record.CreatedAt, record.UpdatedAt));
    }

    public async Task<bool> DeleteCollectionAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.AssetCollections.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (record is null) return false;
        var assets = await db.Assets.Where(x => x.CollectionId == id).ToListAsync(cancellationToken); foreach (var asset in assets) { asset.CollectionId = null; asset.UpdatedAt = timeProvider.GetUtcNow(); }
        db.AssetCollections.Remove(record); AddAudit("AssetCollectionDeleted", "AssetCollection", id, new { record.Name, assetsReleased = assets.Count }); await db.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<RepositoryResult<AssetSummary>> UpdateAsync(Guid id, UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (asset is null) return RepositoryResult<AssetSummary>.NotFound();
        var displayName = (request.DisplayName ?? "").Trim(); if (displayName.Length is 0 or > 120) return RepositoryResult<AssetSummary>.Invalid("Asset name must contain 1 to 120 characters.");
        if (request.CollectionId is not null && !await db.AssetCollections.AnyAsync(x => x.Id == request.CollectionId, cancellationToken)) return RepositoryResult<AssetSummary>.Invalid("The selected collection does not exist.");
        var tags = (request.Tags ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (tags.Length > 20 || tags.Any(x => x.Length > 40)) return RepositoryResult<AssetSummary>.Invalid("Use at most 20 tags of 40 characters each.");
        var notes = (request.Notes ?? "").Trim(); if (notes.Length > 2_000) return RepositoryResult<AssetSummary>.Invalid("Asset notes cannot exceed 2,000 characters.");
        asset.DisplayName = displayName; asset.CollectionId = request.CollectionId; asset.TagsJson = JsonSerializer.Serialize(tags); asset.Notes = notes; asset.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AssetMetadataUpdated", "Asset", id, new { asset.DisplayName, asset.CollectionId, tags }); await db.SaveChangesAsync(cancellationToken); return RepositoryResult<AssetSummary>.Ok(Map(asset));
    }

    public async Task<RepositoryResult<AssetSummary>> SetArchivedAsync(Guid id, bool archived, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (asset is null) return RepositoryResult<AssetSummary>.NotFound();
        asset.IsArchived = archived; asset.UpdatedAt = timeProvider.GetUtcNow(); AddAudit(archived ? "AssetArchived" : "AssetRestored", "Asset", id, new { asset.DisplayName }); await db.SaveChangesAsync(cancellationToken); return RepositoryResult<AssetSummary>.Ok(Map(asset));
    }

    public async Task<IReadOnlyList<AssetPlacementSummary>> ListPlacementsAsync(Guid? assetId, Guid? shotId, CancellationToken cancellationToken)
    {
        var query = db.AssetPlacements.AsNoTracking().AsQueryable(); if (assetId is not null) query = query.Where(x => x.AssetId == assetId); if (shotId is not null) query = query.Where(x => x.ShotId == shotId);
        var placements = await query.ToListAsync(cancellationToken); var shotIds = placements.Select(x => x.ShotId).Distinct().ToArray(); var shots = (await db.Shots.AsNoTracking().Where(x => shotIds.Contains(x.Id)).ToListAsync(cancellationToken)).ToDictionary(x => x.Id);
        return placements.OrderByDescending(x => x.CreatedAt).Where(x => shots.ContainsKey(x.ShotId)).Select(x => new AssetPlacementSummary(x.Id, x.AssetId, x.ShotId, shots[x.ShotId].Code, shots[x.ShotId].Title, x.Role, x.CreatedAt)).ToArray();
    }

    public async Task<RepositoryResult<AssetPlacementSummary>> PlaceAsync(Guid assetId, CreateAssetPlacementRequest request, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId && !x.IsArchived, cancellationToken); if (asset is null) return RepositoryResult<AssetPlacementSummary>.NotFound();
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ShotId, cancellationToken); if (shot is null) return RepositoryResult<AssetPlacementSummary>.Invalid("The selected shot does not exist.");
        if (asset.Kind == nameof(AssetKind.Model)) return RepositoryResult<AssetPlacementSummary>.Invalid("Model assets are inspected in the model viewer; they are not shot placements yet.");
        var expectedRole = asset.Kind switch { "Image" => "Image guide", "Video" => "Video take", _ => "Audio cue" }; var role = string.IsNullOrWhiteSpace(request.Role) ? expectedRole : request.Role.Trim(); if (role != expectedRole) return RepositoryResult<AssetPlacementSummary>.Invalid($"{asset.Kind} assets use the '{expectedRole}' shot role.");
        var existing = await db.AssetPlacements.AsNoTracking().SingleOrDefaultAsync(x => x.AssetId == assetId && x.ShotId == request.ShotId && x.Role == role, cancellationToken); if (existing is not null) return RepositoryResult<AssetPlacementSummary>.Ok(new(existing.Id, existing.AssetId, existing.ShotId, shot.Code, shot.Title, existing.Role, existing.CreatedAt));
        var record = new AssetPlacementRecord { Id = Guid.NewGuid(), AssetId = assetId, ShotId = request.ShotId, Role = role, CreatedAt = timeProvider.GetUtcNow() }; db.AssetPlacements.Add(record); AddAudit("AssetPlaced", "Asset", assetId, new { request.ShotId, role }); await db.SaveChangesAsync(cancellationToken); return RepositoryResult<AssetPlacementSummary>.Ok(new(record.Id, record.AssetId, record.ShotId, shot.Code, shot.Title, record.Role, record.CreatedAt));
    }

    public async Task<bool> RemovePlacementAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.AssetPlacements.SingleOrDefaultAsync(x => x.Id == id, cancellationToken); if (record is null) return false; db.AssetPlacements.Remove(record); AddAudit("AssetPlacementRemoved", "Asset", record.AssetId, new { record.ShotId, record.Role }); await db.SaveChangesAsync(cancellationToken); return true;
    }

    public string ResolveContentPath(AssetRecord asset) => ResolveStoragePath(asset.StoragePath);

    /// <summary>
    /// Resolves a stored relative path against the asset root, refusing anything that
    /// escapes it. Exposed separately from <see cref="ResolveContentPath"/> because
    /// the authority library holds image descriptors rather than asset rows: its
    /// versions must outlive the project whose asset row originally described them.
    /// </summary>
    public string ResolveStoragePath(string storagePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, storagePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Asset storage path escaped the configured root.");
        return path;
    }

    private static string NormalizeFileName(string value)
    {
        var name = Path.GetFileName(value ?? "image").Trim();
        if (name.Length == 0) name = "image";
        return name.Length <= 180 ? name : name[..180];
    }

    private static AssetSummary Map(AssetRecord x) => new(
        x.Id, x.ProjectId, Enum.Parse<AssetKind>(x.Kind), x.OriginalFileName, x.MimeType,
        x.Bytes, x.Width, x.Height, x.DurationSeconds, x.ContentHash, $"/api/assets/{x.Id}/content", x.CreatedAt,
        string.IsNullOrWhiteSpace(x.DisplayName) ? Path.GetFileNameWithoutExtension(x.OriginalFileName) : x.DisplayName,
        x.CollectionId, JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(x.TagsJson) ? "[]" : x.TagsJson) ?? [],
        x.Notes ?? "", string.IsNullOrWhiteSpace(x.Source) ? "Imported" : x.Source, x.IsArchived, x.UpdatedAt == default ? x.CreatedAt : x.UpdatedAt,
        x.RevisionFamilyId, x.RevisionNumber, x.IsCurrentRevision, x.ParentAssetId, x.RevisionPrompt ?? "", x.RevisionEngine ?? "",
        x.PreparationAcceptedAt, x.PreparationAcceptedBy ?? "", x.PreparationAcceptanceNote ?? "", x.PreparationTopologyChanged);

    private void AddAudit(string type, string targetType, Guid targetId, object payload) => db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = type, TargetType = targetType, TargetId = targetId.ToString(), PayloadJson = JsonSerializer.Serialize(payload), CreatedAt = timeProvider.GetUtcNow() });
    private static string? ValidateCollection(string name, string color)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) return "Collection name must contain 1 to 80 characters.";
        if (!HexColor.IsMatch(color ?? "")) return "Collection color must use six-digit hex notation.";
        return null;
    }
    private static string NormalizeSource(string source)
    {
        var value = (source ?? "Imported").Trim(); if (value.Length == 0) value = "Imported"; return value.Length <= 80 ? value : value[..80];
    }

    private static string NormalizeRevisionText(string? value, int limit, string fallback)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0) normalized = fallback;
        return normalized.Length <= limit ? normalized : normalized[..limit];
    }

    private static async Task<ImageInfo?> InspectImageAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var readLength = (int)Math.Min(stream.Length, 1024 * 1024);
        var bytes = new byte[readLength];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0) break;
            offset += read;
        }
        var data = bytes.AsSpan(0, offset);

        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
            return ValidDimensions(width, height) ? new("image/png", ".png", width, height) : null;
        }

        if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            var position = 2;
            while (position + 8 < data.Length)
            {
                if (data[position] != 0xff) { position++; continue; }
                while (position < data.Length && data[position] == 0xff) position++;
                if (position >= data.Length) break;
                var marker = data[position++];
                if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
                if (position + 2 > data.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
                if (length < 2 || position + length > data.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 7) return null;
                    var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                    return ValidDimensions(width, height) ? new("image/jpeg", ".jpg", width, height) : null;
                }
                position += length;
            }
        }
        return null;
    }

    private static bool ValidDimensions(int width, int height) => width is > 0 and <= MaxImageDimension && height is > 0 and <= MaxImageDimension;
    private sealed record ImageInfo(string MimeType, string Extension, int Width, int Height);

    private static async Task<MediaInfo?> InspectMediaAsync(string path, AssetKind kind, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[(int)Math.Min(stream.Length, 65_536)]; var offset = 0;
        while (offset < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken); if (read == 0) break; offset += read; }
        var data = bytes.AsSpan(0, offset);
        if (kind == AssetKind.Audio)
        {
            if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8)) return new("audio/wav", ".wav", null);
            if (data.Length >= 3 && data[..3].SequenceEqual("ID3"u8) || data.Length >= 2 && data[0] == 0xff && (data[1] & 0xe0) == 0xe0) return new("audio/mpeg", ".mp3", null);
            if (data.Length >= 4 && data[..4].SequenceEqual("fLaC"u8)) return new("audio/flac", ".flac", FlacDuration(data));
            if (data.Length >= 4 && data[..4].SequenceEqual("OggS"u8)) return new("audio/ogg", ".ogg", null);
        }
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8)) return new(kind == AssetKind.Audio ? "audio/mp4" : "video/mp4", kind == AssetKind.Audio ? ".m4a" : ".mp4", null);
        if (data.Length >= 4 && data[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return new(kind == AssetKind.Audio ? "audio/webm" : "video/webm", ".webm", null);
        return null;
    }

    private static double? FlacDuration(ReadOnlySpan<byte> data)
    {
        // The mandatory STREAMINFO block stores sample-rate (20 bits) and total
        // samples (36 bits) in one big-endian 64-bit field after 10 payload bytes.
        if (data.Length < 8) return null;
        var position = 4;
        while (position + 4 <= data.Length)
        {
            var blockType = data[position] & 0x7f;
            var blockLength = (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3];
            var payload = position + 4;
            if (blockLength < 0 || payload + blockLength > data.Length) return null;
            if (blockType == 0 && blockLength >= 18)
            {
                var packed = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(payload + 10, 8));
                var sampleRate = (packed >> 44) & 0xfffff;
                var totalSamples = packed & 0xfffffffff;
                return sampleRate > 0 && totalSamples > 0 ? totalSamples / (double)sampleRate : null;
            }
            position = payload + blockLength;
        }
        return null;
    }
    private sealed record MediaInfo(string MimeType, string Extension, double? DurationSeconds);
}
