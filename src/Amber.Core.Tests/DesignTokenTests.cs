using System.Xml.Linq;
using Amber.Core.Layout;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>新界面第 0 批：设计令牌的对比度表（官方设计 §4.2）、分档阈值（§3.2、§3.3）、减少动画（§4.7）、图标资源。</summary>
public class DesignTokenTests
{
    /// <summary>§4.2 “关键对比度”表：深浅两套逐对重算。名字用主题包 format 1 的叫法（PageBg = Base、SidebarBg = Frame…）。</summary>
    public static TheoryData<string, string, double> Pairs => new()
    {
        { "TextPrimary", "PageBg", 4.5 }, { "TextPrimary", "Surface", 4.5 }, { "TextSecondary", "Surface", 4.5 },
        { "TextTertiary", "Surface", 4.5 }, { "TextTertiary", "Overlay", 4.5 }, { "TextTertiary", "ControlPressed", 4.5 },
        { "TextSecondary", "Overlay", 4.5 }, { "TextPrimary", "Overlay", 4.5 },
        { "OnAccent", "Accent", 4.5 }, { "OnAccent", "AccentHover", 4.5 }, { "OnAccent", "AccentPressed", 4.5 },
        { "AccentText", "Surface", 4.5 }, { "AccentText", "ControlSelected", 4.5 },
        { "Accent", "Surface", 3 }, { "Accent", "SurfaceRaised", 3 }, { "Accent", "SidebarBg", 3 },
        { "ControlBorder", "Surface", 3 }, { "ControlBorder", "SidebarBg", 3 }, { "ControlBorder", "SurfaceRaised", 3 }, { "ControlBorder", "Overlay", 3 },
        { "Focus", "PageBg", 3 }, { "Focus", "Surface", 3 }, { "Focus", "SurfaceRaised", 3 }, { "Focus", "Overlay", 3 },
        { "Focus", "Sunken", 3 }, { "Focus", "InputBg", 3 }, { "Focus", "SidebarBg", 3 },
        { "OnPlay", "Play", 4.5 }, { "OnPlay", "PlayHover", 4.5 }, { "OnPlay", "PlayPressed", 4.5 }, { "PlayText", "Surface", 4.5 },
        { "SuccessText", "SuccessTint", 4.5 }, { "WarningText", "WarningTint", 4.5 }, { "DangerText", "DangerTint", 4.5 },
        { "SuccessText", "Overlay", 4.5 }, { "WarningText", "Overlay", 4.5 }, { "DangerText", "Overlay", 4.5 },
        { "SuccessText", "Sunken", 4.5 }, { "WarningText", "Sunken", 4.5 }, { "DangerText", "Sunken", 4.5 },
        { "OnDanger", "Danger", 4.5 }, { "OnBadge", "Badge", 4.5 }, { "OnBadgeMuted", "BadgeMuted", 4.5 },
        { "SidebarText", "NavSelected", 4.5 }, { "SidebarTextMuted", "SidebarBg", 4.5 },
        { "DisabledText", "Surface", 3 }, { "DisabledText", "Overlay", 3 }, { "DisabledText", "SurfaceRaised", 3 }, { "DisabledText", "DisabledFill", 3 },
        { "Danger", "SidebarBg", 3 }, { "Warning", "SidebarBg", 3 }, { "Success", "SidebarBg", 3 },
        { "LoaderVanilla", "Surface", 4.5 }, { "LoaderFabric", "Surface", 4.5 }, { "LoaderForge", "Surface", 4.5 },
        { "LoaderNeoForge", "Surface", 4.5 }, { "LoaderQuilt", "Surface", 4.5 }, { "LoaderOptiFine", "Surface", 4.5 },
        { "LogDebug", "Sunken", 4.5 }, { "LogInfo", "Sunken", 4.5 }, { "LogWarn", "Sunken", 4.5 },
        { "LogError", "Sunken", 4.5 }, { "LogFatal", "Sunken", 4.5 }, { "LogJay", "Sunken", 4.5 },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void DefaultPaletteMeetsContrast(string fg, string bg, double min)
    {
        foreach (var (name, palette) in new[] { ("夜", ThemeDefaults.Dark), ("晨", ThemeDefaults.Light) })
        {
            var ratio = ThemeColor.Contrast(palette[fg], palette[bg]);
            Assert.True(ratio >= min, $"{name}：{fg} / {bg} = {ratio:F2}，要求 ≥ {min}");
        }
    }

    [Fact]
    public void LightPlayButtonNeedsItsBorder()
    {
        // 浅色下琥珀和底色只有 1.81，所以要 PlayBorder 描边，描边对底色 ≥ 3。
        var l = ThemeDefaults.Light;
        Assert.True(ThemeColor.Contrast(l["Play"], l["PageBg"]) < 3);
        Assert.True(ThemeColor.Contrast(l["PlayBorder"], l["PageBg"]) >= 3);
    }

    [Fact]
    public void PlayIsTheOnlyAmberAndAccentIsBlue()
    {
        foreach (var palette in new[] { ThemeDefaults.Dark, ThemeDefaults.Light })
        {
            // 松鸦蓝：蓝通道最大；琥珀：红通道最大。两种颜色分工不能混。
            var accent = palette["Accent"];
            Assert.True(ThemeColor.B(accent) > ThemeColor.R(accent));
            var play = palette["Play"];
            Assert.True(ThemeColor.R(play) > ThemeColor.B(play));
            // 警告是柠檬黄，不是琥珀：绿通道接近红通道。
            var warning = palette["Warning"];
            Assert.True(ThemeColor.G(warning) >= ThemeColor.R(warning) * 0.75);
        }
    }

    [Fact]
    public void ThemeContrastPairsAllHaveDefaults()
    {
        foreach (var (fg, bg, _) in ThemeContrast.Pairs)
        {
            Assert.True(ThemeDefaults.Dark.ContainsKey(fg) && ThemeDefaults.Dark.ContainsKey(bg), $"{fg}/{bg}");
            Assert.True(ThemeDefaults.Light.ContainsKey(fg) && ThemeDefaults.Light.ContainsKey(bg), $"{fg}/{bg}");
        }
    }

    [Fact]
    public void OldFormat1ThemeStillOverridesTheRenamedRoles()
    {
        // 旧主题包只写了 format 1 的名字：PageBg、SidebarBg 这些照常生效，新加的 token 用默认值。
        Assert.Contains("PageBg", ThemePackReader.ColorKeys);
        Assert.Contains("SidebarBg", ThemePackReader.ColorKeys);
        Assert.Contains("Play", ThemePackReader.ColorKeys);
        Assert.Equal(ThemePackReader.ColorKeys.Length, ThemePackReader.ColorKeys.Distinct().Count());
    }

    [Fact]
    public void DarkBackgroundSeedAlsoMovesTheNewSurfaces()
    {
        var derived = ThemeColor.Derive(null, 0xFF101418, dark: true);
        Assert.True(derived.ContainsKey("Overlay"));
        Assert.True(derived.ContainsKey("Sunken"));
        Assert.True(ThemeColor.Contrast(derived["ControlBorder"], derived["Surface"]) >= 3);
    }

    [Theory]
    [InlineData(600, WidthTier.Compact)]
    [InlineData(639.9, WidthTier.Compact)]
    [InlineData(640, WidthTier.Medium)]
    [InlineData(1099, WidthTier.Medium)]
    [InlineData(1100, WidthTier.Wide)]
    [InlineData(double.NaN, WidthTier.Compact)]
    public void WidthTierBoundaries(double width, WidthTier expected) => Assert.Equal(expected, LayoutTiers.ForWidth(width));

    [Theory]
    [InlineData(420, HeightTier.Tight)]
    [InlineData(479, HeightTier.Tight)]
    [InlineData(480, HeightTier.Short)]
    [InlineData(559, HeightTier.Short)]
    [InlineData(560, HeightTier.Normal)]
    public void HeightTierBoundaries(double height, HeightTier expected) => Assert.Equal(expected, LayoutTiers.ForHeight(height));

    [Fact]
    public void TiersHaveHysteresis()
    {
        // 从紧凑往宽拉：过了 640 还要再多 32 才换档；往回缩同理。
        Assert.Equal(WidthTier.Compact, LayoutTiers.ForWidth(660, WidthTier.Compact));
        Assert.Equal(WidthTier.Medium, LayoutTiers.ForWidth(672, WidthTier.Compact));
        Assert.Equal(WidthTier.Medium, LayoutTiers.ForWidth(620, WidthTier.Medium));
        Assert.Equal(WidthTier.Compact, LayoutTiers.ForWidth(607, WidthTier.Medium));
        Assert.Equal(WidthTier.Medium, LayoutTiers.ForWidth(1120, WidthTier.Medium));
        Assert.Equal(WidthTier.Wide, LayoutTiers.ForWidth(1132, WidthTier.Medium));
        Assert.Equal(WidthTier.Wide, LayoutTiers.ForWidth(1080, WidthTier.Wide));
        // 一下子跨两档时直接跳过去，不卡在中间。
        Assert.Equal(WidthTier.Wide, LayoutTiers.ForWidth(1400, WidthTier.Compact));
        Assert.Equal(HeightTier.Normal, LayoutTiers.ForHeight(540, HeightTier.Normal));
        Assert.Equal(HeightTier.Short, LayoutTiers.ForHeight(527, HeightTier.Normal));
        Assert.Equal(HeightTier.Tight, LayoutTiers.ForHeight(500, HeightTier.Tight));
        Assert.Equal(HeightTier.Short, LayoutTiers.ForHeight(512, HeightTier.Tight));
    }

    [Fact]
    public void BiggerTextReachesNarrowTiersEarlier()
    {
        Assert.Equal(WidthTier.Medium, LayoutTiers.ForWidth(780));
        Assert.Equal(WidthTier.Compact, LayoutTiers.ForWidth(780, textScale: 1.25));
        Assert.Equal((672, 470), LayoutTiers.MinimumWindow(1.12));
        Assert.Equal((750, 525), LayoutTiers.MinimumWindow(1.25));
        Assert.Equal((750, 525), LayoutTiers.MinimumWindow(3)); // 超出范围的值夹回 1–1.25
        Assert.Equal((600, 420), LayoutTiers.MinimumWindow(0.5));
    }

    [Theory]
    [InlineData(null, false, null, false)]
    [InlineData(null, true, null, true)]
    [InlineData("system", false, true, true)]
    [InlineData("system", false, false, false)]
    [InlineData("on", false, false, true)]
    [InlineData("off", false, true, false)]
    [InlineData("whatever", false, true, true)]
    public void ReduceMotionResolves(string? mode, bool legacy, bool? system, bool expected) =>
        Assert.Equal(expected, MotionPreference.ShouldReduce(MotionPreference.Migrate(mode, legacy), system));

    [Fact]
    public void ReducedMotionKeepsOnlyShortFades()
    {
        Assert.All(MotionPreference.Durations(true).Values, ms => Assert.True(ms <= 100));
        Assert.Equal(320, MotionPreference.Durations(false)["MotionSlower"]);
        Assert.Equal(true, MotionPreference.ParseGnomeEnableAnimations("false\n"));
        Assert.Equal(false, MotionPreference.ParseGnomeEnableAnimations("true"));
        Assert.Null(MotionPreference.ParseGnomeEnableAnimations("No such schema"));
        Assert.Equal(true, MotionPreference.ParseMacReduceMotion("1\n"));
        Assert.Null(MotionPreference.ParseMacReduceMotion(null));
    }

    [Fact]
    public void MotionTokensInAxamlMatchNormalDurations()
    {
        var doc = XDocument.Load(AppFile("Themes", "Tokens.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (key, ms) in MotionPreference.Durations(false))
        {
            var e = doc.Root!.Elements().Single(e => (string?)e.Attribute(x + "Key") == key);
            Assert.Equal(ms, TimeSpan.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture).TotalMilliseconds);
        }
    }

    [Fact]
    public void IconsAreWellFormedAndUsedNamesExist()
    {
        var doc = XDocument.Load(AppFile("Themes", "Icons.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = doc.Root!.Elements().Select(e => (string)e.Attribute(x + "Key")!).ToHashSet();
        Assert.True(keys.Count >= 60);
        Assert.All(doc.Root.Elements(), e => Assert.StartsWith("M", e.Value.Trim(), StringComparison.OrdinalIgnoreCase));
        // 页面里用到的每个图标都得在表里，不然运行时才报找不到资源。
        var used = Directory.EnumerateFiles(AppFile(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"Resource (Icon\.[A-Za-z]+)").Select(m => m.Groups[1].Value));
        Assert.All(used, k => Assert.Contains(k, keys));
    }

    [Fact]
    public void EveryDynamicResourceKeyIsDefined()
    {
        // A misspelt or removed key does not fail the build: the control silently loses its colour.
        var files = Directory.EnumerateFiles(AppFile(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        var defined = files.SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value)).ToHashSet();
        var missing = files.SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t, @"\{DynamicResource ([A-Za-z0-9_.]+)\}").Select(m => m.Groups[1].Value))
            .Where(k => !defined.Contains(k)).Distinct().ToList();
        Assert.Empty(missing);
    }

    private static string AppFile(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.App", .. parts]);
}
