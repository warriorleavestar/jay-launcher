namespace Amber.Core.Themes;

/// <summary>Jay 自带的配色（和 Amber.App/Themes/Tokens.axaml 一致，测试会核对）。主题没写的 token 用这里的值。</summary>
public static class ThemeDefaults
{
    public static readonly IReadOnlyDictionary<string, uint> Dark = Parse(new()
    {
        ["PageBg"] = "#0E1219", ["Surface"] = "#202A3A", ["SurfaceRaised"] = "#29344A", ["BorderSubtle"] = "#394660",
        ["SidebarBg"] = "#080B10", ["TextPrimary"] = "#E8EDF5", ["TextSecondary"] = "#B3BDCD", ["TextTertiary"] = "#A0ABBE",
        ["Accent"] = "#78A0FF", ["AccentHover"] = "#93B4FF", ["AccentPressed"] = "#6188EA", ["OnAccent"] = "#08132E",
        ["AccentText"] = "#8FB1FF", ["Success"] = "#3DD68C", ["SuccessText"] = "#5BE0A0", ["Warning"] = "#E9C84A",
        ["WarningText"] = "#F0D46A", ["Danger"] = "#FF6B6B", ["DangerText"] = "#FF8C87", ["OnDanger"] = "#1F0606",
        ["DisabledFill"] = "#293342", ["DisabledText"] = "#808B9E", ["SidebarText"] = "#E8EDF5", ["SidebarTextMuted"] = "#A3AEC0",
        ["NavSelected"] = "#1A2336", ["NavHover"] = "#17191E", ["AccentTint"] = "#2E3D5A", ["SuccessTint"] = "#244246",
        ["WarningTint"] = "#3C403C", ["DangerTint"] = "#3F3341", ["Overlay"] = "#2D394E", ["Sunken"] = "#070A0E",
        ["InputBg"] = "#0B0F16", ["Scrim"] = "#8C000000", ["ControlBorder"] = "#7783A0", ["ControlHover"] = "#2D3746",
        ["ControlPressed"] = "#37404E", ["ControlSelected"] = "#2E3D5A", ["Focus"] = "#A9C4FF", ["Play"] = "#F6A93B",
        ["PlayHover"] = "#FFB955", ["PlayPressed"] = "#E0952A", ["OnPlay"] = "#1E1404", ["PlayBorder"] = "#00000000",
        ["PlayText"] = "#F8B85C", ["PlayGlow"] = "#40F6A93B", ["PlayTint"] = "#3E3C3A", ["Badge"] = "#FF6B6B",
        ["OnBadge"] = "#1F0606", ["BadgeMuted"] = "#455068", ["OnBadgeMuted"] = "#E8EDF5", ["IlloLine"] = "#93B4FF",
        ["IlloFill"] = "#202A3A", ["LoaderVanilla"] = "#7DD08A", ["LoaderFabric"] = "#E2C49A", ["LoaderForge"] = "#A9BCD6",
        ["LoaderNeoForge"] = "#F5A66B", ["LoaderQuilt"] = "#C3A6FA", ["LoaderOptiFine"] = "#F08C8C", ["LogDebug"] = "#8D98AA",
        ["LogInfo"] = "#E8EDF5", ["LogWarn"] = "#F0D46A", ["LogError"] = "#FF8C87", ["LogFatal"] = "#FF7AA8",
        ["LogJay"] = "#F8B85C",
    });

    public static readonly IReadOnlyDictionary<string, uint> Light = Parse(new()
    {
        ["PageBg"] = "#F3F5F9", ["Surface"] = "#FFFFFF", ["SurfaceRaised"] = "#EEF1F6", ["BorderSubtle"] = "#D3D9E2",
        ["SidebarBg"] = "#E4E9F0", ["TextPrimary"] = "#111722", ["TextSecondary"] = "#455164", ["TextTertiary"] = "#566275",
        ["Accent"] = "#2C59CC", ["AccentHover"] = "#3A67DA", ["AccentPressed"] = "#2149B0", ["OnAccent"] = "#FFFFFF",
        ["AccentText"] = "#2550BF", ["Success"] = "#16804F", ["SuccessText"] = "#11683F", ["Warning"] = "#9A7400",
        ["WarningText"] = "#7A5C00", ["Danger"] = "#C93030", ["DangerText"] = "#B02828", ["OnDanger"] = "#FFFFFF",
        ["DisabledFill"] = "#F3F4F5", ["DisabledText"] = "#7C8698", ["SidebarText"] = "#111722", ["SidebarTextMuted"] = "#455164",
        ["NavSelected"] = "#CED7EC", ["NavHover"] = "#D9DEE6", ["AccentTint"] = "#E5EBF9", ["SuccessTint"] = "#E7F2ED",
        ["WarningTint"] = "#F5F1E5", ["DangerTint"] = "#F9EAEA", ["Overlay"] = "#FFFFFF", ["Sunken"] = "#EBEEF3",
        ["InputBg"] = "#FFFFFF", ["Scrim"] = "#590B1220", ["ControlBorder"] = "#6E7B8F", ["ControlHover"] = "#F3F3F5",
        ["ControlPressed"] = "#E9EAED", ["ControlSelected"] = "#E5EBF9", ["Focus"] = "#2C59CC", ["Play"] = "#F6A93B",
        ["PlayHover"] = "#FFB955", ["PlayPressed"] = "#E0952A", ["OnPlay"] = "#1E1404", ["PlayBorder"] = "#A3620C",
        ["PlayText"] = "#8F520A", ["PlayGlow"] = "#4DF6A93B", ["PlayTint"] = "#FEF5E7", ["Badge"] = "#C9302C",
        ["OnBadge"] = "#FFFFFF", ["BadgeMuted"] = "#5A6578", ["OnBadgeMuted"] = "#FFFFFF", ["IlloLine"] = "#3A67DA",
        ["IlloFill"] = "#E5EBF9", ["LoaderVanilla"] = "#236B30", ["LoaderFabric"] = "#7D5E30", ["LoaderForge"] = "#3D5675",
        ["LoaderNeoForge"] = "#9C4710", ["LoaderQuilt"] = "#6A3FC2", ["LoaderOptiFine"] = "#A3303A", ["LogDebug"] = "#566275",
        ["LogInfo"] = "#111722", ["LogWarn"] = "#7A5C00", ["LogError"] = "#B02828", ["LogFatal"] = "#A3245A",
        ["LogJay"] = "#8F520A",
    });

    public static readonly IReadOnlyDictionary<string, double> Numbers = new Dictionary<string, double>
    {
        ["FontCaption"] = 12, ["FontBodySmall"] = 13, ["FontBody"] = 14, ["FontSubtitle"] = 16, ["FontTitle"] = 20, ["FontHeadline"] = 24, ["FontDisplay"] = 32,
        ["Space0"] = 2, ["Space1"] = 4, ["Space2"] = 8, ["Space3"] = 12, ["Space4"] = 16, ["Space5"] = 24, ["Space6"] = 32, ["Space7"] = 48, ["Space8"] = 64,
    };

    public static readonly IReadOnlyDictionary<string, double> Radii = new Dictionary<string, double>
    {
        ["RadiusXs"] = 4, ["RadiusSm"] = 6, ["RadiusMd"] = 8, ["RadiusLg"] = 12, ["RadiusXl"] = 16, ["RadiusPill"] = 999,
    };

    private static Dictionary<string, uint> Parse(Dictionary<string, string> hex) =>
        hex.ToDictionary(p => p.Key, p => ThemePackReader.TryColor(p.Value, out var c) ? c : throw new InvalidOperationException(p.Key));
}

/// <summary>颜色计算：WCAG 相对亮度和对比度、混色、从种子色推导整套配色。</summary>
public static class ThemeColor
{
    public static byte A(uint c) => (byte)(c >> 24);
    public static byte R(uint c) => (byte)(c >> 16);
    public static byte G(uint c) => (byte)(c >> 8);
    public static byte B(uint c) => (byte)c;
    public static uint Argb(byte a, byte r, byte g, byte b) => (uint)a << 24 | (uint)r << 16 | (uint)g << 8 | b;

    /// <summary>a 到 b 按 t（0..1）混合，结果不透明度取 a 的。</summary>
    public static uint Mix(uint a, uint b, double t)
    {
        byte m(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Argb(A(a), m(R(a), R(b)), m(G(a), G(b)), m(B(a), B(b)));
    }

    /// <summary>半透明颜色叠在背景上的实际颜色。</summary>
    public static uint Over(uint fg, uint bg)
    {
        var alpha = A(fg) / 255.0;
        return alpha >= 1 ? fg : Mix(Argb(0xFF, R(bg), G(bg), B(bg)), Argb(0xFF, R(fg), G(fg), B(fg)), alpha);
    }

    public static double Luminance(uint c)
    {
        static double ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * ch(R(c)) + 0.7152 * ch(G(c)) + 0.0722 * ch(B(c));
    }

    public static double Contrast(uint fg, uint bg)
    {
        var solidBg = Argb(0xFF, R(bg), G(bg), B(bg));
        var l1 = Luminance(Over(fg, solidBg));
        var l2 = Luminance(solidBg);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    public static bool IsDark(uint c) => Luminance(c) < 0.18;

    private const uint White = 0xFFFFFFFF;
    private const uint Black = 0xFF000000;

    /// <summary>把前景色往黑或白推（选能达到更高对比度的那一头），直到对比度够为止。</summary>
    public static uint EnsureContrast(uint fg, uint bg, double min)
    {
        var solid = Over(fg, bg);
        if (Contrast(solid, bg) >= min) return fg;
        var target = Contrast(White, bg) >= Contrast(Black, bg) ? White : Black;
        for (var t = 0.05; t <= 1.0001; t += 0.05)
        {
            var c = Mix(solid, target, t);
            if (Contrast(c, bg) >= min) return c;
        }
        return target;
    }

    /// <summary>
    /// 从一两个种子色推出整套配色。accent 推强调色一族；background 只用在跟它明暗一致的那一套（深色背景只改深色主题）。
    /// </summary>
    public static Dictionary<string, uint> Derive(uint? accent, uint? background, bool dark)
    {
        var result = new Dictionary<string, uint>();
        var defaults = dark ? ThemeDefaults.Dark : ThemeDefaults.Light;
        if (background is { } bg0 && IsDark(bg0) == dark)
        {
            var bg = bg0 | 0xFF000000;
            var ink = dark ? White : Black;
            result["PageBg"] = bg;
            result["Surface"] = dark ? Mix(bg, White, 0.06) : Mix(bg, White, 0.7);
            result["SurfaceRaised"] = dark ? Mix(bg, White, 0.10) : Mix(bg, White, 0.4);
            result["BorderSubtle"] = Mix(bg, ink, dark ? 0.16 : 0.13);
            result["SidebarBg"] = dark ? Mix(bg, Black, 0.3) : Mix(bg, Black, 0.78);
            result["TextPrimary"] = Mix(ink, bg, 0.08);
            result["TextSecondary"] = Mix(ink, bg, 0.32);
            result["TextTertiary"] = Mix(ink, bg, 0.40);
            result["DisabledFill"] = Mix(bg, ink, 0.12);
            result["DisabledText"] = Mix(ink, bg, 0.42);
            var sidebar = result["SidebarBg"];
            result["SidebarText"] = Mix(White, sidebar, 0.08);
            result["SidebarTextMuted"] = Mix(White, sidebar, 0.32);
            result["NavSelected"] = Mix(sidebar, White, 0.10);
            result["NavHover"] = Mix(sidebar, White, 0.06);
            // 第 0 批新加的表面：跟着背景走，不然深色背景的主题会配上 Jay 默认的墨蓝菜单
            var surface = result["Surface"];
            result["Overlay"] = dark ? Mix(bg, White, 0.14) : White;
            result["Sunken"] = dark ? Mix(bg, Black, 0.45) : Mix(bg, Black, 0.04);
            result["InputBg"] = dark ? Mix(bg, Black, 0.25) : White;
            result["ControlBorder"] = EnsureContrast(Mix(ink, bg, 0.5), surface, 3.0);
            result["ControlHover"] = Mix(surface, ink, dark ? 0.06 : 0.05);
            result["ControlPressed"] = Mix(surface, ink, dark ? 0.10 : 0.09);
            result["IlloFill"] = surface;
        }
        if (accent is { } a0)
        {
            var a = a0 | 0xFF000000;
            var page = result.GetValueOrDefault("PageBg", defaults["PageBg"]);
            result["Accent"] = a;
            result["AccentHover"] = Mix(a, White, 0.12);
            result["AccentPressed"] = Mix(a, Black, 0.15);
            result["OnAccent"] = Contrast(Black, a) >= Contrast(White, a) ? 0xFF1C1408 : White;
            result["AccentText"] = EnsureContrast(dark ? Mix(a, White, 0.12) : Mix(a, Black, 0.3), page, 4.5);
            result["AccentTint"] = (a & 0x00FFFFFF) | 0x26000000;
            result["ControlSelected"] = result["AccentTint"];
            result["Focus"] = dark ? Mix(a, White, 0.3) : a;
            result["IlloLine"] = result["AccentHover"];
        }
        return result;
    }
}

/// <summary>
/// 关键文字的对比度底线：主题配得看不清时自动修正前景色，不拒绝整个主题。
/// 每对的背景按“主题值，没有就用 Jay 默认值”算，所以只改了背景的主题，文字色也会跟着修。
/// </summary>
public static class ThemeContrast
{
    public static readonly (string Fg, string Bg, double Min)[] Pairs =
    [
        ("TextPrimary", "PageBg", 4.5), ("TextPrimary", "Surface", 4.5),
        ("TextSecondary", "PageBg", 4.5), ("TextSecondary", "Surface", 4.5),
        ("TextTertiary", "Surface", 4.5), ("TextTertiary", "PageBg", 4.5),
        ("AccentText", "Surface", 4.5), ("AccentText", "PageBg", 4.5),
        ("OnAccent", "Accent", 4.5), ("OnDanger", "Danger", 4.5),
        ("DangerText", "Surface", 4.5), ("WarningText", "Surface", 4.5), ("SuccessText", "Surface", 4.5),
        ("SidebarText", "SidebarBg", 4.5), ("SidebarTextMuted", "SidebarBg", 4.5),
        ("SidebarText", "NavSelected", 4.5),
        ("DisabledText", "DisabledFill", 3.0),
        ("TextPrimary", "Overlay", 4.5), ("TextSecondary", "Overlay", 4.5), ("TextTertiary", "Overlay", 4.5),
        ("OnPlay", "Play", 4.5), ("PlayText", "Surface", 4.5), ("OnBadge", "Badge", 4.5), ("OnBadgeMuted", "BadgeMuted", 4.5),
    ];

    public static void Enforce(ThemePack pack)
    {
        if (pack.Variants != "light") Enforce(pack.DarkColors, ThemeDefaults.Dark, pack.Problems, "dark");
        if (pack.Variants != "dark") Enforce(pack.LightColors, ThemeDefaults.Light, pack.Problems, "light");
        if (pack.Variants == "both") return;
        // 只有一种的主题两套一样：修好的那套复制过去。
        var source = pack.Variants == "dark" ? pack.DarkColors : pack.LightColors;
        var other = pack.Variants == "dark" ? pack.LightColors : pack.DarkColors;
        other.Clear();
        foreach (var (k, v) in source) other[k] = v;
    }

    private static void Enforce(Dictionary<string, uint> colors, IReadOnlyDictionary<string, uint> defaults, List<ThemeProblem> problems, string variant)
    {
        if (colors.Count == 0) return;
        // format 1 的主题不知道新加的表面：改了卡片色、底色却没写菜单、日志、输入框的底时，让它们跟着走，
        // 不然浅色主题会配上 Jay 默认的墨蓝菜单，文字两边都顾不上。
        if (colors.TryGetValue("Surface", out var surface) && !colors.ContainsKey("Overlay")) colors["Overlay"] = surface;
        if (colors.TryGetValue("PageBg", out var page))
        {
            if (!colors.ContainsKey("Sunken")) colors["Sunken"] = page;
            if (!colors.ContainsKey("InputBg")) colors["InputBg"] = page;
        }
        // 背景层次多的时候前景要同时满足几种背景：多跑两遍，后一对改过的颜色前一对还要再查。
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var (fgKey, bgKey, min) in Pairs)
            {
                if (!colors.ContainsKey(fgKey) && !colors.ContainsKey(bgKey)) continue;
                var fg = colors.GetValueOrDefault(fgKey, defaults[fgKey]);
                var bg = colors.GetValueOrDefault(bgKey, defaults[bgKey]);
                if (ThemeColor.Contrast(fg, bg) >= min) continue;
                colors[fgKey] = ThemeColor.EnsureContrast(fg, bg, min);
                if (pass == 0) problems.Add(new("contrast_fixed", $"{variant}.{fgKey} on {bgKey}"));
                changed = true;
            }
            if (!changed) break;
        }
    }
}

/// <summary>资源文件的快速检查：只看文件头，不在这里解码（解码交给界面层，有尺寸上限在先）。</summary>
public static class ThemeAssets
{
    public static bool LooksLikeFont(ReadOnlySpan<byte> b) =>
        b.Length >= 12 && (b[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || b[..4].SequenceEqual("OTTO"u8) || b[..4].SequenceEqual("true"u8));

    public static (int Width, int Height)? ImageSize(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 24 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && b[12..16].SequenceEqual("IHDR"u8))
        {
            var w = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19];
            var h = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
            return w > 0 && h > 0 ? (w, h) : null;
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var len = b[i + 2] << 8 | b[i + 3];
                if (len < 2) return null;
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    var h = b[i + 5] << 8 | b[i + 6];
                    var w = b[i + 7] << 8 | b[i + 8];
                    return w > 0 && h > 0 ? (w, h) : null;
                }
                i += 2 + len;
            }
        }
        return null;
    }
}
