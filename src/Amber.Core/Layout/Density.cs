namespace Amber.Core.Layout;

/// <summary>
/// Density (official design §6.20 Appearance, decision 10): standard or compact, nothing in between. Compact tightens
/// control and row heights so more fits on a screen (mod tables, logs); standard keeps the theme's own sizes.
/// Safety surfaces carry their own tokens and are never compacted.
/// </summary>
public static class Density
{
    public const string Standard = "standard";
    public const string Compact = "compact";

    public static readonly IReadOnlyList<string> Modes = [Standard, Compact];

    public static string Normalize(string? mode) => string.Equals(mode, Compact, StringComparison.OrdinalIgnoreCase) ? Compact : Standard;

    /// <summary>Size tokens compact density overrides (standard overrides none).</summary>
    public static IReadOnlyDictionary<string, double> Overrides(string? mode) => Normalize(mode) == Compact
        ? new Dictionary<string, double>
        {
            ["ControlHeightSm"] = 24,
            ["ControlHeight"] = 30,
            ["ControlHeightLg"] = 36,
            ["MinHitSize"] = 28,
            ["ListRowHeight"] = 32,
        }
        : new Dictionary<string, double>();

    /// <summary>Row height of data lists (§5 DataList: 40, compact 32).</summary>
    public static double RowHeight(string? mode) => Normalize(mode) == Compact ? 32 : 40;
}
