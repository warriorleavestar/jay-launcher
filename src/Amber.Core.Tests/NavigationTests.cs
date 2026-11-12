using Amber.Core.Layout;
using Amber.Core.Navigation;
using Amber.Core.Themes;
using System.IO.Compression;
using System.Text;

namespace Amber.Core.Tests;

public class NavigationTests
{
    [Theory]
    [InlineData("library", Section.Library)]
    [InlineData("library/abc/mods", Section.Library)]
    [InlineData("discover/mod?target=abc", Section.Discover)]
    [InlineData("play/servers", Section.Play)]
    [InlineData("settings/general#motion", Section.Settings)]
    [InlineData("me", Section.Me)]
    [InlineData("accounts", Section.Me)]
    [InlineData("people", Section.People)]
    [InlineData("help/first-run", Section.Help)]
    [InlineData("", Section.Library)]
    [InlineData("nonsense/x", Section.Library)]
    public void ParsesSections(string text, Section section) => Assert.Equal(section, Route.Parse(text).Section);

    [Fact]
    public void RoundTripsQueryFragmentAndEscapes()
    {
        var r = Route.Parse("jay:discover/mod?target=%E7%94%9F%E5%AD%98%20%E6%9C%8D&q=a%26b#field");
        Assert.Equal(["discover", "mod"], r.Segments);
        Assert.Equal("生存 服", r.Query["target"]);
        Assert.Equal("a&b", r.Query["q"]);
        Assert.Equal("field", r.Fragment);
        Assert.Equal(r, Route.Parse(r.ToString()));
        Assert.Equal("discover/mod?q=a%26b&target=%E7%94%9F%E5%AD%98%20%E6%9C%8D", r.WithoutFragment.ToString());
        Assert.Equal(Route.Parse("library/x"), Route.Parse("/library//x/"));
        Assert.Equal("library/mods", Route.Parse("mods").ToString()); // 认不出的入口挂在游戏库下
        Assert.Equal("library/%E5%AE%9E%E4%BE%8B%2F1", Route.Of(Section.Library, "实例/1").ToString());
    }

    [Fact]
    public void EachSectionKeepsItsOwnHistory()
    {
        var router = new Router();
        var log = new List<(string, NavigationKind)>();
        router.Navigated += (r, k) => log.Add((r.ToString(), k));
        router.Navigate("library/a/mods");
        router.Navigate("library/a/worlds");
        router.Navigate("discover/mod");
        Assert.Equal(Section.Discover, router.CurrentSection);
        Assert.True(router.CanGoBack); // 发现首页 → 模组
        router.SwitchTo(Section.Library);
        Assert.Equal("library/a/worlds", router.Current.ToString()); // 回到离开时的位置
        Assert.True(router.Back());
        Assert.Equal("library/a/mods", router.Current.ToString());
        Assert.True(router.CanGoForward);
        router.SwitchTo(Section.Discover);
        Assert.Equal("discover/mod", router.Current.ToString());
        router.SwitchTo(Section.Discover); // 再点一次当前入口：回入口首页
        Assert.Equal("discover", router.Current.ToString());
        router.SwitchTo(Section.Library);
        Assert.True(router.Forward());
        Assert.Equal("library/a/worlds", router.Current.ToString());
        Assert.False(router.CanGoForward);
        router.SwitchTo(Section.Play); // 没去过：入口首页，没有可以返回的
        Assert.Equal("play", router.Current.ToString());
        Assert.False(router.CanGoBack);
        Assert.Contains(("library/a/mods", NavigationKind.Back), log);
        Assert.Contains(("discover/mod", NavigationKind.Section), log);
    }

    [Fact]
    public void NavigatingDropsForwardHistoryAndSameRouteIsNotDuplicated()
    {
        var router = new Router();
        router.Navigate("library/a");
        router.Navigate("library/b");
        router.Back();
        router.Navigate("library/c");
        Assert.False(router.CanGoForward);
        router.Navigate("library/c#x"); // 同一处只换字段，不加历史
        Assert.Equal("x", router.Current.Fragment);
        router.Back();
        Assert.Equal("library/a", router.Current.ToString());
        Assert.True(router.Back());
        Assert.False(router.Back());
    }

    [Fact]
    public void HistoryDepthIsCapped()
    {
        var router = new Router();
        for (var i = 0; i < Router.MaxDepth * 2; i++) router.Navigate($"library/{i}");
        var steps = 0;
        while (router.Back()) steps++;
        Assert.Equal(Router.MaxDepth - 1, steps);
    }

    [Fact]
    public void ReturnWithResultGoesBackAndHighlights()
    {
        var router = new Router();
        router.Navigate("library/a/mods");
        router.Navigate("discover/mod?target=a", new ReturnContext(router.Current, "生存整合包 › 模组"));
        Assert.Equal("生存整合包 › 模组", router.CurrentReturn?.Label);
        router.Navigate("discover/mod/sodium?target=a"); // 在发现里看详情，返回上下文不跟过来
        Assert.Null(router.CurrentReturn);
        router.Back();
        Assert.True(router.Return("sodium"));
        Assert.Equal("library/a/mods", router.Current.ToString());
        Assert.Equal("sodium", router.TakeHighlight());
        Assert.Null(router.TakeHighlight());
        Assert.False(router.CanGoForward); // 退回原处，历史没变长
        // 没有上下文时 Return 等于 Back。
        router.Navigate("library/a/worlds");
        Assert.True(router.Return());
        Assert.Equal("library/a/mods", router.Current.ToString());
    }

    [Theory]
    [InlineData("home", "library")]
    [InlineData("instances", "library/current")]
    [InlineData("downloads", "library/new")]
    [InlineData("log", "library/current/logs")]
    [InlineData("content", "discover")]
    [InlineData("multiplayer", "play")]
    [InlineData("tools", "settings#Junk")]
    [InlineData("settings", "settings")]
    [InlineData("accounts", "me")]
    public void EveryOldPageHasANewHome(string page, string route) => Assert.Equal(route, LegacyRoutes.For(page)?.ToString());

    [Theory]
    [InlineData("library/start", "library")]              // the launch page: starting happens on each instance
    [InlineData("library/logs", "library/current/logs")]  // the standalone log page: the focused instance's Logs tab
    [InlineData("settings/tools", "settings#Junk")]       // the toolbox: Settings › Storage
    [InlineData("library", "library")]
    [InlineData("library/new", "library/new")]
    [InlineData("library/1.21.1", "library/1.21.1")]
    [InlineData("library/start/mods", "library/start/mods")] // an instance that happens to be called "start"
    [InlineData("library/1.21.1/logs", "library/1.21.1/logs")]
    [InlineData("settings#Source", "settings#Source")]
    [InlineData("play/servers", "play/servers")]
    [InlineData("play/settings#ContactsCard", "people/add")] // the friends card: the Friends page's add panel
    [InlineData("play/settings#IdentityCard", "play/settings#IdentityCard")]
    public void RoutesOfRemovedPagesGoToWhatReplacedThem(string saved, string expected) =>
        Assert.Equal(expected, LegacyRoutes.Resolve(Route.Parse(saved)).ToString());

    [Fact]
    public void TheToolboxRouteLandsOnARealSetting()
    {
        var route = Route.Parse(LegacyRoutes.Tools);
        Assert.Equal(Section.Settings, route.Section);
        Assert.Equal(SettingsCatalog.Storage, SettingsCatalog.Find(route.Fragment)?.Group);
    }

    [Fact]
    public void RouterRewritesBeforeItNavigates()
    {
        var router = new Router { Rewrite = LegacyRoutes.Resolve };
        Route? seen = null;
        router.Navigated += (to, _) => seen = to;
        router.Navigate("library/new");
        router.Navigate("library/start");
        // The retired route never becomes current or enters history: back goes to where the player really was.
        Assert.Equal("library", router.Current.ToString());
        Assert.Equal("library", seen?.ToString());
        Assert.True(router.Back());
        Assert.Equal("library/new", router.Current.ToString());
        router.Navigate("settings/tools");
        Assert.Equal("settings#Junk", router.Current.ToString());
        // A host can keep a route as it is (an instance really called "start").
        router.Rewrite = route => route[1] == "start" ? route : LegacyRoutes.Resolve(route);
        router.Navigate("library/start");
        Assert.Equal("library/start", router.Current.ToString());
    }

    [Fact]
    public void EveryThemePageIdIsMapped() => Assert.All(ThemeLayout.PageIds, id => Assert.NotNull(LegacyRoutes.For(id)));

    [Theory]
    [InlineData(1339, SidebarMode.Auto, false, false)]
    [InlineData(1340, SidebarMode.Auto, false, true)]
    [InlineData(1320, SidebarMode.Auto, true, true)] // 回差：已经展开时要窄过 1308 才收
    [InlineData(1300, SidebarMode.Auto, true, false)]
    [InlineData(880, SidebarMode.Expanded, false, true)]
    [InlineData(879, SidebarMode.Expanded, false, false)] // 展开后内容区不到 640，不展开
    [InlineData(2000, SidebarMode.Collapsed, false, false)]
    public void SidebarExpandsByWindowWidth(double width, SidebarMode mode, bool current, bool expected) =>
        Assert.Equal(expected, ShellRules.IsExpanded(width, mode, expanded: current));

    [Fact]
    public void ShellSizesFollowTiers()
    {
        Assert.Equal(48, ShellRules.TopBarFor(HeightTier.Normal));
        Assert.Equal(40, ShellRules.TopBarFor(HeightTier.Short));
        Assert.Equal(40, ShellRules.TopBarFor(HeightTier.Tight));
        Assert.Equal([16d, 24d, 32d], [ShellRules.PagePaddingFor(WidthTier.Compact), ShellRules.PagePaddingFor(WidthTier.Medium), ShellRules.PagePaddingFor(WidthTier.Wide)]);
        Assert.Equal(SidebarMode.Expanded, ShellRules.ParseMode("full"));
        Assert.Equal(SidebarMode.Collapsed, ShellRules.ParseMode("rail"));
        Assert.Equal(SidebarMode.Auto, ShellRules.ParseMode(null));
        Assert.All(Enum.GetValues<SidebarMode>(), m => Assert.Equal(m, ShellRules.ParseMode(ShellRules.ModeName(m))));
    }

    private static ThemePack ReadShell(string json)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in new[] { ("theme.json", """{ "id": "nav-test", "name": "nav test", "format": 1 }"""), ("layout/shell.json", json) })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }
        ms.Position = 0;
        return ThemePackReader.Read(ms);
    }

    [Fact]
    public void ShellFormat1IsUpgraded()
    {
        var shell = ReadShell("""{ "navWidth": 64, "pages": ["multiplayer", "settings", "content"], "hidden": ["downloads", "content", "tools"] }""").Shell!;
        Assert.Equal("rail", shell.NavStyle); // 窄于 120 的旧导航是图标栏
        Assert.Equal(64, shell.RailWidth);
        Assert.Equal(["multiplayer", "people", "discover", "library"], shell.Sections); // Friends follows multiplayer in packs that predate it
        Assert.Equal(["discover"], shell.Hidden); // 下载和资源都藏了才藏发现

        shell = ReadShell("""{ "navWidth": 150, "navStyle": "full", "hidden": ["content"] }""").Shell!;
        Assert.Equal("expanded", shell.NavStyle);
        Assert.Equal(200, shell.SidebarWidth);
        Assert.Empty(shell.Hidden); // 只藏资源页不会藏掉整个发现
    }

    [Fact]
    public void ShellFormat2FieldsAreRead()
    {
        var pack = ReadShell("""{ "navStyle": "rail", "railWidth": 200, "sidebarWidth": 10, "showShelf": false, "pages": ["discover"], "hidden": ["multiplayer", "library", "settings"] }""");
        var shell = pack.Shell!;
        Assert.Equal("rail", shell.NavStyle);
        Assert.Equal(96, shell.RailWidth);
        Assert.Equal(200, shell.SidebarWidth);
        Assert.False(shell.ShowShelf);
        Assert.Equal(["discover", "library", "multiplayer", "people"], shell.Sections);
        Assert.Equal(["multiplayer"], shell.Hidden);
        Assert.Equal(2, pack.Problems.Count(p => p.Code == "required_page"));
    }
}
