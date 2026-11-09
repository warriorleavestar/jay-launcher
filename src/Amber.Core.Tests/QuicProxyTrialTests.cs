using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class QuicProxyTrialTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 51234);

    [Fact]
    public void OffUnlessAsked()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 16000, 40000) { QuicProxy = false });
        Assert.DoesNotContain("--enable-quic-proxy", args);
    }

    [Fact]
    public void AddsTheFlagWithoutTunAndKeepsTheWhitelist()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 16000, 40000) { QuicProxy = true });
        Assert.Contains("--enable-quic-proxy", args);
        Assert.Contains("--no-tun", args);
        Assert.Contains("--tcp-whitelist", args);
    }

    [Fact]
    public void NotUsedWithFullLan()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, true, [], 16000, Tun: true) { QuicProxy = true });
        Assert.DoesNotContain("--enable-quic-proxy", args);
    }
}
