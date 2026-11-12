using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>Image themes: the "slots" in tokens.json, picking colours from pictures, and the pack "make a theme from pictures" writes.</summary>
public sealed class ImageThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-image-theme-" + Guid.NewGuid().ToString("N"));

    public ImageThemeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Manifest = """{ "format": 2, "id": "pictures", "name": "Pictures" }""";

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private static ThemePack Read(string tokens, params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, bytes) in files.Prepend(("theme.json", Encoding.UTF8.GetBytes(Manifest))).Prepend(("tokens.json", Encoding.UTF8.GetBytes(tokens))))
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        }
        ms.Position = 0;
        return ThemePackReader.Read(ms);
    }

    [Fact]
    public void ReadsTheFourSlotsWithTheirSettings()
    {
        var pack = Read("""
            {
              "slots": {
                "background": { "image": "assets/bg.jpg", "dim": 0.5, "blur": 12, "focus": [0.2, 0.8] },
                "sidebar": { "image": "assets/side.png", "dim": 0.6 },
                "mascot": { "image": "assets/oc.png", "size": 0.5, "side": "left", "opacity": 0.9 },
                "banner": { "image": "assets/banner.png", "focus": [0.5, 0.3] }
              }
            }
            """, ("assets/bg.jpg", [0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 100, 0, 200, 3]), ("assets/side.png", Png(400, 900)),
            ("assets/oc.png", Png(600, 1200)), ("assets/banner.png", Png(1920, 400)));
        Assert.Empty(pack.Problems.Where(p => p.Code != "value_clamped"));
        Assert.Equal(4, pack.Slots.Count);
        var bg = pack.Slots[ThemeSlots.Background];
        Assert.Equal(("assets/bg.jpg", 0.5, 12.0, 0.2, 0.8), (bg.Image, bg.Dim, bg.Blur, bg.FocusX, bg.FocusY));
        Assert.Equal(0.6, pack.Slots[ThemeSlots.Sidebar].Dim);
        Assert.Equal(0, pack.Slots[ThemeSlots.Sidebar].Blur);
        var oc = pack.Slots[ThemeSlots.Mascot];
        Assert.Equal((0.5, "left", 0.9), (oc.Size, oc.Side, oc.Opacity));
        Assert.Equal(0.3, pack.Slots[ThemeSlots.Banner].FocusY);
    }

    [Fact]
    public void NumbersAreClampedSoAPictureCantMakeTextUnreadableOrCoverTheContent()
    {
        var pack = Read("""
            {
              "slots": {
                "background": { "image": "assets/bg.png", "dim": 2, "blur": 500, "focus": [-1, 9] },
                "mascot": { "image": "assets/oc.png", "size": 3, "opacity": 0 }
              }
            }
            """, ("assets/bg.png", Png(100, 100)), ("assets/oc.png", Png(100, 100)));
        var bg = pack.Slots[ThemeSlots.Background];
        Assert.Equal((ThemeSlots.MaxDim, ThemeSlots.MaxBlur, 0.0, 1.0), (bg.Dim, bg.Blur, bg.FocusX, bg.FocusY));
        var oc = pack.Slots[ThemeSlots.Mascot];
        Assert.Equal((ThemeSlots.MaxMascotSize, ThemeSlots.MinMascotOpacity), (oc.Size, oc.Opacity));
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped" && p.Detail.StartsWith("slots.background.dim"));
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped" && p.Detail.StartsWith("slots.mascot.size"));
    }

    [Fact]
    public void BadSlotsAreReportedAndLeftOutWhileTheRestLoads()
    {
        var pack = Read("""
            {
              "colors": { "both": { "Accent": "#3366CC" } },
              "slots": {
                "floor": { "image": "assets/a.png" },
                "background": { "image": "../outside.png" },
                "sidebar": { "image": "assets/missing.png" },
                "banner": { "image": "fonts/a.png" },
                "mascot": { "image": "assets/oc.png", "side": "middle", "size": "big" }
              }
            }
            """, ("assets/a.png", Png(10, 10)), ("assets/oc.png", Png(10, 10)), ("fonts/a.png", Png(10, 10)));
        Assert.Equal(0xFF3366CCu, pack.DarkColors["Accent"]);
        Assert.Equal([ThemeSlots.Mascot], pack.Slots.Keys);
        Assert.Contains(pack.Problems, p => p is { Code: "unknown_key", Detail: "slots.floor" });
        Assert.Contains(pack.Problems, p => p.Code == "unsafe_path");
        Assert.Contains(pack.Problems, p => p.Code == "missing_file");
        Assert.Contains(pack.Problems, p => p.Code == "bad_asset_path");
        Assert.Contains(pack.Problems, p => p is { Code: "bad_value", Detail: "slots.mascot.side" });
        Assert.Contains(pack.Problems, p => p is { Code: "bad_value", Detail: "slots.mascot.size" });
        Assert.Equal("right", pack.Slots[ThemeSlots.Mascot].Side);
        Assert.Equal(ThemeSlots.DefaultMascotSize, pack.Slots[ThemeSlots.Mascot].Size);
    }

    [Fact]
    public void ARedPictureGivesARedAccentAndQuietPages()
    {
        var pixels = new uint[64 * 64];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = i % 4 == 0 ? 0xFFD03030u : 0xFF402020u;
        var palette = ImageTheme.Extract(pixels);
        var (h, s, _) = ImageTheme.ToHsl(palette.Accent);
        Assert.True(h < 15 || h > 345, $"hue {h}");
        Assert.True(s >= 0.45);
        Assert.True(ThemeColor.IsDark(palette.DarkBackground));
        Assert.False(ThemeColor.IsDark(palette.LightBackground));
        Assert.True(ImageTheme.ToHsl(palette.DarkBackground).S <= 0.31);
    }

    [Fact]
    public void SeveralColoursBecomeSwatchesAndAGreyPictureKeepsJaysBlue()
    {
        var pixels = new List<uint>();
        pixels.AddRange(Enumerable.Repeat(0xFF2E7D32u, 600));  // green, most
        pixels.AddRange(Enumerable.Repeat(0xFF1565C0u, 300));  // blue
        pixels.AddRange(Enumerable.Repeat(0xFFF9A825u, 200));  // yellow
        var palette = ImageTheme.Extract(pixels.ToArray());
        Assert.True(palette.Swatches.Count >= 3);
        // Each colour family shows up once; the most vivid one (the blue) leads, not simply the largest area.
        var hues = palette.Swatches.Select(c => ImageTheme.ToHsl(c).H).ToList();
        Assert.Contains(hues, h => h is > 100 and < 150);
        Assert.Contains(hues, h => h is > 195 and < 230);
        Assert.Contains(hues, h => h is > 35 and < 60);
        Assert.InRange(hues[0], 195, 230);

        var grey = ImageTheme.Extract(Enumerable.Repeat(0xFF808080u, 500).ToArray());
        Assert.Equal(ThemeDefaults.Dark["Accent"], grey.Accent);
        Assert.Equal(ThemeDefaults.Dark["Accent"], ImageTheme.Extract([]).Accent);
    }

    [Fact]
    public void ThePackItWritesImportsWithItsSlotsColoursAndAFormat1Background()
    {
        var palette = new ImagePalette(0xFFE0457Bu, ImageTheme.FromHsl(340, 0.3, 0.09), ImageTheme.FromHsl(340, 0.3, 0.95), []);
        var slots = new Dictionary<string, ImageThemeSlot>
        {
            [ThemeSlots.Background] = new(new ThemeSlot { Image = "", Dim = 0.55, Blur = 8, FocusX = 0.3 }, Png(1920, 1080), ".png"),
            [ThemeSlots.Mascot] = new(new ThemeSlot { Image = "", Size = 0.6, Side = "left", Opacity = 0.8 }, Png(600, 1200), ".png"),
        };
        var path = Path.Combine(_dir, "pink.jaytheme");
        var id = ImageTheme.IdFor("Pink Night!");
        Assert.Matches("^pink-night-[0-9a-f]{6}$", id);
        var pack = ImageTheme.Write(path, id, "Pink Night", palette, slots);

        Assert.True(File.Exists(path));
        Assert.Equal(2, pack.Format);
        Assert.Equal("assets/background.png", pack.Slots[ThemeSlots.Background].Image);
        Assert.Equal((0.55, 8.0, 0.3), (pack.Slots[ThemeSlots.Background].Dim, pack.Slots[ThemeSlots.Background].Blur, pack.Slots[ThemeSlots.Background].FocusX));
        Assert.Equal(("left", 0.6, 0.8), (pack.Slots[ThemeSlots.Mascot].Side, pack.Slots[ThemeSlots.Mascot].Size, pack.Slots[ThemeSlots.Mascot].Opacity));
        Assert.Equal(0xFFE0457Bu, pack.DarkColors["Accent"]);
        // The start button keeps Jay's amber: still the one thing to press.
        Assert.False(pack.DarkColors.ContainsKey("Play"));
        // An older Jay that doesn't know "slots" still shows the background.
        Assert.Equal("assets/background.png", pack.BackgroundImage);
        Assert.Equal(0.45, pack.BackgroundOpacity, 3);
        // Readable text on the new pages.
        Assert.True(ThemeColor.Contrast(pack.DarkColors["TextPrimary"], pack.DarkColors["PageBg"]) >= 4.5);
        Assert.True(ThemeColor.Contrast(pack.LightColors["TextPrimary"], pack.LightColors["PageBg"]) >= 4.5);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void APackWithABrokenPictureIsNotWritten()
    {
        var palette = ImageTheme.Extract([]);
        var path = Path.Combine(_dir, "broken.jaytheme");
        var slots = new Dictionary<string, ImageThemeSlot> { [ThemeSlots.Banner] = new(new ThemeSlot { Image = "" }, [1, 2, 3], ".png") };
        var e = Assert.Throws<ThemePackException>(() => ImageTheme.Write(path, "broken-1", "Broken", palette, slots));
        Assert.Equal("bad_image", e.Code);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Equal("reserved_words", Assert.Throws<ThemePackException>(() =>
            ImageTheme.Write(path, "warn-1", SafetyWords.Reserved[0], palette, new Dictionary<string, ImageThemeSlot>())).Code);
    }

    [Fact]
    public void SlotsWriteOnlyWhatEachSlotUses()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            ThemeSlots.Write(w, new Dictionary<string, ThemeSlot>
            {
                [ThemeSlots.Banner] = new() { Image = "assets/banner.png", Dim = 0.7, Blur = 5, FocusY = 0.25 },
                [ThemeSlots.Mascot] = new() { Image = "assets/mascot.png" },
            });
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        var slots = doc.RootElement.GetProperty("slots");
        var banner = slots.GetProperty("banner");
        Assert.False(banner.TryGetProperty("dim", out _));
        Assert.False(banner.TryGetProperty("blur", out _));
        Assert.Equal(0.25, banner.GetProperty("focus")[1].GetDouble());
        var mascot = slots.GetProperty("mascot");
        Assert.False(mascot.TryGetProperty("focus", out _));
        Assert.Equal("right", mascot.GetProperty("side").GetString());
        // Written in the panel's order, whatever order they came in.
        Assert.Equal(["mascot", "banner"], slots.EnumerateObject().Select(p => p.Name));
    }
}
