using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// One leg of a split member connection: which leg (0-based), how many legs the room uses, and the room's key epoch.
/// All three go into the handshake prologue, so a leg sent to the wrong port, a changed leg count or credentials from
/// an older epoch fail the handshake.
/// </summary>
public readonly record struct SplitLeg(int Index, int Count, int Epoch)
{
    public bool IsValid => Count is >= SplitStream.MinLegs and <= SplitStream.MaxLegs && Index >= 0 && Index < Count && Epoch >= 1;
}

/// <summary>
/// Opening and pairing the legs of a split member connection (design: mp-split-design-v3).
/// <para>
/// Each leg is a full <see cref="MemberChannel"/> handshake (hybrid Noise with a per-leg psk, device attestation when the room
/// uses it) over its own path. Right after the handshake the member sends a hello inside the encrypted leg:
/// [1 byte version = 1][32-byte pairing id P]. P is random and the same on every leg of one connection.
/// The host pairs legs by P and accepts a set only when every leg authenticated as the same member and asked for the same service.
/// </para>
/// </summary>
public static class SplitLegs
{
    public const byte HelloVersion = 1;
    public const int PairIdLength = 32;
    internal const int HelloLength = 1 + PairIdLength;

    /// <summary>
    /// Member side: opens every leg in parallel (<paramref name="openLeg"/> gives the raw connection for leg i), runs the handshakes,
    /// sends the hellos and returns the joined stream. Any leg failing drops the others.
    /// </summary>
    public static Task<SplitStream> ConnectAsync(Func<int, CancellationToken, Task<Stream>> openLeg, int count, int epoch, MemberKey key,
        TransportProfile profile, string purpose, byte[] payload, CancellationToken ct = default) =>
        ConnectAsync(openLeg, count, epoch, (raw, leg, c) => MemberChannel.ConnectAsync(raw, key, profile, purpose, payload, c, leg), ct);

    /// <summary>
    /// Same, with the per-leg handshake supplied by the caller (the room tunnel uses <see cref="TunnelHandshake.ConnectAsync"/>,
    /// which also covers the Minecraft disguise). The handshake must bind the <see cref="SplitLeg"/> it is given; on failure it
    /// leaves the raw stream to the caller.
    /// </summary>
    public static async Task<SplitStream> ConnectAsync(Func<int, CancellationToken, Task<Stream>> openLeg, int count, int epoch,
        Func<Stream, SplitLeg, CancellationToken, Task<SecureStream>> handshake, CancellationToken ct = default)
    {
        if (count is < SplitStream.MinLegs or > SplitStream.MaxLegs) throw new ArgumentOutOfRangeException(nameof(count));
        var pairId = RandomNumberGenerator.GetBytes(PairIdLength);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var legs = Enumerable.Range(0, count).Select(i => OpenLegAsync(i)).ToArray();
        try
        {
            await Task.WhenAll(legs);
            return new SplitStream([.. legs.Select(l => l.Result)]);
        }
        catch
        {
            linked.Cancel();
            foreach (var leg in legs)
            {
                try { (await leg).Abort(); }
                catch (Exception e) when (e is IOException or CryptographicException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException or DeviceAttestationException) { }
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pairId);
        }

        async Task<SecureStream> OpenLegAsync(int index)
        {
            var raw = await openLeg(index, linked.Token);
            SecureStream? secure = null;
            try
            {
                secure = await handshake(raw, new SplitLeg(index, count, epoch), linked.Token);
                await secure.WriteAsync((byte[])[HelloVersion, .. pairId], linked.Token);
                return secure;
            }
            catch
            {
                if (secure is not null) secure.Abort();
                else await raw.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>Host side: reads the hello that follows the handshake on one leg. Returns the pairing id.</summary>
    internal static async Task<byte[]> ReadHelloAsync(SecureStream leg, CancellationToken ct)
    {
        var hello = new byte[HelloLength];
        if (!await SecureStream.ReadExactAsync(leg, hello, ct)) throw new EndOfStreamException(L.T("core.mp.transport.tunnel.missinghandshake"));
        if (hello[0] != HelloVersion) throw new CryptographicException(L.T("core.mp.split.rejected"));
        return hello[1..];
    }
}

/// <summary>
/// Host side: collects the legs of split connections until every leg of a set has arrived, then joins them.
/// <para>
/// Rules (design v3 §4–5): a set is keyed by its pairing id; legs pair only when they authenticated as the same
/// <see cref="MemberEntry"/>, carry the same leg count and context (service), and fill distinct indexes. A leg that breaks any
/// rule drops the whole set. Each set waits at most <see cref="PairTimeout"/>; a member may have at most
/// <see cref="MaxPendingPerMember"/> sets waiting, the registry at most <see cref="MaxPending"/>. Finished pairing ids are kept
/// for <see cref="TombstoneFor"/> so a late or replayed leg is dropped at once instead of waiting. When the member is kicked
/// (its revocation fires) waiting legs are dropped too. Nothing about the member is logged.
/// </para>
/// </summary>
public sealed class SplitRegistry
{
    public const int MaxPending = 64;
    public const int MaxPendingPerMember = 4;
    /// <summary>How long a set waits for its other legs (shorter than the 15-second handshake timeout).</summary>
    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan TombstoneFor { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Passed on to each joined <see cref="SplitStream"/> (tests shorten them).</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan EndGrace { get; init; } = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingSet> _pending = new();
    private readonly Dictionary<string, long> _tombstones = new();
    private long _rejected;

    /// <summary>Legs or sets refused (wrong member, duplicate index, timeout, limits, replays).</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    private sealed class PendingSet(MemberEntry member, int count, byte context)
    {
        public MemberEntry Member { get; } = member;
        public int Count { get; } = count;
        public byte Context { get; } = context;
        public SecureStream?[] Legs { get; } = new SecureStream?[count];
        public TaskCompletionSource<SplitStream?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Revocation;
        public int Arrived;
    }

    /// <summary>
    /// Hands one authenticated leg to the registry (after <see cref="MemberChannel.AcceptAsync"/> with a <see cref="SplitLeg"/>).
    /// Reads the hello, then waits for the rest of the set.
    /// <para>
    /// Ownership: from this call on the registry owns <paramref name="secure"/>. The call that completes a set gets the joined
    /// <see cref="SplitStream"/>; the other legs' calls return null (their leg now lives inside that stream; don't dispose it).
    /// On any refusal or timeout the registry aborts every leg of the set and the call throws.
    /// </para>
    /// </summary>
    /// <param name="context">What the connection is for (the tunnel service byte; 0 for chat). Must match on every leg.</param>
    public async Task<SplitStream?> AcceptAsync(SecureStream secure, MemberEntry member, SplitLeg leg, byte context, CancellationToken ct = default)
    {
        byte[] pairId;
        try
        {
            if (!leg.IsValid) throw new CryptographicException(L.T("core.mp.split.rejected"));
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(PairTimeout);
            pairId = await SplitLegs.ReadHelloAsync(secure, helloTimeout.Token);
        }
        catch (Exception e) when (e is IOException or CryptographicException or OperationCanceledException or ObjectDisposedException)
        {
            secure.Abort();
            Interlocked.Increment(ref _rejected);
            throw Refused(e);
        }
        var key = Convert.ToHexString(pairId);
        CryptographicOperations.ZeroMemory(pairId);
        PendingSet set;
        SplitStream? joined = null;
        lock (_gate)
        {
            Sweep();
            if (_tombstones.ContainsKey(key) || member.Revoked) return RefuseLeg(secure);
            if (_pending.TryGetValue(key, out var existing))
            {
                if (!ReferenceEquals(existing.Member, member) || existing.Count != leg.Count || existing.Context != context || existing.Legs[leg.Index] is not null)
                {
                    // Legs that don't belong together: drop all of them, never pick a winner.
                    DropSet(key, existing);
                    return RefuseLeg(secure);
                }
                set = existing;
            }
            else
            {
                if (_pending.Count >= MaxPending || _pending.Values.Count(p => ReferenceEquals(p.Member, member)) >= MaxPendingPerMember) return RefuseLeg(secure);
                set = new PendingSet(member, leg.Count, context);
                _pending[key] = set;
                var captured = set;
                set.Revocation = member.Revocation.Register(() => { lock (_gate) if (_pending.TryGetValue(key, out var p) && p == captured) DropSet(key, p); });
                // Kicked between the check above and Register: the callback already ran (inline) and dropped the new set.
                if (!_pending.ContainsKey(key)) return RefuseLeg(secure);
            }
            set.Legs[leg.Index] = secure;
            if (++set.Arrived == set.Count)
            {
                _pending.Remove(key);
                _tombstones[key] = Stopwatch.GetTimestamp();
                set.Revocation.Dispose();
                joined = new SplitStream([.. set.Legs.Select(l => l!)]) { StallTimeout = StallTimeout, EndGrace = EndGrace };
                set.Done.TrySetResult(null);
            }
        }
        if (joined is not null)
        {
            // Kicked right as the last leg arrived: the revocation callback found nothing pending, so drop the joined stream here.
            if (member.Revoked)
            {
                joined.Abort();
                throw Refused(null);
            }
            return joined;
        }
        try
        {
            await set.Done.Task.WaitAsync(PairTimeout, ct);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            lock (_gate)
                if (_pending.TryGetValue(key, out var p) && p == set) DropSet(key, p, tombstone: true);
            if (set.Done.Task.IsCompletedSuccessfully) return null;
            throw Refused(e);
        }
        // Completed: this leg now lives in the joined stream. Faulted: the set was dropped.
        return await set.Done.Task;
    }

    private SplitStream? RefuseLeg(SecureStream secure)
    {
        secure.Abort();
        Interlocked.Increment(ref _rejected);
        throw Refused(null);
    }

    private static IOException Refused(Exception? inner) => new(L.T("core.mp.split.rejected"), inner);

    /// <summary>Called with the lock held.</summary>
    private void DropSet(string key, PendingSet set, bool tombstone = true)
    {
        _pending.Remove(key);
        if (tombstone) _tombstones[key] = Stopwatch.GetTimestamp();
        set.Revocation.Dispose();
        foreach (var leg in set.Legs) leg?.Abort();
        Interlocked.Increment(ref _rejected);
        set.Done.TrySetException(Refused(null));
    }

    /// <summary>Called with the lock held: forgets old tombstones.</summary>
    private void Sweep()
    {
        foreach (var (key, at) in _tombstones.ToList())
            if (Stopwatch.GetElapsedTime(at) > TombstoneFor) _tombstones.Remove(key);
    }
}
