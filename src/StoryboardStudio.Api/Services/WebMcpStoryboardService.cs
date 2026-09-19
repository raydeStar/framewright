using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
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

    public async Task<WebMcpEnvelope> ShotDetailsAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return Failure("shot_not_found", "That shot is not part of the active project.");
        var authorityIds = Parse<string[]>(shot.ReferenceIdsJson) ?? [];
        var noteIds = await db.Comments.AsNoTracking().Where(x => x.ShotId == shotId && x.Version == shot.Version && x.State == "Open")
            .Select(x => new { x.Id, x.Body, x.X, x.Y, x.ReferenceId }).Take(20).ToListAsync(cancellationToken);
        var authorities = await db.References.AsNoTracking().Where(x => authorityIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id, x.Name, x.Category, Version = x.CurrentVersion,
                LockedConstraint = db.ReferenceVersions.Where(version => version.ReferenceId == x.Id && version.Version == x.CurrentVersion)
                    .Select(version => version.LockedConstraint).FirstOrDefault()
            }).Take(20).ToListAsync(cancellationToken);
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

    public async Task<WebMcpEnvelope> ProposeAsync(CreateShotRevisionProposalRequest request, CancellationToken cancellationToken)
    {
        var validation = Validate(request.CreativeDirection, request.Rationale, request.DesiredMediaType);
        if (validation is not null) return validation;
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return Failure("invalid_idempotency_key", "Supply an idempotency key of 1 to 128 characters.");
        if (request.AuthorityIds.Length > 12 || request.NoteIds.Length > 20)
            return Failure("too_many_bindings", "A proposal can bind at most 12 authorities and 20 notes.");

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
        var proposals = await query.OrderByDescending(x => x.CreatedAtUnixMs).Take(30).ToArrayAsync(cancellationToken);
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
    private static T? Parse<T>(string json) { try { return JsonSerializer.Deserialize<T>(json, JsonOptions); } catch (JsonException) { return default; } }
    private static object ToSummary(ShotRevisionProposalRecord x) => new { x.Id, x.ShotId, x.BaseVersion, x.CreativeDirection, x.Rationale, x.DesiredMediaType, authorityIds = Parse<string[]>(x.AuthorityIdsJson) ?? [], noteIds = Parse<Guid[]>(x.NoteIdsJson) ?? [], x.State, x.CreatedAt, x.UpdatedAt, x.DecidedAt };
    private static WebMcpEnvelope Success(string code, string message, object? data, bool retryable = false) => new(true, "success", code, message, data, retryable);
    private static WebMcpEnvelope Failure(string code, string message) => new(false, "error", code, message);
    private static WebMcpEnvelope Stale(int? currentVersion) => new(false, "conflict", "stale_shot", currentVersion is null ? "The source shot no longer exists." : $"The shot is now v{currentVersion}. Refresh before deciding this proposal.");
}
