using System.Text;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>The themes that ship with Jay, and theme-migrate (official §10.9).</summary>
public class BuiltInThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-builtin-" + Guid.NewGuid().ToString("N"));

    public BuiltInThemeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void BuiltInThemesReadCleanAsFormatTwo()
    {
        foreach (var id in BuiltInThemes.Ids)
        {
            var pack = BuiltInThemes.Load(id);
            Assert.NotNull(pack);
            Assert.Equal(id, pack!.Id);
            Assert.Equal(2, pack.Format);
            Assert.Empty(pack.Migration);
            Assert.True(pack.Problems.Count == 0, $"{id}: {string.Join("; ", pack.Problems)}");
        }
        Assert.Null(BuiltInThemes.Load("not-a-builtin"));
    }

    [Fact]
    public void ClassicAmberIsWarmAndPutsAmberOnTheStartButton()
    {
        var pack = BuiltInThemes.Load(BuiltInThemes.ClassicAmber)!;
        Assert.Equal(0xFF1C1A18u, pack.DarkColors["PageBg"]);
        Assert.Equal(pack.DarkColors["Accent"], pack.DarkColors["Play"]);
    }

    [Fact]
    public void HighContrastBodyTextIsAtLeastSevenToOne()
    {
        var pack = BuiltInThemes.Load(BuiltInThemes.HighContrast)!;
        foreach (var colors in new[] { pack.DarkColors, pack.LightColors })
            foreach (var text in new[] { "TextPrimary", "TextSecondary", "TextTertiary" })
                foreach (var bg in new[] { "PageBg", "Surface", "SurfaceRaised", "Overlay" })
                    Assert.True(ThemeColor.Contrast(colors[text], colors[bg]) >= 7, $"{text} on {bg}: {ThemeColor.Contrast(colors[text], colors[bg]):F2}");
        Assert.Equal(2, pack.Styles["card"][""].BorderWidth);
    }

    [Fact]
    public void StoreListsBuiltInsAndProtectsThem()
    {
        var store = new ThemeStore(Path.Combine(_dir, "themes"));
        var list = store.ListAll();
        Assert.Equal(BuiltInThemes.Ids, list.Take(2).Select(p => p.Id));
        Assert.NotNull(store.TryLoad(BuiltInThemes.HighContrast));
        Assert.False(store.Remove(BuiltInThemes.HighContrast));

        // An import can't take a built-in's id.
        var fake = Path.Combine(_dir, "fake.jaytheme");
        using (var zip = System.IO.Compression.ZipFile.Open(fake, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("theme.json").Open())
            s.Write(Encoding.UTF8.GetBytes($$"""{ "format": 2, "id": "{{BuiltInThemes.ClassicAmber}}", "name": "fake" }"""));
        Assert.Equal("builtin_id", Assert.Throws<ThemePackException>(() => store.Install(fake)).Code);
    }

    [Fact]
    public void MigrateWritesAFormatTwoPackThatReadsTheSame()
    {
        var src = Path.Combine(_dir, "old");
        Directory.CreateDirectory(Path.Combine(src, "layout"));
        Directory.CreateDirectory(Path.Combine(src, "fonts"));
        File.WriteAllText(Path.Combine(src, "theme.json"), """{ "format": 1, "id": "old-look", "name": "Old look", "author": "someone" }""");
        File.WriteAllText(Path.Combine(src, "tokens.json"), """
            { "colors": { "both": { "Accent": "#2E7D32", "OnAccent": "#FFFFFF" }, "light": { "PageBg": "#F0F0F0" } },
              "sizes": { "FontDisplay": 26 }, "radius": { "RadiusLg": 10 } }
            """);
        File.WriteAllText(Path.Combine(src, "styles.json"), """
            { "pill": { "radius": 3, "background": "token:AccentTint" }, "callout": { "borderWidth": 2 },
              "button.primary": { "background": "linear(90, #2E7D32, #1B5E20)" } }
            """);
        File.WriteAllText(Path.Combine(src, "layout", "shell.json"), """{ "navWidth": 60, "hidden": ["downloads", "content"] }""");
        File.WriteAllText(Path.Combine(src, "notes.txt"), "kept as is");
        var before = ThemePackReader.ReadDirectory(src);
        Assert.True(before.Upgraded);

        var output = Path.Combine(_dir, "new.jaytheme");
        var after = ThemeMigrator.Migrate(src, output);
        Assert.True(File.Exists(output));
        Assert.Equal(2, after.Format);
        Assert.Empty(after.Migration);
        Assert.Equal("someone", after.Author);
        Assert.Equal(before.DarkColors["Play"], after.DarkColors["Play"]);
        Assert.Equal(before.LightColors["PageBg"], after.LightColors["PageBg"]);
        Assert.Equal(26, after.Numbers["FontHeadline"]);
        Assert.Equal(14, after.Radii["RadiusXl"]);
        Assert.Equal(3, after.Styles["chip"][""].Radius);
        Assert.Equal(3, after.Styles["tag"][""].Radius);
        Assert.Equal(2, after.Styles["banner"][""].BorderWidth);
        Assert.Equal(PaintKind.Linear, after.Styles["button.primary"][""].Background!.Kind);
        Assert.Equal("rail", after.Shell!.NavStyle);
        Assert.Contains("discover", after.Shell.Hidden);
        // A second migrate is a no-op in meaning.
        var again = ThemeMigrator.Migrate(output, Path.Combine(_dir, "again.jaytheme"));
        Assert.Equal(after.DarkColors, again.DarkColors);
    }

    [Fact]
    public void MigrateKeepsOtherFilesAndNeverTouchesTheSource()
    {
        var src = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "theme.json"), """{ "id": "plain-look", "name": "Plain" }""");
        File.WriteAllText(Path.Combine(src, "README.md"), "hello");
        var manifestBefore = File.ReadAllText(Path.Combine(src, "theme.json"));
        var output = Path.Combine(_dir, "plain.jaytheme");
        ThemeMigrator.Migrate(src, output);
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(src, "theme.json")));
        using var zip = System.IO.Compression.ZipFile.OpenRead(output);
        Assert.Contains(zip.Entries, e => e.FullName == "README.md");
        Assert.Contains(zip.Entries, e => e.FullName == "tokens.json");
    }
}
