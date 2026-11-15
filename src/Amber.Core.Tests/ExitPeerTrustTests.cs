using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Which bridge peers the member exit may trust with a traffic-gate exception: an IPv6 peer must be admitted and must
/// have answered a fresh random ping from its own address. Probes alone, replayed answers and answers to the wrong
/// ping never count.
/// </summary>
public sealed class ExitPeerTrustTests
{
    private const byte Probe = 1, Ping = 4, Pong = 5, Guest = 0, Host = 1;

    private static List<Candidate> Loopback(DirectBridge bridge) =>
        [new(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, bridge.SocketEndPoint.Port))];

    private static DirectPeer Peer(string address, bool admitted, bool verified) =>
        new() { Remote = new IPEndPoint(IPAddress.Parse(address), 40000), Path = PathKind.IPv6, Admitted = admitted, _verified = verified };

    [Fact]
    public void OnlyAdmittedPeersThatAnsweredAFreshPingAreTrustedOverIpv6()
    {
        var trusted = DirectSession.TrustedPeerAddresses([
            Peer("2001:db8::1", admitted: true, verified: true),
            // Only probed (or a replayed probe from a forged source): never answered a ping of this bridge.
            Peer("2001:db8::2", admitted: true, verified: false),
            // Answered, but not (yet) a member of the room.
            Peer("2001:db8::3", admitted: false, verified: true),
            // IPv4 peers keep their earlier treatment.
            Peer("203.0.113.30", admitted: false, verified: false),
        ]).Select(a => a.ToString()).ToArray();
        Assert.Equal(["2001:db8::1", "203.0.113.30"], trusted);
    }

    [Fact]
    public async Task ARealPeerIsVerifiedByItsPingAnswers()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2), limit.Token));
        while (!(host.Peers.Single().Verified && guest.Peers.Single().Verified)) await Task.Delay(50, limit.Token);
        Assert.False(host.Peers.Single().Outdated || guest.Peers.Single().Outdated);
    }

    [Fact]
    public async Task AProbeWithoutTheRightPingAnswerIsNeverVerified()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        using var forged = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A correctly sealed probe (as replayed by someone on the path) gets the address registered...
        await forged.SendAsync(host.Seal(Probe, [0]), target, limit.Token);
        DirectPeer? peer = null;
        while ((peer = host.Peers.SingleOrDefault()) is null) await Task.Delay(20, limit.Token);

        // ...but answers to its pings that don't echo that ping's random value don't verify it: an answer to another
        // ping (an old, replayed one), one with a different random value, and one carrying only the sequence number.
        uint? previous = null;
        for (var answered = 0; answered < 3;)
        {
            var ping = await forged.ReceiveAsync(limit.Token);
            if (ping.Buffer[0] != Ping) continue;
            var seq = BinaryPrimitives.ReadUInt32BigEndian(ping.Buffer.AsSpan(1));
            byte[] payload = answered switch
            {
                0 => [.. ping.Buffer[1..^16].Select((b, i) => i is >= 4 and < 4 + DirectBridge.PingNonceLength ? (byte)(b ^ 0xFF) : b), Guest],
                1 => ping.Buffer[1..5],
                _ => Replayed(previous ?? seq, ping.Buffer[5..^16]),
            };
            previous = seq;
            await forged.SendAsync(host.Seal(Pong, payload), target, limit.Token);
            answered++;
        }
        await Task.Delay(300, limit.Token);
        Assert.False(peer.Verified);

        // The exact echo of the latest ping does verify it: the check is on the answer, not on who sent the probe.
        while (true)
        {
            var ping = await forged.ReceiveAsync(limit.Token);
            if (ping.Buffer[0] != Ping) continue;
            await forged.SendAsync(host.Seal(Pong, [.. ping.Buffer[1..^16], Guest]), target, limit.Token);
            break;
        }
        while (!peer.Verified) await Task.Delay(20, limit.Token);

        static byte[] Replayed(uint seq, byte[] nonce)
        {
            var payload = new byte[4 + nonce.Length + 1];
            BinaryPrimitives.WriteUInt32BigEndian(payload, seq);
            nonce.CopyTo(payload, 4);
            payload[^1] = Guest;
            return payload;
        }
    }

    [Fact]
    public async Task ShortOrWrongPingAnswersLeaveNothingBehind()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        using var forged = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await forged.SendAsync(host.Seal(Probe, [0]), target, limit.Token);
        DirectPeer? peer = null;
        while ((peer = host.Peers.SingleOrDefault()) is null) await Task.Delay(20, limit.Token);
        // Answer every ping with its sequence number only, or with a few bytes of it: each answered ping is forgotten
        // whole (its time and its random value), so nothing piles up.
        for (var answered = 0; answered < 6;)
        {
            var ping = await forged.ReceiveAsync(limit.Token);
            if (ping.Buffer[0] != Ping) continue;
            var length = answered % 2 == 0 ? 4 : 4 + answered;
            await forged.SendAsync(host.Seal(Pong, ping.Buffer.AsSpan(1, length)), target, limit.Token);
            answered++;
            await Task.Delay(50, limit.Token);
            Assert.True(peer.PendingPings.Count <= 1, $"{peer.PendingPings.Count} pings still pending");
        }
        Assert.False(peer.Verified);
    }

    [Fact]
    public async Task SomeoneWhoOnlySendsPacketsBackCannotVerifyAnAddress()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        using var reflector = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // A replayed probe of a real guest registers the address; from then on this side holds no key and can only
        // send the host's own packets back to it.
        await reflector.SendAsync(host.Seal(Probe, [Guest]), target, limit.Token);
        DirectPeer? peer = null;
        while ((peer = host.Peers.SingleOrDefault()) is null) await Task.Delay(20, limit.Token);
        var pongs = 0;
        for (var reflected = 0; reflected < 4;)
        {
            var packet = await reflector.ReceiveAsync(limit.Token);
            if (packet.Buffer[0] == Pong) pongs++;
            if (packet.Buffer[0] != Ping) continue;
            await reflector.SendAsync(packet.Buffer, target, limit.Token);
            reflected++;
        }
        await Task.Delay(300, limit.Token);
        while (reflector.Available > 0)
            if ((await reflector.ReceiveAsync(limit.Token)).Buffer[0] == Pong) pongs++;
        // The host never answered its own pings, so there was never an answer to send back.
        Assert.Equal(0, pongs);
        Assert.False(peer.Verified);
    }

    [Fact]
    public async Task AnAnswerThatClaimsToComeFromTheSameSideDoesNotVerify()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        using var forged = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await forged.SendAsync(host.Seal(Probe, [Guest]), target, limit.Token);
        DirectPeer? peer = null;
        while ((peer = host.Peers.SingleOrDefault()) is null) await Task.Delay(20, limit.Token);
        for (var answered = 0; answered < 3;)
        {
            var ping = await forged.ReceiveAsync(limit.Token);
            if (ping.Buffer[0] != Ping) continue;
            // The exact echo, but marked as answered by a host (what the host itself would send).
            await forged.SendAsync(host.Seal(Pong, [.. ping.Buffer[1..^16], Host]), target, limit.Token);
            answered++;
        }
        await Task.Delay(300, limit.Token);
        Assert.False(peer.Verified);
    }

    [Fact]
    public void AGuestThatJoinedThroughABackupNodeGivesItsIdleBridgeNoException()
    {
        DirectPeer[] peers = [Peer("2001:db8::1", admitted: true, verified: true), Peer("203.0.113.30", admitted: false, verified: false)];
        // The room runs over the backup node; whatever answers on the idle bridge later carries nothing of it.
        Assert.Empty(DirectSession.BridgePeersCarryingTheRoom(isHost: false, usingFallbackNode: true, peers));
        Assert.Equal(peers, DirectSession.BridgePeersCarryingTheRoom(isHost: false, usingFallbackNode: false, peers));
        Assert.Equal(peers, DirectSession.BridgePeersCarryingTheRoom(isHost: true, usingFallbackNode: false, peers));
    }

    [Fact]
    public void AnIpv6LimitTurnsAwayOnlyIpv6AddressesOutsideIt()
    {
        var planned = IPAddress.Parse("2001:db8::7");
        var limit = new HashSet<IPAddress> { planned };
        Assert.False(DirectBridge.Ipv6Held(null, IPAddress.Parse("2001:db8::9")));
        Assert.False(DirectBridge.Ipv6Held(limit, planned));
        Assert.True(DirectBridge.Ipv6Held(limit, IPAddress.Parse("2001:db8::9")));
        Assert.True(DirectBridge.Ipv6Held(new HashSet<IPAddress>(), planned));
        // IPv4, also as a dual-stack socket reports it, is never held.
        Assert.False(DirectBridge.Ipv6Held(new HashSet<IPAddress>(), IPAddress.Parse("203.0.113.5")));
        Assert.False(DirectBridge.Ipv6Held(new HashSet<IPAddress>(), IPAddress.Parse("203.0.113.5").MapToIPv6()));
    }

    [Fact]
    public async Task WithAnIpv6LimitNewIpv6PeersOutsideItAreTurnedAwayAndIpv4IsNot()
    {
        if (!Socket.OSSupportsIPv6) return;
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        if (host.SocketEndPoint.AddressFamily != AddressFamily.InterNetworkV6) return;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var v6Target = new IPEndPoint(IPAddress.IPv6Loopback, host.SocketEndPoint.Port);
        var v4Target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        host.Ipv6Allowed = new HashSet<IPAddress>();

        using var v6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        for (var i = 0; i < 5; i++)
        {
            await v6.SendAsync(host.Seal(Probe, [Guest]), v6Target, limit.Token);
            await Task.Delay(50, limit.Token);
        }
        await Task.Delay(200, limit.Token);
        Assert.Empty(host.Peers);

        using var v4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await v4.SendAsync(host.Seal(Probe, [Guest]), v4Target, limit.Token);
        while (host.Peers.Count == 0) await Task.Delay(20, limit.Token);

        host.Ipv6Allowed = new HashSet<IPAddress> { IPAddress.IPv6Loopback };
        await v6.SendAsync(host.Seal(Probe, [Guest]), v6Target, limit.Token);
        while (host.Peers.Count < 2) await Task.Delay(20, limit.Token);
    }

    [Fact]
    public async Task AnOlderVersionsPingsAreStillAnsweredButItsAnswersNeverVerify()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        using var older = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, host.SocketEndPoint.Port);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await older.SendAsync(host.Seal(Probe, [Guest]), target, limit.Token);
        DirectPeer? peer = null;
        while ((peer = host.Peers.SingleOrDefault()) is null) await Task.Delay(20, limit.Token);

        // Its own ping, sequence number only, comes back as it was sent.
        await older.SendAsync(host.Seal(Ping, [0, 0, 0, 9]), target, limit.Token);
        while (true)
        {
            var packet = await older.ReceiveAsync(limit.Token);
            if (packet.Buffer[0] != Pong) continue;
            Assert.Equal(new byte[] { 0, 0, 0, 9 }, packet.Buffer[1..^16]);
            break;
        }
        Assert.True(peer.Outdated);
        // It echoes our pings whole, without its side: they count for round-trip times, not for verifying.
        var before = peer.PongsReceived;
        for (var answered = 0; answered < 2;)
        {
            var ping = await older.ReceiveAsync(limit.Token);
            if (ping.Buffer[0] != Ping) continue;
            await older.SendAsync(host.Seal(Pong, ping.Buffer[1..^16]), target, limit.Token);
            answered++;
        }
        while (peer.PongsReceived < before + 2) await Task.Delay(20, limit.Token);
        Assert.False(peer.Verified);
    }
}
