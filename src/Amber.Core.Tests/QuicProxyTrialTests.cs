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
        // Without TUN there is no adapter and no broadcast relay, with or without the trial.
        Assert.DoesNotContain("--dev-name", args);
        Assert.DoesNotContain("--enable-udp-broadcast-relay", args);
    }

    [Fact]
    public void AddsTheFlagWithoutTunAndKeepsTheWhitelist()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 16000, 40000) { QuicProxy = true });
        Assert.Contains("--enable-quic-proxy", args);
        Assert.Contains("--no-tun", args);
        Assert.Contains("--tcp-whitelist", args);
        Assert.DoesNotContain("--dev-name", args);
        Assert.DoesNotContain("--enable-udp-broadcast-relay", args);
    }

    [Fact]
    public void OnByDefault()
    {
        Assert.True(EasyTierArgs.QuicProxyFrom(null));
        Assert.True(EasyTierArgs.QuicProxyFrom(""));
        Assert.True(EasyTierArgs.QuicProxyFrom("1"));
        Assert.False(EasyTierArgs.QuicProxyFrom("0"));
        Assert.False(EasyTierArgs.QuicProxyFrom(" 0 "));
        if (System.Environment.GetEnvironmentVariable(EasyTierArgs.QuicProxyVariable) is null)
            Assert.Contains("--enable-quic-proxy", EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 16000, 40000)));
    }

    [Fact]
    public void NotUsedWithFullLan()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, true, [], 16000, Tun: true) { QuicProxy = true });
        Assert.DoesNotContain("--enable-quic-proxy", args);
        Assert.Contains("--enable-udp-broadcast-relay", args);
    }
}
