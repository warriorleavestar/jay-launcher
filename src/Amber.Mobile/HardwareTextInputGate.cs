namespace Amber.Mobile;

/// <summary>
/// Filters duplicate Android hardware-key deliveries without suppressing genuine key repeats.
/// The gate belongs to the view lifetime because Android may recreate input connections between
/// two deliveries of the same physical key.
/// </summary>
internal sealed class HardwareTextInputGate
{
    private const long ImeCommitSuppressionWindowMs = 500;
    private const int MaxPendingHardwareEchoes = 32;
    private bool _hasLast;
    private int _lastKeyCode;
    private long _lastDownTime;
    private int _lastRepeatCount;
    private readonly Queue<PendingHardwareEcho> _pendingHardwareEchoes = new();

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
        RemoveExpiredEchoes(deliveredAt);
        if (_pendingHardwareEchoes.Count >= MaxPendingHardwareEchoes)
            _pendingHardwareEchoes.Dequeue();
        _pendingHardwareEchoes.Enqueue(new(codePoint, deliveredAt));
        return true;
    }

    public bool AcceptCommittedCodePoint(int codePoint, long deliveredAt)
    {
        RemoveExpiredEchoes(deliveredAt);
        if (_pendingHardwareEchoes.TryPeek(out var pending) &&
            deliveredAt >= pending.DeliveredAt && pending.CodePoint == codePoint)
        {
            _pendingHardwareEchoes.Dequeue();
            return false;
        }

        // CommitText preserves event order. A non-matching character means this is an
        // independent IME commit rather than the queued hardware echo, so abandon the stale
        // correlation instead of suppressing a legitimate matching character later.
        _pendingHardwareEchoes.Clear();
        return true;
    }

    private void RemoveExpiredEchoes(long deliveredAt)
    {
        while (_pendingHardwareEchoes.TryPeek(out var pending) &&
               (deliveredAt < pending.DeliveredAt ||
                deliveredAt - pending.DeliveredAt > ImeCommitSuppressionWindowMs))
            _pendingHardwareEchoes.Dequeue();
    }

    private readonly record struct PendingHardwareEcho(int CodePoint, long DeliveredAt);
}

/// <summary>Normalizes Android IME deletion requests without inventing a backspace for a no-op.</summary>
internal readonly record struct AndroidTextDeletionPlan(int Backspaces, int ForwardDeletes)
{
    public static AndroidTextDeletionPlan Create(int beforeLength, int afterLength) =>
        new(Math.Max(0, beforeLength), Math.Max(0, afterLength));
}
