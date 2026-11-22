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

        // A key is allowed to have no CommitText echo. If the newest hardware character is
        // echoed, discard the older unechoed candidates and consume that newest correlation.
        // Do not search arbitrary middle entries by character: only strict FIFO order or the
        // latest event can establish which physical key this otherwise anonymous commit follows.
        if (_pendingHardwareEchoes.Count > 1)
        {
            var newest = _pendingHardwareEchoes.Last();
            if (deliveredAt >= newest.DeliveredAt && newest.CodePoint == codePoint)
            {
                _pendingHardwareEchoes.Clear();
                return false;
            }
        }

        // A commit matching neither the ordered head nor the newest event is independent IME
        // input, so abandon stale correlations instead of suppressing a matching character later.
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
