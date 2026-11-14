using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amber.Core.Themes;

/// <summary>
/// 主题第 2 层：组件样式（styles.json）。只能给白名单里的组件、白名单里的状态设白名单里的属性。
/// 界面层把这些翻译成 Avalonia 样式，只套在主窗口上；确认框、远程控制同意框这些安全窗口永远用 Jay 自带样式。
/// </summary>
public static partial class ThemeStyles
{
    /// <summary>Component → allowed states ("" is the normal state). Official §10.3.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Components = new Dictionary<string, string[]>
    {
        ["button"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.primary"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.secondary"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.tonal"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.ghost"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.danger"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.icon"] = ["", ":hover", ":pressed", ":disabled"],
        ["button.play"] = ["", ":hover", ":pressed"],
        ["card"] = [""],
        ["card.cover"] = ["", ":hover"],
        ["hero"] = [""],
        ["chip"] = ["", ":hover", ":selected"],
        ["badge"] = [""],
        ["tag"] = [""],
        ["tab"] = ["", ":hover", ":selected"],
        ["sidebar"] = [""],
        ["rail.item"] = ["", ":hover", ":selected"],
        ["nav.item"] = ["", ":hover", ":selected"],
        ["topbar"] = [""],
        ["input"] = ["", ":hover", ":focus"],
        ["combobox"] = ["", ":hover", ":focus"],
        ["segmented"] = ["", ":hover", ":checked"],
        ["switch"] = ["", ":hover", ":checked"],
        ["checkbox"] = ["", ":hover", ":checked"],
        ["list.row"] = ["", ":hover", ":selected"],
        ["table.header"] = [""],
        ["banner"] = [""],
        ["toast"] = [""],
        ["menu"] = [""],
        ["tooltip"] = [""],
        ["drawer"] = [""],
        ["dialog"] = [""],
        ["coachmark"] = [""],
        ["progress"] = [""],
        ["capsule"] = [""],
        ["requestbar"] = [""],
        ["heading"] = [""],
        ["window"] = [""],
    };

    /// <summary>Every component takes these (§10.3); a few take extra ones.</summary>
    private static readonly string[] CommonProps = ["background", "foreground", "border", "borderWidth", "radius", "padding", "fontWeight", "shadow"];

    private static readonly IReadOnlyDictionary<string, string[]> ExtraProps = new Dictionary<string, string[]>
    {
        ["button.play"] = ["glow"],
        ["hero"] = ["scrim"],
        ["tab"] = ["indicator", "indicatorHeight"],
        ["sidebar"] = ["indicator"],
        ["rail.item"] = ["indicator"],
        ["nav.item"] = ["indicator"],
        ["topbar"] = ["indicator"],
        ["progress"] = ["track", "fill"],
    };

    public static string[] PropsFor(string component) => [.. CommonProps, .. ExtraProps.GetValueOrDefault(component, [])];

    [GeneratedRegex(@"^linear\(\s*(-?\d{1,3})\s*,(.+)\)$")]
    private static partial Regex LinearPattern();

    [GeneratedRegex(@"^token:([A-Za-z]+)$")]
    private static partial Regex TokenPattern();

    public static void Read(ThemePack pack, JsonElement root, Func<string, byte[]?> open)
    {
        if (root.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", "styles.json")); return; }
        foreach (var component in root.EnumerateObject())
        {
            // Format 1 names: callout → banner, pill → chip / badge / tag, nav.item also styles rail.item (§10.8).
            if (ThemeFormat.ComponentRenames.TryGetValue(component.Name, out var targets) && (pack.Format < 2 || !Components.ContainsKey(component.Name)))
            {
                // Read once (problems are reported once), then share the result with the other new names the pack didn't write itself.
                // A new name the pack also writes itself keeps the pack's own style.
                var free = targets.Where(t => t == component.Name || !root.TryGetProperty(t, out _)).ToList();
                if (free.Count > 0)
                {
                    ReadComponent(pack, free[0], component.Value, open);
                    if (pack.Styles.TryGetValue(free[0], out var read))
                        foreach (var target in free.Skip(1)) pack.Styles[target] = read;
                }
                pack.Migration.Add(new("migrated_component", $"{component.Name} → {string.Join(", ", targets)}"));
                continue;
            }
            ReadComponent(pack, component.Name, component.Value, open);
        }
        // Format 1 had no top bar: the sidebar's background is the top bar's too.
        if (pack.Format < 2 && pack.Styles.TryGetValue("sidebar", out var sidebar) && !pack.Styles.ContainsKey("topbar")
            && sidebar.GetValueOrDefault("")?.Background is { } sidebarBg)
        {
            pack.Styles["topbar"] = new() { [""] = new StyleProps { Background = sidebarBg } };
            pack.Migration.Add(new("migrated_component", "sidebar.background → topbar"));
        }
        EnforceContrast(pack);
    }

    private static void ReadComponent(ThemePack pack, string name, JsonElement value, Func<string, byte[]?> open)
    {
        var component = (Name: name, Value: value);
        {
            if (!Components.TryGetValue(component.Name, out var states)) { pack.Problems.Add(new("unknown_component", component.Name)); return; }
            if (component.Value.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", component.Name)); return; }
            var allowed = PropsFor(component.Name);
            var byState = new Dictionary<string, StyleProps>();
            var normal = new StyleProps();
            foreach (var p in component.Value.EnumerateObject())
            {
                if (p.Name.StartsWith(':'))
                {
                    if (!states.Contains(p.Name)) { pack.Problems.Add(new("unknown_state", $"{component.Name}{p.Name}")); continue; }
                    var stateProps = new StyleProps();
                    if (p.Value.ValueKind == JsonValueKind.Object)
                        foreach (var sp in p.Value.EnumerateObject()) ReadProp(pack, open, stateProps, allowed, $"{component.Name}{p.Name}", sp);
                    else pack.Problems.Add(new("bad_value", $"{component.Name}{p.Name}"));
                    if (!stateProps.IsEmpty) byState[p.Name] = stateProps;
                }
                else ReadProp(pack, open, normal, allowed, component.Name, p);
            }
            if (!normal.IsEmpty) byState[""] = normal;
            if (byState.Count > 0) pack.Styles[component.Name] = byState;
        }
    }

    private static void ReadProp(ThemePack pack, Func<string, byte[]?> open, StyleProps props, string[] allowed, string where, JsonProperty p)
    {
        var at = $"{where}.{p.Name}";
        if (!allowed.Contains(p.Name)) { pack.Problems.Add(new("unknown_key", at)); return; }
        var v = p.Value;
        switch (p.Name)
        {
            case "background":
            case "foreground":
            case "border":
            case "indicator":
            case "track":
            case "fill":
                var paint = v.ValueKind == JsonValueKind.String ? ParsePaint(v.GetString()!, open, p.Name == "background") : null;
                if (paint is null) { pack.Problems.Add(new("bad_paint", at)); return; }
                switch (p.Name)
                {
                    case "background": props.Background = paint; break;
                    case "foreground": props.Foreground = paint; break;
                    case "border": props.Border = paint; break;
                    case "indicator": props.Indicator = paint; break;
                    case "track": props.Track = paint; break;
                    default: props.Fill = paint; break;
                }
                break;
            case "indicatorHeight": props.IndicatorHeight = Number(pack, v, at, 2, 4); break;
            case "scrim": props.Scrim = Number(pack, v, at, ThemeFormat.MinHeroScrim, 1); break;
            case "glow":
                // The start button's glow: a shadow, or just a color (soft glow around the button).
                if (v.ValueKind == JsonValueKind.String && ThemePackReader.TryColor(v.GetString()!.Trim(), out var glowColor))
                    props.Glow = $"0 0 16 0 {ThemePackReader.FormatColor(glowColor)}";
                else if (v.ValueKind == JsonValueKind.String && ParseShadow(v.GetString()!) is { } glow) props.Glow = glow;
                else pack.Problems.Add(new("bad_shadow", at));
                break;
            case "borderWidth": props.BorderWidth = Number(pack, v, at, 0, 8); break;
            case "radius": props.Radius = Number(pack, v, at, 0, 64); break;
            case "padding":
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[0].TryGetDouble(out var h) && v[1].TryGetDouble(out var vert))
                    props.Padding = (Math.Clamp(h, 0, 48), Math.Clamp(vert, 0, 32));
                else if (v.TryGetDoubleSafe(out var all)) props.Padding = (Math.Clamp(all, 0, 48), Math.Clamp(all, 0, 32));
                else pack.Problems.Add(new("bad_value", at));
                break;
            case "fontWeight":
                if (v.ValueKind == JsonValueKind.String && v.GetString() is "normal" or "medium" or "semibold" or "bold") props.FontWeight = v.GetString();
                else pack.Problems.Add(new("bad_value", at));
                break;
            case "shadow":
                if (v.ValueKind == JsonValueKind.String && ParseShadow(v.GetString()!) is { } shadow) props.Shadow = shadow;
                else pack.Problems.Add(new("bad_shadow", at));
                break;
        }
    }

    private static double? Number(ThemePack pack, JsonElement v, string at, double min, double max)
    {
        if (!v.TryGetDoubleSafe(out var d)) { pack.Problems.Add(new("bad_value", at)); return null; }
        var c = Math.Clamp(d, min, max);
        if (c != d) pack.Problems.Add(new("value_clamped", at));
        return c;
    }

    private static bool TryGetDoubleSafe(this JsonElement v, out double d)
    {
        d = 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d) && double.IsFinite(d);
    }

    /// <summary>
    /// 颜色可以写：#RRGGBB / #AARRGGBB、token:名字（跟着深浅色变）、linear(角度, 颜色, 颜色…)（2 到 4 个颜色）、
    /// 只有背景能用 image:assets/…（图片铺满）。
    /// </summary>
    public static Paint? ParsePaint(string text, Func<string, byte[]?>? open = null, bool allowImage = false)
    {
        text = text.Trim();
        if (ThemePackReader.TryColor(text, out var c)) return new Paint(PaintKind.Color, [c], null, 0);
        if (TokenPattern().Match(text) is { Success: true } t)
        {
            var key = ThemeFormat.ColorAliases.GetValueOrDefault(t.Groups[1].Value, t.Groups[1].Value);
            return ThemePackReader.ColorKeys.Contains(key) ? new Paint(PaintKind.Token, [], key, 0) : null;
        }
        if (LinearPattern().Match(text) is { Success: true } lin)
        {
            var colors = new List<uint>();
            foreach (var part in lin.Groups[2].Value.Split(','))
            {
                if (!ThemePackReader.TryColor(part.Trim(), out var stop)) return null;
                colors.Add(stop);
            }
            return colors.Count is >= 2 and <= 4 ? new Paint(PaintKind.Linear, colors, null, int.Parse(lin.Groups[1].Value, CultureInfo.InvariantCulture) % 360) : null;
        }
        if (allowImage && open is not null && text.StartsWith("image:", StringComparison.Ordinal))
        {
            var path = text[6..];
            try { path = ThemePackReader.NormalizeEntryName(path); }
            catch (ThemePackException) { return null; }
            if (!path.StartsWith("assets/", StringComparison.Ordinal) || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")) return null;
            if (open(path) is not { } bytes || ThemeAssets.ImageSize(bytes) is not { } size || size.Width > ThemePackReader.MaxImageSide || size.Height > ThemePackReader.MaxImageSide) return null;
            return new Paint(PaintKind.Image, [], path, 0);
        }
        return null;
    }

    /// <summary>阴影：“x y 模糊 扩展 颜色”，最多两层，用逗号分开。数值有上限，防止画一个铺满屏幕的阴影。</summary>
    public static string? ParseShadow(string text)
    {
        if (text.Trim() == "none") return "none";
        var layers = text.Split(',');
        if (layers.Length > 2) return null;
        var parts = new List<string>();
        foreach (var layer in layers)
        {
            var tokens = layer.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inset = tokens.Length > 0 && tokens[0] == "inset";
            if (inset) tokens = tokens[1..];
            if (tokens.Length != 5) return null;
            var nums = new double[4];
            for (var i = 0; i < 4; i++)
                if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i]) || !double.IsFinite(nums[i]) || Math.Abs(nums[i]) > 32 || (i == 2 && nums[i] < 0)) return null;
            if (!ThemePackReader.TryColor(tokens[4], out var color)) return null;
            parts.Add((inset ? "inset " : "") + string.Join(' ', nums.Select(n => n.ToString(CultureInfo.InvariantCulture))) + " " + ThemePackReader.FormatColor(color));
        }
        return string.Join(", ", parts);
    }

    /// <summary>组件上前景和背景都是确定颜色时，也按 4.5:1 修正文字色（token 按深浅两套都要满足）。</summary>
    private static void EnforceContrast(ThemePack pack)
    {
        foreach (var (component, states) in pack.Styles)
        {
            states.TryGetValue("", out var normal);
            foreach (var (state, props) in states)
            {
                if (state == ":disabled") continue;
                var fg = props.Foreground ?? normal?.Foreground;
                var bg = props.Background ?? normal?.Background;
                if (fg is null || bg is null || fg.Kind is not (PaintKind.Color or PaintKind.Token)) continue;
                // 背景是 token 时深浅两套可能一亮一暗（比如松鸦蓝），一种固定颜色满足不了两边：改用一个两套都看得清的文字 token。
                if (bg.Kind == PaintKind.Token && !ReadableInBoth(fg, bg, pack) &&
                    TextTokens.Select(t => new Paint(PaintKind.Token, [], t, 0)).FirstOrDefault(t => ReadableInBoth(t, bg, pack)) is { } token)
                {
                    props.Foreground = token;
                    fg = token;
                    pack.Problems.Add(new("contrast_fixed", $"{component}{state}.foreground"));
                    continue;
                }
                foreach (var dark in new[] { true, false })
                {
                    var bgColors = Resolve(bg, pack, dark);
                    if (bgColors.Count == 0) continue;
                    var fgColor = Resolve(fg, pack, dark)[0];
                    var fixedColor = fgColor;
                    // 渐变的每个颜色上都要看得清：修过一个再查全部，最多三遍；还不行就用黑白里对比度更高的那个。
                    for (var pass = 0; pass < 3 && bgColors.Any(b => ThemeColor.Contrast(fixedColor, b) < 4.5); pass++)
                        foreach (var b in bgColors) fixedColor = ThemeColor.EnsureContrast(fixedColor, b, 4.5);
                    if (bgColors.Any(b => ThemeColor.Contrast(fixedColor, b) < 4.5))
                        fixedColor = bgColors.Min(b => ThemeColor.Contrast(0xFFFFFFFF, b)) >= bgColors.Min(b => ThemeColor.Contrast(0xFF000000, b)) ? 0xFFFFFFFF : 0xFF000000;
                    if (fixedColor == fgColor) continue;
                    props.Foreground = new Paint(PaintKind.Color, [fixedColor], null, 0);
                    fg = props.Foreground;
                    pack.Problems.Add(new("contrast_fixed", $"{component}{state}.foreground"));
                }
            }
        }
    }

    /// <summary>修正文字时按顺序试的 token：和各种底色配套的“上面的字”。</summary>
    private static readonly string[] TextTokens = ["OnAccent", "TextPrimary", "SidebarText", "OnPlay", "OnDanger", "OnBadge"];

    private static bool ReadableInBoth(Paint fg, Paint bg, ThemePack pack) =>
        new[] { true, false }.All(dark => Resolve(bg, pack, dark) is { Count: > 0 } bgs && Resolve(fg, pack, dark) is { Count: > 0 } fgs
            && bgs.All(b => ThemeColor.Contrast(fgs[0], b) >= 4.5));

    private static List<uint> Resolve(Paint paint, ThemePack pack, bool dark) => paint.Kind switch
    {
        PaintKind.Color or PaintKind.Linear => paint.Colors.ToList(),
        PaintKind.Token => [(dark ? pack.DarkColors : pack.LightColors).GetValueOrDefault(paint.Token!, (dark ? ThemeDefaults.Dark : ThemeDefaults.Light)[paint.Token!])],
        _ => [],
    };
}

public enum PaintKind { Color, Token, Linear, Image }

/// <summary>一种填充：纯色、token 引用、线性渐变或包里的图片。</summary>
public sealed record Paint(PaintKind Kind, IReadOnlyList<uint> Colors, string? Token, int Angle)
{
    /// <summary>Image 的路径存在 Token 里（包内相对路径）。</summary>
    public string? ImagePath => Kind == PaintKind.Image ? Token : null;
}

public sealed class StyleProps
{
    public Paint? Background { get; set; }
    public Paint? Foreground { get; set; }
    public Paint? Border { get; set; }
    public double? BorderWidth { get; set; }
    public double? Radius { get; set; }
    public (double H, double V)? Padding { get; set; }
    public string? FontWeight { get; set; }
    public string? Shadow { get; set; }
    public string? Glow { get; set; }
    public Paint? Indicator { get; set; }
    public double? IndicatorHeight { get; set; }
    public Paint? Track { get; set; }
    public Paint? Fill { get; set; }
    public double? Scrim { get; set; }

    public bool IsEmpty => Background is null && Foreground is null && Border is null && BorderWidth is null && Radius is null
        && Padding is null && FontWeight is null && Shadow is null && Glow is null && Indicator is null && IndicatorHeight is null
        && Track is null && Fill is null && Scrim is null;
}
