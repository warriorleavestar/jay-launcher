using Amber.Core.Activity;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>
/// What a page binds its progress line to (<c>c:ProgressLine</c>): the percent it reports plus the speed and the time
/// left worked out from the last few seconds. The page keeps its own busy flag and status text.
/// </summary>
public sealed partial class WorkProgress : ObservableObject
{
    private readonly ProgressMeter _meter = new();
    private long? _bytes;

    /// <summary>0–100.</summary>
    [ObservableProperty] public partial double Percent { get; set; }

    /// <summary>True until the work has reported how far it is.</summary>
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty] public partial double? BytesPerSecond { get; set; }

    [ObservableProperty] public partial TimeSpan? Remaining { get; set; }

    /// <summary>Bytes transferred so far, when the work knows. Counted with the next <see cref="Report"/>.</summary>
    public void Bytes(long bytes) => _bytes = bytes;

    /// <summary>The work is at <paramref name="percent"/> (0–100).</summary>
    public void Report(double percent, DateTimeOffset? now = null)
    {
        percent = Math.Clamp(percent, 0, 100);
        _meter.Add(now ?? DateTimeOffset.Now, percent / 100, _bytes);
        Percent = percent;
        if (percent > 0) IsIndeterminate = false;
        BytesPerSecond = _meter.BytesPerSecond;
        Remaining = _meter.Remaining;
    }

    /// <summary>New work starts, or the work ended: nothing is known.</summary>
    public void Reset()
    {
        _meter.Reset();
        _bytes = null;
        Percent = 0;
        IsIndeterminate = true;
        BytesPerSecond = null;
        Remaining = null;
    }
}
