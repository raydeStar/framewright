using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Hands approved library assets to a game project as an engine-neutral bundle:
/// the current revision's files, and a <c>framewright-bundle.json</c> manifest
/// saying what each file is, how big it really is, and who approved it.
///
/// Nothing here knows any engine. A bundle is plain files and JSON that an
/// importer -- Unreal, Godot, Unity, a build script -- reads on its own terms;
/// the schema is documented in docs/GAME_BUNDLE.md.
///
/// Two promises. Only what a person approved ships, unless they say pending
/// work may go too, and something sent back never does. And nothing is ever
/// written over: each shipment is a new, timestamped folder (or a zip), built
/// beside its final name and moved into place whole, so an importer watching
/// the folder never sees half a bundle and an earlier bundle is never touched.
/// </summary>
public sealed class ShippingService(
    StudioDbContext db,
    AssetStore assets,
    IProjectScope projectScope,
    IConfiguration configuration,
    BuildIdentityService buildIdentity,
    TimeProvider timeProvider)
{
    public const string ManifestFileName = "framewright-bundle.json";
    public const string Schema = "framewright.bundle.v1";
    public const string TargetsSection = "Integrations:Shipping:Targets";

    /// <summary>A selection is a handful of assets or a collection, not a library dump.</summary>
    private const int MaxSelection = 2_000;

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Target(string Name, string Path, string? Problem);
    private sealed record Candidate(AssetRecord Record, Guid StableId, string? CollectionName, ShipmentItemPreview Preview);
    private sealed record Selection(string SourceName, string SourceKind, Guid? CollectionId, IReadOnlyList<Candidate> Items)
    {
        public int ShipCount => Items.Count(item => item.Preview.Ships);
    }

    /// <summary>
    /// The destinations a workstation has named, from configuration only. The
    /// browser picks one by name; it can never supply a path of its own.
    /// </summary>
    public IReadOnlyList<ShippingTargetSummary> Targets() =>
        [.. ConfiguredTargets().Select(target => new ShippingTargetSummary(
            target.Name, target.Path, target.Problem is null, target.Problem))];

    private List<Target> ConfiguredTargets()
    {
        var targets = new List<Target>();
        foreach (var entry in configuration.GetSection(TargetsSection).GetChildren())
        {
            var name = (entry["Name"] ?? "").Trim();
            var path = (entry["Path"] ?? "").Trim();
            if (name.Length == 0 && path.Length == 0) continue;
            string? problem = null;
            if (name.Length == 0) problem = "This destination has no Name.";
            else if (path.Length == 0) problem = "This destination has no Path.";
            else if (!System.IO.Path.IsPathFullyQualified(path))
                problem = "Its Path must be a full path, such as the game project's import folder.";
            else if (!Directory.Exists(path))
                problem = "Its folder does not exist. Create it, or correct the Path.";
            if (targets.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
                problem = "Another destination already has this name.";
            targets.Add(new Target(name.Length == 0 ? "(unnamed)" : name, path, problem));
        }
        return targets;
    }

    public async Task<RepositoryResult<ShipmentPreview>> PreviewAsync(ShipAssetsRequest request, CancellationToken cancellationToken)
    {
        var selected = await SelectAsync(request, cancellationToken);
        if (selected.Kind != RepositoryResultKind.Ok || selected.Value is null)
            return new RepositoryResult<ShipmentPreview>(null, selected.Kind, selected.Error);
        var selection = selected.Value;
        return RepositoryResult<ShipmentPreview>.Ok(new ShipmentPreview(
            selection.SourceName, request.IncludePending, [.. selection.Items.Select(item => item.Preview)],
            selection.ShipCount, selection.Items.Count - selection.ShipCount));
    }

    /// <summary>Writes a new bundle folder under a configured destination.</summary>
    public async Task<RepositoryResult<ShipmentReceipt>> ShipAsync(ShipAssetsRequest request, CancellationToken cancellationToken)
    {
        var configured = ConfiguredTargets();
        var target = configured.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, (request.Target ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return RepositoryResult<ShipmentReceipt>.Invalid(configured.Count == 0
                ? $"No game destinations are configured. Download a zip instead, or add {TargetsSection} in appsettings.Local.json."
                : $"Choose one of the configured destinations: {string.Join(", ", configured.Select(item => item.Name))}.");
        if (target.Problem is not null)
            return RepositoryResult<ShipmentReceipt>.Unavailable($"{target.Name} cannot be written to: {target.Problem}");

        var selected = await SelectAsync(request, cancellationToken);
        if (selected.Kind != RepositoryResultKind.Ok || selected.Value is null)
            return new RepositoryResult<ShipmentReceipt>(null, selected.Kind, selected.Error);
        var selection = selected.Value;
        if (selection.ShipCount == 0) return RepositoryResult<ShipmentReceipt>.Invalid(NothingToShip(selection, request));

        var shipmentId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var parent = Path.Combine(target.Path, Slug(selection.SourceName, "selection"));
        // Built beside its final name and moved into place whole: an importer
        // watching the folder sees a complete bundle or none at all.
        var staging = Path.Combine(parent, $".partial-{shipmentId:N}");
        try
        {
            Directory.CreateDirectory(parent);
            var files = await WriteBundleAsync(staging, selection, request, shipmentId, now, cancellationToken);
            var folder = FreshFolder(parent, Stamp(now));
            Directory.Move(staging, folder);
            Record(shipmentId, target.Name, folder, selection, request);
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<ShipmentReceipt>.Ok(new ShipmentReceipt(
                shipmentId, target.Name, folder, Path.GetFileName(folder), selection.ShipCount,
                selection.Items.Count - selection.ShipCount, files, now));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(staging);
            return RepositoryResult<ShipmentReceipt>.Unavailable($"The bundle was not written: {error.Message}");
        }
    }

    /// <summary>
    /// The same bundle as a zip in a temporary file, for a workstation with no
    /// destination configured. The caller streams it and the file deletes
    /// itself when the stream closes.
    /// </summary>
    public async Task<RepositoryResult<(string ZipPath, ShipmentReceipt Receipt)>> ZipAsync(
        ShipAssetsRequest request, CancellationToken cancellationToken)
    {
        var selected = await SelectAsync(request, cancellationToken);
        if (selected.Kind != RepositoryResultKind.Ok || selected.Value is null)
            return new RepositoryResult<(string, ShipmentReceipt)>(default, selected.Kind, selected.Error);
        var selection = selected.Value;
        if (selection.ShipCount == 0)
            return RepositoryResult<(string, ShipmentReceipt)>.Invalid(NothingToShip(selection, request));

        var shipmentId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var work = Path.Combine(Path.GetTempPath(), $"framewright-ship-{shipmentId:N}");
        var bundle = Path.Combine(work, "bundle");
        var zipPath = Path.Combine(Path.GetTempPath(), $"framewright-ship-{shipmentId:N}.zip");
        try
        {
            var files = await WriteBundleAsync(bundle, selection, request, shipmentId, now, cancellationToken);
            ZipFile.CreateFromDirectory(bundle, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
            var name = $"{Slug(selection.SourceName, "selection")}-{Stamp(now)}";
            Record(shipmentId, null, null, selection, request);
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<(string, ShipmentReceipt)>.Ok((zipPath, new ShipmentReceipt(
                shipmentId, null, null, name, selection.ShipCount, selection.Items.Count - selection.ShipCount, files, now)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            return RepositoryResult<(string, ShipmentReceipt)>.Unavailable($"The bundle was not written: {error.Message}");
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static string NothingToShip(Selection selection, ShipAssetsRequest request) => selection.Items.Count == 0
        ? $"{selection.SourceName} has nothing in it to ship."
        : request.IncludePending
            ? $"Nothing in {selection.SourceName} can ship: everything there was sent back, archived or is missing its file."
            : $"Nothing in {selection.SourceName} is approved yet. Approve what is ready, or include pending work.";

    /// <summary>
    /// What a request names, each library asset resolved to its family's
    /// current revision, with whether it ships and why not.
    /// </summary>
    private async Task<RepositoryResult<Selection>> SelectAsync(ShipAssetsRequest request, CancellationToken cancellationToken)
    {
        var ids = (request.AssetIds ?? []).Distinct().ToArray();
        if ((request.CollectionId is null) == (ids.Length == 0))
            return RepositoryResult<Selection>.Invalid("Ship one collection, or a selection of assets.");
        if (ids.Length > MaxSelection)
            return RepositoryResult<Selection>.Invalid($"Ship at most {MaxSelection:N0} assets at once.");

        List<AssetRecord> chosen;
        string sourceName;
        string sourceKind;
        if (request.CollectionId is { } collectionId)
        {
            var collection = await db.AssetCollections.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == collectionId, cancellationToken);
            if (collection is null) return RepositoryResult<Selection>.NotFound();
            // What the library shows for the collection: the current revision of
            // each family, and nothing archived.
            chosen = await db.Assets.AsNoTracking()
                .Where(asset => asset.CollectionId == collectionId && !asset.IsArchived
                    && (asset.RevisionFamilyId == null || asset.IsCurrentRevision))
                .ToListAsync(cancellationToken);
            sourceName = collection.Name;
            sourceKind = "collection";
        }
        else
        {
            var named = await db.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).ToListAsync(cancellationToken);
            if (named.Count != ids.Length)
                return RepositoryResult<Selection>.Invalid("Some of those assets are not in this project's library.");
            // An older revision stands for its asset, and what ships is the
            // asset's current revision.
            var families = named.Where(asset => asset.RevisionFamilyId is not null && !asset.IsCurrentRevision)
                .Select(asset => asset.RevisionFamilyId!.Value).Distinct().ToArray();
            var current = families.Length == 0
                ? []
                : await db.Assets.AsNoTracking()
                    .Where(asset => asset.RevisionFamilyId != null && families.Contains(asset.RevisionFamilyId.Value) && asset.IsCurrentRevision)
                    .ToListAsync(cancellationToken);
            chosen = [.. named.Where(asset => asset.RevisionFamilyId is null || asset.IsCurrentRevision)
                .Concat(current)
                .DistinctBy(asset => asset.Id)];
            sourceName = "Selection";
            sourceKind = "selection";
        }

        // The asset's identity across revisions is its first revision's id: the
        // id it had before it was ever revised, which an importer saw first.
        var familyIds = chosen.Where(asset => asset.RevisionFamilyId is not null)
            .Select(asset => asset.RevisionFamilyId!.Value).Distinct().ToArray();
        var roots = familyIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : (await db.Assets.AsNoTracking()
                .Where(asset => asset.RevisionFamilyId != null && familyIds.Contains(asset.RevisionFamilyId.Value))
                .Select(asset => new { Family = asset.RevisionFamilyId!.Value, asset.Id, asset.RevisionNumber })
                .ToListAsync(cancellationToken))
                .GroupBy(row => row.Family)
                .ToDictionary(group => group.Key, group => group.OrderBy(row => row.RevisionNumber ?? int.MaxValue).First().Id);
        var collections = await db.AssetCollections.AsNoTracking()
            .ToDictionaryAsync(collection => collection.Id, collection => collection.Name, cancellationToken);

        var items = chosen
            .OrderBy(asset => asset.Kind, StringComparer.Ordinal)
            .ThenBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(asset =>
            {
                var decision = Enum.TryParse<AssetReviewDecision>(asset.ReviewDecision, out var parsed) ? parsed : AssetReviewDecision.Pending;
                var reason = asset.IsArchived ? "Archived"
                    : decision == AssetReviewDecision.ChangesRequested
                        ? $"Sent back{(string.IsNullOrWhiteSpace(asset.ReviewNote) ? "" : $": {asset.ReviewNote}")}"
                    : decision == AssetReviewDecision.Pending && !request.IncludePending ? "Pending review"
                    : assets.StoredFilePath(asset.StoragePath) is null ? "Its stored file is missing"
                    : null;
                var stable = asset.RevisionFamilyId is { } family && roots.TryGetValue(family, out var root) ? root : asset.Id;
                return new Candidate(asset, stable,
                    asset.CollectionId is { } id && collections.TryGetValue(id, out var name) ? name : null,
                    new ShipmentItemPreview(stable, asset.Id, DisplayName(asset), Enum.Parse<AssetKind>(asset.Kind),
                        decision, reason is null, reason));
            })
            .ToArray();
        return RepositoryResult<Selection>.Ok(new Selection(sourceName, sourceKind, request.CollectionId, items));
    }

    /// <summary>
    /// Copies each shipping file into <paramref name="directory"/>, hashing as
    /// it goes, and writes the manifest last. Returns the bundle's relative paths.
    /// </summary>
    private async Task<string[]> WriteBundleAsync(
        string directory, Selection selection, ShipAssetsRequest request, Guid shipmentId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<object>();
        var files = new List<string>();
        foreach (var candidate in selection.Items.Where(item => item.Preview.Ships))
        {
            var record = candidate.Record;
            var source = assets.StoredFilePath(record.StoragePath)
                ?? throw new InvalidDataException($"{DisplayName(record)} has no stored file.");
            var folder = record.Kind switch
            {
                nameof(AssetKind.Model) => "models",
                nameof(AssetKind.Image) => "images",
                nameof(AssetKind.Video) => "video",
                _ => "audio",
            };
            var extension = Path.GetExtension(record.StoragePath).ToLowerInvariant();
            if (extension.Length == 0) extension = Path.GetExtension(record.OriginalFileName).ToLowerInvariant();
            var baseName = $"{Slug(DisplayName(record), "asset")}-{record.ContentHash[..Math.Min(8, record.ContentHash.Length)].ToLowerInvariant()}";
            var name = baseName + extension;
            for (var suffix = 2; !used.Add($"{folder}/{name}"); suffix++) name = $"{baseName}-{suffix}{extension}";
            var relative = $"files/{folder}/{name}";
            var destination = Path.Combine(directory, "files", folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var (sha256, bytes) = await CopyHashedAsync(source, destination, cancellationToken);
            // The library is content-addressed: a file that no longer hashes to
            // its record is damaged, and shipping it would put the damage in a game.
            if (!string.Equals(sha256, record.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"{DisplayName(record)}'s stored file no longer matches its recorded content hash, so it was not shipped.");

            double[]? dimensions = null;
            int? triangles = null;
            if (record.Kind == nameof(AssetKind.Model))
            {
                var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(destination, cancellationToken));
                dimensions = inspection.Profile?.Dimensions;
                triangles = inspection.Profile?.TriangleCount;
            }

            files.Add(relative);
            items.Add(new
            {
                assetId = candidate.StableId,
                revisionId = record.Id,
                revisionNumber = record.RevisionNumber ?? 1,
                displayName = DisplayName(record),
                kind = record.Kind,
                collection = candidate.CollectionName,
                file = relative,
                mimeType = record.MimeType,
                bytes,
                sha256,
                dimensionsMetres = dimensions,
                triangleCount = triangles,
                widthPixels = record.Kind is nameof(AssetKind.Image) or nameof(AssetKind.Video) ? record.Width : null,
                heightPixels = record.Kind is nameof(AssetKind.Image) or nameof(AssetKind.Video) ? record.Height : null,
                durationSeconds = record.DurationSeconds,
                tags = Tags(record),
                notes = record.Notes ?? "",
                review = new
                {
                    decision = candidate.Preview.Decision.ToString(),
                    note = record.ReviewNote ?? "",
                    decidedAt = record.ReviewDecidedAt,
                },
            });
        }

        var project = await db.Projects.AsNoTracking()
            .Where(item => item.Id == projectScope.ProjectId)
            .Select(item => new { item.Id, item.Name })
            .SingleOrDefaultAsync(cancellationToken);
        var build = buildIdentity.Current;
        var manifest = new
        {
            schema = Schema,
            shipmentId,
            createdAt = now,
            generator = new { name = "Framewright", version = build.Version, commit = build.Commit },
            project = project is null ? null : new { id = project.Id, name = project.Name },
            source = new
            {
                kind = selection.SourceKind,
                collectionId = selection.CollectionId,
                name = selection.SourceName,
            },
            includePending = request.IncludePending,
            // How to read the numbers, so an importer converts once and knows from what.
            units = new { length = "metres", up = "+Y", modelFormat = "glTF 2.0 binary (.glb)" },
            items,
            skipped = selection.Items.Where(item => !item.Preview.Ships).Select(item => new
            {
                assetId = item.StableId,
                revisionId = item.Record.Id,
                displayName = item.Preview.DisplayName,
                kind = item.Record.Kind,
                reason = item.Preview.Reason,
            }).ToArray(),
        };
        // Last, so a bundle with a manifest is a bundle with every file in it.
        await File.WriteAllTextAsync(Path.Combine(directory, ManifestFileName),
            JsonSerializer.Serialize(manifest, ManifestJson), new UTF8Encoding(false), cancellationToken);
        files.Add(ManifestFileName);
        return [.. files];
    }

    private static async Task<(string Sha256, long Bytes)> CopyHashedAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = File.OpenRead(source);
        // CreateNew: a bundle folder is new, so an existing file here is a bug,
        // and failing beats writing over it.
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    /// <summary>The receipt this studio keeps: what went where, and on whose decision.</summary>
    private void Record(Guid shipmentId, string? target, string? folder, Selection selection, ShipAssetsRequest request) =>
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Type = "AssetsShipped",
            TargetType = selection.SourceKind == "collection" ? "AssetCollection" : "Asset",
            TargetId = selection.CollectionId?.ToString() ?? "selection",
            PayloadJson = JsonSerializer.Serialize(new
            {
                shipmentId,
                target = target ?? "zip download",
                folder,
                source = selection.SourceName,
                request.IncludePending,
                shipped = selection.Items.Where(item => item.Preview.Ships)
                    .Select(item => new { item.StableId, revisionId = item.Record.Id, item.Record.ContentHash, decision = item.Preview.Decision.ToString() }),
                skipped = selection.Items.Where(item => !item.Preview.Ships)
                    .Select(item => new { revisionId = item.Record.Id, item.Preview.Reason }),
            }),
            CreatedAt = timeProvider.GetUtcNow(),
        });

    private static string DisplayName(AssetRecord asset) =>
        string.IsNullOrWhiteSpace(asset.DisplayName) ? Path.GetFileNameWithoutExtension(asset.OriginalFileName) : asset.DisplayName;

    private static string[] Tags(AssetRecord asset)
    {
        try { return JsonSerializer.Deserialize<string[]>(string.IsNullOrWhiteSpace(asset.TagsJson) ? "[]" : asset.TagsJson) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>A sortable UTC stamp that is also a valid folder name everywhere.</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", CultureInfo.InvariantCulture);

    private static string FreshFolder(string parent, string name)
    {
        var folder = Path.Combine(parent, name);
        for (var suffix = 2; Directory.Exists(folder) || File.Exists(folder); suffix++)
            folder = Path.Combine(parent, $"{name}-{suffix}");
        return folder;
    }

    /// <summary>
    /// A name any file system and any engine accepts: lower-case ASCII letters,
    /// digits and hyphens, accents folded away.
    /// </summary>
    internal static string Slug(string value, string fallback)
    {
        var folded = new StringBuilder();
        foreach (var character in (value ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(character);
            folded.Append(lower is >= 'a' and <= 'z' or >= '0' and <= '9' ? lower : '-');
        }
        var slug = string.Join('-', folded.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? fallback : slug;
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
