using Amber.Core.Tutorial;

namespace Amber.App.Services;

/// <summary>
/// Contextual tips (official design §9.3). View models call <see cref="Request"/> when a "first time" happens; the main
/// window decides when and how to show it under <see cref="TipRules"/> (idle 800 ms, nothing busy, one at a time, quick
/// closes mute the run, bar on short windows). Seen tips and muted kinds live in the settings' <c>tutorial</c> section.
/// </summary>
public static class TutorialService
{
    private static readonly List<Tip> Queue = [];
    private static readonly Dictionary<string, DateTimeOffset> Requested = [];

    /// <summary>The rules for this run; null until the main window attaches (tests and the CLI have none).</summary>
    public static TipRules? Rules { get; private set; }

    /// <summary>The GUI self-check turns tips off outside its own scope, so they don't cover other screenshots.</summary>
    internal static bool Suspended { get; set; }

    /// <summary>Saves the settings after a tip is shown, closed or reset.</summary>
    internal static Action? Persist { get; set; }

    internal static void Attach(TutorialState state, Action persist)
    {
        Rules = new TipRules(state);
        Persist = persist;
        Queue.Clear();
        Requested.Clear();
    }

    /// <summary>Something happened for the first time; the tip shows when the rules allow (or never, if seen or muted).</summary>
    public static void Request(Tip tip)
    {
        if (Rules is not { } rules || !rules.Wants(tip) || Queue.Contains(tip) || rules.Current == tip) return;
        Queue.Add(tip);
        Requested[tip.Id] = DateTimeOffset.UtcNow;
    }

    /// <summary>The oldest waiting tip and when it was asked for; tips that waited over a minute are dropped (the moment has passed).</summary>
    internal static (Tip Tip, DateTimeOffset At)? Next()
    {
        var now = DateTimeOffset.UtcNow;
        Queue.RemoveAll(t => Rules?.Wants(t) != true || now - Requested[t.Id] > TimeSpan.FromMinutes(1));
        return Queue.Count == 0 ? null : (Queue[0], Requested[Queue[0].Id]);
    }

    internal static void Showing(Tip tip)
    {
        Queue.Remove(tip);
        Rules?.Shown(tip);
        Persist?.Invoke();
    }

    internal static void Closed(bool muteKind)
    {
        Rules?.Closed(muteKind);
        Persist?.Invoke();
    }

    internal static void Hidden() => Rules?.Hidden();

    /// <summary>Help center › "Show all tips again".</summary>
    public static void ResetAll()
    {
        Rules?.Reset();
        Queue.Clear();
        Persist?.Invoke();
    }

    /// <summary>For the self-check: forget the waiting queue.</summary>
    internal static void ClearQueue() => Queue.Clear();
}
