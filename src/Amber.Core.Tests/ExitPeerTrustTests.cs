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
    private const byte Probe = 1, Ping = 4, Pong = 5;

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
                0 => ping.Buffer[1..^16].Select((b, i) => i >= 4 ? (byte)(b ^ 0xFF) : b).ToArray(),
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
            await forged.SendAsync(host.Seal(Pong, ping.Buffer[1..^16]), target, limit.Token);
            break;
        }
        while (!peer.Verified) await Task.Delay(20, limit.Token);

        static byte[] Replayed(uint seq, byte[] nonce)
        {
            var payload = new byte[4 + nonce.Length];
            BinaryPrimitives.WriteUInt32BigEndian(payload, seq);
            nonce.CopyTo(payload, 4);
            return payload;
        }
    }

    [Fact]
    public async Task KickingAPeerSaysItIsGone()
    {
        await using var host = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, true, 1, _ => { });
        await using var guest = new DirectBridge(DirectBridge.CreateSocket(), DirectTestKeys.Bridge, false, null, _ => { });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotNull(await guest.PunchAsync(Loopback(host), NatKind.Cone, TimeSpan.FromSeconds(2), limit.Token));
        var removed = new List<DirectPeer>();
        host.Removed += removed.Add;
        var peer = host.Peers.Single();
        host.Kick(peer);
        Assert.Equal([peer], removed);
        host.DropPeers();
        Assert.Single(removed);
    }
}
