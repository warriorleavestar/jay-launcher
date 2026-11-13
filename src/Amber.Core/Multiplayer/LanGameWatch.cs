namespace Amber.Core.Multiplayer;

/// <summary>
/// The worlds this computer has open to LAN right now, kept from their broadcasts. A game repeats its broadcast every
/// 1.5 seconds; one that hasn't been heard for <see cref="Expiry"/> was closed (or reopened on another port).
/// Pure bookkeeping: whoever listens feeds it with <see cref="Saw"/> and asks for <see cref="Current"/>.
/// </summary>
public sealed class LanGameWatch
{
    /// <summary>Four missed broadcasts, so a busy computer that drops one or two doesn't make a world blink.</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(6);

    private readonly Dictionary<int, (LanGame Game, DateTime First, DateTime Last)> _seen = [];

    public TimeSpan Expiry { get; init; } = DefaultExpiry;

    /// <summary>A broadcast was heard. True when that changes the list: a new port, or a new name on a known one.</summary>
    public bool Saw(LanGame game, DateTime now)
    {
        if (_seen.TryGetValue(game.Port, out var known))
        {
            // A port that had already gone quiet counts as new again: the same world reopened on the same port.
            var lapsed = now - known.Last > Expiry;
            _seen[game.Port] = (game, lapsed ? now : known.First, now);
            return lapsed || known.Game.Motd != game.Motd;
        }
        _seen[game.Port] = (game, now, now);
        return true;
    }

    /// <summary>Drops worlds not heard for <see cref="Expiry"/>. True when one was dropped.</summary>
    public bool Expire(DateTime now)
    {
        var gone = _seen.Where(e => now - e.Value.Last > Expiry).Select(e => e.Key).ToList();
        foreach (var port in gone) _seen.Remove(port);
        return gone.Count > 0;
    }

    /// <summary>The worlds heard within <see cref="Expiry"/>, the most recently opened first.</summary>
    public IReadOnlyList<LanGame> Current(DateTime now) =>
        [.. _seen.Values.Where(e => now - e.Last <= Expiry).OrderByDescending(e => e.First).ThenBy(e => e.Game.Port).Select(e => e.Game)];

    public void Clear() => _seen.Clear();

    /// <summary>
    /// Which world a room on a relay should switch to by itself, if any. Only when the player didn't type a port: a room
    /// waiting for its game takes the newest world; a room whose linked world went quiet takes the newest one that is
    /// still there (the same world reopened on a new port). A room linked to a world that is still open stays put, so
    /// a second world opened alongside is only listed and the player picks it. <paramref name="linkedWasHeard"/>: the
    /// linked world's broadcast was heard before, so its silence means it closed (one linked from the game's log may
    /// simply not have been heard yet).
    /// </summary>
    public static int? AutoPick(IReadOnlyList<LanGame> current, int? linked, bool typed, int relayPort, bool linkedWasHeard = true)
    {
        if (typed) return null;
        if (linked is { } port && (current.Any(g => g.Port == port) || !linkedWasHeard)) return null;
        foreach (var game in current) if (game.Port != relayPort && game.Port != linked) return game.Port;
        return null;
    }
}
