using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Activity;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>活动中心“进行中”的一行。</summary>
public sealed partial class TaskItem(TaskHandle handle) : ObservableObject
{
    public TaskHandle Handle { get; } = handle;
    public string Title => Handle.Title;
    public double Percent => (Handle.Progress ?? 0) * 100;
    public bool Indeterminate => Handle.IsRunning && Handle.Progress is null;
    public bool IsRunning => Handle.IsRunning;
    public bool CanCancel => Handle.CanCancel;
    public bool IsFailed => Handle.State == TaskState.Failed;
    public bool IsDone => Handle.State is TaskState.Succeeded or TaskState.PartlySucceeded;

    /// <summary>The step under the bar while running ("12/30 files"); "Working…" when the task says nothing.</summary>
    public string Step => Handle.Detail ?? (Handle.Progress is null ? L.T("activity.task.working") : "");
    public double? Speed => Handle.BytesPerSecond;
    public TimeSpan? Remaining => Handle.Remaining;

    public string Detail => Handle.State switch
    {
        TaskState.Running => Handle.Detail ?? (Handle.Progress is { } p ? $"{p:P0}" : L.T("activity.task.working")),
        TaskState.Succeeded => Handle.Message ?? L.T("activity.task.done"),
        TaskState.PartlySucceeded => Handle.Message ?? L.T("activity.task.partly"),
        TaskState.Failed => L.F("activity.task.failed", Handle.Message ?? ""),
        _ => L.T("activity.task.cancelled"),
    };

    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Cancel() => Handle.Cancel();
}

/// <summary>活动中心“通知”的一行：Toast 和处理过的请求。</summary>
public sealed record NoticeItem(DateTimeOffset Time, string Text, NotificationType Level)
{
    public string TimeText => Fmt.Time(Time);
    public bool IsError => Level == NotificationType.Error;
}

/// <summary>活动中心“运行记录”的一行：一局游戏。</summary>
public sealed partial class RunItem(LaunchSession session) : ObservableObject
{
    public LaunchSession Session { get; } = session;
    public string Instance => Session.InstanceId;
    public string StartedText => Fmt.Time(Session.Started);
    public bool IsCrash => Session.State is LaunchState.Crashed or LaunchState.Failed;

    public string StateText => Session.State switch
    {
        // While starting: the step running now ("Prepare mods with the Jay loader 12/87", and when it is slow, what it waits on).
        LaunchState.Preparing or LaunchState.Entering when Session.Steps.Current is { } step => step.StatusLine(),
        LaunchState.Preparing => L.T("activity.run.preparing"),
        LaunchState.Entering => L.T("activity.run.entering"),
        LaunchState.Playing => L.F("activity.run.playing", (int)Session.PlayTime(DateTimeOffset.Now).TotalMinutes),
        LaunchState.Exiting => L.T("activity.run.exiting"),
        LaunchState.Exited => L.F("activity.run.exited", (int)Session.PlayTime(DateTimeOffset.Now).TotalMinutes),
        LaunchState.Crashed => L.T("activity.run.crashed"),
        LaunchState.Failed => L.T("activity.run.failed"),
        _ => L.T("activity.run.cancelled"),
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>请求条上一条请求的一个按钮。</summary>
public sealed partial class RequestButton(RequestItem owner, RequestChoice choice) : ObservableObject
{
    public RequestChoice Choice { get; } = choice;
    public string Label => Choice.Label;

    [RelayCommand]
    private void Choose() => owner.Choose(Choice.Id);
}

/// <summary>请求条上的一条请求。</summary>
public sealed partial class RequestItem : ObservableObject
{
    private readonly RequestCenter _center;

    public RequestItem(RequestCenter center, RequestHandle handle)
    {
        _center = center;
        Handle = handle;
        Buttons = handle.Spec.Choices.Select(c => new RequestButton(this, c)).ToList();
    }

    public RequestHandle Handle { get; }
    public string Title => Handle.Spec.Title;
    public string? Body => Handle.Spec.Body;
    public bool HasBody => !string.IsNullOrEmpty(Body);
    public bool IsSafety => Handle.Spec.IsSafety;
    public IReadOnlyList<RequestButton> Buttons { get; }

    public string RemainingText => Handle.Remaining(DateTimeOffset.Now) is { } r ? L.F("activity.request.remaining", (int)Math.Ceiling(r.TotalSeconds)) : "";
    public bool HasRemaining => Handle.Deadline is not null;

    public void Choose(string choice) => _center.Resolve(Handle, choice, RequestSource.Bar);

    public void Tick() => OnPropertyChanged(nameof(RemainingText));
}

/// <summary>
/// 活动中心（右侧抽屉）、请求条、正在玩胶囊（官方设计 §2.3、§6.22）。
/// 任务来自 LauncherService.Tasks，请求来自 LauncherService.Requests，一局一局的游戏来自启动页的 LaunchSession。
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public const int MaxNotices = 100;
    public const int MaxRuns = 30;

    public ObservableCollection<TaskItem> Tasks { get; } = [];
    public ObservableCollection<NoticeItem> Notices { get; } = [];
    public ObservableCollection<RunItem> Runs { get; } = [];
    public ObservableCollection<RequestItem> Requests { get; } = [];

    /// <summary>点胶囊、运行记录里的一行：去游戏库（实例页那批之后是那个实例的日志页签）。</summary>
    public Action<LaunchSession>? OpenRun { get; set; }

    public ActivityViewModel(LauncherService service)
    {
        _service = service;
        service.Tasks.Changed += _ => Dispatcher.UIThread.Post(SyncTasks);
        service.Tasks.Completed += t => Dispatcher.UIThread.Post(() =>
        {
            if (t.State == TaskState.Failed) HasUnseenFailure = true;
        });
        service.Requests.Posted += _ => Dispatcher.UIThread.Post(SyncRequests);
        service.Requests.Resolved += r => Dispatcher.UIThread.Post(() =>
        {
            SyncRequests();
            if (r.ResolvedBy != RequestSource.Program)
                AddNotice(L.F("activity.request.handled", r.Spec.Title), NotificationType.Information);
        });
        Toasts.Shown += AddNotice;
        _timer.Tick += (_, _) => Tick();
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var t in Tasks) t.Refresh();
            foreach (var r in Runs) r.Refresh();
            OnPropertyChanged(string.Empty);
        });
    }

    // ---- 抽屉 ----

    [ObservableProperty] public partial bool IsOpen { get; set; }

    /// <summary>0 进行中、1 通知、2 运行记录。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTasksTab), nameof(IsNoticesTab), nameof(IsRunsTab))]
    public partial int Tab { get; set; }

    public bool IsTasksTab => Tab == 0;
    public bool IsNoticesTab => Tab == 1;
    public bool IsRunsTab => Tab == 2;

    public Amber.App.Localization.OptionItem[] TabOptions { get; } = Amber.App.Localization.OptionItem.Keys("activity.tab.tasks", "activity.tab.notices", "activity.tab.runs");

    partial void OnIsOpenChanged(bool value)
    {
        if (value) HasUnseenFailure = false;
    }

    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void ClearFinished()
    {
        _service.Tasks.ClearFinished();
        SyncTasks();
    }

    [RelayCommand]
    private void ClearNotices()
    {
        Notices.Clear();
        OnPropertyChanged(nameof(HasNotices));
    }

    [RelayCommand]
    private void Open(RunItem run)
    {
        IsOpen = false;
        OpenRun?.Invoke(run.Session);
    }

    // ---- 顶栏活动按钮 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention))]
    public partial bool HasUnseenFailure { get; set; }

    /// <summary>红点：有没看过的失败，或有请求等你处理。</summary>
    public bool HasAttention => HasUnseenFailure || Requests.Count > 0;

    public bool HasRunningTasks => Tasks.Any(t => t.IsRunning);

    /// <summary>外圈进度环扫过的角度（0–360）。没有在跑的任务时是 0。</summary>
    public double RingSweep => (_service.Tasks.OverallProgress ?? 0) * 360;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasNotices => Notices.Count > 0;
    public bool HasRuns => Runs.Count > 0;
    public bool HasFinishedTasks => Tasks.Any(t => !t.IsRunning);

    public string ActivityTip => HasRunningTasks ? L.F("activity.tip.running", Tasks.Count(t => t.IsRunning)) : L.T("activity.tip");

    private void SyncTasks()
    {
        var all = _service.Tasks.All.Reverse().ToList();
        for (var i = Tasks.Count - 1; i >= 0; i--)
            if (!all.Contains(Tasks[i].Handle)) Tasks.RemoveAt(i);
        foreach (var (h, i) in all.Select((h, i) => (h, i)))
        {
            var existing = Tasks.FirstOrDefault(t => t.Handle == h);
            if (existing is null) Tasks.Insert(Math.Min(i, Tasks.Count), new TaskItem(h));
            else existing.Refresh();
        }
        RaiseIndicators();
    }

    private void RaiseIndicators()
    {
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(RingSweep));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasFinishedTasks));
        OnPropertyChanged(nameof(ActivityTip));
        OnPropertyChanged(nameof(HasAttention));
        UpdateTimer();
    }

    // ---- 通知 ----

    private void AddNotice(string text, NotificationType level)
    {
        Notices.Insert(0, new NoticeItem(DateTimeOffset.Now, text, level));
        while (Notices.Count > MaxNotices) Notices.RemoveAt(Notices.Count - 1);
        if (level == NotificationType.Error && !IsOpen) HasUnseenFailure = true;
        OnPropertyChanged(nameof(HasNotices));
    }

    // ---- 请求条 ----

    [ObservableProperty] public partial bool RequestsExpanded { get; set; }

    public RequestItem? FirstRequest => Requests.FirstOrDefault();
    public bool HasRequests => Requests.Count > 0;
    public bool HasMoreRequests => Requests.Count > 1;
    public string MoreRequestsText => L.F("activity.request.more", Requests.Count);

    [RelayCommand]
    private void ToggleRequests() => RequestsExpanded = !RequestsExpanded;

    /// <summary>Requests already on screen somewhere better (a knock while the room is open): left off the bar.</summary>
    public Func<RequestHandle, bool>? ShownElsewhere { get; set; }

    /// <summary>Re-read the requests (the page that shows some of them came or went).</summary>
    public void Resync() => SyncRequests();

    private void SyncRequests()
    {
        var pending = _service.Requests.Pending.Where(r => ShownElsewhere?.Invoke(r) != true).ToList();
        Requests.Clear();
        foreach (var r in pending) Requests.Add(new RequestItem(_service.Requests, r));
        if (Requests.Count <= 1) RequestsExpanded = false;
        OnPropertyChanged(nameof(FirstRequest));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(HasMoreRequests));
        OnPropertyChanged(nameof(MoreRequestsText));
        OnPropertyChanged(nameof(HasAttention));
        UpdateTimer();
    }

    // ---- 正在玩胶囊和运行记录 ----

    private LaunchSession? _pill;

    /// <summary>启动页开了一局。</summary>
    public void Track(LaunchSession session)
    {
        Runs.Insert(0, new RunItem(session));
        while (Runs.Count > MaxRuns) Runs.RemoveAt(Runs.Count - 1);
        session.Changed += _ => Dispatcher.UIThread.Post(() =>
        {
            Runs.FirstOrDefault(r => r.Session == session)?.Refresh();
            RaisePill();
        });
        session.Steps.Changed += () => Dispatcher.UIThread.Post(() => Runs.FirstOrDefault(r => r.Session == session)?.Refresh());
        _pill = null;
        OnPropertyChanged(nameof(HasRuns));
        RaisePill();
    }

    /// <summary>胶囊显示哪一局：有在跑的显示最新的那一局；都结束了时显示刚崩溃的那一局（点开或下一局开始后收起）。</summary>
    private LaunchSession? PillSession =>
        Runs.Select(r => r.Session).FirstOrDefault(s => s.IsActive)
        ?? (_pill is null ? Runs.Select(r => r.Session).FirstOrDefault(s => s.State == LaunchState.Crashed && DateTimeOffset.Now - s.Ended < TimeSpan.FromMinutes(30)) : null);

    public bool HasPill => PillSession is not null;
    public bool PillIsCrash => PillSession?.State == LaunchState.Crashed;
    public int ActiveRuns => Runs.Count(r => r.Session.IsActive);

    public string PillText
    {
        get
        {
            if (PillSession is not { } s) return "";
            if (ActiveRuns > 1) return L.F("activity.pill.many", ActiveRuns);
            var time = s.PlayTime(DateTimeOffset.Now);
            return s.State switch
            {
                LaunchState.Preparing => L.F("activity.pill.preparing", s.InstanceId),
                LaunchState.Entering => L.F("activity.pill.entering", s.InstanceId),
                LaunchState.Playing => L.F("activity.pill.playing", s.InstanceId, $"{(int)time.TotalMinutes}:{time.Seconds:D2}"),
                LaunchState.Exiting => L.F("activity.pill.exiting", s.InstanceId),
                _ => L.F("activity.pill.crashed", s.InstanceId),
            };
        }
    }

    [RelayCommand]
    private void OpenPill()
    {
        if (PillSession is not { } s) return;
        if (!s.IsActive) _pill = s; // 看过了：崩溃提示收起
        OpenRun?.Invoke(s);
        RaisePill();
    }

    private void RaisePill()
    {
        OnPropertyChanged(nameof(HasPill));
        OnPropertyChanged(nameof(PillIsCrash));
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(ActiveRuns));
        UpdateTimer();
    }

    // ---- 每秒：请求倒计时、胶囊时长 ----

    private void UpdateTimer()
    {
        var need = Requests.Any(r => r.HasRemaining) || Runs.Any(r => r.Session.IsActive);
        if (need && !_timer.IsEnabled) _timer.Start();
        else if (!need && _timer.IsEnabled) _timer.Stop();
    }

    private void Tick()
    {
        if (_service.Requests.Expire() > 0) SyncRequests();
        foreach (var r in Requests) r.Tick();
        foreach (var r in Runs.Where(r => r.Session.IsActive)) r.Refresh();
        RaisePill();
    }
}
