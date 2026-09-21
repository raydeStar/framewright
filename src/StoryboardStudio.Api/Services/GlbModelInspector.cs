using System.Numerics;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// The bounded GLB subset Framewright accepts for browser inspection, and the
/// resource ceilings it refuses to load past.
///
/// Everything here is checked before any mesh reaches a graphics context: an
/// oversized, malformed, or externally-linked asset is a refusal with a reason,
/// never a blank viewport or an asset record that looks importable.
/// </summary>
public sealed record GlbSupportProfile(
    string Container,
    string SpecificationVersion,
    string[] SupportedRequiredExtensions,
    long MaxBytes,
    int MaxVertices,
    int MaxTriangles,
    long MaxEmbeddedTextureBytes,
    int MaxNodes,
    int MaxMaterials,
    int MaxImages)
{
    public static readonly GlbSupportProfile Default = new(
        Container: "GLB (binary glTF, self-contained)",
        SpecificationVersion: "2.0",
        SupportedRequiredExtensions: [],
        MaxBytes: 64L * 1024 * 1024,
        MaxVertices: 1_500_000,
        MaxTriangles: 2_000_000,
        MaxEmbeddedTextureBytes: 48L * 1024 * 1024,
        MaxNodes: 4_096,
        MaxMaterials: 256,
        MaxImages: 64);
}

public sealed record GlbMaterialSummary(
    string Name, bool Textured, string AlphaMode, bool DoubleSided, double Transmission = 0);

public sealed record GlbModelProfile(
    string Container,
    string SpecificationVersion,
    string Generator,
    int NodeCount,
    int MeshCount,
    int PrimitiveCount,
    int VertexCount,
    int TriangleCount,
    int ImageCount,
    /// <summary>
    /// Which texture coordinate channels the geometry actually carries, as
    /// TEXCOORD_n numbers, and how many primitives carry none.
    ///
    /// A mesh with no UVs cannot be painted and cannot show the texture it
    /// already has. Nothing here reported that, so a derivative that had lost
    /// its map looked identical to one that kept it until somebody rendered it.
    /// </summary>
    int[] UvChannels,
    int PrimitivesWithoutUvs,
    long EmbeddedTextureBytes,
    long BinaryChunkBytes,
    string[] DeclaredExtensions,
    string[] RequiredExtensions,
    GlbMaterialSummary[] Materials,
    double[] BoundsMin,
    double[] BoundsMax,
    double[] Dimensions,
    GlbRigProfile Rig,
    GlbClipSummary[] Clips);

public sealed record GlbInspectionResult(bool Ok, string? Error, GlbModelProfile? Profile);

public static class GlbModelInspector
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    /// <summary>
    /// Validates the container, the declared subset, and the resource ceilings,
    /// then reports what the artist is about to look at. Only the JSON chunk and
    /// the accessor bounds are read, so inspection cost does not scale with the
    /// size of the geometry payload.
    /// </summary>
    public static GlbInspectionResult Inspect(ReadOnlySpan<byte> bytes, GlbSupportProfile? profile = null)
    {
        var limits = profile ?? GlbSupportProfile.Default;
        if (bytes.Length > limits.MaxBytes)
            return Failure($"This model is larger than the supported {limits.MaxBytes / (1024 * 1024)} MB.");
        if (bytes.Length < 20) return Failure("This file is too short to be a GLB container.");
        if (ReadUInt32(bytes, 0) != GlbMagic) return Failure("This file is not a GLB container.");
        var version = ReadUInt32(bytes, 4);
        if (version != 2) return Failure($"Only GLB version 2 is supported; this file declares version {version}.");
        var declaredLength = ReadUInt32(bytes, 8);
        if (declaredLength != (uint)bytes.Length)
            return Failure("This GLB is truncated or padded: its header length does not match the file.");

        JsonDocument? json = null;
        long binaryChunkBytes = 0;
        var binaryChunkOffset = 0;
        var offset = 12;
        var chunkIndex = 0;
        try
        {
            while (offset + 8 <= bytes.Length)
            {
                var chunkLength = ReadUInt32(bytes, offset);
                var chunkType = ReadUInt32(bytes, offset + 4);
                offset += 8;
                if (chunkLength > (uint)(bytes.Length - offset))
                    return Failure("This GLB is truncated: a chunk claims more bytes than the file holds.");
                if (chunkLength % 4 != 0)
                    return Failure("This GLB is malformed: chunk lengths must be 4-byte aligned.");

                if (chunkIndex == 0 && chunkType != JsonChunk)
                    return Failure("This GLB is malformed: the first chunk must be JSON.");
                if (chunkType == JsonChunk)
                {
                    if (json is not null) return Failure("This GLB is malformed: it declares more than one JSON chunk.");
                    try { json = JsonDocument.Parse(bytes.Slice(offset, (int)chunkLength).ToArray()); }
                    catch (JsonException) { return Failure("This GLB's JSON chunk could not be read."); }
                }
                else if (chunkType == BinaryChunk)
                {
                    if (binaryChunkBytes > 0) return Failure("This GLB is malformed: it declares more than one binary chunk.");
                    binaryChunkBytes = chunkLength;
                    binaryChunkOffset = offset;
                }

                offset += (int)chunkLength;
                chunkIndex++;
            }

            if (offset != bytes.Length) return Failure("This GLB is malformed: its chunk table does not fill the file.");
            if (json is null) return Failure("This GLB has no JSON chunk.");
            // The skin is read from the file's own bytes rather than taken on
            // trust from the JSON, so the binary chunk travels with the document.
            var binary = binaryChunkBytes > 0 ? bytes.Slice(binaryChunkOffset, (int)binaryChunkBytes) : default;
            return Describe(json.RootElement, binaryChunkBytes, binary, limits);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return Failure("This GLB contains malformed field values. Check its glTF structure before importing it.");
        }
        finally { json?.Dispose() ; }
    }

    private static GlbInspectionResult Describe(JsonElement root, long binaryChunkBytes, ReadOnlySpan<byte> binary, GlbSupportProfile limits)
    {
        if (!root.TryGetProperty("asset", out var assetNode) || !assetNode.TryGetProperty("version", out var versionNode)
            || versionNode.GetString() is not { } specVersion)
            return Failure("This glTF document does not declare its asset version.");
        if (!specVersion.StartsWith("2.", StringComparison.Ordinal))
            return Failure($"Only glTF 2.0 is supported; this document declares {specVersion}.");

        var required = StringArray(root, "extensionsRequired");
        var unsupported = required.Where(x => !limits.SupportedRequiredExtensions.Contains(x, StringComparer.Ordinal)).ToArray();
        if (unsupported.Length > 0)
            return Failure($"This model requires unsupported extensions: {string.Join(", ", unsupported)}.");

        // A self-contained GLB resolves everything from its own binary chunk. A
        // URI here would reach outside the imported file, so the initial route
        // refuses it rather than fetching anything.
        foreach (var name in (string[])["buffers", "images"])
        {
            if (!root.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("uri", out _))
                    return Failure($"This model references external {name} by URI. Import a self-contained GLB instead.");
            }
        }

        var accessors = Array(root, "accessors");
        var bufferViews = Array(root, "bufferViews");
        var meshes = Array(root, "meshes");
        var nodes = Array(root, "nodes");
        var materials = Array(root, "materials");
        var images = Array(root, "images");

        if (nodes.Length > limits.MaxNodes) return Failure($"This model has {nodes.Length} nodes, above the supported {limits.MaxNodes}.");
        if (materials.Length > limits.MaxMaterials) return Failure($"This model has {materials.Length} materials, above the supported {limits.MaxMaterials}.");
        if (images.Length > limits.MaxImages) return Failure($"This model has {images.Length} images, above the supported {limits.MaxImages}.");

        long embeddedTextureBytes = 0;
        foreach (var image in images)
        {
            if (!image.TryGetProperty("bufferView", out var viewIndex) || !viewIndex.TryGetInt32(out var view)
                || view < 0 || view >= bufferViews.Length)
                return Failure("This model has an image that does not resolve to its own binary chunk.");
            embeddedTextureBytes += Int(bufferViews[view], "byteLength", 0);
        }
        if (embeddedTextureBytes > limits.MaxEmbeddedTextureBytes)
            return Failure($"This model embeds {embeddedTextureBytes / (1024 * 1024)} MB of textures, above the supported {limits.MaxEmbeddedTextureBytes / (1024 * 1024)} MB.");

        long vertexCount = 0;
        long triangleCount = 0;
        var primitiveCount = 0;
        var uvChannels = new SortedSet<int>();
        var primitivesWithoutUvs = 0;
        foreach (var mesh in meshes)
        {
            foreach (var primitive in Array(mesh, "primitives"))
            {
                primitiveCount++;
                var mode = Int(primitive, "mode", 4);
                if (!primitive.TryGetProperty("attributes", out var attributes)
                    || !attributes.TryGetProperty("POSITION", out var positionIndex)
                    || !positionIndex.TryGetInt32(out var position)
                    || position < 0 || position >= accessors.Length)
                    return Failure("This model has a mesh primitive without a resolvable POSITION accessor.");

                var vertices = Int(accessors[position], "count", 0);
                if (vertices <= 0) return Failure("This model has a mesh primitive with no vertices.");
                vertexCount += vertices;

                // Counted per primitive rather than per mesh: one primitive
                // losing its map is a hole in the paint, not a rounding error.
                var carriedUvs = 0;
                for (var channel = 0; attributes.TryGetProperty($"TEXCOORD_{channel}", out _); channel++)
                {
                    uvChannels.Add(channel);
                    carriedUvs++;
                }
                if (carriedUvs == 0) primitivesWithoutUvs++;

                var indexed = primitive.TryGetProperty("indices", out var indicesIndex) && indicesIndex.TryGetInt32(out var indices)
                    && indices >= 0 && indices < accessors.Length
                    ? Int(accessors[indices], "count", 0)
                    : vertices;
                if (mode == 4) triangleCount += indexed / 3;
            }
        }

        if (primitiveCount == 0) return Failure("This model contains no mesh primitives to inspect.");
        if (vertexCount > limits.MaxVertices) return Failure($"This model has {vertexCount:N0} vertices, above the supported {limits.MaxVertices:N0}.");
        if (triangleCount > limits.MaxTriangles) return Failure($"This model has {triangleCount:N0} triangles, above the supported {limits.MaxTriangles:N0}.");

        var bounds = WorldBounds(root, nodes, meshes, accessors);
        if (bounds is null) return Failure("This model's geometry bounds could not be resolved. Every POSITION accessor must declare min and max.");
        var (min, max) = bounds.Value;

        var summaries = materials.Select(material => new GlbMaterialSummary(
            Name: material.TryGetProperty("name", out var name) ? name.GetString() ?? "Unnamed material" : "Unnamed material",
            Textured: HasTexture(material),
            AlphaMode: material.TryGetProperty("alphaMode", out var alpha) ? alpha.GetString() ?? "OPAQUE" : "OPAQUE",
            DoubleSided: material.TryGetProperty("doubleSided", out var doubleSided) && doubleSided.ValueKind == JsonValueKind.True,
            // Glass is not an alpha mode. A material that transmits stays
            // OPAQUE in glTF and carries KHR_materials_transmission instead, so
            // a reader that only knows alpha modes calls a window solid.
            Transmission: Transmission(material))).ToArray();

        return new GlbInspectionResult(true, null, new GlbModelProfile(
            Container: limits.Container,
            SpecificationVersion: specVersion,
            Generator: assetNode.TryGetProperty("generator", out var generator) ? generator.GetString() ?? "" : "",
            NodeCount: nodes.Length,
            MeshCount: meshes.Length,
            PrimitiveCount: primitiveCount,
            VertexCount: (int)vertexCount,
            TriangleCount: (int)triangleCount,
            ImageCount: images.Length,
            UvChannels: [.. uvChannels],
            PrimitivesWithoutUvs: primitivesWithoutUvs,
            EmbeddedTextureBytes: embeddedTextureBytes,
            BinaryChunkBytes: binaryChunkBytes,
            DeclaredExtensions: StringArray(root, "extensionsUsed"),
            RequiredExtensions: required,
            Materials: summaries,
            BoundsMin: [Round(min.X), Round(min.Y), Round(min.Z)],
            BoundsMax: [Round(max.X), Round(max.Y), Round(max.Z)],
            Dimensions: [Round(max.X - min.X), Round(max.Y - min.Y), Round(max.Z - min.Z)],
            Rig: GlbRigInspector.Inspect(root, binary),
            Clips: GlbClipInspector.Inspect(root, binary)));
    }

    /// <summary>
    /// Bounds in scene space, so a known-dimension fixture can be checked
    /// against the number the artist will read, not against raw accessor values
    /// that ignore how the node tree places them.
    /// </summary>
    private static (Vector3 Min, Vector3 Max)? WorldBounds(
        JsonElement root, JsonElement[] nodes, JsonElement[] meshes, JsonElement[] accessors)
    {
        var sceneIndex = Int(root, "scene", 0);
        var scenes = Array(root, "scenes");
        var roots = scenes.Length > sceneIndex && sceneIndex >= 0
            ? Array(scenes[sceneIndex], "nodes").Select(x => x.TryGetInt32(out var value) ? value : -1).ToArray()
            : Enumerable.Range(0, nodes.Length).ToArray();

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var found = false;
        var visited = new HashSet<int>();

        var pending = new Stack<(int Node, Matrix4x4 World)>();
        foreach (var index in roots.Reverse()) pending.Push((index, Matrix4x4.Identity));

        while (pending.Count > 0)
        {
            var (index, parent) = pending.Pop();
            if (index < 0 || index >= nodes.Length || !visited.Add(index)) continue;
            var node = nodes[index];
            var world = LocalTransform(node) * parent;

            if (node.TryGetProperty("mesh", out var meshIndex) && meshIndex.TryGetInt32(out var mesh)
                && mesh >= 0 && mesh < meshes.Length)
            {
                foreach (var primitive in Array(meshes[mesh], "primitives"))
                {
                    if (!primitive.TryGetProperty("attributes", out var attributes)
                        || !attributes.TryGetProperty("POSITION", out var positionIndex)
                        || !positionIndex.TryGetInt32(out var position)
                        || position < 0 || position >= accessors.Length) return null;
                    var accessor = accessors[position];
                    if (Vector(accessor, "min") is not { } localMin || Vector(accessor, "max") is not { } localMax) return null;

                    // Every corner, because a rotated box's extents are not the
                    // rotated extents of its corners taken two at a time.
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var point = new Vector3(
                            (corner & 1) == 0 ? localMin.X : localMax.X,
                            (corner & 2) == 0 ? localMin.Y : localMax.Y,
                            (corner & 4) == 0 ? localMin.Z : localMax.Z);
                        var placed = Vector3.Transform(point, world);
                        if (!float.IsFinite(placed.X) || !float.IsFinite(placed.Y) || !float.IsFinite(placed.Z)) return null;
                        min = Vector3.Min(min, placed);
                        max = Vector3.Max(max, placed);
                        found = true;
                    }
                }
            }

            foreach (var child in Array(node, "children"))
                if (child.TryGetInt32(out var childIndex)) pending.Push((childIndex, world));
        }

        return found ? (min, max) : null;
    }

    private static Matrix4x4 LocalTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Array && matrix.GetArrayLength() == 16)
        {
            var m = matrix.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            // glTF stores column-major; read as row-major this is the transpose,
            // which is exactly the row-vector form System.Numerics transforms with.
            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        var scale = Vector(node, "scale") ?? Vector3.One;
        var translation = Vector(node, "translation") ?? Vector3.Zero;
        var rotation = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out var quaternion) && quaternion.ValueKind == JsonValueKind.Array && quaternion.GetArrayLength() == 4)
        {
            var q = quaternion.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            rotation = new Quaternion(q[0], q[1], q[2], q[3]);
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static double Transmission(JsonElement material) =>
        material.TryGetProperty("extensions", out var extensions)
        && extensions.TryGetProperty("KHR_materials_transmission", out var transmission)
        && transmission.TryGetProperty("transmissionFactor", out var factor)
        && factor.ValueKind == JsonValueKind.Number
            ? factor.GetDouble()
            : 0;

    private static bool HasTexture(JsonElement material)
    {
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr)
            && (pbr.TryGetProperty("baseColorTexture", out _) || pbr.TryGetProperty("metallicRoughnessTexture", out _))) return true;
        return material.TryGetProperty("normalTexture", out _)
            || material.TryGetProperty("occlusionTexture", out _)
            || material.TryGetProperty("emissiveTexture", out _);
    }

    private static Vector3? Vector(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return null;
        var parts = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (parts.Any(part => !double.IsFinite(part))) return null;
        return new Vector3((float)parts[0], (float)parts[1], (float)parts[2]);
    }

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string[] StringArray(JsonElement element, string name) =>
        Array(element, name).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();

    private static int Int(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static double Round(float value) => Math.Round(value, 4);
    private static GlbInspectionResult Failure(string error) => new(false, error, null);
}
