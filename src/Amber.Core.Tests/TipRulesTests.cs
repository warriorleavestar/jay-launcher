using Amber.Core.Localization;
using Amber.Core.Tutorial;

namespace Amber.Core.Tests;

public class TipRulesTests
{
    private DateTimeOffset _now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private TipRules Rules(TutorialState? state = null) => new(state ?? new TutorialState(), () => _now);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    [Fact]
    public void WaitsForQuietAndForNothingBusy()
    {
        var rules = Rules();
        Assert.False(rules.CanShow(Tips.ModsToggle, TimeSpan.FromMilliseconds(500), busy: false));
        Assert.False(rules.CanShow(Tips.ModsToggle, Quiet, busy: true));
        Assert.True(rules.CanShow(Tips.ModsToggle, TipRules.Idle, busy: false));
    }

    [Fact]
    public void OneAtATimeAndOnlyOnce()
    {
        var state = new TutorialState();
        var rules = Rules(state);
        rules.Shown(Tips.ModsToggle);
        Assert.False(rules.CanShow(Tips.FirstCrash, Quiet, false));
        rules.Closed();
        Assert.True(rules.CanShow(Tips.FirstCrash, Quiet, false));
        Assert.False(rules.Wants(Tips.ModsToggle));
        // Remembered across runs through the saved state.
        Assert.False(Rules(state).Wants(Tips.ModsToggle));
    }

    [Fact]
    public void MutingAKindSilencesTheWholeKind()
    {
        var rules = Rules();
        rules.Shown(Tips.NarrowWindow);
        rules.Closed(muteKind: true);
        Assert.True(rules.IsKindMuted(TipKind.Appearance));
        Assert.False(rules.Wants(Tips.ThemeImport));
        Assert.True(rules.Wants(Tips.FirstKnock));
    }

    [Fact]
    public void TwoQuickClosesMuteTheRunButSlowOnesDont()
    {
        var rules = Rules();
        rules.Shown(Tips.ModsToggle);
        rules.Closed();
        _now += TimeSpan.FromSeconds(11);
        rules.Shown(Tips.FirstCrash);
        rules.Closed();
        Assert.False(rules.MutedThisRun);
        _now += TimeSpan.FromSeconds(9);
        rules.Shown(Tips.FirstKnock);
        rules.Closed();
        Assert.True(rules.MutedThisRun);
        Assert.False(rules.Wants(Tips.ThemeImport));
    }

    [Fact]
    public void HiddenByLeavingThePageDoesNotCountAsClosing()
    {
        var rules = Rules();
        rules.Shown(Tips.ModsToggle);
        rules.Hidden();
        rules.Shown(Tips.FirstCrash);
        rules.Closed();
        rules.Shown(Tips.FirstKnock);
        rules.Hidden();
        Assert.False(rules.MutedThisRun);
        Assert.Null(rules.Current);
    }

    [Fact]
    public void ResetBringsEverythingBack()
    {
        var rules = Rules();
        rules.Shown(Tips.ModsToggle);
        rules.Closed(muteKind: true);
        rules.Shown(Tips.FirstCrash);
        rules.Closed();
        rules.Reset();
        Assert.True(rules.Wants(Tips.ModsToggle) && rules.Wants(Tips.FirstCrash) && !rules.MutedThisRun);
    }

    [Theory]
    [InlineData(420, true, TipMode.Bar)]
    [InlineData(559, true, TipMode.Bar)]
    [InlineData(560, true, TipMode.Bubble)]
    [InlineData(900, false, TipMode.Bar)]
    public void ShortWindowsOrMissingAnchorsUseTheBar(double height, bool anchor, TipMode mode) =>
        Assert.Equal(mode, TipRules.ModeFor(height, anchor));

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ja")]
    [InlineData("lzh")]
    public void EveryTipHasText(string code)
    {
        var table = L.Load(code);
        foreach (var tip in Tips.All) Assert.True(table.TryGetValue(tip.TextKey, out var text) && text.Length > 0, $"{code}: {tip.TextKey}");
        Assert.Equal(Tips.All.Count, Tips.All.Select(t => t.Id).Distinct().Count());
    }
}
