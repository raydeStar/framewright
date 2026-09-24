using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Small editable scenes made of distinct instances of exact model revisions.
///
/// A scene saves as one unit against the version it was read at, so a save
/// built on a stale view is refused rather than quietly erasing newer work.
/// Instances are pinned to a revision, never to "whatever the library calls
/// this now": renaming a model in the library moves nothing, and removing an
/// instance removes the placement, not the asset.
/// </summary>
public sealed class SceneService(StudioDbContext db, AssetStore assets, IProjectScope projectScope, TimeProvider timeProvider)
{
    private const int MaxInstances = 200;
    /// <summary>Stand-in geometry the viewport can draw without loading anything.</summary>
    internal static readonly string[] SupportedShapes = ["Box", "Cylinder", "Sphere", "Plane"];
    private const double MaxDistanceFromOrigin = 10_000;

    public async Task<IReadOnlyList<SceneListItem>> ListAsync(CancellationToken cancellationToken)
    {
        // SQLite cannot order DateTimeOffset, so materialize the project's
        // local scene set before choosing the 200 most recently changed rows.
        // Taking first made the selected subset arbitrary and also raised EF's
        // row-limit-without-ordering warning on every scene-workspace load.
        var scenes = await db.Scenes.AsNoTracking().ToArrayAsync(cancellationToken);
        var counts = await db.SceneInstances.AsNoTracking()
            .GroupBy(x => x.SceneId)
            .Select(group => new { SceneId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        // SQLite cannot order DateTimeOffset, so the bounded set is ordered here.
        return [.. scenes
            .OrderByDescending(scene => scene.UpdatedAt)
            .ThenByDescending(scene => scene.Id)
            .Take(200)
            .Select(scene => new SceneListItem(
                scene.Id, scene.Name, scene.Version,
                counts.FirstOrDefault(count => count.SceneId == scene.Id)?.Count ?? 0,
                scene.UpdatedAt))];
    }

    public async Task<RepositoryResult<SceneSummary>> GetAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        return scene is null
            ? RepositoryResult<SceneSummary>.NotFound()
            : RepositoryResult<SceneSummary>.Ok(await DescribeAsync(scene, cancellationToken));
    }

    public async Task<RepositoryResult<SceneSummary>> CreateAsync(CreateSceneRequest request, CancellationToken cancellationToken)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120) return RepositoryResult<SceneSummary>.Invalid("A scene name must be 1 to 120 characters.");
        if (await db.Scenes.CountAsync(cancellationToken) >= 200)
            return RepositoryResult<SceneSummary>.Invalid("This project already holds the supported number of scenes.");

        var now = timeProvider.GetUtcNow();
        var scene = new SceneRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectScope.ProjectId, Name = name, Version = 1,
            CameraYaw = 0.9, CameraPitch = 0.42, CameraDistance = 6,
            CameraTargetX = 0, CameraTargetY = 0.5, CameraTargetZ = 0, CameraFieldOfView = 38,
            KeyLightIntensity = 2.2, KeyLightYaw = 0.8, KeyLightPitch = 0.9, AmbientLightIntensity = 1.4,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Scenes.Add(scene);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SceneCreated", TargetType = "Scene", TargetId = scene.Id.ToString(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { scene.Name }), CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SceneSummary>.Ok(await DescribeAsync(scene, cancellationToken));
    }

    /// <summary>
    /// Saves the whole scene atomically against the version it was read at.
    /// Everything is validated before anything is written, so a rejected save
    /// leaves the stored scene exactly as it was.
    /// </summary>
    public async Task<RepositoryResult<SceneSummary>> SaveAsync(Guid sceneId, SaveSceneRequest request, CancellationToken cancellationToken)
    {
        var scene = await db.Scenes.SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        if (scene is null) return RepositoryResult<SceneSummary>.NotFound();
        if (scene.Version != request.ExpectedVersion)
            return RepositoryResult<SceneSummary>.Conflict($"This scene is now version {scene.Version}. Reopen it before saving, so newer work is not overwritten.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120) return RepositoryResult<SceneSummary>.Invalid("A scene name must be 1 to 120 characters.");

        var instances = request.Instances ?? [];
        if (instances.Length > MaxInstances)
            return RepositoryResult<SceneSummary>.Invalid($"A scene supports up to {MaxInstances} instances in this build.");
        if (instances.Select(x => x.Id).Distinct().Count() != instances.Length)
            return RepositoryResult<SceneSummary>.Invalid("Each instance needs its own identifier.");

        if (Validate(request.Camera, request.Environment) is { } environmentError)
            return RepositoryResult<SceneSummary>.Invalid(environmentError);

        foreach (var instance in instances)
        {
            if ((instance.Name ?? "").Trim().Length is 0 or > 120)
                return RepositoryResult<SceneSummary>.Invalid("Each instance needs a name of 1 to 120 characters.");
            if (Validate(instance) is { } instanceError) return RepositoryResult<SceneSummary>.Invalid(instanceError);
        }

        // Every modelled instance must point at a model revision this project
        // really holds, so a scene can never cite another project's asset or a
        // picture. Placeholders point at no asset at all.
        var assetIds = instances.Where(x => x.AssetId is not null).Select(x => x.AssetId!.Value).Distinct().ToArray();
        var models = await db.Assets.AsNoTracking()
            .Where(x => assetIds.Contains(x.Id) && x.Kind == nameof(AssetKind.Model))
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (models.Length != assetIds.Length)
            return RepositoryResult<SceneSummary>.Invalid("Every modelled scene instance must reference a model in this project.");

        var existing = await db.SceneInstances.Where(x => x.SceneId == scene.Id).ToListAsync(cancellationToken);

        // A clip binding is checked against the clip's own file and against the
        // rig it would drive, before any of it is written. A mismatched skeleton
        // or an out-of-range trim is refused here rather than at playback.
        foreach (var instance in instances.Where(instance => instance.Clip is not null))
        {
            // Animation readiness is judged against the compiler's skeleton
            // profiles when a clip is first bound to a model revision. Both are
            // immutable, so a binding this scene already holds keeps that verdict:
            // a studio without the compiler (another machine, an imported copy)
            // can still save the scene rather than having it stranded.
            var alreadyBound = existing.Any(record => record.Id == instance.Id && record.AssetId == instance.AssetId
                && record.ClipAssetId == instance.Clip!.ClipAssetId && record.ClipName == instance.Clip.ClipName);
            if (await ValidateClipAsync(instance, alreadyBound, cancellationToken) is { } clipError)
                return RepositoryResult<SceneSummary>.Invalid(clipError);
        }

        var now = timeProvider.GetUtcNow();
        var kept = new HashSet<Guid>();
        var order = 0;
        foreach (var instance in instances)
        {
            var record = existing.SingleOrDefault(x => x.Id == instance.Id);
            if (record is null)
            {
                record = new SceneInstanceRecord
                {
                    Id = instance.Id == Guid.Empty ? Guid.NewGuid() : instance.Id,
                    ProjectId = projectScope.ProjectId, SceneId = scene.Id, Name = "", CreatedAt = now,
                };
                db.SceneInstances.Add(record);
            }
            record.AssetId = instance.AssetId;
            // Exactly one of these describes the object, checked before anything
            // is written, so pinning a model clears the stand-in it replaces.
            record.PlaceholderShape = instance.Placeholder?.Shape;
            record.PlaceholderSizeX = instance.Placeholder?.Size[0] ?? 0;
            record.PlaceholderSizeY = instance.Placeholder?.Size[1] ?? 0;
            record.PlaceholderSizeZ = instance.Placeholder?.Size[2] ?? 0;
            record.Name = instance.Name.Trim();
            record.SortOrder = order++;
            record.PositionX = instance.Position[0]; record.PositionY = instance.Position[1]; record.PositionZ = instance.Position[2];
            record.RotationX = instance.Rotation[0]; record.RotationY = instance.Rotation[1]; record.RotationZ = instance.Rotation[2];
            record.ScaleX = instance.Scale[0]; record.ScaleY = instance.Scale[1]; record.ScaleZ = instance.Scale[2];
            // Playback settings live on the object, which is what lets two
            // objects share one clip and still be trimmed and scrubbed apart.
            record.ClipAssetId = instance.Clip?.ClipAssetId;
            record.ClipName = instance.Clip?.ClipName;
            record.ClipStart = instance.Clip?.Start ?? 0;
            record.ClipEnd = instance.Clip?.End ?? 0;
            record.ClipSpeed = instance.Clip?.Speed ?? 1;
            record.ClipTime = instance.Clip?.Time ?? 0;
            record.ClipLoop = instance.Clip?.Loop ?? false;
            record.ClipRootMotion = instance.Clip?.RootMotion;
            record.MotionAxis = instance.Motion?.Axis;
            record.MotionPivotX = instance.Motion?.Pivot[0] ?? 0;
            record.MotionPivotY = instance.Motion?.Pivot[1] ?? 0;
            record.MotionPivotZ = instance.Motion?.Pivot[2] ?? 0;
            record.MotionFrom = instance.Motion?.FromRadians ?? 0;
            record.MotionTo = instance.Motion?.ToRadians ?? 0;
            record.MotionSeconds = instance.Motion?.Seconds ?? 0;
            record.MotionPingPong = instance.Motion?.PingPong ?? false;
            record.UpdatedAt = now;
            kept.Add(record.Id);
        }

        // Removing an instance removes the placement only. The library asset it
        // was pinned to is untouched.
        foreach (var removed in existing.Where(x => !kept.Contains(x.Id))) db.SceneInstances.Remove(removed);

        scene.Name = name;
        scene.CameraYaw = request.Camera.Yaw; scene.CameraPitch = request.Camera.Pitch; scene.CameraDistance = request.Camera.Distance;
        scene.CameraTargetX = request.Camera.Target[0]; scene.CameraTargetY = request.Camera.Target[1]; scene.CameraTargetZ = request.Camera.Target[2];
        scene.CameraFieldOfView = request.Camera.FieldOfView;
        scene.KeyLightIntensity = request.Environment.KeyIntensity;
        scene.KeyLightYaw = request.Environment.KeyYaw;
        scene.KeyLightPitch = request.Environment.KeyPitch;
        scene.AmbientLightIntensity = request.Environment.AmbientIntensity;
        scene.EnvironmentJson = System.Text.Json.JsonSerializer.Serialize(request.Environment);
        scene.Version += 1;
        scene.UpdatedAt = now;

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SceneSaved", TargetType = "Scene", TargetId = scene.Id.ToString(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { scene.Version, instances = instances.Length }),
            CreatedAt = now,
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return RepositoryResult<SceneSummary>.Conflict(
                "This scene changed while it was being saved. Reopen it before saving again.");
        }
        return RepositoryResult<SceneSummary>.Ok(await DescribeAsync(scene, cancellationToken));
    }

    private async Task<SceneSummary> DescribeAsync(SceneRecord scene, CancellationToken cancellationToken)
    {
        var rows = await db.SceneInstances.AsNoTracking()
            .Where(x => x.SceneId == scene.Id).OrderBy(x => x.SortOrder).Take(MaxInstances).ToArrayAsync(cancellationToken);
        var assetIds = rows.Where(x => x.AssetId is not null).Select(x => x.AssetId!.Value).Distinct().ToArray();
        var models = await db.Assets.AsNoTracking().Where(x => assetIds.Contains(x.Id)).ToArrayAsync(cancellationToken);
        var clipAssetIds = rows.Where(x => x.ClipAssetId is not null).Select(x => x.ClipAssetId!.Value).Distinct().ToArray();
        var clipNames = (await db.Assets.AsNoTracking()
            .Where(x => clipAssetIds.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName }).ToArrayAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => x.DisplayName);

        var instances = new List<SceneInstanceSummary>(rows.Length);
        foreach (var row in rows)
        {
            if (row.PlaceholderShape is { } shape)
            {
                // A placeholder is always drawable: it is the simple geometry
                // itself, not a stand-in for a file that could go missing.
                double[] size = [row.PlaceholderSizeX, row.PlaceholderSizeY, row.PlaceholderSizeZ];
                instances.Add(new SceneInstanceSummary(
                    row.Id, null, row.Name,
                    [row.PositionX, row.PositionY, row.PositionZ],
                    [row.RotationX, row.RotationY, row.RotationZ],
                    [row.ScaleX, row.ScaleY, row.ScaleZ],
                    "Placeholder", 1, null, true, false, size,
                    new ScenePlaceholderSummary(shape, size), row.Role, row.SourcePlanId,
                    null, DescribeMotion(row)));
                continue;
            }

            var asset = models.FirstOrDefault(x => x.Id == row.AssetId);
            // An instance survives its model becoming unavailable. It reports
            // that honestly and keeps its identity, rather than disappearing.
            var available = asset is not null && assets.StoredFileExists(asset.StoragePath);
            var dimensions = available ? await DimensionsAsync(row.AssetId!.Value, cancellationToken) : [0d, 0d, 0d];
            instances.Add(new SceneInstanceSummary(
                row.Id, row.AssetId, row.Name,
                [row.PositionX, row.PositionY, row.PositionZ],
                [row.RotationX, row.RotationY, row.RotationZ],
                [row.ScaleX, row.ScaleY, row.ScaleZ],
                asset?.DisplayName ?? "Unavailable model",
                asset?.RevisionNumber ?? 1,
                available ? $"/api/assets/{row.AssetId}/content" : null,
                available, asset?.IsArchived ?? false, dimensions,
                null, row.Role, row.SourcePlanId,
                DescribeClip(row, clipNames), DescribeMotion(row)));
        }

        return new SceneSummary(
            scene.Id, scene.Name, scene.Version,
            new SceneCameraSummary(scene.CameraYaw, scene.CameraPitch, scene.CameraDistance,
                [scene.CameraTargetX, scene.CameraTargetY, scene.CameraTargetZ], scene.CameraFieldOfView),
            scene.EnvironmentJson is { } lighting
                ? System.Text.Json.JsonSerializer.Deserialize<SceneEnvironmentSummary>(lighting)!
                : new SceneEnvironmentSummary(scene.KeyLightIntensity, scene.KeyLightYaw, scene.KeyLightPitch, scene.AmbientLightIntensity),
            [.. instances], scene.UpdatedAt);
    }

    private static SceneClipBindingSummary? DescribeClip(SceneInstanceRecord row, IReadOnlyDictionary<Guid, string> clipNames)
    {
        if (row.ClipAssetId is not { } clipAssetId || row.ClipName is not { } clipName) return null;
        return new SceneClipBindingSummary(
            clipAssetId, clipName, clipNames.GetValueOrDefault(clipAssetId),
            row.ClipStart, row.ClipEnd, row.ClipSpeed, row.ClipTime, row.ClipLoop,
            row.ClipRootMotion ?? nameof(ClipRootMotion.Hold));
    }

    private static SceneRigidMotionSummary? DescribeMotion(SceneInstanceRecord row) =>
        row.MotionAxis is { } axis
            ? new SceneRigidMotionSummary(
                [row.MotionPivotX, row.MotionPivotY, row.MotionPivotZ],
                axis, row.MotionFrom, row.MotionTo, row.MotionSeconds, row.MotionPingPong)
            : null;

    /// <summary>
    /// A clip may only be bound to an object whose rig it actually fits. The
    /// clip's own file is read for this, so a binding can never outlive the
    /// skeleton it was checked against. Readiness is asked only of a new
    /// binding; the bone match and trim are checked on every save.
    /// </summary>
    private async Task<string?> ValidateClipAsync(SaveSceneInstanceRequest instance, bool alreadyBound, CancellationToken cancellationToken)
    {
        var binding = instance.Clip!;
        if (instance.AssetId is not { } assetId)
            return "A clip needs a model to drive. A placeholder has no skeleton to animate.";
        if (instance.Motion is not null)
            return "An object plays a clip or turns about a pivot, not both at once.";

        var clipSource = await assets.RigAndClipsAsync(binding.ClipAssetId, cancellationToken);
        if (clipSource is null) return "That clip is not a model this project holds.";
        var clip = clipSource.Value.Clips.FirstOrDefault(candidate => candidate.Name == binding.ClipName);
        if (clip is null) return $"That clip has no animation called {binding.ClipName}.";
        if (!clip.Supported) return "That clip is not supported, so it cannot be bound. Read its findings first.";

        var target = await assets.RigAndClipsAsync(assetId, cancellationToken);
        if (target is null) return "That object's model could not be read.";
        var rig = target.Value.Rig;
        if (!rig.HasSkeleton) return "That object has no skeleton, so it cannot play a clip.";
        if (!rig.AnimationReady && !alreadyBound) return "That object's rig is not animation-ready, so it cannot play a clip.";

        // Every bone the clip moves has to exist on the rig it would drive.
        var bones = rig.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var unmatched = clip.TargetBones.Where(bone => !bones.Contains(bone)).ToArray();
        if (unmatched.Length > 0)
            return $"That clip moves bones this object's skeleton does not have, starting with {unmatched[0]}.";

        if (!double.IsFinite(binding.Start) || !double.IsFinite(binding.End) || binding.Start < 0 || binding.End > clip.Duration)
            return $"A clip trim must lie between 0 and {clip.Duration} seconds.";
        if (binding.End <= binding.Start)
            return "A clip trim must end after it starts.";
        if (!double.IsFinite(binding.Speed) || binding.Speed is < 0.1 or > 4)
            return "Clip speed must be between 0.1 and 4.";
        if (!double.IsFinite(binding.Time) || binding.Time < binding.Start || binding.Time > binding.End)
            return "The playback position must lie inside the trimmed clip.";
        if (binding.RootMotion is not (nameof(ClipRootMotion.Hold) or nameof(ClipRootMotion.Offset)))
            return "Root motion must be Hold or Offset.";
        return null;
    }

    private async Task<double[]> DimensionsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var profile = await assets.ModelProfileAsync(assetId, cancellationToken);
        return profile.Value?.Dimensions ?? [0d, 0d, 0d];
    }

    internal static string? Validate(SceneCameraSummary camera, SceneEnvironmentSummary environment)
    {
        if (camera.Target is not { Length: 3 } || camera.Target.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
            return "The camera target must be three finite coordinates inside the supported working volume.";
        if (!double.IsFinite(camera.Yaw) || !double.IsFinite(camera.Pitch)) return "The camera orbit must be finite.";
        if (!double.IsFinite(camera.Distance) || camera.Distance is <= 0 or > MaxDistanceFromOrigin) return "The camera distance must be greater than zero and inside the working volume.";
        if (!double.IsFinite(camera.FieldOfView) || camera.FieldOfView is < 10 or > 120) return "The camera field of view must be between 10 and 120 degrees.";
        foreach (var value in (double[])[environment.KeyIntensity, environment.AmbientIntensity])
            if (!double.IsFinite(value) || value is < 0 or > 20) return "Light intensity must be between 0 and 20.";
        if (!double.IsFinite(environment.KeyYaw) || !double.IsFinite(environment.KeyPitch)) return "The key light direction must be finite.";
        static bool Color(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
        if (!Color(environment.KeyColor) || !Color(environment.AmbientColor) || !Color(environment.BackgroundColor))
            return "Lighting colors must use #RRGGBB.";
        if (!double.IsFinite(environment.Exposure) || environment.Exposure is < 0.1 or > 5)
            return "Exposure must be between 0.1 and 5.";
        var lights = environment.PointLights ?? [];
        if (lights.Any(light => light is null)) return "A local light cannot be empty.";
        if (lights.Length > 12 || lights.Count(light => light.CastShadow) > 2)
            return "A scene supports twelve local lights, with shadows on at most two.";
        if (lights.Any(light => light.Id == Guid.Empty) || lights.Select(light => light.Id).Distinct().Count() != lights.Length)
            return "Each local light needs a distinct identifier.";
        foreach (var light in lights)
        {
            if ((light.Name ?? "").Trim().Length is 0 or > 80 || !Color(light.Color))
                return "Each local light needs a name and #RRGGBB color.";
            if (light.Position is not { Length: 3 } || light.Position.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
                return "Local light positions must be three finite coordinates inside the working volume.";
            if (!double.IsFinite(light.Intensity) || light.Intensity is < 0 or > 3000 || !double.IsFinite(light.Distance) || light.Distance is <= 0 or > 100)
                return "Local light intensity must be 0 to 3000 and reach greater than zero through 100 metres.";
        }
        return null;
    }

    /// <summary>
    /// Every scene object is either a model revision or a placeholder. Both at
    /// once would leave two sources of truth for what is drawn; neither would
    /// leave a transform with nothing under it.
    /// </summary>
    internal static string? ValidatePlaceholder(Guid? assetId, ScenePlaceholderSummary? placeholder)
    {
        if (assetId is not null && placeholder is not null)
            return "A scene object is either a model revision or a placeholder, not both.";
        if (assetId is null && placeholder is null)
            return "Each scene object needs either a model revision or placeholder geometry.";
        if (placeholder is null) return null;
        if (!SupportedShapes.Contains(placeholder.Shape))
            return "Placeholder geometry must be one of " + string.Join(", ", SupportedShapes) + ".";
        if (placeholder.Size is not { Length: 3 } || placeholder.Size.Any(value => !double.IsFinite(value) || value is < 0.01 or > 1000))
            return "Placeholder size must be three finite values between 0.01 and 1000 metres.";
        return null;
    }

    private static string? Validate(SaveSceneInstanceRequest instance)
    {
        if (ValidatePlaceholder(instance.AssetId, instance.Placeholder) is { } placeholderError) return placeholderError;
        if (instance.Motion is { } motion)
        {
            if (instance.Clip is not null) return "An object plays a clip or turns about a pivot, not both at once.";
            if (RigidMotionSampler.Validate(new RigidMotionSampler.Motion(
                motion.Pivot, motion.Axis ?? "", motion.FromRadians, motion.ToRadians, motion.Seconds, motion.PingPong)) is { } motionError)
                return motionError;
        }
        if (instance.Position is not { Length: 3 } || instance.Rotation is not { Length: 3 } || instance.Scale is not { Length: 3 })
            return "Each instance needs a three-axis position, rotation, and scale.";
        if (instance.Position.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
            return "Instance positions must be finite and inside the supported working volume.";
        if (instance.Rotation.Any(value => !double.IsFinite(value)))
            return "Instance rotations must be finite.";
        if (instance.Scale.Any(value => !double.IsFinite(value) || value is < 0.001 or > 1000))
            return "Instance scale must be between 0.001 and 1000 on every axis.";
        return null;
    }
}
