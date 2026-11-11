using Amber.Core.Activity;

namespace Amber.Core.Tests;

/// <summary>The speed and time-left numbers behind the one progress line.</summary>
public class ProgressMeterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void NothingToSayBeforeThereIsHistory()
    {
        var meter = new ProgressMeter();
        Assert.Null(meter.BytesPerSecond);
        Assert.Null(meter.Remaining);
        meter.Add(At(0), 0.0, 0);
        meter.Add(At(0.5), 0.1, 1_000_000);
        // Half a second in: too early for a speed, far too early for an estimate.
        Assert.Null(meter.BytesPerSecond);
        Assert.Null(meter.Remaining);
        meter.Add(At(1), 0.2, 2_000_000);
        Assert.Equal(2_000_000, meter.BytesPerSecond!.Value, 3);
        Assert.Null(meter.Remaining);
    }

    [Fact]
    public void SteadyDownloadGivesSpeedAndTimeLeft()
    {
        var meter = new ProgressMeter();
        for (var s = 0; s <= 4; s++) meter.Add(At(s), s * 0.1, s * 4_000_000L);
        Assert.Equal(4_000_000, meter.BytesPerSecond!.Value, 3);
        // 40% done at 10% a second: 6 seconds to go.
        Assert.Equal(6, meter.Remaining!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void OnlyTheLastSecondsCount()
    {
        var meter = new ProgressMeter(TimeSpan.FromSeconds(4));
        // Ten slow seconds, then four fast ones: the numbers follow the fast stretch.
        for (var s = 0; s <= 10; s++) meter.Add(At(s), s * 0.01, s * 1_000L);
        for (var s = 11; s <= 14; s++) meter.Add(At(s), 0.10 + (s - 10) * 0.1, 10_000 + (s - 10) * 1_000_000L);
        Assert.InRange(meter.BytesPerSecond!.Value, 900_000, 1_000_001);
        Assert.InRange(meter.Remaining!.Value.TotalSeconds, 4.9, 6.5);
    }

    [Fact]
    public void WithoutBytesThereIsNoSpeedButStillAnEstimate()
    {
        var meter = new ProgressMeter();
        meter.Add(At(0), 0.0);
        meter.Add(At(2), 0.5);
        Assert.Null(meter.BytesPerSecond);
        Assert.Equal(2, meter.Remaining!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void UnknownFractionGivesSpeedOnly()
    {
        var meter = new ProgressMeter();
        meter.Add(At(0), null, 0);
        meter.Add(At(2), null, 3_000_000);
        Assert.Equal(1_500_000, meter.BytesPerSecond!.Value, 3);
        Assert.Null(meter.Remaining);
    }

    [Fact]
    public void GoingBackwardsStartsOver()
    {
        var meter = new ProgressMeter();
        for (var s = 0; s <= 3; s++) meter.Add(At(s), s * 0.3, s * 1_000_000L);
        Assert.NotNull(meter.Remaining);
        // The next step starts from zero: the old pace says nothing about it.
        meter.Add(At(4), 0.0, 0);
        Assert.Null(meter.BytesPerSecond);
        Assert.Null(meter.Remaining);
        meter.Add(At(6), 0.5, 2_000_000);
        Assert.Equal(1_000_000, meter.BytesPerSecond!.Value, 3);
        Assert.Equal(2, meter.Remaining!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void StalledOrAbsurdEstimatesAreNotShown()
    {
        var stalled = new ProgressMeter();
        stalled.Add(At(0), 0.3, 100);
        stalled.Add(At(5), 0.3, 100);
        Assert.Null(stalled.Remaining);
        Assert.Equal(0, stalled.BytesPerSecond!.Value, 3);

        var crawling = new ProgressMeter();
        crawling.Add(At(0), 0.0);
        crawling.Add(At(5), 0.00001);
        // Five days at this pace: a guess, not an estimate.
        Assert.Null(crawling.Remaining);

        var done = new ProgressMeter();
        done.Add(At(0), 0.5);
        done.Add(At(3), 1.0);
        Assert.Equal(TimeSpan.Zero, done.Remaining);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var meter = new ProgressMeter();
        for (var s = 0; s <= 3; s++) meter.Add(At(s), s * 0.1, s * 1000L);
        meter.Reset();
        Assert.Null(meter.BytesPerSecond);
        Assert.Null(meter.Remaining);
    }
}
