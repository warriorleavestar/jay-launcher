using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>
/// Theme pack format 2 (official §10): the format 2 token names, the motion tokens, and the in-memory upgrade of
/// format 1 packs (§10.8). The upgrade never changes the pack's files; it records what it did in
/// <see cref="ThemePack.Migration"/> so the theme card can show it and the user can copy it for the author.
/// </summary>
public static class ThemeFormat
{
    /// <summary>The hero scrim never goes below this (text on a cover must stay readable).</summary>
    public const double MinHeroScrim = 0.85;

    /// <summary>
    /// Format 2 color name → Jay's resource name. Both spellings are accepted in every format; Jay's own resources
    /// keep their old names so XAML does not change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ColorAliases = new Dictionary<string, string>
    {
        ["Base"] = "PageBg",
        ["Raised"] = "SurfaceRaised",
        ["Divider"] = "BorderSubtle",
        ["Frame"] = "SidebarBg",
        ["NavBg"] = "SidebarBg",
        ["TopBarBg"] = "SidebarBg",
        ["Text"] = "TextPrimary",
        ["NavText"] = "SidebarText",
        ["NavTextMuted"] = "SidebarTextMuted",
        ["NavItemSelected"] = "NavSelected",
        ["NavItemHover"] = "NavHover",
        ["TextDisabled"] = "DisabledText",
    };

    /// <summary>Jay's resource name → the name a format 2 pack writes (first alias wins: Frame, not NavBg).</summary>
    public static string PublicColorName(string key) =>
        ColorAliases.FirstOrDefault(p => p.Value == key) is { Key: { } name } ? name : key;

    public static readonly IReadOnlyDictionary<string, string> NumberAliases = new Dictionary<string, string>
    {
        ["PlayHeight"] = "PlayButtonHeight",
    };

    public static string PublicNumberName(string key) =>
        NumberAliases.FirstOrDefault(p => p.Value == key) is { Key: { } name } ? name : key;

    /// <summary>tokens.json "motion": format 2 name → Jay's resource, default length in ms. At most twice the default.</summary>
    public static readonly IReadOnlyDictionary<string, (string Resource, double Default)> MotionKeys = new Dictionary<string, (string, double)>
    {
        ["DurationFast"] = ("MotionFast", 100),
        ["DurationBase"] = ("MotionBase", 160),
        ["DurationSlow"] = ("MotionSlow", 240),
        ["DurationSlower"] = ("MotionSlower", 320),
    };

    public static void ReadMotion(ThemePack pack, JsonElement motion)
    {
        if (motion.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", "motion")); return; }
        foreach (var p in motion.EnumerateObject())
        {
            if (!MotionKeys.TryGetValue(p.Name, out var key)) { pack.Problems.Add(new("unknown_key", "motion." + p.Name)); continue; }
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var ms) || !double.IsFinite(ms)) { pack.Problems.Add(new("bad_value", "motion." + p.Name)); continue; }
            var clamped = Math.Clamp(ms, 0, key.Default * 2);
            if (clamped != ms) pack.Problems.Add(new("value_clamped", $"motion.{p.Name} {ms.ToString(CultureInfo.InvariantCulture)}"));
            pack.Durations[key.Resource] = clamped;
        }
    }

    /// <summary>True when <paramref name="minJay"/> (like "0.8.0" or "v1.2") is newer than <paramref name="jay"/>. Unreadable values never block.</summary>
    public static bool NeedsNewerJay(string minJay, Version jay)
    {
        var text = minJay.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+', ' ']);
        if (dash >= 0) text = text[..dash];
        if (!text.Contains('.')) text += ".0";
        if (!Version.TryParse(text, out var wanted)) return false;
        return Normalize(wanted) > Normalize(jay);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// Format 1 token meanings → format 2 (§10.8). Runs after the colors and numbers are read and before the contrast fixes.
    /// </summary>
    internal static void UpgradeTokens(ThemePack pack)
    {
        var notes = pack.Migration;
        notes.Add(new("migrated_format", "1 → 2"));
        // Format 1 had one accent color: the start button used Jay's amber. An author who designed an accent gets it on the start button too.
        var playFollows = false;
        foreach (var colors in new[] { pack.DarkColors, pack.LightColors })
        {
            if (!colors.TryGetValue("Accent", out var accent) || colors.ContainsKey("Play")) continue;
            colors["Play"] = accent;
            var on = colors.TryGetValue("OnAccent", out var onAccent) ? onAccent
                : ThemeColor.Contrast(0xFF000000, accent) >= ThemeColor.Contrast(0xFFFFFFFF, accent) ? 0xFF1E1404 : 0xFFFFFFFF;
            var states = ThemeColor.StatesFor(accent | 0xFF000000, on);
            colors["OnPlay"] = on;
            colors["PlayHover"] = colors.GetValueOrDefault("AccentHover", states.Hover);
            colors["PlayPressed"] = colors.GetValueOrDefault("AccentPressed", states.Pressed);
            if (colors.TryGetValue("AccentText", out var text)) colors["PlayText"] = text;
            if (colors.TryGetValue("AccentTint", out var tint)) colors["PlayTint"] = tint;
            colors["PlayGlow"] = (accent & 0x00FFFFFF) | 0x40000000;
            playFollows = true;
        }
        if (playFollows) notes.Add(new("migrated_play_from_accent", "Play = Accent"));
        // FontDisplay was 26 and used for the big titles: that size is FontHeadline now, FontDisplay grows with it (26 → 32).
        if (pack.Numbers.TryGetValue("FontDisplay", out var display) && !pack.Numbers.ContainsKey("FontHeadline"))
        {
            pack.Numbers["FontHeadline"] = Math.Clamp(display, 14, 64);
            pack.Numbers["FontDisplay"] = Math.Clamp(Math.Round(display * 32 / 26), 16, 72);
            notes.Add(new("migrated_number", $"FontDisplay {Num(display)} → FontHeadline {Num(pack.Numbers["FontHeadline"])}, FontDisplay {Num(pack.Numbers["FontDisplay"])}"));
        }
        if (pack.Radii.TryGetValue("RadiusSm", out var sm) && !pack.Radii.ContainsKey("RadiusXs"))
        {
            pack.Radii["RadiusXs"] = Math.Min(sm, 24);
            notes.Add(new("migrated_number", $"RadiusSm {Num(sm)} → RadiusXs, RadiusSm"));
        }
        if (pack.Radii.TryGetValue("RadiusLg", out var lg) && !pack.Radii.ContainsKey("RadiusXl"))
        {
            // Square themes stay square.
            pack.Radii["RadiusXl"] = lg == 0 ? 0 : Math.Min(lg + 4, 80);
            notes.Add(new("migrated_number", $"RadiusXl = {Num(pack.Radii["RadiusXl"])}"));
        }
    }

    /// <summary>Format 1 component → format 2 components (§10.8). Format 2 packs that still use the old names get the same treatment.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ComponentRenames = new Dictionary<string, string[]>
    {
        ["callout"] = ["banner"],
        ["pill"] = ["chip", "badge", "tag"],
        ["nav.item"] = ["nav.item", "rail.item"],
    };

    /// <summary>The plain-text upgrade report for the author (【复制给作者看】). English codes plus details, one per line.</summary>
    public static string Report(ThemePack pack, Func<ThemeProblem, string>? describe = null)
    {
        describe ??= p => p.ToString();
        var sb = new StringBuilder();
        sb.Append(pack.Name).Append(' ').Append(pack.Version).Append(" (format ").Append(pack.Format).Append(')').AppendLine();
        foreach (var note in pack.Migration) sb.Append("- ").AppendLine(describe(note));
        var fixes = pack.Problems.Where(p => p.Code == "contrast_fixed").ToList();
        var others = pack.Problems.Where(p => p.Code != "contrast_fixed").ToList();
        if (fixes.Count > 0) sb.Append("contrast_fixed × ").Append(fixes.Count).Append(": ").AppendLine(string.Join(", ", fixes.Select(f => f.Detail)));
        foreach (var p in others) sb.Append("- ").AppendLine(describe(p));
        return sb.ToString().TrimEnd();
    }

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
