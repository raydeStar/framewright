using StoryboardStudio.Api.Services;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The import gate for 3D. Every case here is something that must be refused
/// with a reason before any geometry reaches a graphics context, or a number the
/// artist is going to read off the inspector and trust.
/// </summary>
public sealed class GlbModelInspectorTests
{
    [Theory]
    [InlineData("asset")]
    [InlineData("count")]
    [InlineData("translation")]
    public void MalformedFieldTypesAreRefusedWithoutCrashing(string field)
    {
        var bytes = ModelFixtures.Mutate(document =>
        {
            if (field == "asset") document["asset"] = false;
            else if (field == "count") document["accessors"]![0]!["count"] = "many";
            else document["nodes"]![0]!["translation"] = new JsonArray("wrong", 0, 0);
        });
        var result = GlbModelInspector.Inspect(bytes);
        Assert.False(result.Ok);
        Assert.Contains("malformed field", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Glass is not an alpha mode. A material that transmits stays OPAQUE in
    /// glTF and carries KHR_materials_transmission instead, so an inspector
    /// that only reads alpha modes tells an artist a window is solid.
    /// </summary>
    [Fact]
    public void AMaterialThatTransmitsIsNotReportedAsSolid()
    {
        var glazed = ModelFixtures.Mutate(document =>
        {
            var material = document["materials"]!.AsArray()[0]!.AsObject();
            material["extensions"] = new JsonObject
            {
                ["KHR_materials_transmission"] = new JsonObject { ["transmissionFactor"] = 0.85 },
            };
            document["extensionsUsed"] = new JsonArray("KHR_materials_transmission");
        });

        var result = GlbModelInspector.Inspect(glazed);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0.85, result.Profile!.Materials[0].Transmission);
        // The alpha mode is genuinely still opaque; the transmission is the
        // separate fact, and both are reported rather than one standing in.
        Assert.Equal("OPAQUE", result.Profile.Materials[0].AlphaMode);
        Assert.Equal(0, result.Profile.Materials[1].Transmission);
    }

    [Fact]
    public void TheKnownFixtureReportsItsRealDimensionsMaterialsAndCounts()
    {
        var result = GlbModelInspector.Inspect(ModelFixtures.AsymmetricBlock());

        Assert.True(result.Ok, result.Error);
        var profile = result.Profile!;
        Assert.Equal("2.0", profile.SpecificationVersion);
        Assert.Equal(2, profile.NodeCount);
        Assert.Equal(2, profile.MeshCount);
        Assert.Equal(16, profile.VertexCount);
        Assert.Equal(24, profile.TriangleCount);
        Assert.Empty(profile.RequiredExtensions);
        Assert.Equal(0, profile.ImageCount);

        // The tab's offset lives on its node, so these numbers are only right if
        // the node transform was applied rather than raw accessor bounds read.
        Assert.Equal([0d, 0d, 0d], profile.BoundsMin);
        Assert.Equal([2d, 1d, 0.75d], profile.BoundsMax);
        Assert.Equal([2d, 1d, 0.75d], profile.Dimensions);

        Assert.Collection(profile.Materials,
            material => { Assert.Equal("Block body", material.Name); Assert.False(material.Textured); Assert.Equal("OPAQUE", material.AlphaMode); },
            material => { Assert.Equal("Corner tab", material.Name); Assert.False(material.Textured); });
    }

    [Fact]
    public void AMirroredImportCannotPassAsTheFixture()
    {
        // Negating X moves the tab to the opposite corner. The bounds have to
        // notice, or an axis-flipped import would look correct.
        var mirrored = ModelFixtures.Mutate(document =>
            document["nodes"]![1]!["translation"] = new JsonArray(-1.75, 0.75, 0.5));

        var profile = GlbModelInspector.Inspect(mirrored).Profile!;
        Assert.Equal(-1.75d, profile.BoundsMin[0]);
        Assert.NotEqual([0d, 0d, 0d], profile.BoundsMin);
    }

    [Theory]
    [InlineData(0, "not a GLB container")]
    [InlineData(4, "version")]
    [InlineData(8, "truncated or padded")]
    public void ACorruptHeaderIsRefusedWithAReason(int offset, string expected)
    {
        var bytes = ModelFixtures.AsymmetricBlock();
        bytes[offset] ^= 0xFF;

        var result = GlbModelInspector.Inspect(bytes);
        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void ATruncatedFileIsRefusedRatherThanPartiallyRead()
    {
        var bytes = ModelFixtures.AsymmetricBlock();
        var result = GlbModelInspector.Inspect(bytes.AsSpan(0, bytes.Length - 40));
        Assert.False(result.Ok);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void ExternalReferencesAndUnsupportedRequiredExtensionsAreRefused()
    {
        var external = ModelFixtures.Mutate(document =>
            document["images"] = new JsonArray(new JsonObject { ["uri"] = "https://example.test/skin.png" }));
        var externalResult = GlbModelInspector.Inspect(external);
        Assert.False(externalResult.Ok);
        Assert.Contains("external", externalResult.Error, StringComparison.OrdinalIgnoreCase);

        var extension = ModelFixtures.Mutate(document =>
            document["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression"));
        var extensionResult = GlbModelInspector.Inspect(extension);
        Assert.False(extensionResult.Ok);
        Assert.Contains("KHR_draco_mesh_compression", extensionResult.Error);
    }

    [Fact]
    public void ResourceCeilingsAreEnforcedBeforeLoading()
    {
        var tiny = GlbSupportProfile.Default with { MaxVertices = 4 };
        var vertices = GlbModelInspector.Inspect(ModelFixtures.AsymmetricBlock(), tiny);
        Assert.False(vertices.Ok);
        Assert.Contains("vertices", vertices.Error, StringComparison.OrdinalIgnoreCase);

        var small = GlbSupportProfile.Default with { MaxBytes = 128 };
        var oversized = GlbModelInspector.Inspect(ModelFixtures.AsymmetricBlock(), small);
        Assert.False(oversized.Ok);
        Assert.Contains("larger than", oversized.Error, StringComparison.OrdinalIgnoreCase);

        var fewNodes = GlbSupportProfile.Default with { MaxNodes = 1 };
        var nodes = GlbModelInspector.Inspect(ModelFixtures.AsymmetricBlock(), fewNodes);
        Assert.False(nodes.Ok);
        Assert.Contains("nodes", nodes.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeometryWithoutResolvableBoundsIsRefusedRatherThanGuessed()
    {
        var withoutBounds = ModelFixtures.Mutate(document =>
        {
            var accessor = (JsonObject)document["accessors"]![0]!;
            accessor.Remove("min");
            accessor.Remove("max");
        });

        var result = GlbModelInspector.Inspect(withoutBounds);
        Assert.False(result.Ok);
        Assert.Contains("min and max", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AModelWithNoGeometryIsRefused()
    {
        var empty = ModelFixtures.Mutate(document => document["meshes"] = new JsonArray());
        var result = GlbModelInspector.Inspect(empty);
        Assert.False(result.Ok);
        Assert.Contains("no mesh primitives", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Reads the committed known-dimension fixture and rewrites its JSON chunk for
/// the refusal cases, so every test starts from the same real GLB rather than a
/// hand-rolled approximation of one.
/// </summary>
public static class ModelFixtures
{
    private const uint JsonChunk = 0x4E4F534A;

    public static byte[] AsymmetricBlock() => File.ReadAllBytes(Path());
    public static byte[] AsymmetricPost() => File.ReadAllBytes(Path("asymmetric-post.glb"));
    public static byte[] RiggedFigure() => File.ReadAllBytes(Path("rigged-figure.glb"));
    // Dense enough that reducing it means something: a runtime budget is at
    // least a thousand triangles, so no budget can ever be smaller than a block.
    public static byte[] DenseProp() => File.ReadAllBytes(Path("dense-prop.glb"));
    public static byte[] DensePropRuntime() => File.ReadAllBytes(Path("dense-prop-runtime.glb"));
    public static byte[] RiggedWrongProfile() => File.ReadAllBytes(Path("rigged-wrong-profile.glb"));
    public static byte[] RiggedBrokenSkin() => File.ReadAllBytes(Path("rigged-broken-skin.glb"));
    public static byte[] ClipArmRaise() => File.ReadAllBytes(Path("clip-arm-raise.glb"));
    public static byte[] ClipWrongSkeleton() => File.ReadAllBytes(Path("clip-wrong-skeleton.glb"));

    public static string Path(string fileName = "asymmetric-block.glb")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "fixtures", "glb", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"The {fileName} fixture was not found above the test output directory.");
    }

    public static byte[] Mutate(Action<JsonObject> edit)
    {
        var bytes = AsymmetricBlock();
        var jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
        var chunkType = BitConverter.ToUInt32(bytes, 16);
        if (chunkType != JsonChunk) throw new InvalidOperationException("The fixture's first chunk is not JSON.");
        var document = JsonNode.Parse(
            Encoding.UTF8.GetString(bytes, 20, jsonLength))!.AsObject();
        edit(document);

        var rewritten = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        if (rewritten.Length % 4 != 0)
            rewritten = [.. rewritten, .. Enumerable.Repeat((byte)' ', 4 - (rewritten.Length % 4))];

        var binaryOffset = 20 + jsonLength;
        var binary = bytes.AsSpan(binaryOffset).ToArray();
        var total = 12 + 8 + rewritten.Length + binary.Length;

        var output = new List<byte>(total);
        output.AddRange(BitConverter.GetBytes(0x46546C67u));
        output.AddRange(BitConverter.GetBytes(2u));
        output.AddRange(BitConverter.GetBytes((uint)total));
        output.AddRange(BitConverter.GetBytes((uint)rewritten.Length));
        output.AddRange(BitConverter.GetBytes(JsonChunk));
        output.AddRange(rewritten);
        output.AddRange(binary);
        return [.. output];
    }
}
