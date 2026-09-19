using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// The cross-project authority library, per docs/MULTI_PROJECT_PLAN.md step 5.
///
/// The whole design rests on one rule: <b>copy on import, with provenance — never a
/// live cross-project link.</b> That follows from IMPLEMENTATION_HANDOFF.md §10,
/// "a version cites exact authority versions, never a mutable latest". A live link
/// would mean editing a character in the library silently changed a frame already
/// delivered in another project, which is precisely the corruption the manifest
/// design exists to prevent.
///
/// So an import writes a genuine project-local authority and records where it came
/// from. Pulling a later library version <i>appends</i> a new project version rather
/// than editing the imported one, so every frozen manifest keeps citing the exact
/// version it was rendered against.
/// </summary>
public sealed class AuthorityLibraryService(
    StudioDbContext db,
    IProjectScope projectScope,
    AssetStore assets,
    TimeProvider timeProvider)
{
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedCategories = ["Character", "Role", "Wardrobe", "Pose", "Location", "Architecture", "Prop", "Style", "World"];
    private static readonly string[] LibraryImportTags = ["library", "imported"];

    /// <summary>
    /// Every library authority, annotated with how the active project relates to it.
    /// </summary>
    public async Task<IReadOnlyList<LibraryAuthoritySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var authorities = await db.LibraryAuthorities.AsNoTracking().ToListAsync(cancellationToken);
        if (authorities.Count == 0) return [];
        var ids = authorities.Select(x => x.Id).ToArray();
        var heads = (await db.LibraryAuthorityVersions.AsNoTracking()
                .Where(x => ids.Contains(x.LibraryAuthorityId))
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.LibraryAuthorityId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Version).First());

        // Scoped by the query filter, so this is the active project's imports only.
        var imported = (await db.References.AsNoTracking()
                .Where(x => x.OriginLibraryId != null)
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.OriginLibraryId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        return authorities
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(x => heads.ContainsKey(x.Id))
            .Select(authority =>
            {
                var head = heads[authority.Id];
                var local = imported.GetValueOrDefault(authority.Id);
                return new LibraryAuthoritySummary(
                    authority.Id, authority.Slug, authority.Name, authority.Category, head.Version,
                    head.Description, head.LockedConstraint, authority.Accent, authority.VisualVariant,
                    ImageUrl(authority.Id, head), authority.UpdatedAt,
                    local?.Id, local?.OriginVersion,
                    local?.OriginVersion is int held && held < head.Version);
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<LibraryAuthorityVersionSummary>> GetVersionsAsync(Guid libraryId, CancellationToken cancellationToken)
        => (await db.LibraryAuthorityVersions.AsNoTracking().Where(x => x.LibraryAuthorityId == libraryId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.Version)
            .Select(x => new LibraryAuthorityVersionSummary(
                x.Id, x.LibraryAuthorityId, x.Version, x.Description, x.LockedConstraint,
                ImageUrl(x.LibraryAuthorityId, x), x.ContentHash, x.RatifiedAt, x.OriginProjectId))
            .ToArray();

    /// <summary>
    /// The image bytes for a library version, resolved from its stored descriptor.
    ///
    /// Served from the library rather than through <c>/api/assets</c> because asset
    /// rows are project-scoped: previewing a library authority must not require the
    /// viewing project to already own a copy of it.
    /// </summary>
    public async Task<(string Path, string MimeType, string ContentHash)?> ResolveImageAsync(Guid libraryId, int version, CancellationToken cancellationToken)
    {
        var record = await db.LibraryAuthorityVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LibraryAuthorityId == libraryId && x.Version == version, cancellationToken);
        if (record?.ImageStoragePath is null || record.ImageContentHash is null) return null;
        var path = assets.ResolveStoragePath(record.ImageStoragePath);
        return File.Exists(path) ? (path, record.ImageMimeType ?? "application/octet-stream", record.ImageContentHash) : null;
    }

    /// <summary>
    /// Copies a library authority into the active project at the library's current
    /// version, recording provenance. Selective by construction: the artist imports
    /// the authorities they want and leaves the rest.
    /// </summary>
    public async Task<RepositoryResult<ReferenceSummary>> ImportAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var authority = await db.LibraryAuthorities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (authority is null) return RepositoryResult<ReferenceSummary>.NotFound();
        var head = await HeadVersionAsync(libraryId, cancellationToken);
        if (head is null) return RepositoryResult<ReferenceSummary>.Conflict("This library authority has no versions to import.");

        if (await db.References.AnyAsync(x => x.OriginLibraryId == libraryId, cancellationToken))
            return RepositoryResult<ReferenceSummary>.Conflict($"{authority.Name} is already imported into this project. Pull the latest version instead.");
        if (await db.References.AnyAsync(x => x.Name == authority.Name, cancellationToken))
            return RepositoryResult<ReferenceSummary>.Conflict($"This project already has an unrelated authority named {authority.Name}. Rename it before importing.");

        var now = timeProvider.GetUtcNow();
        var imageAssetId = await MaterialiseImageAsync(head, cancellationToken);
        // Keep the library's readable slug when this project has it free, so the same
        // character reads the same way across productions.
        var id = await FreeReferenceIdAsync(authority.Slug, cancellationToken);
        var record = new ReferenceRecord
        {
            Id = id,
            ProjectId = projectScope.ProjectId,
            Name = authority.Name,
            Category = authority.Category,
            CurrentVersion = 1,
            LastIssuedVersion = 1,
            Status = "Authority",
            Accent = authority.Accent,
            VisualVariant = authority.VisualVariant,
            OriginLibraryId = authority.Id,
            OriginVersion = head.Version,
            CreatedAt = now,
            UpdatedAt = now
        };
        // The project stack starts at v1 even though the library was at v7: this is a
        // new authority in this production, and its own version numbers are what its
        // manifests will cite. OriginVersion records what it was taken from.
        var version = new ReferenceVersionRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            ReferenceId = id,
            Version = 1,
            Description = head.Description,
            LockedConstraint = head.LockedConstraint,
            ImageAssetId = imageAssetId,
            ContentHash = StudioDatabaseInitializer.HashReference(id, 1, head.Description, head.LockedConstraint, head.ImageContentHash),
            RatifiedAt = now
        };
        db.References.Add(record);
        db.ReferenceVersions.Add(version);
        AddAudit("LibraryAuthorityImported", "Reference", id, new { libraryId, libraryVersion = head.Version, authority.Name, imageAssetId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(record, version));
    }

    /// <summary>
    /// Promotes a project-local authority into the library.
    ///
    /// This is the path that matters most in practice: most characters start inside
    /// one project and only prove reusable later. Without it the library would only
    /// ever receive what someone predicted in advance.
    ///
    /// An authority that already came from the library adds a version to it rather
    /// than creating a duplicate, so refinements made in a production flow back up.
    /// </summary>
    public async Task<RepositoryResult<LibraryAuthoritySummary>> PromoteAsync(string referenceId, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<LibraryAuthoritySummary>.NotFound();
        var head = await db.ReferenceVersions.AsNoTracking()
            .Where(x => x.ReferenceId == referenceId)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null) return RepositoryResult<LibraryAuthoritySummary>.Conflict("This authority has no versions to promote.");
        if (!AllowedCategories.Contains(reference.Category) || !HexColor.IsMatch(reference.Accent))
            return RepositoryResult<LibraryAuthoritySummary>.Invalid("Fix this authority's category and accent before promoting it.");

        var now = timeProvider.GetUtcNow();
        var descriptor = head.ImageAssetId is null ? null : await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == head.ImageAssetId, cancellationToken);

        var authority = reference.OriginLibraryId is Guid existingId
            ? await db.LibraryAuthorities.SingleOrDefaultAsync(x => x.Id == existingId, cancellationToken)
            : null;
        if (authority is null)
        {
            if (await db.LibraryAuthorities.AnyAsync(x => x.Name == reference.Name, cancellationToken))
                return RepositoryResult<LibraryAuthoritySummary>.Conflict($"The library already holds an authority named {reference.Name}. Import it instead, or rename this one.");
            authority = new LibraryAuthorityRecord
            {
                Id = Guid.NewGuid(),
                Slug = await FreeLibrarySlugAsync(reference.Id, cancellationToken),
                Name = reference.Name,
                Category = reference.Category,
                CurrentVersion = 0,
                LastIssuedVersion = 0,
                Accent = reference.Accent,
                VisualVariant = reference.VisualVariant,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.LibraryAuthorities.Add(authority);
        }

        var next = authority.LastIssuedVersion + 1;
        var libraryVersion = new LibraryAuthorityVersionRecord
        {
            Id = Guid.NewGuid(),
            LibraryAuthorityId = authority.Id,
            Version = next,
            Description = head.Description,
            LockedConstraint = head.LockedConstraint,
            ContentHash = StudioDatabaseInitializer.HashReference(authority.Slug, next, head.Description, head.LockedConstraint, descriptor?.ContentHash),
            RatifiedAt = now,
            OriginProjectId = projectScope.ProjectId,
            ImageContentHash = descriptor?.ContentHash,
            ImageStoragePath = descriptor?.StoragePath,
            ImageMimeType = descriptor?.MimeType,
            ImageBytes = descriptor?.Bytes,
            ImageWidth = descriptor?.Width,
            ImageHeight = descriptor?.Height
        };
        db.LibraryAuthorityVersions.Add(libraryVersion);
        authority.CurrentVersion = next;
        authority.LastIssuedVersion = next;
        authority.Category = reference.Category;
        authority.Accent = reference.Accent;
        authority.UpdatedAt = now;

        // Link the two from here on, so the project can see later library movement
        // and so a second promotion adds a version instead of a duplicate.
        reference.OriginLibraryId = authority.Id;
        reference.OriginVersion = next;
        reference.UpdatedAt = now;

        AddAudit("AuthorityPromotedToLibrary", "Reference", reference.Id, new { libraryId = authority.Id, libraryVersion = next, reference.Name, imageContentHash = descriptor?.ContentHash });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<LibraryAuthoritySummary>.Ok(new LibraryAuthoritySummary(
            authority.Id, authority.Slug, authority.Name, authority.Category, next,
            libraryVersion.Description, libraryVersion.LockedConstraint, authority.Accent, authority.VisualVariant,
            ImageUrl(authority.Id, libraryVersion), authority.UpdatedAt, reference.Id, next, false));
    }

    /// <summary>
    /// Takes a newer library version into an already-imported authority.
    ///
    /// Appends a version to the project's stack; it never edits the imported one.
    /// That is the whole point of provenance over linkage — a manifest frozen against
    /// v4 keeps meaning v4 after the project pulls v7.
    /// </summary>
    public async Task<RepositoryResult<ReferenceSummary>> PullUpdateAsync(string referenceId, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<ReferenceSummary>.NotFound();
        if (reference.OriginLibraryId is not Guid libraryId)
            return RepositoryResult<ReferenceSummary>.Conflict("This authority was created in this project and has no library origin to pull from.");
        var head = await HeadVersionAsync(libraryId, cancellationToken);
        if (head is null) return RepositoryResult<ReferenceSummary>.Conflict("The library authority no longer has any versions.");
        if (reference.OriginVersion is int held && held >= head.Version)
            return RepositoryResult<ReferenceSummary>.Conflict($"This project already holds library version {head.Version}.");

        var now = timeProvider.GetUtcNow();
        var imageAssetId = await MaterialiseImageAsync(head, cancellationToken);
        var next = reference.LastIssuedVersion + 1;
        var version = new ReferenceVersionRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            ReferenceId = reference.Id,
            Version = next,
            Description = head.Description,
            LockedConstraint = head.LockedConstraint,
            ImageAssetId = imageAssetId,
            ContentHash = StudioDatabaseInitializer.HashReference(reference.Id, next, head.Description, head.LockedConstraint, head.ImageContentHash),
            RatifiedAt = now
        };
        db.ReferenceVersions.Add(version);
        reference.CurrentVersion = next;
        reference.LastIssuedVersion = next;
        reference.OriginVersion = head.Version;
        reference.UpdatedAt = now;
        AddAudit("LibraryAuthorityUpdatePulled", "Reference", reference.Id, new { libraryId, libraryVersion = head.Version, projectVersion = next, imageAssetId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(reference, version));
    }

    /// <summary>
    /// Removes a library authority. Imports are copies, so nothing in any project
    /// breaks; the projects simply lose the ability to pull further updates, and
    /// their provenance is cleared so the list does not point at a missing origin.
    /// </summary>
    public async Task<RepositoryResult<bool>> DeleteAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var authority = await db.LibraryAuthorities.SingleOrDefaultAsync(x => x.Id == libraryId, cancellationToken);
        if (authority is null) return RepositoryResult<bool>.NotFound();
        var versions = await db.LibraryAuthorityVersions.Where(x => x.LibraryAuthorityId == libraryId).ToListAsync(cancellationToken);
        var dependents = await db.References.IgnoreQueryFilters().Where(x => x.OriginLibraryId == libraryId).ToListAsync(cancellationToken);
        foreach (var dependent in dependents) { dependent.OriginLibraryId = null; dependent.OriginVersion = null; }
        db.LibraryAuthorityVersions.RemoveRange(versions);
        db.LibraryAuthorities.Remove(authority);
        AddAudit("LibraryAuthorityDeleted", "LibraryAuthority", authority.Id.ToString(), new { authority.Name, destroyedVersions = versions.Count, detachedProjectCopies = dependents.Count });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(true);
    }

    private async Task<LibraryAuthorityVersionRecord?> HeadVersionAsync(Guid libraryId, CancellationToken cancellationToken)
        => (await db.LibraryAuthorityVersions.AsNoTracking().Where(x => x.LibraryAuthorityId == libraryId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

    /// <summary>
    /// Gives the active project its own asset row over the library image's bytes.
    ///
    /// Asset rows are project-scoped while the file store is content-addressed and
    /// shared, so this is a row-level copy and never a byte-level one: the same
    /// reference image across five projects is five rows and one file. Returns null
    /// when the version has no image, or when the stored file has gone missing —
    /// an authority without its picture is still usable, a broken row is not.
    /// </summary>
    private async Task<Guid?> MaterialiseImageAsync(LibraryAuthorityVersionRecord version, CancellationToken cancellationToken)
    {
        if (version.ImageContentHash is null || version.ImageStoragePath is null) return null;
        var existing = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.ContentHash == version.ImageContentHash, cancellationToken);
        if (existing is not null) return existing.Id;
        if (!File.Exists(assets.ResolveStoragePath(version.ImageStoragePath))) return null;

        var now = timeProvider.GetUtcNow();
        var record = new AssetRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            Kind = AssetKind.Image.ToString(),
            OriginalFileName = Path.GetFileName(version.ImageStoragePath),
            MimeType = version.ImageMimeType ?? "image/png",
            Bytes = version.ImageBytes ?? 0,
            Width = version.ImageWidth,
            Height = version.ImageHeight,
            ContentHash = version.ImageContentHash,
            StoragePath = version.ImageStoragePath,
            CreatedAt = now,
            DisplayName = $"Library import · {version.ImageContentHash[..8]}",
            TagsJson = JsonSerializer.Serialize(LibraryImportTags),
            Notes = "Imported from the authority library. The bytes are shared with every other project holding this image.",
            Source = "Library import",
            IsArchived = false,
            UpdatedAt = now
        };
        db.Assets.Add(record);
        return record.Id;
    }

    /// <summary>Reference ids are per-project, so a slug only has to be free here.</summary>
    private async Task<string> FreeReferenceIdAsync(string preferred, CancellationToken cancellationToken)
    {
        var basis = Slug(preferred);
        var candidate = basis;
        for (var suffix = 2; await db.References.AnyAsync(x => x.Id == candidate, cancellationToken); suffix++)
            candidate = $"{basis}-{suffix}";
        return candidate;
    }

    /// <summary>The library is one global namespace, so its slugs are unique overall.</summary>
    private async Task<string> FreeLibrarySlugAsync(string preferred, CancellationToken cancellationToken)
    {
        var basis = Slug(preferred);
        var candidate = basis;
        for (var suffix = 2; await db.LibraryAuthorities.AnyAsync(x => x.Slug == candidate, cancellationToken); suffix++)
            candidate = $"{basis}-{suffix}";
        return candidate;
    }

    /// <summary>
    /// Requires both halves of the descriptor, because resolution does: advertising a
    /// URL from the hash alone would hand out a link that is certain to 404.
    /// </summary>
    private static string? ImageUrl(Guid libraryId, LibraryAuthorityVersionRecord version)
        => version.ImageContentHash is null || version.ImageStoragePath is null
            ? null
            : $"/api/library/{libraryId}/versions/{version.Version}/image";

    private static ReferenceSummary MapReference(ReferenceRecord reference, ReferenceVersionRecord version) => new(
        reference.Id, reference.Name, reference.Category, version.Description, reference.CurrentVersion,
        reference.Status, reference.Accent, version.LockedConstraint, reference.VisualVariant,
        version.ImageAssetId, version.ImageAssetId is null ? null : $"/api/assets/{version.ImageAssetId}/content");

    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"authority-{Guid.NewGuid():N}" : slug[..Math.Min(slug.Length, 80)];
    }

    private void AddAudit(string type, string targetType, string targetId, object payload)
        => db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Type = type,
            TargetType = targetType,
            TargetId = targetId,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = timeProvider.GetUtcNow()
        });
}
