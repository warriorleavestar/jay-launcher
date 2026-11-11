using System.Globalization;
using System.Xml.Linq;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>The 2026-11 art direction as data: wing bars (Jay's mark) and pixel faces.</summary>
public sealed class ArtDirectionTests
{
    private const uint Accent = 0xFF112233, Light = 0xFFAABBCC, Dark = 0xFF010203;

    [Fact]
    public void WingBarsAreFourHardEdgedBarsInThirteenUnits()
    {
        var stops = WingBars.Stops(Accent, Light, Dark);
        Assert.Equal(13, WingBars.Period);
        Assert.Equal([0, 0.4615, 0.4615, 0.6154, 0.6154, 0.8462, 0.8462, 1], stops.Select(s => s.Offset));
        Assert.Equal([Accent, Accent, Dark, Dark, Light, Light, Dark, Dark], stops.Select(s => s.Color));
    }

    [Fact]
    public void TokensAxamlWingBrushesMatchTheDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.App", "Themes", "Tokens.axaml");
        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (variant, defaults) in new[] { ("Dark", ThemeDefaults.Dark), ("Light", ThemeDefaults.Light) })
        {
            var dict = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant);
            var expected = WingBars.Stops(defaults["Accent"], defaults["WingLight"], defaults["WingDark"]);
            foreach (var (key, end) in new[] { ("WingBrush", $"{WingBars.Period},0"), ("WingBrushVertical", $"0,{WingBars.Period}") })
            {
                var brush = dict.Elements().Single(e => e.Name.LocalName == "LinearGradientBrush" && (string?)e.Attribute(x + "Key") == key);
                Assert.Equal("0,0", (string?)brush.Attribute("StartPoint"));
                Assert.Equal(end, (string?)brush.Attribute("EndPoint"));
                Assert.Equal("Repeat", (string?)brush.Attribute("SpreadMethod"));
                var stops = brush.Elements().Select(e =>
                {
                    Assert.True(ThemePackReader.TryColor((string)e.Attribute("Color")!, out var color));
                    return (Offset: double.Parse((string)e.Attribute("Offset")!, CultureInfo.InvariantCulture), Color: color);
                }).ToList();
                Assert.Equal(expected, stops);
            }
        }
    }

    [Fact]
    public void DefaultWingBarsReadAsBars()
    {
        foreach (var palette in new[] { ThemeDefaults.Dark, ThemeDefaults.Light })
        {
            // The three colours must stay apart, or the mark turns into a plain blue line.
            Assert.True(ThemeColor.Contrast(palette["WingLight"], palette["WingDark"]) >= 7);
            Assert.True(ThemeColor.Contrast(palette["Accent"], palette["WingDark"]) >= 3);
            Assert.True(ThemeColor.Contrast(palette["WingLight"], palette["Accent"]) >= 1.5);
        }
    }

    [Fact]
    public void ThemePackWingBarsFollowItsAccent()
    {
        // Nothing set: Jay's own bars.
        var none = WingBars.Colors(new Dictionary<string, uint>(), dark: true);
        Assert.Equal((ThemeDefaults.Dark["Accent"], ThemeDefaults.Dark["WingLight"], ThemeDefaults.Dark["WingDark"]), none);

        // Only an accent: a lighter and a darker bar of the same hue, on both variants.
        const uint green = 0xFF5BD75B;
        foreach (var dark in new[] { true, false })
        {
            var (accent, light, deep) = WingBars.Colors(new Dictionary<string, uint> { ["Accent"] = green }, dark);
            Assert.Equal(green, accent);
            Assert.True(ThemeColor.Luminance(light) > ThemeColor.Luminance(accent));
            Assert.True(ThemeColor.Luminance(deep) < ThemeColor.Luminance(accent));
            Assert.True(ThemeColor.G(light) > ThemeColor.R(light) && ThemeColor.G(deep) > ThemeColor.R(deep), "bars keep the accent's hue");
            Assert.True(ThemeColor.Contrast(light, deep) >= 4.5);
        }

        // The pack's own bar colours win, and a translucent accent is made opaque (gradient stops are drawn as is).
        var own = WingBars.Colors(new Dictionary<string, uint> { ["Accent"] = 0x805BD75B, ["WingLight"] = 0xFFFFFFFF, ["WingDark"] = 0xFF000000 }, dark: true);
        Assert.Equal((green, 0xFFFFFFFF, 0xFF000000), own);
    }

    public static TheoryData<string> Names => new(["Steve", "Alex", "小松鸦", "あかり", "", "a", "Notch_fan", "林间小屋"]);

    [Theory]
    [MemberData(nameof(Names))]
    public void FaceIsStableForAName(string name)
    {
        var cells = PixelFace.Cells(name);
        Assert.Equal(PixelFace.Size * PixelFace.Size, cells.Length);
        Assert.Equal(cells, PixelFace.Cells(name));
        Assert.All(cells, c => Assert.Equal(0xFFu, c >> 24));
    }

    [Fact]
    public void FaceMouthIsAlwaysCentredAndEyesAreLevel()
    {
        var distinct = new HashSet<string>();
        for (var i = 0; i < 600; i++)
        {
            var name = "player-" + i;
            var cells = PixelFace.Cells(name);
            distinct.Add(string.Join(',', cells));
            uint at(int x, int y) => cells[y * PixelFace.Size + x];

            // Mouth: row 6, two or four cells, mirrored around the middle of the face.
            var mouth = Enumerable.Range(0, PixelFace.Size).Where(x => at(x, 6) == PixelFace.MouthColor).ToList();
            Assert.True(mouth.Count is 2 or 4, $"{name}: mouth of {mouth.Count} cells");
            Assert.Equal(PixelFace.Size - 1, mouth[0] + mouth[^1]);
            Assert.Equal(mouth.Count, mouth[^1] - mouth[0] + 1);
            for (var y = 0; y < PixelFace.Size; y++)
                if (y != 6) Assert.DoesNotContain(PixelFace.MouthColor, Enumerable.Range(0, PixelFace.Size).Select(x => at(x, y)));

            // Eyes: row 4, whites outside, the same iris colour on both sides.
            Assert.Equal(PixelFace.EyeWhite, at(1, 4));
            Assert.Equal(PixelFace.EyeWhite, at(6, 4));
            Assert.Equal(at(2, 4), at(5, 4));
            // The nose shadow sits right above the middle of the mouth.
            Assert.Equal(at(3, 5), at(4, 5));
        }
        Assert.True(distinct.Count >= 300, $"only {distinct.Count} different faces for 600 names");
    }
}
