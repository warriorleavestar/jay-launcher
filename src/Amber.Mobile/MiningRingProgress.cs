namespace Amber.Mobile;

/// <summary>Presentation state for one held finger. Only a completed mining cycle can flash a full ring.</summary>
public sealed class MiningRingProgress
{
    private int _lastPartial = -1;
    private long _fullUntil;

    public int Observe(int damage, long nowMs)
    {
        if (damage is >= 0 and < 1000)
        {
            _lastPartial = damage;
            _fullUntil = 0;
            return damage;
        }

        // The break tick can precede the terrain upload. Keep the existing arc until
        // the renderer has a replacement, then close it in this very draw.
        if (damage == -2) return _lastPartial;
        if (damage == 1000 && _lastPartial >= 0)
            _fullUntil = nowMs + 100;
        _lastPartial = -1;
        return nowMs < _fullUntil ? 1000 : -1;
    }

    public static float PartialSweep(int damage, float radius, float strokeWidth)
    {
        // Round end caps cover one stroke width of the gap. Leave another width visible.
        float gapDegrees = 2 * strokeWidth / radius * (180 / MathF.PI);
        return Math.Min(damage * 0.36f, 360 - gapDegrees);
    }
}
