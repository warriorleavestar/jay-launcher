namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Places for connections still in their handshake, shared by every port of one listener. When all are taken, a
/// connection still in its handshake is closed to make room for the new one: the oldest one of the caller holding the
/// most places. An honest handshake is over within a round trip or two while a stalled one sits until its deadline, so to
/// keep members out a flood has to refill every place faster than a member's handshake takes, and one caller that can be
/// told apart (a real address) only ever pushes out its own connections once it holds the most.
/// </summary>
/// <param name="max">Places in all.</param>
/// <param name="perSource">Places one caller with a known address may hold; past that its own oldest makes room.</param>
internal sealed class HandshakeSlots(int max, int perSource)
{
    internal sealed class Slot
    {
        internal LinkedListNode<Slot>? Node;
        internal Action? Evict;
        internal bool Released;
        internal string Source = "";
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
            else if (_live.Count >= max) victim = OldestOfBusiest();
            if (victim is not null)
            {
                Unlink(victim);
                var close = victim.Evict;
                victim.Evict = null;
                _evicted++;
                evictedOther = true;
                // Under the lock, so a handshake that finishes at the same moment is either released first (and not in
                // the list any more) or closed here, never closed after it was handed on.
                try { close?.Invoke(); }
                catch (Exception e) when (e is ObjectDisposedException or System.Net.Sockets.SocketException) { }
            }
            var slot = new Slot { Evict = evict, Source = key };
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

    /// <summary>The oldest connection among the callers holding the most places (ties go to the oldest, never the newest).</summary>
    private Slot? OldestOfBusiest()
    {
        var most = _liveBySource.Values.Max();
        for (var node = _live.First; node is not null; node = node.Next)
            if (_liveBySource[node.Value.Source] == most) return node.Value;
        return null;
    }

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
