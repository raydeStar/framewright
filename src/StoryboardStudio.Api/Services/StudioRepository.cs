using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

public sealed class StudioRepository(
    StudioDbContext db,
    IProjectScope projectScope,
    ActiveProjectRegistry activeProject,
    AssetStore assets,
    IVideoMediaProbe videoMediaProbe,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SketchObjectKinds = new(["Human", "Chair", "Table", "Doorway", "Arrow", "Block"], StringComparer.Ordinal);
    private static readonly HashSet<string> HumanPoses = new(["Neutral", "Attention", "Seated", "Kneeling", "Prone", "Walking", "Running", "Pointing", "Reaching", "Conversation", "Custom"], StringComparer.Ordinal);
    private static readonly HashSet<string> HumanFacings = new(["Front", "Three-quarter left", "Three-quarter right", "Profile left", "Profile right", "Back"], StringComparer.Ordinal);
    private static readonly HashSet<string> HumanBuilds = new(["Neutral", "Slender", "Broad", "Compact", "Child"], StringComparer.Ordinal);
    private static readonly HashSet<string> IdentityModes = new(["Unique person", "Exact character", "Anonymous"], StringComparer.Ordinal);
    public async Task<StudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var comments = (await db.Comments.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(x => x.CreatedAt).ToList();
        // Read terminal job state before its promoted shot. A completed job and
        // its shot update commit atomically; this ordering prevents one
        // snapshot from reporting Completed alongside the pre-promotion slot.
        var jobs = (await db.Jobs.AsNoTracking().ToListAsync(cancellationToken)).OrderByDescending(x => x.CreatedAt).Take(12).ToList();
        var shots = await db.Shots.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var references = await GetReferenceSummariesAsync(cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);

        return new StudioSnapshot(
            MapProject(project),
            shots.Select(x => MapShot(x, comments.Count(comment => comment.ShotId == x.Id && comment.Version == x.Version && comment.State == "Open"))).ToArray(),
            references,
            comments.Select(MapComment).ToArray(),
            jobs.Select(MapJob).ToArray(),
            timeProvider.GetUtcNow(),
            false);
    }

    public async Task<RepositoryResult<ProjectSummary>> UpdateProjectAsync(UpdateProjectRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        if (project.UpdatedAt != request.ExpectedUpdatedAt) return RepositoryResult<ProjectSummary>.Conflict("Project settings changed. Refresh before saving.");
        var validation = ValidateProjectContract(
            request.Name, request.Production, request.SequenceCode, request.SequenceName,
            request.FramesPerSecond, request.AspectRatio, request.DeliveryWidth, request.DeliveryHeight,
            request.ColorSpace, request.AudioSampleRate,
            request.VisualStyle, request.WorldCanon, request.PromptDirectives, request.NegativeDirectives,
            out var aspectRatio);
        if (validation is not null) return RepositoryResult<ProjectSummary>.Invalid(validation);
        var requestedColorSpace = request.ColorSpace.Trim();
        var requestedVisualStyle = request.VisualStyle.Trim();
        var requestedWorldCanon = request.WorldCanon.Trim();
        var requestedPromptDirectives = request.PromptDirectives.Trim();
        var requestedNegativeDirectives = request.NegativeDirectives.Trim();
        var deliveryContractChanged =
            project.FramesPerSecond != request.FramesPerSecond ||
            !string.Equals(project.AspectRatio, aspectRatio, StringComparison.OrdinalIgnoreCase) ||
            project.DeliveryWidth != request.DeliveryWidth ||
            project.DeliveryHeight != request.DeliveryHeight ||
            !string.Equals(project.ColorSpace, requestedColorSpace, StringComparison.OrdinalIgnoreCase) ||
            project.AudioSampleRate != request.AudioSampleRate;
        var worldContractChanged =
            !string.Equals(project.VisualStyle, requestedVisualStyle, StringComparison.Ordinal) ||
            !string.Equals(project.WorldCanon, requestedWorldCanon, StringComparison.Ordinal) ||
            !string.Equals(project.PromptDirectives, requestedPromptDirectives, StringComparison.Ordinal) ||
            !string.Equals(project.NegativeDirectives, requestedNegativeDirectives, StringComparison.Ordinal);
        var generationContractChanged = deliveryContractChanged || worldContractChanged;
        var previousDeliveryContract = new
        {
            project.FramesPerSecond,
            project.AspectRatio,
            project.DeliveryWidth,
            project.DeliveryHeight,
            project.ColorSpace,
            project.AudioSampleRate
        };
        var previousWorldContract = new
        {
            project.VisualStyle,
            project.WorldCanon,
            project.PromptDirectives,
            project.NegativeDirectives
        };
        var invalidatedVideoBindings = 0;
        var reopenedRatifiedShots = 0;
        if (generationContractChanged)
        {
            var now = timeProvider.GetUtcNow();
            var shots = await db.Shots.ToListAsync(cancellationToken);
            var currentCandidates = await db.CandidateVersions
                .Where(candidate => candidate.IsCurrent)
                .ToListAsync(cancellationToken);
            var highestCandidateVersions = await db.CandidateVersions
                .GroupBy(candidate => candidate.ShotId)
                .Select(group => new { ShotId = group.Key, Version = group.Max(candidate => candidate.Version) })
                .ToDictionaryAsync(item => item.ShotId, item => item.Version, cancellationToken);
            var openComments = await db.Comments
                .Where(comment => comment.State == "Open")
                .ToListAsync(cancellationToken);
            foreach (var shot in shots)
            {
                var sourceVersion = shot.Version;
                var bindingInvalidated = shot.ProductionVideoJobId is not null || shot.ProductionVideoAssetId is not null;
                if (bindingInvalidated)
                    invalidatedVideoBindings++;
                shot.ProductionVideoJobId = null;
                shot.ProductionVideoAssetId = null;

                // World laws and delivery geometry are inputs to every visual
                // output, not presentation metadata. Preserve the old ratified
                // authority row, but reopen the current shot so an export can
                // never claim that pixels generated under the old contract match
                // the newly selected project canon.
                if (shot.Approval == nameof(ApprovalState.Ratified)) reopenedRatifiedShots++;
                foreach (var current in currentCandidates.Where(candidate => candidate.ShotId == shot.Id))
                {
                    current.IsCurrent = false;
                    current.SupersededAt = now;
                }
                shot.Version = Math.Max(sourceVersion, highestCandidateVersions.GetValueOrDefault(shot.Id, sourceVersion)) + 1;
                shot.Approval = nameof(ApprovalState.Working);
                shot.ContinuityState = "Review";
                shot.UpdatedAt = now;
                db.CandidateVersions.Add(new CandidateVersionRecord
                {
                    Id = Guid.NewGuid(),
                    ShotId = shot.Id,
                    Version = shot.Version,
                    Stage = shot.Stage,
                    Approval = nameof(ApprovalState.Working),
                    IsCurrent = true,
                    AssetId = shot.CurrentAssetId,
                    CreatedAt = now
                });
                foreach (var comment in openComments.Where(comment => comment.ShotId == shot.Id && comment.Version == sourceVersion))
                {
                    db.Comments.Add(new CommentRecord
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = comment.ProjectId,
                        ShotId = shot.Id,
                        Version = shot.Version,
                        X = comment.X,
                        Y = comment.Y,
                        Body = comment.Body,
                        State = "Open",
                        CreatedAt = now,
                        ReferenceId = comment.ReferenceId,
                        ReferenceVersion = comment.ReferenceVersion
                    });
                }
            }
        }
        project.Name = request.Name.Trim(); project.Production = request.Production.Trim(); project.SequenceCode = request.SequenceCode.Trim().ToUpperInvariant();
        project.SequenceName = request.SequenceName.Trim(); project.FramesPerSecond = request.FramesPerSecond; project.AspectRatio = aspectRatio; project.DeliveryWidth = request.DeliveryWidth; project.DeliveryHeight = request.DeliveryHeight; project.ColorSpace = requestedColorSpace; project.AudioSampleRate = request.AudioSampleRate;
        project.VisualStyle = requestedVisualStyle; project.WorldCanon = requestedWorldCanon; project.PromptDirectives = requestedPromptDirectives; project.NegativeDirectives = requestedNegativeDirectives; project.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("ProjectSettingsUpdated", "Project", project.Id.ToString(), new { project.Name, project.Production, project.SequenceCode, project.SequenceName, project.FramesPerSecond, project.AspectRatio, project.DeliveryWidth, project.DeliveryHeight, project.ColorSpace, project.AudioSampleRate, project.VisualStyle, project.WorldCanon, project.PromptDirectives, project.NegativeDirectives, deliveryContractChanged, worldContractChanged, generationContractChanged, previousDeliveryContract, previousWorldContract, invalidatedVideoBindings, reopenedRatifiedShots });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RepositoryResult<ProjectSummary>.Conflict("Project settings changed while they were being saved."); }
        return RepositoryResult<ProjectSummary>.Ok(MapProject(project));
    }

    /// <summary>
    /// Every project, with the counts that let the switcher distinguish them.
    ///
    /// Deliberately reads across the scope: this is the one list whose whole job is
    /// to see other projects, so the shot and authority counts ignore the filters.
    /// </summary>
    public async Task<IReadOnlyList<ProjectListItem>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = await db.Projects.AsNoTracking().ToListAsync(cancellationToken);
        var shotCounts = await db.Shots.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(x => x.ProjectId).Select(x => new { ProjectId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, cancellationToken);
        var authorityCounts = await db.References.IgnoreQueryFilters().AsNoTracking()
            .GroupBy(x => x.ProjectId).Select(x => new { ProjectId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, cancellationToken);
        var active = activeProject.Current;
        return projects
            .OrderByDescending(x => x.Id == active)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ProjectListItem(
                x.Id, x.Name, x.Production, x.SequenceCode, x.SequenceName,
                shotCounts.GetValueOrDefault(x.Id), authorityCounts.GetValueOrDefault(x.Id),
                x.Id == active, x.UpdatedAt, x.IsSample))
            .ToArray();
    }

    public async Task<RepositoryResult<ProjectSummary>> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateProjectContract(
            request.Name, request.Production, request.SequenceCode, request.SequenceName,
            request.FramesPerSecond, request.AspectRatio, request.DeliveryWidth, request.DeliveryHeight,
            request.ColorSpace, request.AudioSampleRate,
            request.VisualStyle ?? StudioDefaults.VisualStyle,
            request.WorldCanon ?? StudioDefaults.WorldCanon,
            request.PromptDirectives ?? StudioDefaults.PromptDirectives,
            request.NegativeDirectives ?? StudioDefaults.NegativeDirectives,
            out var aspectRatio);
        if (validation is not null) return RepositoryResult<ProjectSummary>.Invalid(validation);

        var name = request.Name.Trim();
        if (await db.Projects.AnyAsync(x => x.Name == name, cancellationToken))
            return RepositoryResult<ProjectSummary>.Conflict("A project with this name already exists.");

        var record = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            Production = request.Production.Trim(),
            SequenceCode = request.SequenceCode.Trim().ToUpperInvariant(),
            SequenceName = request.SequenceName.Trim(),
            FramesPerSecond = request.FramesPerSecond,
            AspectRatio = aspectRatio,
            DeliveryWidth = request.DeliveryWidth,
            DeliveryHeight = request.DeliveryHeight,
            ColorSpace = request.ColorSpace.Trim(),
            AudioSampleRate = request.AudioSampleRate,
            VisualStyle = (request.VisualStyle ?? StudioDefaults.VisualStyle).Trim(),
            WorldCanon = (request.WorldCanon ?? StudioDefaults.WorldCanon).Trim(),
            PromptDirectives = (request.PromptDirectives ?? StudioDefaults.PromptDirectives).Trim(),
            NegativeDirectives = (request.NegativeDirectives ?? StudioDefaults.NegativeDirectives).Trim(),
            // A new project starts empty, so switching to it immediately would hide
            // the artist's current work behind a blank board. Activation is a
            // separate, explicit act.
            IsActive = false,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        db.Projects.Add(record);
        AddAudit("ProjectCreated", "Project", record.Id.ToString(), new { record.Name, record.Production, record.SequenceCode, record.FramesPerSecond, record.AspectRatio, record.DeliveryWidth, record.DeliveryHeight, record.ColorSpace, record.AudioSampleRate });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ProjectSummary>.Ok(MapProject(record));
    }

    /// <summary>
    /// Switches the active project. The database row and the in-memory registry are
    /// updated together, and the row is written first: if the process dies between
    /// the two, a restart reads the persisted choice rather than reverting.
    /// </summary>
    public async Task<RepositoryResult<ProjectSummary>> ActivateProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var target = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (target is null) return RepositoryResult<ProjectSummary>.NotFound();

        foreach (var project in await db.Projects.Where(x => x.IsActive && x.Id != projectId).ToListAsync(cancellationToken))
        {
            project.IsActive = false;
        }
        target.IsActive = true;
        AddAudit("ProjectActivated", "Project", target.Id.ToString(), new { target.Name, previous = activeProject.Current });
        await db.SaveChangesAsync(cancellationToken);
        activeProject.Set(target.Id);
        projectScope.Bind(target.Id);
        return RepositoryResult<ProjectSummary>.Ok(MapProject(target));
    }

    /// <summary>
    /// Deletes a project and everything scoped to it.
    ///
    /// Refused while it is the active project, so the artist cannot delete the board
    /// they are looking at, and refused for the last remaining project, because an
    /// app with no project has no coherent state to show. The audit trail is
    /// deliberately not deleted: it is the record that the project existed.
    /// </summary>
    public async Task<RepositoryResult<ProjectDeletionSummary>> DeleteProjectAsync(Guid projectId, bool acceptRatifiedLoss, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null) return RepositoryResult<ProjectDeletionSummary>.NotFound();
        if (projectId == activeProject.Current)
            return RepositoryResult<ProjectDeletionSummary>.Conflict("This is the active project. Switch to another project before deleting it.");
        if (await db.Projects.CountAsync(cancellationToken) <= 1)
            return RepositoryResult<ProjectDeletionSummary>.Conflict("The last project cannot be deleted.");

        var ratified = await db.ShotVersions.IgnoreQueryFilters().CountAsync(x => x.ProjectId == projectId, cancellationToken);
        if (ratified > 0 && !acceptRatifiedLoss)
            return RepositoryResult<ProjectDeletionSummary>.Conflict(
                $"{project.Name} holds {ratified} ratified version{(ratified == 1 ? "" : "s")} archived as approval evidence. Confirm explicitly to delete the project and that evidence together.");

        var running = await db.Jobs.IgnoreQueryFilters().AnyAsync(
            x => x.ProjectId == projectId && (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString()), cancellationToken);
        if (running) return RepositoryResult<ProjectDeletionSummary>.Conflict("A generation job is still running in this project. Wait for it to finish before deleting it.");

        var shots = await db.Shots.IgnoreQueryFilters().CountAsync(x => x.ProjectId == projectId, cancellationToken);
        var authorities = await db.References.IgnoreQueryFilters().CountAsync(x => x.ProjectId == projectId, cancellationToken);
        var assets = await db.Assets.IgnoreQueryFilters().CountAsync(x => x.ProjectId == projectId, cancellationToken);
        var summary = new ProjectDeletionSummary(project.Name, shots, authorities, ratified, assets);

        // A project is one unit of production evidence. Every scoped row, the
        // project record, and the surviving audit event therefore commit as one
        // SQLite transaction. A full disk, cancellation, or malformed legacy row
        // must leave the entire project intact rather than producing a half-empty
        // board with no trustworthy account of what was lost.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var shotIds = await db.Shots.IgnoreQueryFilters()
                .Where(x => x.ProjectId == projectId)
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);
            var assetIds = await db.Assets.IgnoreQueryFilters()
                .Where(x => x.ProjectId == projectId)
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);

            // AssetPlacements has no ProjectId by design, so remove joins while
            // both parent-id sets still exist. Otherwise project deletion leaves
            // invisible orphan rows that can later collide with imported ids.
            await db.AssetPlacements
                .Where(x => shotIds.Contains(x.ShotId) || assetIds.Contains(x.AssetId))
                .ExecuteDeleteAsync(cancellationToken);

            // Assets are content-addressed and deduplicated across projects, so
            // rows go but stored files deliberately stay: another project may
            // hold identical bytes, and a shared store must never be pruned here.
            await db.Shots.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.Comments.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.AssetReviewNotes.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.ShotVisualAudits.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.ShotRevisionProposals.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.Jobs.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.ShotVersions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SketchDocuments.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.PosePresets.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.GenerationManifests.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.ReferenceVersions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.References.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.CandidateVersions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.FrameMarkups.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.TimelineClips.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.VoiceProfiles.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.MusicRenders.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.MusicCompositionRevisions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.MusicCompositions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneShotBindings.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneAnnotations.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneProposals.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneBlockoutItems.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneBlockoutPlans.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.SceneInstances.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.Scenes.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.AssetCollections.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);
            await db.Assets.IgnoreQueryFilters().Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(cancellationToken);

            AddAudit("ProjectDeleted", "Project", projectId.ToString(), new
            {
                project.Name,
                project.Production,
                destroyedShots = shots,
                destroyedAuthorities = authorities,
                destroyedRatifiedVersions = ratified,
                destroyedAssetRecords = assets,
                acceptRatifiedLoss
            });
            db.Projects.Remove(project);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        return RepositoryResult<ProjectDeletionSummary>.Ok(summary);
    }

    public async Task<RepositoryResult<IReadOnlyList<ShotSummary>>> ReorderShotsAsync(ReorderShotsRequest request, CancellationToken cancellationToken)
    {
        var shots = await db.Shots.OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        if (request.ShotIds.Count != shots.Count || request.ShotIds.Distinct().Count() != shots.Count || request.ShotIds.Any(id => shots.All(x => x.Id != id)))
            return RepositoryResult<IReadOnlyList<ShotSummary>>.Invalid("Shot order must contain every current shot exactly once.");
        var byId = shots.ToDictionary(x => x.Id); var now = timeProvider.GetUtcNow();
        for (var index = 0; index < request.ShotIds.Count; index++) { var shot = byId[request.ShotIds[index]]; shot.SortOrder = index + 1; shot.UpdatedAt = now; }
        AddAudit("ShotOrderChanged", "Project", projectScope.ProjectId.ToString(), new { shotIds = request.ShotIds });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<IReadOnlyList<ShotSummary>>.Ok(request.ShotIds.Select(id => MapShot(byId[id], 0)).ToArray());
    }

    public async Task<RepositoryResult<ReferenceSummary>> CreateReferenceAsync(CreateReferenceRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateReference(request.Name, request.Category, request.Description, request.LockedConstraint, request.Accent);
        if (validation is not null) return RepositoryResult<ReferenceSummary>.Invalid(validation);
        AssetRecord? asset = null;
        if (request.ImageAssetId is not null)
        {
            asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ImageAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (asset is null) return RepositoryResult<ReferenceSummary>.Invalid("The authority image is not a valid project image asset.");
        }
        var idBase = Slug(request.Name);
        var id = idBase;
        var suffix = 2;
        while (await db.References.AnyAsync(x => x.Id == id, cancellationToken)) id = $"{idBase}-{suffix++}";
        if (await db.References.AnyAsync(x => x.Name == request.Name.Trim(), cancellationToken))
        {
            return RepositoryResult<ReferenceSummary>.Conflict("An authority with this name already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var visualVariant = (await db.References.MaxAsync(x => (int?)x.VisualVariant, cancellationToken) ?? 0) % 9 + 1;
        var record = new ReferenceRecord
        {
            Id = id,
            ProjectId = projectScope.ProjectId,
            Name = request.Name.Trim(),
            Category = NormalizeCategory(request.Category),
            CurrentVersion = 1,
            LastIssuedVersion = 1,
            Status = "Authority",
            Accent = request.Accent.ToLowerInvariant(),
            VisualVariant = visualVariant,
            CreatedAt = now,
            UpdatedAt = now
        };
        var version = new ReferenceVersionRecord
        {
            Id = Guid.NewGuid(),
            ReferenceId = id,
            Version = 1,
            Description = request.Description.Trim(),
            LockedConstraint = request.LockedConstraint.Trim(),
            ImageAssetId = asset?.Id,
            ContentHash = StudioDatabaseInitializer.HashReference(id, 1, request.Description.Trim(), request.LockedConstraint.Trim(), asset?.ContentHash),
            RatifiedAt = now
        };
        db.References.Add(record);
        db.ReferenceVersions.Add(version);
        AddAudit("AuthorityCreated", "Reference", id, new { version.Id, version.Version, version.ContentHash, version.ImageAssetId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(record, version));
    }

    public async Task<RepositoryResult<ReferenceSummary>> CreateReferenceVersionAsync(string referenceId, CreateReferenceVersionRequest request, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<ReferenceSummary>.NotFound();
        if (reference.CurrentVersion != request.ExpectedVersion) return RepositoryResult<ReferenceSummary>.Conflict($"The authority changed from version {request.ExpectedVersion} to {reference.CurrentVersion}. Refresh before creating a version.");
        var validation = ValidateReference(reference.Name, reference.Category, request.Description, request.LockedConstraint, reference.Accent);
        if (validation is not null) return RepositoryResult<ReferenceSummary>.Invalid(validation);
        AssetRecord? asset = null;
        if (request.ImageAssetId is not null)
        {
            asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ImageAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (asset is null) return RepositoryResult<ReferenceSummary>.Invalid("The authority image is not a valid project image asset.");
        }
        var now = timeProvider.GetUtcNow();
        var next = reference.LastIssuedVersion + 1;
        var version = new ReferenceVersionRecord
        {
            Id = Guid.NewGuid(),
            ReferenceId = reference.Id,
            Version = next,
            Description = request.Description.Trim(),
            LockedConstraint = request.LockedConstraint.Trim(),
            ImageAssetId = asset?.Id,
            ContentHash = StudioDatabaseInitializer.HashReference(reference.Id, next, request.Description.Trim(), request.LockedConstraint.Trim(), asset?.ContentHash),
            RatifiedAt = now
        };
        reference.CurrentVersion = next;
        reference.LastIssuedVersion = next;
        reference.UpdatedAt = now;
        db.ReferenceVersions.Add(version);
        AddAudit("AuthorityVersionCreated", "Reference", reference.Id, new { version.Id, version.Version, version.ContentHash, version.ImageAssetId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(reference, version));
    }

    /// <summary>
    /// Re-issues an older version's content as a new version on top of the stack.
    ///
    /// The product model is that the top of the stack is what ships, so promotion
    /// copies forward rather than moving a live pointer: there is nothing to mark,
    /// and history stays append-only. Mirrors shot candidate copy-forward.
    /// </summary>
    public async Task<RepositoryResult<ReferenceSummary>> PromoteReferenceVersionAsync(string referenceId, int version, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<ReferenceSummary>.NotFound();
        var source = await db.ReferenceVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ReferenceId == referenceId && x.Version == version, cancellationToken);
        if (source is null) return RepositoryResult<ReferenceSummary>.NotFound();
        if (source.Version == reference.CurrentVersion)
            return RepositoryResult<ReferenceSummary>.Conflict($"Version {version} is already at the top of the stack.");

        var now = timeProvider.GetUtcNow();
        var next = reference.LastIssuedVersion + 1;
        string? sourceHash = null;
        if (source.ImageAssetId is not null)
            sourceHash = (await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.ImageAssetId, cancellationToken))?.ContentHash;
        var promoted = new ReferenceVersionRecord
        {
            Id = Guid.NewGuid(),
            ReferenceId = reference.Id,
            Version = next,
            Description = source.Description,
            LockedConstraint = source.LockedConstraint,
            ImageAssetId = source.ImageAssetId,
            ContentHash = StudioDatabaseInitializer.HashReference(reference.Id, next, source.Description, source.LockedConstraint, sourceHash),
            RatifiedAt = now
        };
        reference.CurrentVersion = next;
        reference.LastIssuedVersion = next;
        reference.UpdatedAt = now;
        db.ReferenceVersions.Add(promoted);
        AddAudit("AuthorityVersionPromoted", "Reference", reference.Id, new { from = version, to = next, promoted.ContentHash });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(reference, promoted));
    }

    /// <summary>
    /// Drops a version off the stack.
    ///
    /// A version that a frozen manifest cited is refused: a rendered frame has to
    /// stay explainable against the exact inputs recorded for it. Discarded
    /// attempts that nothing points at are free to remove.
    /// </summary>
    public async Task<RepositoryResult<ReferenceSummary>> DeleteReferenceVersionAsync(string referenceId, int version, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<ReferenceSummary>.NotFound();
        var versions = await db.ReferenceVersions.Where(x => x.ReferenceId == referenceId).OrderBy(x => x.Version).ToListAsync(cancellationToken);
        var target = versions.SingleOrDefault(x => x.Version == version);
        if (target is null) return RepositoryResult<ReferenceSummary>.NotFound();
        if (versions.Count == 1) return RepositoryResult<ReferenceSummary>.Conflict("An authority must keep at least one version. Delete the authority instead.");

        var citedBy = await CitingManifestsAsync(referenceId, version, cancellationToken);
        if (citedBy.Length > 0)
            return RepositoryResult<ReferenceSummary>.Conflict(
                $"Version {version} is cited by {citedBy.Length} frozen manifest{(citedBy.Length == 1 ? "" : "s")} ({string.Join(", ", citedBy.Take(3))}). Removing it would leave those frames unexplainable.");

        var pinCount = await db.Comments.AsNoTracking().CountAsync(
            x => x.ReferenceId == referenceId && x.ReferenceVersion == version, cancellationToken);
        if (pinCount > 0)
            return RepositoryResult<ReferenceSummary>.Conflict(
                $"Version {version} is preserved by {pinCount} retained frame placement record{(pinCount == 1 ? "" : "s")}. Removing it would break that exact-version history.");

        db.ReferenceVersions.Remove(target);
        var remaining = versions.Where(x => x.Version != version).ToList();
        var head = remaining[^1];
        reference.CurrentVersion = head.Version;
        reference.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AuthorityVersionDeleted", "Reference", reference.Id, new { version, newHead = head.Version });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(reference, head));
    }

    /// <summary>
    /// Edits the authority's identity metadata. Versioned content (description,
    /// locked constraint, image) is never edited in place: changing that still
    /// requires a new version on the stack.
    /// </summary>
    public async Task<RepositoryResult<ReferenceSummary>> UpdateReferenceAsync(string referenceId, UpdateReferenceRequest request, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<ReferenceSummary>.NotFound();
        var head = await db.ReferenceVersions.AsNoTracking()
            .Where(x => x.ReferenceId == referenceId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        if (head is null) return RepositoryResult<ReferenceSummary>.NotFound();
        var validation = ValidateReference(request.Name, request.Category, head.Description, head.LockedConstraint, request.Accent);
        if (validation is not null) return RepositoryResult<ReferenceSummary>.Invalid(validation);
        if (await db.References.AnyAsync(x => x.Id != referenceId && x.Name == request.Name.Trim(), cancellationToken))
            return RepositoryResult<ReferenceSummary>.Conflict("Another authority already uses this name.");

        reference.Name = request.Name.Trim();
        reference.Category = NormalizeCategory(request.Category);
        reference.Accent = request.Accent.Trim().ToLowerInvariant();
        reference.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("AuthorityUpdated", "Reference", reference.Id, new { reference.Name, reference.Category });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ReferenceSummary>.Ok(MapReference(reference, head));
    }

    /// <summary>
    /// Removes an authority and its whole stack. Refused while any shot still
    /// cites it, so a shot can never reference something that is gone.
    /// </summary>
    public async Task<RepositoryResult<bool>> DeleteReferenceAsync(string referenceId, CancellationToken cancellationToken)
    {
        var reference = await db.References.SingleOrDefaultAsync(x => x.Id == referenceId, cancellationToken);
        if (reference is null) return RepositoryResult<bool>.NotFound();

        var shots = await db.Shots.AsNoTracking().Select(x => new { x.Code, x.ReferenceIdsJson }).ToListAsync(cancellationToken);
        var citing = shots
            .Where(x => (JsonSerializer.Deserialize<string[]>(x.ReferenceIdsJson, CanonicalJson) ?? []).Contains(referenceId, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Code).ToArray();
        if (citing.Length > 0)
            return RepositoryResult<bool>.Conflict(
                $"{reference.Name} is cited by {string.Join(", ", citing)}. Remove it from those shots before deleting the authority.");

        var manifestCitations = await CitingManifestVersionsAsync(referenceId, cancellationToken);
        if (manifestCitations.Length > 0)
            return RepositoryResult<bool>.Conflict(
                $"{reference.Name} is preserved by {manifestCitations.Length} frozen manifest{(manifestCitations.Length == 1 ? "" : "s")} ({string.Join(", ", manifestCitations.Take(3))}). The authority cannot be deleted while generated work cites it.");

        var pinCount = await db.Comments.AsNoTracking().CountAsync(x => x.ReferenceId == referenceId, cancellationToken);
        if (pinCount > 0)
            return RepositoryResult<bool>.Conflict(
                $"{reference.Name} is preserved by {pinCount} retained frame placement record{(pinCount == 1 ? "" : "s")}. Removing it would break that exact-version history.");

        var versions = await db.ReferenceVersions.Where(x => x.ReferenceId == referenceId).ToListAsync(cancellationToken);
        AddAudit("AuthorityDeleted", "Reference", reference.Id, new { reference.Name, reference.Category, destroyedVersions = versions.Count });
        db.ReferenceVersions.RemoveRange(versions);
        db.References.Remove(reference);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(true);
    }

    /// <summary>Shot codes whose frozen manifests bound this exact authority version.</summary>
    private async Task<string[]> CitingManifestsAsync(string referenceId, int version, CancellationToken cancellationToken)
    {
        var manifests = await db.GenerationManifests.AsNoTracking().Select(x => new { x.ShotCode, x.AuthoritiesJson }).ToListAsync(cancellationToken);
        return manifests
            // CanonicalJson matters: bindings are written camelCase, and the default
            // options are case-sensitive, so a plain Deserialize silently produced
            // empty ids and version 0 — the guard existed but could never fire.
            .Where(x => (JsonSerializer.Deserialize<AuthorityBinding[]>(x.AuthoritiesJson, CanonicalJson) ?? [])
                .Any(binding => string.Equals(binding.Id, referenceId, StringComparison.OrdinalIgnoreCase) && binding.Version == version))
            .Select(x => x.ShotCode).Distinct().ToArray();
    }

    /// <summary>Shot codes whose frozen manifests bind any version of this authority.</summary>
    private async Task<string[]> CitingManifestVersionsAsync(string referenceId, CancellationToken cancellationToken)
    {
        var manifests = await db.GenerationManifests.AsNoTracking().Select(x => new { x.ShotCode, x.AuthoritiesJson }).ToListAsync(cancellationToken);
        return manifests
            .Where(x => (JsonSerializer.Deserialize<AuthorityBinding[]>(x.AuthoritiesJson, CanonicalJson) ?? [])
                .Any(binding => string.Equals(binding.Id, referenceId, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.ShotCode).Distinct().ToArray();
    }

    public async Task<IReadOnlyList<ReferenceVersionSummary>> GetReferenceVersionsAsync(string referenceId, CancellationToken cancellationToken)
    {
        var versions = await db.ReferenceVersions.AsNoTracking().Where(x => x.ReferenceId == referenceId).ToListAsync(cancellationToken);
        return versions.OrderByDescending(x => x.Version).Select(x => new ReferenceVersionSummary(
            x.Id, x.ReferenceId, x.Version, x.Description, x.LockedConstraint, x.ImageAssetId,
            x.ImageAssetId is null ? null : $"/api/assets/{x.ImageAssetId}/content", x.ContentHash, x.RatifiedAt)).ToArray();
    }

    public async Task<RepositoryResult<ShotSummary>> CreateShotAsync(CreateShotRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateShotAsync(request.Code, request.Title, request.Description, request.DurationFrames, request.Camera, request.Action, request.ReferenceIds, request.Constraints, cancellationToken);
        if (error is not null) return RepositoryResult<ShotSummary>.Invalid(error);
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Shots.AnyAsync(x => x.Code == code, cancellationToken)) return RepositoryResult<ShotSummary>.Conflict("A shot with this code already exists.");
        AssetRecord? initialImage = null;
        if (request.InitialImageAssetId is not null)
        {
            initialImage = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.InitialImageAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (initialImage is null) return RepositoryResult<ShotSummary>.Invalid("The dropped shot image is not a valid project image asset.");
        }
        var now = timeProvider.GetUtcNow();
        var record = new ShotRecord
        {
            Id = Guid.NewGuid(),
            Code = code,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Stage = initialImage is null ? ShotStage.Sketch.ToString() : ShotStage.Draft.ToString(),
            Approval = ApprovalState.Working.ToString(),
            Version = 1,
            DurationFrames = request.DurationFrames,
            SortOrder = (await db.Shots.MaxAsync(x => (int?)x.SortOrder, cancellationToken) ?? 0) + 1,
            VisualVariant = (await db.Shots.MaxAsync(x => (int?)x.VisualVariant, cancellationToken) ?? 0) % 9 + 1,
            ContinuityState = "Pending",
            Camera = request.Camera.Trim(),
            Action = request.Action.Trim(),
            ReferenceIdsJson = JsonSerializer.Serialize(request.ReferenceIds.Distinct().ToArray()),
            ConstraintsJson = JsonSerializer.Serialize(request.Constraints.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray()),
            CurrentAssetId = initialImage?.Id,
            UpdatedAt = now
        };
        db.Shots.Add(record);
        db.CandidateVersions.Add(new CandidateVersionRecord
        {
            Id = Guid.NewGuid(),
            ShotId = record.Id,
            Version = 1,
            Stage = record.Stage,
            Approval = record.Approval,
            IsCurrent = true,
            AssetId = initialImage?.Id,
            CreatedAt = now
        });
        AddAudit("ShotCreated", "Shot", record.Id.ToString(), new { record.Code, record.Version, initialImageAssetId = initialImage?.Id });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ShotSummary>.Ok(MapShot(record, 0));
    }

    /// <summary>
    /// Removes a shot slot and everything scoped to it.
    ///
    /// Ratified versions are append-only evidence under the product rules, so a
    /// shot that carries any is refused unless the caller explicitly accepts the
    /// loss. Assets are content-addressed and shared, so they are deliberately
    /// left alone — deleting a slot must never orphan another shot's imagery.
    /// The audit event records what was destroyed, including the ratified
    /// versions, so the trail survives the rows.
    /// </summary>
    public async Task<RepositoryResult<ShotDeletionSummary>> DeleteShotAsync(Guid shotId, bool acceptRatifiedLoss, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<ShotDeletionSummary>.NotFound();

        var ratified = await db.ShotVersions.Where(x => x.ShotId == shotId).CountAsync(cancellationToken);
        if (ratified > 0 && !acceptRatifiedLoss)
            return RepositoryResult<ShotDeletionSummary>.Conflict(
                $"{shot.Code} has {ratified} ratified version{(ratified == 1 ? "" : "s")} archived as approval evidence. Confirm explicitly to delete the slot and that evidence together.");

        var running = await db.Jobs.AnyAsync(x => x.ShotId == shotId && (x.State == JobState.Queued.ToString() || x.State == JobState.Running.ToString()), cancellationToken);
        if (running) return RepositoryResult<ShotDeletionSummary>.Conflict("A generation job is still running for this shot. Wait for it to finish before deleting the slot.");

        var candidates = await db.CandidateVersions.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var comments = await db.Comments.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var jobs = await db.Jobs.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var versions = await db.ShotVersions.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var sketches = await db.SketchDocuments.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var manifests = await db.GenerationManifests.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);
        var markups = await db.FrameMarkups.Where(x => x.ShotId == shotId).ToListAsync(cancellationToken);

        var summary = new ShotDeletionSummary(shot.Code, candidates.Count, versions.Count, comments.Count, jobs.Count);
        AddAudit("ShotDeleted", "Shot", shot.Id.ToString(), new
        {
            shot.Code,
            shot.Title,
            shot.Stage,
            shot.Approval,
            shot.Version,
            destroyedCandidates = candidates.Count,
            destroyedRatifiedVersions = versions.Select(x => x.Version).ToArray(),
            destroyedComments = comments.Count,
            destroyedJobs = jobs.Count,
            acceptRatifiedLoss
        });

        db.CandidateVersions.RemoveRange(candidates);
        db.Comments.RemoveRange(comments);
        db.Jobs.RemoveRange(jobs);
        db.ShotVersions.RemoveRange(versions);
        db.SketchDocuments.RemoveRange(sketches);
        db.GenerationManifests.RemoveRange(manifests);
        db.FrameMarkups.RemoveRange(markups);
        db.Shots.Remove(shot);

        // Close the hole the removal leaves. Ordering is relative so a gap is
        // harmless, but contiguous positions keep drag-reorder arithmetic and
        // the board's displayed index honest.
        var remaining = await db.Shots.Where(x => x.Id != shotId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        for (var index = 0; index < remaining.Count; index++) remaining[index].SortOrder = index + 1;

        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ShotDeletionSummary>.Ok(summary);
    }

    /// <summary>
    /// Copies a shot's intent into a new slot placed directly after it.
    ///
    /// Intent only: description, action, camera, cited authorities and locked
    /// constraints carry over. Candidates, comments, sketches and approval state
    /// deliberately do not — a duplicate is a fresh slot that happens to start
    /// from the same brief, not a copy of someone else's approved work.
    /// </summary>
    public async Task<RepositoryResult<ShotSummary>> DuplicateShotAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var source = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (source is null) return RepositoryResult<ShotSummary>.NotFound();

        var existing = await db.Shots.Select(x => x.Code).ToListAsync(cancellationToken);
        var code = NextAvailableCode(source.Code, existing);
        if (code is null) return RepositoryResult<ShotSummary>.Conflict("Could not derive a free shot code from " + source.Code + ".");

        var now = timeProvider.GetUtcNow();
        var record = new ShotRecord
        {
            Id = Guid.NewGuid(),
            Code = code,
            Title = source.Title,
            Description = source.Description,
            Stage = ShotStage.Sketch.ToString(),
            Approval = ApprovalState.Working.ToString(),
            Version = 1,
            DurationFrames = source.DurationFrames,
            SortOrder = source.SortOrder,
            VisualVariant = source.VisualVariant % 9 + 1,
            ContinuityState = "Pending",
            Camera = source.Camera,
            Action = source.Action,
            ReferenceIdsJson = source.ReferenceIdsJson,
            ConstraintsJson = source.ConstraintsJson,
            UpdatedAt = now
        };

        // Land the copy immediately after its source rather than at the end.
        foreach (var later in await db.Shots.Where(x => x.SortOrder > source.SortOrder).ToListAsync(cancellationToken)) later.SortOrder += 1;
        record.SortOrder = source.SortOrder + 1;

        db.Shots.Add(record);
        db.CandidateVersions.Add(new CandidateVersionRecord
        {
            Id = Guid.NewGuid(),
            ShotId = record.Id,
            Version = 1,
            Stage = record.Stage,
            Approval = record.Approval,
            IsCurrent = true,
            CreatedAt = now
        });
        AddAudit("ShotDuplicated", "Shot", record.Id.ToString(), new { from = source.Code, to = record.Code });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ShotSummary>.Ok(MapShot(record, 0));
    }

    /// <summary>SH-020 becomes SH-021, then SH-022, and so on.</summary>
    private static string? NextAvailableCode(string code, IReadOnlyCollection<string> taken)
    {
        var digits = code.Length;
        while (digits > 0 && char.IsDigit(code[digits - 1])) digits--;
        var prefix = code[..digits];
        var numeric = code[digits..];
        if (numeric.Length == 0) { prefix = code + "-"; numeric = "1"; }
        if (!long.TryParse(numeric, out var value)) return null;
        for (var attempt = 1; attempt <= 999; attempt++)
        {
            var candidate = prefix + (value + attempt).ToString(new string('0', numeric.Length), CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    public async Task<RepositoryResult<ShotSummary>> UpdateShotAsync(Guid shotId, UpdateShotRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<ShotSummary>.NotFound();
        if (shot.UpdatedAt != request.ExpectedUpdatedAt) return RepositoryResult<ShotSummary>.Conflict("The shot changed in another session. Refresh before saving.");
        var error = await ValidateShotAsync(shot.Code, request.Title, request.Description, request.DurationFrames, request.Camera, request.Action, request.ReferenceIds, request.Constraints, cancellationToken);
        if (error is not null) return RepositoryResult<ShotSummary>.Invalid(error);
        var before = new
        {
            shot.Version,
            shot.Title,
            shot.Description,
            shot.DurationFrames,
            shot.Camera,
            shot.Action,
            shot.ReferenceIdsJson,
            shot.ConstraintsJson,
            shot.VideoFirstFrameCandidateId,
            shot.VideoLastFrameCandidateId,
            shot.ProductionVideoJobId,
            shot.ProductionVideoAssetId
        };
        var reopened = shot.Approval == ApprovalState.Ratified.ToString();
        if (reopened)
        {
            var currentCandidates = await db.CandidateVersions.Where(x => x.ShotId == shotId && x.IsCurrent).ToListAsync(cancellationToken);
            foreach (var currentCandidate in currentCandidates) { currentCandidate.IsCurrent = false; currentCandidate.SupersededAt = timeProvider.GetUtcNow(); }
            shot.Version += 1;
            shot.Stage = ShotStage.Sketch.ToString();
            shot.Approval = ApprovalState.Working.ToString();
            shot.CurrentAssetId = null;
        }
        shot.Title = request.Title.Trim();
        shot.Description = request.Description.Trim();
        shot.DurationFrames = request.DurationFrames;
        shot.Camera = request.Camera.Trim();
        shot.Action = request.Action.Trim();
        var nextReferenceIds = request.ReferenceIds.Distinct().ToArray();
        var removedReferencePins = await db.Comments
            .Where(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open" && x.ReferenceId != null && !nextReferenceIds.Contains(x.ReferenceId))
            .ToListAsync(cancellationToken);
        foreach (var pin in removedReferencePins) pin.State = "Resolved";
        shot.ReferenceIdsJson = JsonSerializer.Serialize(nextReferenceIds);
        shot.ConstraintsJson = JsonSerializer.Serialize(request.Constraints.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToArray());
        // A production take is evidence for the exact intent that created it.
        // Any intent edit invalidates that binding, even when the shot was still
        // in working review and therefore did not need to be reopened first.
        shot.ProductionVideoJobId = null;
        shot.ProductionVideoAssetId = null;
        shot.ContinuityState = "Pending";
        shot.UpdatedAt = timeProvider.GetUtcNow();
        var candidate = await db.CandidateVersions.SingleOrDefaultAsync(x => x.ShotId == shotId && x.Version == shot.Version, cancellationToken);
        if (candidate is null)
        {
            db.CandidateVersions.Add(new CandidateVersionRecord { Id = Guid.NewGuid(), ShotId = shot.Id, Version = shot.Version, Stage = shot.Stage, Approval = shot.Approval, IsCurrent = true, AssetId = shot.CurrentAssetId, CreatedAt = shot.UpdatedAt });
        }
        else { candidate.Stage = shot.Stage; candidate.Approval = shot.Approval; candidate.IsCurrent = true; candidate.AssetId = shot.CurrentAssetId; }
        AddAudit("ShotUpdated", "Shot", shot.Id.ToString(), new
        {
            before,
            after = new
            {
                shot.Version,
                shot.Title,
                shot.Description,
                shot.DurationFrames,
                shot.Camera,
                shot.Action,
                shot.ReferenceIdsJson,
                shot.ConstraintsJson,
                shot.VideoFirstFrameCandidateId,
                shot.VideoLastFrameCandidateId,
                shot.ProductionVideoJobId,
                shot.ProductionVideoAssetId
            },
            reopened,
            resolvedReferencePins = removedReferencePins.Count
        });
        await db.SaveChangesAsync(cancellationToken);
        var comments = await db.Comments.CountAsync(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open", cancellationToken);
        return RepositoryResult<ShotSummary>.Ok(MapShot(shot, comments));
    }

    public async Task<SketchDocumentSummary?> GetSketchAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var record = await db.SketchDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.ShotId == shotId, cancellationToken);
        return record is null ? null : MapSketch(record);
    }

    public async Task<IReadOnlyList<PosePresetSummary>> ListPosePresetsAsync(CancellationToken cancellationToken)
        => (await db.PosePresets.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken))
            .Select(MapPosePreset).ToArray();

    public async Task<RepositoryResult<PosePresetSummary>> CreatePosePresetAsync(CreatePosePresetRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 60) return RepositoryResult<PosePresetSummary>.Invalid("Pose names must contain 1 to 60 characters.");
        var jointsError = ValidatePoseJoints(request.Joints);
        if (jointsError is not null) return RepositoryResult<PosePresetSummary>.Invalid(jointsError);
        if (await db.PosePresets.CountAsync(cancellationToken) >= 50)
            return RepositoryResult<PosePresetSummary>.Conflict("This project already has 50 saved poses. Delete an unused pose before saving another.");
        var existingPoseNames = await db.PosePresets.AsNoTracking()
            .Select(preset => preset.Name)
            .ToListAsync(cancellationToken);
        if (existingPoseNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            return RepositoryResult<PosePresetSummary>.Conflict($"A saved pose named '{name}' already exists.");

        var now = timeProvider.GetUtcNow();
        var record = new PosePresetRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            JointsJson = JsonSerializer.Serialize(request.Joints.Select(joint => new SketchJoint(joint.Name.Trim(), joint.X, joint.Y)).ToArray(), CanonicalJson),
            CreatedAt = now
        };
        db.PosePresets.Add(record);
        AddAudit("PosePresetCreated", "PosePreset", record.Id.ToString(), new { record.Name });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return RepositoryResult<PosePresetSummary>.Conflict($"A saved pose named '{name}' already exists.");
        }
        return RepositoryResult<PosePresetSummary>.Ok(MapPosePreset(record));
    }

    public async Task<RepositoryResult<bool>> DeletePosePresetAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await db.PosePresets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (record is null) return RepositoryResult<bool>.NotFound();
        db.PosePresets.Remove(record);
        AddAudit("PosePresetDeleted", "PosePreset", record.Id.ToString(), new { record.Name });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(true);
    }

    public async Task<RepositoryResult<SketchDocumentSummary>> SaveSketchAsync(Guid shotId, SaveSketchRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Shots.AsNoTracking().AnyAsync(x => x.Id == shotId, cancellationToken))
        {
            return RepositoryResult<SketchDocumentSummary>.NotFound();
        }

        var validationError = ValidateSketch(request);
        if (validationError is not null) return RepositoryResult<SketchDocumentSummary>.Invalid(validationError);

        var now = timeProvider.GetUtcNow();
        var content = NormalizeSketch(request.Content);
        var creativeBrief = request.CreativeBrief.Trim();
        var contentJson = JsonSerializer.Serialize(content, CanonicalJson);
        var objectReferenceIds = (content.Objects ?? [])
            .SelectMany(item => new[] { item.CharacterReferenceId, item.WardrobeReferenceId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (objectReferenceIds.Length > 0)
        {
            var validReferenceIds = await db.References.AsNoTracking()
                .Where(reference => objectReferenceIds.Contains(reference.Id))
                .Select(reference => reference.Id)
                .ToListAsync(cancellationToken);
            var missingReferenceIds = objectReferenceIds.Except(validReferenceIds, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missingReferenceIds.Length > 0)
                return RepositoryResult<SketchDocumentSummary>.Invalid($"Blocking objects reference unavailable authorities: {string.Join(", ", missingReferenceIds)}.");
        }
        if (request.UnderlayAssetId is not null && !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == request.UnderlayAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken))
        {
            return RepositoryResult<SketchDocumentSummary>.Invalid("The selected underlay is not a valid image asset.");
        }
        AssetRecord? compositionAsset = null;
        if (request.CompositionAssetId is not null)
        {
            compositionAsset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CompositionAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (compositionAsset is null) return RepositoryResult<SketchDocumentSummary>.Invalid("The exported composition is not a valid image asset.");
        }
        var contentHash = Hash(JsonSerializer.Serialize(new { creativeBrief, content, request.UnderlayAssetId, request.CompositionAssetId, compositionAsset?.ContentHash }, CanonicalJson));
        var record = await db.SketchDocuments.SingleOrDefaultAsync(x => x.ShotId == shotId, cancellationToken);

        if (record is null)
        {
            if (request.ExpectedRevision != 0)
            {
                return RepositoryResult<SketchDocumentSummary>.Conflict("The sketch does not exist at the expected revision. Refresh before saving.");
            }
            record = new SketchDocumentRecord
            {
                Id = Guid.NewGuid(),
                ShotId = shotId,
                Revision = 1,
                CreativeBrief = creativeBrief,
                ContentJson = contentJson,
                UnderlayAssetId = request.UnderlayAssetId,
                CompositionAssetId = request.CompositionAssetId,
                ContentHash = contentHash,
                UpdatedAt = now
            };
            db.SketchDocuments.Add(record);
        }
        else
        {
            if (record.Revision != request.ExpectedRevision)
            {
                return RepositoryResult<SketchDocumentSummary>.Conflict($"The sketch changed from revision {request.ExpectedRevision} to {record.Revision}. Refresh before saving.");
            }
            if (record.ContentHash == contentHash)
            {
                return RepositoryResult<SketchDocumentSummary>.Ok(MapSketch(record));
            }
            record.Revision += 1;
            record.CreativeBrief = creativeBrief;
            record.ContentJson = contentJson;
            record.UnderlayAssetId = request.UnderlayAssetId;
            record.CompositionAssetId = request.CompositionAssetId;
            record.ContentHash = contentHash;
            record.UpdatedAt = now;
        }

        AddAudit("SketchSaved", "Shot", shotId.ToString(), new { record.Id, record.Revision, record.ContentHash });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return RepositoryResult<SketchDocumentSummary>.Conflict("The sketch changed while it was being saved. Refresh and retry.");
        }
        catch (DbUpdateException) when (request.ExpectedRevision == 0)
        {
            return RepositoryResult<SketchDocumentSummary>.Conflict("A sketch was created by another session. Refresh before saving.");
        }
        return RepositoryResult<SketchDocumentSummary>.Ok(MapSketch(record));
    }

    public async Task<FrameMarkupSummary?> GetFrameMarkupAsync(Guid shotId, int version, CancellationToken cancellationToken)
    {
        var record = await db.FrameMarkups.AsNoTracking().SingleOrDefaultAsync(x => x.ShotId == shotId && x.Version == version, cancellationToken);
        return record is null ? null : MapMarkup(record);
    }

    public async Task<RepositoryResult<FrameMarkupSummary>> SaveFrameMarkupAsync(Guid shotId, int version, SaveFrameMarkupRequest request, CancellationToken cancellationToken)
    {
        if (!await db.CandidateVersions.AsNoTracking().AnyAsync(x => x.ShotId == shotId && x.Version == version, cancellationToken))
            return RepositoryResult<FrameMarkupSummary>.NotFound();
        var validationError = ValidateStrokes(request.ExpectedRevision, request.Strokes, 500, 25_000, "markup");
        if (validationError is not null) return RepositoryResult<FrameMarkupSummary>.Invalid(validationError);

        var strokes = request.Strokes.Select(stroke => new SketchStroke(stroke.Id.Trim(), stroke.Points.ToArray(), stroke.Color.ToLowerInvariant(), stroke.Width)).ToArray();
        var strokesJson = JsonSerializer.Serialize(strokes, CanonicalJson);
        var contentHash = Hash(strokesJson);
        var record = await db.FrameMarkups.SingleOrDefaultAsync(x => x.ShotId == shotId && x.Version == version, cancellationToken);
        if (record is null)
        {
            if (request.ExpectedRevision != 0) return RepositoryResult<FrameMarkupSummary>.Conflict("The markup does not exist at the expected revision. Refresh before saving.");
            record = new FrameMarkupRecord { Id = Guid.NewGuid(), ShotId = shotId, Version = version, Revision = 1, StrokesJson = strokesJson, ContentHash = contentHash, UpdatedAt = timeProvider.GetUtcNow() };
            db.FrameMarkups.Add(record);
        }
        else
        {
            if (record.Revision != request.ExpectedRevision) return RepositoryResult<FrameMarkupSummary>.Conflict($"The markup changed from revision {request.ExpectedRevision} to {record.Revision}. Refresh before saving.");
            if (record.ContentHash == contentHash) return RepositoryResult<FrameMarkupSummary>.Ok(MapMarkup(record));
            record.Revision += 1;
            record.StrokesJson = strokesJson;
            record.ContentHash = contentHash;
            record.UpdatedAt = timeProvider.GetUtcNow();
        }
        AddAudit("FrameMarkupSaved", "ShotVersion", $"{shotId}:v{version}", new { record.Id, record.Revision, record.ContentHash });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RepositoryResult<FrameMarkupSummary>.Conflict("The markup changed while it was being saved. Refresh and retry."); }
        catch (DbUpdateException) when (request.ExpectedRevision == 0) { return RepositoryResult<FrameMarkupSummary>.Conflict("Markup was created by another session. Refresh before saving."); }
        return RepositoryResult<FrameMarkupSummary>.Ok(MapMarkup(record));
    }

    public async Task<IReadOnlyList<GenerationManifestSummary>> GetManifestsAsync(Guid shotId, CancellationToken cancellationToken)
        => (await db.GenerationManifests.AsNoTracking().Where(x => x.ShotId == shotId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .Select(MapManifest)
            .ToArray();

    /// <summary>
    /// Saves the shot's intended motion endpoints before a video manifest exists.
    /// Candidate images remain immutable; this only changes which two revisions
    /// the next H3 packet will offer as its start and optional landing frame.
    /// </summary>
    public async Task<RepositoryResult<ShotSummary>> UpdateVideoEndpointsAsync(Guid shotId, UpdateVideoEndpointsRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<ShotSummary>.NotFound();
        if (shot.UpdatedAt != request.ExpectedUpdatedAt)
            return RepositoryResult<ShotSummary>.Conflict("The shot changed in another session. Refresh before changing video endpoints.");

        var requestedIds = new[] { request.FirstFrameCandidateId, request.LastFrameCandidateId }
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var candidates = requestedIds.Length == 0
            ? []
            : await db.CandidateVersions.AsNoTracking()
                .Where(x => x.ShotId == shotId && requestedIds.Contains(x.Id))
                .ToArrayAsync(cancellationToken);
        if (candidates.Length != requestedIds.Length || candidates.Any(x => x.AssetId is null))
            return RepositoryResult<ShotSummary>.Invalid("Every video endpoint must be an image revision attached to this shot.");

        var first = request.FirstFrameCandidateId is null ? null : candidates.Single(x => x.Id == request.FirstFrameCandidateId);
        var last = request.LastFrameCandidateId is null ? null : candidates.Single(x => x.Id == request.LastFrameCandidateId);
        var firstAssetId = first?.AssetId ?? shot.CurrentAssetId;
        if (last is not null && last.AssetId == firstAssetId)
            return RepositoryResult<ShotSummary>.Conflict("Start and Last Frame must use different image revisions.");

        var endpointsChanged = shot.VideoFirstFrameCandidateId != request.FirstFrameCandidateId ||
            shot.VideoLastFrameCandidateId != request.LastFrameCandidateId;
        if (!endpointsChanged)
        {
            var unchangedComments = await db.Comments.CountAsync(
                x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open",
                cancellationToken);
            return RepositoryResult<ShotSummary>.Ok(MapShot(shot, unchangedComments));
        }

        shot.VideoFirstFrameCandidateId = request.FirstFrameCandidateId;
        shot.VideoLastFrameCandidateId = request.LastFrameCandidateId;
        // Changing either motion anchor invalidates the previously selected
        // production take. The media remains in history; it simply cannot be
        // mistaken for the reviewed delivery for these new endpoints.
        shot.ProductionVideoJobId = null;
        shot.ProductionVideoAssetId = null;
        var reopenedRatifiedVideo = shot.Stage == nameof(ShotStage.Video) &&
            shot.Approval == nameof(ApprovalState.Ratified);
        if (reopenedRatifiedVideo)
        {
            shot.Approval = nameof(ApprovalState.Working);
            shot.ContinuityState = "Review";
            var currentCandidate = await db.CandidateVersions.SingleOrDefaultAsync(
                candidate => candidate.ShotId == shot.Id && candidate.IsCurrent,
                cancellationToken);
            if (currentCandidate is not null)
                currentCandidate.Approval = nameof(ApprovalState.Working);
        }
        shot.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("VideoEndpointsUpdated", "Shot", shot.Id.ToString(), new
        {
            firstFrameCandidateId = shot.VideoFirstFrameCandidateId,
            lastFrameCandidateId = shot.VideoLastFrameCandidateId,
            reopenedRatifiedVideo
        });
        await db.SaveChangesAsync(cancellationToken);
        var comments = await db.Comments.CountAsync(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open", cancellationToken);
        return RepositoryResult<ShotSummary>.Ok(MapShot(shot, comments));
    }

    /// <summary>
    /// Deletes a discarded candidate. The live candidate and any ratified version
    /// are append-only evidence and are refused — reviewing is destructive only
    /// for attempts nobody accepted.
    /// </summary>
    public async Task<RepositoryResult<bool>> DeleteCandidateAsync(Guid shotId, Guid candidateId, CancellationToken cancellationToken)
    {
        var candidate = await db.CandidateVersions.SingleOrDefaultAsync(x => x.Id == candidateId && x.ShotId == shotId, cancellationToken);
        if (candidate is null) return RepositoryResult<bool>.NotFound();
        if (candidate.IsCurrent) return RepositoryResult<bool>.Conflict("The live candidate cannot be deleted. Generate or select a different one first.");
        if (string.Equals(candidate.Approval, ApprovalState.Ratified.ToString(), StringComparison.Ordinal))
            return RepositoryResult<bool>.Conflict("A ratified version is permanent evidence and cannot be deleted.");
        var endpointOwner = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (endpointOwner?.VideoFirstFrameCandidateId == candidate.Id || endpointOwner?.VideoLastFrameCandidateId == candidate.Id)
            return RepositoryResult<bool>.Conflict("This revision is a selected video endpoint. Choose a different Start or Last Frame before deleting it.");

        db.CandidateVersions.Remove(candidate);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Type = "CandidateDeleted",
            TargetType = "CandidateVersion",
            TargetId = candidate.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { shotId, candidate.Version, candidate.Stage, candidate.AssetId }),
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(true);
    }

    /// <summary>
    /// Copies an earlier image candidate forward as a new working head. The
    /// selected candidate and the previous head remain immutable history; only
    /// the new copy becomes current.
    /// </summary>
    public async Task<RepositoryResult<ShotSummary>> PromoteCandidateAsync(Guid shotId, Guid candidateId, PromoteCandidateRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<ShotSummary>.NotFound();
        if (shot.Version != request.ExpectedCurrentVersion)
            return RepositoryResult<ShotSummary>.Conflict($"The live shot changed from version {request.ExpectedCurrentVersion} to {shot.Version}. Refresh before choosing a revision.");

        var candidate = await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidateId && x.ShotId == shotId, cancellationToken);
        if (candidate is null) return RepositoryResult<ShotSummary>.NotFound();
        if (candidate.IsCurrent) return RepositoryResult<ShotSummary>.Conflict("That revision is already the live working version.");
        if (candidate.AssetId is null) return RepositoryResult<ShotSummary>.Conflict("That revision has no image to copy forward.");
        if (!await db.Assets.AsNoTracking().AnyAsync(x => x.Id == candidate.AssetId, cancellationToken))
            return RepositoryResult<ShotSummary>.Conflict("That revision's image asset is no longer available.");

        var now = timeProvider.GetUtcNow();
        var currentCandidates = await db.CandidateVersions.Where(x => x.ShotId == shotId && x.IsCurrent).ToListAsync(cancellationToken);
        foreach (var current in currentCandidates)
        {
            current.IsCurrent = false;
            current.SupersededAt = now;
        }

        var highestVersion = await db.CandidateVersions.Where(x => x.ShotId == shotId).MaxAsync(x => (int?)x.Version, cancellationToken) ?? shot.Version;
        var newVersion = Math.Max(shot.Version, highestVersion) + 1;
        var copy = new CandidateVersionRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            Version = newVersion,
            Stage = candidate.Stage,
            Approval = ApprovalState.Working.ToString(),
            IsCurrent = true,
            AssetId = candidate.AssetId,
            SourceManifestId = candidate.SourceManifestId,
            CreatedAt = now
        };
        db.CandidateVersions.Add(copy);
        shot.Version = newVersion;
        shot.Stage = candidate.Stage;
        shot.Approval = ApprovalState.Working.ToString();
        shot.CurrentAssetId = candidate.AssetId;
        shot.ProductionVideoJobId = null;
        shot.ProductionVideoAssetId = null;
        shot.ContinuityState = "Review";
        shot.UpdatedAt = now;
        AddAudit("CandidateCopiedForward", "Shot", shotId.ToString(), new { sourceCandidateId = candidate.Id, sourceVersion = candidate.Version, newCandidateId = copy.Id, newVersion, candidate.AssetId });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return RepositoryResult<ShotSummary>.Conflict("The candidate stack changed while this revision was being chosen. Refresh and try again."); }
        return RepositoryResult<ShotSummary>.Ok(MapShot(shot, 0));
    }

    public async Task<RepositoryResult<GenerationManifestSummary>> PrepareManifestAsync(Guid shotId, PrepareGenerationManifestRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<GenerationManifestSummary>.NotFound();
        if (shot.Version != request.ExpectedShotVersion)
        {
            return RepositoryResult<GenerationManifestSummary>.Conflict($"The shot changed from version {request.ExpectedShotVersion} to {shot.Version}. Refresh before preparing a manifest.");
        }

        var sketch = await db.SketchDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.ShotId == shotId, cancellationToken);
        if (sketch is null && request.ExpectedSketchRevision != 0)
            return RepositoryResult<GenerationManifestSummary>.Conflict("The composition sketch is no longer available. Refresh before preparing a manifest.");
        if (sketch is not null && sketch.Revision != request.ExpectedSketchRevision)
        {
            return RepositoryResult<GenerationManifestSummary>.Conflict($"The sketch changed from revision {request.ExpectedSketchRevision} to {sketch.Revision}. Refresh before preparing a manifest.");
        }
        var stableShotIntent = BuildShotCreativeBrief(shot);
        var iterationDirection = request.CreativeBriefOverride?.Trim() ?? sketch?.CreativeBrief?.Trim();
        // A sketch note or revision direction is a delta, never a replacement
        // for the scene. Keep the complete shot intent in every manifest so a
        // second or third iteration cannot progressively forget the cast,
        // setting, action, or camera.
        var creativeBrief = string.IsNullOrWhiteSpace(iterationDirection)
            ? stableShotIntent
            : iterationDirection.Contains(shot.Description, StringComparison.OrdinalIgnoreCase)
                && iterationDirection.Contains(shot.Action, StringComparison.OrdinalIgnoreCase)
                ? iterationDirection
                : $"{stableShotIntent}\n\nCURRENT ITERATION DIRECTION (changes only; preserve the full shot intent above):\n{iterationDirection}";
        if (string.IsNullOrWhiteSpace(creativeBrief))
        {
            return RepositoryResult<GenerationManifestSummary>.Invalid("A creative brief is required before preparing a manifest.");
        }
        var constraints = JsonSerializer.Deserialize<string[]>(shot.ConstraintsJson) ?? [];
        creativeBrief = WorldPromptContract.Build(project) + "\n\nSHOT OR ASSET BRIEF:\n" + creativeBrief;
        var visualOverrides = constraints.Where(VisualOverrideContract.IsOverride).ToArray();
        if (visualOverrides.Length > 0)
        {
            creativeBrief += "\n\nAPPROVED SHOT-SPECIFIC VISUAL OVERRIDES:\n" +
                "These accepted differences take precedence only where they directly conflict with older scene wording or base shot rules. Preserve every other scene, action, camera, reference, and authority detail.\n" +
                string.Join("\n", visualOverrides.Select(item => $"- {item}"));
        }
        AssetRecord? compositionAsset = null;
        var compositionAssetId = request.CompositionAssetId
            ?? (request.AllowSketchCompositionFallback ? sketch?.CompositionAssetId : null);
        if (compositionAssetId is not null)
        {
            compositionAsset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == compositionAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (compositionAsset is null) return RepositoryResult<GenerationManifestSummary>.Conflict(request.CompositionAssetId is null ? "The saved composition asset is unavailable. Save the sketch again before preparing a manifest." : "The annotation guide is not an available image asset. Recreate it before preparing this change.");
        }

        var endpointRole = request.VideoEndpointRole?.Trim();
        if (endpointRole is not null && !string.Equals(endpointRole, "LastFrame", StringComparison.Ordinal))
            return RepositoryResult<GenerationManifestSummary>.Invalid("The requested video endpoint role is not supported.");
        if (endpointRole is not null)
        {
            if (request.Purpose != GenerationPurpose.Draft || compositionAsset is null || request.VideoEndpointSourceCandidateId is null)
                return RepositoryResult<GenerationManifestSummary>.Invalid("A Last Frame revision requires a source image candidate and the Draft image route.");
            var sourceCandidate = await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.VideoEndpointSourceCandidateId && x.ShotId == shotId,
                cancellationToken);
            if (sourceCandidate?.AssetId != compositionAsset.Id)
                return RepositoryResult<GenerationManifestSummary>.Conflict("The Last Frame source no longer matches the selected Start Frame. Refresh and try again.");
            creativeBrief += "\n\nLAST FRAME DERIVATION CONTRACT:\n" +
                "- Treat the attached Start Frame as the sole composition and continuity source.\n" +
                "- Preserve its visual style, camera and lens language, crop, color script, lighting logic, character identity, wardrobe, architecture, props, and all locked constraints unless the requested end-state explicitly changes one.\n" +
                "- Produce one clean later moment from the same shot, never a split screen, storyboard pair, montage, overlay, ghost, or before-and-after image.\n" +
                "- Preserve continuity, not identical pixels: rebuild pose, expression, hands, fabric, and nearby anatomy wherever the requested action requires it.\n" +
                "- Make the requested end-state clearly readable at full-frame review. Do not return a near-duplicate that merely changes texture, lighting noise, or tiny incidental details.\n" +
                "- Change only what must visibly happen by the end of the shot; maintain plausible screen direction and spatial continuity.";
        }

        // Once a real frame is the visual base, its pixels—not the old shape
        // sketch—own composition and pose. Re-appending blocker directions here
        // can contradict a requested edit (for example, "look down" versus an
        // original "facing front" sketch object) and quietly erase the change.
        var isCurrentFrameEdit = compositionAsset is not null
            && (compositionAsset.Id == shot.CurrentAssetId
                || request.MarkupRevision is not null
                || endpointRole is not null);

        var gateError = ValidatePurposeGate(shot, request.Purpose);
        if (gateError is not null) return RepositoryResult<GenerationManifestSummary>.Conflict(gateError);

        var referenceIds = (JsonSerializer.Deserialize<string[]>(shot.ReferenceIdsJson) ?? [])
            .Where(referenceId => !string.IsNullOrWhiteSpace(referenceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referenceRecords = await db.References.AsNoTracking().Where(x => referenceIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var referenceById = referenceRecords.ToDictionary(reference => reference.Id, StringComparer.OrdinalIgnoreCase);
        var missingReferences = referenceIds.Where(id => !referenceById.ContainsKey(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missingReferences.Length > 0)
            return RepositoryResult<GenerationManifestSummary>.Conflict($"The authority packet is incomplete: {string.Join(", ", missingReferences)}.");
        var sketchContent = sketch is null
            ? new SketchContent([], [], [])
            : NormalizeSketch(JsonSerializer.Deserialize<SketchContent>(sketch.ContentJson, CanonicalJson) ?? new SketchContent([], [], []));
        if (!isCurrentFrameEdit)
        {
            var blockingContract = BuildBlockingContract(sketchContent.Objects ?? [], referenceRecords);
            if (blockingContract.Length > 0) creativeBrief += "\n\n" + blockingContract;
        }
        var referencePins = await db.Comments.AsNoTracking()
            .Where(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open" && x.ReferenceId != null)
            .ToListAsync(cancellationToken);
        referencePins = referencePins.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        var pinnedVersions = referencePins
            .Where(x => x.ReferenceId is not null && x.ReferenceVersion is not null)
            .GroupBy(x => x.ReferenceId!)
            .ToDictionary(group => group.Key, group => group.Last().ReferenceVersion!.Value);
        var authorities = referenceIds
            .Select((referenceId, sourceOrder) =>
            {
                var reference = referenceById[referenceId];
                var version = pinnedVersions.GetValueOrDefault(reference.Id, reference.CurrentVersion);
                var placements = referencePins
                    .Where(pin => string.Equals(pin.ReferenceId, reference.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(pin => new AuthorityPlacementBinding(
                        pin.X,
                        pin.Y,
                        pin.Body,
                        ReferencePlacementInstruction(reference.Name, version, reference.Category, pin.X, pin.Y, pin.Body)))
                    .ToArray();
                return new AuthorityBinding(reference.Id, reference.Name, reference.Category, version, placements.Length > 0, placements, sourceOrder);
            })
            .ToArray();

        var referenceVersionRecords = await db.ReferenceVersions.AsNoTracking()
            .Where(version => referenceIds.Contains(version.ReferenceId))
            .ToListAsync(cancellationToken);
        var selectedAuthorityVersions = authorities.Select(authority =>
        {
            var version = referenceVersionRecords.Single(item => item.ReferenceId == authority.Id && item.Version == authority.Version);
            return new { Authority = authority, Version = version };
        }).ToArray();
        var authorityImageIds = selectedAuthorityVersions
            .Where(item => item.Version.ImageAssetId is not null)
            .Select(item => item.Version.ImageAssetId!.Value)
            .Distinct()
            .ToArray();
        var authorityImageAssets = await db.Assets.AsNoTracking()
            .Where(asset => authorityImageIds.Contains(asset.Id) && asset.Kind == AssetKind.Image.ToString())
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
        if (authorityImageAssets.Count != authorityImageIds.Length)
            return RepositoryResult<GenerationManifestSummary>.Conflict("The authority packet contains an unavailable image asset.");
        var frozenAuthorityInputs = selectedAuthorityVersions.Select(item => new FrozenGenerationAuthorityInput(
            item.Authority.Id,
            item.Authority.Version,
            item.Version.ContentHash,
            item.Version.Description,
            item.Version.LockedConstraint,
            item.Version.ImageAssetId is { } imageAssetId
                ? GenerationFrozenInputContract.Capture(authorityImageAssets[imageAssetId])
                : null)).ToArray();
        creativeBrief += "\n\nAPPROVED AUTHORITY SPECS:\n" + string.Join("\n", selectedAuthorityVersions.Select(item =>
            $"- {item.Authority.Name} ({item.Authority.Category}) v{item.Authority.Version}: {item.Version.Description} Locked: {item.Version.LockedConstraint}"));

        var placements = authorities
            .SelectMany(authority => (authority.Placements ?? []).Select(placement => new
            {
                referenceId = authority.Id,
                referenceVersion = authority.Version,
                referenceName = authority.Name,
                category = authority.Category,
                placement.X,
                placement.Y,
                placement.Body,
                placement.Instruction
            }))
            .ToArray();
        if (placements.Length > 0)
        {
            var repeatedRoles = placements
                .Where(pin => pin.category.Equals("Role", StringComparison.OrdinalIgnoreCase))
                .GroupBy(pin => new { pin.referenceId, pin.referenceVersion, pin.referenceName })
                .Where(group => group.Count() > 1)
                .Select(group => $"- REQUIRED CAST COUNT: Render exactly {group.Count()} distinct {group.Key.referenceName} subjects, one for each spatial anchor below. Every instance must visibly belong to the same approved role and design family: preserve the uniform, insignia, palette, equipment, and demeanor on each one. Generate visibly different people with varied facial structure, age, hair, and other natural features; do not make twins or copy the authority face. Vary pose according to each explicit note. Do not merge them into one person, omit one, substitute a different role, or turn either into a floating portrait.")
                .ToArray();
            var fullBodyRequested = placements.Any(pin => (pin.category.Equals("Character", StringComparison.OrdinalIgnoreCase) || pin.category.Equals("Role", StringComparison.OrdinalIgnoreCase))
                && Regex.IsMatch(pin.Body, "\\b(full[- ]?body|head[- ]?to[- ]?toe|standing|stands|walking|walks|slouched|pose|body)\\b", RegexOptions.IgnoreCase));
            var placementRequirements = string.Join("\n", repeatedRoles);
            if (fullBodyRequested)
                placementRequirements += (placementRequirements.Length > 0 ? "\n" : string.Empty) +
                    "- REQUIRED FRAMING: Use a wide or full-body composition. Keep every anchored character completely visible from head to toe with readable posture and no cropped limbs. Do not convert the shot into a portrait, close-up, bust, or single-subject frame.";

            creativeBrief += "\n\nREFERENCE COMPOSITION CONTRACT:\n" +
                "- Produce one continuous camera image, never a collage, montage, split screen, pasted cutout, floating portrait, double exposure, or ghosted reference layer.\n" +
                "- A coordinate is the center of the target subject or environment region. It is not a location where the reference image itself should appear.\n" +
                "- Reference backgrounds, framing, crops, poses, and unrelated people are not content for the new shot.\n" +
                "- Character authorities preserve one exact identity. Role authorities preserve shared costume, equipment, and demeanor while allowing distinct people. Pose authorities transfer blocking only. Depict one subject for every character or role anchor. An unpinned character or role authority defaults to one subject. Style authorities are global and have no spatial position.\n" +
                (placementRequirements.Length > 0 ? placementRequirements + "\n" : string.Empty) + "\n" +
                "REFERENCE PLACEMENTS (semantic anchors, not pasted-image coordinates):\n" +
                string.Join("\n", placements.Select(pin => pin.Instruction));
        }

        var createdAt = timeProvider.GetUtcNow();
        var manifestPayload = new
        {
            // v3 freezes routing facts (current-frame mode, pinned authority
            // placements, and source order) as data rather than prompt prose.
            schemaVersion = 3,
            projectId = projectScope.ProjectId,
            projectFormat = new { project.AspectRatio, deliveryWidth = project.DeliveryWidth, deliveryHeight = project.DeliveryHeight, project.ColorSpace, project.AudioSampleRate },
            shot = new { shot.Id, shot.Code, version = shot.Version, shot.Camera, shot.Action },
            sketch = sketch is null ? null : new { sketch.Id, revision = sketch.Revision, sketch.ContentHash, compositionAssetId = compositionAsset?.Id, compositionAssetHash = compositionAsset?.ContentHash },
            route = request.Route.ToString(),
            purpose = request.Purpose.ToString(),
            worldSettings = WorldPromptContract.Snapshot(project),
            creativeBrief,
            videoEndpointRole = endpointRole,
            videoEndpointSourceCandidateId = request.VideoEndpointSourceCandidateId,
            markupRevision = request.MarkupRevision,
            isCurrentFrameEdit,
            authorities,
            constraints,
            referencePlacements = placements,
            sceneObjects = sketchContent.Objects ?? [],
            frozenInputs = new FrozenGenerationInputs(
                compositionAsset is null ? null : GenerationFrozenInputContract.Capture(compositionAsset),
                null,
                frozenAuthorityInputs)
        };
        var manifestJson = JsonSerializer.Serialize(manifestPayload, CanonicalJson);
        var manifestHash = Hash(manifestJson);
        var existingManifest = await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(x => x.ManifestHash == manifestHash, cancellationToken);
        if (existingManifest is not null) return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(existingManifest));

        var record = new GenerationManifestRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            ShotCode = shot.Code,
            ShotVersion = shot.Version,
            SketchId = sketch?.Id ?? Guid.Empty,
            SketchRevision = sketch?.Revision ?? 0,
            Route = request.Route.ToString(),
            Purpose = request.Purpose.ToString(),
            State = ManifestState.Prepared.ToString(),
            CreativeBrief = creativeBrief,
            AuthoritiesJson = JsonSerializer.Serialize(authorities, CanonicalJson),
            ConstraintsJson = JsonSerializer.Serialize(constraints, CanonicalJson),
            ManifestJson = manifestJson,
            ManifestHash = manifestHash,
            ProviderCallMade = false,
            CompositionAssetId = compositionAsset?.Id,
            CompositionAssetHash = compositionAsset?.ContentHash,
            CreatedAt = createdAt
        };
        db.GenerationManifests.Add(record);
        AddAudit("GenerationManifestPrepared", "Shot", shotId.ToString(), new { record.Id, record.Route, record.Purpose, record.ManifestHash, record.ProviderCallMade, request.MarkupRevision });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(record));
    }

    public async Task<RepositoryResult<GenerationManifestSummary>> PrepareVideoManifestAsync(Guid shotId, PrepareVideoManifestRequest request, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<GenerationManifestSummary>.NotFound();
        if (shot.Version != request.ExpectedShotVersion) return RepositoryResult<GenerationManifestSummary>.Conflict("The shot changed. Refresh before preparing video.");
        if (request.Quality == VideoQuality.Max) return RepositoryResult<GenerationManifestSummary>.Invalid("Max quality is created by promoting an approved video take.");
        if (shot.CurrentAssetId is null && request.FirstFrameCandidateId is null) return RepositoryResult<GenerationManifestSummary>.Conflict("Choose an imported or generated image as the video Start Frame.");
        if (string.IsNullOrWhiteSpace(request.MotionBrief) || request.MotionBrief.Trim().Length > 8_000) return RepositoryResult<GenerationManifestSummary>.Invalid("Motion brief must contain 1 to 8,000 characters.");
        CandidateVersionRecord? firstCandidate = null;
        if (request.FirstFrameCandidateId is not null)
        {
            firstCandidate = await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.FirstFrameCandidateId && x.ShotId == shotId, cancellationToken);
            if (firstCandidate is null || firstCandidate.AssetId is null) return RepositoryResult<GenerationManifestSummary>.Invalid("The selected first frame is not an image revision attached to this shot.");
        }
        var firstFrameAssetId = firstCandidate?.AssetId ?? shot.CurrentAssetId;
        var firstFrame = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == firstFrameAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
        if (firstFrame is null) return RepositoryResult<GenerationManifestSummary>.Conflict("The selected first-frame asset is unavailable.");
        if (firstFrame.Width is not > 0 || firstFrame.Height is not > 0) return RepositoryResult<GenerationManifestSummary>.Conflict("The first frame needs readable image dimensions before video generation.");
        // Still providers do not all emit the commissioned project aspect ratio.
        // Preserve the immutable source and let the curated H3 workflow perform
        // the visible contract promised by the UI: a deterministic center crop
        // followed by scaling to the selected project proxy canvas.
        AssetRecord? lastFrame = null;
        CandidateVersionRecord? lastCandidate = null;
        if (request.LastFrameCandidateId is not null)
        {
            lastCandidate = await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.LastFrameCandidateId && x.ShotId == shotId, cancellationToken);
            if (lastCandidate is null || lastCandidate.AssetId is null) return RepositoryResult<GenerationManifestSummary>.Invalid("The selected last frame is not an image revision attached to this shot.");
            if (lastCandidate.Id == firstCandidate?.Id || lastCandidate.AssetId == firstFrame.Id) return RepositoryResult<GenerationManifestSummary>.Conflict("Choose a different shot revision for the last frame, or use motion-only generation.");
        }
        var lastFrameAssetId = lastCandidate?.AssetId;
        if (lastFrameAssetId is not null)
        {
            if (!request.ConfirmEndpointCompatibility) return RepositoryResult<GenerationManifestSummary>.Conflict("Confirm endpoint compatibility after reviewing the first and last frames, or use motion-only generation.");
            lastFrame = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lastFrameAssetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
            if (lastFrame is null) return RepositoryResult<GenerationManifestSummary>.Invalid("The selected last frame is not a valid image asset.");
            if (firstFrame.Width is not > 0 || firstFrame.Height is not > 0 || lastFrame.Width is not > 0 || lastFrame.Height is not > 0)
                return RepositoryResult<GenerationManifestSummary>.Conflict("First and last frames need readable image dimensions before interpolation.");
            // First and last anchors are normalized by the same center-crop path,
            // so differing source canvases do not create a mixed-resolution take.
        }
        var referenceIds = JsonSerializer.Deserialize<string[]>(shot.ReferenceIdsJson) ?? [];
        var referenceRecords = await db.References.AsNoTracking().Where(x => referenceIds.Contains(x.Id)).ToListAsync(cancellationToken);
        var authorities = referenceRecords.Select(reference => new AuthorityBinding(reference.Id, reference.Name, reference.Category, reference.CurrentVersion)).ToArray();
        if (authorities.Length != referenceIds.Length) return RepositoryResult<GenerationManifestSummary>.Conflict("The authority packet is incomplete.");
        var authorityVersions = await db.ReferenceVersions.AsNoTracking()
            .Where(version => referenceIds.Contains(version.ReferenceId))
            .ToListAsync(cancellationToken);
        var selectedAuthorityVersions = authorities.Select(authority =>
        {
            var version = authorityVersions.Single(item => item.ReferenceId == authority.Id && item.Version == authority.Version);
            return new { Authority = authority, Version = version };
        }).ToArray();
        var authorityImageIds = selectedAuthorityVersions
            .Where(item => item.Version.ImageAssetId is not null)
            .Select(item => item.Version.ImageAssetId!.Value)
            .Distinct()
            .ToArray();
        var authorityImageAssets = await db.Assets.AsNoTracking()
            .Where(asset => authorityImageIds.Contains(asset.Id) && asset.Kind == AssetKind.Image.ToString())
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
        if (authorityImageAssets.Count != authorityImageIds.Length)
            return RepositoryResult<GenerationManifestSummary>.Conflict("The authority packet contains an unavailable image asset.");
        var frozenAuthorityInputs = selectedAuthorityVersions.Select(item => new FrozenGenerationAuthorityInput(
            item.Authority.Id,
            item.Authority.Version,
            item.Version.ContentHash,
            item.Version.Description,
            item.Version.LockedConstraint,
            item.Version.ImageAssetId is { } imageAssetId
                ? GenerationFrozenInputContract.Capture(authorityImageAssets[imageAssetId])
                : null)).ToArray();
        var constraints = JsonSerializer.Deserialize<string[]>(shot.ConstraintsJson) ?? [];
        var motionBrief = WorldPromptContract.Build(project) + "\n\nVIDEO MOTION BRIEF:\n" + request.MotionBrief.Trim() + "\n\nVISUAL MOTION ONLY. Do not generate background music, dialogue, voices, narration, or sound effects. Preserve the selected first frame and locked world canon. A last frame is an optional compatibility-tested target, not permission to leap or ignore intermediate continuity.";
        var takeId = NormalizeTakeId(request.TakeId);
        var proxy = ProductionVideoContract.VideoCanvas(project.DeliveryWidth, project.DeliveryHeight, request.Quality);
        var seedPacket = JsonSerializer.Serialize(new { shot.Id, firstFrame.ContentHash, lastFrameHash = lastFrame?.ContentHash, motionBrief, authorities, constraints, takeId }, CanonicalJson);
        var videoSeed = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(seedPacket)), 0) & long.MaxValue;
        var payload = new { schemaVersion = 4, projectId = projectScope.ProjectId, projectFormat = new { project.AspectRatio, project.FramesPerSecond, deliveryWidth = project.DeliveryWidth, deliveryHeight = project.DeliveryHeight, project.ColorSpace, project.AudioSampleRate, fit = "cover-center-crop" }, videoCanvas = new { proxy.Width, proxy.Height }, worldSettings = WorldPromptContract.Snapshot(project), shot = new { shot.Id, shot.Code, version = shot.Version, shot.Description, shot.DurationFrames, shot.Camera, shot.Action }, firstFrame = new { firstFrame.Id, candidateId = firstCandidate?.Id, candidateVersion = firstCandidate?.Version, firstFrame.ContentHash, firstFrame.Width, firstFrame.Height }, lastFrame = lastFrame is null ? null : new { lastFrame.Id, candidateId = lastCandidate?.Id, candidateVersion = lastCandidate?.Version, lastFrame.ContentHash, lastFrame.Width, lastFrame.Height }, route = GenerationRoute.FastDraft.ToString(), purpose = GenerationPurpose.Video.ToString(), creativeBrief = motionBrief, authorities, constraints, endpointCompatibilityConfirmed = lastFrame is not null && request.ConfirmEndpointCompatibility, videoQuality = request.Quality.ToString(), videoSeed, videoTakeId = takeId, frozenInputs = new FrozenGenerationInputs(GenerationFrozenInputContract.Capture(firstFrame), lastFrame is null ? null : GenerationFrozenInputContract.Capture(lastFrame), frozenAuthorityInputs) };
        var manifestJson = JsonSerializer.Serialize(payload, CanonicalJson); var manifestHash = Hash(manifestJson);
        var existing = await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(x => x.ManifestHash == manifestHash, cancellationToken); if (existing is not null) return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(existing));
        var record = new GenerationManifestRecord { Id = Guid.NewGuid(), ShotId = shot.Id, ShotCode = shot.Code, ShotVersion = shot.Version, SketchId = Guid.Empty, SketchRevision = 0, Route = GenerationRoute.FastDraft.ToString(), Purpose = GenerationPurpose.Video.ToString(), State = ManifestState.Prepared.ToString(), CreativeBrief = motionBrief, AuthoritiesJson = JsonSerializer.Serialize(authorities, CanonicalJson), ConstraintsJson = JsonSerializer.Serialize(constraints, CanonicalJson), ManifestJson = manifestJson, ManifestHash = manifestHash, ProviderCallMade = false, CompositionAssetId = firstFrame.Id, CompositionAssetHash = firstFrame.ContentHash, LastFrameAssetId = lastFrame?.Id, LastFrameAssetHash = lastFrame?.ContentHash, CreatedAt = timeProvider.GetUtcNow() };
        db.GenerationManifests.Add(record); AddAudit("VideoManifestPrepared", "Shot", shotId.ToString(), new { record.Id, record.ManifestHash, firstFrame = firstFrame.Id, firstFrameCandidate = firstCandidate?.Id, lastFrame = lastFrame?.Id, lastFrameCandidate = lastCandidate?.Id, quality = request.Quality, videoSeed, takeId, providerCallMade = false }); await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(record));
    }

    public async Task<RepositoryResult<GenerationManifestSummary>> PrepareVideoPromotionManifestAsync(Guid shotId, Guid sourceJobId, int expectedShotVersion, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<GenerationManifestSummary>.NotFound();
        if (shot.Version != expectedShotVersion) return RepositoryResult<GenerationManifestSummary>.Conflict("The shot changed. Refresh before promoting this take.");
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceJobId && x.ShotId == shotId, cancellationToken);
        if (job is null || job.Kind != "Video" || job.State != JobState.Completed.ToString() || job.ManifestId is null || job.OutputAssetId is null)
            return RepositoryResult<GenerationManifestSummary>.Invalid("Choose a completed video take to promote.");
        var source = await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.ManifestId && x.Purpose == GenerationPurpose.Video.ToString(), cancellationToken);
        if (source is null || source.State != ManifestState.Completed.ToString()) return RepositoryResult<GenerationManifestSummary>.Conflict("The selected take no longer has a completed frozen video packet.");
        if (!string.Equals(source.ManifestHash, Hash(source.ManifestJson), StringComparison.Ordinal))
            return RepositoryResult<GenerationManifestSummary>.Conflict("The selected take's frozen packet failed its integrity check.");
        var currentCandidate = await db.CandidateVersions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ShotId == shotId && x.IsCurrent, cancellationToken);
        if (currentCandidate?.SourceManifestId != source.Id)
            return RepositoryResult<GenerationManifestSummary>.Conflict("This take no longer belongs to the current shot revision. Generate a fresh review take before promoting to Max.");
        var sourceAsset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == job.OutputAssetId && x.Kind == AssetKind.Video.ToString(), cancellationToken);
        if (sourceAsset is null) return RepositoryResult<GenerationManifestSummary>.Conflict("The selected take's video asset is unavailable.");
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);

        JsonObject? payload;
        try { payload = JsonNode.Parse(source.ManifestJson)?.AsObject(); }
        catch (JsonException) { payload = null; }
        if (payload is null) return RepositoryResult<GenerationManifestSummary>.Conflict("The selected take packet cannot be read.");
        var frozenAuthorities = JsonSerializer.Deserialize<AuthorityBinding[]>(source.AuthoritiesJson, CanonicalJson) ?? [];
        var frozenConstraints = JsonSerializer.Deserialize<string[]>(source.ConstraintsJson, CanonicalJson) ?? [];
        JsonNode? authorityNode = null;
        JsonNode? constraintNode = null;
        try
        {
            authorityNode = JsonNode.Parse(source.AuthoritiesJson);
            constraintNode = JsonNode.Parse(source.ConstraintsJson);
        }
        catch (JsonException) { /* Rejected by the consistency gate below. */ }
        var currentWorld = JsonSerializer.SerializeToNode(WorldPromptContract.Snapshot(project), CanonicalJson);
        if (!JsonNode.DeepEquals(payload["worldSettings"], currentWorld) ||
            !string.Equals(payload["creativeBrief"]?.GetValue<string>(), source.CreativeBrief, StringComparison.Ordinal) ||
            !JsonNode.DeepEquals(payload["authorities"], authorityNode) ||
            !JsonNode.DeepEquals(payload["constraints"], constraintNode))
            return RepositoryResult<GenerationManifestSummary>.Conflict("The project world settings or frozen take packet changed. Generate a fresh review take before promoting to Max.");

        var currentReferenceIds = JsonSerializer.Deserialize<string[]>(shot.ReferenceIdsJson) ?? [];
        var currentReferences = await db.References.AsNoTracking()
            .Where(reference => currentReferenceIds.Contains(reference.Id))
            .ToDictionaryAsync(reference => reference.Id, StringComparer.Ordinal, cancellationToken);
        if (currentReferences.Count != currentReferenceIds.Length || frozenAuthorities.Length != currentReferenceIds.Length ||
            currentReferenceIds.Any(id => !currentReferences.TryGetValue(id, out var reference) ||
                !frozenAuthorities.Any(binding => string.Equals(binding.Id, id, StringComparison.Ordinal) &&
                    binding.Version == reference.CurrentVersion &&
                    string.Equals(binding.Name, reference.Name, StringComparison.Ordinal) &&
                    string.Equals(binding.Category, reference.Category, StringComparison.Ordinal))))
            return RepositoryResult<GenerationManifestSummary>.Conflict("The shot's approved authority set changed. Generate a fresh review take before promoting to Max.");
        var currentConstraints = JsonSerializer.Deserialize<string[]>(shot.ConstraintsJson) ?? [];
        if (!frozenConstraints.SequenceEqual(currentConstraints, StringComparer.Ordinal))
            return RepositoryResult<GenerationManifestSummary>.Conflict("The shot constraints changed. Generate a fresh review take before promoting to Max.");
        if (payload["shot"] is not JsonObject frozenShot ||
            !string.Equals(frozenShot["description"]?.GetValue<string>(), shot.Description, StringComparison.Ordinal) ||
            frozenShot["durationFrames"]?.GetValue<int>() != shot.DurationFrames ||
            !string.Equals(frozenShot["camera"]?.GetValue<string>(), shot.Camera, StringComparison.Ordinal) ||
            !string.Equals(frozenShot["action"]?.GetValue<string>(), shot.Action, StringComparison.Ordinal))
            return RepositoryResult<GenerationManifestSummary>.Conflict("The shot description, timing, camera, or action changed. Generate a fresh review take before promoting to Max.");

        var firstCandidate = shot.VideoFirstFrameCandidateId is { } firstId
            ? await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == firstId && x.ShotId == shotId, cancellationToken)
            : null;
        var lastCandidate = shot.VideoLastFrameCandidateId is { } lastId
            ? await db.CandidateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lastId && x.ShotId == shotId, cancellationToken)
            : null;
        var expectedFirstAssetId = firstCandidate?.AssetId ?? shot.CurrentAssetId;
        var expectedLastAssetId = lastCandidate?.AssetId;
        if (source.CompositionAssetId != expectedFirstAssetId || source.LastFrameAssetId != expectedLastAssetId)
            return RepositoryResult<GenerationManifestSummary>.Conflict("The selected video endpoints changed. Generate a fresh review take before promoting to Max.");

        var sourceQuality = ParseVideoQuality(payload["videoQuality"]?.GetValue<string>());
        if (sourceQuality == VideoQuality.Max) return RepositoryResult<GenerationManifestSummary>.Conflict("This take is already max quality.");
        var videoSeed = payload["videoSeed"]?.GetValue<long?>() ?? (BitConverter.ToInt64(Convert.FromHexString(source.ManifestHash[..16])) & long.MaxValue);
        var takeId = payload["videoTakeId"]?.GetValue<string>() ?? source.ManifestHash[..16];
        payload["schemaVersion"] = 4;
        payload["videoQuality"] = VideoQuality.Max.ToString();
        var maxCanvas = ProductionVideoContract.VideoCanvas(project.DeliveryWidth, project.DeliveryHeight, VideoQuality.Max);
        payload["projectFormat"] = JsonSerializer.SerializeToNode(new
        {
            project.AspectRatio,
            project.FramesPerSecond,
            deliveryWidth = project.DeliveryWidth,
            deliveryHeight = project.DeliveryHeight,
            project.ColorSpace,
            project.AudioSampleRate,
            fit = "cover-center-crop"
        }, CanonicalJson);
        payload["videoCanvas"] = JsonSerializer.SerializeToNode(new { maxCanvas.Width, maxCanvas.Height }, CanonicalJson);
        payload["videoSeed"] = videoSeed;
        payload["videoTakeId"] = takeId;
        payload["promotedFromJobId"] = job.Id;
        payload["promotionSourceAssetId"] = sourceAsset.Id;
        if (payload["shot"] is JsonObject shotNode) shotNode["version"] = shot.Version;
        var manifestJson = payload.ToJsonString(CanonicalJson);
        var manifestHash = Hash(manifestJson);
        var existing = await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(x => x.ManifestHash == manifestHash, cancellationToken);
        if (existing is not null) return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(existing));
        var record = new GenerationManifestRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            ShotCode = shot.Code,
            ShotVersion = shot.Version,
            SketchId = Guid.Empty,
            SketchRevision = 0,
            Route = source.Route,
            Purpose = source.Purpose,
            State = ManifestState.Prepared.ToString(),
            CreativeBrief = source.CreativeBrief,
            AuthoritiesJson = source.AuthoritiesJson,
            ConstraintsJson = source.ConstraintsJson,
            ManifestJson = manifestJson,
            ManifestHash = manifestHash,
            ProviderCallMade = false,
            CompositionAssetId = source.CompositionAssetId,
            CompositionAssetHash = source.CompositionAssetHash,
            LastFrameAssetId = source.LastFrameAssetId,
            LastFrameAssetHash = source.LastFrameAssetHash,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.GenerationManifests.Add(record);
        AddAudit("VideoTakePromotedToMax", "Shot", shotId.ToString(), new { record.Id, sourceJobId, sourceQuality, videoSeed, takeId, sourceAsset = sourceAsset.Id });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<GenerationManifestSummary>.Ok(MapManifest(record));
    }

    public async Task<CommentSummary?> AddCommentAsync(Guid shotId, CreateCommentRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null || string.IsNullOrWhiteSpace(request.Body)) return null;
        ReferenceRecord? reference = null;
        if (!string.IsNullOrWhiteSpace(request.ReferenceId))
        {
            var attached = JsonSerializer.Deserialize<string[]>(shot.ReferenceIdsJson) ?? [];
            if (!attached.Contains(request.ReferenceId, StringComparer.Ordinal)) return null;
            reference = await db.References.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReferenceId, cancellationToken);
            if (reference is null) return null;
        }

        var comment = new CommentRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shotId,
            Version = shot.Version,
            X = Math.Clamp(request.X, 0, 1),
            Y = Math.Clamp(request.Y, 0, 1),
            Body = request.Body.Trim(),
            State = "Open",
            CreatedAt = timeProvider.GetUtcNow(),
            ReferenceId = reference?.Id,
            ReferenceVersion = reference?.CurrentVersion
        };
        db.Comments.Add(comment);
        AddAudit(reference is null ? "CommentAdded" : "ReferencePinned", "Shot", shotId.ToString(), new { comment.Id, comment.Version, comment.ReferenceId, comment.ReferenceVersion });
        await db.SaveChangesAsync(cancellationToken);
        return MapComment(comment);
    }

    public async Task<CommentSummary?> MoveCommentAsync(Guid commentId, MoveCommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(x => x.Id == commentId, cancellationToken);
        if (comment is null) return null;
        comment.X = Math.Clamp(request.X, 0, 1);
        comment.Y = Math.Clamp(request.Y, 0, 1);
        AddAudit("CommentMoved", "Comment", commentId.ToString(), new { comment.ShotId, comment.Version, comment.X, comment.Y });
        await db.SaveChangesAsync(cancellationToken);
        return MapComment(comment);
    }

    public async Task<(ShotSummary? Shot, string? Error)> RatifyAsync(Guid shotId, RatifyRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return (null, null);
        if (shot.Version != request.ExpectedVersion) return (null, "The shot version changed. Refresh before ratifying.");
        var openComments = await db.Comments.CountAsync(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open", cancellationToken);
        if (openComments > 0) return (null, $"Resolve {openComments} open feedback note{(openComments == 1 ? "" : "s")} before ratifying this version.");
        VideoMediaMetadata? verifiedVideoMetadata = null;
        if (shot.Stage == ShotStage.Video.ToString())
        {
            if (shot.ProductionVideoJobId is null || shot.ProductionVideoAssetId is null)
                return (null, "Promote a reviewed take to Max quality before ratifying this video.");
            var productionJob = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == shot.ProductionVideoJobId && x.ShotId == shot.Id &&
                    x.State == JobState.Completed.ToString() && x.OutputAssetId == shot.ProductionVideoAssetId,
                cancellationToken);
            var productionManifest = productionJob?.ManifestId is { } manifestId
                ? await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == manifestId, cancellationToken)
                : null;
            var productionAsset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == shot.ProductionVideoAssetId && x.Kind == AssetKind.Video.ToString(),
                cancellationToken);
            var project = await db.Projects.AsNoTracking().SingleAsync(
                x => x.Id == projectScope.ProjectId,
                cancellationToken);
            if (productionJob is null || productionManifest is null || productionAsset is null ||
                !ProductionVideoContract.IsMaxForProject(productionManifest, project, shot))
                return (null, "The selected production video is not a completed Max-quality take for the current project delivery format. Promote and review a matching take before ratifying.");
            var mediaValidation = await videoMediaProbe.ValidateAsync(
                assets.ResolveContentPath(productionAsset),
                new VideoMediaExpectation(project.DeliveryWidth, project.DeliveryHeight, project.FramesPerSecond, shot.DurationFrames, project.ColorSpace),
                cancellationToken);
            if (!mediaValidation.IsValid)
                return (null, $"The selected production video's encoded stream does not match the current project delivery format. {mediaValidation.Detail}");
            verifiedVideoMetadata = mediaValidation.Metadata;
        }
        shot.Approval = "Ratified";
        shot.ContinuityState = "Clear";
        shot.UpdatedAt = timeProvider.GetUtcNow();
        var archived = await db.ShotVersions.AnyAsync(x => x.ShotId == shotId && x.Version == shot.Version, cancellationToken);
        if (!archived) db.ShotVersions.Add(StudioDatabaseInitializer.CreateAuthority(shot, shot.UpdatedAt));
        var candidate = await db.CandidateVersions.SingleOrDefaultAsync(x => x.ShotId == shotId && x.Version == shot.Version, cancellationToken);
        if (candidate is null) db.CandidateVersions.Add(new CandidateVersionRecord { Id = Guid.NewGuid(), ShotId = shot.Id, Version = shot.Version, Stage = shot.Stage, Approval = shot.Approval, IsCurrent = true, AssetId = shot.CurrentAssetId, CreatedAt = shot.UpdatedAt });
        else { candidate.Approval = ApprovalState.Ratified.ToString(); candidate.IsCurrent = true; }
        AddAudit("ShotRatified", "Shot", shotId.ToString(), new
        {
            shot.Version,
            request.Reason,
            shot.ProductionVideoJobId,
            shot.ProductionVideoAssetId,
            verifiedVideoMetadata
        });
        await db.SaveChangesAsync(cancellationToken);
        return (MapShot(shot, 0), null);
    }

    public async Task<CommentSummary?> ResolveCommentAsync(Guid commentId, CancellationToken cancellationToken)
    {
        var comment = await db.Comments.SingleOrDefaultAsync(x => x.Id == commentId, cancellationToken);
        if (comment is null) return null;
        comment.State = "Resolved";
        AddAudit("CommentResolved", "Comment", commentId.ToString(), new { comment.ShotId, comment.Version });
        await db.SaveChangesAsync(cancellationToken);
        return MapComment(comment);
    }

    public async Task<IReadOnlyList<AssetReviewNoteSummary>> GetAssetReviewNotesAsync(Guid assetId, CancellationToken cancellationToken)
        => (await db.AssetReviewNotes.AsNoTracking()
            .Where(x => x.AssetId == assetId)
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .Select(MapAssetReviewNote)
            .ToArray();

    public async Task<RepositoryResult<AssetReviewNoteSummary>> AddAssetReviewNoteAsync(
        Guid assetId,
        CreateAssetReviewNoteRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.Assets.AsNoTracking().AnyAsync(x => x.Id == assetId && x.Kind == AssetKind.Image.ToString(), cancellationToken))
            return RepositoryResult<AssetReviewNoteSummary>.NotFound();
        if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Trim().Length > 2_000)
            return RepositoryResult<AssetReviewNoteSummary>.Invalid("Review notes must contain 1 to 2,000 characters.");
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
            return RepositoryResult<AssetReviewNoteSummary>.Invalid("Review-note coordinates must be finite numbers.");

        var note = new AssetReviewNoteRecord
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            X = Math.Clamp(request.X, 0, 1),
            Y = Math.Clamp(request.Y, 0, 1),
            Body = request.Body.Trim(),
            State = "Open",
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.AssetReviewNotes.Add(note);
        AddAudit("AssetReviewNoteAdded", "Asset", assetId.ToString(), new { note.Id, note.X, note.Y });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetReviewNoteSummary>.Ok(MapAssetReviewNote(note));
    }

    public async Task<RepositoryResult<AssetReviewNoteSummary>> MoveAssetReviewNoteAsync(
        Guid noteId,
        MoveCommentRequest request,
        CancellationToken cancellationToken)
    {
        var note = await db.AssetReviewNotes.SingleOrDefaultAsync(x => x.Id == noteId, cancellationToken);
        if (note is null) return RepositoryResult<AssetReviewNoteSummary>.NotFound();
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
            return RepositoryResult<AssetReviewNoteSummary>.Invalid("Review-note coordinates must be finite numbers.");
        note.X = Math.Clamp(request.X, 0, 1);
        note.Y = Math.Clamp(request.Y, 0, 1);
        AddAudit("AssetReviewNoteMoved", "AssetReviewNote", note.Id.ToString(), new { note.AssetId, note.X, note.Y });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetReviewNoteSummary>.Ok(MapAssetReviewNote(note));
    }

    public async Task<RepositoryResult<AssetReviewNoteSummary>> ResolveAssetReviewNoteAsync(Guid noteId, CancellationToken cancellationToken)
    {
        var note = await db.AssetReviewNotes.SingleOrDefaultAsync(x => x.Id == noteId, cancellationToken);
        if (note is null) return RepositoryResult<AssetReviewNoteSummary>.NotFound();
        note.State = "Resolved";
        AddAudit("AssetReviewNoteResolved", "AssetReviewNote", note.Id.ToString(), new { note.AssetId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<AssetReviewNoteSummary>.Ok(MapAssetReviewNote(note));
    }

    public async Task<IReadOnlyList<ShotVersionSummary>> GetVersionsAsync(Guid shotId, CancellationToken cancellationToken)
        => (await db.ShotVersions.AsNoTracking().Where(x => x.ShotId == shotId).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.Version)
            .Select(x => new ShotVersionSummary(
                x.Id,
                x.ShotId,
                x.Version,
                Enum.Parse<ShotStage>(x.Stage),
                Enum.Parse<ApprovalState>(x.Approval),
                x.VisualVariant,
                x.ManifestHash,
                x.RatifiedAt,
                x.AssetId,
                x.AssetId is null ? null : $"/api/assets/{x.AssetId}/content",
                x.ProductionVideoJobId,
                x.ProductionVideoAssetId,
                x.ProductionVideoAssetId is null ? null : $"/api/assets/{x.ProductionVideoAssetId}/content",
                x.Code,
                x.Title,
                x.Description,
                x.DurationFrames,
                x.Camera,
                x.Action,
                x.VideoFirstFrameCandidateId,
                x.VideoLastFrameCandidateId))
            .ToArray();

    public async Task<IReadOnlyList<CandidateVersionSummary>> GetCandidatesAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var candidates = await db.CandidateVersions.AsNoTracking().Where(x => x.ShotId == shotId).OrderByDescending(x => x.Version).ToListAsync(cancellationToken);
        var assetIds = candidates.Where(x => x.AssetId is not null).Select(x => x.AssetId!.Value).Distinct().ToArray();
        var assets = await db.Assets.AsNoTracking().Where(x => assetIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        return candidates.Select(x =>
        {
            var asset = x.AssetId is not null && assets.TryGetValue(x.AssetId.Value, out var found) ? found : null;
            return new CandidateVersionSummary(x.Id, x.ShotId, x.Version, Enum.Parse<ShotStage>(x.Stage), Enum.Parse<ApprovalState>(x.Approval), x.IsCurrent,
                x.AssetId, x.AssetId is null ? null : $"/api/assets/{x.AssetId}/content", x.SourceManifestId, x.CreatedAt, x.SupersededAt, asset?.Width, asset?.Height);
        }).ToArray();
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

    private static string? ValidateSketch(SaveSketchRequest request)
    {
        if (request.ExpectedRevision < 0) return "Expected revision cannot be negative.";
        if (request.CreativeBrief is null || request.CreativeBrief.Trim().Length > 8_000) return "Creative brief must be 8,000 characters or fewer.";
        if (request.Content is null || request.Content.Strokes is null || request.Content.Labels is null) return "Sketch content is required.";
        if (request.Content.Strokes.Count > 1_000) return "A sketch can contain at most 1,000 strokes.";
        if (request.Content.Labels.Count > 250) return "A sketch can contain at most 250 text labels.";
        if (request.Content.Strokes.Sum(stroke => stroke?.Points?.Count ?? 0) > 50_000) return "A sketch can contain at most 50,000 points.";
        foreach (var stroke in request.Content.Strokes)
        {
            if (stroke is null || string.IsNullOrWhiteSpace(stroke.Id) || stroke.Id.Length > 100) return "Every stroke needs a valid id.";
            if (stroke.Points is null || stroke.Points.Count is < 1 or > 10_000) return "Every stroke must contain between 1 and 10,000 points.";
            if (stroke.Color is null || !HexColor.IsMatch(stroke.Color)) return "Stroke colors must use six-digit hex notation.";
            if (!double.IsFinite(stroke.Width) || stroke.Width is < 1 or > 64) return "Stroke width must be between 1 and 64.";
            if (stroke.Points.Any(point => point is null || !IsUnit(point.X) || !IsUnit(point.Y) || !IsUnit(point.Pressure))) return "Sketch points must use finite normalized coordinates and pressure.";
        }
        foreach (var label in request.Content.Labels)
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Id) || label.Id.Length > 100) return "Every text label needs a valid id.";
            if (string.IsNullOrWhiteSpace(label.Text) || label.Text.Trim().Length > 500) return "Text labels must contain 1 to 500 characters.";
            if (!IsUnit(label.X) || !IsUnit(label.Y)) return "Text labels must use finite normalized coordinates.";
        }
        var objects = request.Content.Objects ?? [];
        if (objects.Count > 100) return "A sketch can contain at most 100 blocking objects.";
        foreach (var item in objects)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 100) return "Every blocking object needs a valid id.";
            if (!SketchObjectKinds.Contains(item.Kind)) return $"Unsupported blocking object kind: {item.Kind}.";
            if (!IsUnit(item.X) || !IsUnit(item.Y)) return "Blocking objects must use finite normalized positions.";
            if (!double.IsFinite(item.Width) || item.Width is < .02 or > 1 || !double.IsFinite(item.Height) || item.Height is < .02 or > 1) return "Blocking object dimensions must be between 0.02 and 1.";
            if (!double.IsFinite(item.Rotation) || item.Rotation is < -180 or > 180) return "Blocking object rotation must be between -180 and 180 degrees.";
            if (item.Color is null || !HexColor.IsMatch(item.Color)) return "Blocking object colors must use six-digit hex notation.";
            if (item.Label is null || item.Label.Trim().Length > 100) return "Blocking object labels must be 100 characters or fewer.";
            if (item.CharacterReferenceId?.Length > 100 || item.WardrobeReferenceId?.Length > 100) return "Blocking object authority ids must be 100 characters or fewer.";
            if (item.Kind == "Human")
            {
                if (!HumanPoses.Contains(item.Pose)) return $"Unsupported human pose: {item.Pose}.";
                if (!HumanFacings.Contains(item.Facing)) return $"Unsupported human facing: {item.Facing}.";
                if (!HumanBuilds.Contains(item.Build)) return $"Unsupported human build: {item.Build}.";
                if (!IdentityModes.Contains(item.IdentityMode)) return $"Unsupported human identity mode: {item.IdentityMode}.";
                if (item.IdentityMode == "Exact character" && string.IsNullOrWhiteSpace(item.CharacterReferenceId)) return "An exact-character blocker requires a character authority.";
                if (item.Joints is null || item.Joints.Count is < 1 or > 24) return "A human blocker must contain between 1 and 24 joints.";
                if (item.Joints.Select(joint => joint.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Joints.Count) return "Human joint names must be unique.";
                foreach (var joint in item.Joints)
                {
                    if (joint is null || string.IsNullOrWhiteSpace(joint.Name) || joint.Name.Length > 50) return "Every human joint needs a valid name.";
                    if (!IsUnit(joint.X) || !IsUnit(joint.Y)) return "Human joints must use finite normalized coordinates.";
                }
            }
            else if (item.Joints is null || item.Joints.Count > 0) return "Only human blocking objects may contain joints.";
        }
        return null;
    }

    private static string? ValidatePoseJoints(IReadOnlyList<SketchJoint>? joints)
    {
        if (joints is null || joints.Count is < 1 or > 24) return "A saved pose must contain between 1 and 24 joints.";
        if (joints.Any(joint => joint is null)) return "Every saved pose joint needs a valid name.";
        if (joints.Select(joint => joint.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != joints.Count) return "Saved pose joint names must be unique.";
        foreach (var joint in joints)
        {
            if (joint is null || string.IsNullOrWhiteSpace(joint.Name) || joint.Name.Length > 50) return "Every saved pose joint needs a valid name.";
            if (!IsUnit(joint.X) || !IsUnit(joint.Y)) return "Saved pose joints must use finite normalized coordinates.";
        }
        return null;
    }

    private static string? ValidateStrokes(int expectedRevision, IReadOnlyList<SketchStroke>? strokes, int maxStrokes, int maxPoints, string label)
    {
        if (expectedRevision < 0) return "Expected revision cannot be negative.";
        if (strokes is null) return $"The {label} stroke collection is required.";
        if (strokes.Count > maxStrokes) return $"A {label} can contain at most {maxStrokes:N0} strokes.";
        if (strokes.Sum(stroke => stroke?.Points?.Count ?? 0) > maxPoints) return $"A {label} can contain at most {maxPoints:N0} points.";
        foreach (var stroke in strokes)
        {
            if (stroke is null || string.IsNullOrWhiteSpace(stroke.Id) || stroke.Id.Length > 100) return "Every stroke needs a valid id.";
            if (stroke.Points is null || stroke.Points.Count is < 1 or > 10_000) return "Every stroke must contain between 1 and 10,000 points.";
            if (stroke.Color is null || !HexColor.IsMatch(stroke.Color)) return "Stroke colors must use six-digit hex notation.";
            if (!double.IsFinite(stroke.Width) || stroke.Width is < 1 or > 64) return "Stroke width must be between 1 and 64.";
            if (stroke.Points.Any(point => point is null || !IsUnit(point.X) || !IsUnit(point.Y) || !IsUnit(point.Pressure))) return "Stroke points must use finite normalized coordinates and pressure.";
        }
        return null;
    }

    private async Task<IReadOnlyList<ReferenceSummary>> GetReferenceSummariesAsync(CancellationToken cancellationToken)
    {
        var references = (await db.References.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .ToList();
        var versions = await db.ReferenceVersions.AsNoTracking().ToListAsync(cancellationToken);
        var current = versions.ToDictionary(x => (x.ReferenceId, x.Version));
        return references.Select(reference => MapReference(reference, current[(reference.Id, reference.CurrentVersion)])).ToArray();
    }

    private static ReferenceSummary MapReference(ReferenceRecord reference, ReferenceVersionRecord version) => new(
        reference.Id, reference.Name, reference.Category, version.Description, reference.CurrentVersion,
        reference.Status, reference.Accent, version.LockedConstraint, reference.VisualVariant,
        version.ImageAssetId, version.ImageAssetId is null ? null : $"/api/assets/{version.ImageAssetId}/content");

    private static string? ValidateReference(string name, string category, string description, string constraint, string accent)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) return "Authority name must contain 1 to 100 characters.";
        if (!AllowedReferenceCategories.Contains(NormalizeCategory(category))) return "Authority category must be Character, Role, Wardrobe, Pose, Location, Architecture, Prop, Style, or World.";
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 2_000) return "Authority description must contain 1 to 2,000 characters.";
        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim().Length > 1_000) return "Locked constraint must contain 1 to 1,000 characters.";
        if (accent is null || !HexColor.IsMatch(accent)) return "Authority accent must use six-digit hex notation.";
        return null;
    }

    private async Task<string?> ValidateShotAsync(string code, string title, string description, int durationFrames, string camera, string action, IReadOnlyList<string> referenceIds, IReadOnlyList<string> constraints, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 24 || !Regex.IsMatch(code.Trim(), "^[A-Za-z0-9][A-Za-z0-9_-]*$")) return "Shot code must contain 1 to 24 letters, numbers, hyphens, or underscores.";
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 120) return "Shot title must contain 1 to 120 characters.";
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 4_000) return "Shot description must contain 1 to 4,000 characters.";
        if (durationFrames is < 1 or > 86_400) return "Shot duration must be between 1 and 86,400 frames.";
        // A shot starts from one sentence; camera and action refine it later and
        // are left out of prompts while they are empty.
        if (camera is null || camera.Trim().Length > 500) return "Camera language must contain at most 500 characters.";
        if (action is null || action.Trim().Length > 2_000) return "Shot action must contain at most 2,000 characters.";
        if (referenceIds is null || referenceIds.Count > 100) return "A shot can cite at most 100 authorities.";
        if (constraints is null || constraints.Count > 200 || constraints.Any(x => x is null || x.Trim().Length > 1_000)) return "A shot can contain at most 200 constraints of up to 1,000 characters each.";
        var existing = await db.References.AsNoTracking().Where(x => referenceIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken);
        var missing = referenceIds.Distinct().Except(existing).ToArray();
        return missing.Length > 0 ? $"Unknown authorities: {string.Join(", ", missing)}." : null;
    }

    /// <summary>
    /// The project format contract, shared by create and edit so a project can
    /// never be created in a shape that editing would reject.
    /// </summary>
    private static string? ValidateProjectContract(
        string name, string production, string sequenceCode, string sequenceName,
        int framesPerSecond, string? requestedAspectRatio, int deliveryWidth, int deliveryHeight,
        string colorSpace, int audioSampleRate,
        string visualStyle, string worldCanon, string promptDirectives, string negativeDirectives,
        out string aspectRatio)
    {
        aspectRatio = requestedAspectRatio?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160 || string.IsNullOrWhiteSpace(production) || production.Trim().Length > 160)
            return "Project and production names must contain 1 to 160 characters.";
        if (string.IsNullOrWhiteSpace(sequenceCode) || sequenceCode.Trim().Length > 40 || string.IsNullOrWhiteSpace(sequenceName) || sequenceName.Trim().Length > 160)
            return "Sequence code and name are required.";
        if (framesPerSecond is < 1 or > 120) return "Frame rate must be between 1 and 120 fps.";
        if (new[] { visualStyle, worldCanon, promptDirectives, negativeDirectives }.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 4_000))
            return "Every world setting is required and must contain at most 4,000 characters.";
        if (!Regex.IsMatch(aspectRatio, @"^\d+(?:\.\d+)?:\d+(?:\.\d+)?$")) return "Aspect ratio must use width:height notation, such as 2.39:1.";
        if (deliveryWidth is < 320 or > 7680 || deliveryHeight is < 180 or > 4320 || deliveryWidth % 2 != 0 || deliveryHeight % 2 != 0)
            return "Delivery width and height must be even numbers between 320×180 and 7680×4320.";
        if (colorSpace is null || !new[] { "Rec.709", "Display P3 D65", "Rec.2020" }.Contains(colorSpace.Trim(), StringComparer.Ordinal))
            return "Color space must be Rec.709, Display P3 D65, or Rec.2020.";
        if (audioSampleRate is not (44_100 or 48_000 or 96_000))
            return "Audio sample rate must be 44.1, 48, or 96 kHz.";
        var ratioParts = aspectRatio.Split(':');
        var namedRatio = double.Parse(ratioParts[0], CultureInfo.InvariantCulture) / double.Parse(ratioParts[1], CultureInfo.InvariantCulture);
        var deliveryRatio = (double)deliveryWidth / deliveryHeight;
        if (Math.Abs(namedRatio - deliveryRatio) / namedRatio > .01)
            return "The delivery resolution must match the project aspect ratio within 1%.";
        return null;
    }

    private static readonly HashSet<string> AllowedReferenceCategories = ["Character", "Role", "Wardrobe", "Pose", "Location", "Architecture", "Prop", "Style", "World"];
    private static string NormalizeCategory(string value) => AllowedReferenceCategories.FirstOrDefault(x => x.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value?.Trim() ?? "";
    private static string Slug(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? $"authority-{Guid.NewGuid():N}" : slug[..Math.Min(slug.Length, 80)];
    }

    private static string NormalizeTakeId(string? value)
    {
        var normalized = Regex.Replace(value?.Trim() ?? string.Empty, "[^a-zA-Z0-9_-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized[..Math.Min(normalized.Length, 80)];
    }

    private static VideoQuality ParseVideoQuality(string? value)
        => Enum.TryParse<VideoQuality>(value, true, out var quality) ? quality : VideoQuality.Low;

    private static string ReferencePlacementInstruction(string name, int? version, string category, double x, double y, string body)
    {
        var at = $"{Math.Round(x * 100)}% across / {Math.Round(y * 100)}% down";
        return category.ToLowerInvariant() switch
        {
            "style" or "world" => $"- GLOBAL {category.ToUpperInvariant()} AUTHORITY: {name} v{version}. Apply its visual rules across the whole frame; ignore the old pin coordinate and never depict or paste its reference subject. Note: {body}",
            "character" => $"- SUBJECT IDENTITY ANCHOR: center exactly one depiction of {name} v{version} near {at}. Use its reference only for facial identity, hair, proportions, skin tone, and approved marks; do not copy its background, crop, pose, or extra portrait. Note: {body}",
            "role" => $"- ROLE ANCHOR: center one person performing the {name} v{version} role near {at}. Preserve the approved uniform, equipment, insignia, and demeanor, but generate a distinct natural face rather than copying the exemplar's identity. Do not copy its background, crop, or pose. Note: {body}",
            "pose" => $"- POSE ANCHOR: place the intended subject near {at} using the blocking and body position from {name} v{version}. Transfer no face, clothing, setting, or identity from the pose exemplar. Note: {body}",
            "location" or "architecture" => $"- ENVIRONMENT ANCHOR: build {name} v{version} through the environment around {at}. Preserve its architecture and materials as one continuous scene; do not paste the authority image as a panel or backdrop. Note: {body}",
            "wardrobe" => $"- WARDROBE ASSIGNMENT near {at}: apply {name} v{version} to the intended character's body only. Do not create another person from the wardrobe reference. Note: {body}",
            "prop" => $"- PROP ANCHOR: place one approved {name} v{version} near {at}, respecting handedness, scale, and attachment rules. Do not paste the entire reference image. Note: {body}",
            _ => $"- SEMANTIC ANCHOR: use {name} ({category}) v{version} for the assigned subject or region near {at}, never as a pasted image layer. Note: {body}"
        };
    }

    private static SketchContent NormalizeSketch(SketchContent content) => new(
        content.Strokes.Select(stroke => new SketchStroke(stroke.Id.Trim(), stroke.Points.ToArray(), stroke.Color.ToLowerInvariant(), stroke.Width)).ToArray(),
        content.Labels.Select(label => new SketchLabel(label.Id.Trim(), label.X, label.Y, label.Text.Trim())).ToArray(),
        (content.Objects ?? []).Select(item => new SketchObject(
            item.Id.Trim(), item.Kind.Trim(), item.X, item.Y, item.Width, item.Height, item.Rotation,
            item.Color.ToLowerInvariant(), item.Label.Trim(), item.Pose.Trim(), item.Facing.Trim(), item.Build.Trim(), item.IdentityMode.Trim(), item.FullBody,
            string.IsNullOrWhiteSpace(item.CharacterReferenceId) ? null : item.CharacterReferenceId.Trim(),
            string.IsNullOrWhiteSpace(item.WardrobeReferenceId) ? null : item.WardrobeReferenceId.Trim(),
            (item.Joints ?? []).Select(joint => new SketchJoint(joint.Name.Trim(), joint.X, joint.Y)).ToArray())).ToArray());

    private static string BuildBlockingContract(IReadOnlyList<SketchObject> objects, IReadOnlyList<ReferenceRecord> references)
    {
        if (objects.Count == 0) return string.Empty;
        var authorityNames = references.ToDictionary(reference => reference.Id, reference => reference.Name, StringComparer.OrdinalIgnoreCase);
        var directions = objects.Select((item, index) =>
        {
            var position = $"centered at {Math.Round(item.X * 100)}% across and {Math.Round(item.Y * 100)}% down; occupies about {Math.Round(item.Width * 100)}% of frame width by {Math.Round(item.Height * 100)}% of frame height";
            if (item.Kind != "Human")
                return $"- Object {index + 1}, {item.Kind}{(item.Label.Length > 0 ? $" '{item.Label}'" : string.Empty)}: {position}; rotation {Math.Round(item.Rotation)} degrees. Preserve this object's placement, scale, and orientation.";

            var character = item.CharacterReferenceId is not null && authorityNames.TryGetValue(item.CharacterReferenceId, out var characterName) ? characterName : null;
            var wardrobe = item.WardrobeReferenceId is not null && authorityNames.TryGetValue(item.WardrobeReferenceId, out var wardrobeName) ? wardrobeName : null;
            var identity = item.IdentityMode switch
            {
                "Exact character" when character is not null => $"preserve the exact identity from {character}",
                "Anonymous" => "keep the face unreadable or anonymous",
                _ => "create a distinct original person; do not copy a face from wardrobe, role, pose, or style references"
            };
            var clothing = wardrobe is null ? "use wardrobe described by the shot" : $"use {wardrobe} for clothing, equipment, insignia, and demeanor only";
            var visibility = item.FullBody ? "REQUIRED: keep the entire figure visible from head to toe with no cropped limbs" : "the figure may be cropped only as implied by the blocker";
            return $"- Human {index + 1}{(item.Label.Length > 0 ? $" '{item.Label}'" : string.Empty)}: {position}; {item.Pose.ToLowerInvariant()} pose; facing {item.Facing.ToLowerInvariant()}; {item.Build.ToLowerInvariant()} build; {identity}; {clothing}; {visibility}. Match the articulated silhouette and limb directions in the composition guide.";
        });
        return "BLOCKING OBJECT CONTRACT:\n" +
            "- The attached composition is a spatial plan, not finished anatomy or literal flat artwork. Convert every blocker into one coherent cinematic scene.\n" +
            "- Render exactly one subject or prop for every object below. Do not omit, merge, duplicate, collage, ghost, or turn a blocker into a floating portrait.\n" +
            "- Preserve each object's position, relative scale, facing, pose, and full-body requirement. Empty space in the plan is intentional.\n" +
            string.Join("\n", directions);
    }

    // Approval records authority; it does not grant permission to experiment.
    // Any selected image revision can seed another still or a video pass. The
    // immutable ratified history remains valuable without becoming a workflow lock.
    private static string? ValidatePurposeGate(ShotRecord shot, GenerationPurpose purpose) => purpose switch
    {
        GenerationPurpose.Draft or GenerationPurpose.Final or GenerationPurpose.Video => null,
        _ => "Unsupported generation purpose."
    };

    private static string BuildShotCreativeBrief(ShotRecord shot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(shot.Description)) parts.Add(shot.Description.Trim());
        if (!string.IsNullOrWhiteSpace(shot.Action)) parts.Add($"Action: {shot.Action.Trim()}");
        if (!string.IsNullOrWhiteSpace(shot.Camera)) parts.Add($"Camera: {shot.Camera.Trim()}");
        return string.Join("\n\n", parts);
    }

    private static bool IsUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SketchDocumentSummary MapSketch(SketchDocumentRecord x) => new(
        x.Id, x.ShotId, x.Revision, x.CreativeBrief,
        NormalizeSketch(JsonSerializer.Deserialize<SketchContent>(x.ContentJson, CanonicalJson) ?? new SketchContent([], [], [])),
        x.UnderlayAssetId, x.CompositionAssetId,
        x.ContentHash, x.UpdatedAt);

    private static PosePresetSummary MapPosePreset(PosePresetRecord x) => new(
        x.Id, x.Name,
        JsonSerializer.Deserialize<SketchJoint[]>(x.JointsJson, CanonicalJson) ?? [],
        x.CreatedAt);

    private static FrameMarkupSummary MapMarkup(FrameMarkupRecord x) => new(
        x.Id, x.ShotId, x.Version, x.Revision,
        JsonSerializer.Deserialize<SketchStroke[]>(x.StrokesJson, CanonicalJson) ?? [],
        x.ContentHash, x.UpdatedAt);

    private static GenerationManifestSummary MapManifest(GenerationManifestRecord x)
    {
        VideoQuality? quality = null; long? seed = null; string? takeId = null; Guid? promotedFromJobId = null;
        if (x.Purpose == GenerationPurpose.Video.ToString())
        {
            try
            {
                using var payload = JsonDocument.Parse(x.ManifestJson);
                var root = payload.RootElement;
                if (root.TryGetProperty("videoQuality", out var qualityNode) && Enum.TryParse<VideoQuality>(qualityNode.GetString(), true, out var parsedQuality)) quality = parsedQuality;
                if (root.TryGetProperty("videoSeed", out var seedNode) && seedNode.TryGetInt64(out var parsedSeed)) seed = parsedSeed;
                if (root.TryGetProperty("videoTakeId", out var takeNode)) takeId = takeNode.GetString();
                if (root.TryGetProperty("promotedFromJobId", out var promotedNode) && Guid.TryParse(promotedNode.GetString(), out var parsedJobId)) promotedFromJobId = parsedJobId;
            }
            catch (JsonException) { /* Legacy video manifests predate quality metadata and remain Low. */ }
            quality ??= VideoQuality.Low;
        }
        return new(
            x.Id, x.ShotId, x.ShotCode, x.ShotVersion, x.SketchId, x.SketchRevision,
            Enum.Parse<GenerationRoute>(x.Route), Enum.Parse<GenerationPurpose>(x.Purpose), Enum.Parse<ManifestState>(x.State),
            x.CreativeBrief,
            JsonSerializer.Deserialize<AuthorityBinding[]>(x.AuthoritiesJson, CanonicalJson) ?? [],
            JsonSerializer.Deserialize<string[]>(x.ConstraintsJson, CanonicalJson) ?? [],
            x.ManifestHash, x.ProviderCallMade, x.CompositionAssetId, x.CompositionAssetHash, x.CreatedAt,
            x.LastFrameAssetId, x.LastFrameAssetHash, quality, seed, takeId, promotedFromJobId);
    }

    private static ProjectSummary MapProject(ProjectRecord x) => new(x.Id, x.Name, x.Production, x.SequenceCode, x.SequenceName, x.FramesPerSecond, x.AspectRatio, x.DeliveryWidth, x.DeliveryHeight, x.VisualStyle, x.WorldCanon, x.PromptDirectives, x.NegativeDirectives, x.UpdatedAt, x.ColorSpace, x.AudioSampleRate, x.IsSample);

    private static ShotSummary MapShot(ShotRecord x, int openComments) => new(
        x.Id, x.Code, x.Title, x.Description,
        Enum.Parse<ShotStage>(x.Stage), Enum.Parse<ApprovalState>(x.Approval),
        x.Version, x.DurationFrames, x.SortOrder, x.VisualVariant, openComments,
        x.ContinuityState, x.Camera, x.Action,
        JsonSerializer.Deserialize<string[]>(x.ReferenceIdsJson) ?? [],
        JsonSerializer.Deserialize<string[]>(x.ConstraintsJson) ?? [], x.UpdatedAt,
        x.CurrentAssetId, x.CurrentAssetId is null ? null : $"/api/assets/{x.CurrentAssetId}/content",
        x.VideoFirstFrameCandidateId, x.VideoLastFrameCandidateId,
        x.ProductionVideoJobId, x.ProductionVideoAssetId,
        x.ProductionVideoAssetId is null ? null : $"/api/assets/{x.ProductionVideoAssetId}/content");

    private static CommentSummary MapComment(CommentRecord x) => new(x.Id, x.ShotId, x.Version, x.X, x.Y, x.Body, x.State, x.CreatedAt, x.ReferenceId, x.ReferenceVersion);
    private static AssetReviewNoteSummary MapAssetReviewNote(AssetReviewNoteRecord x) => new(x.Id, x.AssetId, x.X, x.Y, x.Body, x.State, x.CreatedAt);
    /// <summary>
    /// One job on its own. The API has always handed out /api/jobs/{id} as the
    /// place to watch queued work; this is that place. A screen following a
    /// single long job should not have to pull the whole studio snapshot to
    /// find out how far it has got.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> JobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken);
        return job is null ? RepositoryResult<JobSummary>.NotFound() : RepositoryResult<JobSummary>.Ok(MapJob(job));
    }

    private static JobSummary MapJob(JobRecord x) => new(x.Id, x.ShotId, x.ShotCode, x.Kind, Enum.Parse<JobState>(x.State), x.Progress, x.Phase, x.Backend, x.CreatedAt, x.CompletedAt, x.Error, x.ManifestId, x.AdapterId, x.OutputAssetId, x.OutputAssetId is null ? null : $"/api/assets/{x.OutputAssetId}/content", x.ProviderRequestId, x.Attempt, x.RetryOfJobId, x.LastHeartbeatAt, x.WorkType, x.AcknowledgedAt);
}

public enum RepositoryResultKind { Ok, NotFound, Invalid, Conflict, Unavailable }

public sealed record RepositoryResult<T>(T? Value, RepositoryResultKind Kind, string? Error)
{
    internal static RepositoryResult<T> Ok(T value) => new(value, RepositoryResultKind.Ok, null);
    internal static RepositoryResult<T> NotFound() => new(default, RepositoryResultKind.NotFound, null);
    internal static RepositoryResult<T> Invalid(string error) => new(default, RepositoryResultKind.Invalid, error);
    internal static RepositoryResult<T> Conflict(string error) => new(default, RepositoryResultKind.Conflict, error);
    internal static RepositoryResult<T> Unavailable(string error) => new(default, RepositoryResultKind.Unavailable, error);
}
