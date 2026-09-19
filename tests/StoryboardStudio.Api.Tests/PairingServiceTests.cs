using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class PairingServiceTests
{
    [Fact]
    public void PairingCodesAreShortLivedRotatedAndExchangeForOpaqueSessions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:AllowLan"] = "true" }).Build();
        var pairing = new PairingService(TimeProvider.System, configuration);
        var initial = pairing.Status(loopback: true, paired: false);
        Assert.True(initial.LanEnabled); Assert.Matches("^[0-9]{8}$", initial.PairingCode!); Assert.NotNull(initial.CodeExpiresAt);
        Assert.Null(pairing.Claim("00000000", "tablet-a"));
        var token = pairing.Claim(initial.PairingCode!, "tablet-a");
        Assert.NotNull(token); Assert.Equal(64, token!.Length); Assert.True(pairing.Validate(token)); Assert.Null(pairing.Claim(initial.PairingCode!, "tablet-b"));
        var remote = pairing.Status(loopback: false, paired: true); Assert.Null(remote.PairingCode); Assert.True(remote.IsPaired);
        pairing.RevokeAll(loopback: true); Assert.False(pairing.Validate(token));
    }

    [Fact]
    public void PairingIsInertWhenLANModeIsDisabled()
    {
        var pairing = new PairingService(TimeProvider.System, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:AllowLan"] = "false" }).Build());
        Assert.False(pairing.Status(true, false).LanEnabled); Assert.Null(pairing.Claim("12345678", "tablet"));
    }
}
