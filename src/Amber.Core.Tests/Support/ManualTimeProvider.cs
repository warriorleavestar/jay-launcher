namespace Amber.Core.Tests;

/// <summary>A clock that only moves when a test calls <see cref="Advance"/>, so age checks don't depend on machine load.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = TimeSpan.TicksPerDay;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _ticks));
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
