using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// The identity of one exact skeleton, computed the same way the Reference
/// Asset Compiler computes it.
///
/// A clip is only honestly bindable to the skeleton it was authored against.
/// For a rig that matches a documented profile the profile carries that
/// promise; for a rig that matches none — a creature, a machine — the skeleton
/// itself is the only identity there is, and this is it. A matching fingerprint
/// means these clips were made for this skeleton. It says nothing about
/// retargeting.
///
/// Two programs in two languages have to reach the same hex string from the
/// same skeleton, so every step that could differ between them is pinned by
/// the compiler's `docs/BROWSER_STUDIO_CONTRACT.md` rather than left to a
/// language default. The worked example in that document is asserted by a test
/// in both repositories; when they disagree, compare the canonical text rather
/// than the hashes, because the text says which field drifted.
/// </summary>
public static class SkeletonFingerprint
{
    /// <summary>A change in how this is computed is a new prefix, never a quiet change of meaning.</summary>
    public const string Algorithm = "rac-skeleton-v1";

    private const double Quantization = 1_000_000;

    /// <summary>
    /// The field and line separators. A bone name carrying one could shift the
    /// fields so two different skeletons canonicalize to the same text, so such
    /// a name is refused rather than escaped: an escaping scheme is one more
    /// thing each language has to get right.
    /// </summary>
    private static readonly char[] ForbiddenInNames = ['|', '\n', '\r'];

    public sealed record Joint(string Name, string? Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale);

    public sealed record Result(bool Ok, string? Error, string? Fingerprint, string? CanonicalText);

    public static Result Compute(IReadOnlyList<Joint> joints)
    {
        if (joints.Count == 0) return new Result(false, "A skeleton with no joints has no fingerprint.", null, null);

        var prepared = new List<(string Name, string Parent, Vector3 Translation, Quaternion Rotation, Vector3 Scale)>(joints.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var joint in joints)
        {
            if (string.IsNullOrEmpty(joint.Name)) return Failure("Every joint needs a name.");
            if (!seen.Add(joint.Name)) return Failure($"Joint {joint.Name} appears more than once.");
            if (joint.Name.IndexOfAny(ForbiddenInNames) >= 0)
                return Failure("A joint name cannot contain a field or line separator.");

            var parent = joint.Parent ?? "";
            if (parent.IndexOfAny(ForbiddenInNames) >= 0)
                return Failure("A parent name cannot contain a field or line separator.");

            if (!Finite(joint.Translation) || !Finite(joint.Scale)) return Failure("A skeleton value must be a finite number.");
            if (Canonical(joint.Rotation) is not { } rotation) return Failure("A rest rotation must be a finite, non-zero quaternion.");

            prepared.Add((joint.Name, parent, joint.Translation, rotation, joint.Scale));
        }

        // Ordinal order: the UTF-8 bytes, never a culture's idea of order.
        prepared.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

        var builder = new StringBuilder().Append(Algorithm).Append('\n');
        foreach (var (name, parent, translation, rotation, scale) in prepared)
        {
            builder.Append(name).Append('|').Append(parent).Append('|')
                .Append(Quantize(translation.X)).Append(',').Append(Quantize(translation.Y)).Append(',').Append(Quantize(translation.Z)).Append('|')
                .Append(Quantize(rotation.X)).Append(',').Append(Quantize(rotation.Y)).Append(',').Append(Quantize(rotation.Z)).Append(',').Append(Quantize(rotation.W)).Append('|')
                .Append(Quantize(scale.X)).Append(',').Append(Quantize(scale.Y)).Append(',').Append(Quantize(scale.Z)).Append('\n');
        }

        var text = builder.ToString();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new Result(true, null, digest, text);
    }

    /// <summary>
    /// Round half away from zero at six decimal places, written as an integer.
    /// Stated explicitly because .NET's <c>ToString("F6")</c> rounds halfway
    /// values away from zero while Python's <c>format(x, '.6f')</c> rounds them
    /// to even; neither default is used, so both sides can agree.
    /// </summary>
    internal static string Quantize(double value)
    {
        var scaled = value * Quantization;
        var rounded = scaled >= 0 ? Math.Floor(scaled + 0.5) : Math.Ceiling(scaled - 0.5);
        // Negative zero is the same place as zero and has to read the same.
        if (rounded == 0) rounded = 0;
        return ((long)rounded).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Normalize, then fix the sign so w is not negative: a quaternion and its
    /// negation are the same rotation, and without this one skeleton produces
    /// two fingerprints.
    /// </summary>
    internal static Quaternion? Canonical(Quaternion rotation)
    {
        if (!double.IsFinite(rotation.X) || !double.IsFinite(rotation.Y)
            || !double.IsFinite(rotation.Z) || !double.IsFinite(rotation.W)) return null;
        var length = Math.Sqrt((double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
            + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W);
        if (length <= 0) return null;

        var canonical = new Quaternion(
            (float)(rotation.X / length), (float)(rotation.Y / length),
            (float)(rotation.Z / length), (float)(rotation.W / length));
        return canonical.W < 0
            ? new Quaternion(-canonical.X, -canonical.Y, -canonical.Z, -canonical.W)
            : canonical;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Result Failure(string error) => new(false, error, null, null);
}
