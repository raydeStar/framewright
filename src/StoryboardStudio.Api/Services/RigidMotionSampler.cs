using System.Numerics;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// A door on its hinge, a lantern on its hook: one rigid part turning about a
/// declared pivot, with no skeleton anywhere near it.
///
/// The pivot is a point in the object's own local space, so the hinge stays put
/// while the rest of the object swings around it. Like every other sampler
/// here, this is pure arithmetic: it stores nothing, and a static prop with no
/// declared motion is never touched by it.
/// </summary>
public static class RigidMotionSampler
{
    public const double MaxSeconds = 600;
    public static readonly string[] Axes = ["X", "Y", "Z"];

    public sealed record Motion(double[] Pivot, string Axis, double FromRadians, double ToRadians, double Seconds, bool PingPong);

    public sealed record SampleResult(bool Ok, string? Error, double[] Position, double[] Rotation, double Angle);

    /// <summary>
    /// Where the part sits at one time, given where the artist placed it.
    /// The declared pivot is the one point the motion leaves exactly where it is.
    /// </summary>
    public static SampleResult Sample(Motion motion, double[] position, double[] rotation, double time)
    {
        if (Validate(motion) is { } error) return new SampleResult(false, error, [], [], 0);
        if (!double.IsFinite(time) || time < 0 || time > motion.Seconds)
            return new SampleResult(false, $"A sample time must be between 0 and {motion.Seconds} seconds.", [], [], 0);
        if (position is not { Length: 3 } || rotation is not { Length: 3 })
            return new SampleResult(false, "This object has no three-axis placement to move from.", [], [], 0);

        var ratio = motion.Seconds <= 0 ? 0 : time / motion.Seconds;
        // Ping-pong runs out and back inside the same duration, so a lantern
        // swings rather than snapping back to where it started.
        if (motion.PingPong) ratio = ratio <= 0.5 ? ratio * 2 : (1 - ratio) * 2;
        var angle = motion.FromRadians + (motion.ToRadians - motion.FromRadians) * ratio;

        var axis = motion.Axis switch
        {
            "X" => Vector3.UnitX,
            "Y" => Vector3.UnitY,
            _ => Vector3.UnitZ,
        };
        var turn = Matrix4x4.CreateFromAxisAngle(axis, (float)angle);
        var pivot = new Vector3((float)motion.Pivot[0], (float)motion.Pivot[1], (float)motion.Pivot[2]);
        // Turning about a pivot is a turn about the origin plus the offset that
        // puts the pivot back where it was.
        var correction = pivot - Vector3.Transform(pivot, turn);

        var placed = new Vector3((float)position[0], (float)position[1], (float)position[2]) + correction;
        if (!float.IsFinite(placed.X) || !float.IsFinite(placed.Y) || !float.IsFinite(placed.Z))
            return new SampleResult(false, "This motion puts the object somewhere that is not a finite position.", [], [], 0);

        var turned = (double[])[rotation[0], rotation[1], rotation[2]];
        var index = motion.Axis switch { "X" => 0, "Y" => 1, _ => 2 };
        turned[index] += angle;

        return new SampleResult(true, null,
            [Math.Round(placed.X, 4), Math.Round(placed.Y, 4), Math.Round(placed.Z, 4)],
            [Math.Round(turned[0], 4), Math.Round(turned[1], 4), Math.Round(turned[2], 4)],
            Math.Round(angle, 4));
    }

    public static string? Validate(Motion motion)
    {
        if (motion.Pivot is not { Length: 3 } || motion.Pivot.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10_000))
            return "A pivot must be three finite coordinates inside the supported working volume.";
        if (!Axes.Contains(motion.Axis))
            return "A rigid part turns about X, Y, or Z.";
        foreach (var angle in (double[])[motion.FromRadians, motion.ToRadians])
            if (!double.IsFinite(angle) || Math.Abs(angle) > 6.2832)
                return "A rigid part's angles must be finite and within one full turn.";
        if (motion.FromRadians == motion.ToRadians)
            return "A rigid part that ends where it started is not motion. Leave it static instead.";
        if (!double.IsFinite(motion.Seconds) || motion.Seconds is <= 0 or > MaxSeconds)
            return $"A rigid part's duration must be greater than zero and no more than {MaxSeconds:N0} seconds.";
        return null;
    }
}
