using Amber.Core.Help;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class LessonsTests
{
    [Fact]
    public void EachLessonHasFiveCardsAndEndsWithItsNextStep()
    {
        Assert.Equal(5, Lessons.MultiplayerCards.Count);
        Assert.Equal(5, Lessons.SafetyCards.Count);
        Assert.Equal(LessonAction.Diagnostics, Lessons.MultiplayerCards[^1].Action);
        Assert.Equal(LessonAction.Drill, Lessons.SafetyCards[^1].Action);
        Assert.All(Lessons.MultiplayerCards.SkipLast(1).Concat(Lessons.SafetyCards.SkipLast(1)), c => Assert.Equal(LessonAction.None, c.Action));
        Assert.Equal(10, Lessons.Pictures.Distinct().Count());
    }

    [Fact]
    public void EveryCardHasTextInEveryLanguage()
    {
        string[] keys = ["lesson.multiplayer.heading", "lesson.safety.heading", "lesson.step", "lesson.next", "lesson.previous", "lesson.done", "lesson.article"];
        foreach (var code in new[] { "en", "zh-Hans", "zh-Hant", "ja", "lzh" })
        {
            var table = L.Load(code);
            foreach (var key in keys.Concat(Lessons.MultiplayerCards.Concat(Lessons.SafetyCards).SelectMany(c => new[] { c.Title, c.Body })))
                Assert.True(table.ContainsKey(key), $"{code}: {key}");
        }
    }

    [Fact]
    public void EveryPictureIsDrawn()
    {
        foreach (var id in Lessons.Pictures) Assert.True(Amber.App.Views.Controls.LineArt.Has(id), id);
    }

    [Theory]
    [InlineData(LessonKind.Multiplayer)]
    [InlineData(LessonKind.Safety)]
    public void TheFullArticleExists(LessonKind kind)
    {
        foreach (var lang in new[] { "en", "zh-Hans", "zh-Hant", "ja" })
            Assert.NotNull(HelpLibrary.Load(lang).Get(Lessons.Article(kind)));
    }
}
