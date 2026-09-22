using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Director Mode inside a scene: notes anchored to one object, a context packet
/// describing exactly which instance is selected, and proposals that can only
/// ever move that one instance.
///
/// Two identical props are two different objects here. Every note, context
/// packet, and proposal names an instance id, so "change the chair" can never
/// quietly mean the other chair.
/// </summary>
public sealed class SceneDirectionService(StudioDbContext db, IProjectScope projectScope, TimeProvider timeProvider)
{
    private const int DirectorContextVersion = 1;
    private static readonly string[] ReadOnlyActions = ["get_director_context"];
    private static readonly string[] SelectedActions = ["propose_scene_edit", "get_director_context"];
    private static readonly string[] ReferenceActions = ["propose_scene_blockout", "get_director_context"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RepositoryResult<IReadOnlyList<SceneAnnotationSummary>>> ListAnnotationsAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        if (scene is null) return RepositoryResult<IReadOnlyList<SceneAnnotationSummary>>.NotFound();
        return RepositoryResult<IReadOnlyList<SceneAnnotationSummary>>.Ok(await DescribeAnnotationsAsync(sceneId, cancellationToken));
    }

    public async Task<RepositoryResult<SceneAnnotationSummary>> AddAnnotationAsync(
        Guid sceneId, CreateSceneAnnotationRequest request, CancellationToken cancellationToken)
    {
        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        if (scene is null) return RepositoryResult<SceneAnnotationSummary>.NotFound();

        var instance = await db.SceneInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.InstanceId && x.SceneId == sceneId, cancellationToken);
        if (instance is null) return RepositoryResult<SceneAnnotationSummary>.Invalid("That object is not part of this scene.");

        var body = (request.Body ?? "").Trim();
        if (body.Length is 0 or > 2000) return RepositoryResult<SceneAnnotationSummary>.Invalid("A note must be 1 to 2000 characters.");
        if (request.Anchor is not { Length: 3 } || request.Anchor.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10_000))
            return RepositoryResult<SceneAnnotationSummary>.Invalid("A note anchor must be three finite local coordinates.");
        if (await db.SceneAnnotations.CountAsync(x => x.SceneId == sceneId, cancellationToken) >= 200)
            return RepositoryResult<SceneAnnotationSummary>.Invalid("This scene already holds the supported number of notes.");

        var now = timeProvider.GetUtcNow();
        var annotation = new SceneAnnotationRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, SceneId = sceneId,
            InstanceId = instance.Id,
            // The revision the anchor was measured against, so a later revision
            // swap makes this note explicitly stale instead of moving it. A note
            // on a placeholder binds to no revision, so replacing that
            // placeholder with a real model makes it stale in the same way.
            AssetId = instance.AssetId ?? Guid.Empty,
            AnchorX = request.Anchor[0], AnchorY = request.Anchor[1], AnchorZ = request.Anchor[2],
            CameraYaw = request.Camera.Yaw, CameraPitch = request.Camera.Pitch, CameraDistance = request.Camera.Distance,
            CameraTargetX = request.Camera.Target[0], CameraTargetY = request.Camera.Target[1], CameraTargetZ = request.Camera.Target[2],
            Body = body, State = "Open", CreatedAt = now, UpdatedAt = now,
        };
        db.SceneAnnotations.Add(annotation);
        await db.SaveChangesAsync(cancellationToken);

        var described = await DescribeAnnotationsAsync(sceneId, cancellationToken);
        return RepositoryResult<SceneAnnotationSummary>.Ok(described.Single(x => x.Id == annotation.Id));
    }

    public async Task<RepositoryResult<SceneAnnotationSummary>> ResolveAnnotationAsync(Guid annotationId, CancellationToken cancellationToken)
    {
        var annotation = await db.SceneAnnotations.SingleOrDefaultAsync(x => x.Id == annotationId, cancellationToken);
        if (annotation is null) return RepositoryResult<SceneAnnotationSummary>.NotFound();
        annotation.State = "Resolved";
        annotation.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        var described = await DescribeAnnotationsAsync(annotation.SceneId, cancellationToken);
        return RepositoryResult<SceneAnnotationSummary>.Ok(described.Single(x => x.Id == annotationId));
    }

    /// <summary>
    /// What the artist has selected in a scene, as one bounded packet, with a
    /// token bound to the scene version, the selected instance, its pinned
    /// revision, its transform, and its open notes.
    /// </summary>
    public async Task<WebMcpEnvelope> ContextAsync(
        Guid sceneId, Guid? instanceId, Guid? referenceAssetId, bool directorMode, double time, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(time) || time is < 0 or > 86_400)
            return Failure("invalid_scene_time", "Scene time must be finite and between 0 and 86400 seconds.");
        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        if (scene is null) return Failure("scene_not_found", "That scene is not part of the active project.");

        var instances = await db.SceneInstances.AsNoTracking()
            .Where(x => x.SceneId == sceneId).OrderBy(x => x.SortOrder).Take(200).ToArrayAsync(cancellationToken);
        var selected = instanceId is null ? null : instances.FirstOrDefault(x => x.Id == instanceId);
        if (instanceId is not null && selected is null)
            return Failure("instance_not_found", "That object is not part of this scene.");

        // The reference the artist is reading from, when there is one. It carries
        // its content hash so a blockout proposal can be bound to the exact
        // picture it was read from rather than to whatever is there later.
        var reference = referenceAssetId is null
            ? null
            : await db.Assets.AsNoTracking()
                .Where(x => x.Id == referenceAssetId && x.Kind == nameof(AssetKind.Image))
                .Select(x => new { x.Id, x.DisplayName, x.ContentHash }).SingleOrDefaultAsync(cancellationToken);
        if (referenceAssetId is not null && reference is null)
            return Failure("reference_not_found", "That reference is not an image in the active project.");

        var annotations = await DescribeAnnotationsAsync(sceneId, cancellationToken);
        var selectedNotes = selected is null
            ? []
            : annotations.Where(x => x.InstanceId == selected.Id && x.State == "Open").ToArray();
        var pinnedAssetIds = instances.Where(instance => instance.AssetId is not null)
            .Select(instance => instance.AssetId!.Value).Distinct().ToArray();
        var assetNames = await db.Assets.AsNoTracking()
            .Where(x => pinnedAssetIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DisplayName, x.RevisionNumber }).ToArrayAsync(cancellationToken);

        var roundedTime = Math.Round(time, 4);
        return Success("scene_director_context",
            selected is null
                ? $"{scene.Name} is open with {instances.Length} {(instances.Length == 1 ? "object" : "objects")} and nothing selected."
                : $"{selected.Name} is selected in {scene.Name}.",
            new
            {
                contextVersion = DirectorContextVersion,
                stateToken = ComputeStateToken(scene, selected, selectedNotes, roundedTime),
                scene = new { scene.Id, scene.Name, scene.Version, instanceCount = instances.Length, scene.UpdatedAt },
                view = new
                {
                    directorMode,
                    time = roundedTime,
                    camera = new
                    {
                        yaw = scene.CameraYaw, pitch = scene.CameraPitch, distance = scene.CameraDistance,
                        target = new[] { scene.CameraTargetX, scene.CameraTargetY, scene.CameraTargetZ },
                        fieldOfView = scene.CameraFieldOfView,
                    },
                },
                selectedObject = selected is null ? null : new
                {
                    instanceId = selected.Id,
                    selected.Name,
                    assetId = selected.AssetId,
                    assetName = assetNames.FirstOrDefault(x => x.Id == selected.AssetId)?.DisplayName
                        ?? (selected.PlaceholderShape is null ? "Unavailable model" : selected.PlaceholderShape + " placeholder"),
                    placeholder = selected.PlaceholderShape is null ? null : new
                    {
                        shape = selected.PlaceholderShape,
                        size = new[] { selected.PlaceholderSizeX, selected.PlaceholderSizeY, selected.PlaceholderSizeZ },
                    },
                    role = selected.Role,
                    revisionNumber = assetNames.FirstOrDefault(x => x.Id == selected.AssetId)?.RevisionNumber ?? 1,
                    position = new[] { selected.PositionX, selected.PositionY, selected.PositionZ },
                    rotation = new[] { selected.RotationX, selected.RotationY, selected.RotationZ },
                    scale = new[] { selected.ScaleX, selected.ScaleY, selected.ScaleZ },
                },
                // Every object is listed by identity, so an agent naming one of
                // two identical props has to name which one.
                objects = instances.Select(instance => new
                {
                    instanceId = instance.Id, instance.Name, assetId = instance.AssetId,
                    position = new[] { instance.PositionX, instance.PositionY, instance.PositionZ },
                }).ToArray(),
                annotations = selectedNotes.Select(note => new
                {
                    note.Id, note.Body, note.Anchor, note.Stale, boundToAssetId = note.AssetId,
                }).ToArray(),
                reference = reference is null ? null : new
                {
                    assetId = reference.Id, name = reference.DisplayName, contentHash = reference.ContentHash,
                    contentUrl = $"/api/assets/{reference.Id}/content",
                },
                availableActions = reference is null
                    ? (selected is null ? ReadOnlyActions : SelectedActions)
                    : (selected is null ? ReferenceActions : [.. SelectedActions, "propose_scene_blockout"]),
                note = "Proposals name one instance. Applying one is the artist's action and changes nothing else in the scene.",
            });
    }

    public async Task<WebMcpEnvelope> ProposeAsync(CreateSceneProposalRequest request, CancellationToken cancellationToken)
    {
        var direction = (request.Direction ?? "").Trim();
        var rationale = (request.Rationale ?? "").Trim();
        if (direction.Length is 0 or > 1000) return Failure("invalid_direction", "Creative direction must be 1 to 1000 characters.");
        if (rationale.Length > 600) return Failure("invalid_rationale", "Rationale must be 600 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return Failure("invalid_idempotency_key", "Supply an idempotency key of 1 to 128 characters.");
        if (request.Position is null && request.Rotation is null && request.Scale is null)
            return Failure("empty_proposal", "A scene proposal must name at least one of position, rotation, or scale.");
        foreach (var vector in new[] { request.Position, request.Rotation, request.Scale })
        {
            if (vector is null) continue;
            if (vector.Length != 3 || vector.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10_000))
                return Failure("invalid_transform", "A proposed transform must be three finite values inside the working volume.");
        }
        if (request.Scale is not null && request.Scale.Any(value => value is < 0.001 or > 1000))
            return Failure("invalid_transform", "A proposed scale must be between 0.001 and 1000 on every axis.");
        if (!double.IsFinite(request.ObservedSceneTime) || request.ObservedSceneTime is < 0 or > 86_400)
            return Failure("invalid_scene_time", "Scene time must be finite and between 0 and 86400 seconds.");

        var existing = await db.SceneProposals.SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var same = existing.SceneId == request.SceneId && existing.InstanceId == request.InstanceId && existing.Direction == direction;
            return same
                ? Success("proposal_replayed", "The existing proposal was returned without creating a duplicate.", await SummarizeAsync(existing, cancellationToken))
                : Failure("idempotency_conflict", "That idempotency key already belongs to a different proposal.");
        }

        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SceneId, cancellationToken);
        if (scene is null) return Failure("scene_not_found", "That scene is not part of the active project.");
        if (scene.Version != request.ExpectedSceneVersion) return StaleScene(scene.Version);

        var instance = await db.SceneInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.InstanceId && x.SceneId == scene.Id, cancellationToken);
        if (instance is null) return Failure("instance_not_found", "That object is not part of this scene.");

        // The proposal has to be anchored in a context the agent actually read,
        // for this exact object.
        var annotations = await DescribeAnnotationsAsync(scene.Id, cancellationToken);
        var expected = ComputeStateToken(scene, instance,
            annotations.Where(x => x.InstanceId == instance.Id && x.State == "Open").ToArray(),
            Math.Round(request.ObservedSceneTime, 4));
        if (!string.Equals(expected, request.ObservedStateToken, StringComparison.OrdinalIgnoreCase))
            return new WebMcpEnvelope(false, "conflict", "stale_context",
                "This scene changed after that director context was read. Read it again before proposing.", null, true);

        var now = timeProvider.GetUtcNow();
        var proposal = new SceneProposalRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, SceneId = scene.Id, InstanceId = instance.Id,
            BaseSceneVersion = scene.Version, ObservedStateToken = expected,
            Direction = direction, Rationale = rationale,
            PositionJson = request.Position is null ? null : JsonSerializer.Serialize(request.Position, JsonOptions),
            RotationJson = request.Rotation is null ? null : JsonSerializer.Serialize(request.Rotation, JsonOptions),
            ScaleJson = request.Scale is null ? null : JsonSerializer.Serialize(request.Scale, JsonOptions),
            State = "Pending", IdempotencyKey = request.IdempotencyKey.Trim(),
            CreatedAt = now, CreatedAtUnixMs = now.ToUnixTimeMilliseconds(), UpdatedAt = now,
        };
        db.SceneProposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        return Success("proposal_created",
            $"A reviewable change to {instance.Name} was staged. The scene is unchanged until the artist applies and saves it.",
            await SummarizeAsync(proposal, cancellationToken));
    }

    public async Task<RepositoryResult<IReadOnlyList<SceneProposalSummary>>> ListProposalsAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        var ordered = await db.SceneProposals.AsNoTracking()
            .Where(x => x.SceneId == sceneId)
            .OrderByDescending(x => x.CreatedAtUnixMs)
            .ThenByDescending(x => x.Id)
            .Take(60)
            .ToArrayAsync(cancellationToken);
        var summaries = new List<SceneProposalSummary>(ordered.Length);
        foreach (var proposal in ordered) summaries.Add(await SummarizeAsync(proposal, cancellationToken));
        return RepositoryResult<IReadOnlyList<SceneProposalSummary>>.Ok(summaries);
    }

    public Task<RepositoryResult<SceneProposalSummary>> AcceptAsync(Guid proposalId, CancellationToken cancellationToken) =>
        DecideAsync(proposalId, "Accepted", cancellationToken);

    public Task<RepositoryResult<SceneProposalSummary>> RejectAsync(Guid proposalId, CancellationToken cancellationToken) =>
        DecideAsync(proposalId, "Rejected", cancellationToken);

    /// <summary>
    /// Marks an accepted proposal as applied and hands back the one instance it
    /// targets. The transform still reaches the scene through the ordinary
    /// validated save, so applying is never a hidden write.
    /// </summary>
    public async Task<RepositoryResult<SceneProposalSummary>> ApplyAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await db.SceneProposals.SingleOrDefaultAsync(x => x.Id == proposalId, cancellationToken);
        if (proposal is null) return RepositoryResult<SceneProposalSummary>.NotFound();
        if (proposal.State == "Rejected") return RepositoryResult<SceneProposalSummary>.Invalid("A rejected proposal cannot be applied.");
        if (proposal.State == "Pending") return RepositoryResult<SceneProposalSummary>.Invalid("Accept this direction before applying it.");

        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposal.SceneId, cancellationToken);
        if (scene is null) return RepositoryResult<SceneProposalSummary>.NotFound();
        if (scene.Version != proposal.BaseSceneVersion)
            return RepositoryResult<SceneProposalSummary>.Conflict($"This scene is now version {scene.Version}. Reopen it and read the context again before applying.");
        if (!await db.SceneInstances.AsNoTracking().AnyAsync(x => x.Id == proposal.InstanceId && x.SceneId == scene.Id, cancellationToken))
            return RepositoryResult<SceneProposalSummary>.Invalid("That object is no longer part of this scene.");

        if (proposal.State != "Applied")
        {
            proposal.State = "Applied";
            proposal.AppliedAt = proposal.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return RepositoryResult<SceneProposalSummary>.Ok(await SummarizeAsync(proposal, cancellationToken));
    }

    private async Task<RepositoryResult<SceneProposalSummary>> DecideAsync(Guid proposalId, string state, CancellationToken cancellationToken)
    {
        var proposal = await db.SceneProposals.SingleOrDefaultAsync(x => x.Id == proposalId, cancellationToken);
        if (proposal is null) return RepositoryResult<SceneProposalSummary>.NotFound();
        if (proposal.State == state) return RepositoryResult<SceneProposalSummary>.Ok(await SummarizeAsync(proposal, cancellationToken));
        if (proposal.State != "Pending") return RepositoryResult<SceneProposalSummary>.Invalid($"This proposal was already {proposal.State.ToLowerInvariant()}.");
        proposal.State = state;
        proposal.DecidedAt = proposal.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SceneProposalSummary>.Ok(await SummarizeAsync(proposal, cancellationToken));
    }

    private async Task<IReadOnlyList<SceneAnnotationSummary>> DescribeAnnotationsAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        // DateTimeOffset ordering is not translated by SQLite. Materialize this
        // scene's notes before applying the documented oldest-first cap so the
        // retained set is deliberate instead of whichever rows SQLite returns.
        var annotations = await db.SceneAnnotations.AsNoTracking()
            .Where(x => x.SceneId == sceneId)
            .ToArrayAsync(cancellationToken);
        var instances = await db.SceneInstances.AsNoTracking().Where(x => x.SceneId == sceneId).ToArrayAsync(cancellationToken);
        return [.. annotations
            .OrderBy(annotation => annotation.CreatedAt)
            .ThenBy(annotation => annotation.Id)
            .Take(200)
            .Select(annotation =>
            {
                var instance = instances.FirstOrDefault(x => x.Id == annotation.InstanceId);
                return new SceneAnnotationSummary(
                    annotation.Id, annotation.SceneId, annotation.InstanceId, annotation.AssetId,
                    instance?.Name ?? "Removed object",
                    [annotation.AnchorX, annotation.AnchorY, annotation.AnchorZ],
                    new SceneCameraSummary(annotation.CameraYaw, annotation.CameraPitch, annotation.CameraDistance,
                        [annotation.CameraTargetX, annotation.CameraTargetY, annotation.CameraTargetZ], 38),
                    annotation.Body, annotation.State,
                    // The geometry under this anchor changed if the instance now
                    // points at a different revision.
                    Stale: instance is not null && instance.AssetId.GetValueOrDefault() != annotation.AssetId,
                    Orphaned: instance is null,
                    annotation.CreatedAt);
            })];
    }

    private async Task<SceneProposalSummary> SummarizeAsync(SceneProposalRecord proposal, CancellationToken cancellationToken)
    {
        var instance = await db.SceneInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proposal.InstanceId, cancellationToken);
        return new SceneProposalSummary(
            proposal.Id, proposal.SceneId, proposal.InstanceId, instance?.Name ?? "Removed object",
            proposal.BaseSceneVersion, proposal.Direction, proposal.Rationale,
            Parse(proposal.PositionJson), Parse(proposal.RotationJson), Parse(proposal.ScaleJson),
            proposal.State, proposal.CreatedAt, proposal.DecidedAt, proposal.AppliedAt);
    }

    private static double[]? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<double[]>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string ComputeStateToken(
        SceneRecord scene, SceneInstanceRecord? instance, IReadOnlyList<SceneAnnotationSummary> notes, double time)
    {
        var builder = new StringBuilder()
            .Append("scene-director-v").Append(DirectorContextVersion)
            .Append('|').Append(scene.ProjectId).Append('|').Append(scene.Id).Append('|').Append(scene.Version)
            .Append('|').Append(Fixed(time))
            .Append('|').Append(instance?.Id).Append('|').Append(instance?.AssetId)
            .Append('|').Append(Fixed(instance?.PositionX)).Append(',').Append(Fixed(instance?.PositionY)).Append(',').Append(Fixed(instance?.PositionZ))
            .Append('|').Append(Fixed(instance?.RotationX)).Append(',').Append(Fixed(instance?.RotationY)).Append(',').Append(Fixed(instance?.RotationZ))
            .Append('|').Append(Fixed(instance?.ScaleX)).Append(',').Append(Fixed(instance?.ScaleY)).Append(',').Append(Fixed(instance?.ScaleZ));
        foreach (var note in notes.OrderBy(x => x.Id))
            builder.Append('|').Append(note.Id).Append(':').Append(note.Body).Append(':').Append(note.Stale ? '1' : '0');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Fixed(double? value) => (value ?? 0).ToString("F4", CultureInfo.InvariantCulture);

    private static WebMcpEnvelope Success(string code, string message, object? data) => new(true, "success", code, message, data);
    private static WebMcpEnvelope Failure(string code, string message) => new(false, "error", code, message);
    private static WebMcpEnvelope StaleScene(int version) => new(false, "conflict", "stale_scene",
        $"This scene is now version {version}. Read the director context again before proposing.", null, true);
}
