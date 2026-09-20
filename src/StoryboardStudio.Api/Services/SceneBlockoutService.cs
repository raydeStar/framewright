using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Turning one reference picture into a construction plan the artist can argue
/// with before anything is built.
///
/// A plan is a proposal, not a scene: it names the objects the reference seems
/// to call for, matches the ones the library already holds, describes simple
/// stand-in geometry for the rest, and says plainly what it could not see. It
/// stays inert until the artist approves it, and approving it builds a blockout
/// out of placeholders and existing models only. Nothing here dispatches a
/// provider or generates an asset, at either step.
/// </summary>
public sealed class SceneBlockoutService(StudioDbContext db, AssetStore assets, IProjectScope projectScope, TimeProvider timeProvider)
{
    /// <summary>A reference yields a readable plan, not an unbounded object dump.</summary>
    private const int MaxItems = 12;
    private const int MaxPlans = 60;
    private const int MaxNotes = 10;
    private const double MaxDistanceFromOrigin = 10_000;
    private static readonly string[] Confidences = ["Certain", "Approximate", "Occluded"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SceneCameraSummary DefaultCamera = new(0.9, 0.42, 6, [0, 0.5, 0], 38);

    public async Task<WebMcpEnvelope> ProposeAsync(ProposeSceneBlockoutRequest request, CancellationToken cancellationToken)
    {
        var title = (request.Title ?? "").Trim();
        var summary = (request.Summary ?? "").Trim();
        if (title.Length is 0 or > 120) return Failure("invalid_title", "A blockout plan needs a title of 1 to 120 characters.");
        if (summary.Length is 0 or > 1000) return Failure("invalid_summary", "A blockout plan needs a summary of 1 to 1000 characters.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            return Failure("invalid_idempotency_key", "Supply an idempotency key of 1 to 128 characters.");

        var items = request.Items ?? [];
        if (items.Length == 0) return Failure("empty_plan", "A blockout plan must describe at least one object.");
        if (items.Length > MaxItems) return Failure("plan_too_large", $"A blockout plan supports up to {MaxItems} objects in this build.");

        var assumptions = Clean(request.Assumptions);
        var uncertainties = Clean(request.Uncertainties);
        if (assumptions is null || uncertainties is null)
            return Failure("invalid_notes", $"Assumptions and uncertainties are up to {MaxNotes} entries of 300 characters each.");

        var camera = request.Camera ?? DefaultCamera;
        if (ValidateCamera(camera) is { } cameraError) return Failure("invalid_camera", cameraError);

        var replay = await db.SceneBlockoutPlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return replay.ReferenceAssetId == request.ReferenceAssetId && replay.Title == title
                ? Success("blockout_replayed", "The existing plan was returned without creating a duplicate.", await DescribeAsync(replay, cancellationToken))
                : Failure("idempotency_conflict", "That idempotency key already belongs to a different plan.");
        }

        var reference = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ReferenceAssetId, cancellationToken);
        if (reference is null) return Failure("reference_not_found", "That reference is not part of the active project.");
        if (reference.Kind != nameof(AssetKind.Image))
            return Failure("reference_not_an_image", "A blockout is read from a reference image or sketch.");
        // The plan is bound to the exact bytes it was read from, so a plan built
        // on a reference the artist has since replaced is refused rather than
        // applied to a picture nobody looked at.
        if (!string.Equals(reference.ContentHash, (request.ObservedReferenceHash ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            return new WebMcpEnvelope(false, "conflict", "stale_reference",
                "This reference changed after it was read. Read the reference again before proposing a blockout.", null, true);

        if (await db.SceneBlockoutPlans.CountAsync(cancellationToken) >= MaxPlans)
            return Failure("too_many_plans", "This project already holds the supported number of blockout plans.");

        var matchIds = items.Where(x => x.MatchAssetId is not null).Select(x => x.MatchAssetId!.Value).Distinct().ToArray();
        var matches = await db.Assets.AsNoTracking()
            .Where(x => matchIds.Contains(x.Id) && x.Kind == nameof(AssetKind.Model))
            .Select(x => new { x.Id, x.DisplayName }).ToArrayAsync(cancellationToken);
        if (matches.Length != matchIds.Length)
            return Failure("match_not_found", "Every library match must be a model this project already holds.");

        var prepared = new List<SceneBlockoutItemRecord>(items.Length);
        var order = 0;
        foreach (var item in items)
        {
            var role = (item.Role ?? "").Trim();
            if (role.Length is 0 or > 80) return Failure("invalid_role", "Each planned object needs a role of 1 to 80 characters.");

            var placeholder = item.Shape is null && item.Size is null
                ? null
                : new ScenePlaceholderSummary((item.Shape ?? "").Trim(), item.Size ?? []);
            if (SceneService.ValidatePlaceholder(item.MatchAssetId, placeholder) is { } shapeError)
                return Failure("invalid_placeholder", shapeError);

            var position = item.Position ?? [0, 0, 0];
            var rotation = item.Rotation ?? [0, 0, 0];
            var scale = item.Scale ?? [1, 1, 1];
            if (ValidateTransform(position, rotation, scale) is { } transformError) return Failure("invalid_transform", transformError);

            var motion = (item.MotionIntent ?? "").Trim();
            var note = (item.Note ?? "").Trim();
            if (motion.Length > 200) return Failure("invalid_motion_intent", "Motion intent must be 200 characters or fewer.");
            if (note.Length > 300) return Failure("invalid_note", "An object note must be 300 characters or fewer.");

            var confidence = (item.Confidence ?? "Approximate").Trim();
            if (!Confidences.Contains(confidence))
                return Failure("invalid_confidence", "Confidence must be one of " + string.Join(", ", Confidences) + ".");

            prepared.Add(new SceneBlockoutItemRecord
            {
                Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, PlanId = Guid.Empty, SortOrder = order++,
                Role = role, MatchAssetId = item.MatchAssetId,
                PlaceholderShape = placeholder?.Shape,
                PlaceholderSizeX = placeholder?.Size[0] ?? 0,
                PlaceholderSizeY = placeholder?.Size[1] ?? 0,
                PlaceholderSizeZ = placeholder?.Size[2] ?? 0,
                PositionX = position[0], PositionY = position[1], PositionZ = position[2],
                RotationX = rotation[0], RotationY = rotation[1], RotationZ = rotation[2],
                ScaleX = scale[0], ScaleY = scale[1], ScaleZ = scale[2],
                MotionIntent = motion, Confidence = confidence, Note = note,
            });
        }

        var now = timeProvider.GetUtcNow();
        var plan = new SceneBlockoutPlanRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId,
            ReferenceAssetId = reference.Id, ReferenceContentHash = reference.ContentHash,
            Title = title, Summary = summary, State = "Pending",
            CameraYaw = camera.Yaw, CameraPitch = camera.Pitch, CameraDistance = camera.Distance,
            CameraTargetX = camera.Target[0], CameraTargetY = camera.Target[1], CameraTargetZ = camera.Target[2],
            CameraFieldOfView = camera.FieldOfView,
            AssumptionsJson = JsonSerializer.Serialize(assumptions, JsonOptions),
            UncertaintiesJson = JsonSerializer.Serialize(uncertainties, JsonOptions),
            IdempotencyKey = request.IdempotencyKey.Trim(),
            CreatedAt = now, CreatedAtUnixMs = now.ToUnixTimeMilliseconds(), UpdatedAt = now,
        };
        foreach (var item in prepared) item.PlanId = plan.Id;
        db.SceneBlockoutPlans.Add(plan);
        db.SceneBlockoutItems.AddRange(prepared);
        await db.SaveChangesAsync(cancellationToken);

        return Success("blockout_proposed",
            $"A {prepared.Count}-object blockout plan was staged for review. Nothing is built until the artist approves it.",
            await DescribeAsync(plan, cancellationToken));
    }

    public async Task<RepositoryResult<IReadOnlyList<SceneBlockoutPlanSummary>>> ListAsync(Guid? referenceAssetId, CancellationToken cancellationToken)
    {
        var query = db.SceneBlockoutPlans.AsNoTracking();
        if (referenceAssetId is { } id) query = query.Where(x => x.ReferenceAssetId == id);
        var plans = await query.Take(MaxPlans).ToArrayAsync(cancellationToken);
        // SQLite cannot order DateTimeOffset, so the bounded set is ordered here.
        var summaries = new List<SceneBlockoutPlanSummary>(plans.Length);
        foreach (var plan in plans.OrderByDescending(x => x.CreatedAtUnixMs))
            summaries.Add(await DescribeAsync(plan, cancellationToken));
        return RepositoryResult<IReadOnlyList<SceneBlockoutPlanSummary>>.Ok(summaries);
    }

    public async Task<RepositoryResult<SceneBlockoutPlanSummary>> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await db.SceneBlockoutPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == planId, cancellationToken);
        return plan is null
            ? RepositoryResult<SceneBlockoutPlanSummary>.NotFound()
            : RepositoryResult<SceneBlockoutPlanSummary>.Ok(await DescribeAsync(plan, cancellationToken));
    }

    /// <summary>The plan a scene was built from, so its reasoning survives reopening.</summary>
    public async Task<RepositoryResult<SceneBlockoutPlanSummary>> ForSceneAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        var plan = await db.SceneBlockoutPlans.AsNoTracking().FirstOrDefaultAsync(x => x.SceneId == sceneId, cancellationToken);
        return plan is null
            ? RepositoryResult<SceneBlockoutPlanSummary>.NotFound()
            : RepositoryResult<SceneBlockoutPlanSummary>.Ok(await DescribeAsync(plan, cancellationToken));
    }

    public async Task<RepositoryResult<SceneBlockoutPlanSummary>> RejectAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await db.SceneBlockoutPlans.SingleOrDefaultAsync(x => x.Id == planId, cancellationToken);
        if (plan is null) return RepositoryResult<SceneBlockoutPlanSummary>.NotFound();
        if (plan.State == "Applied") return RepositoryResult<SceneBlockoutPlanSummary>.Invalid("This plan already built a scene and cannot be rejected.");
        if (plan.State != "Rejected")
        {
            plan.State = "Rejected";
            plan.DecidedAt = plan.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return RepositoryResult<SceneBlockoutPlanSummary>.Ok(await DescribeAsync(plan, cancellationToken));
    }

    /// <summary>
    /// The artist's approval, and the only thing in this milestone that creates
    /// anything. It builds a new scene, so no existing work can be overwritten:
    /// every object is a placeholder or a model the project already holds, and
    /// no provider job is created. From here the blockout is an ordinary scene,
    /// corrected and saved through the ordinary validated save.
    /// </summary>
    public async Task<RepositoryResult<SceneSummary>> ApplyAsync(
        Guid planId, ApplySceneBlockoutRequest request, SceneService scenes, CancellationToken cancellationToken)
    {
        var plan = await db.SceneBlockoutPlans.SingleOrDefaultAsync(x => x.Id == planId, cancellationToken);
        if (plan is null) return RepositoryResult<SceneSummary>.NotFound();
        if (plan.State == "Rejected") return RepositoryResult<SceneSummary>.Invalid("A rejected plan cannot be applied.");

        // Applying twice hands back the scene that was already built rather than
        // quietly building a second copy of it.
        if (plan.State == "Applied" && plan.SceneId is { } builtSceneId)
            return await scenes.GetAsync(builtSceneId, cancellationToken);

        var reference = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == plan.ReferenceAssetId, cancellationToken);
        if (reference is null) return RepositoryResult<SceneSummary>.Invalid("The reference this plan was read from is no longer in this project.");

        var items = await db.SceneBlockoutItems.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        if (items.Count == 0) return RepositoryResult<SceneSummary>.Invalid("This plan no longer describes any objects.");

        // A library match that has since been removed would leave an object with
        // nothing under it, so the whole plan is refused rather than half built.
        var matchIds = items.Where(x => x.MatchAssetId is not null).Select(x => x.MatchAssetId!.Value).Distinct().ToArray();
        var present = await db.Assets.AsNoTracking()
            .Where(x => matchIds.Contains(x.Id) && x.Kind == nameof(AssetKind.Model))
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (present.Length != matchIds.Length)
            return RepositoryResult<SceneSummary>.Invalid("A model this plan matched is no longer in the library. Read the reference again before building.");

        var name = (request?.SceneName ?? "").Trim();
        if (name.Length == 0) name = plan.Title;
        if (name.Length > 120) return RepositoryResult<SceneSummary>.Invalid("A scene name must be 1 to 120 characters.");

        var now = timeProvider.GetUtcNow();
        var scene = new SceneRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, Name = name, Version = 1,
            CameraYaw = plan.CameraYaw, CameraPitch = plan.CameraPitch, CameraDistance = plan.CameraDistance,
            CameraTargetX = plan.CameraTargetX, CameraTargetY = plan.CameraTargetY, CameraTargetZ = plan.CameraTargetZ,
            CameraFieldOfView = plan.CameraFieldOfView,
            KeyLightIntensity = 2.2, KeyLightYaw = 0.8, KeyLightPitch = 0.9, AmbientLightIntensity = 1.4,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Scenes.Add(scene);

        var order = 0;
        foreach (var item in items.OrderBy(x => x.SortOrder))
        {
            var instance = new SceneInstanceRecord
            {
                Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, SceneId = scene.Id,
                AssetId = item.MatchAssetId,
                PlaceholderShape = item.PlaceholderShape,
                PlaceholderSizeX = item.PlaceholderSizeX,
                PlaceholderSizeY = item.PlaceholderSizeY,
                PlaceholderSizeZ = item.PlaceholderSizeZ,
                // Each object keeps the role it was planned for and the plan it
                // came from, so what the agent assumed stays inspectable later.
                Name = item.Role, Role = item.Role, SourcePlanId = plan.Id,
                SortOrder = order++,
                PositionX = item.PositionX, PositionY = item.PositionY, PositionZ = item.PositionZ,
                RotationX = item.RotationX, RotationY = item.RotationY, RotationZ = item.RotationZ,
                ScaleX = item.ScaleX, ScaleY = item.ScaleY, ScaleZ = item.ScaleZ,
                CreatedAt = now, UpdatedAt = now,
            };
            db.SceneInstances.Add(instance);
            item.InstanceId = instance.Id;
        }

        plan.State = "Applied";
        plan.SceneId = scene.Id;
        plan.DecidedAt ??= now;
        plan.AppliedAt = plan.UpdatedAt = now;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SceneBlockoutApplied", TargetType = "Scene", TargetId = scene.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { planId = plan.Id, plan.ReferenceAssetId, objects = items.Count }, JsonOptions),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await scenes.GetAsync(scene.Id, cancellationToken);
    }

    private async Task<SceneBlockoutPlanSummary> DescribeAsync(SceneBlockoutPlanRecord plan, CancellationToken cancellationToken)
    {
        var items = await db.SceneBlockoutItems.AsNoTracking()
            .Where(x => x.PlanId == plan.Id).OrderBy(x => x.SortOrder).Take(MaxItems).ToArrayAsync(cancellationToken);
        var matchIds = items.Where(x => x.MatchAssetId is not null).Select(x => x.MatchAssetId!.Value).Distinct().ToArray();
        var matches = await db.Assets.AsNoTracking()
            .Where(x => matchIds.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName }).ToArrayAsync(cancellationToken);
        var reference = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == plan.ReferenceAssetId, cancellationToken);
        var sceneName = plan.SceneId is { } sceneId
            ? (await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken))?.Name
            : null;

        return new SceneBlockoutPlanSummary(
            plan.Id, plan.ReferenceAssetId, reference?.DisplayName ?? "Removed reference", plan.ReferenceContentHash,
            reference is not null && assets.StoredFileExists(reference.StoragePath) ? $"/api/assets/{plan.ReferenceAssetId}/content" : null,
            // The plan still describes the picture it was read from, so a
            // reference edited since then is flagged rather than assumed current.
            ReferenceChanged: reference is not null && !string.Equals(reference.ContentHash, plan.ReferenceContentHash, StringComparison.OrdinalIgnoreCase),
            plan.Title, plan.Summary, plan.State,
            new SceneCameraSummary(plan.CameraYaw, plan.CameraPitch, plan.CameraDistance,
                [plan.CameraTargetX, plan.CameraTargetY, plan.CameraTargetZ], plan.CameraFieldOfView),
            Read(plan.AssumptionsJson), Read(plan.UncertaintiesJson),
            [.. items.Select(item => new SceneBlockoutItemSummary(
                item.Id, item.Role, item.MatchAssetId,
                matches.FirstOrDefault(x => x.Id == item.MatchAssetId)?.DisplayName,
                item.PlaceholderShape is { } shape
                    ? new ScenePlaceholderSummary(shape, [item.PlaceholderSizeX, item.PlaceholderSizeY, item.PlaceholderSizeZ])
                    : null,
                [item.PositionX, item.PositionY, item.PositionZ],
                [item.RotationX, item.RotationY, item.RotationZ],
                [item.ScaleX, item.ScaleY, item.ScaleZ],
                item.MotionIntent, item.Confidence, item.Note, item.InstanceId))],
            plan.SceneId, sceneName, plan.CreatedAt, plan.DecidedAt, plan.AppliedAt);
    }

    private static string[]? Clean(string[]? entries)
    {
        if (entries is null) return [];
        if (entries.Length > MaxNotes) return null;
        var cleaned = entries.Select(entry => (entry ?? "").Trim()).Where(entry => entry.Length > 0).ToArray();
        return cleaned.Any(entry => entry.Length > 300) ? null : cleaned;
    }

    private static string[] Read(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? ValidateCamera(SceneCameraSummary camera)
    {
        if (camera.Target is not { Length: 3 } || camera.Target.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
            return "The proposed camera target must be three finite coordinates inside the working volume.";
        if (!double.IsFinite(camera.Yaw) || !double.IsFinite(camera.Pitch)) return "The proposed camera orbit must be finite.";
        if (!double.IsFinite(camera.Distance) || camera.Distance is <= 0 or > MaxDistanceFromOrigin)
            return "The proposed camera distance must be greater than zero and inside the working volume.";
        if (!double.IsFinite(camera.FieldOfView) || camera.FieldOfView is < 10 or > 120)
            return "The proposed camera field of view must be between 10 and 120 degrees.";
        return null;
    }

    private static string? ValidateTransform(double[] position, double[] rotation, double[] scale)
    {
        if (position is not { Length: 3 } || rotation is not { Length: 3 } || scale is not { Length: 3 })
            return "Each planned object needs a three-axis position, rotation, and scale.";
        if (position.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
            return "Planned positions must be finite and inside the supported working volume.";
        if (rotation.Any(value => !double.IsFinite(value))) return "Planned rotations must be finite.";
        if (scale.Any(value => !double.IsFinite(value) || value is < 0.001 or > 1000))
            return "Planned scale must be between 0.001 and 1000 on every axis.";
        return null;
    }

    private static WebMcpEnvelope Success(string code, string message, object? data) => new(true, "success", code, message, data);
    private static WebMcpEnvelope Failure(string code, string message) => new(false, "error", code, message);
}
