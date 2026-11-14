using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>The steps of one start, in the order the launch runs them.</summary>
public enum LaunchStepId
{
    /// <summary>Checking the game's files against the version's lists.</summary>
    Files,
    /// <summary>Downloading what was missing (skipped when nothing was).</summary>
    Download,
    /// <summary>Finding (or downloading) the Java this version needs.</summary>
    Java,
    /// <summary>Signing in: refreshing the Microsoft session, or the offline name.</summary>
    Account,
    /// <summary>The universal loader preparing the mods (skipped when it is off or nothing changed).</summary>
    Loader,
    /// <summary>The player's own pre-launch command (only shown when one is set).</summary>
    Command,
    /// <summary>The game sandbox: its level, and the one admin step the first strong launch needs.</summary>
    Sandbox,
    /// <summary>Starting the Java process.</summary>
    Start,
    /// <summary>Waiting until the game reaches its title screen.</summary>
    Window,
}

public enum LaunchStepState { Pending, Running, Done, Skipped, Failed }

/// <summary>
/// One row of the launch steps. Title is the localization key; Detail what this step is doing or did (a file name,
/// "standard sandbox"), Reason why it was skipped or failed. Done/Total only while it runs and has a count.
/// </summary>
public sealed record LaunchStep(LaunchStepId Id, LaunchStepState State, string? Detail = null, string? Reason = null,
    int? Done = null, int? Total = null, TimeSpan? Took = null, bool Slow = false)
{
    public string TitleKey => "launch.step." + Key(Id);

    /// <summary>What a slow step is waiting on, as a localization key.</summary>
    public string WaitingKey => "launch.step." + Key(Id) + ".waiting";

    public bool HasCount => State == LaunchStepState.Running && Total is > 0;

    /// <summary>Worth saying in the run log: the pre-launch command only when it ran.</summary>
    public bool Visible => Id != LaunchStepId.Command || State is LaunchStepState.Running or LaunchStepState.Done or LaunchStepState.Failed;

    /// <summary>"Prepare mods with the Jay loader 12/87": the title, and the count while it runs.</summary>
    public string Line => HasCount ? L.F("launch.step.count", L.T(TitleKey), Done!.Value, Total!.Value) : L.T(TitleKey);

    /// <summary>The line, plus "taking longer than usual · waiting on …" once the step is slow (progress row, activity drawer).</summary>
    public string StatusLine(string? extra = null)
    {
        var parts = new List<string> { extra is null ? Line : Line + " · " + extra };
        if (Slow && State == LaunchStepState.Running) parts.Add(L.F("launch.step.slow_line", L.T("launch.step.slow"), L.T(WaitingKey)));
        return string.Join(" · ", parts);
    }

    public string StateKey => State switch
    {
        LaunchStepState.Pending => "launch.step.state.pending",
        LaunchStepState.Running => "launch.step.state.running",
        LaunchStepState.Done => "launch.step.state.done",
        LaunchStepState.Skipped => "launch.step.state.skipped",
        _ => "launch.step.state.failed",
    };

    /// <summary>One line for the run log once the step ended: "Check Java: done (Using the Java chosen in settings)".</summary>
    public string LogLine()
    {
        var note = State is LaunchStepState.Skipped or LaunchStepState.Failed ? Reason : Detail;
        var head = L.T(TitleKey) + L.T("launch.step.log_separator") + L.T(StateKey);
        return string.IsNullOrEmpty(note) ? head : head + " (" + note + ")";
    }

    internal static string Key(LaunchStepId id) => id switch
    {
        LaunchStepId.Files => "files",
        LaunchStepId.Download => "download",
        LaunchStepId.Java => "java",
        LaunchStepId.Account => "account",
        LaunchStepId.Loader => "loader",
        LaunchStepId.Command => "command",
        LaunchStepId.Sandbox => "sandbox",
        LaunchStepId.Start => "start",
        _ => "window",
    };
}

/// <summary>
/// The steps of one start, fed from the launch as it runs (it changes nothing about how the launch works). Starting a
/// step finishes the one before it; a step never started by the end of the launch is left pending. Not thread-safe:
/// the launch reports on one thread (the UI thread in Jay).
/// </summary>
public sealed class LaunchSteps
{
    private readonly LaunchStep[] _steps = Enum.GetValues<LaunchStepId>().Select(id => new LaunchStep(id, LaunchStepState.Pending)).ToArray();
    private readonly Func<DateTimeOffset> _now;
    private readonly LaunchStepTimings? _timings;
    private readonly string? _instance;
    private DateTimeOffset _started, _lastChange;
    private long _bytes;
    private bool _ended;

    public LaunchSteps(Func<DateTimeOffset>? now = null, LaunchStepTimings? timings = null, string? instance = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _timings = timings;
        _instance = instance;
    }

    /// <summary>A step changed (state, detail, count, or became slow).</summary>
    public event Action? Changed;

    public IReadOnlyList<LaunchStep> Steps => _steps;

    public LaunchStep this[LaunchStepId id] => _steps[(int)id];

    /// <summary>The step running now, else null.</summary>
    public LaunchStep? Current => _steps.FirstOrDefault(s => s.State == LaunchStepState.Running);

    /// <summary>The start ended in a failure on one of the steps.</summary>
    public bool Failed => _steps.Any(s => s.State == LaunchStepState.Failed);

    /// <summary>Starts a step. A step still running before it counts as done.</summary>
    public void Begin(LaunchStepId id, string? detail = null)
    {
        // Steps only move forward: a finished step does not run again, and nothing runs after a cancel or a failure.
        if (_ended || IsFinished(this[id].State)) return;
        if (this[id].State == LaunchStepState.Running) { Detail(id, detail); return; }
        if (Current is { } running && running.Id != id) Finish(running.Id, LaunchStepState.Done, null, null);
        _started = _lastChange = _now();
        _bytes = 0;
        Set(id, new LaunchStep(id, LaunchStepState.Running, detail));
    }

    /// <summary>What the running step is doing now. Ignored for a step that is not running.</summary>
    public void Detail(LaunchStepId id, string? detail)
    {
        var step = this[id];
        if (step.State != LaunchStepState.Running || step.Detail == detail) return;
        _lastChange = _now();
        Set(id, step with { Detail = detail, Slow = false });
    }

    /// <summary>
    /// A count for the running step (12 of 87); starts it when it is not running yet. A late report (for a step that
    /// already finished, one before a step that has started since, or after a cancel or a failure) is ignored.
    /// <paramref name="bytes"/> is how much a download has received so far: a big file that is still arriving is not
    /// a stall even while the file count stays the same.
    /// </summary>
    public void Count(LaunchStepId id, int done, int total, string? detail = null, long? bytes = null)
    {
        if (_ended || IsFinished(this[id].State)) return;
        if (this[id].State == LaunchStepState.Pending && _steps.Skip((int)id + 1).Any(s => s.State != LaunchStepState.Pending)) return;
        if (this[id].State != LaunchStepState.Running) Begin(id, detail);
        var step = this[id];
        if (bytes is { } b && b > _bytes)
        {
            _bytes = b;
            _lastChange = _now();
        }
        if (step.Done == done && step.Total == total && (detail is null || step.Detail == detail)) return;
        _lastChange = _now();
        Set(id, step with { Done = done, Total = total, Detail = detail ?? step.Detail, Slow = false });
    }

    private static bool IsFinished(LaunchStepState state) => state is LaunchStepState.Done or LaunchStepState.Skipped or LaunchStepState.Failed;

    public void Done(LaunchStepId id, string? detail = null) => Finish(id, LaunchStepState.Done, detail, null);

    /// <summary>The step did not need doing, and why ("mods unchanged").</summary>
    public void Skip(LaunchStepId id, string reason) => Finish(id, LaunchStepState.Skipped, null, reason);

    /// <summary>The start stopped here, with one short reason (the fixes are elsewhere).</summary>
    public void Fail(string reason)
    {
        var at = Current?.Id ?? _steps.LastOrDefault(s => s.State != LaunchStepState.Pending)?.Id ?? LaunchStepId.Files;
        Finish(at, LaunchStepState.Failed, null, reason);
        _ended = true;
    }

    /// <summary>The player cancelled: the running step stops with that reason, the rest stay pending.</summary>
    public void Cancel(string reason)
    {
        if (Current is { } running) Finish(running.Id, LaunchStepState.Skipped, null, reason);
        _ended = true;
    }

    private void Finish(LaunchStepId id, LaunchStepState state, string? detail, string? reason)
    {
        var step = this[id];
        if (step.State is LaunchStepState.Done or LaunchStepState.Skipped or LaunchStepState.Failed && state != LaunchStepState.Failed) return;
        // A step finished without ever running (skipped up front, or done in no time) still finishes the one before it.
        if (step.State == LaunchStepState.Pending && Current is { } running && running.Id != id) Finish(running.Id, LaunchStepState.Done, null, null);
        TimeSpan? took = null;
        if (step.State == LaunchStepState.Running)
        {
            took = _now() - _started;
            if (state == LaunchStepState.Done && _instance is not null) _timings?.Record(_instance, id, took.Value);
        }
        Set(id, new LaunchStep(id, state, detail ?? (state == LaunchStepState.Done ? step.Detail : null), reason, Took: took));
    }

    /// <summary>
    /// Called every second or so: marks the running step slow once it has run well past its usual time (or, for a
    /// download, once nothing has moved for a while). True when that changed something.
    /// </summary>
    public bool Tick()
    {
        if (Current is not { Slow: false } step) return false;
        var now = _now();
        var usual = _timings?.Usual(_instance, step.Id) ?? LaunchStepTimings.Floor(step.Id);
        var slow = step.Id == LaunchStepId.Download
            ? now - _lastChange >= LaunchStepTimings.Stalled
            : now - _started >= usual;
        if (!slow) return false;
        Set(step.Id, step with { Slow = true });
        return true;
    }

    private void Set(LaunchStepId id, LaunchStep step)
    {
        _steps[(int)id] = step;
        Changed?.Invoke();
    }
}

/// <summary>
/// How long each step took on the last starts of each instance, so "this is taking longer than usual" means usual for
/// this instance. Kept in memory for the session; a step is slow at twice its usual time and never before its floor.
/// </summary>
public sealed class LaunchStepTimings
{
    private readonly Dictionary<(string, LaunchStepId), TimeSpan> _last = [];
    private readonly object _lock = new();

    /// <summary>A download with no progress for this long is waiting on the server.</summary>
    public static readonly TimeSpan Stalled = TimeSpan.FromSeconds(20);

    /// <summary>The time before which a step is never called slow.</summary>
    public static TimeSpan Floor(LaunchStepId id) => TimeSpan.FromSeconds(id switch
    {
        LaunchStepId.Files => 20,
        LaunchStepId.Download => 120,
        LaunchStepId.Java => 30,
        LaunchStepId.Account => 15,
        LaunchStepId.Loader => 90,
        LaunchStepId.Command => 30,
        LaunchStepId.Sandbox => 30,
        LaunchStepId.Start => 15,
        _ => 120,
    });

    public void Record(string instance, LaunchStepId id, TimeSpan took)
    {
        lock (_lock) _last[(instance.ToLowerInvariant(), id)] = took;
    }

    /// <summary>When a running step counts as slow: twice the last time, at least its floor.</summary>
    public TimeSpan Usual(string? instance, LaunchStepId id)
    {
        var floor = Floor(id);
        if (instance is null) return floor;
        lock (_lock)
            return _last.TryGetValue((instance.ToLowerInvariant(), id), out var last) && last * 2 > floor ? last * 2 : floor;
    }
}
