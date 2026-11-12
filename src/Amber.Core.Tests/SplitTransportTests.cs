using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Multi-path split for member connections (mp-split-design-v3): n-of-n shares, per-leg handshakes bound to
/// leg index / count / epoch, host-side pairing rules, and how the joined stream ends.
/// </summary>
public sealed class SplitTransportTests
{
    private const int Epoch = 3;
    private const byte Context = 1;
    private static readonly TransportProfile Profile = TransportProfile.For(TransportMode.High);

    private static byte[] Pub(byte seed) => [.. Enumerable.Range(seed, 32).Select(i => (byte)i)];

    // ---- shares ----

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SharesRecombineAndNoSingleShareIsTheData(int legs)
    {
        var data = RandomNumberGenerator.GetBytes(4096);
        var shares = SplitStream.MakeShares(data, legs, 7);
        Assert.Equal(legs, shares.Length);
        var sum = new byte[data.Length];
        foreach (var share in shares)
        {
            Assert.Equal(shares[0][..6], share[..6]);
            var body = share[6..];
            Assert.NotEqual(data, body);
            for (var i = 0; i < sum.Length; i++) sum[i] ^= body[i];
        }
        Assert.Equal(data, sum);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AllZeroDataStillGivesRandomLookingShares(int legs)
    {
        var shares = SplitStream.MakeShares(new byte[16384], legs, 0);
        foreach (var share in shares)
        {
            var body = share[6..];
            // Not a proof, just a sanity check: every byte value shows up and no value dominates.
            var counts = new int[256];
            foreach (var b in body) counts[b]++;
            Assert.All(counts, c => Assert.InRange(c, 20, 140));
        }
    }

    [Fact]
    public void PadsAreNeverReused()
    {
        var a = SplitStream.MakeShares(new byte[1024], 2, 0);
        var b = SplitStream.MakeShares(new byte[1024], 2, 1);
        Assert.NotEqual(a[0][6..], b[0][6..]);
    }

    // ---- end to end over loopback TCP ----

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 16384)]
    [InlineData(2, 16385)]
    [InlineData(3, 1 << 20)]
    public async Task RoundTripsBothWaysAndClosesCleanly(int legs, int size)
    {
        await using var rig = await Rig.StartAsync(legs);
        var (client, host) = await rig.JoinAsync();
        var up = RandomNumberGenerator.GetBytes(size);
        var down = RandomNumberGenerator.GetBytes(size);
        var write = Task.WhenAll(client.WriteAsync(up).AsTask(), host.WriteAsync(down).AsTask());
        var got = await Task.WhenAll(ReadExactlyAsync(host, size), ReadExactlyAsync(client, size));
        await write;
        Assert.Equal(up, got[0]);
        Assert.Equal(down, got[1]);
        Assert.Equal(legs, host.LegCount);

        await client.DisposeAsync();
        Assert.Equal(0, await host.ReadAsync(new byte[1]));
        Assert.True(host.ClosedCleanly);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task ManySmallWritesArriveInOrder()
    {
        await using var rig = await Rig.StartAsync(2);
        var (client, host) = await rig.JoinAsync();
        var expected = new List<byte>();
        for (var i = 0; i < 2000; i++)
        {
            var b = new[] { (byte)i, (byte)(i >> 8) };
            expected.AddRange(b);
            await client.WriteAsync(b);
        }
        Assert.Equal(expected.ToArray(), await ReadExactlyAsync(host, expected.Count));
        await client.DisposeAsync();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task EachLegIsBoundToItsIndexCountAndEpoch()
    {
        await using var rig = await Rig.StartAsync(2);
        // Leg 0's handshake sent to leg 1's port: different prologue and psk, so it fails.
        await Assert.ThrowsAnyAsync<Exception>(() => rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), port: 1));
        // Right port, wrong leg count or old epoch.
        await Assert.ThrowsAnyAsync<Exception>(() => rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 3, Epoch), port: 1));
        await Assert.ThrowsAnyAsync<Exception>(() => rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 2, Epoch - 1), port: 1));
        // A plain (unsplit) member handshake doesn't pass on a split port either.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, rig.Ports[0]);
            await Assert.ThrowsAnyAsync<Exception>(() => MemberChannel.ConnectAsync(tcp.GetStream(), rig.Guest, Profile, "tunnel", [Context]));
        }
        Assert.Empty(rig.Joined);
    }

    [Fact]
    public async Task LegsOfTwoMembersNeverPairEvenWithTheSamePairingId()
    {
        await using var rig = await Rig.StartAsync(2);
        var other = rig.AddMember(Pub(100), "Mallory");
        var pairId = RandomNumberGenerator.GetBytes(32);
        var a = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, pairId);
        var b = await rig.ConnectLegAsync(other, new SplitLeg(1, 2, Epoch), 1, pairId);
        await AssertDroppedAsync(a);
        await AssertDroppedAsync(b);
        Assert.Empty(rig.Joined);
        Assert.True(rig.Registry.Rejected >= 1);
    }

    [Fact]
    public async Task DuplicateIndexDropsTheSet()
    {
        await using var rig = await Rig.StartAsync(3);
        var pairId = RandomNumberGenerator.GetBytes(32);
        var a = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 3, Epoch), 0, pairId);
        var again = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 3, Epoch), 0, pairId);
        await AssertDroppedAsync(a);
        await AssertDroppedAsync(again);
        // The pairing id is now burned: a late leg with it is dropped at once.
        var late = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 3, Epoch), 1, pairId);
        await AssertDroppedAsync(late);
        Assert.Empty(rig.Joined);
    }

    [Fact]
    public async Task AMissingLegTimesOutAndDropsTheOthers()
    {
        await using var rig = await Rig.StartAsync(2, pairTimeout: TimeSpan.FromMilliseconds(400));
        var a = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, RandomNumberGenerator.GetBytes(32));
        await AssertDroppedAsync(a);
        Assert.Equal(0, rig.Registry.PendingCount);
    }

    [Fact]
    public async Task KickWhileWaitingDropsTheWaitingLeg()
    {
        await using var rig = await Rig.StartAsync(2);
        var a = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, RandomNumberGenerator.GetBytes(32));
        Assert.True(await WaitAsync(() => rig.Registry.PendingCount == 1));
        rig.Table.Remove(rig.GuestEntry);
        await AssertDroppedAsync(a);
        Assert.Equal(0, rig.Registry.PendingCount);
    }

    [Fact]
    public async Task KickAfterJoiningClosesEveryLeg()
    {
        await using var rig = await Rig.StartAsync(2, abortOnKick: true);
        var (client, _) = await rig.JoinAsync();
        rig.Table.Remove(rig.GuestEntry);
        await AssertNotCleanEndAsync(client);
    }

    [Fact]
    public async Task OnePerMemberLimitOnWaitingSets()
    {
        await using var rig = await Rig.StartAsync(2);
        var waiting = new List<SecureStream>();
        for (var i = 0; i < SplitRegistry.MaxPendingPerMember; i++)
            waiting.Add(await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, RandomNumberGenerator.GetBytes(32)));
        Assert.True(await WaitAsync(() => rig.Registry.PendingCount == SplitRegistry.MaxPendingPerMember));
        var extra = await rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, RandomNumberGenerator.GetBytes(32));
        await AssertDroppedAsync(extra);
        Assert.Equal(SplitRegistry.MaxPendingPerMember, rig.Registry.PendingCount);
        foreach (var w in waiting) w.Abort();
    }

    [Fact]
    public async Task MismatchedChunkHeadersAreTampering()
    {
        await using var rig = await Rig.StartAsync(2);
        var pairId = RandomNumberGenerator.GetBytes(32);
        var legs = await Task.WhenAll(
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, pairId),
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 2, Epoch), 1, pairId));
        var host = await rig.NextJoinedAsync();
        var tampered = 0;
        host.Tampered += () => Interlocked.Increment(ref tampered);
        // Same length, different sequence numbers on the two legs.
        await legs[0].WriteAsync((byte[])[0, 0, 0, 0, 0, 1, 0xAA]);
        await legs[1].WriteAsync((byte[])[0, 0, 0, 5, 0, 1, 0xBB]);
        await Assert.ThrowsAsync<CryptographicException>(async () => await host.ReadAsync(new byte[8]));
        Assert.Equal(1, tampered);
        await AssertDroppedAsync(legs[0]);
        await AssertDroppedAsync(legs[1]);
    }

    [Fact]
    public async Task OneLegEndingIsNeverACleanEnd()
    {
        await using var rig = await Rig.StartAsync(2);
        var pairId = RandomNumberGenerator.GetBytes(32);
        var legs = await Task.WhenAll(
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, pairId),
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 2, Epoch), 1, pairId));
        var host = await rig.NextJoinedAsync();
        // Leg 0 closes properly (authenticated close record); leg 1 stays open and quiet.
        await legs[0].CloseAsync();
        var ex = await Assert.ThrowsAsync<IOException>(async () => await host.ReadAsync(new byte[8]));
        Assert.False(host.ClosedCleanly);
        await AssertDroppedAsync(legs[1]);
    }

    [Fact]
    public async Task ASilentLegWhileAnotherDeliversIsDropped()
    {
        await using var rig = await Rig.StartAsync(2, stallTimeout: TimeSpan.FromMilliseconds(500));
        var pairId = RandomNumberGenerator.GetBytes(32);
        var legs = await Task.WhenAll(
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(0, 2, Epoch), 0, pairId),
            rig.ConnectLegAsync(rig.Guest, new SplitLeg(1, 2, Epoch), 1, pairId));
        var host = await rig.NextJoinedAsync();
        await legs[0].WriteAsync((byte[])[0, 0, 0, 0, 0, 1, 0xAA]);
        await Assert.ThrowsAsync<IOException>(async () => await host.ReadAsync(new byte[8]));
        await AssertDroppedAsync(legs[1]);
    }

    [Fact]
    public async Task CallerCancellationAbortsWithoutCloseRecords()
    {
        await using var rig = await Rig.StartAsync(2);
        var (client, host) = await rig.JoinAsync();
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.ReadAsync(new byte[8], cts.Token));
        await AssertNotCleanEndAsync(client);
    }

    [Fact]
    public void LegShapeIsValidated()
    {
        Assert.True(new SplitLeg(0, 2, 1).IsValid);
        Assert.True(new SplitLeg(2, 3, 9).IsValid);
        Assert.False(new SplitLeg(2, 2, 1).IsValid);
        Assert.False(new SplitLeg(0, 1, 1).IsValid);
        Assert.False(new SplitLeg(0, 4, 1).IsValid);
        Assert.False(new SplitLeg(0, 2, 0).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitStream(Array.Empty<SecureStream>()));
    }

    [Fact]
    public void LegPsksDifferFromEachOtherAndFromTheMemberPsk()
    {
        var key = MemberKey.Create();
        var plain = MemberChannel.PskFor(key, null);
        var a = MemberChannel.PskFor(key, new SplitLeg(0, 2, 1));
        var b = MemberChannel.PskFor(key, new SplitLeg(1, 2, 1));
        Assert.Equal(key.CopyPsk(), plain);
        Assert.NotEqual(plain, a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, MemberChannel.PskFor(key, new SplitLeg(0, 3, 7)));
    }

    // ---- helpers ----

    private static async Task<byte[]> ReadExactlyAsync(Stream s, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    /// <summary>The stream ended without a clean close: reading throws, or ends with ClosedCleanly false.</summary>
    private static async Task AssertNotCleanEndAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var buffer = new byte[64];
            while (await stream.ReadAsync(buffer, timeout.Token) > 0) { }
        }
        catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException or SocketException)
        {
            return;
        }
        Assert.False(((IAuthenticatedStream)stream).ClosedCleanly);
    }

    /// <summary>The host dropped this leg: reading ends without a clean close (or throws).</summary>
    private static async Task AssertDroppedAsync(SecureStream leg)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var buffer = new byte[64];
            while (await leg.ReadAsync(buffer, timeout.Token) > 0) { }
            Assert.False(leg.ClosedCleanly);
        }
        catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException or SocketException) { }
        finally
        {
            leg.Abort();
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 5)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>A host with one loopback listener per leg, a member table and a split registry.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<TcpListener> _listeners = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _loops = [];
        private readonly System.Threading.Channels.Channel<SplitStream> _joined = System.Threading.Channels.Channel.CreateUnbounded<SplitStream>();
        private readonly List<SplitStream> _all = [];
        private int _count;
        private bool _abortOnKick;

        public MemberTable Table { get; } = new();
        public SplitRegistry Registry { get; private set; } = new();
        public MemberEntry GuestEntry { get; private set; } = null!;
        public MemberKey Guest { get; private set; } = null!;
        public int[] Ports { get; private set; } = [];
        public IReadOnlyList<SplitStream> Joined { get { lock (_all) return [.. _all]; } }

        public static Task<Rig> StartAsync(int legs, TimeSpan? pairTimeout = null, TimeSpan? stallTimeout = null, bool abortOnKick = false)
        {
            var rig = new Rig { _count = legs, _abortOnKick = abortOnKick };
            rig.Registry = new SplitRegistry
            {
                PairTimeout = pairTimeout ?? TimeSpan.FromSeconds(10),
                StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(15),
                EndGrace = stallTimeout ?? TimeSpan.FromSeconds(3),
            };
            rig.Guest = rig.AddMember(Pub(1), "Alice", out var entry);
            rig.GuestEntry = entry;
            var ports = new int[legs];
            for (var i = 0; i < legs; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                rig._listeners.Add(listener);
                ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port;
                var index = i;
                rig._loops.Add(Task.Run(() => rig.AcceptLoopAsync(listener, index)));
            }
            rig.Ports = ports;
            return Task.FromResult(rig);
        }

        public MemberKey AddMember(byte[] pub, string name) => AddMember(pub, name, out _);

        private MemberKey AddMember(byte[] pub, string name, out MemberEntry entry)
        {
            entry = Table.Issue(pub, name);
            // The member's own copy (from the knock reply); the host's copy is zeroed on kick.
            return new MemberKey(entry.Key.Id, entry.Key.CopyPsk());
        }

        private async Task AcceptLoopAsync(TcpListener listener, int index)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var leg = new SplitLeg(index, _count, Epoch);
                        var (secure, member, payload) = await MemberChannel.AcceptAsync(tcp.GetStream(), Table, Profile, "tunnel",
                            p => p is [Context], _cts.Token, leg);
                        var joined = await Registry.AcceptAsync(secure, member, leg, payload[0], _cts.Token);
                        if (joined is null) return;
                        if (_abortOnKick) member.Revocation.Register(joined.Abort);
                        lock (_all) _all.Add(joined);
                        await _joined.Writer.WriteAsync(joined);
                    }
                    catch (Exception e) when (e is IOException or CryptographicException or OperationCanceledException or ObjectDisposedException or SocketException or EndOfStreamException)
                    {
                        tcp.Dispose();
                    }
                });
            }
        }

        public async Task<SplitStream> NextJoinedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _joined.Reader.ReadAsync(timeout.Token);
        }

        /// <summary>One member-side leg: handshake on the given port, then (unless pairId is null) the hello.</summary>
        public async Task<SecureStream> ConnectLegAsync(MemberKey key, SplitLeg leg, int port, byte[]? pairId = null)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, Ports[port]);
            var secure = await MemberChannel.ConnectAsync(tcp.GetStream(), key, Profile, "tunnel", [Context], default, leg);
            pairId ??= RandomNumberGenerator.GetBytes(32);
            await secure.WriteAsync((byte[])[SplitLegs.HelloVersion, .. pairId]);
            return secure;
        }

        public async Task<(SplitStream Client, SplitStream Host)> JoinAsync()
        {
            var client = await SplitLegs.ConnectAsync(async (i, ct) =>
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, Ports[i], ct);
                return tcp.GetStream();
            }, _count, Epoch, Guest, Profile, "tunnel", [Context]);
            return (client, await NextJoinedAsync());
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            foreach (var l in _listeners) l.Stop();
            foreach (var s in Joined) s.Abort();
            try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}
