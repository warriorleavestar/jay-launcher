using System.IO.Compression;
using System.Text;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>主题包的排版和层级（layout/shell.json、layout/pages/*.json）：只认白名单里的容器和区块，必需的区块不会丢。</summary>
public class ThemeLayoutTests
{
    private static ThemePack Read(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, text) in new[] { ("theme.json", """{ "id": "layout-test", "name": "排版" }"""), ("tokens.json", "{}") }.Concat(files))
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        ms.Position = 0;
        return ThemePackReader.Read(ms);
    }

    private static IEnumerable<string> RegionsIn(LayoutNode n) =>
        (n.Region is { } r ? [r] : Enumerable.Empty<string>()).Concat(n.Children.SelectMany(RegionsIn));

    [Fact]
    public void ReadsShell()
    {
        var pack = Read(("layout/shell.json", """
            { "nav": "top", "navStyle": "icons", "navWidth": 9999, "showBrand": false, "contentMaxWidth": 1200,
              "pages": ["settings", "home", "nope", "home"], "hidden": ["log", "settings"] }
            """));
        var shell = pack.Shell!;
        Assert.Equal("top", shell.Nav);
        Assert.Equal("icons", shell.NavStyle);
        Assert.Equal(360, shell.NavWidth); // 夹到范围里
        Assert.False(shell.ShowBrand);
        Assert.Equal(1200, shell.ContentMaxWidth);
        Assert.Equal(["settings", "home"], shell.PageOrder.Take(2));
        Assert.Equal(ThemeLayout.PageIds.Length, shell.PageOrder.Count); // 没写的页接在后面
        Assert.Equal(["log"], shell.Hidden); // 设置页藏不掉
        Assert.Contains(pack.Problems, p => p.Code == "required_page");
    }

    [Fact]
    public void BadShellValuesAreIgnoredNotFatal()
    {
        var pack = Read(("layout/shell.json", """{ "nav": "diagonal", "navStyle": 3, "showBrand": "yes" }"""));
        Assert.Equal("left", pack.Shell!.Nav);
        Assert.Equal("full", pack.Shell.NavStyle);
        Assert.True(pack.Shell.ShowBrand);
        Assert.True(pack.Problems.Count(p => p.Code == "bad_value") >= 3);
    }

    [Fact]
    public void ReadsPageTreesPerBreakpoint()
    {
        var pack = Read(("layout/pages/home.json", """
            {
              "wide": { "type": "grid", "columns": ["*", "320"], "rows": ["auto", "*"], "spacing": 16, "children": [
                  { "type": "region", "name": "home.title", "columnSpan": 2 },
                  { "type": "scroll", "row": 1, "children": ["home.pending", "home.onboarding", "home.instance", "home.crash"] },
                  { "type": "region", "name": "home.launch", "row": 1, "column": 1, "valign": "start" } ] },
              "compact": { "type": "stack", "spacing": 8, "children": ["home.launch", "home.instance"] }
            }
            """));
        var home = pack.Pages["home"];
        var wide = ThemeLayout.TreeFor(home, "wide")!;
        Assert.Equal("grid", wide.Type);
        Assert.Equal(["*", "320"], wide.Columns);
        Assert.Equal(1, wide.Children[2].Column);
        Assert.Equal(6, RegionsIn(wide).Count());
        // medium 没写：先用 default，再用 wide。
        Assert.Same(wide, ThemeLayout.TreeFor(home, "medium"));
        // compact 少了几个必需区块：自动补在后面，并记一条问题。
        var compact = ThemeLayout.TreeFor(home, "compact")!;
        var regions = RegionsIn(compact).ToList();
        Assert.Equal("home.launch", regions[0]);
        foreach (var required in ThemeLayout.Regions["home"].Required) Assert.Contains(required, regions);
        Assert.Contains(pack.Problems, p => p.Code == "required_region_added");
    }

    [Fact]
    public void SingleTreeIsTheDefault()
    {
        var pack = Read(("layout/pages/home.json", """{ "type": "stack", "children": ["home.launch", "home.pending", "home.onboarding", "home.instance", "home.crash"] }"""));
        var tree = ThemeLayout.TreeFor(pack.Pages["home"], "compact")!;
        Assert.Equal("home.launch", RegionsIn(tree).First());
        Assert.DoesNotContain(pack.Problems, p => p.Code == "required_region_added");
    }

    [Fact]
    public void UnknownAndDuplicateRegionsAreDropped()
    {
        var pack = Read(("layout/pages/home.json", """
            { "type": "stack", "children": ["home.launch", "home.launch", "settings.secret", "home.pending", "home.onboarding", "home.instance", "home.crash",
              { "type": "xaml", "children": [] } ] }
            """));
        var regions = RegionsIn(ThemeLayout.TreeFor(pack.Pages["home"], "wide")!).ToList();
        Assert.Single(regions, r => r == "home.launch");
        Assert.DoesNotContain("settings.secret", regions);
        Assert.Contains(pack.Problems, p => p.Code == "unknown_region");
        Assert.Contains(pack.Problems, p => p.Code == "duplicate_region");
        Assert.Contains(pack.Problems, p => p.Code == "unknown_node");
    }

    [Fact]
    public void TooDeepTreeIsRejected()
    {
        var json = new StringBuilder();
        for (var i = 0; i < 14; i++) json.Append("""{ "type": "stack", "children": [""");
        json.Append("\"home.launch\"");
        for (var i = 0; i < 14; i++) json.Append("]}");
        var pack = Read(("layout/pages/home.json", json.ToString()));
        Assert.Contains(pack.Problems, p => p.Code == "layout_too_deep");
    }

    [Fact]
    public void SettingsPageLayoutIsLocked()
    {
        var pack = Read(("layout/pages/settings.json", """{ "type": "stack", "children": [] }"""));
        Assert.False(pack.Pages.ContainsKey("settings"));
        Assert.Contains(pack.Problems, p => p.Code == "page_locked");
    }

    [Fact]
    public void LongTextIsCutAndImagesMustExist()
    {
        var pack = Read(("layout/pages/home.json", $$"""
            { "type": "stack", "children": [ { "type": "text", "text": "{{new string('字', 500)}}", "style": "h2" },
              { "type": "image", "source": "image:assets/missing.png" },
              "home.launch", "home.pending", "home.onboarding", "home.instance", "home.crash" ] }
            """));
        var tree = ThemeLayout.TreeFor(pack.Pages["home"], "wide")!;
        var text = tree.Children.First(c => c.Type == "text");
        Assert.True(text.Text!.Length <= 200);
        Assert.DoesNotContain(tree.Children, c => c.Type == "image");
    }

    [Fact]
    public void ExampleThemesReadWithoutProblems()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "themes", "examples"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        foreach (var example in Directory.GetDirectories(Path.Combine(dir!, "themes", "examples")))
        {
            var pack = ThemePackReader.ReadDirectory(example);
            Assert.True(pack.Problems.Count == 0, $"{Path.GetFileName(example)}: {string.Join("; ", pack.Problems)}");
        }
        var topbar = ThemePackReader.ReadDirectory(Path.Combine(dir!, "themes", "examples", "topbar"));
        Assert.Equal("top", topbar.Shell!.Nav);
        Assert.Equal("dock", ThemeLayout.TreeFor(topbar.Pages["home"], "compact")!.Type);
        Assert.Equal("grid", ThemeLayout.TreeFor(topbar.Pages["home"], "wide")!.Type);
    }

    [Fact]
    public void OtherPagesGetTheirRequiredRegionsBack()
    {
        var pack = Read(("layout/pages/tools.json", """{ "type": "dock", "children": [ { "type": "region", "name": "tools.status", "dock": "top" }, "home.launch" ] }"""));
        var regions = RegionsIn(ThemeLayout.TreeFor(pack.Pages["tools"], "wide")!).ToList();
        Assert.Equal(["tools.status", "tools.body"], regions); // 别的页的区块不能拿过来；body 自动补回
        Assert.Contains(pack.Problems, p => p.Code == "unknown_region");
    }

    [Fact]
    public void PackDirectorySkipsHiddenFilesAndRejectsBadPacks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-themedir-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, "theme.json"), """{ "id": "packed", "name": "打包" }""");
            File.WriteAllText(Path.Combine(dir, "tokens.json"), "{}");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "x");
            File.WriteAllText(Path.Combine(dir, ".DS_Store"), "x");
            var output = Path.Combine(dir, "..", Path.GetFileName(dir) + ".jaytheme");
            var pack = ThemePackWriter.PackDirectory(dir, output);
            Assert.Equal("packed", pack.Id);
            using (var zip = ZipFile.OpenRead(output))
                Assert.Equal(["theme.json", "tokens.json"], zip.Entries.Select(e => e.FullName).Order());
            File.Delete(output);

            File.WriteAllText(Path.Combine(dir, "run.exe"), "MZ");
            Assert.Throws<ThemePackException>(() => ThemePackWriter.PackDirectory(dir, output));
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".tmp"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
