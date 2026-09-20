using System.Numerics;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// How a clip's movement of the root bone reaches the scene.
///
/// Hold keeps the character where the artist put it: the root's translation is
/// dropped from the pose and the object does not move. Offset takes that same
/// translation out of the pose and reports it once, as an offset to the
/// object's own position. Either way the movement is applied exactly once,
/// never both in the pose and again on the object.
/// </summary>
public enum ClipRootMotion
{
    Hold,
    Offset,
}

/// <summary>
/// Samples a clip against a rig at one exact time.
///
/// It is a pure calculation: the same clip, rig, and time give the same numbers
/// every time, and nothing is stored or drawn. Playback in a browser is the
/// same arithmetic run repeatedly; this is the answer everything else is
/// checked against.
/// </summary>
public static class ClipSampler
{
    public sealed record SampleResult(
        bool Ok, string? Error,
        RigPoseCalculator.JointPlacement[] Joints,
        double[] RootOffset, ClipRootMotion RootMotion);

    public static SampleResult Sample(GlbRigProfile rig, GlbClipSummary clip, double time, ClipRootMotion rootMotion)
    {
        if (!rig.HasSkeleton || rig.Bones.Length == 0)
            return new SampleResult(false, "This model has no skeleton to animate.", [], [0, 0, 0], rootMotion);
        if (!clip.Supported)
            return new SampleResult(false, "This clip is not supported, so it is not sampled.", [], [0, 0, 0], rootMotion);
        if (!double.IsFinite(time) || time < 0 || time > clip.Duration)
            return new SampleResult(false, $"A sample time must be between 0 and {clip.Duration} seconds.", [], [0, 0, 0], rootMotion);

        var rootBone = rig.Bones.OrderBy(bone => bone.Depth).First().Name;
        var world = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var placements = new List<RigPoseCalculator.JointPlacement>(rig.Bones.Length);
        double[] rootOffset = [0, 0, 0];

        foreach (var bone in rig.Bones.OrderBy(bone => bone.Depth))
        {
            var translation = new Vector3((float)bone.RestTranslation[0], (float)bone.RestTranslation[1], (float)bone.RestTranslation[2]);
            var rotation = Quaternion.Normalize(new Quaternion(
                (float)bone.RestRotation[0], (float)bone.RestRotation[1], (float)bone.RestRotation[2], (float)bone.RestRotation[3]));
            if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.W)) rotation = Quaternion.Identity;
            var scale = new Vector3((float)bone.RestScale[0], (float)bone.RestScale[1], (float)bone.RestScale[2]);

            foreach (var channel in clip.Channels.Where(channel => channel.Bone == bone.Name))
            {
                var value = ValueAt(channel, time);
                if (value is null) continue;
                switch (channel.Path)
                {
                    case "translation":
                        var moved = new Vector3((float)value[0], (float)value[1], (float)value[2]);
                        if (bone.Name == rootBone)
                        {
                            // The root's movement never reaches both the pose and
                            // the object. It is taken out here, and reported once.
                            var travel = moved - translation;
                            if (rootMotion == ClipRootMotion.Offset)
                                rootOffset = [Round(travel.X), Round(travel.Y), Round(travel.Z)];
                            break;
                        }
                        translation = moved;
                        break;
                    case "rotation":
                        rotation = Quaternion.Normalize(new Quaternion(
                            (float)value[0], (float)value[1], (float)value[2], (float)value[3]));
                        break;
                    case "scale":
                        scale = new Vector3((float)value[0], (float)value[1], (float)value[2]);
                        break;
                }
            }

            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            var placed = bone.Parent is { } parent && world.TryGetValue(parent, out var parentWorld) ? local * parentWorld : local;
            world[bone.Name] = placed;

            var position = placed.Translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return new SampleResult(false, "This clip puts a bone somewhere that is not a finite position.", [], [0, 0, 0], rootMotion);
            placements.Add(new RigPoseCalculator.JointPlacement(
                bone.Name, bone.Parent,
                [Round(position.X), Round(position.Y), Round(position.Z)],
                bone.RestWorldPosition));
        }

        return new SampleResult(true, null, [.. placements], rootOffset, rootMotion);
    }

    /// <summary>
    /// The channel's value at one time. Before the first keyframe and after the
    /// last it holds, which is what glTF specifies, so a trimmed clip never
    /// extrapolates into motion nobody authored.
    /// </summary>
    private static double[]? ValueAt(GlbClipChannel channel, double time)
    {
        if (channel.Times.Length == 0) return null;
        if (time <= channel.Times[0]) return channel.Values[0];
        if (time >= channel.Times[^1]) return channel.Values[^1];

        var next = 1;
        while (next < channel.Times.Length && channel.Times[next] < time) next++;
        var previous = next - 1;
        if (channel.Interpolation == "STEP") return channel.Values[previous];

        var span = channel.Times[next] - channel.Times[previous];
        var ratio = span <= 0 ? 0 : (time - channel.Times[previous]) / span;
        var from = channel.Values[previous];
        var to = channel.Values[next];

        if (channel.Path == "rotation")
        {
            // Quaternions are interpolated as rotations rather than as four
            // numbers, so a half-way sample is a half-way turn.
            var a = Quaternion.Normalize(new Quaternion((float)from[0], (float)from[1], (float)from[2], (float)from[3]));
            var b = Quaternion.Normalize(new Quaternion((float)to[0], (float)to[1], (float)to[2], (float)to[3]));
            var blended = Quaternion.Slerp(a, b, (float)ratio);
            return [blended.X, blended.Y, blended.Z, blended.W];
        }

        var mixed = new double[from.Length];
        for (var part = 0; part < from.Length; part++) mixed[part] = from[part] + (to[part] - from[part]) * ratio;
        return mixed;
    }

    private static double Round(float value) => Math.Round(value, 4);
}
