using System.Numerics;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// One documented body class. A rig is only ever described as matching a
/// profile this build actually knows; anything else is reported as an unknown
/// skeleton, never as a nearly-right humanoid.
/// </summary>
public sealed record HumanoidRigProfile(string Id, string Name, string[] RequiredBones)
{
    /// <summary>
    /// The one body class this build supports. More body classes, hand and
    /// facial rigs, and retargeting are deliberately out of scope here.
    /// </summary>
    public static readonly HumanoidRigProfile HumanoidA = new(
        "humanoid-a", "Humanoid A (spine, arms, legs)",
        [
            "Hips", "Spine", "Chest", "Neck", "Head",
            "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
            "RightUpperLeg", "RightLowerLeg", "RightFoot",
        ]);
}

/// <summary>One bone as the file describes it, with its rest pose in both forms.</summary>
public sealed record GlbBoneSummary(
    string Name, string? Parent, int Depth,
    double[] RestTranslation, double[] RestRotation, double[] RestScale,
    double[] RestWorldPosition);

/// <summary>
/// What is actually known about a rig, and what could not be confirmed.
///
/// <see cref="AnimationReady"/> is the only claim that matters downstream, and
/// it is granted solely when a known profile is matched and every check passed.
/// An imported skeleton whose bones happen to be named something else is an
/// unknown skeleton, not an animation-ready character.
/// </summary>
public sealed record GlbRigProfile(
    bool HasSkeleton,
    string ProfileId, string ProfileName, bool ProfileMatched,
    string[] MissingBones, string[] UnexpectedBones,
    int SkinCount, int BoneCount, int SkinnedVertexCount, int MaxInfluencesPerVertex,
    GlbBoneSummary[] Bones,
    bool TransformsFinite, bool BindPoseValid, bool SkinWeightsValid, int SkinWeightsChecked,
    string[] Findings, bool AnimationReady,
    /// <summary>
    /// This exact skeleton's identity, computed the way the compiler computes
    /// it, from the values the file stores rather than the rounded ones the
    /// bone summaries display. Null when the skeleton cannot be fingerprinted.
    /// </summary>
    string? Fingerprint = null);

/// <summary>
/// Reads a GLB's skin, skeleton, and bind data and says plainly what is sound.
///
/// Weights are read from the binary chunk rather than assumed from the JSON, so
/// a file that declares a skin it cannot honour fails here instead of in a
/// viewport. A static prop is not a failure: it simply has no skeleton.
/// </summary>
public static class GlbRigInspector
{
    /// <summary>Above this, weights are not claimed valid: they are reported unchecked.</summary>
    private const int MaxValidatedSkinnedVertices = 250_000;
    private const float WeightSumTolerance = 1e-3f;

    public static readonly GlbRigProfile NoSkeleton = new(
        false, "none", "No skeleton", false, [], [], 0, 0, 0, 0, [],
        true, true, true, 0, [], false);

    public static GlbRigProfile Inspect(ReadOnlySpan<byte> json, ReadOnlySpan<byte> binary, HumanoidRigProfile? profile = null)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        return Inspect(document.RootElement, binary, profile);
    }

    public static GlbRigProfile Inspect(JsonElement root, ReadOnlySpan<byte> binary, HumanoidRigProfile? profile = null)
    {
        var expected = profile ?? HumanoidRigProfile.HumanoidA;
        var skins = Array(root, "skins");
        if (skins.Length == 0) return NoSkeleton;

        var findings = new List<string>();
        var nodes = Array(root, "nodes");
        var accessors = Array(root, "accessors");
        var bufferViews = Array(root, "bufferViews");
        var meshes = Array(root, "meshes");

        if (skins.Length > 1)
            findings.Add($"This model declares {skins.Length} skins. This build inspects one skin per model.");

        var skin = skins[0];
        var joints = Array(skin, "joints")
            .Select(x => x.TryGetInt32(out var value) ? value : -1).ToArray();
        if (joints.Length == 0 || joints.Any(index => index < 0 || index >= nodes.Length))
        {
            findings.Add("This skin names joints that are not nodes in this file.");
            return Unknown(skins.Length, findings);
        }
        if (joints.Distinct().Count() != joints.Length)
        {
            findings.Add("This skin names the same joint more than once.");
            return Unknown(skins.Length, findings);
        }

        // The parent of a joint is whichever node lists it as a child, so the
        // hierarchy comes from the file rather than from bone names.
        var parentOf = new Dictionary<int, int>();
        for (var index = 0; index < nodes.Length; index++)
        {
            foreach (var child in Array(nodes[index], "children"))
                if (child.TryGetInt32(out var childIndex) && childIndex >= 0 && childIndex < nodes.Length)
                    parentOf[childIndex] = index;
        }

        var transformsFinite = true;
        var names = new Dictionary<int, string>();
        foreach (var joint in joints)
            names[joint] = Name(nodes[joint], joint);

        var bones = new List<GlbBoneSummary>(joints.Length);
        // The fingerprint reads the values the file stores. The summaries below
        // round for display, and a fingerprint taken from rounded values would
        // not match the compiler's.
        var identity = new List<SkeletonFingerprint.Joint>(joints.Length);
        // Rest transforms compose down the hierarchy, so a bone under a rotated
        // or scaled parent reports where it actually rests, not where its own
        // translation alone would put it.
        var world = new Dictionary<int, Matrix4x4>();
        foreach (var joint in joints.OrderBy(joint => Depth(joint, parentOf, joints)))
        {
            var node = nodes[joint];
            var translation = Vector(node, "translation") ?? Vector3.Zero;
            var scale = Vector(node, "scale") ?? Vector3.One;
            var rotation = Rotation(node);
            if (!Finite(translation) || !Finite(scale) || rotation is null) transformsFinite = false;

            var parent = parentOf.TryGetValue(joint, out var parentIndex) && joints.Contains(parentIndex) ? parentIndex : (int?)null;
            var quaternion = rotation ?? Quaternion.Identity;
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(quaternion) * Matrix4x4.CreateTranslation(translation);
            var placed = parent is { } known && world.TryGetValue(known, out var parentWorld) ? local * parentWorld : local;
            world[joint] = placed;
            var position = placed.Translation;
            identity.Add(new SkeletonFingerprint.Joint(
                names[joint], parent is { } identityParent ? names[identityParent] : null,
                translation, quaternion, scale));
            bones.Add(new GlbBoneSummary(
                names[joint], parent is { } above ? names[above] : null, Depth(joint, parentOf, joints),
                [Round(translation.X), Round(translation.Y), Round(translation.Z)],
                [Round(quaternion.X), Round(quaternion.Y), Round(quaternion.Z), Round(quaternion.W)],
                [Round(scale.X), Round(scale.Y), Round(scale.Z)],
                [Round(position.X), Round(position.Y), Round(position.Z)]));
        }
        if (!transformsFinite) findings.Add("This skeleton has a bone whose rest transform is not a finite number.");

        var bindPoseValid = ValidateBindPose(skin, accessors, bufferViews, binary, joints.Length, findings);
        var (weightsValid, checkedVertices, skinnedVertices, maxInfluences) =
            ValidateSkin(root, meshes, accessors, bufferViews, binary, joints.Length, findings);

        var present = bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expected.RequiredBones.Where(bone => !present.Contains(bone)).ToArray();
        var unexpected = bones.Select(bone => bone.Name)
            .Where(name => !expected.RequiredBones.Contains(name, StringComparer.Ordinal)).ToArray();
        var matched = missing.Length == 0;
        if (!matched)
            findings.Add($"This skeleton does not match {expected.Name}: {missing.Length} of its {expected.RequiredBones.Length} bones are missing, starting with {missing[0]}.");

        var ready = matched && transformsFinite && bindPoseValid && weightsValid && skins.Length == 1;
        if (!ready && matched)
            findings.Add("This rig matches the profile but did not pass every check, so it is not animation-ready.");

        var fingerprint = SkeletonFingerprint.Compute(identity);
        if (!fingerprint.Ok && fingerprint.Error is { } fingerprintError) findings.Add(fingerprintError);

        return new GlbRigProfile(
            HasSkeleton: true,
            ProfileId: matched ? expected.Id : "unknown",
            ProfileName: matched ? expected.Name : "Unknown skeleton",
            ProfileMatched: matched,
            MissingBones: missing, UnexpectedBones: unexpected,
            SkinCount: skins.Length, BoneCount: bones.Count,
            SkinnedVertexCount: skinnedVertices, MaxInfluencesPerVertex: maxInfluences,
            Bones: [.. bones],
            TransformsFinite: transformsFinite, BindPoseValid: bindPoseValid,
            SkinWeightsValid: weightsValid, SkinWeightsChecked: checkedVertices,
            Findings: [.. findings], AnimationReady: ready,
            Fingerprint: fingerprint.Fingerprint);
    }

    private static GlbRigProfile Unknown(int skinCount, List<string> findings) => new(
        true, "unknown", "Unknown skeleton", false, [], [], skinCount, 0, 0, 0, [],
        false, false, false, 0, [.. findings], false);

    private static bool ValidateBindPose(
        JsonElement skin, JsonElement[] accessors, JsonElement[] bufferViews, ReadOnlySpan<byte> binary,
        int jointCount, List<string> findings)
    {
        if (!skin.TryGetProperty("inverseBindMatrices", out var indexNode) || !indexNode.TryGetInt32(out var index)
            || index < 0 || index >= accessors.Length)
        {
            findings.Add("This skin declares no inverse bind matrices, so its bind pose cannot be confirmed.");
            return false;
        }

        var accessor = accessors[index];
        if (Int(accessor, "count", 0) != jointCount)
        {
            findings.Add("This skin has a different number of bind matrices than joints.");
            return false;
        }
        if (Text(accessor, "type") != "MAT4" || Int(accessor, "componentType", 0) != 5126)
        {
            findings.Add("This skin's bind matrices are not 32-bit float 4x4 matrices.");
            return false;
        }

        var data = Read(accessor, bufferViews, binary, jointCount * 64);
        if (data.IsEmpty)
        {
            findings.Add("This skin's bind matrices do not resolve to the file's own binary chunk.");
            return false;
        }
        for (var offset = 0; offset < jointCount * 64; offset += 4)
        {
            if (!float.IsFinite(BitConverter.ToSingle(data.Slice(offset, 4))))
            {
                findings.Add("This skin has a bind matrix that is not a finite number.");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Reads every skinned vertex's joints and weights out of the binary chunk.
    /// A model with more skinned vertices than this build validates is reported
    /// as unchecked rather than quietly assumed sound.
    /// </summary>
    private static (bool Valid, int Checked, int SkinnedVertices, int MaxInfluences) ValidateSkin(
        JsonElement root, JsonElement[] meshes, JsonElement[] accessors, JsonElement[] bufferViews,
        ReadOnlySpan<byte> binary, int jointCount, List<string> findings)
    {
        var valid = true;
        var inspected = 0;
        var skinned = 0;
        var maxInfluences = 0;

        foreach (var node in Array(root, "nodes"))
        {
            if (!node.TryGetProperty("skin", out _)) continue;
            if (!node.TryGetProperty("mesh", out var meshNode) || !meshNode.TryGetInt32(out var mesh)
                || mesh < 0 || mesh >= meshes.Length) continue;

            foreach (var primitive in Array(meshes[mesh], "primitives"))
            {
                if (!primitive.TryGetProperty("attributes", out var attributes)) continue;
                if (Accessor(attributes, "JOINTS_0", accessors) is not { } jointAccessor
                    || Accessor(attributes, "WEIGHTS_0", accessors) is not { } weightAccessor)
                {
                    findings.Add("This model has a skinned mesh without joint and weight attributes.");
                    valid = false;
                    continue;
                }

                var count = Int(jointAccessor, "count", 0);
                if (count != Int(weightAccessor, "count", 0))
                {
                    findings.Add("This model has a skinned mesh whose joint and weight counts disagree.");
                    valid = false;
                    continue;
                }
                skinned += count;
                maxInfluences = Math.Max(maxInfluences, 4);

                if (inspected + count > MaxValidatedSkinnedVertices)
                {
                    findings.Add($"This model has more than {MaxValidatedSkinnedVertices:N0} skinned vertices, which is more than this build validates.");
                    valid = false;
                    continue;
                }

                var jointStride = Int(jointAccessor, "componentType", 0) switch { 5121 => 4, 5123 => 8, _ => 0 };
                var weightComponent = Int(weightAccessor, "componentType", 0);
                if (jointStride == 0 || Text(jointAccessor, "type") != "VEC4" || Text(weightAccessor, "type") != "VEC4"
                    || weightComponent is not (5126 or 5121 or 5123))
                {
                    findings.Add("This model's skin uses joint or weight formats this build does not read.");
                    valid = false;
                    continue;
                }

                var weightStride = weightComponent switch { 5121 => 4, 5123 => 8, _ => 16 };
                var jointData = Read(jointAccessor, bufferViews, binary, count * jointStride);
                var weightData = Read(weightAccessor, bufferViews, binary, count * weightStride);
                if (jointData.IsEmpty || weightData.IsEmpty)
                {
                    findings.Add("This model's skin data does not resolve to the file's own binary chunk.");
                    valid = false;
                    continue;
                }

                for (var vertex = 0; vertex < count; vertex++)
                {
                    var sum = 0f;
                    for (var influence = 0; influence < 4; influence++)
                    {
                        var joint = Int(jointData, vertex * jointStride + influence * (jointStride / 4), jointStride / 4);
                        var weight = Weight(weightData, vertex * weightStride + influence * (weightStride / 4), weightComponent);
                        if (!float.IsFinite(weight) || weight < 0)
                        {
                            findings.Add("This model has a skin weight that is negative or not a number.");
                            return (false, inspected, skinned, maxInfluences);
                        }
                        if (weight > 0 && (joint < 0 || joint >= jointCount))
                        {
                            findings.Add("This model has a vertex weighted to a bone this skin does not have.");
                            return (false, inspected, skinned, maxInfluences);
                        }
                        sum += weight;
                    }
                    if (Math.Abs(sum - 1f) > WeightSumTolerance)
                    {
                        findings.Add(sum == 0
                            ? "This model has a vertex that is weighted to no bone at all."
                            : "This model has a vertex whose skin weights do not sum to one.");
                        return (false, inspected, skinned, maxInfluences);
                    }
                    inspected++;
                }
            }
        }

        if (skinned == 0)
        {
            findings.Add("This model declares a skin that no mesh uses.");
            return (false, 0, 0, 0);
        }
        return (valid, inspected, skinned, maxInfluences);
    }

    private static JsonElement? Accessor(JsonElement attributes, string name, JsonElement[] accessors) =>
        attributes.TryGetProperty(name, out var indexNode) && indexNode.TryGetInt32(out var index)
            && index >= 0 && index < accessors.Length
            ? accessors[index]
            : null;

    private static ReadOnlySpan<byte> Read(JsonElement accessor, JsonElement[] bufferViews, ReadOnlySpan<byte> binary, int length)
    {
        if (!accessor.TryGetProperty("bufferView", out var viewNode) || !viewNode.TryGetInt32(out var view)
            || view < 0 || view >= bufferViews.Length) return default;
        var offset = Int(bufferViews[view], "byteOffset", 0) + Int(accessor, "byteOffset", 0);
        // An interleaved stride would need a different reader, so it is refused
        // rather than read as if it were tightly packed.
        if (bufferViews[view].TryGetProperty("byteStride", out _)) return default;
        if (offset < 0 || length < 0 || offset + length > binary.Length) return default;
        return binary.Slice(offset, length);
    }

    private static float Weight(ReadOnlySpan<byte> data, int offset, int componentType) => componentType switch
    {
        5121 => data[offset] / 255f,
        5123 => BitConverter.ToUInt16(data.Slice(offset, 2)) / 65535f,
        _ => BitConverter.ToSingle(data.Slice(offset, 4)),
    };

    private static int Int(ReadOnlySpan<byte> data, int offset, int size) =>
        size == 1 ? data[offset] : BitConverter.ToUInt16(data.Slice(offset, 2));

    private static int Depth(int joint, Dictionary<int, int> parentOf, int[] joints)
    {
        var depth = 0;
        var current = joint;
        while (parentOf.TryGetValue(current, out var parent) && joints.Contains(parent) && depth < 256)
        {
            current = parent;
            depth++;
        }
        return depth;
    }

    private static string Name(JsonElement node, int index) =>
        node.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text ? text : $"Bone {index}";

    private static Quaternion? Rotation(JsonElement node)
    {
        if (!node.TryGetProperty("rotation", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
            return Quaternion.Identity;
        var parts = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (parts.Any(part => !double.IsFinite(part))) return null;
        return new Quaternion((float)parts[0], (float)parts[1], (float)parts[2], (float)parts[3]);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Vector3? Vector(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return null;
        var parts = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        return new Vector3((float)parts[0], (float)parts[1], (float)parts[2]);
    }

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static double Round(float value) => Math.Round(value, 4);
}
