using Amber.Core.Help;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

public class TermsTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ja")]
    [InlineData("lzh")]
    public void EveryTermHasANameAndASentence(string code)
    {
        var table = L.Load(code);
        foreach (var t in Terms.All)
        {
            Assert.True(table.TryGetValue(t.NameKey, out var name) && name.Length > 0, $"{code}: {t.NameKey}");
            Assert.True(table.TryGetValue(t.HintKey, out var hint) && hint.Length > name!.Length, $"{code}: {t.HintKey}");
        }
        Assert.True(table.ContainsKey("term.what_is") && table.ContainsKey("term.learn_more"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ja")]
    public void EveryTermLeadsToAnArticle(string lang)
    {
        var ids = HelpLibrary.LoadLanguage(lang).Select(a => a.Id).ToHashSet();
        foreach (var t in Terms.All) Assert.Contains(t.Article, ids);
    }

    [Fact]
    public void IdsAreUniqueAndKnown()
    {
        Assert.Equal(Terms.All.Count, Terms.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal("connection", Terms.Get("relay")?.Article);
        Assert.Null(Terms.Get("nope"));
        Assert.Null(Terms.Get(null));
    }
}
