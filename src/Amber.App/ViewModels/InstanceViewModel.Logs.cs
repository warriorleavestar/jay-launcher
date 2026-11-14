using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One run in the Logs tab's run list: "✕ Today 20:14 · Crashed", "● Now · Playing 12 min".</summary>
public sealed class LogRunRow(RunRecord record, bool live)
{
    public RunRecord Record { get; } = record;
    public bool IsLive { get; } = live;
    public bool IsMissing => !IsLive && !Record.LogExists;

    public string Glyph => IsLive ? "●" : Record.Outcome switch
    {
        RunOutcome.Crashed or RunOutcome.Failed => "✕",
        RunOutcome.Stopped => "■",
        _ => "✓",
    };

    public string Label
    {
        get
        {
            if (IsLive) return L.F("logs.run.live", Fmt.Minutes(DateTimeOffset.Now - Record.StartedAt));
            var when = Fmt.Ago(Record.StartedAt, DateTimeOffset.Now);
            var tail = Record.Outcome switch
            {
                RunOutcome.Crashed => L.T("logs.run.crashed"),
                RunOutcome.Failed => L.T("logs.run.failed"),
                RunOutcome.Stopped => L.T("logs.run.stopped"),
                _ => Record.Duration is { } d ? Fmt.Duration(d) : L.T("logs.run.exited"),
            };
            return IsMissing ? L.F("logs.run.missing", when) : $"{when} · {tail}";
        }
    }

    public string Tip => Record.StartedAt.ToLocalTime().ToString("g", L.Culture) + (Record.Summary is { Length: > 0 } s ? " · " + s : "");
}

/// <summary>
/// One row of the log: a line, its folded stack trace, or a repeat count. Rows are kept across refreshes of a live
/// run, so the labels follow a language change (<see cref="LocalizedObservable"/> holds the row only weakly).
/// </summary>
public sealed partial class LogRow(LogEvent e, long sequence) : LocalizedObservable
{
    public LogEvent Event { get; } = e;

    /// <summary>
    /// The position of the event's first line in the run's log, counted from the run's start, so it doesn't shift
    /// when old lines roll out of a full live log. Two events with the same text and time still differ here.
    /// </summary>
    internal long Sequence { get; } = sequence;
    public string Time => Event.Line.Time == DateTimeOffset.MinValue ? "" : Event.Line.Time.ToLocalTime().ToString("HH:mm:ss", L.Culture);
    public string Tag => Event.Line.FromJay ? "JAY" : Event.Line.Level.ToString().ToUpperInvariant();
    public string Message => Event.Line.Message;
    public bool IsError => Event.IsError;
    public bool IsWarning => Event.IsWarning;
    public bool IsJay => Event.Line.FromJay;
    public bool IsMuted => !IsJay && Event.Line.Level <= GameLogLevel.Debug;
    public bool HasStack => Event.HasStack;
    public string StackLabel => L.P(Expanded ? "logs.stack.hide" : "logs.stack.show", Event.Stack.Count, Event.Stack.Count);
    public string StackText => string.Join("\n", Event.Stack);
    public bool HasRepeats => Event.Repeats > 1;
    public string RepeatsLabel => "×" + Event.Repeats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StackLabel))]
    public partial bool Expanded { get; set; }

    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    internal string CopyText => $"{Time} {Tag} {Message}" + (HasStack ? "\n" + StackText : "") + (HasRepeats ? $" ({RepeatsLabel})" : "");
}

/// <summary>
/// Instance › Logs (official design §6.9): one log per run, kept across restarts; the run list (wide) or a picker,
/// a diagnosis line for crashes, filters (all / warnings and errors / errors only), search, error counts with
/// jump to next / previous (F8 / Shift+F8), follow latest, copy all, and [Export error report…] behind a locked
/// privacy preview. Stack traces fold into one row and repeated warnings show ×N.
/// </summary>
public sealed partial class InstanceViewModel
{
    public ObservableCollection<LogRunRow> LogRuns { get; } = [];
    public ObservableCollection<LogRow> LogRows { get; } = [];

    /// <summary>The live run's in-memory log for an instance (set by the main view model).</summary>
    public Func<string, RunLog?>? LiveLogFor { get; set; }

    /// <summary>Opens a file picker and writes the report there (set by the view). Returns the path or null.</summary>
    public Func<string, Task<string?>>? SaveReportAs { get; set; }

    /// <summary>Copies text to the clipboard (set by the view).</summary>
    public Func<string, Task<bool>>? CopyText { get; set; }

    /// <summary>Shows the locked privacy preview (set by the view): Confirm = save to file, Alternate = copy.</summary>
    public Func<Task<Amber.App.Controls.Safety.SafetyChoice>>? AskExportReport { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogRun), nameof(LogDiagnosis), nameof(HasLogDiagnosis), nameof(IsLiveRun), nameof(HasCrashReport))]
    public partial LogRunRow? SelectedLogRun { get; set; }

    public bool HasLogRun => SelectedLogRun is not null;
    public bool HasLogRuns => LogRuns.Count > 0;
    public bool IsLiveRun => SelectedLogRun?.IsLive == true;
    public bool HasCrashReport => SelectedLogRun?.Record.CrashReport is { } p && File.Exists(p);

    /// <summary>"Diagnosis: …" for a run that crashed or failed with a known reason.</summary>
    public string LogDiagnosis => SelectedLogRun is { IsLive: false, Record: { Outcome: RunOutcome.Crashed or RunOutcome.Failed, Summary: { Length: > 0 } s } }
        ? L.F("logs.diagnosis", s) : "";

    public bool HasLogDiagnosis => LogDiagnosis.Length > 0;

    /// <summary>No-trace mode: chat is not written to logs (said at the top of the tab).</summary>
    public bool NoTraceOn => _service.Settings.NoTrace;

    public OptionItem[] LogFilters { get; } = OptionItem.Keys("logs.filter.all", "logs.filter.problems", "logs.filter.errors");

    [ObservableProperty]
    public partial int LogFilterIndex { get; set; }

    [ObservableProperty]
    public partial string LogSearch { get; set; } = "";

    [ObservableProperty]
    public partial bool LogFollow { get; set; } = true;

    [ObservableProperty]
    public partial LogRow? SelectedLogRow { get; set; }

    public int LogErrorCount { get; private set; }
    public int LogWarningCount { get; private set; }
    public string LogErrorBadge => "✕ " + LogErrorCount;
    public string LogWarningBadge => "⚠ " + LogWarningCount;
    public bool LogEmpty => LogRows.Count == 0;
    public string LogEmptyText => L.T(!HasLogRuns ? "logs.empty" : SelectedLogRun?.IsMissing == true ? "logs.missing" : _allEvents.Count > 0 ? "logs.empty_filtered" : "logs.empty_run");

    /// <summary>The view scrolls to this row (jump to error, follow latest).</summary>
    public event Action<LogRow>? LogJumpRequested;

    private List<LogEvent> _allEvents = [];

    /// <summary>The sequence number of <c>_allEvents</c>' line 0 (lines that rolled out of a full live log before it).</summary>
    private long _eventBase;
    private long _liveRevision = -1;
    private DispatcherTimer? _liveTimer;

    partial void OnSelectedLogRunChanged(LogRunRow? value) => LoadLogRun();
    partial void OnLogFilterIndexChanged(int value) => RebuildLogRows();
    partial void OnLogSearchChanged(string value) => RebuildLogRows();

    partial void OnLogFollowChanged(bool value)
    {
        if (value && LogRows.Count > 0) LogJumpRequested?.Invoke(LogRows[^1]);
    }

    /// <summary>Lists this instance's runs (live first) and shows the newest.</summary>
    internal void LoadLogs()
    {
        var keep = SelectedLogRun?.Record.LogPath;
        LogRuns.Clear();
        if (Current is { } c)
        {
            var live = LiveLogFor?.Invoke(c.Id);
            IReadOnlyList<RunRecord> saved;
            try { saved = RunLogStore.List(c.WorkDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { saved = []; }
            foreach (var record in saved)
                LogRuns.Add(new LogRunRow(record, live?.Store is { } store && store.Record.LogPath == record.LogPath && record.Outcome == RunOutcome.Running));
        }
        OnPropertyChanged(nameof(HasLogRuns));
        OnPropertyChanged(nameof(NoTraceOn));
        SelectedLogRun = LogRuns.FirstOrDefault(r => r.Record.LogPath == keep) ?? LogRuns.FirstOrDefault();
        if (SelectedLogRun is null) LoadLogRun();
        UpdateLiveTimer();
    }

    /// <summary>A run began or ended in this instance: refresh the list if the tab is open.</summary>
    public void OnRunsChanged(string instanceId)
    {
        if (Current?.Id != instanceId || !_loadedTabs.Contains((int)InstanceTab.Logs)) return;
        SelectedLogRun = null;
        LoadLogs();
    }

    private void LoadLogRun()
    {
        _allEvents = [];
        _eventBase = 0;
        _liveRevision = -1;
        if (SelectedLogRun is { } run)
        {
            if (run.IsLive && Current is { } c && LiveLogFor?.Invoke(c.Id) is { } live)
            {
                _allEvents = LogEvents.Group(LiveLines(live));
                _eventBase = live.Dropped;
                _liveRevision = live.Revision;
            }
            else if (!run.IsMissing)
            {
                try { _allEvents = LogEvents.Group(RunLogStore.Read(run.Record.LogPath)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        RebuildLogRows();
        UpdateLiveTimer();
    }

    private static List<StoredLogLine> LiveLines(RunLog live) =>
        [.. live.All.Select(e => new StoredLogLine(e.Timestamp, e.Severity, e.Message, e.Level == "JAY"))];

    private bool Shows(LogEvent e)
    {
        var level = LogFilterIndex switch
        {
            1 => e.IsError || e.IsWarning,
            2 => e.IsError,
            _ => true,
        };
        return level && (LogSearch.Length == 0 || e.Line.Message.Contains(LogSearch, StringComparison.OrdinalIgnoreCase)
            || e.Stack.Any(s => s.Contains(LogSearch, StringComparison.OrdinalIgnoreCase)));
    }

    private void RebuildLogRows()
    {
        // A live run regroups its whole log once a second. Rows whose event didn't change are kept as they are (an
        // opened stack stays open, the list isn't redone); a changed event (more stack lines, a higher ×N) gets a new
        // row that keeps the old one's expanded state.
        // Rows are matched by the first line's sequence number in the run (which doesn't shift when old lines roll
        // out of a full live log) together with the line itself (so another run's row at the same position isn't
        // taken). The line alone isn't enough: the same error twice in one millisecond, each with its own stack, has
        // two equal first lines.
        var old = new Dictionary<(long, StoredLogLine), LogRow>();
        foreach (var r in LogRows) old.TryAdd((r.Sequence, r.Event.Line), r);
        var selected = SelectedLogRow;
        LogRow? selectedAgain = null;
        var rows = new List<LogRow>();
        var kept = new HashSet<LogRow>(ReferenceEqualityComparer.Instance);
        foreach (var e in _allEvents.Where(Shows))
        {
            var sequence = _eventBase + e.Index;
            old.Remove((sequence, e.Line), out var before);
            LogRow row;
            if (before is not null && before.Event.Repeats == e.Repeats && before.Event.Stack.SequenceEqual(e.Stack))
            {
                row = before;
                kept.Add(row);
            }
            else row = new LogRow(e, sequence) { Expanded = before?.Expanded == true };
            if (before is not null && ReferenceEquals(before, selected)) selectedAgain = row;
            rows.Add(row);
        }
        SyncLogRows(rows, kept);
        if (selected is not null)
        {
            if (selectedAgain is not null) selectedAgain.IsHighlighted = true;
            SelectedLogRow = selectedAgain;
        }
        LogErrorCount = _allEvents.Where(e => e.IsError).Sum(e => e.Repeats);
        LogWarningCount = _allEvents.Where(e => e.IsWarning).Sum(e => e.Repeats);
        OnPropertyChanged(nameof(LogErrorBadge));
        OnPropertyChanged(nameof(LogWarningBadge));
        OnPropertyChanged(nameof(LogEmpty));
        OnPropertyChanged(nameof(LogEmptyText));
        if (LogFollow && LogRows.Count > 0) LogJumpRequested?.Invoke(LogRows[^1]);
    }

    /// <summary>
    /// Makes <see cref="LogRows"/> equal to <paramref name="rows"/> with the fewest changes: rows that left are removed and
    /// new ones inserted where they belong, so the list only redraws what changed.
    /// </summary>
    private void SyncLogRows(List<LogRow> rows, HashSet<LogRow> kept)
    {
        if (kept.Count == 0)
        {
            LogRows.Clear();
            foreach (var r in rows) LogRows.Add(r);
            return;
        }
        for (var i = LogRows.Count - 1; i >= 0; i--)
            if (!kept.Contains(LogRows[i])) LogRows.RemoveAt(i);
        for (var k = 0; k < rows.Count; k++)
        {
            if (k < LogRows.Count && ReferenceEquals(LogRows[k], rows[k])) continue;
            if (kept.Contains(rows[k]))
            {
                // The kept rows came back in another order (shouldn't happen for a log): start over.
                LogRows.Clear();
                foreach (var r in rows) LogRows.Add(r);
                return;
            }
            LogRows.Insert(k, rows[k]);
        }
    }

    /// <summary>While a live run is shown, new lines come in once a second.</summary>
    private void UpdateLiveTimer()
    {
        var want = IsLiveRun && IsCurrent && TabIndex == (int)InstanceTab.Logs;
        if (want && _liveTimer is null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += (_, _) => PollLive();
            _liveTimer.Start();
        }
        else if (!want && _liveTimer is not null)
        {
            _liveTimer.Stop();
            _liveTimer = null;
        }
    }

    internal void PollLive()
    {
        if (!IsCurrent || TabIndex != (int)InstanceTab.Logs)
        {
            UpdateLiveTimer();
            return;
        }
        if (!IsLiveRun || Current is not { } c || LiveLogFor?.Invoke(c.Id) is not { } live || live.Revision == _liveRevision) return;
        _liveRevision = live.Revision;
        _allEvents = LogEvents.Group(LiveLines(live));
        _eventBase = live.Dropped;
        RebuildLogRows();
    }

    [RelayCommand]
    private void NextLogError() => JumpLogError(1);

    [RelayCommand]
    private void PreviousLogError() => JumpLogError(-1);

    /// <summary>F8 / Shift+F8: the next / previous error among the shown rows (wraps around).</summary>
    public void JumpLogError(int direction)
    {
        var errors = LogRows.Where(r => r.IsError).ToList();
        if (errors.Count == 0) return;
        var at = SelectedLogRow is { } row ? LogRows.IndexOf(row) : direction > 0 ? -1 : LogRows.Count;
        var target = direction > 0
            ? errors.FirstOrDefault(r => LogRows.IndexOf(r) > at) ?? errors[0]
            : errors.LastOrDefault(r => LogRows.IndexOf(r) < at) ?? errors[^1];
        LogFollow = false;
        if (SelectedLogRow is { } old) old.IsHighlighted = false;
        target.IsHighlighted = true;
        SelectedLogRow = target;
        LogJumpRequested?.Invoke(target);
    }

    [RelayCommand]
    private void ToggleStack(LogRow? row)
    {
        if (row is not null) row.Expanded = !row.Expanded;
    }

    [RelayCommand]
    private async Task CopyAllLogAsync()
    {
        if (CopyText is null) return;
        var ok = await CopyText(string.Join("\n", LogRows.Select(r => r.CopyText)));
        SetStatus(ok ? StatusMessage.Success("common.copied") : StatusMessage.Warning("log.copy_failed"));
    }

    [RelayCommand]
    private void OpenCrashReport()
    {
        if (SelectedLogRun?.Record.CrashReport is { } path && File.Exists(path)) OpenPath(path);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (Current is { } c)
        {
            OpenPath(RunLogStore.Dir(c.WorkDir));
        }
    }

    /// <summary>The cleaned report for the shown run (what the preview describes).</summary>
    internal string BuildErrorReport()
    {
        if (Current is not { } c) return "";
        var lines = _allEvents.SelectMany(e => new[] { e.Line }.Concat(e.Stack.Select(s => e.Line with { Message = s })))
            .ToList();
        IReadOnlyList<string> mods;
        try
        {
            mods = System.IO.Directory.Exists(c.ModsDir)
                ? [.. System.IO.Directory.EnumerateFiles(c.ModsDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { mods = []; }
        string? crash = null;
        if (SelectedLogRun?.Record.CrashReport is { } path && File.Exists(path))
        {
            try { crash = File.ReadAllText(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var loader = c.Loader is null ? null : $"{c.Loader} {c.LoaderVersion}";
        return ErrorReport.Build(new ErrorReportInput(c.Id, c.McVersion, loader, mods, lines, null, null, crash));
    }

    /// <summary>[Export error report…]: the locked privacy preview first, then save to a file or copy.</summary>
    [RelayCommand]
    private async Task ExportErrorReportAsync()
    {
        if (Current is not { } c || AskExportReport is null) return;
        var choice = await AskExportReport();
        if (choice == Amber.App.Controls.Safety.SafetyChoice.Keep) return;
        var report = BuildErrorReport();
        if (choice == Amber.App.Controls.Safety.SafetyChoice.Alternate)
        {
            var ok = CopyText is not null && await CopyText(report);
            SetStatus(ok ? StatusMessage.Success("logs.report.copied") : StatusMessage.Warning("log.copy_failed"));
            return;
        }
        var name = $"jay-report-{c.Id}-{DateTime.Now:yyyyMMdd-HHmm}.txt";
        if (SaveReportAs is null || await SaveReportAs(name) is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, report);
            SetStatus(StatusMessage.Success("logs.report.saved", Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("logs.report.failed", e, ErrorText.Of(e)));
        }
    }
}
