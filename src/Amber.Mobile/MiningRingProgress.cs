namespace Amber.Mobile;

/// <summary>Presentation state for one held finger. Only a completed mining cycle can flash a full ring.</summary>
public sealed class MiningRingProgress
{
    private bool _wasMining;
    private long _fullUntil;

    public int Observe(int damage, long nowMs)
    {
        if (damage is >= 0 and < 1000)
        {
            _wasMining = true;
            _fullUntil = 0;
            return damage;
        }

        // Keep the completed stroke visible across a few display frames. This never
        // advances partial damage, and a new block starts its own progress immediately.
        if (damage == 1000 && _wasMining)
            _fullUntil = nowMs + 100;
        _wasMining = false;
        return nowMs < _fullUntil ? 1000 : -1;
    }
}
