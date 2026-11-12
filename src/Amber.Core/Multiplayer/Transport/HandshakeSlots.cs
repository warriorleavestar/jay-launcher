namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Places for connections still in their handshake, shared by every port of one listener. When all are taken, the oldest
/// connection still in its handshake is closed to make room for the new one: an honest handshake is over within a round
/// trip or two, while a stalled one sits until its deadline, so filling the places can't lock new members out.
/// </summary>
internal sealed class HandshakeSlots(int max)
{
    internal sealed class Slot
    {
        internal LinkedListNode<Slot>? Node;
        internal Action? Evict;
        internal bool Released;
    }

    private readonly Lock _gate = new();
    /// <summary>Slots still in their handshake and not yet told to close, oldest first.</summary>
    private readonly LinkedList<Slot> _live = new();
    private int _held;
    private int _evicted;

    /// <summary>Connections holding a place right now (including ones told to close that haven't ended yet).</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Connections closed to make room for a newer one.</summary>
    public int Evicted => Volatile.Read(ref _evicted);

    /// <summary>
    /// Takes a place. <paramref name="evict"/> closes this connection if it later has to make room (it runs under the
    /// slots' lock, so it must be quick and must not call back in). Returns null only when too many connections are
    /// already closing; <paramref name="evictedOther"/> says an older connection was closed for this one.
    /// </summary>
    public Slot? TryTake(Action evict, out bool evictedOther)
    {
        evictedOther = false;
        lock (_gate)
        {
            if (_held >= max * 2) return null;
            if (_live.Count >= max)
            {
                var oldest = _live.First!.Value;
                _live.RemoveFirst();
                oldest.Node = null;
                var close = oldest.Evict;
                oldest.Evict = null;
                _evicted++;
                evictedOther = true;
                // Under the lock, so a handshake that finishes at the same moment is either released first (and not in
                // the list any more) or closed here, never closed after it was handed on.
                try { close?.Invoke(); }
                catch (Exception e) when (e is ObjectDisposedException or System.Net.Sockets.SocketException) { }
            }
            var slot = new Slot { Evict = evict };
            slot.Node = _live.AddLast(slot);
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
            if (slot.Node is not null) _live.Remove(slot.Node);
            slot.Node = null;
            slot.Evict = null;
            _held--;
        }
    }
}
