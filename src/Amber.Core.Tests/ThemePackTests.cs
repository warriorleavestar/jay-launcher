using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>界面主题包：读取、校验、对比度修正、安装、启动保护。主题包会随联机配置发给陌生人，这里的拒绝规则都要有测试。</summary>
public class ThemePackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-theme-" + Guid.NewGuid().ToString("N"));

    public ThemePackTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Manifest = """{ "format": 1, "id": "test-theme", "name": "测试主题", "author": "Jay", "version": "1.2.0" }""";

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    private static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    private static ThemePack Read(params (string, byte[])[] entries) => ThemePackReader.Read(new MemoryStream(Zip(entries)));

    private static string Code(Action action) => Assert.Throws<ThemePackException>(action).Code;

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    [Fact]
    public void ReadsColorsSizesFontsAndBackground()
    {
        var pack = Read(
            Text("theme.json", Manifest),
            Text("tokens.json", """
            {
              // 注释和结尾逗号都允许，手写方便
              "colors": { "dark": { "Accent": "#3B82F6", "PageBg": "#101418", }, "both": { "Danger": "#CC2222" } },
              "sizes": { "FontBody": 15, "FontCaption": 4 },
              "radius": { "RadiusMd": 0 },
              "fonts": { "ui": { "default": "Inter", "zh-Hans": "fonts/cn.ttf" } },
              "background": { "image": "assets/bg.png", "opacity": 0.4 }
            }
            """),
            ("fonts/cn.ttf", [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            ("assets/bg.png", Png(1920, 1080)));
        Assert.Equal("test-theme", pack.Id);
        Assert.Equal("测试主题", pack.Name);
        Assert.Equal(0xFF3B82F6u, pack.DarkColors["Accent"]);
        Assert.Equal(0xFFCC2222u, pack.LightColors["Danger"]);
        Assert.False(pack.LightColors.ContainsKey("Accent"));
        Assert.Equal(15, pack.Numbers["FontBody"]);
        Assert.Equal(11, pack.Numbers["FontCaption"]); // 太小的字号抬到 11
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped");
        Assert.Equal(0, pack.Radii["RadiusMd"]);
        Assert.Equal("Inter", pack.UiFonts["default"]);
        Assert.Equal("fonts/cn.ttf", pack.UiFonts["zh-Hans"]);
        Assert.Equal("assets/bg.png", pack.BackgroundImage);
        Assert.Equal(0.4, pack.BackgroundOpacity);
    }

    [Fact]
    public void ManifestOnlyPackIsValid()
    {
        var pack = Read(Text("theme.json", Manifest));
        Assert.Empty(pack.DarkColors);
        Assert.Empty(pack.Problems);
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("/etc/passwd.json")]
    [InlineData("C:/x.json")]
    [InlineData("a/../../b.json")]
    [InlineData("a//b.json")]
    public void RejectsUnsafePaths(string name) =>
        Assert.Equal("unsafe_path", Code(() => Read(Text("theme.json", Manifest), Text(name, "{}"))));

    [Theory]
    [InlineData("payload.dll")]
    [InlineData("styles.axaml")]
    [InlineData("run.sh")]
    [InlineData("assets/x.svg")]
    public void RejectsCodeAndUnknownFileTypes(string name) =>
        Assert.Equal("file_type_not_allowed", Code(() => Read(Text("theme.json", Manifest), Text(name, "x"))));

    [Fact]
    public void RejectsTooManyFiles()
    {
        var entries = Enumerable.Range(0, ThemePackReader.MaxEntries + 1).Select(i => Text($"f{i}.txt", "x")).Prepend(Text("theme.json", Manifest)).ToArray();
        Assert.Equal("too_many_files", Code(() => Read(entries)));
    }

    [Fact]
    public void RejectsOversizedEntryEvenIfCompressedWell()
    {
        var big = new byte[ThemePackReader.MaxEntryBytes + 1]; // 全是 0，压缩后很小（压缩炸弹的样子）
        Assert.Equal("file_too_large", Code(() => Read(Text("theme.json", Manifest), ("big.txt", big))));
    }

    [Theory]
    [InlineData("""{ "name": "x" }""", "bad_manifest")]
    [InlineData("""{ "id": "../x" }""", "bad_id")]
    [InlineData("""{ "id": "UPPER" }""", "bad_id")]
    [InlineData("""not json""", "bad_json")]
    public void RejectsBadManifest(string manifest, string code) =>
        Assert.Equal(code, Code(() => Read(Text("theme.json", manifest))));

    [Fact]
    public void RejectsMissingManifestAndNonZip()
    {
        Assert.Equal("missing_manifest", Code(() => Read(Text("tokens.json", "{}"))));
        Assert.Equal("not_zip", Code(() => ThemePackReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("hello")))));
    }

    [Fact]
    public void BadPartsBecomeProblemsNotFailures()
    {
        var pack = Read(
            Text("theme.json", Manifest),
            Text("tokens.json", """
            {
              "colors": { "dark": { "Accent": "red", "NotAToken": "#FFFFFF", "Surface": "#202020" } },
              "fonts": { "ui": { "default": "https://evil.example/font.ttf", "ja": "fonts/missing.ttf", "zh-Hant": "fonts/fake.ttf" } },
              "background": { "image": "../../secret.png" }
            }
            """),
            ("fonts/fake.ttf", Encoding.UTF8.GetBytes("MZ this is not a font")),
            Text("layout/shell.json", "{}"));
        Assert.Equal(0xFF202020u, pack.DarkColors["Surface"]);
        Assert.False(pack.DarkColors.ContainsKey("Accent"));
        Assert.Empty(pack.UiFonts);
        Assert.Null(pack.BackgroundImage);
        var codes = pack.Problems.Select(p => p.Code).ToList();
        Assert.Contains("bad_color", codes);
        Assert.Contains("unknown_key", codes);
        Assert.Contains("bad_font", codes);
        Assert.Contains("missing_file", codes);
        Assert.Contains("bad_font_file", codes);
        Assert.Contains("unsafe_path", codes);
        Assert.Contains("layer_not_supported_yet", codes);
    }

    [Fact]
    public void RejectsHugeImages()
    {
        var pack = Read(
            Text("theme.json", Manifest),
            Text("tokens.json", """{ "background": { "image": "assets/huge.png" } }"""),
            ("assets/huge.png", Png(20000, 100)));
        Assert.Null(pack.BackgroundImage);
        Assert.Contains(pack.Problems, p => p.Code == "image_too_large");
    }

    [Fact]
    public void UnreadableTextIsFixedToMeetContrast()
    {
        // 文字和背景同色：读的时候自动把文字推到看得清（≥ 4.5:1）。
        var pack = Read(Text("theme.json", Manifest), Text("tokens.json", """
            { "colors": { "dark": { "PageBg": "#808080", "Surface": "#808080", "TextPrimary": "#808080" } } }
            """));
        var text = pack.DarkColors["TextPrimary"];
        Assert.True(ThemeColor.Contrast(text, 0xFF808080) >= 4.5);
        Assert.Contains(pack.Problems, p => p.Code == "contrast_fixed" && p.Detail.Contains("TextPrimary"));
        // 只改了背景的主题：没写的文字色（按默认值算看不清时）也会补上修正过的值。
        Assert.True(pack.DarkColors.ContainsKey("TextSecondary"));
        Assert.True(ThemeColor.Contrast(pack.DarkColors["TextSecondary"], 0xFF808080) >= 4.5);
    }

    [Fact]
    public void DefaultColorsAlreadyMeetContrast()
    {
        foreach (var defaults in new[] { ThemeDefaults.Dark, ThemeDefaults.Light })
            foreach (var (fg, bg, min) in ThemeContrast.Pairs)
                Assert.True(ThemeColor.Contrast(defaults[fg], defaults[bg]) >= min - 0.05, $"{fg} on {bg}: {ThemeColor.Contrast(defaults[fg], defaults[bg]):F2}");
    }

    [Fact]
    public void SeedDerivesReadablePalette()
    {
        var pack = Read(Text("theme.json", Manifest), Text("tokens.json", """{ "seed": { "accent": "#7C3AED", "background": "#0B1020" } }"""));
        Assert.Equal(0xFF7C3AEDu, pack.DarkColors["Accent"]);
        Assert.Equal(0xFF7C3AEDu, pack.LightColors["Accent"]);
        Assert.Equal(0xFF0B1020u, pack.DarkColors["PageBg"]);
        Assert.False(pack.LightColors.ContainsKey("PageBg")); // 深色背景不套到浅色主题上
        foreach (var (fg, bg, min) in ThemeContrast.Pairs)
        {
            var colors = pack.DarkColors;
            var f = colors.GetValueOrDefault(fg, ThemeDefaults.Dark[fg]);
            var b = colors.GetValueOrDefault(bg, ThemeDefaults.Dark[bg]);
            Assert.True(ThemeColor.Contrast(f, b) >= min, $"{fg} on {bg}");
        }
    }

    [Fact]
    public void SingleVariantThemeCopiesToBoth()
    {
        var pack = Read(Text("theme.json", Manifest), Text("tokens.json", """{ "variants": "dark", "colors": { "dark": { "Accent": "#00FF88" } } }"""));
        Assert.Equal("dark", pack.Variants);
        Assert.Equal(pack.DarkColors["Accent"], pack.LightColors["Accent"]);
    }

    [Fact]
    public void DefaultsMatchTokensAxaml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.App", "Themes", "Tokens.axaml");
        var doc = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (variant, defaults) in new[] { ("Dark", ThemeDefaults.Dark), ("Light", ThemeDefaults.Light) })
        {
            var dict = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant);
            var brushes = dict.Elements().Where(e => e.Name.LocalName == "SolidColorBrush")
                .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string)e.Attribute("Color")!);
            Assert.Equal(ThemePackReader.ColorKeys.Order(), brushes.Keys.Order());
            foreach (var (key, hex) in brushes)
            {
                Assert.True(ThemePackReader.TryColor(hex, out var c));
                Assert.True(defaults[key] == c, $"{variant}.{key}: Tokens.axaml {hex}, ThemeDefaults {ThemePackReader.FormatColor(defaults[key])}");
            }
        }
        foreach (var e in doc.Root!.Elements().Where(e => e.Name.LocalName == "Double"))
        {
            var key = (string)e.Attribute(x + "Key")!;
            if (ThemeDefaults.Numbers.TryGetValue(key, out var v)) Assert.Equal(v, double.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void StoreInstallsListsReplacesAndRemoves()
    {
        var store = new ThemeStore(Path.Combine(_dir, "themes"));
        var file = Path.Combine(_dir, "a.jaytheme");
        File.WriteAllBytes(file, Zip(Text("theme.json", Manifest), Text("tokens.json", """{ "colors": { "both": { "Accent": "#112233" } } }"""), ("assets/bg.png", Png(10, 10))));
        var installed = store.Install(file);
        Assert.True(File.Exists(Path.Combine(store.Root, "test-theme", "assets", "bg.png")));
        Assert.Equal(installed.Id, Assert.Single(store.List()).Id);

        File.WriteAllBytes(file, Zip(Text("theme.json", Manifest.Replace("测试主题", "新名字")), Text("tokens.json", "{}")));
        store.Install(file);
        var again = Assert.Single(store.List());
        Assert.Equal("新名字", again.Name);
        Assert.False(File.Exists(Path.Combine(store.Root, "test-theme", "assets", "bg.png"))); // 旧版本的文件不留

        Assert.True(store.Remove("test-theme"));
        Assert.Empty(store.List());
        Assert.False(store.Remove("../themes"));
        Assert.Empty(Directory.GetDirectories(store.Root)); // 没有残留的临时目录
    }

    [Fact]
    public void StoreRejectsBadPackWithoutLeavingFiles()
    {
        var store = new ThemeStore(Path.Combine(_dir, "themes"));
        var file = Path.Combine(_dir, "bad.jaytheme");
        File.WriteAllBytes(file, Zip(Text("theme.json", Manifest), Text("evil.exe", "MZ")));
        Assert.Throws<ThemePackException>(() => store.Install(file));
        Assert.False(Directory.Exists(Path.Combine(store.Root, "test-theme")));
    }

    [Fact]
    public void StartupGuardFallsBackAfterTwoCrashes()
    {
        var guard = new ThemeStartupGuard(Path.Combine(_dir, "guard.txt"));
        Assert.True(guard.Begin("t"));   // 第一次启动，崩了（没调 Succeeded）
        Assert.True(guard.Begin("t"));   // 第二次，又崩了
        Assert.False(guard.Begin("t"));  // 第三次：用默认主题
        Assert.True(guard.Begin("other")); // 换了别的主题重新计数
        guard.Succeeded();
        Assert.True(guard.Begin("t"));
        guard.Succeeded();
        Assert.True(guard.Begin("t"));   // 成功过就清零
    }

    [Fact]
    public void WriterRoundTrips()
    {
        var path = Path.Combine(_dir, "out.jaytheme");
        ThemePackWriter.Write(path, "my-theme-copy", "我的主题", ThemeDefaults.Dark, ThemeDefaults.Light, ThemeDefaults.Numbers, ThemeDefaults.Radii);
        var pack = ThemePackReader.Read(path);
        Assert.Equal("my-theme-copy", pack.Id);
        Assert.Equal("我的主题", pack.Name);
        Assert.Equal(ThemeDefaults.Dark.Count, pack.DarkColors.Count);
        Assert.All(ThemeDefaults.Light, p => Assert.Equal(p.Value, pack.LightColors[p.Key]));
        Assert.Equal(14, pack.Numbers["FontBody"]);
        Assert.Empty(pack.Problems);
    }

    [Fact]
    public void ReadsComponentStyles()
    {
        var pack = Read(Text("theme.json", Manifest), Text("styles.json", """
            {
              "button.primary": { "background": "linear(90, #102030, #203040)", "foreground": "#FFFFFF", "radius": 0, "padding": [18, 6],
                                  "shadow": "3 3 0 0 #000000", ":hover": { "background": "token:SidebarBg" } },
              "card": { "background": "image:assets/paper.png", "borderWidth": 2, "shadow": "0 4 12 0 #33000000, inset 0 1 0 0 #22FFFFFF" },
              "nav.item": { ":selected": { "background": "#334455", "foreground": "#FFFFFF" } }
            }
            """), ("assets/paper.png", Png(64, 64)));
        var primary = pack.Styles["button.primary"];
        Assert.Equal(PaintKind.Linear, primary[""].Background!.Kind);
        Assert.Equal(90, primary[""].Background!.Angle);
        Assert.Equal((18.0, 6.0), primary[""].Padding);
        Assert.Equal("3 3 0 0 #000000", primary[""].Shadow);
        Assert.Equal("SidebarBg", primary[":hover"].Background!.Token);
        Assert.Equal("assets/paper.png", pack.Styles["card"][""].Background!.ImagePath);
        Assert.Equal(PaintKind.Color, pack.Styles["nav.item"][":selected"].Background!.Kind);
        Assert.True(pack.Problems.Count == 0, string.Join("; ", pack.Problems));
    }

    [Fact]
    public void ComponentStyleRulesAreWhitelisted()
    {
        var pack = Read(Text("theme.json", Manifest), Text("styles.json", """
            {
              "remote.consent": { "background": "#00FF00" },
              "confirm.dialog": { "background": "#00FF00" },
              "card": { "foreground": "#FFFFFF", ":hover": { "background": "#000000" }, "background": "image:../../etc/x.png", "radius": 999 },
              "button": { "background": "url(https://evil.example/x.png)", "shadow": "0 0 500 400 #FF000000", "fontWeight": "900" },
              "heading": { "foreground": "token:NotAToken" },
              "pill": { "background": "image:assets/x.png" }
            }
            """));
        var codes = pack.Problems.Select(p => p.Code).ToList();
        Assert.Equal(2, codes.Count(c => c == "unknown_component")); // 安全界面不是组件，改不了
        Assert.Contains("unknown_key", codes);   // card 没有 foreground
        Assert.Contains("unknown_state", codes); // card 没有 :hover
        Assert.Contains("bad_shadow", codes);
        Assert.Contains("value_clamped", codes);
        Assert.Equal(4, codes.Count(c => c == "bad_paint")); // ../ 路径、网址、不存在的 token、只有背景能用图片但文件不在
        Assert.False(pack.Styles.ContainsKey("button") && pack.Styles["button"][""].Background is not null);
        Assert.Equal(64, pack.Styles["card"][""].Radius);
    }

    [Fact]
    public void ComponentTextIsFixedToMeetContrast()
    {
        var pack = Read(Text("theme.json", Manifest), Text("styles.json", """
            { "button.primary": { "background": "linear(0, #E0538A, #F08AB0)", "foreground": "#FFFFFF" },
              "nav.item": { ":selected": { "background": "token:Accent", "foreground": "token:Accent" } } }
            """));
        var fg = pack.Styles["button.primary"][""].Foreground!;
        Assert.True(ThemeColor.Contrast(fg.Colors[0], 0xFFE0538A) >= 4.5 && ThemeColor.Contrast(fg.Colors[0], 0xFFF08AB0) >= 4.5);
        var nav = pack.Styles["nav.item"][":selected"].Foreground!;
        Assert.Equal(PaintKind.Color, nav.Kind);
        Assert.True(ThemeColor.Contrast(nav.Colors[0], ThemeDefaults.Dark["Accent"]) >= 4.5);
        Assert.True(ThemeColor.Contrast(nav.Colors[0], ThemeDefaults.Light["Accent"]) >= 4.5);
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("0 2 6 0 #66000000", "0 2 6 0 #66000000")]
    [InlineData("inset 1 1 0 0 #FFFFFF", "inset 1 1 0 0 #FFFFFF")]
    [InlineData("0 2 6 #66000000", null)]
    [InlineData("0 2 -6 0 #000000", null)]
    [InlineData("1 1 1 1 #000000, 2 2 2 2 #000000, 3 3 3 3 #000000", null)]
    public void ParsesShadows(string text, string? expected) => Assert.Equal(expected, ThemeStyles.ParseShadow(text));

    [Fact]
    public void ReadsJpegSize()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x01, 0x00, 0x02, 0x00, 3, 0, 0, 0, 0];
        Assert.Equal((512, 256), ThemeAssets.ImageSize(jpeg));
        Assert.Null(ThemeAssets.ImageSize("GIF89a....."u8));
    }
}
