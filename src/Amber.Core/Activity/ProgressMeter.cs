namespace Amber.Core.Activity;

/// <summary>
/// Turns a stream of progress reports into a speed and a time-left estimate, for the one progress line every download
/// uses. It looks at the last few seconds only, so the numbers follow the connection instead of the whole run.
/// Not thread-safe: the owner serializes calls.
/// </summary>
public sealed class ProgressMeter(TimeSpan? window = null)
{
    /// <summary>How far back the speed and the estimate look.</summary>
    public TimeSpan Window { get; } = window ?? TimeSpan.FromSeconds(8);

    /// <summary>Under this much history there is no estimate yet (the first second of a download says little).</summary>
    public static readonly TimeSpan MinHistory = TimeSpan.FromSeconds(2);

    /// <summary>Longer estimates than this are not shown: they are guesses.</summary>
    public static readonly TimeSpan MaxEstimate = TimeSpan.FromHours(24);

    private readonly List<(DateTimeOffset At, double? Fraction, long? Bytes)> _samples = [];

    /// <summary>
    /// One report: <paramref name="fraction"/> is 0–1 or null when unknown, <paramref name="bytes"/> the total
    /// transferred so far or null. Going backwards (a new step started from zero) starts the history over.
    /// </summary>
    public void Add(DateTimeOffset now, double? fraction, long? bytes = null)
    {
        if (_samples.Count > 0)
        {
            var last = _samples[^1];
            if (now < last.At || (fraction is { } f && last.Fraction is { } lf && f < lf) || (bytes is { } b && last.Bytes is { } lb && b < lb))
                _samples.Clear();
        }
        _samples.Add((now, fraction, bytes));
        // Keep one sample older than the window so the span covers all of it.
        while (_samples.Count > 2 && now - _samples[1].At >= Window) _samples.RemoveAt(0);
    }

    public void Reset() => _samples.Clear();

    /// <summary>Bytes per second over the window. Null without byte counts or with under a second of history.</summary>
    public double? BytesPerSecond
    {
        get
        {
            var first = _samples.FirstOrDefault(s => s.Bytes is not null);
            var last = _samples.LastOrDefault(s => s.Bytes is not null);
            if (first.Bytes is not { } b0 || last.Bytes is not { } b1) return null;
            var seconds = (last.At - first.At).TotalSeconds;
            return seconds < 1 ? null : (b1 - b0) / seconds;
        }
    }

    /// <summary>
    /// Time left at the pace of the window. Null while there is too little history, when nothing moved, when the
    /// fraction is unknown, or when the answer would be longer than a day.
    /// </summary>
    public TimeSpan? Remaining
    {
        get
        {
            var first = _samples.FirstOrDefault(s => s.Fraction is not null);
            var last = _samples.LastOrDefault(s => s.Fraction is not null);
            if (first.Fraction is not { } f0 || last.Fraction is not { } f1) return null;
            var span = last.At - first.At;
            if (span < MinHistory || f1 <= f0) return null;
            if (f1 >= 1) return TimeSpan.Zero;
            var seconds = (1 - f1) / ((f1 - f0) / span.TotalSeconds);
            return seconds > MaxEstimate.TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
        }
    }
}
