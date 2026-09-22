using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

public sealed record WebMcpEnvelope(bool Ok, string Status, string Code, string Message, object? Data = null, bool Retryable = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateShotRevisionProposalRequest(
    Guid ShotId,
    int ExpectedVersion,
    string CreativeDirection,
    string Rationale,
    string DesiredMediaType,
    string[] AuthorityIds,
    Guid[] NoteIds,
    string[] PreservedConstraints,
    string ObservedStateToken,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditShotRevisionProposalRequest(
    string CreativeDirection,
    string Rationale,
    string DesiredMediaType);

public sealed class WebMcpStoryboardService(
    StudioDbContext db,
    ContinuityService continuity,
    TimeProvider timeProvider)
{
    /// <summary>Bumped whenever the director packet's shape changes.</summary>
    private const int DirectorContextVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WebMcpEnvelope> ContextAsync(Guid? selectedShotId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == db.ActiveProjectId, cancellationToken);
        var shotCount = await db.Shots.CountAsync(cancellationToken);
        var pending = await db.ShotRevisionProposals.CountAsync(x => x.State == "Pending", cancellationToken);
        var selected = selectedShotId is null ? null : await db.Shots.AsNoTracking()
            .Where(x => x.Id == selectedShotId).Select(x => new { x.Id, x.Code, x.Title, x.Version, x.UpdatedAt }).SingleOrDefaultAsync(cancellationToken);
        return Success("storyboard_context", "Active storyboard context loaded.", new
        {
            project = new { project.Id, project.Name, project.SequenceCode, project.SequenceName, project.AspectRatio, project.DeliveryWidth, project.DeliveryHeight, project.VisualStyle },
            shotCount,
            pendingProposalCount = pending,
            selectedShot = selected
        });
    }

    public async Task<WebMcpEnvelope> ListShotsAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 20);
        var total = await db.Shots.CountAsync(cancellationToken);
        var shots = await db.Shots.AsNoTracking().OrderBy(x => x.SortOrder).Skip(offset).Take(limit)
            .Select(x => new { x.Id, x.Code, x.Title, x.Description, x.Stage, x.Approval, x.Version, x.ContinuityState, x.UpdatedAt })
            .ToListAsync(cancellationToken);
        return Success("shots_listed", $"Loaded {shots.Count} of {total} shots.", new { shots, offset, limit, total });
    }

    /// <summary>
    /// A bounded search over reusable model revisions in the active project.
    ///
    /// Scene blockout proposals accept an exact model asset id, so the agent
    /// needs a read-only way to discover those ids instead of guessing them or
    /// proposing new generation before checking what the artist already owns.
    /// Archived and superseded revisions stay out of the result, while their
    /// immutable rows and bytes remain available through the ordinary library.
    /// </summary>
    public async Task<WebMcpEnvelope> SearchSceneAssetsAsync(
        string? search, int offset, int limit, CancellationToken cancellationToken)
    {
        search = (search ?? "").Trim();
        if (search.Length > 120)
            return Failure("invalid_asset_search", "Scene asset search text must be 120 characters or fewer.");

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 20);
        var query = db.Assets.AsNoTracking()
            .Where(x => x.Kind == "Model" && !x.IsArchived
                && (x.RevisionFamilyId == null || x.IsCurrentRevision));
        if (search.Length > 0)
        {
            var pattern = $"%{EscapeLike(search)}%";
            query = query.Where(x =>
                EF.Functions.Like(x.DisplayName, pattern, "\\")
                || EF.Functions.Like(x.OriginalFileName, pattern, "\\")
                || EF.Functions.Like(x.TagsJson, pattern, "\\")
                || EF.Functions.Like(x.Notes, pattern, "\\")
                || EF.Functions.Like(x.Source, pattern, "\\"));
        }

        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.OriginalFileName).ThenBy(x => x.Id)
            .Skip(offset).Take(limit)
            .Select(x => new
            {
                x.Id, x.DisplayName, x.OriginalFileName, x.ContentHash, x.TagsJson, x.Notes, x.Source,
                x.RevisionFamilyId, x.RevisionNumber, x.IsCurrentRevision,
                x.PreparationAcceptedAt, x.PreparationAcceptanceNote, x.PreparationTopologyChanged,
                x.UpdatedAt,
            })
            .ToArrayAsync(cancellationToken);
        var ids = page.Select(x => x.Id).ToArray();
        var sceneUsage = await db.SceneInstances.AsNoTracking()
            .Where(x => x.AssetId != null && ids.Contains(x.AssetId.Value))
            .GroupBy(x => x.AssetId!.Value)
            .Select(group => new { AssetId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.AssetId, x => x.Count, cancellationToken);

        var assets = page.Select(x => new
        {
            assetId = x.Id,
            name = string.IsNullOrWhiteSpace(x.DisplayName)
                ? Path.GetFileNameWithoutExtension(x.OriginalFileName)
                : x.DisplayName,
            kind = "Model",
            revisionFamilyId = x.RevisionFamilyId,
            revisionNumber = x.RevisionNumber ?? 1,
            currentRevision = x.RevisionFamilyId is null || x.IsCurrentRevision,
            x.ContentHash,
            tags = Parse<string[]>(x.TagsJson) ?? [],
            description = BoundedText(x.Notes, 300),
            source = BoundedText(x.Source, 80),
            preparation = new
            {
                decision = x.PreparationAcceptedAt is not null
                    ? "Accepted"
                    : string.IsNullOrWhiteSpace(x.PreparationAcceptanceNote) ? "NotRecorded" : "Refused",
                acceptedAt = x.PreparationAcceptedAt,
                note = BoundedText(x.PreparationAcceptanceNote, 300),
                topologyChanged = x.PreparationTopologyChanged,
            },
            sceneUsageCount = sceneUsage.GetValueOrDefault(x.Id),
            canMatchSceneBlockout = true,
            x.UpdatedAt,
        }).ToArray();

        return Success("scene_assets_listed", $"Loaded {assets.Length} of {total} reusable scene models.", new
        {
            assets,
            search,
            offset,
            limit,
            total,
            generationAuthorized = false,
            usage = "Pass one returned assetId as matchAssetId in a scene blockout proposal. Searching does not place, import, or generate anything.",
        });
    }

    public async Task<WebMcpEnvelope> ShotDetailsAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return Failure("shot_not_found", "That shot is not part of the active project.");
        var authorityIds = Parse<string[]>(shot.ReferenceIdsJson) ?? [];
        var openNotes = await db.Comments.AsNoTracking()
            .Where(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open")
            .Select(x => new { x.Id, x.Body, x.X, x.Y, x.ReferenceId, x.CreatedAt })
            .ToListAsync(cancellationToken);
        var noteIds = openNotes.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(20)
            .Select(x => new { x.Id, x.Body, x.X, x.Y, x.ReferenceId }).ToList();
        var authorityRows = await db.References.AsNoTracking().Where(x => authorityIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.Name, x.Category, Version = x.CurrentVersion,
                LockedConstraint = db.ReferenceVersions.Where(version => version.ReferenceId == x.Id && version.Version == x.CurrentVersion)
                    .Select(version => version.LockedConstraint).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var authorities = authorityRows.OrderBy(x => Array.IndexOf(authorityIds, x.Id)).ThenBy(x => x.Id).Take(20).ToList();
        return Success("shot_details", $"Loaded {shot.Code} v{shot.Version}.", new
        {
            shot = new { shot.Id, shot.Code, shot.Title, shot.Description, shot.Action, shot.Camera, shot.Stage, shot.Approval, shot.Version, shot.DurationFrames, shot.ConstraintsJson, shot.UpdatedAt },
            authorities,
            openNotes = noteIds
        });
    }

    public async Task<WebMcpEnvelope> ContinuityAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var report = await continuity.EvaluateAsync(shotId, cancellationToken);
        return report is null
            ? Failure("shot_not_found", "That shot is not part of the active project.")
            : Success("continuity_inspected", $"Continuity is {report.GateState} for {report.ShotCode}.", report);
    }

    /// <summary>
    /// The exact state the artist is looking at, as one bounded packet, plus a
    /// state token that binds it to the picture an agent can open for itself.
    ///
    /// The token is derived only from persisted state, so a browser cannot talk
    /// its own stale view into looking current, and it deliberately excludes the
    /// active tool and full-view flag: those change what the artist is doing,
    /// not which revision is on screen.
    /// </summary>
    public async Task<WebMcpEnvelope> DirectorContextAsync(
        Guid shotId, int? displayedVersion, bool archivedPreview, bool directorMode, string? tool, CancellationToken cancellationToken)
    {
        var (view, failure) = await ResolveViewAsync(shotId, displayedVersion, archivedPreview, cancellationToken);
        if (view is null) return failure!;

        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == db.ActiveProjectId, cancellationToken);
        var authorityIds = Parse<string[]>(view.Shot.ReferenceIdsJson) ?? [];
        var authorityRows = await db.References.AsNoTracking().Where(x => authorityIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.Name, x.Category, Version = x.CurrentVersion,
                LockedConstraint = db.ReferenceVersions.Where(version => version.ReferenceId == x.Id && version.Version == x.CurrentVersion)
                    .Select(version => version.LockedConstraint).FirstOrDefault()
            }).ToArrayAsync(cancellationToken);
        var authorities = authorityRows.OrderBy(x => Array.IndexOf(authorityIds, x.Id)).ThenBy(x => x.Id).Take(20).ToArray();

        return Success("director_context", $"{view.Shot.Code} v{view.DisplayedVersion} is on screen with {view.Notes.Length} open {(view.Notes.Length == 1 ? "note" : "notes")}.", new
        {
            contextVersion = DirectorContextVersion,
            stateToken = view.StateToken,
            project = new { project.Id, project.Name, project.SequenceCode, project.AspectRatio, project.DeliveryWidth, project.DeliveryHeight, project.FramesPerSecond },
            subject = new
            {
                kind = "shot", shotId = view.Shot.Id, view.Shot.Code, view.Shot.Title, view.Shot.Stage, view.Shot.Approval,
                liveVersion = view.Shot.Version, displayedVersion = view.DisplayedVersion, archived = view.Archived,
                editable = !view.Archived, view.Shot.Camera, view.Shot.DurationFrames, view.Shot.UpdatedAt
            },
            view = new { directorMode, fullView = directorMode, tool = NormalizeTool(tool) },
            visual = new
            {
                kind = view.AssetId is null ? "placeholder" : "frame",
                assetId = view.AssetId,
                contentUrl = view.AssetId is null ? null : $"/api/assets/{view.AssetId}/content",
                contentHash = view.AssetHash,
                width = view.Width, height = view.Height,
                markupRevision = view.MarkupRevision,
                describes = view.AssetId is null
                    ? "No generated frame yet. The workspace is showing a placeholder storyboard layout."
                    : $"The exact frame on screen for {view.Shot.Code} v{view.DisplayedVersion}. Note coordinates are normalized to this frame."
            },
            annotations = view.Notes.Select(note => new { note.Id, note.X, note.Y, note.Body, authorityId = note.ReferenceId, authorityVersion = note.ReferenceVersion, boundToVersion = note.Version }).ToArray(),
            constraints = Parse<string[]>(view.Shot.ConstraintsJson) ?? [],
            authorities,
            availableActions = AvailableActions(view.Archived)
        });
    }

    /// <summary>
    /// The picture that goes with a packet. It is served only while the state
    /// token still matches, so structured context and visual context can never
    /// describe two different revisions.
    /// </summary>
    public async Task<WebMcpEnvelope> DirectorObservationAsync(
        Guid shotId, int? displayedVersion, bool archivedPreview, string? stateToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateToken) || stateToken.Length != 64 || !stateToken.All(Uri.IsHexDigit))
            return Failure("invalid_state_token", "Read the director context first, then pass back its exact state token.");

        var (view, failure) = await ResolveViewAsync(shotId, displayedVersion, archivedPreview, cancellationToken);
        if (view is null) return failure!;
        if (!string.Equals(view.StateToken, stateToken, StringComparison.OrdinalIgnoreCase))
            return new WebMcpEnvelope(false, "conflict", "stale_context",
                "This view changed after that context was read. Read the director context again before acting on it.", null, true);

        return Success("frame_observation", view.AssetId is null
            ? $"{view.Shot.Code} v{view.DisplayedVersion} has no generated frame yet."
            : $"The frame on screen is {view.Shot.Code} v{view.DisplayedVersion}.", new
        {
            contextVersion = DirectorContextVersion,
            stateToken = view.StateToken,
            shotId = view.Shot.Id,
            displayedVersion = view.DisplayedVersion,
            archived = view.Archived,
            kind = view.AssetId is null ? "placeholder" : "frame",
            contentUrl = view.AssetId is null ? null : $"/api/assets/{view.AssetId}/content",
            contentHash = view.AssetHash,
            width = view.Width, height = view.Height,
            markupRevision = view.MarkupRevision,
            openNoteCount = view.Notes.Length
        });
    }

    private async Task<(DirectorView? View, WebMcpEnvelope? Failure)> ResolveViewAsync(
        Guid shotId, int? displayedVersion, bool archivedPreview, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return (null, Failure("shot_not_found", "That shot is not part of the active project."));

        // Only an archived preview can name a revision of its own. When the
        // workspace is on the live head the server resolves which revision that
        // is now, so a browser that is one refresh behind reports honest current
        // state instead of an error - and an observation carrying the older
        // token still fails as stale.
        if (archivedPreview && displayedVersion is > 0 && displayedVersion > shot.Version)
            return (null, Failure("revision_not_found", $"{shot.Code} has no revision v{displayedVersion} in the active project."));
        var archived = archivedPreview && displayedVersion is > 0 && displayedVersion != shot.Version;
        var version = archived ? displayedVersion!.Value : shot.Version;
        var assetId = shot.CurrentAssetId;
        if (archived)
        {
            // SQLite cannot order DateTimeOffset, so the per-version set is
            // ordered after materialization before choosing the newest row.
            var candidates = await db.CandidateVersions.AsNoTracking()
                .Where(x => x.ShotId == shot.Id && x.Version == version).ToArrayAsync(cancellationToken);
            var candidate = candidates.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefault();
            if (candidate is null)
                return (null, Failure("revision_not_found", $"{shot.Code} has no archived revision v{version} in the active project."));
            assetId = candidate.AssetId;
        }

        var asset = assetId is null ? null : await db.Assets.AsNoTracking()
            .Where(x => x.Id == assetId).Select(x => new { x.ContentHash, x.Width, x.Height }).SingleOrDefaultAsync(cancellationToken);
        var markupRevision = await db.FrameMarkups.AsNoTracking()
            .Where(x => x.ShotId == shot.Id && x.Version == version).Select(x => (int?)x.Revision).FirstOrDefaultAsync(cancellationToken) ?? 0;
        var openNotes = await db.Comments.AsNoTracking()
            .Where(x => x.ShotId == shot.Id && x.Version == version && x.State == "Open").ToArrayAsync(cancellationToken);
        var notes = openNotes.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(20).ToArray();

        return (new DirectorView(shot, version, archived, assetId, asset?.ContentHash, asset?.Width, asset?.Height, markupRevision, notes,
            ComputeStateToken(shot, version, archived, assetId, asset?.ContentHash, markupRevision, notes)), null);
    }

    private static string ComputeStateToken(
        ShotRecord shot, int version, bool archived, Guid? assetId, string? assetHash, int markupRevision, CommentRecord[] notes)
    {
        var builder = new StringBuilder()
            .Append("director-context-v").Append(DirectorContextVersion)
            .Append('|').Append(shot.ProjectId).Append('|').Append(shot.Id)
            .Append('|').Append(shot.Version).Append('|').Append(version).Append('|').Append(archived ? '1' : '0')
            .Append('|').Append(shot.UpdatedAt.ToUnixTimeMilliseconds())
            .Append('|').Append(assetId).Append('|').Append(assetHash)
            .Append('|').Append(markupRevision)
            .Append('|').Append(shot.Camera).Append('|').Append(shot.ConstraintsJson).Append('|').Append(shot.ReferenceIdsJson);
        foreach (var note in notes.OrderBy(x => x.Id))
            builder.Append('|').Append(note.Id)
                .Append(':').Append(note.X.ToString("F4", CultureInfo.InvariantCulture))
                .Append(':').Append(note.Y.ToString("F4", CultureInfo.InvariantCulture))
                .Append(':').Append(note.Body);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string[] AvailableActions(bool archived) => archived
        ? ["get_shot_details", "inspect_shot_continuity", "observe_current_frame"]
        : ["get_shot_details", "inspect_shot_continuity", "observe_current_frame", "propose_shot_revision"];

    private static string NormalizeTool(string? tool) => tool?.Trim().ToLowerInvariant() switch
    {
        "draw" => "draw", "comment" => "comment", _ => "select"
    };

    private sealed record DirectorView(
        ShotRecord Shot, int DisplayedVersion, bool Archived, Guid? AssetId, string? AssetHash,
        int? Width, int? Height, int MarkupRevision, CommentRecord[] Notes, string StateToken);

    public async Task<WebMcpEnvelope> ProposeAsync(CreateShotRevisionProposalRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request.CreativeDirection, request.Rationale, request.DesiredMediaType);
        if (validation is not null) return validation;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return Failure("invalid_idempotency_key", "Supply an idempotency key of 1 to 128 characters.");
        if (request.AuthorityIds.Length > 12 || request.NoteIds.Length > 20 || request.PreservedConstraints.Length > 20)
            return Failure("too_many_bindings", "A proposal can bind at most 12 authorities, 20 notes, and 20 preserved constraints.");

        var existing = await db.ShotRevisionProposals.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var same = existing.ShotId == request.ShotId && existing.BaseVersion == request.ExpectedVersion
                && existing.CreativeDirection == request.CreativeDirection.Trim();
            return same ? Success("proposal_replayed", "The existing proposal was returned without creating a duplicate.", ToSummary(existing))
                : Failure("idempotency_conflict", "That idempotency key already belongs to a different proposal.");
        }

        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == request.ShotId, cancellationToken);
        if (shot is null) return Failure("shot_not_found", "That shot is not part of the active project.");
        if (shot.Version != request.ExpectedVersion) return Stale(shot.Version);

        // A proposal has to be anchored in a view the agent actually read. Without
        // this, "propose" is a blind write dressed as collaboration.
        var (observed, observationFailure) = await ResolveViewAsync(shot.Id, null, false, cancellationToken);
        if (observed is null) return observationFailure!;
        if (!string.Equals(observed.StateToken, request.ObservedStateToken, StringComparison.OrdinalIgnoreCase))
            return new WebMcpEnvelope(false, "conflict", "stale_context",
                "This shot changed after that director context was read. Read it again before proposing.", null, true);

        // Everything a proposal promises to preserve must be a rule the shot or
        // one of its authorities really holds, so an agent cannot invent a
        // reassuring constraint that nothing enforces.
        var shotConstraints = Parse<string[]>(shot.ConstraintsJson) ?? [];
        var attachedAuthorityIds = Parse<string[]>(shot.ReferenceIdsJson) ?? [];
        var lockedConstraints = await db.References.AsNoTracking()
            .Where(x => attachedAuthorityIds.Contains(x.Id))
            .Select(x => db.ReferenceVersions.Where(version => version.ReferenceId == x.Id && version.Version == x.CurrentVersion)
                .Select(version => version.LockedConstraint).FirstOrDefault())
            .ToArrayAsync(cancellationToken);
        var preservable = new HashSet<string>(
            shotConstraints.Concat(lockedConstraints.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)),
            StringComparer.OrdinalIgnoreCase);
        var invented = request.PreservedConstraints.Select(x => x.Trim()).Where(x => !preservable.Contains(x)).ToArray();
        if (invented.Length > 0)
            return Failure("invalid_preserved_constraints", "A proposal can only promise to preserve this shot's own rules and its attached authorities' locked constraints.");

        var authorityCount = await db.References.CountAsync(x => request.AuthorityIds.Contains(x.Id), cancellationToken);
        var noteCount = await db.Comments.CountAsync(x => request.NoteIds.Contains(x.Id) && x.ShotId == shot.Id && x.Version == shot.Version, cancellationToken);
        if (authorityCount != request.AuthorityIds.Distinct().Count() || noteCount != request.NoteIds.Distinct().Count())
            return Failure("invalid_bindings", "One or more authorities or notes are outside this shot and active project.");

        var now = timeProvider.GetUtcNow();
        var proposal = new ShotRevisionProposalRecord
        {
            Id = Guid.NewGuid(), ShotId = shot.Id, BaseVersion = shot.Version, BaseUpdatedAt = shot.UpdatedAt,
            CreativeDirection = request.CreativeDirection.Trim(), Rationale = request.Rationale.Trim(),
            DesiredMediaType = NormalizeMedia(request.DesiredMediaType), AuthorityIdsJson = JsonSerializer.Serialize(request.AuthorityIds.Distinct(), JsonOptions),
            NoteIdsJson = JsonSerializer.Serialize(request.NoteIds.Distinct(), JsonOptions), State = "Pending",
            PreservedConstraintsJson = JsonSerializer.Serialize(request.PreservedConstraints.Select(x => x.Trim()).Distinct(), JsonOptions),
            ObservedStateToken = observed.StateToken,
            IdempotencyKey = request.IdempotencyKey.Trim(), CreatedAt = now, CreatedAtUnixMs = now.ToUnixTimeMilliseconds(), UpdatedAt = now
        };
        db.ShotRevisionProposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        return Success("proposal_created", "A reviewable proposal was staged. No generation job was created.", ToSummary(proposal));
    }

    public async Task<WebMcpEnvelope> ListProposalsAsync(Guid? shotId, CancellationToken cancellationToken)
    {
        var query = db.ShotRevisionProposals.AsNoTracking();
        if (shotId is not null) query = query.Where(x => x.ShotId == shotId);
        // SQLite cannot order DateTimeOffset, so the immutable UTC millisecond
        // key keeps this query bounded before materialization.
        var proposals = await query.OrderByDescending(x => x.CreatedAtUnixMs).ThenByDescending(x => x.Id).Take(30).ToArrayAsync(cancellationToken);
        return Success("proposals_listed", $"Loaded {proposals.Length} proposals.", proposals.Select(ToSummary).ToArray());
    }

    public async Task<WebMcpEnvelope> EditAsync(Guid id, EditShotRevisionProposalRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request.CreativeDirection, request.Rationale, request.DesiredMediaType);
        if (validation is not null) return validation;
        var proposal = await db.ShotRevisionProposals.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (proposal is null) return Failure("proposal_not_found", "That proposal is not part of the active project.");
        if (proposal.State != "Pending") return Failure("proposal_decided", "Accepted and rejected proposals are immutable.");
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposal.ShotId, cancellationToken);
        if (shot is null || shot.Version != proposal.BaseVersion || shot.UpdatedAt != proposal.BaseUpdatedAt)
            return Stale(shot?.Version);
        proposal.CreativeDirection = request.CreativeDirection.Trim();
        proposal.Rationale = request.Rationale.Trim();
        proposal.DesiredMediaType = NormalizeMedia(request.DesiredMediaType);
        proposal.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Success("proposal_updated", "The pending proposal was updated.", ToSummary(proposal));
    }

    public Task<WebMcpEnvelope> AcceptAsync(Guid id, CancellationToken cancellationToken) => DecideAsync(id, "Accepted", cancellationToken);
    public Task<WebMcpEnvelope> RejectAsync(Guid id, CancellationToken cancellationToken) => DecideAsync(id, "Rejected", cancellationToken);

    /// <summary>
    /// The artist pushing an accepted direction into the ordinary revision
    /// surface. This is the only step that turns a proposal into working
    /// instructions, it happens once, and it still authorizes no provider: the
    /// existing explicit Generate action remains the only thing that does.
    /// </summary>
    public async Task<WebMcpEnvelope> ApplyAsync(Guid id, CancellationToken cancellationToken)
    {
        var proposal = await db.ShotRevisionProposals.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (proposal is null) return Failure("proposal_not_found", "That proposal is not part of the active project.");
        if (proposal.State == "Rejected") return Failure("proposal_rejected", "A rejected proposal cannot be applied.");
        if (proposal.State == "Pending") return Failure("proposal_not_accepted", "Accept this direction before applying it.");

        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposal.ShotId, cancellationToken);
        if (shot is null || shot.Version != proposal.BaseVersion || shot.UpdatedAt != proposal.BaseUpdatedAt)
            return Stale(shot?.Version);

        var instructions = await InstructionsAsync(proposal, shot, cancellationToken);
        // Applying twice is a replay, never a second application. The artist can
        // reopen the revision surface as often as they like.
        if (proposal.State == "Applied")
            return Success("proposal_replayed", "This direction was already applied. Reopening the same instructions.", instructions);

        proposal.State = "Applied";
        proposal.AppliedAt = proposal.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Success("proposal_applied",
            "The direction is open in the ordinary revision surface. Nothing was generated; the artist still chooses when to render.",
            await InstructionsAsync(proposal, shot, cancellationToken));
    }

    private async Task<object> InstructionsAsync(ShotRevisionProposalRecord proposal, ShotRecord shot, CancellationToken cancellationToken)
    {
        var noteIds = Parse<Guid[]>(proposal.NoteIdsJson) ?? [];
        var targetRows = await db.Comments.AsNoTracking()
            .Where(x => noteIds.Contains(x.Id) && x.ShotId == shot.Id && x.Version == proposal.BaseVersion)
            .Select(x => new { x.Id, x.X, x.Y, x.Body, authorityId = x.ReferenceId, authorityVersion = x.ReferenceVersion })
            .ToArrayAsync(cancellationToken);
        var targets = targetRows.OrderBy(x => Array.IndexOf(noteIds, x.Id)).ThenBy(x => x.Id).Take(20).ToArray();
        return new
        {
            proposal = ToSummary(proposal),
            instructions = new
            {
                shotId = shot.Id, shot.Code, baseVersion = proposal.BaseVersion,
                direction = proposal.CreativeDirection, proposal.Rationale, proposal.DesiredMediaType,
                preservedConstraints = Parse<string[]>(proposal.PreservedConstraintsJson) ?? [],
                targetedNotes = targets,
                generationAuthorized = false,
                note = "These are working instructions for the artist's revision surface. No provider job exists until the artist starts one."
            }
        };
    }

    public async Task<WebMcpEnvelope> JobStatusAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().Where(x => x.Id == jobId)
            .Select(x => new { x.Id, x.ShotId, x.ShotCode, x.Kind, x.State, x.Progress, x.Phase, x.Backend, x.CreatedAt, x.CompletedAt, x.Error })
            .SingleOrDefaultAsync(cancellationToken);
        return job is null ? Failure("job_not_found", "That generation job is not part of the active project.")
            : Success("generation_status", $"{job.ShotCode} is {job.State} at {job.Progress}%.", job, job.State is "Queued" or "Running");
    }

    private async Task<WebMcpEnvelope> DecideAsync(Guid id, string state, CancellationToken cancellationToken)
    {
        var proposal = await db.ShotRevisionProposals.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (proposal is null) return Failure("proposal_not_found", "That proposal is not part of the active project.");
        if (proposal.State == state) return Success("proposal_replayed", $"This proposal was already {state.ToLowerInvariant()}.", ToSummary(proposal));
        if (proposal.State != "Pending") return Failure("proposal_decided", $"This proposal was already {proposal.State.ToLowerInvariant()}.");

        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposal.ShotId, cancellationToken);
        if (shot is null || shot.Version != proposal.BaseVersion || shot.UpdatedAt != proposal.BaseUpdatedAt)
            return Stale(shot?.Version);
        proposal.State = state;
        proposal.DecidedAt = proposal.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Success($"proposal_{state.ToLowerInvariant()}", state == "Accepted"
            ? "Proposal accepted as human-approved direction. No provider was called; open revision options when ready."
            : "Proposal rejected. Canonical shot state was not changed.", ToSummary(proposal));
    }

    private static WebMcpEnvelope? Validate(string direction, string rationale, string media)
    {
        if (string.IsNullOrWhiteSpace(direction) || direction.Length > 1000) return Failure("invalid_direction", "Creative direction must be 1 to 1000 characters.");
        if (rationale.Length > 600) return Failure("invalid_rationale", "Rationale must be 600 characters or fewer.");
        if (NormalizeMedia(media) is not ("Image" or "Video")) return Failure("invalid_media_type", "Desired media type must be Image or Video.");
        return null;
    }

    private static string NormalizeMedia(string value) => value.Trim().ToLowerInvariant() switch { "image" => "Image", "video" => "Video", _ => value.Trim() };
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string BoundedText(string? value, int limit)
    {
        var text = (value ?? "").Trim();
        return text.Length <= limit ? text : text[..limit];
    }
    private static T? Parse<T>(string json) { try { return JsonSerializer.Deserialize<T>(json, JsonOptions); } catch (JsonException) { return default; } }
    private static object ToSummary(ShotRevisionProposalRecord x) => new { x.Id, x.ShotId, x.BaseVersion, x.CreativeDirection, x.Rationale, x.DesiredMediaType, authorityIds = Parse<string[]>(x.AuthorityIdsJson) ?? [], noteIds = Parse<Guid[]>(x.NoteIdsJson) ?? [], preservedConstraints = Parse<string[]>(x.PreservedConstraintsJson) ?? [], x.State, x.CreatedAt, x.UpdatedAt, x.DecidedAt, x.AppliedAt };
    private static WebMcpEnvelope Success(string code, string message, object? data, bool retryable = false) => new(true, "success", code, message, data, retryable);
    private static WebMcpEnvelope Failure(string code, string message) => new(false, "error", code, message);
    private static WebMcpEnvelope Stale(int? currentVersion) => new(false, "conflict", "stale_shot", currentVersion is null ? "The source shot no longer exists." : $"The shot is now v{currentVersion}. Refresh before deciding this proposal.");
}
