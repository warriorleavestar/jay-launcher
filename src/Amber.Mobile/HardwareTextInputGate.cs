namespace Amber.Mobile;

/// <summary>
/// Filters duplicate Android hardware-key deliveries without suppressing genuine key repeats.
/// The gate belongs to the view lifetime because Android may recreate input connections between
/// two deliveries of the same physical key.
/// </summary>
internal sealed class HardwareTextInputGate
{
    private const long ImeCommitSuppressionWindowMs = 500;
    private bool _hasLast;
    private int _lastKeyCode;
    private long _lastDownTime;
    private int _lastRepeatCount;
    private bool _hasPendingCommittedDuplicate;
    private int _lastHardwareCodePoint;
    private long _lastHardwareDeliveredAt;

    public bool Accept(int keyCode, long downTime, int repeatCount)
    {
        var sameKey = _hasLast && _lastKeyCode == keyCode;
        var duplicateRepeat = sameKey && _lastDownTime == downTime &&
                              _lastRepeatCount == repeatCount;
        if (duplicateRepeat) return false;

        _hasLast = true;
        _lastKeyCode = keyCode;
        _lastDownTime = downTime;
        _lastRepeatCount = repeatCount;
        return true;
    }

    public bool AcceptHardwareCodePoint(int keyCode, int codePoint, long downTime,
        long deliveredAt, int repeatCount)
    {
        if (!Accept(keyCode, downTime, repeatCount)) return false;
        _hasPendingCommittedDuplicate = true;
        _lastHardwareCodePoint = codePoint;
        _lastHardwareDeliveredAt = deliveredAt;
        return true;
    }

    public bool AcceptCommittedCodePoint(int codePoint, long deliveredAt)
    {
        var hardwareDuplicate = _hasPendingCommittedDuplicate &&
                                _lastHardwareCodePoint == codePoint &&
                                deliveredAt >= _lastHardwareDeliveredAt &&
                                deliveredAt - _lastHardwareDeliveredAt <= ImeCommitSuppressionWindowMs;
        // A physical key may be echoed once through CommitText by a recreated input
        // connection. Consume only that one correlated echo. Keeping the pending marker set
        // would also discard a legitimate second copy of the same character (for example the
        // two 'o' characters in "book").
        _hasPendingCommittedDuplicate = false;
        return !hardwareDuplicate;
    }

}
