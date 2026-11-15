using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public sealed class ExitRoutingPlanTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
    private static EasyTierOptions Guest => new(Room, false, ["udp://127.0.0.1:23000"], 10000,
        Tun: true, ExitNode: EasyTierArgs.HostIp) { DirectUnderlayAddresses = ["203.0.113.10", "192.168.1.2", "203.0.113.10"] };

    [Fact]
    public void ExitSelectionRequiresCaptureRoutesAndProtectsDirectUnderlay()
    {
        var plan = Assert.IsType<ExitNetworkPlan>(ExitNetworkPlan.Create(Guest));
        Assert.Equal(["192.168.1.2", "203.0.113.10"], plan.UnderlayAddresses);
        Assert.Equal(OperatingSystem.IsWindows() ? "Amber LAN-10000" : "amberlan-10000", plan.AdapterName);
        var args = EasyTierArgs.Build(Guest);
        var offset = args.IndexOf("--manual-routes");
        Assert.True(offset >= 0);
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1"], args.Skip(offset + 1).Take(2));
        Assert.Contains("--disable-p2p", args);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--enable-udp-broadcast-relay", args);
            Assert.Equal(plan.AdapterName, args[args.IndexOf("--dev-name") + 1]);
        }
    }

    [Fact]
    public void ProvidingExitAndOrdinaryLanDoNotCaptureLocalInternet()
    {
        foreach (var allow in new[] { false, true })
        {
            var options = Guest with { ExitNode = null, AllowExit = allow };
            Assert.Null(ExitNetworkPlan.Create(options));
            Assert.DoesNotContain("--manual-routes", EasyTierArgs.Build(options));
        }
    }

    [Fact]
    public void NodeModeKeepsNativePhysicalInterfaceBinding()
    {
        var args = EasyTierArgs.Build(Guest with { Peers = ["udp://203.0.113.10:11010"], DirectUnderlayAddresses = [] });
        Assert.Equal("true", args[args.IndexOf("--bind-device") + 1]);
        Assert.Contains("--disable-p2p", args);
        Assert.Contains("--manual-routes", args);
        Assert.Equal(["203.0.113.10"], ExitNetworkPlan.Create(Guest with
            { Peers = ["udp://203.0.113.10:11010"], DirectUnderlayAddresses = [] })!.UnderlayAddresses);
    }

    [Fact]
    public void MixedBridgeAndPublicFallbackFailsBeforeChangingNetwork()
    {
        Assert.Throws<InvalidOperationException>(() => EasyTierArgs.Build(Guest with
            { Peers = ["udp://127.0.0.1:23000", "udp://203.0.113.10:11010"] }));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("224.1.2.3")]
    [InlineData("255.255.255.255")]
    [InlineData("10.233.144.1")]
    [InlineData("example.com")]
    [InlineData("198.51.100.4; whoami")]
    public void InvalidUnderlayIsRejected(string address) =>
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with { DirectUnderlayAddresses = [address] }));

    [Fact]
    public void CannotSelectSelfOrUseUnlimitedUnderlayList()
    {
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with { ExitNode = EasyTierArgs.VirtualIp(Guest) }));
        Assert.Throws<ArgumentException>(() => ExitNetworkPlan.Create(Guest with
            { DirectUnderlayAddresses = Enumerable.Repeat("203.0.113.10", ExitNetworkPlan.MaxUnderlayAddresses + 1).ToArray() }));
    }

    [Fact]
    public void UntrackedIpv6OrHostnameUnderlayIsRejectedBeforeRouteChanges()
    {
        Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { DirectUnderlayUsesIpv6 = true }));
        // A relay named by domain is refused with its own message: its address would get a direct exception for every program.
        var e = Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { Peers = ["udp://relay.example:11010"] }));
        Assert.Equal(Amber.Core.Localization.L.T("core.mp.exit.relay_domain"), e.Message);
        Assert.Throws<InvalidOperationException>(() => ExitNetworkPlan.Create(Guest with { Peers = ["udp://[2001:db8::1]:11010"] }));
    }

    private static Func<string, CancellationToken, Task<System.Net.IPAddress[]>> Lookup(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(System.Net.IPAddress.Parse).ToArray());

    [Fact]
    public async Task DomainRelaysArePinnedToTheAddressThePlanProtects()
    {
        var options = await ExitRelays.PinAsync(Guest with { Peers = ["tcp://relay.example.org:11010", "wss://ws.example.org:443/et", "udp://127.0.0.1:23000"] },
            Lookup("2001:db8::7", "203.0.113.20", "203.0.113.21"));
        // TCP is pinned to one IPv4 address; WebSocket keeps its name (TLS needs it) and all its IPv4 addresses are protected.
        Assert.Equal(["tcp://203.0.113.20:11010", "wss://ws.example.org:443/et", "udp://127.0.0.1:23000"], options.Peers);
        Assert.Equal(["203.0.113.20", "203.0.113.21"], options.RelayAddresses["ws.example.org"]);
        var plan = Assert.IsType<ExitNetworkPlan>(ExitNetworkPlan.Create(options with { Peers = [.. options.Peers.Where(p => !p.Contains("127.0.0.1", StringComparison.Ordinal))] }));
        Assert.Contains("203.0.113.20", plan.UnderlayAddresses);
        Assert.Contains("203.0.113.21", plan.UnderlayAddresses);
        Assert.DoesNotContain(plan.UnderlayAddresses, a => a.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavingTheExitRestoresTheConfiguredRelays()
    {
        var configured = new[] { "tcp://relay.example.org:11010" };
        var pinned = await ExitRelays.PinAsync(Guest with { Peers = configured }, Lookup("203.0.113.20"));
        var left = await ExitRelays.PinAsync(pinned with { ExitNode = null }, Lookup("203.0.113.99"));
        Assert.Equal(configured, left.Peers);
        Assert.Null(left.ConfiguredPeers);
        Assert.Empty(left.RelayAddresses);
        // Choosing again resolves again, from the configured names.
        var again = await ExitRelays.PinAsync(left with { ExitNode = EasyTierArgs.HostIp }, Lookup("203.0.113.30"));
        Assert.Equal(["tcp://203.0.113.30:11010"], again.Peers);
    }

    [Fact]
    public async Task ARelayWithoutAnIpv4AddressSaysSo()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExitRelays.PinAsync(Guest with { Peers = ["tcp://relay.example.org:11010"] }, Lookup("2001:db8::7")));
        Assert.Contains("relay.example.org", e.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExitRelays.PinAsync(Guest with { Peers = ["tcp://relay.example.org:11010"] },
            (_, _) => Task.FromException<System.Net.IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound))));
    }

    [Fact]
    public async Task WithoutAnExitNothingIsLookedUp()
    {
        var options = Guest with { ExitNode = null, Peers = ["tcp://relay.example.org:11010"] };
        var same = await ExitRelays.PinAsync(options, (_, _) => throw new InvalidOperationException("looked up"));
        Assert.Equal(options.Peers, same.Peers);
    }
}
