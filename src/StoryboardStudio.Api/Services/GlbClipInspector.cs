using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>One animated channel: which bone it moves, how, and its keyframes.</summary>
public sealed record GlbClipChannel(string Bone, string Path, string Interpolation, double[] Times, double[][] Values);

/// <summary>
/// One reusable clip as the file declares it.
///
/// <see cref="Supported"/> is what decides whether a clip may ever be bound to
/// a character. A clip this build cannot sample exactly is reported with the
/// reason rather than played approximately.
/// </summary>
public sealed record GlbClipSummary(
    string Name, double Duration, int ChannelCount,
    string[] TargetBones, string[] Paths, bool MovesRoot,
    bool Supported, string[] Findings, GlbClipChannel[] Channels);

/// <summary>
/// Reads a GLB's animations into clips that can be sampled exactly.
///
/// Only the interpolations this build can reproduce are accepted, and every
/// keyframe is read from the file's own binary chunk, so a clip that cannot be
/// sampled is refused here rather than played as an approximation of itself.
/// </summary>
public static class GlbClipInspector
{
    private const int MaxClips = 64;
    private const int MaxKeyframes = 4096;
    /// <summary>Ten minutes. Long enough for any shot this studio cuts.</summary>
    public const double MaxDurationSeconds = 600;
    private static readonly string[] SupportedPaths = ["translation", "rotation", "scale"];
    private static readonly string[] SupportedInterpolations = ["LINEAR", "STEP"];

    public static GlbClipSummary[] Inspect(JsonElement root, ReadOnlySpan<byte> binary)
    {
        var animations = Array(root, "animations");
        if (animations.Length == 0) return [];

        var nodes = Array(root, "nodes");
        var accessors = Array(root, "accessors");
        var bufferViews = Array(root, "bufferViews");
        // The root bone is the joint with no parent inside the skin, which is
        // what makes root motion a thing this build can have a policy about.
        var rootBone = RootBone(root, nodes);

        var clips = new List<GlbClipSummary>(Math.Min(animations.Length, MaxClips));
        foreach (var animation in animations.Take(MaxClips))
        {
            var findings = new List<string>();
            var supported = true;
            var channels = new List<GlbClipChannel>();
            var samplers = Array(animation, "samplers");
            var duration = 0d;

            foreach (var channel in Array(animation, "channels"))
            {
                if (!channel.TryGetProperty("target", out var target)
                    || !target.TryGetProperty("node", out var nodeIndex) || !nodeIndex.TryGetInt32(out var node)
                    || node < 0 || node >= nodes.Length)
                {
                    findings.Add("This clip moves something that is not a node in its own file.");
                    supported = false;
                    continue;
                }

                var path = target.TryGetProperty("path", out var pathNode) ? pathNode.GetString() ?? "" : "";
                if (!SupportedPaths.Contains(path))
                {
                    findings.Add($"This clip animates {(path.Length == 0 ? "an unnamed property" : path)}, which this build does not play.");
                    supported = false;
                    continue;
                }

                if (!channel.TryGetProperty("sampler", out var samplerIndex) || !samplerIndex.TryGetInt32(out var index)
                    || index < 0 || index >= samplers.Length)
                {
                    findings.Add("This clip has a channel with no sampler.");
                    supported = false;
                    continue;
                }

                var sampler = samplers[index];
                var interpolation = sampler.TryGetProperty("interpolation", out var mode) ? mode.GetString() ?? "LINEAR" : "LINEAR";
                if (!SupportedInterpolations.Contains(interpolation))
                {
                    findings.Add($"This clip uses {interpolation} interpolation, which this build does not sample.");
                    supported = false;
                    continue;
                }

                var times = ReadScalars(sampler, "input", accessors, bufferViews, binary);
                var stride = path == "rotation" ? 4 : 3;
                var values = ReadVectors(sampler, "output", accessors, bufferViews, binary, stride);
                if (times is null || values is null || times.Length == 0 || times.Length != values.Length)
                {
                    findings.Add("This clip has keyframes that do not resolve to its own binary chunk.");
                    supported = false;
                    continue;
                }
                if (times.Length > MaxKeyframes)
                {
                    findings.Add($"This clip has more than {MaxKeyframes:N0} keyframes on one channel, which is more than this build reads.");
                    supported = false;
                    continue;
                }
                if (times.Any(time => !double.IsFinite(time) || time < 0) || values.Any(value => value.Any(part => !double.IsFinite(part))))
                {
                    findings.Add("This clip has a keyframe that is not a finite number.");
                    supported = false;
                    continue;
                }
                for (var step = 1; step < times.Length; step++)
                {
                    if (times[step] > times[step - 1]) continue;
                    findings.Add("This clip's keyframe times do not run forwards.");
                    supported = false;
                    break;
                }

                duration = Math.Max(duration, times[^1]);
                channels.Add(new GlbClipChannel(Name(nodes[node], node), path, interpolation, times, values));
            }

            if (channels.Count == 0)
            {
                findings.Add("This clip moves nothing this build can play.");
                supported = false;
            }
            if (duration <= 0 || duration > MaxDurationSeconds)
            {
                findings.Add($"This clip's duration must be greater than zero and no more than {MaxDurationSeconds:N0} seconds.");
                supported = false;
            }

            var movesRoot = rootBone is not null
                && channels.Any(channel => channel.Bone == rootBone && channel.Path == "translation");
            clips.Add(new GlbClipSummary(
                Name: animation.TryGetProperty("name", out var clipName) && clipName.GetString() is { Length: > 0 } text
                    ? text
                    : $"Clip {clips.Count + 1}",
                Duration: Math.Round(duration, 4),
                ChannelCount: channels.Count,
                TargetBones: [.. channels.Select(channel => channel.Bone).Distinct()],
                Paths: [.. channels.Select(channel => channel.Path).Distinct()],
                MovesRoot: movesRoot,
                Supported: supported,
                Findings: [.. findings.Distinct()],
                Channels: [.. channels]));
        }

        return [.. clips];
    }

    private static string? RootBone(JsonElement root, JsonElement[] nodes)
    {
        var skins = Array(root, "skins");
        if (skins.Length == 0) return null;
        var joints = Array(skins[0], "joints").Select(x => x.TryGetInt32(out var value) ? value : -1).ToArray();
        if (joints.Length == 0) return null;

        var parented = new HashSet<int>();
        foreach (var node in nodes)
            foreach (var child in Array(node, "children"))
                if (child.TryGetInt32(out var childIndex)) parented.Add(childIndex);

        var rootIndex = joints.FirstOrDefault(joint => !parented.Contains(joint), -1);
        return rootIndex >= 0 && rootIndex < nodes.Length ? Name(nodes[rootIndex], rootIndex) : null;
    }

    private static double[]? ReadScalars(
        JsonElement sampler, string name, JsonElement[] accessors, JsonElement[] bufferViews, ReadOnlySpan<byte> binary)
    {
        if (Accessor(sampler, name, accessors) is not { } accessor) return null;
        if (Text(accessor, "type") != "SCALAR" || Int(accessor, "componentType", 0) != 5126) return null;
        var count = Int(accessor, "count", 0);
        var data = Read(accessor, bufferViews, binary, count * 4);
        if (data.IsEmpty || count == 0) return null;
        var values = new double[count];
        for (var index = 0; index < count; index++) values[index] = BitConverter.ToSingle(data.Slice(index * 4, 4));
        return values;
    }

    private static double[][]? ReadVectors(
        JsonElement sampler, string name, JsonElement[] accessors, JsonElement[] bufferViews, ReadOnlySpan<byte> binary, int stride)
    {
        if (Accessor(sampler, name, accessors) is not { } accessor) return null;
        if (Text(accessor, "type") != (stride == 4 ? "VEC4" : "VEC3") || Int(accessor, "componentType", 0) != 5126) return null;
        var count = Int(accessor, "count", 0);
        var data = Read(accessor, bufferViews, binary, count * stride * 4);
        if (data.IsEmpty || count == 0) return null;
        var values = new double[count][];
        for (var index = 0; index < count; index++)
        {
            var vector = new double[stride];
            for (var part = 0; part < stride; part++)
                vector[part] = BitConverter.ToSingle(data.Slice((index * stride + part) * 4, 4));
            values[index] = vector;
        }
        return values;
    }

    private static JsonElement? Accessor(JsonElement sampler, string name, JsonElement[] accessors) =>
        sampler.TryGetProperty(name, out var indexNode) && indexNode.TryGetInt32(out var index)
            && index >= 0 && index < accessors.Length
            ? accessors[index]
            : null;

    private static ReadOnlySpan<byte> Read(JsonElement accessor, JsonElement[] bufferViews, ReadOnlySpan<byte> binary, int length)
    {
        if (!accessor.TryGetProperty("bufferView", out var viewNode) || !viewNode.TryGetInt32(out var view)
            || view < 0 || view >= bufferViews.Length) return default;
        if (bufferViews[view].TryGetProperty("byteStride", out _)) return default;
        var offset = Int(bufferViews[view], "byteOffset", 0) + Int(accessor, "byteOffset", 0);
        if (offset < 0 || length <= 0 || offset + length > binary.Length) return default;
        return binary.Slice(offset, length);
    }

    private static string Name(JsonElement node, int index) =>
        node.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } text ? text : $"Bone {index}";

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
}
