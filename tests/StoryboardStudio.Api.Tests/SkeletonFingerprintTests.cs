using StoryboardStudio.Api.Services;
using System.Numerics;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The compiler and this studio both compute a skeleton's identity, in
/// different languages, and a clip binds on the strength of them agreeing. The
/// worked example in the compiler's browser studio contract is asserted here
/// and in its own test suite; if the two ever disagree, the canonical text says
/// which field drifted.
/// </summary>
public sealed class SkeletonFingerprintTests
{
    /// <summary>
    /// Two joints, supplied out of order, with a rotation that is not the
    /// identity and a translation that lands exactly on a rounding boundary.
    /// </summary>
    private static SkeletonFingerprint.Joint[] SharedVector() =>
    [
        new("Spine", "Hips",
            new Vector3(0f, 0.1234565f, 0f),
            new Quaternion(0f, 0f, 0.3826834f, 0.9238795f),
            Vector3.One),
        new("Hips", null, new Vector3(0f, 0.95f, 0f), Quaternion.Identity, Vector3.One),
    ];

    private const string SharedVectorFingerprint = "18c20df3170c39e2b00a889d44b9f38c2b6309aae636ce1e091e82736058d589";

    [Fact]
    public void TheSharedVectorHashesToTheSameStringTheCompilerProduces()
    {
        var result = SkeletonFingerprint.Compute(SharedVector());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(SharedVectorFingerprint, result.Fingerprint);
        // The canonical text is asserted too, because when the two sides
        // disagree this is what tells you which field drifted.
        Assert.Equal(
            "rac-skeleton-v1\n"
            + "Hips||0,950000,0|0,0,0,1000000|1000000,1000000,1000000\n"
            + "Spine|Hips|0,123457,0|0,0,382683,923880|1000000,1000000,1000000\n",
            result.CanonicalText);
    }

    [Fact]
    public void TheOrderJointsArriveInDoesNotChangeTheFingerprint()
    {
        var forwards = SkeletonFingerprint.Compute(SharedVector());
        var backwards = SkeletonFingerprint.Compute([.. SharedVector().Reverse()]);

        Assert.Equal(forwards.Fingerprint, backwards.Fingerprint);
        Assert.Equal(64, forwards.Fingerprint!.Length);
    }

    [Fact]
    public void HalfwayValuesRoundAwayFromZeroRatherThanToEven()
    {
        // The case where .NET's "F6" and Python's ".6f" disagree. Pinning it is
        // the whole point of quantizing to an integer instead of formatting.
        Assert.Equal("123457", SkeletonFingerprint.Quantize(0.1234565));
        Assert.Equal("-123457", SkeletonFingerprint.Quantize(-0.1234565));
        Assert.Equal("1", SkeletonFingerprint.Quantize(0.0000005));
        Assert.Equal("-1", SkeletonFingerprint.Quantize(-0.0000005));
        // Negative zero is the same place as zero and must read the same.
        Assert.Equal("0", SkeletonFingerprint.Quantize(-0.0));
        Assert.Equal("0", SkeletonFingerprint.Quantize(0.0));
    }

    [Fact]
    public void ANegatedOrUnnormalizedQuaternionIsTheSameRotation()
    {
        var joints = SharedVector();
        var negated = SharedVector();
        var turn = joints[0].Rotation;
        negated[0] = negated[0] with { Rotation = new Quaternion(-turn.X, -turn.Y, -turn.Z, -turn.W) };
        var scaled = SharedVector();
        scaled[0] = scaled[0] with { Rotation = new Quaternion(turn.X * 4, turn.Y * 4, turn.Z * 4, turn.W * 4) };

        Assert.Equal(SharedVectorFingerprint, SkeletonFingerprint.Compute(negated).Fingerprint);
        Assert.Equal(SharedVectorFingerprint, SkeletonFingerprint.Compute(scaled).Fingerprint);
        Assert.True(SkeletonFingerprint.Canonical(negated[0].Rotation)!.Value.W >= 0);
    }

    [Fact]
    public void ADifferentSkeletonIsADifferentFingerprint()
    {
        foreach (var changed in new Func<SkeletonFingerprint.Joint[], SkeletonFingerprint.Joint[]>[]
        {
            joints => [joints[0] with { Name = "Chest" }, joints[1]],
            // Bigger than one quantization step. A difference of 1e-6 at a
            // rounding boundary can vanish into float32 and is not a
            // different skeleton as far as this fingerprint is concerned.
            joints => [joints[0] with { Translation = new Vector3(0f, 0.1235f, 0f) }, joints[1]],
            joints => [joints[0] with { Parent = null }, joints[1]],
            joints => [joints[0] with { Scale = new Vector3(1f, 1.5f, 1f) }, joints[1]],
        })
        {
            var result = SkeletonFingerprint.Compute(changed(SharedVector()));
            Assert.True(result.Ok, result.Error);
            Assert.NotEqual(SharedVectorFingerprint, result.Fingerprint);
        }
    }

    [Fact]
    public void ASkeletonThatCannotBeFingerprintedIsRefusedRatherThanGuessedAt()
    {
        SkeletonFingerprint.Joint[][] broken =
        [
            [],
            [new("", null, Vector3.Zero, Quaternion.Identity, Vector3.One)],
            [new("Hips", null, Vector3.Zero, new Quaternion(0, 0, 0, 0), Vector3.One)],
            [new("Hips", null, new Vector3(0f, float.PositiveInfinity, 0f), Quaternion.Identity, Vector3.One)],
            [
                new("Hips", null, Vector3.Zero, Quaternion.Identity, Vector3.One),
                new("Hips", null, Vector3.Zero, Quaternion.Identity, Vector3.One),
            ],
            // A name carrying a field or line separator could shift the fields,
            // so two different skeletons would canonicalize to the same text.
            [new("Hip|s", null, Vector3.Zero, Quaternion.Identity, Vector3.One)],
            [new("Hips\nSpine", null, Vector3.Zero, Quaternion.Identity, Vector3.One)],
            [new("Spine", "Hip|s", Vector3.Zero, Quaternion.Identity, Vector3.One)],
        ];

        foreach (var skeleton in broken)
        {
            var result = SkeletonFingerprint.Compute(skeleton);
            Assert.False(result.Ok);
            Assert.Null(result.Fingerprint);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }
}
