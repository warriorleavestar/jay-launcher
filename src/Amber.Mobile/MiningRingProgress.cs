namespace Amber.Mobile;

/// <summary>Presentation state for one held finger. Only a completed mining cycle can flash a full ring.</summary>
public sealed class MiningRingProgress
{
    private int _lastPartial = -1;
    private bool _completionPending;
    private long _fullUntil;

    public int Observe(int damage, long nowMs)
    {
        if (damage is >= 0 and < 1000)
        {
            _lastPartial = damage;
            _completionPending = false;
            _fullUntil = 0;
            return damage;
        }

        // Minecraft has completed the tick, but its surface still needs to present it.
        // Keep the last partial for this draw, then show the closed stroke on the next draw.
        if (damage == 1000 && _lastPartial >= 0)
        {
            int partial = _lastPartial;
            _lastPartial = -1;
            _completionPending = true;
            return partial;
        }

        _lastPartial = -1;
        if (_completionPending)
        {
            _completionPending = false;
            _fullUntil = nowMs + 100;
        }
        return nowMs < _fullUntil ? 1000 : -1;
    }

    public static float PartialSweep(int damage, float radius, float strokeWidth)
    {
        // Round end caps cover one stroke width of the gap. Leave another width visible.
        float gapDegrees = 2 * strokeWidth / radius * (180 / MathF.PI);
        return Math.Min(damage * 0.36f, 360 - gapDegrees);
    }
}
