using System.Text;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>Theme pack format 2 (official §10): new names, minJay, the wider component set, and the upgrade of format 1 packs.</summary>
public class ThemeFormat2Tests
{
    private static ThemePack Read(string manifest, string? tokens = null, string? styles = null)
    {
        var files = new Dictionary<string, byte[]> { ["theme.json"] = Encoding.UTF8.GetBytes(manifest) };
        if (tokens is not null) files["tokens.json"] = Encoding.UTF8.GetBytes(tokens);
        if (styles is not null) files["styles.json"] = Encoding.UTF8.GetBytes(styles);
        return ThemePackReader.ParseFiles(files);
    }

    private const string V1 = """{ "format": 1, "id": "old-theme", "name": "Old", "version": "1.0.0" }""";
    private const string V2 = """{ "format": 2, "id": "new-theme", "name": "New", "version": "2.0.0" }""";

    [Fact]
    public void MissingFormatIsFormatOneAndIsUpgraded()
    {
        var pack = Read("""{ "id": "no-format", "name": "x" }""");
        Assert.Equal(1, pack.Format);
        Assert.True(pack.Upgraded);
        Assert.Contains(pack.Migration, m => m.Code == "migrated_format");

        var v2 = Read(V2);
        Assert.False(v2.Upgraded);
        Assert.Empty(v2.Migration);
        Assert.Empty(v2.Problems);
    }

    [Fact]
    public void NewerFormatStillReadsWhatItKnows()
    {
        var pack = Read("""{ "format": 3, "id": "future", "name": "x" }""", """{ "colors": { "both": { "Accent": "#3366CC" } } }""");
        Assert.Contains(pack.Problems, p => p.Code == "newer_format");
        Assert.Equal(0xFF3366CCu, pack.DarkColors["Accent"]);
    }

    [Theory]
    [InlineData("0.8.0", "0.7.0", true)]
    [InlineData("0.7.0", "0.7.0", false)]
    [InlineData("0.7", "0.7.3", false)]
    [InlineData("v1.0.0-beta", "0.9.9", true)]
    [InlineData("1", "1.0", false)]
    [InlineData("not a version", "0.1", false)]
    public void MinJayComparesVersions(string minJay, string jay, bool newer) =>
        Assert.Equal(newer, ThemeFormat.NeedsNewerJay(minJay, Version.Parse(jay)));

    [Fact]
    public void TooNewMinJayIsReportedButThePackStillReads()
    {
        var pack = Read("""{ "format": 2, "id": "needs-new", "name": "x", "minJay": "999.0.0" }""", """{ "colors": { "both": { "Accent": "#3366CC" } } }""");
        Assert.Equal("999.0.0", pack.MinJay);
        Assert.Contains(pack.Problems, p => p.Code == "needs_newer_jay" && p.Detail == "999.0.0");
        Assert.Equal(0xFF3366CCu, pack.DarkColors["Accent"]);
    }

    [Fact]
    public void FormatTwoColorNamesMapToJayResources()
    {
        var pack = Read(V2, """
            { "colors": { "dark": { "Base": "#101010", "Text": "#F0F0F0", "Frame": "#000000", "Divider": "#505050", "TextDisabled": "#909090" } } }
            """, """{ "card": { "background": "token:Raised" } }""");
        Assert.Equal(0xFF101010u, pack.DarkColors["PageBg"]);
        Assert.Equal(0xFFF0F0F0u, pack.DarkColors["TextPrimary"]);
        Assert.Equal(0xFF000000u, pack.DarkColors["SidebarBg"]);
        Assert.Equal("SurfaceRaised", pack.Styles["card"][""].Background!.Token);
        Assert.Equal("Frame", ThemeFormat.PublicColorName("SidebarBg"));
        Assert.Equal("Base", ThemeFormat.PublicColorName("PageBg"));
    }

    [Fact]
    public void FormatOneAccentIsUsedForTheStartButton()
    {
        var pack = Read(V1, """{ "colors": { "both": { "Accent": "#2E7D32", "OnAccent": "#FFFFFF" } } }""");
        Assert.Equal(0xFF2E7D32u, pack.DarkColors["Play"]);
        Assert.Equal(0xFFFFFFFFu, pack.LightColors["OnPlay"]);
        Assert.Contains(pack.Migration, m => m.Code == "migrated_play_from_accent");

        // Format 2 packs say what they want: an accent alone in "colors" leaves Jay's start button alone.
        var v2 = Read(V2, """{ "colors": { "both": { "Accent": "#2E7D32" } } }""");
        Assert.False(v2.DarkColors.ContainsKey("Play"));
    }

    [Fact]
    public void SeedPlayAndOneColorThemes()
    {
        var oneColor = Read(V2, """{ "seed": { "accent": "#7C3AED" } }""");
        Assert.Equal(0xFF7C3AEDu, oneColor.DarkColors["Play"]);
        var two = Read(V2, """{ "seed": { "accent": "#7C3AED", "play": "#F6A93B" } }""");
        Assert.Equal(0xFFF6A93Bu, two.LightColors["Play"]);
        Assert.Equal(0xFF7C3AEDu, two.LightColors["Accent"]);
    }

    [Theory]
    [InlineData("#0F9D8A")]
    [InlineData("#F6A93B")]
    [InlineData("#7C3AED")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#E5E500")]
    public void SeededButtonsStayReadableWhenHoveredAndPressed(string color)
    {
        var pack = Read(V2, $$"""{ "seed": { "accent": "{{color}}", "background": "#0E1219" } }""");
        foreach (var colors in new[] { pack.DarkColors, pack.LightColors })
        {
            foreach (var fill in new[] { "Accent", "AccentHover", "AccentPressed" })
                Assert.True(ThemeColor.Contrast(colors["OnAccent"], colors[fill]) >= 4.5, $"OnAccent on {fill}");
            foreach (var fill in new[] { "Play", "PlayHover", "PlayPressed" })
                Assert.True(ThemeColor.Contrast(colors["OnPlay"], colors[fill]) >= 4.5, $"OnPlay on {fill}");
        }
        Assert.DoesNotContain(pack.Problems, p => p.Detail.Contains("Pressed") || p.Detail.Contains("Hover"));
    }

    [Fact]
    public void FormatOneNumbersAreUpgraded()
    {
        var pack = Read(V1, """{ "sizes": { "FontDisplay": 26 }, "radius": { "RadiusSm": 3, "RadiusLg": 0 } }""");
        Assert.Equal(26, pack.Numbers["FontHeadline"]);
        Assert.Equal(32, pack.Numbers["FontDisplay"]);
        Assert.Equal(3, pack.Radii["RadiusXs"]);
        Assert.Equal(0, pack.Radii["RadiusXl"]); // square stays square
        var round = Read(V1, """{ "radius": { "RadiusLg": 12 } }""");
        Assert.Equal(16, round.Radii["RadiusXl"]);

        // The same values in a format 2 pack mean exactly what they say.
        var v2 = Read(V2, """{ "sizes": { "FontDisplay": 26 } }""");
        Assert.Equal(26, v2.Numbers["FontDisplay"]);
        Assert.False(v2.Numbers.ContainsKey("FontHeadline"));
    }

    [Fact]
    public void ControlHeightsOnlyGrowAndMotionIsCapped()
    {
        var pack = Read(V2, """
            { "sizes": { "ControlHeight": 20, "ControlHeightLg": 50, "PlayHeight": 30 },
              "motion": { "DurationBase": 1000, "DurationFast": 50, "DurationSpin": 3 } }
            """);
        Assert.Equal(36, pack.Numbers["ControlHeight"]);
        Assert.Equal(50, pack.Numbers["ControlHeightLg"]);
        Assert.Equal(40, pack.Numbers["PlayButtonHeight"]);
        Assert.Equal(320, pack.Durations["MotionBase"]);
        Assert.Equal(50, pack.Durations["MotionFast"]);
        Assert.Contains(pack.Problems, p => p.Code == "unknown_key" && p.Detail == "motion.DurationSpin");
        Assert.Equal("PlayHeight", ThemeFormat.PublicNumberName("PlayButtonHeight"));
    }

    [Fact]
    public void HeroScrimNeverGoesBelowTheFloor()
    {
        var pack = Read(V2, """{ "hero": { "mode": "blur", "scrim": 0.3 } }""", """{ "hero": { "scrim": 0.2, "radius": 20 } }""");
        Assert.Equal("blur", pack.HeroMode);
        Assert.Equal(0.85, pack.HeroScrim);
        Assert.Equal(0.85, pack.Styles["hero"][""].Scrim);
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped");
    }

    [Fact]
    public void FormatOneComponentsAreRenamed()
    {
        var pack = Read(V1, styles: """
            { "callout": { "radius": 2 },
              "pill": { "radius": 3 },
              "chip": { "radius": 9 },
              "nav.item": { "radius": 4 },
              "sidebar": { "background": "#112233" } }
            """);
        Assert.Equal(2, pack.Styles["banner"][""].Radius);
        Assert.Equal(9, pack.Styles["chip"][""].Radius); // the pack's own chip wins over the old pill
        Assert.Equal(3, pack.Styles["badge"][""].Radius);
        Assert.Equal(3, pack.Styles["tag"][""].Radius);
        Assert.Equal(4, pack.Styles["rail.item"][""].Radius);
        Assert.Equal(4, pack.Styles["nav.item"][""].Radius);
        Assert.Equal(0xFF112233u, pack.Styles["topbar"][""].Background!.Colors[0]);
        Assert.False(pack.Styles.ContainsKey("callout"));
        Assert.Contains(pack.Migration, m => m.Code == "migrated_component" && m.Detail.StartsWith("pill"));

        var v2 = Read(V2, styles: """{ "nav.item": { "radius": 4 }, "sidebar": { "background": "#112233" } }""");
        Assert.False(v2.Styles.ContainsKey("rail.item"));
        Assert.False(v2.Styles.ContainsKey("topbar"));
        Assert.Empty(v2.Migration);
    }

    [Fact]
    public void FormatTwoComponentsAndTheirExtras()
    {
        var pack = Read(V2, styles: """
            { "button.play": { "glow": "#80F6A93B", ":hover": { "background": "token:PlayHover" } },
              "tab": { "indicator": "token:Accent", "indicatorHeight": 9, ":selected": { "fontWeight": "bold" } },
              "progress": { "track": "#202020", "fill": "linear(90, #FF0000, #00FF00)" },
              "card.cover": { ":hover": { "border": "#FFFFFF" } },
              "switch": { ":checked": { "background": "token:Accent" } },
              "button.tonal": { "radius": 6 },
              "toast": { "radius": 6 }, "dialog": { "radius": 6 }, "requestbar": { "radius": 6 } }
            """);
        Assert.DoesNotContain(pack.Problems, p => p.Code is "unknown_component" or "unknown_state" or "unknown_key");
        Assert.Equal("0 0 16 0 #80F6A93B", pack.Styles["button.play"][""].Glow);
        Assert.Equal(4, pack.Styles["tab"][""].IndicatorHeight);
        Assert.Equal(PaintKind.Linear, pack.Styles["progress"][""].Fill!.Kind);
        Assert.Equal("Accent", pack.Styles["switch"][":checked"].Background!.Token);
        // Extras belong to their component only.
        var wrong = Read(V2, styles: """{ "card": { "glow": "#FFFFFF", "indicator": "#FFFFFF" } }""");
        Assert.Equal(2, wrong.Problems.Count(p => p.Code == "unknown_key"));
    }

    [Fact]
    public void DarkCardsThatMeltIntoThePageGetALighterDivider()
    {
        var pack = Read(V2, """{ "variants": "dark", "colors": { "dark": { "PageBg": "#101010", "Surface": "#121212", "BorderSubtle": "#151515" } } }""");
        Assert.True(ThemeColor.Contrast(pack.DarkColors["BorderSubtle"], 0xFF101010) >= 1.6);
        Assert.Contains(pack.Problems, p => p.Code == "contrast_fixed" && p.Detail.Contains("BorderSubtle"));
    }

    [Fact]
    public void StartButtonThatBlendsInGetsAnOutline()
    {
        var pack = Read(V2, """{ "variants": "dark", "colors": { "dark": { "PageBg": "#202020", "Play": "#262626", "OnPlay": "#FFFFFF", "PlayHover": "#303030", "PlayPressed": "#1C1C1C" } } }""");
        Assert.True(ThemeColor.Contrast(pack.DarkColors["PlayBorder"], 0xFF202020) >= 3);
        // Jay's light theme already outlines its start button: kept as it is.
        var light = Read(V2, """{ "variants": "light", "colors": { "light": { "PageBg": "#FFFFFF" } } }""");
        Assert.False(light.LightColors.ContainsKey("PlayBorder"));
    }

    [Fact]
    public void TextOnTintedBannersIsCheckedAfterCompositing()
    {
        // A see-through tint over a dark card: the text is checked against what is actually seen.
        var pack = Read(V2, """{ "variants": "dark", "colors": { "dark": { "Surface": "#1A1A1A", "AccentTint": "#40FFFFFF", "TextPrimary": "#B0B0B0" } } }""");
        var seen = ThemeColor.Mix(0xFF1A1A1A, 0xFFFFFFFF, 0x40 / 255.0);
        Assert.True(ThemeColor.Contrast(pack.DarkColors["TextPrimary"], seen) >= 4.5);
    }

    [Fact]
    public void ReportListsUpgradeNotesAndFixes()
    {
        var pack = Read(V1, """{ "colors": { "dark": { "PageBg": "#808080", "TextPrimary": "#808080" } } }""", """{ "callout": { "radius": 2 } }""");
        var report = ThemeFormat.Report(pack);
        Assert.Contains("Old 1.0.0 (format 1)", report);
        Assert.Contains("migrated_component", report);
        Assert.Contains("contrast_fixed ×", report);
    }

    [Fact]
    public void WriterProducesFormatTwoThatReadsBackClean()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-theme2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "copy.jaytheme");
            ThemePackWriter.Write(path, "copy", "Copy", ThemeDefaults.Dark, ThemeDefaults.Light, ThemeDefaults.Numbers, ThemeDefaults.Radii);
            var pack = ThemePackReader.Read(path);
            Assert.Equal(2, pack.Format);
            Assert.Empty(pack.Problems);
            Assert.Empty(pack.Migration);
            Assert.Equal(ThemeDefaults.Dark["PageBg"], pack.DarkColors["PageBg"]);
            Assert.Equal(48, pack.Numbers["PlayButtonHeight"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeysReservedForLaterMotionAndScenesLoadWithoutProblems()
    {
        // A pack written for a later Jay with pseudo-3D motion and a scene slot still loads here: the reserved keys are
        // read past and listed, never reported as unknown.
        var pack = Read(V2, """{ "motion3d": { "tilt": 6, "depth": 0.4 }, "slots": { "scene": { "image": "assets/scene.png" } } }""");
        Assert.Empty(pack.Problems);
        Assert.Equal(["motion3d", "slots.scene"], pack.ReservedKeys.OrderBy(k => k).ToArray());
        Assert.Empty(pack.Slots);

        var other = Read(V2, """{ "slots": { "floor": { "image": "assets/floor.png" } } }""");
        Assert.Contains(other.Problems, p => p.Code == "unknown_key" && p.Detail == "slots.floor");
    }
}
