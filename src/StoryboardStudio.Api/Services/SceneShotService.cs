using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Freezes an editable scene into the ordinary shot review path. Rendering the
/// pixels happens in the scene's real browser renderer; this service verifies
/// the saved inputs, delivery canvas, and image before it advances the shot.
/// </summary>
public sealed class SceneShotService(
    StudioDbContext db,
    SceneService scenes,
    AssetStore assets,
    IProjectScope projectScope,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<IReadOnlyList<SceneShotBindingSummary>> ListAsync(
        Guid sceneId,
        CancellationToken cancellationToken)
    {
        var rows = await db.SceneShotBindings.AsNoTracking()
            .Where(x => x.SceneId == sceneId)
            .ToArrayAsync(cancellationToken);
        return rows.OrderByDescending(x => x.CreatedAt).Take(50).Select(Map).ToArray();
    }

    public async Task<RepositoryResult<SceneShotBindingSummary>> RenderStillAsync(
        Guid sceneId,
        Guid shotId,
        int expectedSceneVersion,
        int expectedShotVersion,
        SceneCameraSummary camera,
        double startTime,
        double endTime,
        double stillTime,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > AssetStore.MaxImageBytes)
            return RepositoryResult<SceneShotBindingSummary>.Invalid("The rendered still is empty or exceeds the image limit.");
        if (ValidateCamera(camera) is { } cameraError)
            return RepositoryResult<SceneShotBindingSummary>.Invalid(cameraError);
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || !double.IsFinite(stillTime) ||
            startTime < 0 || endTime <= startTime || endTime > 3_600 || stillTime < startTime || stillTime > endTime)
            return RepositoryResult<SceneShotBindingSummary>.Invalid(
                "Scene timing must be finite, ordered, under one hour, and the still time must lie inside the shot range.");

        var sceneResult = await scenes.GetAsync(sceneId, cancellationToken);
        if (sceneResult.Kind == RepositoryResultKind.NotFound || sceneResult.Value is null)
            return RepositoryResult<SceneShotBindingSummary>.NotFound();
        var scene = sceneResult.Value;
        if (scene.Version != expectedSceneVersion)
            return RepositoryResult<SceneShotBindingSummary>.Conflict(
                $"The scene changed from version {expectedSceneVersion} to {scene.Version}. Reopen it before rendering.");
        if (scene.Instances.Length == 0)
            return RepositoryResult<SceneShotBindingSummary>.Invalid("Place at least one object before rendering a scene still.");

        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<SceneShotBindingSummary>.NotFound();
        if (shot.Version != expectedShotVersion)
            return RepositoryResult<SceneShotBindingSummary>.Conflict(
                $"The shot changed from version {expectedShotVersion} to {shot.Version}. Refresh before rendering.");
        var project = await db.Projects.AsNoTracking()
            .SingleAsync(x => x.Id == projectScope.ProjectId, cancellationToken);
        var expectedFrames = (endTime - startTime) * project.FramesPerSecond;
        if (Math.Abs(expectedFrames - shot.DurationFrames) > 0.5)
            return RepositoryResult<SceneShotBindingSummary>.Invalid(
                $"The selected scene range is {expectedFrames:0.##} frames, but {shot.Code} is {shot.DurationFrames} frames at {project.FramesPerSecond} fps.");

        var snapshot = new
        {
            scene,
            shot = new { shot.Id, shot.Code, sourceVersion = shot.Version, shot.DurationFrames },
            camera,
            timing = new { startTime, endTime, stillTime },
            delivery = new
            {
                project.DeliveryWidth,
                project.DeliveryHeight,
                project.FramesPerSecond,
                project.ColorSpace,
                project.AudioSampleRate,
            },
        };
        var snapshotJson = JsonSerializer.Serialize(snapshot, Json);
        var snapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var input = file.OpenReadStream();
        var imported = await assets.ImportSceneStillAsync(
            input,
            $"{shot.Code.ToLowerInvariant()}-scene-v{scene.Version}-{stillTime:0.###}.png",
            file.Length,
            cancellationToken);
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RepositoryResult<SceneShotBindingSummary>(null, imported.Kind, imported.Error);
        }
        if (imported.Value.Width != project.DeliveryWidth || imported.Value.Height != project.DeliveryHeight)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RepositoryResult<SceneShotBindingSummary>.Invalid(
                $"The rendered still must be exactly {project.DeliveryWidth} × {project.DeliveryHeight}; received {imported.Value.Width} × {imported.Value.Height}.");
        }

        // Recheck both version gates after the file has been validated. This is
        // the write boundary; a scene or shot that moved during upload wins.
        var currentSceneVersion = await db.Scenes.Where(x => x.Id == sceneId).Select(x => x.Version).SingleAsync(cancellationToken);
        var currentShotVersion = await db.Shots.Where(x => x.Id == shotId).Select(x => x.Version).SingleAsync(cancellationToken);
        if (currentSceneVersion != expectedSceneVersion || currentShotVersion != expectedShotVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RepositoryResult<SceneShotBindingSummary>.Conflict(
                "The scene or shot changed while the still was being imported. Nothing was attached; render the current state again.");
        }

        var now = timeProvider.GetUtcNow();
        var nextVersion = shot.Version + 1;
        var currentCandidates = await db.CandidateVersions
            .Where(x => x.ShotId == shot.Id && x.IsCurrent)
            .ToArrayAsync(cancellationToken);
        foreach (var current in currentCandidates)
        {
            current.IsCurrent = false;
            current.SupersededAt = now;
        }
        var candidate = new CandidateVersionRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            ShotId = shot.Id,
            Version = nextVersion,
            Stage = nameof(ShotStage.Draft),
            Approval = nameof(ApprovalState.Working),
            IsCurrent = true,
            AssetId = imported.Value.Id,
            CreatedAt = now,
        };
        db.CandidateVersions.Add(candidate);

        shot.Version = nextVersion;
        shot.Stage = nameof(ShotStage.Draft);
        shot.Approval = nameof(ApprovalState.Working);
        shot.CurrentAssetId = imported.Value.Id;
        shot.Camera = $"Scene camera · {scene.Name} · {stillTime:0.###} s";
        shot.ContinuityState = "Review";
        shot.ProductionVideoJobId = null;
        shot.ProductionVideoAssetId = null;
        shot.UpdatedAt = now;

        var binding = new SceneShotBindingRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            SceneId = scene.Id,
            SceneName = scene.Name,
            SceneVersion = scene.Version,
            ShotId = shot.Id,
            ShotCode = shot.Code,
            ShotVersion = nextVersion,
            CameraJson = JsonSerializer.Serialize(camera, Json),
            StartTime = startTime,
            EndTime = endTime,
            StillTime = stillTime,
            DeliveryWidth = project.DeliveryWidth,
            DeliveryHeight = project.DeliveryHeight,
            FramesPerSecond = project.FramesPerSecond,
            ColorSpace = project.ColorSpace,
            SnapshotJson = snapshotJson,
            SnapshotHash = snapshotHash,
            StillAssetId = imported.Value.Id,
            CreatedAt = now,
        };
        db.SceneShotBindings.Add(binding);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Type = "SceneStillBound",
            TargetType = "Shot",
            TargetId = shot.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                binding.Id,
                binding.SceneId,
                binding.SceneVersion,
                binding.ShotVersion,
                binding.StillAssetId,
                binding.SnapshotHash,
            }, Json),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RepositoryResult<SceneShotBindingSummary>.Ok(Map(binding));
    }

    private static string? ValidateCamera(SceneCameraSummary camera)
    {
        if (camera.Target is not { Length: 3 } || camera.Target.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10_000))
            return "The shot camera target must be three finite coordinates inside the supported working volume.";
        if (!double.IsFinite(camera.Yaw) || !double.IsFinite(camera.Pitch))
            return "The shot camera orbit must be finite.";
        if (!double.IsFinite(camera.Distance) || camera.Distance is <= 0 or > 10_000)
            return "The shot camera distance must be greater than zero and inside the working volume.";
        if (!double.IsFinite(camera.FieldOfView) || camera.FieldOfView is < 10 or > 120)
            return "The shot camera field of view must be between 10 and 120 degrees.";
        return null;
    }

    private static SceneShotBindingSummary Map(SceneShotBindingRecord row) => new(
        row.Id,
        row.SceneId,
        row.SceneName,
        row.SceneVersion,
        row.ShotId,
        row.ShotCode,
        row.ShotVersion,
        JsonSerializer.Deserialize<SceneCameraSummary>(row.CameraJson, Json)
            ?? throw new InvalidDataException("A stored scene shot camera could not be read."),
        row.StartTime,
        row.EndTime,
        row.StillTime,
        row.DeliveryWidth,
        row.DeliveryHeight,
        row.FramesPerSecond,
        row.ColorSpace,
        row.SnapshotHash,
        row.StillAssetId,
        $"/api/assets/{row.StillAssetId}/content",
        row.CreatedAt);
}
