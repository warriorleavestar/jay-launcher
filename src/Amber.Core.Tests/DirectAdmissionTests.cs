using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Who a direct bridge takes, and for how long: bridges that have not proved membership are capped (overall and per IP
/// address) and, when no invite is open, reach nothing and are dropped unless they prove membership; a guest replaces a
/// host that went silent with the host's new address.
/// </summary>
public sealed class DirectAdmissionTests
{
    private const byte Probe = 1, ProbeAck = 2, Data = 0x10;

    private static IPEndPoint Endpoint(DirectBridge bridge) => new(IPAddress.Loopback, bridge.SocketEndPoint.Port);

    /// <summary>Sends one authenticated probe from a fresh port; true when the bridge answered it (took the peer).</summary>
    private static async Task<bool> ProbeFromNewPortAsync(DirectBridge host, List<UdpClient> keep)
    {
        var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        keep.Add(client);
        await client.SendAsync(host.Seal(Probe, [0]), Endpoint(host));
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (true)
                if ((await client.ReceiveAsync(wait.Token)).Buffer[0] == ProbeAck) return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task UnprovenBridgesFromOneAddressAreCapped()
    {
        using var lobby = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { },
            ((IPEndPoint)lobby.Client.LocalEndPoint!).Port);
        var clients = new List<UdpClient>();
        try
        {
            var accepted = 0;
            for (var i = 0; i < DirectBridge.MaxUnadmittedPerAddress + 3; i++)
                if (await ProbeFromNewPortAsync(host, clients)) accepted++;
            Assert.Equal(DirectBridge.MaxUnadmittedPerAddress, accepted);
            Assert.Equal(DirectBridge.MaxUnadmittedPerAddress, host.Peers.Count);
            Assert.True(DirectBridge.MaxUnadmittedPeers < DirectBridge.MaxHostPeers);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    [Fact]
    public async Task BridgeTakenWhileClosedReachesNothingAndIsDroppedUnlessItProvesMembership()
    {
        using var lobby = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { },
            ((IPEndPoint)lobby.Client.LocalEndPoint!).Port)
        {
            AcceptingNewPeers = () => false,
            AdmitOnlyWindow = TimeSpan.FromMilliseconds(1500),
        };
        var asked = 0;
        host.AdmissionWanted += _ => Interlocked.Increment(ref asked);
        var clients = new List<UdpClient>();
        try
        {
            Assert.True(await ProbeFromNewPortAsync(host, clients));
            var peer = Assert.Single(host.Peers);
            Assert.False(peer.Admitted);
            Assert.Null(peer.Local);
            // Data goes nowhere, not even to the lobby.
            await clients[0].SendAsync(new byte[] { Data, 1, 2, 3 }, Endpoint(host));
            using (var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lobby.ReceiveAsync(none.Token));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (host.Peers.Count > 0) await Task.Delay(100, limit.Token);
            Assert.True(Volatile.Read(ref asked) >= 1);
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    [Fact]
    public async Task UnauthenticatedDataDoesNotKeepAnUnprovenBridge()
    {
        using var lobby = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { },
            ((IPEndPoint)lobby.Client.LocalEndPoint!).Port) { PeerTimeout = TimeSpan.FromMilliseconds(600) };
        var clients = new List<UdpClient>();
        try
        {
            Assert.True(await ProbeFromNewPortAsync(host, clients));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (host.Peers.Count > 0)
            {
                await clients[0].SendAsync(new byte[] { Data, 1 }, Endpoint(host));
                await Task.Delay(100, limit.Token);
            }
        }
        finally
        {
            foreach (var c in clients) c.Dispose();
        }
    }

    /// <summary>
    /// The host's address changed: the old one went silent and the host's probes now come from a new address. The guest
    /// takes the new one once the old one has been quiet for a while, and not before.
    /// </summary>
    [Fact]
    public async Task GuestTakesTheHostsNewAddressAfterTheOldOneWentSilent()
    {
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { })
            { HostReplaceAfter = TimeSpan.FromSeconds(1.5) };
        List<Candidate> guestAddress = [new(PathKind.Lan, Endpoint(guest))];
        var oldHost = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        Assert.NotNull(await oldHost.PunchAsync(guestAddress, NatKind.Cone, TimeSpan.FromSeconds(5)));
        await using var newHost = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        // While the old address still answers, the new one is refused.
        Assert.Null(await newHost.PunchAsync(guestAddress, NatKind.Cone, TimeSpan.FromSeconds(0.8)));
        await oldHost.DisposeAsync();
        var moved = await newHost.PunchAsync(guestAddress, NatKind.Cone, TimeSpan.FromSeconds(6));
        Assert.NotNull(moved);
        Assert.Equal(newHost.SocketEndPoint.Port, Assert.Single(guest.Peers).Remote.Port);
    }

    /// <summary>A guest drops its host after the peer timeout, like the host does, so the bridge can connect again.</summary>
    [Fact]
    public async Task GuestDropsASilentHost()
    {
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { })
            { PeerTimeout = TimeSpan.FromMilliseconds(500) };
        var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        Assert.NotNull(await guest.PunchAsync([new(PathKind.Lan, Endpoint(host))], NatKind.Cone, TimeSpan.FromSeconds(5)));
        await host.DisposeAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (guest.Peers.Count > 0) await Task.Delay(100, limit.Token);
    }
}
