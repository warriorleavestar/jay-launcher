namespace Amber.Core.GameLibrary;

/// <summary>One instance as the library home shows it (official design §6.2). Built from local data only; no network.</summary>
public sealed record LibraryEntry(
    string Id,
    string Name,
    string McVersion,
    string? Loader,
    bool Pinned = false,
    DateTimeOffset? LastPlayed = null,
    bool HasProblem = false,
    bool IsRunning = false);

/// <summary>Filter chips: all, pinned, has a problem, running.</summary>
public enum LibraryFilter { All, Pinned, Problems, Running }

/// <summary>Sort orders: recently played, name, game version (newest first).</summary>
public enum LibrarySort { Recent, Name, Version }

/// <summary>Search, filter and sort for the library home, plus picking the "continue playing" instance.</summary>
public static class LibraryQuery
{
    public static IReadOnlyList<LibraryEntry> Apply(IEnumerable<LibraryEntry> all, string? search, LibraryFilter filter, LibrarySort sort)
    {
        var terms = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = all.Where(e => filter switch
        {
            LibraryFilter.Pinned => e.Pinned,
            LibraryFilter.Problems => e.HasProblem,
            LibraryFilter.Running => e.IsRunning,
            _ => true,
        }).Where(e => terms.All(t => Matches(e, t)));
        return (sort switch
        {
            LibrarySort.Name => list.OrderByDescending(e => e.Pinned).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.Version => list.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.McVersion, GameVersionComparer.Instance)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            // Recently played: pinned first, then by last played (never played last), then by name.
            _ => list.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.LastPlayed ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToList();
    }

    /// <summary>A search term matches the name, id, game version or loader (case-insensitive substring).</summary>
    public static bool Matches(LibraryEntry e, string term) =>
        e.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
        || e.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
        || e.McVersion.Contains(term, StringComparison.OrdinalIgnoreCase)
        || (e.Loader?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// "Continue playing": the most recent entry of <paramref name="recent"/> that still exists; else the current instance;
    /// else the most recently played; else the first one. Null when the library is empty.
    /// </summary>
    public static LibraryEntry? ContinueWith(IReadOnlyList<LibraryEntry> all, IEnumerable<string>? recent, string? current)
    {
        if (all.Count == 0) return null;
        foreach (var id in recent ?? [])
            if (all.FirstOrDefault(e => e.Id == id) is { } hit) return hit;
        if (current is not null && all.FirstOrDefault(e => e.Id == current) is { } cur) return cur;
        return all.Where(e => e.LastPlayed is not null).MaxBy(e => e.LastPlayed) ?? all[0];
    }

    /// <summary>Moves <paramref name="id"/> to the front of a most-recent-first list, keeping at most <paramref name="max"/>.</summary>
    public static List<string> Touch(IEnumerable<string>? recent, string id, int max = 20)
    {
        var list = new List<string> { id };
        list.AddRange((recent ?? []).Where(r => r != id));
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }
}

/// <summary>Compares Minecraft versions numerically (1.20.10 &gt; 1.20.9; 26.1 &gt; 1.21.4). Non-numeric parts compare as text.</summary>
public sealed class GameVersionComparer : IComparer<string>
{
    public static readonly GameVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var a = x.Split('.', '-', ' ');
        var b = y.Split('.', '-', ' ');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;
            var na = int.TryParse(a[i], out var ia);
            var nb = int.TryParse(b[i], out var ib);
            var c = na && nb ? ia.CompareTo(ib) : na ? 1 : nb ? -1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>
/// Automatic cover art (official design §6.2): a stable palette slot and initial for an instance, so the same instance
/// always gets the same colors on every machine. The app maps the slot to a gradient.
/// </summary>
public static class CoverArt
{
    public const int Slots = 8;

    /// <summary>FNV-1a over the UTF-16 id: stable across runs and platforms (string.GetHashCode is randomized).</summary>
    public static int SlotFor(string id)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in id)
            {
                hash = (hash ^ c) * 16777619u;
            }
            return (int)(hash % Slots);
        }
    }

    /// <summary>The first letter or ideograph of the name, upper-cased; "?" for an empty name.</summary>
    public static string InitialOf(string name)
    {
        foreach (var rune in name.EnumerateRunes())
            if (System.Text.Rune.IsLetterOrDigit(rune)) return System.Text.Rune.ToUpperInvariant(rune).ToString();
        return "?";
    }
}
