using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>The launch steps: what each step says while a start runs, why one was skipped or failed, and when one is slow.</summary>
[Collection("GlobalLanguage")]
public sealed class LaunchStepsTests
{
    private sealed class Lang : IDisposable
    {
        private readonly string _before = L.Language;
        public Lang(string code) => L.SetLanguage(code);
        public void Dispose() => L.SetLanguage(_before);
    }

    private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private LaunchSteps New(LaunchStepTimings? timings = null) => new(() => _now, timings, "pack");

    [Fact]
    public void StartingAStepFinishesTheOneBefore()
    {
        var steps = New();
        steps.Begin(LaunchStepId.Files);
        Assert.Equal(LaunchStepId.Files, steps.Current?.Id);
        steps.Begin(LaunchStepId.Java);
        Assert.Equal(LaunchStepState.Done, steps[LaunchStepId.Files].State);
        Assert.Equal(LaunchStepState.Running, steps[LaunchStepId.Java].State);
        Assert.Equal(LaunchStepState.Pending, steps[LaunchStepId.Download].State);
    }

    [Fact]
    public void SkippingAStepSaysWhyAndFinishesTheRunningOne()
    {
        var steps = New();
        steps.Count(LaunchStepId.Files, 10, 480);
        steps.Skip(LaunchStepId.Download, "All files are there, skipped");
        Assert.Equal(LaunchStepState.Done, steps[LaunchStepId.Files].State);
        Assert.Equal(LaunchStepState.Skipped, steps[LaunchStepId.Download].State);
        Assert.Equal("All files are there, skipped", steps[LaunchStepId.Download].Reason);
        Assert.Null(steps.Current);
    }

    [Fact]
    public void OnlyTheRunningStepHasACount()
    {
        var steps = New();
        steps.Begin(LaunchStepId.Loader);
        steps.Count(LaunchStepId.Loader, 12, 87, "Going through the mods one by one");
        Assert.True(steps[LaunchStepId.Loader].HasCount);
        Assert.Equal("Going through the mods one by one", steps[LaunchStepId.Loader].Detail);
        steps.Done(LaunchStepId.Loader);
        Assert.False(steps[LaunchStepId.Loader].HasCount);
    }

    [Fact]
    public void AFailureLandsOnTheStepThatWasRunning_AndTheRestStayPending()
    {
        var steps = New();
        steps.Begin(LaunchStepId.Files);
        steps.Begin(LaunchStepId.Java);
        steps.Fail("No Java 21 found");
        Assert.True(steps.Failed);
        Assert.Equal(LaunchStepState.Failed, steps[LaunchStepId.Java].State);
        Assert.Equal("No Java 21 found", steps[LaunchStepId.Java].Reason);
        Assert.Equal(LaunchStepState.Pending, steps[LaunchStepId.Sandbox].State);
    }

    [Fact]
    public void CancellingStopsTheRunningStep()
    {
        var steps = New();
        steps.Begin(LaunchStepId.Download);
        steps.Cancel("Cancelled");
        Assert.Equal(LaunchStepState.Skipped, steps[LaunchStepId.Download].State);
        Assert.Null(steps.Current);
        Assert.False(steps.Failed);
    }

    [Fact]
    public void AStepIsSlowOnlyAfterItsFloor()
    {
        var steps = New();
        steps.Begin(LaunchStepId.Account);
        _now += LaunchStepTimings.Floor(LaunchStepId.Account) - TimeSpan.FromSeconds(1);
        Assert.False(steps.Tick());
        _now += TimeSpan.FromSeconds(2);
        Assert.True(steps.Tick());
        Assert.True(steps[LaunchStepId.Account].Slow);
        // Said once.
        Assert.False(steps.Tick());
    }

    [Fact]
    public void SlowMeansTwiceTheUsualTimeForThisInstance()
    {
        var timings = new LaunchStepTimings();
        var first = New(timings);
        first.Begin(LaunchStepId.Loader);
        _now += TimeSpan.FromSeconds(80);
        first.Done(LaunchStepId.Loader);
        Assert.Equal(TimeSpan.FromSeconds(160), timings.Usual("PACK", LaunchStepId.Loader));
        // Another instance keeps the floor.
        Assert.Equal(LaunchStepTimings.Floor(LaunchStepId.Loader), timings.Usual("other", LaunchStepId.Loader));

        var second = New(timings);
        second.Begin(LaunchStepId.Loader);
        _now += TimeSpan.FromSeconds(120);
        Assert.False(second.Tick());
        _now += TimeSpan.FromSeconds(41);
        Assert.True(second.Tick());
    }

    [Fact]
    public void ADownloadIsSlowWhenNothingMoves()
    {
        var steps = New();
        steps.Count(LaunchStepId.Download, 1, 100);
        _now += TimeSpan.FromSeconds(15);
        steps.Count(LaunchStepId.Download, 40, 100);
        _now += LaunchStepTimings.Stalled - TimeSpan.FromSeconds(1);
        Assert.False(steps.Tick());
        _now += TimeSpan.FromSeconds(2);
        Assert.True(steps.Tick());
        // Moving again clears it.
        steps.Count(LaunchStepId.Download, 41, 100);
        Assert.False(steps[LaunchStepId.Download].Slow);
    }

    [Fact]
    public void FileProgressAfterJavaStartedIsIgnored()
    {
        var steps = New();
        steps.Count(LaunchStepId.Files, 10, 480);
        steps.Begin(LaunchStepId.Java);
        steps.Count(LaunchStepId.Files, 480, 480);
        steps.Count(LaunchStepId.Download, 3, 480);
        Assert.Equal(LaunchStepState.Done, steps[LaunchStepId.Files].State);
        Assert.Equal(LaunchStepState.Pending, steps[LaunchStepId.Download].State);
        Assert.Equal(LaunchStepId.Java, steps.Current?.Id);
    }

    [Fact]
    public void ProgressAfterCancelOrFailureIsIgnored()
    {
        var steps = New();
        steps.Count(LaunchStepId.Download, 5, 100);
        steps.Cancel("Cancelled");
        steps.Count(LaunchStepId.Download, 6, 100);
        steps.Count(LaunchStepId.Java, 1, 2);
        steps.Begin(LaunchStepId.Account);
        Assert.Equal(LaunchStepState.Skipped, steps[LaunchStepId.Download].State);
        Assert.Null(steps.Current);
        Assert.Equal(LaunchStepState.Pending, steps[LaunchStepId.Java].State);

        var failed = New();
        failed.Begin(LaunchStepId.Java);
        failed.Fail("No Java");
        failed.Detail(LaunchStepId.Java, "later");
        failed.Begin(LaunchStepId.Account);
        Assert.Equal(LaunchStepState.Failed, failed[LaunchStepId.Java].State);
        Assert.Null(failed.Current);
    }

    [Fact]
    public void BytesArrivingAgainAfterAStallClearSlow()
    {
        var steps = New();
        var changes = 0;
        steps.Count(LaunchStepId.Download, 0, 3, bytes: 1_000);
        _now += LaunchStepTimings.Stalled + TimeSpan.FromSeconds(1);
        Assert.True(steps.Tick());
        Assert.True(steps[LaunchStepId.Download].Slow);
        steps.Changed += () => changes++;
        // Same file count, more bytes: the line stops saying it waits on the server.
        steps.Count(LaunchStepId.Download, 0, 3, bytes: 2_000_000);
        Assert.False(steps[LaunchStepId.Download].Slow);
        Assert.Equal(1, changes);
        Assert.False(steps.Tick());
    }

    [Fact]
    public void ABigFileStillArrivingIsNotAStall()
    {
        var steps = New();
        steps.Count(LaunchStepId.Download, 0, 3, bytes: 1_000);
        for (var i = 0; i < 6; i++)
        {
            _now += TimeSpan.FromSeconds(10);
            steps.Count(LaunchStepId.Download, 0, 3, bytes: 1_000 + (i + 1) * 5_000_000L);
            Assert.False(steps.Tick());
        }
        // Neither the bytes nor the count move: now it is waiting on the server.
        _now += LaunchStepTimings.Stalled + TimeSpan.FromSeconds(1);
        steps.Count(LaunchStepId.Download, 0, 3, bytes: 30_001_000);
        Assert.True(steps.Tick());
    }

    [Fact]
    public void LinesUseTheStepNamesAndTheCount()
    {
        using var _ = new Lang("zh-Hans");
        var steps = New();
        steps.Count(LaunchStepId.Loader, 12, 87);
        Assert.Equal("用 Jay 加载器准备模组 12/87", steps[LaunchStepId.Loader].Line);
        _now += TimeSpan.FromMinutes(5);
        steps.Tick();
        Assert.Equal("用 Jay 加载器准备模组 12/87 · 这一步比平时久，Jay 加载器在逐个处理模组，模组多时会久一些", steps[LaunchStepId.Loader].StatusLine());
        steps.Skip(LaunchStepId.Sandbox, "沙盒关着，跳过");
        Assert.Equal("用 Jay 加载器准备模组：完成", steps[LaunchStepId.Loader].LogLine());
        Assert.Equal("准备沙盒：跳过 (沙盒关着，跳过)", steps[LaunchStepId.Sandbox].LogLine());
    }

    [Fact]
    public void EveryStepHasATitleAndAWaitingLineInEveryLanguage()
    {
        foreach (var code in new[] { "en", "zh-Hans", "zh-Hant", "ja", "lzh" })
        {
            var table = L.Load(code);
            foreach (var id in Enum.GetValues<LaunchStepId>())
            {
                var step = new LaunchStep(id, LaunchStepState.Running);
                Assert.True(table.ContainsKey(step.TitleKey), $"{code} {step.TitleKey}");
                Assert.True(table.ContainsKey(step.WaitingKey), $"{code} {step.WaitingKey}");
            }
        }
    }

    [Theory]
    [InlineData("requirements", true)]
    [InlineData("platform", true)]
    [InlineData("bake-mod", true)]
    [InlineData("bake-instance", true)]
    [InlineData("warm", true)]
    [InlineData("log", false)]
    public void LoaderStagesAreSaidInPlainWords(string stage, bool known)
    {
        using var _ = new Lang("zh-Hans");
        var text = HomeViewModel.LoaderStageText(stage);
        Assert.Equal(known, text is not null);
        if (text is not null) Assert.DoesNotContain("烘焙", text);
    }

    [Fact]
    public void AFailedStepKeepsOneShortLine()
    {
        Assert.Equal("first line", HomeViewModel.ShortReason("first line\nat Foo.Bar()"));
        Assert.Equal(120, HomeViewModel.ShortReason(new string('x', 300)).Length);
    }
}
