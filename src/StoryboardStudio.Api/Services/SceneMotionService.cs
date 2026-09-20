using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Where one scene object sits at one exact time.
///
/// This is the answer a browser's playback is checked against: it is calculated
/// from the stored files every time, stores nothing, and draws nothing, so
/// scrubbing to a known time has one correct result rather than whatever a
/// renderer happened to accumulate. A clip's movement of the root reaches the
/// scene exactly once, under the object's own stated policy.
/// </summary>
public sealed class SceneMotionService(Persistence.StudioDbContext db, AssetStore assets)
{
    public async Task<RepositoryResult<SceneMotionSampleSummary>> SampleAsync(
        Guid sceneId, Guid instanceId, double time, CancellationToken cancellationToken)
    {
        var scene = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sceneId, cancellationToken);
        if (scene is null) return RepositoryResult<SceneMotionSampleSummary>.NotFound();
        var instance = await db.SceneInstances.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == instanceId && x.SceneId == sceneId, cancellationToken);
        if (instance is null) return RepositoryResult<SceneMotionSampleSummary>.NotFound();

        double[] position = [instance.PositionX, instance.PositionY, instance.PositionZ];
        double[] rotation = [instance.RotationX, instance.RotationY, instance.RotationZ];

        if (instance.MotionAxis is { } axis)
        {
            var motion = new RigidMotionSampler.Motion(
                [instance.MotionPivotX, instance.MotionPivotY, instance.MotionPivotZ],
                axis, instance.MotionFrom, instance.MotionTo, instance.MotionSeconds, instance.MotionPingPong);
            var swung = RigidMotionSampler.Sample(motion, position, rotation, time);
            if (!swung.Ok) return RepositoryResult<SceneMotionSampleSummary>.Invalid(swung.Error!);
            return RepositoryResult<SceneMotionSampleSummary>.Ok(new SceneMotionSampleSummary(
                sceneId, instanceId, instance.Name, Math.Round(time, 4), "RigidPart",
                [], [0, 0, 0], nameof(ClipRootMotion.Hold),
                swung.Position, swung.Rotation, swung.Angle));
        }

        if (instance.ClipAssetId is not { } clipAssetId || instance.ClipName is not { } clipName)
        {
            // A static prop is not an error: it simply sits where it was put,
            // at every time anyone asks about.
            if (!double.IsFinite(time) || time < 0)
                return RepositoryResult<SceneMotionSampleSummary>.Invalid("A sample time must be zero or more.");
            return RepositoryResult<SceneMotionSampleSummary>.Ok(new SceneMotionSampleSummary(
                sceneId, instanceId, instance.Name, Math.Round(time, 4), "Static",
                [], [0, 0, 0], nameof(ClipRootMotion.Hold), position, rotation, 0));
        }

        var clipSource = await assets.RigAndClipsAsync(clipAssetId, cancellationToken);
        if (clipSource is null) return RepositoryResult<SceneMotionSampleSummary>.Unavailable("The clip this object plays could not be read.");
        var clip = clipSource.Value.Clips.FirstOrDefault(candidate => candidate.Name == clipName);
        if (clip is null) return RepositoryResult<SceneMotionSampleSummary>.Invalid($"That clip no longer has an animation called {clipName}.");

        if (instance.AssetId is not { } assetId) return RepositoryResult<SceneMotionSampleSummary>.Invalid("This object has no model to animate.");
        var target = await assets.RigAndClipsAsync(assetId, cancellationToken);
        if (target is null) return RepositoryResult<SceneMotionSampleSummary>.Unavailable("This object's model could not be read.");

        // Playback time runs inside the trim at this object's own speed, so two
        // objects sharing one clip are at different places in it.
        var trimmed = instance.ClipEnd > instance.ClipStart ? instance.ClipEnd - instance.ClipStart : clip.Duration;
        if (!double.IsFinite(time) || time < 0)
            return RepositoryResult<SceneMotionSampleSummary>.Invalid("A sample time must be zero or more.");
        var elapsed = time * (instance.ClipSpeed <= 0 ? 1 : instance.ClipSpeed);
        if (elapsed > trimmed)
        {
            if (!instance.ClipLoop)
                return RepositoryResult<SceneMotionSampleSummary>.Invalid($"This object's clip runs for {Math.Round(trimmed / (instance.ClipSpeed <= 0 ? 1 : instance.ClipSpeed), 4)} seconds at its own speed.");
            elapsed %= trimmed;
        }

        var sampled = ClipSampler.Sample(
            target.Value.Rig, clip, instance.ClipStart + elapsed,
            instance.ClipRootMotion == nameof(ClipRootMotion.Offset) ? ClipRootMotion.Offset : ClipRootMotion.Hold);
        if (!sampled.Ok) return RepositoryResult<SceneMotionSampleSummary>.Invalid(sampled.Error!);

        // The root's movement is applied exactly once: it is already out of the
        // pose, so it is added to the object's position here and nowhere else.
        double[] placed = [
            position[0] + sampled.RootOffset[0],
            position[1] + sampled.RootOffset[1],
            position[2] + sampled.RootOffset[2]];

        return RepositoryResult<SceneMotionSampleSummary>.Ok(new SceneMotionSampleSummary(
            sceneId, instanceId, instance.Name, Math.Round(time, 4), "Character",
            [.. sampled.Joints.Select(joint => new RigJointPlacementSummary(joint.Bone, joint.Parent, joint.Position, joint.RestPosition))],
            sampled.RootOffset, sampled.RootMotion.ToString(),
            placed, rotation, 0));
    }
}
