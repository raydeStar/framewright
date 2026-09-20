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
    private const double MaxDistanceFromOrigin = 10_000;

    public async Task<IReadOnlyList<SceneListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var scenes = await db.Scenes.AsNoTracking().Take(200).ToArrayAsync(cancellationToken);
        var counts = await db.SceneInstances.AsNoTracking()
            .GroupBy(x => x.SceneId)
            .Select(group => new { SceneId = group.Key, Count = group.Count() })
            .ToArrayAsync(cancellationToken);
        // SQLite cannot order DateTimeOffset, so the bounded set is ordered here.
        return [.. scenes
            .OrderByDescending(scene => scene.UpdatedAt)
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

        // Every instance must point at a model revision this project really
        // holds, so a scene can never cite another project's asset or a picture.
        var assetIds = instances.Select(x => x.AssetId).Distinct().ToArray();
        var models = await db.Assets.AsNoTracking()
            .Where(x => assetIds.Contains(x.Id) && x.Kind == nameof(AssetKind.Model))
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (models.Length != assetIds.Length)
            return RepositoryResult<SceneSummary>.Invalid("Every scene instance must reference a model in this project.");

        var now = timeProvider.GetUtcNow();
        var existing = await db.SceneInstances.Where(x => x.SceneId == scene.Id).ToListAsync(cancellationToken);
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
            record.Name = instance.Name.Trim();
            record.SortOrder = order++;
            record.PositionX = instance.Position[0]; record.PositionY = instance.Position[1]; record.PositionZ = instance.Position[2];
            record.RotationX = instance.Rotation[0]; record.RotationY = instance.Rotation[1]; record.RotationZ = instance.Rotation[2];
            record.ScaleX = instance.Scale[0]; record.ScaleY = instance.Scale[1]; record.ScaleZ = instance.Scale[2];
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
        scene.Version += 1;
        scene.UpdatedAt = now;

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SceneSaved", TargetType = "Scene", TargetId = scene.Id.ToString(),
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { scene.Version, instances = instances.Length }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SceneSummary>.Ok(await DescribeAsync(scene, cancellationToken));
    }

    private async Task<SceneSummary> DescribeAsync(SceneRecord scene, CancellationToken cancellationToken)
    {
        var rows = await db.SceneInstances.AsNoTracking()
            .Where(x => x.SceneId == scene.Id).OrderBy(x => x.SortOrder).Take(MaxInstances).ToArrayAsync(cancellationToken);
        var assetIds = rows.Select(x => x.AssetId).Distinct().ToArray();
        var models = await db.Assets.AsNoTracking().Where(x => assetIds.Contains(x.Id)).ToArrayAsync(cancellationToken);

        var instances = new List<SceneInstanceSummary>(rows.Length);
        foreach (var row in rows)
        {
            var asset = models.FirstOrDefault(x => x.Id == row.AssetId);
            // An instance survives its model becoming unavailable. It reports
            // that honestly and keeps its identity, rather than disappearing.
            var available = asset is not null && assets.StoredFileExists(asset.StoragePath);
            var dimensions = available ? await DimensionsAsync(row.AssetId, cancellationToken) : [0d, 0d, 0d];
            instances.Add(new SceneInstanceSummary(
                row.Id, row.AssetId, row.Name,
                [row.PositionX, row.PositionY, row.PositionZ],
                [row.RotationX, row.RotationY, row.RotationZ],
                [row.ScaleX, row.ScaleY, row.ScaleZ],
                asset?.DisplayName ?? "Unavailable model",
                asset?.RevisionNumber ?? 1,
                available ? $"/api/assets/{row.AssetId}/content" : null,
                available, asset?.IsArchived ?? false, dimensions));
        }

        return new SceneSummary(
            scene.Id, scene.Name, scene.Version,
            new SceneCameraSummary(scene.CameraYaw, scene.CameraPitch, scene.CameraDistance,
                [scene.CameraTargetX, scene.CameraTargetY, scene.CameraTargetZ], scene.CameraFieldOfView),
            new SceneEnvironmentSummary(scene.KeyLightIntensity, scene.KeyLightYaw, scene.KeyLightPitch, scene.AmbientLightIntensity),
            [.. instances], scene.UpdatedAt);
    }

    private async Task<double[]> DimensionsAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var profile = await assets.ModelProfileAsync(assetId, cancellationToken);
        return profile.Value?.Dimensions ?? [0d, 0d, 0d];
    }

    private static string? Validate(SceneCameraSummary camera, SceneEnvironmentSummary environment)
    {
        if (camera.Target is not { Length: 3 } || camera.Target.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxDistanceFromOrigin))
            return "The camera target must be three finite coordinates inside the supported working volume.";
        if (!double.IsFinite(camera.Yaw) || !double.IsFinite(camera.Pitch)) return "The camera orbit must be finite.";
        if (!double.IsFinite(camera.Distance) || camera.Distance is <= 0 or > MaxDistanceFromOrigin) return "The camera distance must be greater than zero and inside the working volume.";
        if (!double.IsFinite(camera.FieldOfView) || camera.FieldOfView is < 10 or > 120) return "The camera field of view must be between 10 and 120 degrees.";
        foreach (var value in (double[])[environment.KeyIntensity, environment.AmbientIntensity])
            if (!double.IsFinite(value) || value is < 0 or > 20) return "Light intensity must be between 0 and 20.";
        if (!double.IsFinite(environment.KeyYaw) || !double.IsFinite(environment.KeyPitch)) return "The key light direction must be finite.";
        return null;
    }

    private static string? Validate(SaveSceneInstanceRequest instance)
    {
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
