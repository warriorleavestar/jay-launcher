namespace Amber.Core.Multiplayer.Public;

/// <summary>How the public room list is ordered.</summary>
public enum RoomSort { MostPlayers, Name, Newest }

/// <summary>Search, game-version filter, "walk right in" and sort for the public room list (official §6.16).</summary>
public static class RoomFilter
{
    /// <summary>
    /// Rooms whose name or host contains <paramref name="search"/> (case-insensitive), on <paramref name="game"/> when given,
    /// and, with <paramref name="openOnly"/>, only those that need neither a password nor the host's approval.
    /// </summary>
    public static IReadOnlyList<RoomListing> Apply(IEnumerable<RoomListing> rooms, string? search, string? game, bool openOnly, RoomSort sort)
    {
        var text = search?.Trim() ?? "";
        var matched = rooms.Where(r =>
            (text.Length == 0 || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.Host.Contains(text, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(game) || string.Equals(r.Game, game, StringComparison.OrdinalIgnoreCase))
            && (!openOnly || IsOpen(r)));
        return (sort switch
        {
            RoomSort.Name => matched.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            RoomSort.Newest => matched.OrderByDescending(r => r.Updated),
            _ => matched.OrderByDescending(r => r.Players).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }

    /// <summary>Anyone can walk right in: no password, no waiting for the host.</summary>
    public static bool IsOpen(RoomListing room) => room.Mode != RoomVisibility.Password && (!room.Approval || room.Mode == RoomVisibility.Direct);

    /// <summary>The game versions in the list, for the version filter.</summary>
    public static IReadOnlyList<string> Games(IEnumerable<RoomListing> rooms) =>
        rooms.Select(r => r.Game).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}
