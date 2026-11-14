namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Game connections one member (or, without members, one caller address) may have open at once, shared by every way a
/// room takes game connections (the single tunnel and the split one). The game's side takes a bounded number of
/// connections in all and on loopback can't tell who a connection is for, so each member's share is kept here, where the
/// member is known: past it only that member is turned away. A place is held from just before the connection is handed
/// to the game until it ends.
/// </summary>
public sealed class GameStreamQuota
{
    public const int PerMember = 16;

    private readonly object _gate = new();
    private readonly Dictionary<object, int> _held = [];

    /// <summary>Takes a place for <paramref name="owner"/> (a <see cref="MemberEntry"/> or a caller address); false when its share is used up.</summary>
    public bool TryTake(object owner)
    {
        lock (_gate)
        {
            var n = _held.GetValueOrDefault(owner);
            if (n >= PerMember) return false;
            _held[owner] = n + 1;
            return true;
        }
    }

    public void GiveBack(object owner)
    {
        lock (_gate)
        {
            if (_held.GetValueOrDefault(owner) <= 1) _held.Remove(owner);
            else _held[owner]--;
        }
    }

    /// <summary>Places <paramref name="owner"/> holds now (tests).</summary>
    public int HeldBy(object owner)
    {
        lock (_gate) return _held.GetValueOrDefault(owner);
    }
}
