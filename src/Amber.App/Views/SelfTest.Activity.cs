using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Auth;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=activity (UI batch 3): task center in the activity drawer, the request bar, notifications,
/// the playing pill, the launch-failure card with its fix buttons, and the close guard. Screenshots in three width tiers.
/// No game directory, no network: tasks, requests and game runs are simulated.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task ActivityAsync()
    {
        var activity = main.Activity;
        var service = main.Service;
        try
        {
            window.Width = 1024;
            window.Height = 700;
            main.Shell.Navigate("library");
            await Task.Delay(400);

            // ---- tasks ----
            var cancelled = false;
            var task = service.Tasks.Track("install", "自检安装 1.20.1", () => cancelled = true);
            task.Report(0.4, "12/30 个文件");
            await Task.Delay(300);
            Check("任务：活动按钮外圈显示进度", activity.HasRunningTasks && Math.Abs(activity.RingSweep - 144) < 1, $"{activity.RingSweep}");
            Press(Key.J, KeyModifiers.Control);
            await Task.Delay(300);
            Check("任务：Ctrl+J 打开活动抽屉", activity.IsOpen && window.ActivityDrawer.IsEffectivelyVisible);
            var rows = window.ActivityTaskList.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Check("任务：抽屉“进行中”里有这个任务和进度说明", rows.Contains("自检安装 1.20.1") && rows.Contains("12/30 个文件"), string.Join(" | ", rows));
            Check("任务：抽屉宽 400（中档）", Math.Abs(window.ActivityDrawer.Bounds.Width - 400) < 1, $"{window.ActivityDrawer.Bounds.Width}");
            await ShotAsync("activity-tasks-1024");
            var cancel = window.ActivityTaskList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible);
            cancel?.Command?.Execute(cancel.CommandParameter);
            await Task.Delay(300);
            Check("任务：点取消会叫停任务", cancelled && task.CancelRequested);
            task.Cancelled();
            Press(Key.Escape, KeyModifiers.None);
            await Task.Delay(200);
            Check("任务：Esc 关抽屉", !activity.IsOpen);

            var failed = service.Tasks.Track("import", "自检导入");
            failed.Fail("磁盘已满");
            await Task.Delay(300);
            Check("任务：失败后活动按钮有红点", activity.HasUnseenFailure && activity.HasAttention);
            activity.Toggle();
            await Task.Delay(200);
            Check("任务：打开抽屉后红点消失", !activity.HasUnseenFailure);
            Check("任务：失败原因写在任务下面", activity.Tasks.Any(t => t.IsFailed && t.Detail.Contains("磁盘已满")));
            activity.ClearFinishedCommand.Execute(null);
            await Task.Delay(200);
            Check("任务：清除已完成", activity.Tasks.Count == 0);
            activity.Close();

            // ---- notifications ----
            Toasts.Show("自检错误通知", NotificationType.Error);
            await Task.Delay(300);
            Check("通知：出错的 Toast 不自动消失", Toasts.DurationFor(NotificationType.Error, TimeSpan.FromSeconds(5)) == TimeSpan.Zero);
            Check("通知：Toast 进了“通知”回看，出错有红点", activity.Notices.FirstOrDefault() is { IsError: true, Text: "自检错误通知" } && activity.HasAttention);
            activity.Tab = 1;
            activity.Toggle();
            await ShotAsync("activity-notices-1024");
            activity.Close();
            activity.Tab = 0;

            // ---- request bar ----
            var request = service.Requests.Post(new RequestSpec("selftest", "自检请求：允许 Alex 加入？", "30 秒内不处理就拒绝。",
                [new("allow", "允许"), new("deny", "拒绝", IsDefault: true)], Timeout: TimeSpan.FromSeconds(30), TimeoutChoice: "deny"));
            await Task.Delay(400);
            Check("请求条：出现在顶栏下面", window.RequestBar.IsEffectivelyVisible);
            var barText = window.RequestBar.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Check("请求条：标题和倒计时", barText.Contains("自检请求：允许 Alex 加入？") && barText.Any(t => t.StartsWith("还剩", StringComparison.Ordinal)), string.Join(" | ", barText));
            var second = service.Requests.Post(new RequestSpec("selftest", "自检请求二", null, [new("ok", "知道了", IsDefault: true)]));
            var dup = service.Requests.Post(new RequestSpec("selftest", "自检请求二（重复）", null, [new("ok", "知道了")], DedupeKey: "dup"));
            var dup2 = service.Requests.Post(new RequestSpec("selftest", "自检请求二（重复）", null, [new("ok", "知道了")], DedupeKey: "dup"));
            await Task.Delay(300);
            Check("请求条：同一件事只出现一次", dup == dup2 && activity.Requests.Count == 3, $"{activity.Requests.Count}");
            Check("请求条：多条时折叠成“N 件事等你处理”", activity.HasMoreRequests && activity.MoreRequestsText.Contains('3'));
            var safety = service.Requests.Post(new RequestSpec("selftest", "自检：Bob 请求控制你的电脑", null,
                [new("deny", "拒绝", IsDefault: true), new("allow", "允许", IsDanger: true)], IsSafety: true));
            await Task.Delay(400);
            Check("请求条：安全请求排在最前，用锁定的安全外观", activity.FirstRequest?.Handle == safety
                && window.RequestBar.GetVisualDescendants().OfType<SafetyBanner>().Any(b => b.IsEffectivelyVisible));
            await ShotAsync("activity-requests-1024");
            service.Requests.Resolve(safety, "deny", RequestSource.Bar);
            await Task.Delay(300);
            var allow = window.RequestBar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && Equals(b.Content, "允许"));
            allow?.Command?.Execute(null);
            await Task.Delay(300);
            Check("请求条：点按钮处理请求，其它地方同步收起", request.Choice == "allow" && request.ResolvedBy == RequestSource.Bar
                && activity.Requests.All(r => r.Handle != request));
            Check("请求条：处理过的请求记进通知", activity.Notices.Any(n => n.Text.Contains("自检请求：允许 Alex 加入？")));
            service.Requests.Resolve(second, "ok", RequestSource.Program);
            service.Requests.Withdraw("dup");
            await Task.Delay(300);
            Check("请求条：全部处理后消失", !window.RequestBar.IsVisible);

            // ---- playing pill ----
            var now = DateTimeOffset.Now;
            var session = new LaunchSession("自检实例", now);
            activity.Track(session);
            await Task.Delay(200);
            Check("胶囊：准备中就出现", window.PlayingPill.IsEffectivelyVisible && activity.PillText.Contains("自检实例"), activity.PillText);
            session.ProcessUp(now);
            session.EnteredGame(now);
            await Task.Delay(300);
            Check("胶囊：游戏中显示时长", activity.PillText.Contains("自检实例 · ", StringComparison.Ordinal), activity.PillText);
            await ShotAsync("activity-pill-1024");
            session.ProcessEnded(DateTimeOffset.Now, -1, crashed: true, "自检崩溃");
            await Task.Delay(300);
            Check("胶囊：崩溃后变红", activity.PillIsCrash && window.PlayingPill.Classes.Contains("crash"));
            Check("运行记录：这一局记下来了", activity.Runs.FirstOrDefault()?.Session == session && activity.Runs[0].IsCrash);
            main.Shell.Navigate("settings");
            window.PlayingPill.Command?.Execute(null);
            await Task.Delay(300);
            Check("胶囊：点开去游戏库，看过后收起", main.Shell.Router.Current.Section == Amber.Core.Navigation.Section.Library && !activity.HasPill);

            // ---- launch failure card (N1) ----
            var home = main.Pages.OfType<HomeViewModel>().First();
            var page = window.PageHost.GetVisualDescendants().OfType<HomePage>().FirstOrDefault();
            async Task<List<string>> FailureButtonsAsync(Exception e)
            {
                home.ShowFailureForSelfTest(e);
                await Task.Delay(300);
                return page?.FailureFixes.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string ?? "").ToList() ?? [];
            }
            var login = await FailureButtonsAsync(new LoginRequiredException("expired"));
            Check("启动失败：登录过期 → 重新登录排第一", page?.FailureCard.IsEffectivelyVisible == true && login.FirstOrDefault() == L.T("home.failure.fix.relogin"), string.Join(",", login));
            var java = await FailureButtonsAsync(new JavaNotFoundException(21, "no java"));
            Check("启动失败：缺 Java → 下载 Java", java.FirstOrDefault() == L.T("home.failure.fix.java") && home.Failure?.Title.Contains("21") == true, string.Join(",", java));
            var net = await FailureButtonsAsync(new HttpRequestException("timeout"));
            Check("启动失败：下载失败 → 换镜像重试", net.Contains(L.T("home.failure.fix.mirror")) || service.Settings.DownloadSource == "mirror", string.Join(",", net));
            Check("启动失败：可以导出报告", home.Failure?.CanExport == true);
            await ShotAsync("activity-failure-1024");
            home.DismissFailureCommand.Execute(null);
            await Task.Delay(200);
            Check("启动失败：可以关掉", page?.FailureCard.IsVisible == false);

            // ---- three width tiers ----
            activity.Toggle();
            foreach (var (w, h, name) in new[] { (600.0, 420.0, "600"), (1440.0, 900.0, "1440") })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(500);
                var compact = w < 700;
                var width = window.ActivityDrawer.Bounds.Width;
                Check($"抽屉（{name}）：{(compact ? "紧凑档铺满内容区" : "宽 400")}",
                    compact ? Math.Abs(width - window.ContentColumn.Bounds.Width) < 2 : Math.Abs(width - 400) < 1, $"{width}");
                await ShotAsync($"activity-drawer-{name}");
            }
            activity.Close();
            window.Width = 1024;
            window.Height = 700;
            await Task.Delay(300);

            // ---- close guard (N2) ----
            Check("关窗保护：没事时不拦", main.CloseBlockers().Count == 0 && window.StartCloseGuard() is null);
            var running = service.Tasks.Track("install", "自检关窗任务", () => { });
            SafetyChoice answer = SafetyChoice.Keep;
            window.CloseChooser = _ => Task.FromResult(answer);
            await (window.StartCloseGuard() ?? Task.CompletedTask);
            Check("关窗保护：有进行中的任务时先问，选取消就不关", window.LastCloseBlockers?.Any(b => b.Contains('1')) == true && window.IsVisible,
                string.Join(" | ", window.LastCloseBlockers ?? []));
            answer = SafetyChoice.Alternate;
            await (window.StartCloseGuard() ?? Task.CompletedTask);
            // Xvfb has no window manager, so the minimize request may bounce back to Normal; the answer and "still open" are what matter.
            Check("关窗保护：选最小化就不关", window.LastCloseChoice == SafetyChoice.Alternate && window.IsVisible, $"{window.WindowState} {window.IsVisible}");
            window.WindowState = WindowState.Normal;
            window.CloseChooser = null;
            var dialog = SafetyDialog.CreateChoiceForSelfTest(L.T("close.title"), L.T("close.body"), L.T("close.quit_all"), L.T("common.cancel"),
                L.T("close.minimize"), main.CloseBlockers());
            dialog.Show(window);
            await Task.Delay(500);
            Check("关窗保护：确认框是锁定的安全外观，三个按钮，默认焦点在“取消”",
                dialog.AlternateButton is not null && dialog.KeepButton.IsFocused && dialog.Panel.IsEffectivelyVisible);
            await DialogShotAsync(dialog, "activity-close-guard");
            dialog.Close();
            main.EndAllForExit();
            await Task.Delay(200);
            Check("关窗保护：全部结束会取消进行中的任务", running.CancelRequested);
            running.Cancelled();
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
    }

    private async Task DialogShotAsync(Window target, string name)
    {
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height));
            bitmap.Render(target);
            bitmap.Save(Path.Combine(reportDir, $"{++_shot:D2}-{name}.png"));
        });
    }
}
