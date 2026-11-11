namespace Amber.Core.Tutorial;

/// <summary>What a tip is about; "Don't show tips like this" mutes one kind.</summary>
public enum TipKind { Content, Game, Multiplayer, Appearance }

/// <summary>One contextual tip (official design §9.3). The app decides which control the bubble points at.</summary>
public sealed record Tip(string Id, TipKind Kind, string TextKey);

/// <summary>The tip list from §9.3. Each one shows at most once.</summary>
public static class Tips
{
    public static readonly Tip ModsToggle = new("mods-toggle", TipKind.Content, "tip.mods_toggle");
    public static readonly Tip FirstCrash = new("first-crash", TipKind.Game, "tip.first_crash");
    public static readonly Tip FirstKnock = new("first-knock", TipKind.Multiplayer, "tip.first_knock");
    public static readonly Tip ThemeImport = new("theme-import", TipKind.Appearance, "tip.theme_import");
    public static readonly Tip NarrowWindow = new("narrow-window", TipKind.Appearance, "tip.narrow_window");

    public static readonly IReadOnlyList<Tip> All = [ModsToggle, FirstCrash, FirstKnock, ThemeImport, NarrowWindow];

    public static Tip? Get(string? id) => All.FirstOrDefault(t => t.Id == id);
}

/// <summary>What is remembered on this computer (the settings' <c>tutorial</c> section). "Show all tips again" clears it.</summary>
public sealed class TutorialState
{
    public List<string> Seen { get; set; } = [];
    public List<string> MutedKinds { get; set; } = [];
}

/// <summary>How a tip appears: a bubble at its anchor, or a one-line bar at the top of the content on short windows.</summary>
public enum TipMode { Bubble, Bar }

/// <summary>
/// The "don't interrupt" rules from §9.3, without any UI so they can be unit-tested:
/// 1. wait for <see cref="Idle"/> without input, and never while something busy is on screen (the caller says so);
/// 2. one tip at a time; 3. every tip can be dismissed, or its whole kind muted;
/// 4. two tips closed within <see cref="QuickCloseWindow"/> mutes tips for the rest of this run;
/// 5. windows shorter than <see cref="ShortWindow"/> get the bar instead of a bubble.
/// </summary>
public sealed class TipRules(TutorialState state, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan QuickCloseWindow = TimeSpan.FromSeconds(10);
    public const double ShortWindow = 560;

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset? _lastClose;

    public TutorialState State { get; } = state;

    /// <summary>The tip on screen, if any.</summary>
    public Tip? Current { get; private set; }

    /// <summary>Set after two quick closes; cleared only by a new run or <see cref="Reset"/>.</summary>
    public bool MutedThisRun { get; private set; }

    public bool IsSeen(Tip tip) => State.Seen.Contains(tip.Id);
    public bool IsKindMuted(TipKind kind) => State.MutedKinds.Contains(kind.ToString());

    /// <summary>Whether <paramref name="tip"/> may still show at some point (not seen, not muted).</summary>
    public bool Wants(Tip tip) => !MutedThisRun && !IsSeen(tip) && !IsKindMuted(tip.Kind);

    /// <summary>Whether it may show right now: wanted, nothing else on screen, idle long enough, nothing busy.</summary>
    public bool CanShow(Tip tip, TimeSpan sinceInput, bool busy) => Wants(tip) && Current is null && sinceInput >= Idle && !busy;

    public static TipMode ModeFor(double windowHeight, bool hasAnchor) => windowHeight < ShortWindow || !hasAnchor ? TipMode.Bar : TipMode.Bubble;

    /// <summary>Marks <paramref name="tip"/> as on screen and seen (it never shows again, even if the app closes now).</summary>
    public void Shown(Tip tip)
    {
        Current = tip;
        if (!State.Seen.Contains(tip.Id)) State.Seen.Add(tip.Id);
    }

    /// <summary>The tip was closed ("Got it", ×, or "Don't show tips like this" when <paramref name="muteKind"/>).</summary>
    public void Closed(bool muteKind = false)
    {
        if (Current is not { } tip) return;
        Current = null;
        if (muteKind && !IsKindMuted(tip.Kind)) State.MutedKinds.Add(tip.Kind.ToString());
        var now = _now();
        if (_lastClose is { } last && now - last <= QuickCloseWindow) MutedThisRun = true;
        _lastClose = now;
    }

    /// <summary>The tip went away without the user closing it (its page closed); doesn't count toward rule 4.</summary>
    public void Hidden() => Current = null;

    /// <summary>"Show all tips again" (help center): forget what was seen and muted.</summary>
    public void Reset()
    {
        State.Seen.Clear();
        State.MutedKinds.Clear();
        MutedThisRun = false;
        _lastClose = null;
    }
}
