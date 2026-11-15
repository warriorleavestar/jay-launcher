using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// An on-path attacker between two members rewrites the client-to-host byte stream of a wrapped tunnel
/// while it flows: flip a bit, drop bytes, repeat a slice, swap two slices, or cut the stream.
/// Whatever it does, the host may only ever deliver a prefix of what the client really sent (no altered,
/// replayed or reordered data), and it may only report a clean close when the client's close record
/// arrived intact. The handshake failing is also fine.
/// </summary>
public class AdversarialTransportTests
{
    private static readonly RoomCode Room = RoomCode.Create(25565);

    // Records of different sizes, so attacks land in headers, padding, rekeys and payloads.
    private static readonly byte[][] Messages = Enumerable.Range(0, 6)
        .Select(i => Enumerable.Range(0, 37 + i * 911).Select(j => (byte)(i * 31 + j)).ToArray())
        .ToArray();

    private static readonly byte[] Sent = Messages.SelectMany(m => m).ToArray();

    public static TheoryData<TransportMode, bool> Modes()
    {
        var data = new TheoryData<TransportMode, bool>();
        foreach (var mode in new[] { TransportMode.Minecraft, TransportMode.High, TransportMode.Extreme })
            foreach (var disguise in new[] { false, true }) data.Add(mode, disguise);
        return data;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task UntouchedStreamDeliversEverything(TransportMode mode, bool disguise)
    {
        var o = await RunAsync(mode, disguise, new Rewrite());
        Assert.True(o.HandshakeOk);
        Assert.Equal(Sent, o.Received);
        Assert.True(o.ClosedCleanly);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BitFlipsAfterTheHandshakeAreNeverAccepted(TransportMode mode, bool disguise)
    {
        var clean = await RunAsync(mode, disguise, new Rewrite());
        var rnd = new Random(Seed(mode, disguise));
        foreach (var at in Spots(clean, rnd, 20))
        {
            var rewrite = new Rewrite { FlipAt = at };
            var o = await RunAsync(mode, disguise, rewrite);
            AssertSafe(o, $"flip at {at} (handshake {o.HandshakeBytes}, total {o.WireBytes})");
            // Lengths vary a little between runs (random login name, padding, cover records), so only judge
            // flips that really landed after this run's handshake.
            if (!rewrite.Applied || at < o.HandshakeBytes) continue;
            Assert.False(o.ClosedCleanly && o.Received.Length == Sent.Length, $"flip at {at} went unnoticed");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task BitFlipsInTheHandshakeFailTheHandshake(TransportMode mode, bool disguise)
    {
        var clean = await RunAsync(mode, disguise, new Rewrite());
        var rnd = new Random(Seed(mode, disguise) + 50);
        for (var i = 0; i < 6; i++)
        {
            var at = rnd.Next(clean.HandshakeBytes);
            var rewrite = new Rewrite { FlipAt = at };
            var o = await RunAsync(mode, disguise, rewrite, TimeSpan.FromSeconds(4));
            AssertSafe(o, $"handshake flip at {at}");
            // With the Minecraft disguise the login packet's name and the 128 random "verify token" bytes are
            // cover, not part of the Noise messages, so a flip there may go unnoticed; the session data is
            // still checked by AssertSafe. Without the disguise every handshake byte is authenticated.
            if (!disguise) Assert.False(o.ClosedCleanly, $"handshake flip at {at} still produced a clean session");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task DroppedReplayedOrSwappedBytesAreNeverAccepted(TransportMode mode, bool disguise)
    {
        var clean = await RunAsync(mode, disguise, new Rewrite());
        var rnd = new Random(Seed(mode, disguise) + 100);
        var dataBytes = clean.WireBytes - clean.HandshakeBytes;
        for (var i = 0; i < 15; i++)
        {
            var len = 1 + rnd.Next(64);
            var at = clean.HandshakeBytes + rnd.Next(dataBytes - 2 * len);
            var rewrite = (i % 3) switch
            {
                0 => new Rewrite { DropAt = at, Length = len },
                1 => new Rewrite { ReplayAt = at, Length = len },
                _ => new Rewrite { SwapAt = at, Length = len },
            };
            var o = await RunAsync(mode, disguise, rewrite);
            AssertSafe(o, $"{rewrite}");
            if (!rewrite.Applied) continue;
            Assert.False(o.ClosedCleanly, $"{rewrite} ended in an authenticated close");
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task CutStreamIsNeverACleanClose(TransportMode mode, bool disguise)
    {
        var clean = await RunAsync(mode, disguise, new Rewrite());
        var rnd = new Random(Seed(mode, disguise) + 200);
        var cuts = Spots(clean, rnd, 8).Append(clean.WireBytes - 1);
        foreach (var cut in cuts)
        {
            var rewrite = new Rewrite { CutAt = cut };
            var o = await RunAsync(mode, disguise, rewrite);
            AssertSafe(o, $"cut at {cut}/{o.WireBytes}");
            if (!rewrite.Applied) continue;
            Assert.False(o.ClosedCleanly, $"cut at {cut} looked like a clean close");
        }
    }

    [Fact]
    public async Task WrappedStreamCarriesNoPlaintext()
    {
        // A passive observer must not find any 16-byte run of the game data on the wire.
        foreach (var mode in new[] { TransportMode.Minecraft, TransportMode.High, TransportMode.Extreme })
        {
            var rewrite = new Rewrite();
            await RunAsync(mode, false, rewrite);
            var wire = rewrite.Wire.ToArray();
            foreach (var m in Messages)
                for (var i = 0; i + 16 <= m.Length; i += 16)
                    Assert.Equal(-1, wire.AsSpan().IndexOf(m.AsSpan(i, 16)));
        }
    }

    private static int Seed(TransportMode mode, bool disguise) => (int)mode * 2 + (disguise ? 1 : 0);

    /// <summary>Spots after the handshake, including the first and last data bytes.</summary>
    private static IEnumerable<int> Spots(Outcome clean, Random rnd, int count)
    {
        yield return clean.HandshakeBytes;
        yield return clean.WireBytes - 1;
        for (var i = 0; i < count; i++) yield return clean.HandshakeBytes + rnd.Next(clean.WireBytes - clean.HandshakeBytes);
    }

    private static void AssertSafe(Outcome o, string what)
    {
        Assert.True(o.Received.Length <= Sent.Length, $"{what}: host delivered more than was sent");
        Assert.True(Sent.AsSpan(0, o.Received.Length).SequenceEqual(o.Received), $"{what}: host delivered bytes the client never sent");
        if (o.ClosedCleanly) Assert.Equal(Sent.Length, o.Received.Length);
    }

    /// <summary>
    /// Rewrites the client's stream byte by byte as it passes. Held bytes (for a swap) are released when the
    /// swap completes or the client finishes. Positions count the client's original bytes.
    /// </summary>
    private sealed class Rewrite
    {
        public int FlipAt { get; init; } = -1;
        public int DropAt { get; init; } = -1;
        public int ReplayAt { get; init; } = -1;
        public int SwapAt { get; init; } = -1;
        public int CutAt { get; init; } = -1;
        public int Length { get; init; }
        public MemoryStream Wire { get; } = new();

        /// <summary>True once the stream reached the spot where the change starts (and, for swaps and replays, ends).</summary>
        public bool Applied { get; private set; }

        private readonly List<byte> _replay = [];
        private readonly List<byte> _held = [];
        private readonly List<byte> _second = [];
        private bool _cut;

        public void Feed(int pos, byte b, List<byte> output)
        {
            Wire.WriteByte(b);
            if (pos == FlipAt || pos == CutAt || pos == DropAt + Length - 1 && DropAt >= 0 || pos == ReplayAt + Length - 1 && ReplayAt >= 0) Applied = true;
            if (_cut || pos == CutAt) { _cut = true; return; }
            if (pos == FlipAt) b ^= (byte)(1 << (pos % 8));
            if (DropAt >= 0 && pos >= DropAt && pos < DropAt + Length) return;
            if (SwapAt >= 0 && pos >= SwapAt && pos < SwapAt + Length) { _held.Add(b); return; }
            output.Add(b);
            if (SwapAt >= 0 && pos >= SwapAt + Length && pos < SwapAt + 2 * Length) _second.Add(b);
            if (SwapAt >= 0 && pos == SwapAt + 2 * Length - 1)
            {
                // Swapping two identical slices changes nothing on the wire, so it doesn't count as an attack.
                Applied = !_held.SequenceEqual(_second);
                output.AddRange(_held);
                _held.Clear();
            }
            if (ReplayAt >= 0 && pos >= ReplayAt && pos < ReplayAt + Length)
            {
                _replay.Add(b);
                if (pos == ReplayAt + Length - 1) output.AddRange(_replay);
            }
        }

        public void Finish(List<byte> output)
        {
            if (!_cut) output.AddRange(_held);
            _held.Clear();
        }

        public override string ToString() =>
            DropAt >= 0 ? $"drop {Length} at {DropAt}" : ReplayAt >= 0 ? $"replay {Length} at {ReplayAt}" :
            SwapAt >= 0 ? $"swap {Length} at {SwapAt}" : CutAt >= 0 ? $"cut at {CutAt}" : FlipAt >= 0 ? $"flip at {FlipAt}" : "none";
    }

    private sealed record Outcome(bool HandshakeOk, byte[] Received, bool ClosedCleanly, int HandshakeBytes, int WireBytes);

    private static async Task<Outcome> RunAsync(TransportMode mode, bool disguise, Rewrite rewrite, TimeSpan? limit = null)
    {
        using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var (clientRaw, attackerIn, a1, b1) = await PairAsync();
        var (attackerOut, host, a2, b2) = await PairAsync();
        using (a1) using (b1) using (a2) using (b2)
        {
            static bool Benign(Exception e) => e is IOException or ObjectDisposedException or OperationCanceledException or SocketException;

            // Host → client is passed through unchanged so the handshake can complete.
            var down = Task.Run(async () =>
            {
                try { await attackerOut.CopyToAsync(attackerIn, ct); } catch (Exception e) when (Benign(e)) { }
                try { a1.Client.Shutdown(SocketShutdown.Both); } catch (Exception e) when (Benign(e)) { }
            });

            // Client → host goes through the rewrite.
            var wireBytes = 0;
            var up = Task.Run(async () =>
            {
                var buf = new byte[4096];
                var output = new List<byte>();
                try
                {
                    int n;
                    while ((n = await attackerIn.ReadAsync(buf, ct)) > 0)
                    {
                        output.Clear();
                        for (var i = 0; i < n; i++) rewrite.Feed(wireBytes++, buf[i], output);
                        if (output.Count > 0) await attackerOut.WriteAsync(output.ToArray(), ct);
                    }
                    output.Clear();
                    rewrite.Finish(output);
                    if (output.Count > 0) await attackerOut.WriteAsync(output.ToArray(), ct);
                }
                catch (Exception e) when (Benign(e)) { }
                try { a2.Client.Shutdown(SocketShutdown.Send); } catch (Exception e) when (Benign(e)) { }
            });

            // Host: accept, then read until the stream ends or fails.
            var hostTask = Task.Run(async () =>
            {
                var received = new MemoryStream();
                try
                {
                    var accepted = await TunnelHandshake.AcceptAsync(host, Room, mode, disguise, ct);
                    if (accepted is null) return (false, received.ToArray(), false);
                    var stream = accepted.Value.Stream;
                    var buf = new byte[8192];
                    try
                    {
                        int n;
                        while ((n = await stream.ReadAsync(buf, ct)) > 0) received.Write(buf, 0, n);
                    }
                    catch (Exception e) when (e is CryptographicException or EndOfStreamException || Benign(e)) { }
                    var clean = stream.ClosedCleanly;
                    stream.Abort();
                    return (true, received.ToArray(), clean);
                }
                catch (Exception e) when (e is CryptographicException or EndOfStreamException or InvalidDataException or FormatException || Benign(e))
                {
                    return (false, received.ToArray(), false);
                }
            });

            // Client: handshake, send the messages, send the close record, then stop sending.
            var counting = new CountingStream(clientRaw);
            var handshakeBytes = -1;
            try
            {
                var client = await TunnelHandshake.ConnectAsync(counting, Room, mode, disguise, TunnelService.Game, ct);
                handshakeBytes = (int)counting.Written;
                foreach (var m in Messages) await client.WriteAsync(m, ct);
                await client.CloseAsync(ct);
            }
            catch (Exception e) when (e is CryptographicException or EndOfStreamException or InvalidDataException or FormatException || Benign(e)) { }
            try { a1.Client.Shutdown(SocketShutdown.Send); } catch (Exception e) when (Benign(e)) { }

            var (ok, received, closed) = await hostTask;
            await up;
            try { a1.Client.Shutdown(SocketShutdown.Both); a2.Client.Shutdown(SocketShutdown.Both); } catch (Exception e) when (Benign(e)) { }
            await down;
            return new Outcome(ok, received, closed, handshakeBytes, wireBytes);
        }
    }

    private static async Task<(NetworkStream Client, NetworkStream Server, TcpClient A, TcpClient B)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var a = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await a.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var b = await accept;
        listener.Stop();
        a.NoDelay = b.NoDelay = true;
        return (a.GetStream(), b.GetStream(), a, b);
    }

    /// <summary>Counts bytes written, so the test knows where the handshake ends on the wire.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Written;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Written += count; inner.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Written += buffer.Length; return inner.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Written += count; return inner.WriteAsync(buffer, offset, count, ct); }
    }
}
