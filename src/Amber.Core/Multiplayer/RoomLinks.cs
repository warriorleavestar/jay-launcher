namespace Amber.Core.Multiplayer;

/// <summary>How my computer reaches someone in the room.</summary>
public enum LinkKind
{
    /// <summary>Not connected (yet), or it cannot be told apart from the others.</summary>
    None,
    /// <summary>Straight to their computer: each side sees the other's IP address.</summary>
    Direct,
    /// <summary>Through a relay: neither side sees the other's IP address.</summary>
    Relay,
    /// <summary>The room as a whole: some people direct, some through a relay.</summary>
    Mixed,
}

/// <summary>A link and its round-trip time, when EasyTier has measured one.</summary>
public readonly record struct RoomLink(LinkKind Kind, double? LatencyMs)
{
    public static readonly RoomLink None = new(LinkKind.None, null);
}

/// <summary>
/// The connection mark of a room ("Direct · 18 ms", "Relayed · 82 ms") and the line under each person's avatar, from
/// EasyTier's peer list (UI restructure: people, rooms, networks). Pure logic.
///
/// EasyTier names every guest alike, so with several guests a peer cannot be matched to a person. A guest's link is
/// then reported only when all guests are reached the same way; the host is always its own peer.
/// </summary>
public static class RoomLinks
{
    public const string HostName = "amber-host";

    /// <summary>The people actually connected: not me, not relay servers.</summary>
    public static bool IsPlayer(PeerInfo peer) => peer.Cost != "Local" && peer.Hostname.Length > 0;

    public static LinkKind KindOf(PeerInfo peer) => peer.Cost.Contains("p2p", StringComparison.OrdinalIgnoreCase) ? LinkKind.Direct : LinkKind.Relay;

    /// <summary>The whole room: one kind when everyone is reached the same way, with the slowest round trip.</summary>
    public static RoomLink Room(IEnumerable<PeerInfo> peers) => Summarize(peers.Where(IsPlayer), mixed: LinkKind.Mixed);

    /// <summary>A guest looking at the host.</summary>
    public static RoomLink ToHost(IEnumerable<PeerInfo> peers) =>
        peers.FirstOrDefault(p => IsPlayer(p) && p.Hostname == HostName) is { } host ? new RoomLink(KindOf(host), host.LatencyMs) : RoomLink.None;

    /// <summary>
    /// Someone who is not the host. With one guest peer the link is theirs; with several it is reported only when all
    /// are reached the same way (without a round-trip time, which would be somebody else's).
    /// </summary>
    public static RoomLink ToGuest(IEnumerable<PeerInfo> peers)
    {
        var guests = peers.Where(p => IsPlayer(p) && p.Hostname != HostName).ToList();
        if (guests.Count == 1) return new RoomLink(KindOf(guests[0]), guests[0].LatencyMs);
        var all = Summarize(guests, mixed: LinkKind.None);
        return all with { LatencyMs = null };
    }

    private static RoomLink Summarize(IEnumerable<PeerInfo> players, LinkKind mixed)
    {
        var list = players.ToList();
        if (list.Count == 0) return RoomLink.None;
        var direct = list.Count(p => KindOf(p) == LinkKind.Direct);
        var kind = direct == list.Count ? LinkKind.Direct : direct == 0 ? LinkKind.Relay : mixed;
        var slowest = list.Where(p => p.LatencyMs is not null).Select(p => p.LatencyMs!.Value).DefaultIfEmpty(double.NaN).Max();
        return new RoomLink(kind, double.IsNaN(slowest) ? null : slowest);
    }
}
