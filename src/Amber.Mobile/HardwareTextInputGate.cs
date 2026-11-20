namespace Amber.Mobile;

/// <summary>
/// Filters duplicate Android hardware-key deliveries without suppressing genuine key repeats.
/// The gate belongs to the view lifetime because Android may recreate input connections between
/// two deliveries of the same physical key.
/// </summary>
internal sealed class HardwareTextInputGate(long duplicateWindowMs = 120)
{
    private const long ImeCommitSuppressionWindowMs = 500;
    private bool _hasLast;
    private int _lastKeyCode;
    private long _lastDownTime;
    private long _lastDeliveredAt;
    private int _lastRepeatCount;
    private bool _hasPendingCommittedDuplicate;
    private int _lastHardwareCodePoint;
    private long _lastHardwareDeliveredAt;
    private bool _hasLastCommittedCodePoint;
    private int _lastCommittedCodePoint;
    private long _lastCommittedAt;

    public bool Accept(int keyCode, long downTime, long deliveredAt, int repeatCount)
    {
        var sameKey = _hasLast && _lastKeyCode == keyCode;
        var duplicateRepeat = sameKey && _lastDownTime == downTime &&
                              _lastRepeatCount == repeatCount;
        var clonedInitialPress = sameKey && repeatCount == 0 && _lastRepeatCount == 0 &&
                                 deliveredAt >= _lastDeliveredAt &&
                                 deliveredAt - _lastDeliveredAt <= duplicateWindowMs;
        if (duplicateRepeat || clonedInitialPress) return false;

        _hasLast = true;
        _lastKeyCode = keyCode;
        _lastDownTime = downTime;
        _lastDeliveredAt = deliveredAt;
        _lastRepeatCount = repeatCount;
        return true;
    }

    public bool AcceptHardwareCodePoint(int keyCode, int codePoint, long downTime,
        long deliveredAt, int repeatCount)
    {
        if (!Accept(keyCode, downTime, deliveredAt, repeatCount)) return false;
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
        if (hardwareDuplicate) return false;
        _hasPendingCommittedDuplicate = false;

        var clonedCommit = _hasLastCommittedCodePoint &&
                           _lastCommittedCodePoint == codePoint &&
                           deliveredAt >= _lastCommittedAt &&
                           deliveredAt - _lastCommittedAt <= duplicateWindowMs;
        if (clonedCommit) return false;
        _hasLastCommittedCodePoint = true;
        _lastCommittedCodePoint = codePoint;
        _lastCommittedAt = deliveredAt;
        return true;
    }

}
