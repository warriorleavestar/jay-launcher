namespace Amber.Core.Themes;

/// <summary>
/// The wing bars: Jay's signature mark, after the barred blue wing feathers of a jay. One period is 13 units:
/// accent 0–6, dark 6–8, light 8–11, dark 11–13. Used only on the selected nav item, the selected tab, progress fills
/// and the edge of code cards. The default brushes are written out in Amber.App/Themes/Tokens.axaml (a unit test
/// compares them with <see cref="Stops"/>); for a theme pack the app builds them from <see cref="Colors"/>.
/// </summary>
public static class WingBars
{
    /// <summary>Length of one period, in device-independent pixels.</summary>
    public const int Period = 13;

    /// <summary>Where each bar ends inside a period: accent, dark, light, dark.</summary>
    public static readonly int[] Edges = [6, 8, 11, 13];

    /// <summary>
    /// Hard-edged gradient stops for one period (offsets 0..1): two stops per bar, so there is no blending between bars.
    /// </summary>
    public static IReadOnlyList<(double Offset, uint Color)> Stops(uint accent, uint light, uint dark)
    {
        uint[] colors = [accent, dark, light, dark];
        var stops = new List<(double, uint)>(8);
        var start = 0;
        for (var i = 0; i < Edges.Length; i++)
        {
            stops.Add((Math.Round(start / (double)Period, 4), colors[i]));
            stops.Add((Math.Round(Edges[i] / (double)Period, 4), colors[i]));
            start = Edges[i];
        }
        return stops;
    }

    /// <summary>
    /// The three bar colours for one variant of a theme. A pack that changes the accent but says nothing about the
    /// bars gets a light and a dark bar in the accent's own hue, so the mark never clashes with the pack.
    /// </summary>
    public static (uint Accent, uint Light, uint Dark) Colors(IReadOnlyDictionary<string, uint> pack, bool dark)
    {
        var defaults = dark ? ThemeDefaults.Dark : ThemeDefaults.Light;
        var ownAccent = pack.TryGetValue("Accent", out var a);
        var accent = (ownAccent ? a : defaults["Accent"]) | 0xFF000000;
        var light = pack.TryGetValue("WingLight", out var l) ? l
            : ownAccent ? ThemeColor.Mix(accent, 0xFFFFFFFF, dark ? 0.72 : 0.6) : defaults["WingLight"];
        var deep = pack.TryGetValue("WingDark", out var d) ? d
            : ownAccent ? ThemeColor.Mix(accent, 0xFF000000, dark ? 0.82 : 0.68) : defaults["WingDark"];
        return (accent, light | 0xFF000000, deep | 0xFF000000);
    }
}
