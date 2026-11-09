namespace Amber.Core.Content;

/// <summary>The filter chips on Instance › Mods (official design §6.5).</summary>
public enum ModFilter { All, Disabled, Updates, Problems }

/// <summary>What the mods table needs to know about one row to search and filter it.</summary>
public readonly record struct ModFacts(string Name, string FileName, string? ModId, string? Authors, bool Enabled, bool HasUpdate, bool HasProblem);

/// <summary>Search and filter for the mods table. Pure, so it is unit-tested.</summary>
public static class ModQuery
{
    /// <summary>
    /// Search matches the name, file name, mod id or authors, ignoring case; each word must match somewhere.
    /// Empty search matches everything.
    /// </summary>
    public static bool Matches(ModFacts mod, string? search, ModFilter filter)
    {
        var passes = filter switch
        {
            ModFilter.Disabled => !mod.Enabled,
            ModFilter.Updates => mod.HasUpdate,
            ModFilter.Problems => mod.HasProblem,
            _ => true,
        };
        if (!passes) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Has(mod.Name, word) && !Has(mod.FileName, word) && !Has(mod.ModId, word) && !Has(mod.Authors, word)) return false;
        }
        return true;
    }

    private static bool Has(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Shift+click / Shift+arrow range selection: every index between the anchor and the target, inclusive, in order.
    /// An anchor outside the list selects only the target.
    /// </summary>
    public static IReadOnlyList<int> Range(int anchor, int target, int count)
    {
        if (target < 0 || target >= count) return [];
        if (anchor < 0 || anchor >= count) return [target];
        var (from, to) = anchor <= target ? (anchor, target) : (target, anchor);
        return Enumerable.Range(from, to - from + 1).ToList();
    }
}
