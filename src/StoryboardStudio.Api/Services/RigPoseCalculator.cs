using System.Numerics;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Where a rig's bones land when named bones are rotated away from their rest
/// pose.
///
/// This is a pure calculation over the skeleton the inspector read: the same
/// rig and the same pose give the same numbers every time, and nothing is
/// stored or drawn. It exists so a pose can be checked before any of it is
/// trusted to a renderer or a generated animation.
/// </summary>
public static class RigPoseCalculator
{
    public const double MaxRotationRadians = 6.2832;

    /// <summary>A rotation in radians applied to one named bone, on top of its rest pose.</summary>
    public sealed record BonePose(string Bone, double[] Rotation);

    public sealed record JointPlacement(string Bone, string? Parent, double[] Position, double[] RestPosition);

    public sealed record PoseResult(bool Ok, string? Error, JointPlacement[] Joints);

    public static PoseResult Apply(GlbRigProfile rig, IReadOnlyList<BonePose> pose)
    {
        if (!rig.HasSkeleton) return new PoseResult(false, "This model has no skeleton to pose.", []);
        if (rig.Bones.Length == 0) return new PoseResult(false, "This model's skeleton could not be read.", []);

        var rotations = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        foreach (var bone in pose)
        {
            var name = (bone.Bone ?? "").Trim();
            if (rig.Bones.All(known => known.Name != name))
                return new PoseResult(false, $"This rig has no bone called {name}.", []);
            if (bone.Rotation is not { Length: 3 } || bone.Rotation.Any(value => !double.IsFinite(value) || Math.Abs(value) > MaxRotationRadians))
                return new PoseResult(false, "Each posed bone needs three finite rotations within one full turn.", []);
            if (rotations.ContainsKey(name))
                return new PoseResult(false, $"The pose names {name} more than once.", []);
            // Intrinsic X, then Y, then Z, which is the order the rest of the
            // studio states rotations in.
            rotations[name] = Quaternion.CreateFromYawPitchRoll(
                (float)bone.Rotation[1], (float)bone.Rotation[0], (float)bone.Rotation[2]);
        }

        var world = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var placements = new List<JointPlacement>(rig.Bones.Length);
        // Bones arrive parents first, so a parent's world transform is always
        // known by the time its children are placed.
        foreach (var bone in rig.Bones.OrderBy(bone => bone.Depth))
        {
            var rest = Quaternion.Normalize(new Quaternion(
                (float)bone.RestRotation[0], (float)bone.RestRotation[1], (float)bone.RestRotation[2], (float)bone.RestRotation[3]));
            if (!float.IsFinite(rest.X) || !float.IsFinite(rest.Y) || !float.IsFinite(rest.Z) || !float.IsFinite(rest.W))
                rest = Quaternion.Identity;
            var posed = rotations.TryGetValue(bone.Name, out var extra) ? extra * rest : rest;

            var local =
                Matrix4x4.CreateScale((float)bone.RestScale[0], (float)bone.RestScale[1], (float)bone.RestScale[2])
                * Matrix4x4.CreateFromQuaternion(posed)
                * Matrix4x4.CreateTranslation((float)bone.RestTranslation[0], (float)bone.RestTranslation[1], (float)bone.RestTranslation[2]);
            var placed = bone.Parent is { } parent && world.TryGetValue(parent, out var parentWorld) ? local * parentWorld : local;
            world[bone.Name] = placed;

            var position = placed.Translation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return new PoseResult(false, "This pose puts a bone somewhere that is not a finite position.", []);
            placements.Add(new JointPlacement(
                bone.Name, bone.Parent,
                [Round(position.X), Round(position.Y), Round(position.Z)],
                bone.RestWorldPosition));
        }

        return new PoseResult(true, null, [.. placements]);
    }

    private static double Round(float value) => Math.Round(value, 4);
}
