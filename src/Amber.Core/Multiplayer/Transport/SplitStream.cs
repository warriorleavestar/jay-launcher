using System.Buffers.Binary;
using System.Security.Cryptography;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// n-of-n XOR split over 2 or 3 legs. Every chunk d is cut into n shares: legs 0..n-2 each carry a fresh random pad r_i,
/// the last leg carries d ⊕ r_0 ⊕ … ⊕ r_(n-2). Anyone holding fewer than all n shares learns nothing about d.
/// <para>
/// Each leg is already an end-to-end authenticated <see cref="SecureStream"/> with its own handshake, so the split adds no MAC
/// of its own: a share can't be changed or forged without breaking that leg's AEAD. What the split checks is that the legs stay
/// in step: every chunk header ([4-byte sequence][2-byte length]) must be identical on all legs and the sequence must be the
/// next one expected. Anything else is treated as tampering and every leg is dropped.
/// </para>
/// <para>
/// Ending: the stream ends cleanly only when every leg ends with its authenticated close record at the same chunk boundary.
/// One leg ending, failing or going silent while another delivers is an error, and all legs are dropped
/// (never a quiet fall back to fewer legs).
/// </para>
/// Wire format with two legs is the same as the Jay server's privacy+ split, which uses this class too.
/// </summary>
public sealed class SplitStream : Stream, IAuthenticatedStream
{
    public const int MinLegs = 2;
    public const int MaxLegs = 3;
    public const int MaxChunk = 16 * 1024;
    private const int HeaderLength = 6;

    private readonly SecureStream[] _legs;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private uint _sendSeq;
    private uint _receiveSeq;
    private byte[] _readBuffer = [];
    private int _readOffset;
    private int _aborted;
    private bool _ended;

    /// <summary>When one leg has delivered and another hasn't, how long to wait before dropping them all.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>When one leg ends, how long the others get to end too (close records arrive at about the same time).</summary>
    internal TimeSpan EndGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <param name="legs">Legs in index order. The last one carries the masked data.</param>
    public SplitStream(params SecureStream[] legs)
    {
        if (legs.Length is < MinLegs or > MaxLegs) throw new ArgumentOutOfRangeException(nameof(legs));
        _legs = [.. legs];
    }

    public int LegCount => _legs.Length;

    public bool ClosedCleanly => _ended && _legs.All(l => l.ClosedCleanly);

    /// <summary>Bytes written to the network on all legs together (about n times the data).</summary>
    public long BytesOnWire => _legs.Sum(l => l.BytesOnWire);

    /// <summary>Raised once when a leg mismatch is seen (tampering, not a plain disconnect).</summary>
    public event Action? Tampered;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => FlushAsync().GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => Task.WhenAll(_legs.Select(l => l.FlushAsync(ct)));
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _aborted) != 0, this);
            while (!buffer.IsEmpty)
            {
                // The sequence never wraps: after 2^32 - 1 chunks the connection ends (a new one starts at 0).
                if (_sendSeq == uint.MaxValue) throw new IOException(L.T("core.mp.split.exhausted"));
                var n = Math.Min(buffer.Length, MaxChunk);
                var shares = MakeShares(buffer.Span[..n], _legs.Length, _sendSeq);
                _sendSeq++;
                try
                {
                    await Task.WhenAll(_legs.Select((leg, i) => leg.WriteAsync(shares[i], ct).AsTask()));
                }
                finally
                {
                    foreach (var share in shares) CryptographicOperations.ZeroMemory(share);
                }
                buffer = buffer[n..];
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The n shares of one chunk, each with the chunk header in front: n-1 fresh random pads, then the data XOR all pads.
    /// </summary>
    internal static byte[][] MakeShares(ReadOnlySpan<byte> chunk, int legs, uint seq)
    {
        if (chunk.Length is 0 or > MaxChunk) throw new ArgumentOutOfRangeException(nameof(chunk));
        var shares = new byte[legs][];
        for (var i = 0; i < legs; i++)
        {
            shares[i] = new byte[HeaderLength + chunk.Length];
            BinaryPrimitives.WriteUInt32BigEndian(shares[i], seq);
            BinaryPrimitives.WriteUInt16BigEndian(shares[i].AsSpan(4), (ushort)chunk.Length);
        }
        var masked = shares[^1].AsSpan(HeaderLength);
        chunk.CopyTo(masked);
        for (var i = 0; i < legs - 1; i++)
        {
            var pad = shares[i].AsSpan(HeaderLength);
            RandomNumberGenerator.Fill(pad);
            Xor(masked, pad, masked);
        }
        return shares;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_readOffset >= _readBuffer.Length)
        {
            if (_ended) return 0;
            CryptographicOperations.ZeroMemory(_readBuffer);
            var chunk = await ReadChunkAsync(ct);
            if (chunk is null)
            {
                _ended = true;
                return 0;
            }
            _readBuffer = chunk;
            _readOffset = 0;
        }
        var count = Math.Min(buffer.Length, _readBuffer.Length - _readOffset);
        _readBuffer.AsSpan(_readOffset, count).CopyTo(buffer.Span);
        _readOffset += count;
        return count;
    }

    private async Task<byte[]?> ReadChunkAsync(CancellationToken ct)
    {
        var headers = _legs.Select(_ => new byte[HeaderLength]).ToArray();
        var got = await ReadAllAsync(headers, ct);
        if (got.All(g => !g)) return null;
        // Only some legs ended: never a clean end (another leg may have been cut on purpose).
        if (got.Any(g => !g)) throw Fail(new IOException(L.T("core.mp.split.path_lost")), tampered: false);
        for (var i = 1; i < headers.Length; i++)
            if (!headers[i].AsSpan().SequenceEqual(headers[0])) throw Fail(new CryptographicException(L.T("core.mp.split.mismatch")), tampered: true);
        if (BinaryPrimitives.ReadUInt32BigEndian(headers[0]) != _receiveSeq) throw Fail(new CryptographicException(L.T("core.mp.split.mismatch")), tampered: true);
        var length = BinaryPrimitives.ReadUInt16BigEndian(headers[0].AsSpan(4));
        if (length is 0 or > MaxChunk) throw Fail(new CryptographicException(L.T("core.mp.split.mismatch")), tampered: true);
        _receiveSeq++;
        var bodies = _legs.Select(_ => new byte[length]).ToArray();
        got = await ReadAllAsync(bodies, ct);
        if (got.Any(g => !g)) throw Fail(new IOException(L.T("core.mp.split.path_lost")), tampered: false);
        var data = bodies[^1];
        for (var i = 0; i < bodies.Length - 1; i++)
        {
            Xor(data, bodies[i], data);
            CryptographicOperations.ZeroMemory(bodies[i]);
        }
        return data;
    }

    /// <summary>
    /// Fills one buffer from each leg. When one leg has finished (data or end) the others get <see cref="StallTimeout"/>
    /// (or <see cref="EndGrace"/> when it ended) to catch up; after that all legs are dropped so nothing hangs.
    /// Returns per leg whether it delivered (false: that leg ended cleanly before any byte).
    /// </summary>
    private async Task<bool[]> ReadAllAsync(byte[][] buffers, CancellationToken ct)
    {
        var reads = _legs.Select((leg, i) => SecureStream.ReadExactAsync(leg, buffers[i], ct)).ToArray();
        var pending = reads.ToList();
        Task<bool>? first = null;
        try
        {
            while (pending.Count > 0)
            {
                Task<bool> done;
                if (first is null) done = await Task.WhenAny(pending);
                else
                {
                    var wait = first.IsCompletedSuccessfully && first.Result ? StallTimeout : EndGrace;
                    var timer = Task.Delay(wait, ct);
                    var next = await Task.WhenAny(pending.Cast<Task>().Append(timer));
                    if (next == timer)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new IOException(L.T("core.mp.split.stalled"));
                    }
                    done = (Task<bool>)next;
                }
                first ??= done;
                pending.Remove(done);
                // A failed leg (truncated, bad record) ends the whole stream right away.
                if (done.IsFaulted || done.IsCanceled) await done;
            }
            return reads.Select(r => r.Result).ToArray();
        }
        catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            throw Fail(e, tampered: false);
        }
        catch (OperationCanceledException)
        {
            Abort();
            throw;
        }
        finally
        {
            if (Volatile.Read(ref _aborted) != 0) await DrainAsync(pending);
        }
    }

    private static async Task DrainAsync(IEnumerable<Task> tasks)
    {
        foreach (var t in tasks)
        {
            try { await t; }
            catch (Exception e) when (e is IOException or CryptographicException or ObjectDisposedException or OperationCanceledException or System.Net.Sockets.SocketException) { }
        }
    }

    private Exception Fail(Exception e, bool tampered)
    {
        var first = Volatile.Read(ref _aborted) == 0;
        Abort();
        if (tampered && first) Tampered?.Invoke();
        return e;
    }

    private static void Xor(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> output)
    {
        for (var i = 0; i < output.Length; i++) output[i] = (byte)(x[i] ^ y[i]);
    }

    /// <summary>Drops every leg without close records (the other side sees a truncated connection).</summary>
    public void Abort()
    {
        Interlocked.Exchange(ref _aborted, 1);
        foreach (var leg in _legs) leg.Abort();
    }

    /// <summary>Closes cleanly: each leg sends its close record, then all are disposed. After <see cref="Abort"/> nothing is sent.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _aborted) != 0)
            foreach (var leg in _legs) leg.Abort();
        await Task.WhenAll(_legs.Select(l => l.DisposeAsync().AsTask()));
        CryptographicOperations.ZeroMemory(_readBuffer);
        _writeLock.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ = DisposeAsync().AsTask();
        base.Dispose(disposing);
    }
}
