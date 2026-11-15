namespace Amber.Core.Multiplayer.Files;

/// <summary>
/// How much one sender may hand over without being asked: files taken automatically from a checked friend count against a
/// per-sender allowance over a sliding window. Past it, the next offer goes to the request bar like anyone else's.
/// </summary>
public sealed class AutoAcceptBudget(int maxFiles = AutoAcceptBudget.DefaultFiles, long maxBytes = AutoAcceptBudget.DefaultBytes,
    TimeSpan? window = null, Func<DateTimeOffset>? clock = null)
{
    public const int DefaultFiles = 20;
    public const long DefaultBytes = 2L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Queue<(DateTimeOffset At, long Size)>> _taken = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Senders with anything still counted (tests).</summary>
    public int Senders { get { lock (_gate) { Prune(_clock()); return _taken.Count; } } }

    /// <summary>
    /// Counts a file of <paramref name="size"/> bytes against <paramref name="sender"/>'s allowance. False (and nothing
    /// counted) when it would go over, or when the size makes no sense.
    /// </summary>
    public bool TryTake(string sender, long size)
    {
        if (string.IsNullOrEmpty(sender) || size < 0 || size > maxBytes) return false;
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            if (!_taken.TryGetValue(sender, out var taken)) taken = new();
            if (taken.Count >= maxFiles || taken.Sum(t => t.Size) + size > maxBytes) return false;
            taken.Enqueue((now, size));
            _taken[sender] = taken;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) _taken.Clear();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (sender, taken) in _taken.ToList())
        {
            while (taken.Count > 0 && now - taken.Peek().At >= _window) taken.Dequeue();
            if (taken.Count == 0) _taken.Remove(sender);
        }
    }
}
