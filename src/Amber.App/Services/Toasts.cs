using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Amber.App.Services;

/// <summary>
/// 右下角的 Toast（改版规格 5.4）：最多 3 条。删除后显示“已删除 X【撤销】”8 秒；
/// Toast 消失后，本次运行里删过的东西还能用 Ctrl+Z（最近一次）撤销。
/// 文件进回收目录的用 Deleted；列表里的一项（服务器、节点）用 Removed，自己给出怎么放回去。
/// </summary>
public static class Toasts
{
    public static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(8);

    private static WindowNotificationManager? _manager;
    private static Views.Controls.BakeNoticeState? _bakeHost;
    internal static Views.Controls.BakeNoticeState? BakeNotices => _bakeHost;

    /// <summary>
    /// 一条撤销记录：Toast 上显示的名字，撤销时做的事（返回恢复后的名字，失败抛异常），
    /// 和恢复成功后刷新界面的事（它出错不算恢复失败）。
    /// </summary>
    private sealed record UndoItem(string Name, Func<string> Undo, Action? AfterUndo = null)
    {
        /// <summary>正在撤销（按钮和 Ctrl+Z 同时来时只做一次）。</summary>
        public bool Running { get; set; }

        /// <summary>上次撤销失败了：Ctrl+Z 先跳过它去撤销更早的，Toast 上的按钮还能再试。</summary>
        public bool Failed { get; set; }
    }

    /// <summary>撤销记录：最近的在最后。</summary>
    private static readonly List<UndoItem> History = [];

    public static void Attach(Window window, Views.Controls.BakeNoticeState? bakeHost = null)
    {
        // Bottom right, above the launch bar, at most 3: top right would cover the request bar and the activity button.
        _manager = new WindowNotificationManager(window) { Position = NotificationPosition.BottomRight, MaxItems = 3, Margin = new Avalonia.Thickness(0, 0, 0, 96) };
        _bakeHost?.Clear();
        _bakeHost = bakeHost;
        window.Closed += (_, _) =>
        {
            bakeHost?.Clear();
            if (ReferenceEquals(_bakeHost, bakeHost)) _bakeHost = null;
        };
    }

    /// <summary>烘焙后的早期崩溃使用启动状态框内的提示，保留八秒的重试机会。</summary>
    public static void BakeFailed(string text, Action retry)
    {
        var host = _bakeHost;
        Dispatcher.UIThread.Post(() =>
        {
            if (host is not null && ReferenceEquals(_bakeHost, host)) host.Show(text, retry);
        });
    }

    /// <summary>A bake notice whose button does something other than retry (download the missing loader).</summary>
    public static void BakeAction(string text, string actionText, string? actionTip, Action action)
    {
        var host = _bakeHost;
        if (host is null) { Show(text); return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_bakeHost, host)) host.Show(text, action, actionText, actionTip);
        });
    }

    /// <summary>A bake problem with nothing to retry (the game already launched normally, or nothing was launched).</summary>
    public static void BakeProblem(string text)
    {
        var host = _bakeHost;
        if (host is null) { Show(text); return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_bakeHost, host)) host.Show(text, null);
        });
    }

    /// <summary>开始处理新的启动请求后，旧失败提示不能遮住这次的状态或留下过时重试入口。</summary>
    internal static void DismissBakeFailures() => _bakeHost?.Clear();

    /// <summary>每条 Toast 显示时触发（活动中心“通知”里回看）。在界面线程上。</summary>
    public static event Action<string, NotificationType>? Shown;

    /// <summary>出错的 Toast 不自动消失（§6.22），其余 5 秒。</summary>
    internal static TimeSpan DurationFor(NotificationType type, TimeSpan normal) => type == NotificationType.Error ? TimeSpan.Zero : normal;

    public static void Show(string text, NotificationType type = NotificationType.Information) =>
        Dispatcher.UIThread.Post(() =>
        {
            Shown?.Invoke(text, type);
            _manager?.Show(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 }, type, DurationFor(type, TimeSpan.FromSeconds(5)));
        });

    /// <summary>带一个按钮的 Toast（比如安装完成后的“打开实例”）。出错级的不自动消失。</summary>
    public static void WithAction(string text, string actionLabel, Action action, NotificationType type = NotificationType.Success) =>
        Dispatcher.UIThread.Post(() =>
        {
            Shown?.Invoke(text, type);
            if (_manager is null) return;
            var button = new Button { Content = actionLabel, Classes = { "secondary" }, Margin = new Avalonia.Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var content = new DockPanel
            {
                Children =
                {
                    button,
                    new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            DockPanel.SetDock(button, Dock.Right);
            button.Click += (_, _) => action();
            _manager.Show(content, type, DurationFor(type, UndoDuration));
        });

    /// <summary>
    /// 文件进回收目录后调用：记下撤销信息并显示 Toast。restored 收到实际恢复到的路径。
    /// keepName：原位置有同名的就不恢复（实例），而不是改名放回。
    /// </summary>
    public static void Deleted(TrashEntry entry, Action<string> restored, bool keepName = false)
    {
        string? path = null;
        Add(new UndoItem(entry.Name, () =>
        {
            path = TrashService.Restore(entry, keepName);
            return Path.GetFileName(path);
        }, () => restored(path!)));
    }

    /// <summary>列表里的一项删掉后调用（不是文件）：undo 把它放回去。</summary>
    public static void Removed(string name, Action undo) =>
        Add(new UndoItem(name, () =>
        {
            undo();
            return name;
        }));

    private static void Add(UndoItem item)
    {
        lock (History) History.Add(item);
        Dispatcher.UIThread.Post(() =>
        {
            Shown?.Invoke(L.F("trash.deleted", item.Name), NotificationType.Information);
            if (_manager is null) return;
            var undo = new Button { Content = L.T("common.undo"), Classes = { "secondary" }, Margin = new Avalonia.Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var content = new DockPanel
            {
                Children =
                {
                    undo,
                    new TextBlock { Text = L.F("trash.deleted", item.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            DockPanel.SetDock(undo, Dock.Right);
            undo.Click += (_, _) =>
            {
                undo.IsEnabled = false;
                Undo(item);
                // 没撤销成功（还在记录里）就让按钮能再点。
                undo.IsEnabled = IsPending(item);
            };
            _manager.Show(content, NotificationType.Information, UndoDuration);
        });
    }

    /// <summary>
    /// 撤销最近一次删除（Ctrl+Z）。没有可撤销的返回 false。
    /// 最近的那条撤销失败过（比如文件被占用）就先撤销更早的，不让一条坏的挡住后面所有的；都失败过才再试最近的。
    /// </summary>
    public static bool UndoLatest()
    {
        UndoItem? item;
        lock (History)
            item = History.LastOrDefault(i => !i.Running && !i.Failed) ?? History.LastOrDefault(i => !i.Running);
        if (item is null) return false;
        Undo(item);
        return true;
    }

    /// <summary>
    /// 撤销一条记录。成功后才从记录里拿掉；失败（比如文件被占用）时留着，可以再试。
    /// 回收目录里已经没有这一项、或者不允许放回原处，再试也没用，才一起拿掉。
    /// 原来的实例不在了（ParentMissing）留着：把实例装回来以后还能再撤销。
    /// </summary>
    private static void Undo(UndoItem item)
    {
        lock (History)
        {
            if (!History.Contains(item) || item.Running) return;
            item.Running = true;
        }
        string name;
        var remove = false;
        try
        {
            name = item.Undo();
            remove = true;
        }
        catch (TrashException e)
        {
            remove = e.Reason is TrashFailure.EntryMissing or TrashFailure.OriginalOutsideOwner;
            Show(L.F("trash.restore_failed", Describe(e)), NotificationType.Error);
            return;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Show(L.F("trash.restore_failed", ErrorText.Describe(e)), NotificationType.Error);
            return;
        }
        finally
        {
            // 不管撤销成功、失败还是抛了别的异常，Running 都要放开，不然这一条就再也撤销不了。
            Finish(item, remove);
        }
        // 已经恢复了：刷新界面出错不能报成“恢复失败”，也不能让程序崩掉。
        try { item.AfterUndo?.Invoke(); }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine("Jay: refresh after undo failed: " + e); }
        Show(L.F("trash.restored", name), NotificationType.Success);
    }

    private static bool IsPending(UndoItem item)
    {
        lock (History) return History.Contains(item);
    }

    /// <summary>还能撤销的条数（测试用）。</summary>
    internal static int PendingCount
    {
        get { lock (History) return History.Count; }
    }

    /// <summary>清空撤销记录（测试用）。</summary>
    internal static void ClearHistory()
    {
        lock (History) History.Clear();
    }

    /// <summary>加一条撤销记录，带恢复后的刷新（测试用；界面上走 Deleted / Removed）。</summary>
    internal static void AddForTest(string name, Func<string> undo, Action? afterUndo) => Add(new UndoItem(name, undo, afterUndo));

    private static void Finish(UndoItem item, bool remove)
    {
        lock (History)
        {
            item.Running = false;
            item.Failed = !remove;
            if (remove) History.Remove(item);
        }
    }

    /// <summary>放进状态栏消息参数里的原因：显示时才翻译，换语言后跟着变。</summary>
    public static object Reason(TrashException e) => new DeferredReason(e);

    private sealed record DeferredReason(TrashException Error)
    {
        public override string ToString() => Describe(Error);
    }

    /// <summary>回收目录出错的原因写成当前语言的话。</summary>
    public static string Describe(TrashException e) => e.Reason switch
    {
        TrashFailure.SourceMissing => L.F("trash.failure.source_missing", Path.GetFileName(e.PathInvolved)),
        TrashFailure.NoTrashLocation => L.T("trash.failure.no_location"),
        TrashFailure.IsTrashItself => L.T("trash.failure.is_trash"),
        TrashFailure.TrashInScannedFolder => L.T("trash.failure.in_scanned"),
        TrashFailure.OriginalOutsideOwner => L.T("trash.failure.outside_owner"),
        TrashFailure.EntryMissing => L.T("trash.failure.entry_missing"),
        TrashFailure.ParentMissing => L.T("trash.failure.parent_missing"),
        TrashFailure.TargetExists => L.F("trash.failure.target_exists", Path.GetFileName(e.PathInvolved)),
        _ => e.Message,
    };
}
