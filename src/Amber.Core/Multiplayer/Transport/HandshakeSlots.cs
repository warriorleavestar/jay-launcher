namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Places for connections still in their handshake, shared by every port of one listener. When all are taken, a
/// connection still in its handshake is closed to make room for the new one. Connections younger than the grace period
/// are left alone while older ones are there: among the older ones, a caller holding clearly more than its share loses
/// its oldest, otherwise the oldest goes. Only when every place is younger than the grace period does the oldest of all
/// go. An honest handshake is over within a round trip or two while a stalled one sits until its deadline, so a member
/// (including one opening several legs at once, or several members behind one address) is only pushed out by a flood
/// that refills every place within the grace period, or within the member's own handshake.
/// </summary>
/// <param name="max">Places in all.</param>
/// <param name="perSource">Places one caller with a known address may hold; past that its own oldest makes room.</param>
/// <param name="grace">How long a new connection is left alone while older ones are there (default 2 s).</param>
/// <param name="clock">Time since some fixed start (tests); default a monotonic clock.</param>
internal sealed class HandshakeSlots(int max, int perSource, TimeSpan? grace = null, Func<TimeSpan>? clock = null)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _grace = grace ?? DefaultGrace;
    private readonly Func<TimeSpan> _now = clock ?? (() => TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency));

    internal sealed class Slot
    {
        internal LinkedListNode<Slot>? Node;
        internal Action? Evict;
        internal bool Released;
        internal string Source = "";
        internal TimeSpan Taken;
    }

    private readonly Lock _gate = new();
    /// <summary>Slots still in their handshake and not yet told to close, oldest first.</summary>
    private readonly LinkedList<Slot> _live = new();
    private readonly Dictionary<string, int> _liveBySource = new(StringComparer.Ordinal);
    private int _held;
    private int _evicted;

    /// <summary>Connections holding a place right now (including ones told to close that haven't ended yet).</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Connections closed to make room for a newer one.</summary>
    public int Evicted => Volatile.Read(ref _evicted);

    /// <summary>
    /// Takes a place for a connection from <paramref name="source"/> (null when callers can't be told apart, such as
    /// connections handed over on loopback: they share one group with no per-caller limit). <paramref name="evict"/>
    /// closes this connection if it later has to make room (it runs under the slots' lock, so it must be quick and must
    /// not call back in). Returns null only when too many connections are already closing; <paramref name="evictedOther"/>
    /// says another connection was closed for this one.
    /// </summary>
    public Slot? TryTake(string? source, Action evict, out bool evictedOther)
    {
        evictedOther = false;
        var key = source ?? "";
        lock (_gate)
        {
            if (_held >= max * 2) return null;
            Slot? victim = null;
            if (source is not null && _liveBySource.GetValueOrDefault(key) >= perSource) victim = OldestOf(key);
            else if (_live.Count >= max) victim = Choose();
            if (victim is not null)
            {
                Unlink(victim);
                var close = victim.Evict;
                victim.Evict = null;
                _evicted++;
                evictedOther = true;
                // Under the lock, so a handshake that finishes at the same moment is either released first (and not in
                // the list any more) or closed here, never closed after it was handed on.
                // Whatever closing throws, the new connection still gets its place and the accept loop goes on.
                try { close?.Invoke(); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
            var slot = new Slot { Evict = evict, Source = key, Taken = _now() };
            slot.Node = _live.AddLast(slot);
            _liveBySource[key] = _liveBySource.GetValueOrDefault(key) + 1;
            _held++;
            return slot;
        }
    }

    /// <summary>Gives the place back (once; later calls do nothing).</summary>
    public void Release(Slot slot)
    {
        lock (_gate)
        {
            if (slot.Released) return;
            slot.Released = true;
            Unlink(slot);
            slot.Evict = null;
            _held--;
        }
    }

    /// <summary>
    /// Which connection makes room when every place is taken. Only connections past the grace period are considered
    /// (all of them when none is): if one caller holds clearly more of those than its share (more than 2, and more than
    /// places / callers), its oldest; otherwise the oldest of them. Ties go to the oldest, never the newest.
    /// </summary>
    private Slot? Choose()
    {
        var cutoff = _now() - _grace;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var node = _live.First; node is not null && node.Value.Taken <= cutoff; node = node.Next)
            counts[node.Value.Source] = counts.GetValueOrDefault(node.Value.Source) + 1;
        // None past the grace period: the oldest of all (a member's handshake is newer than the flood around it).
        if (counts.Count == 0) return _live.First?.Value;
        var most = counts.Values.Max();
        var share = Math.Max(2, max / Math.Max(1, _liveBySource.Count));
        if (most > share)
        {
            for (var node = _live.First; node is not null; node = node.Next)
                if (counts.TryGetValue(node.Value.Source, out var n) && n == most) return node.Value;
        }
        return _live.First?.Value;
    }

    /// <summary>
    /// A caller over its own limit loses its oldest, grace period or not: the list is oldest first, so that is one past
    /// the grace period whenever it has one, and a caller can't shelter behind fresh connections it keeps opening.
    /// (Several members behind one address share its limit of <c>perSource</c>.)
    /// </summary>
    private Slot? OldestOf(string source)
    {
        for (var node = _live.First; node is not null; node = node.Next)
            if (node.Value.Source == source) return node.Value;
        return null;
    }

    private void Unlink(Slot slot)
    {
        if (slot.Node is null) return;
        _live.Remove(slot.Node);
        slot.Node = null;
        if (_liveBySource.GetValueOrDefault(slot.Source) <= 1) _liveBySource.Remove(slot.Source);
        else _liveBySource[slot.Source]--;
    }
}
