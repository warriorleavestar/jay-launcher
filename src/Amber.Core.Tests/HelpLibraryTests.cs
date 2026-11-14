using Amber.Core.Help;
using Amber.Core.Navigation;

namespace Amber.Core.Tests;

public class HelpLibraryTests
{
    [Fact]
    public void ParsesFrontMatterBlocksAndLinks()
    {
        var a = HelpArticle.Parse("---\nid: x\ncategory: start\nroutes: library, library/*\nrelated: y\n---\n\n# Title\n\nFirst **line**\ncontinues [here](jay:settings#Source).\n\n## How\n- Click [Other](help:y)\n- Press `F1`\n");
        Assert.Equal("x", a.Id);
        Assert.Equal("start", a.Category);
        Assert.Equal(["library", "library/*"], a.Routes);
        Assert.Equal("Title", a.Title);
        Assert.Equal("First line continues here.", a.Summary);
        Assert.Equal([HelpBlockKind.Paragraph, HelpBlockKind.Heading, HelpBlockKind.Bullet, HelpBlockKind.Bullet], a.Blocks.Select(b => b.Kind));
        var link = Assert.Single(a.Blocks[0].Links);
        Assert.True(link.IsRoute);
        Assert.Equal("settings#Source", link.Value);
        Assert.True(a.Blocks[2].Links[0].IsArticle);
        Assert.Equal("Press F1", a.Blocks[3].Text);
    }

    [Fact]
    public void ParsesScreenshotLines()
    {
        var a = HelpArticle.Parse("---\nid: x\ncategory: start\n---\n\n# T\n\nIntro.\n\n![The library](shot:library)\n\n## How\n- Step\n");
        var shot = Assert.Single(a.Blocks, b => b.Kind == HelpBlockKind.Shot);
        Assert.Equal("library", shot.Shot);
        Assert.Equal("The library", shot.Text);
        Assert.Equal("Intro.", a.Summary);
    }

    [Fact]
    public void ShotNamesRejectPathTricks()
    {
        Assert.Null(HelpLibrary.OpenShot("en", "../en/library"));
        Assert.Null(HelpLibrary.OpenShot("en", "nope"));
        Assert.Null(HelpLibrary.OpenShot("../x", "library"));
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryScreenshotExistsAndIsUsed(string lang)
    {
        var library = HelpLibrary.Load(lang);
        var used = library.All.SelectMany(a => a.Blocks).Where(b => b.Kind == HelpBlockKind.Shot).ToList();
        foreach (var b in used)
        {
            Assert.Contains(b.Shot!, HelpLibrary.ShotNames);
            Assert.False(string.IsNullOrWhiteSpace(b.Text), b.Shot);
        }
        foreach (var name in HelpLibrary.ShotNames)
        {
            Assert.Contains(used, b => b.Shot == name);
            using var s = HelpLibrary.OpenShot(lang, name);
            Assert.NotNull(s);
            var head = new byte[8];
            Assert.Equal(8, s!.Read(head, 0, 8));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
        }
    }

    [Fact]
    public void FourLanguagesHaveTheSameArticles()
    {
        var ids = HelpLibrary.Languages.Select(l => HelpLibrary.LoadLanguage(l).Select(a => a.Id).Order().ToArray()).ToArray();
        Assert.True(ids[0].Length >= 10, string.Join(",", ids[0]));
        foreach (var other in ids) Assert.Equal(ids[0], other);
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryLinkPointsSomewhere(string lang)
    {
        var library = HelpLibrary.Load(lang);
        foreach (var a in library.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.Contains(a.Category, HelpLibrary.Categories);
            foreach (var r in a.Related) Assert.NotNull(library.Get(r));
            foreach (var link in a.Blocks.SelectMany(b => b.Links))
            {
                if (link.IsArticle) Assert.NotNull(library.Get(link.Value));
                if (link.IsRoute && Route.Parse(link.Value).Fragment is { } field) Assert.NotNull(SettingsCatalog.Find(field));
            }
        }
    }

    [Theory]
    [InlineData("library", "library")]
    [InlineData("library/new", "new-instance")]
    [InlineData("library/current/logs", "logs")]
    [InlineData("library/1.21.1/logs", "logs")]
    [InlineData("library/1.21.1", "instance")]
    [InlineData("library/1.21.1/mods", "instance")]
    [InlineData("discover", "discover")]
    [InlineData("discover/mod?target=x", "discover")]
    [InlineData("play", "multiplayer")]
    [InlineData("settings", "settings")]
    [InlineData("settings#Source", "settings")]
    [InlineData("settings/storage", "settings")]
    [InlineData("me", "accounts")]
    [InlineData("accounts", "accounts")]
    [InlineData("people", "friends")]
    [InlineData("about", "network-list")]
    [InlineData("help", "first-time")]
    [InlineData("play/public", "public-rooms")]
    [InlineData("play/servers", "servers")]
    [InlineData("settings/network", "connection")]
    [InlineData("settings/multiplayer", "settings")]
    public void EveryRouteHasAnArticle(string route, string expected) =>
        Assert.Equal(expected, HelpLibrary.Load("zh-Hans").ForRoute(Route.Parse(route))?.Id);

    [Fact]
    public void SearchFindsByTitleBodyAndPinyinInitials()
    {
        var zh = HelpLibrary.Load("zh-Hans");
        Assert.Equal("child-mode", zh.Search("给孩子用").First().Id);
        Assert.Equal("child-mode", zh.Search("ghzy").First().Id);
        Assert.Contains(zh.Search("F8"), a => a.Id == "logs");
        Assert.Empty(zh.Search("完全没有这个词xyz"));
        Assert.Equal(zh.All.Count, zh.Search("").Count);
        Assert.Equal("logs", HelpLibrary.Load("en").Search("logs").First().Id);
    }

    [Theory]
    [InlineData("lzh", "zh-Hant")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("ja", "ja")]
    [InlineData("fr", "en")]
    public void HelpLanguageFallsBack(string ui, string expected) => Assert.Equal(expected, HelpLibrary.HelpLanguage(ui));

    /// <summary>The catalog of official §9.7: every category has articles, in every language.</summary>
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("en")]
    [InlineData("ja")]
    public void EveryCategoryHasArticles(string lang)
    {
        var library = HelpLibrary.Load(lang);
        foreach (var category in HelpLibrary.Categories)
            Assert.Contains(library.All, a => a.Category == category);
        Assert.True(library.All.Count >= 31, library.All.Count.ToString());
    }
}
