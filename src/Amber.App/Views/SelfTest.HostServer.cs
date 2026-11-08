using System.Diagnostics;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 开服端到端自检（两个 Jay 实例配合）：
/// AMBER_SELFTEST_SCOPE=host-server：在真实窗口里按“开服”卡片的按钮走完 填地址 → 准备服务端（真下载）→ 启动 → 邀请 → 等客户端进服 → 撤销 → 停服 → 再开，然后关窗口（关 Jay 时存盘）。
/// AMBER_SELFTEST_SCOPE=jay-client：加离线账号，等邀请码文件（AMBER_SELFTEST_INVITE），在“Jay 服务器”里加入并进服，等被撤销断开。
/// 两边靠 AMBER_SELFTEST_INVITE 指向的文件交换邀请码；主机端在同一目录写 revoked.txt。
/// </summary>
internal sealed partial class SelfTest
{
    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private async Task<MultiplayerPage?> OpenMultiplayerAsync(MultiplayerViewModel vm)
    {
        Dispatcher.UIThread.Invoke(() => main.CurrentPage = vm);
        var page = await FindAsync<MultiplayerPage>(_ => true);
        await Task.Delay(500);
        return page;
    }

    private void ScrollTo(MultiplayerPage page, string key)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
            var heading = page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == L.T(key) && t.IsEffectivelyVisible);
            if (heading?.TranslatePoint(new Avalonia.Point(), scroll) is { } position)
                scroll.Offset = new Avalonia.Vector(0, Math.Max(0, scroll.Offset.Y + position.Y - 10));
        });
    }

    private async Task<Button?> ButtonFor(System.Windows.Input.ICommand command, object? parameter = null) =>
        await FindAsync<Button>(b => ReferenceEquals(b.Command, command) && (parameter is null || Equals(b.CommandParameter, parameter)));

    private async Task HostServerAsync()
    {
        var invitePath = Env("AMBER_SELFTEST_INVITE", Path.Combine(reportDir, "invite.txt"));
        var signalDir = Path.GetDirectoryName(invitePath)!;
        var vm = Page<MultiplayerViewModel>();
        var page = await OpenMultiplayerAsync(vm);
        if (page is null)
        {
            Check("开服：联机页显示", false);
            return;
        }
        var host = vm.HostServer;
        Log("数据目录：" + host.DataDirectory);
        ScrollTo(page, "multiplayer.hostserver.title");

        // 1. 填地址
        Check("开服：一开始只显示填地址这一步", Dispatcher.UIThread.Invoke(() => host.NeedsInit && !host.NeedsSetup && !host.IsSetUp));
        Dispatcher.UIThread.Invoke(() =>
        {
            host.PublicHost = "127.0.0.1";
            host.PublicPort = "47670";
        });
        await ShotAsync("开服-填地址");
        Check("开服：点“下一步”", Press(await ButtonFor(host.InitCommand)));
        Check("开服：初始化后进入准备服务端这一步", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.IsInitialized && host.NeedsSetup), 10), host.Status);

        // 2. 准备 MC 服务端（真下载）
        Dispatcher.UIThread.Invoke(() =>
        {
            host.McVersion = "1.21.1";
            host.MemoryMb = "2048";
            host.AcceptEula = true;
        });
        ScrollTo(page, "multiplayer.hostserver.title");
        await ShotAsync("开服-准备服务端前");
        var sw = Stopwatch.StartNew();
        Check("开服：点“准备 MC 服务端”", Press(await ButtonFor(host.SetupCommand)));
        var setUp = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.IsSetUp || (!host.IsBusy && host.Status.Length > 0 && sw.Elapsed > TimeSpan.FromSeconds(3))), 1200);
        Check("开服：服务端准备好", Dispatcher.UIThread.Invoke(() => host.IsSetUp), $"{sw.Elapsed.TotalSeconds:F0} 秒；{host.Status}");
        await ShotAsync("开服-准备好");
        if (!Dispatcher.UIThread.Invoke(() => host.IsSetUp)) return;

        // 3. 启动
        Check("开服：点“启动服务器”", Press(await ButtonFor(host.StartCommand)));
        var started = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.LogText.Contains("Done (")), 300);
        Check("开服：控制台里看到 MC 启动完成（Done）", started, Dispatcher.UIThread.Invoke(() => host.Status));
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var e in page.GetVisualDescendants().OfType<Expander>().Where(e => e.Header is string s && s == L.T("multiplayer.hostserver.console")))
                e.IsExpanded = true;
        });
        ScrollTo(page, "multiplayer.hostserver.title");
        await ShotAsync("开服-运行中控制台");
        Log("控制台末尾：" + Dispatcher.UIThread.Invoke(() => string.Join(" | ", host.LogText.Split('\n').TakeLast(4).Select(l => l.Trim()))));
        Check("开服：摘要显示运行中、0 人在线", Dispatcher.UIThread.Invoke(() => host.Summary.Contains(L.F("multiplayer.hostserver.online", 0))), host.Summary);

        // 控制台命令
        Dispatcher.UIThread.Invoke(() => host.McCommand = "list");
        Press(await ButtonFor(host.SendCommandCommand));
        Check("开服：控制台命令 list 有回应", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.LogText.Contains("players online")), 10));

        // 4. 邀请码
        Dispatcher.UIThread.Invoke(() => host.InviteName = "Steve");
        Check("开服：点“生成邀请码”", Press(await ButtonFor(host.CreateInviteCommand)));
        var code = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.InviteCode.Length > 0), 5) ? Dispatcher.UIThread.Invoke(() => host.InviteCode) : "";
        Check("开服：生成了邀请码", code.Length > 0, Dispatcher.UIThread.Invoke(() => host.Status));
        Check("开服：点“复制”", Press(await ButtonFor(host.CopyInviteCommand)));
        await Task.Delay(500);
        var clip = await Dispatcher.UIThread.InvokeAsync(async () => Amber.App.Services.AvaloniaClipboard.FromMainWindow() is { } board ? await board.GetTextAsync() : null);
        Check("开服：剪贴板里是邀请码", clip == code, Dispatcher.UIThread.Invoke(() => host.Status));
        ScrollTo(page, "multiplayer.hostserver.title");
        await ShotAsync("开服-邀请码");
        File.WriteAllText(invitePath, code);

        // 5. 等客户端进服
        var joined = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.Summary.Contains(L.F("multiplayer.hostserver.online", 1)) && host.LogText.Contains("Steve joined the game")), 900);
        Check("开服：客户端进服后在线人数变成 1", joined, Dispatcher.UIThread.Invoke(() => host.Summary));
        Check("开服：名单里有 Steve（1 台设备）", Dispatcher.UIThread.Invoke(() => host.Members.Any(m => m.Name == "Steve" && m.HasDevices)),
            Dispatcher.UIThread.Invoke(() => string.Join("；", host.Members.Select(m => m.Name + " " + m.Detail))));
        await Task.Delay(8000); // 让客户端把世界加载完
        ScrollTo(page, "multiplayer.hostserver.title");
        await ShotAsync("开服-在线1人");

        // 6. 撤销设备
        if (joined)
        {
            var steve = Dispatcher.UIThread.Invoke(() => host.Members.First(m => m.Name == "Steve"));
            var revokeWatch = Stopwatch.StartNew();
            Check("开服：点 Steve 的“撤销设备”", Press(await ButtonFor(host.RevokeCommand, steve)));
            var kicked = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.Summary.Contains(L.F("multiplayer.hostserver.online", 0)) && host.LogText.Contains("Steve lost connection")), 15);
            Check("开服：撤销后客户端立刻被断开", kicked, $"{revokeWatch.Elapsed.TotalSeconds:F1} 秒；{host.Summary}；{host.Status}");
            File.WriteAllText(Path.Combine(signalDir, "revoked.txt"), DateTime.Now.ToString("O"));
            Check("开服：撤销后 Steve 没有设备了", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.Members.Any(m => m.Name == "Steve" && !m.HasDevices)), 5));
            ScrollTo(page, "multiplayer.hostserver.title");
            await ShotAsync("开服-撤销后");
        }

        // 7. 停止服务器
        var mcLog = Path.Combine(host.DataDirectory, "mc", "logs", "latest.log");
        Check("开服：点“停止服务器”", Press(await ButtonFor(host.StopCommand)));
        var stopped = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => !host.IsRunning && !host.IsBusy), 90);
        var logText = File.Exists(mcLog) ? ReadShared(mcLog) : "";
        Check("开服：停止后存盘退出", stopped && logText.Contains("Stopping server") && logText.Contains("All dimensions are saved"),
            $"{host.Status}；日志 {mcLog} 有 Stopping server={logText.Contains("Stopping server")} 存盘={logText.Contains("All dimensions are saved")}");
        Check("开服：停止后能再点“启动”", Dispatcher.UIThread.Invoke(() => host.CanStart));
        ScrollTo(page, "multiplayer.hostserver.title");
        await ShotAsync("开服-已停止");

        // 8. 再开一次，交给关窗口（关 Jay 时先存盘）
        await StartForExitAsync(host, mcLog);
    }

    /// <summary>启动服务器，等这一次的 MC 真正启动完成（新的 latest.log 里有 Done），然后由调用方关窗口。</summary>
    private async Task StartForExitAsync(HostServerViewModel host, string mcLog)
    {
        Check("开服：点“启动服务器”（之后关窗口）", Press(await ButtonFor(host.StartCommand)));
        await Task.Delay(3000); // 让 MC 把上一次的 latest.log 归档
        var log = "";
        Check("开服：再次启动完成", await WaitAsync(() =>
        {
            log = ReadShared(mcLog);
            return Dispatcher.UIThread.Invoke(() => host.IsRunning) && log.Contains("Done (") && !log.Contains("Stopping server");
        }, 300));
        File.WriteAllText(Path.Combine(reportDir, "close-at.txt"), DateTime.Now.ToString("O"));
        Log("关闭窗口（服务器开着）：" + DateTime.Now.ToString("O"));
    }

    /// <summary>只测“服务器开着时关 Jay”：已经准备好的数据目录里启动服务器，等 MC 启动完成就关窗口。</summary>
    private async Task HostExitAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        var page = await OpenMultiplayerAsync(vm);
        var host = vm.HostServer;
        if (page is null || !Dispatcher.UIThread.Invoke(() => host.IsSetUp))
        {
            Check("开服：数据目录已经准备好", false, host.DataDirectory);
            return;
        }
        await StartForExitAsync(host, Path.Combine(host.DataDirectory, "mc", "logs", "latest.log"));
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    private async Task JayClientAsync()
    {
        var invitePath = Env("AMBER_SELFTEST_INVITE", Path.Combine(reportDir, "invite.txt"));
        var signalDir = Path.GetDirectoryName(invitePath)!;
        var version = Env("AMBER_SELFTEST_VERSION", "1.21.1");

        var service = Page<HomeViewModel>().Service;
        // 离线账号 Steve
        var accounts = Page<AccountsViewModel>();
        Dispatcher.UIThread.Invoke(() =>
        {
            if (!service.Accounts.Store.Accounts.Any(a => a.Name == "Steve"))
            {
                accounts.OfflineName = "Steve";
                accounts.AddOfflineCommand.Execute(null);
            }
        });
        Check("客户端：离线账号 Steve", Dispatcher.UIThread.Invoke(() => service.Accounts.Store.Selected?.Name == "Steve"), service.Accounts.Store.Selected?.Name ?? "无");

        var vm = Page<MultiplayerViewModel>();
        var page = await OpenMultiplayerAsync(vm);
        if (page is null)
        {
            Check("客户端：联机页显示", false);
            return;
        }
        Dispatcher.UIThread.Invoke(() => vm.TargetInstance = version);
        var servers = vm.JayServers;
        Log("等邀请码：" + invitePath);
        var got = await WaitAsync(() => File.Exists(invitePath) && File.ReadAllText(invitePath).Trim().Length > 0, 1800);
        Check("客户端：拿到邀请码", got);
        if (!got) return;
        var invite = File.ReadAllText(invitePath).Trim();
        Dispatcher.UIThread.Invoke(() => servers.InviteText = invite);
        ScrollTo(page, "multiplayer.jayserver.title");
        await ShotAsync("客户端-粘贴邀请码");
        Check("客户端：点“加入”", Press(await ButtonFor(servers.JoinCommand)));
        var enrolled = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => servers.Servers.Count == 1 && !servers.IsBusy), 30);
        Check("客户端：入服成功，列表里有这台服务器", enrolled, Dispatcher.UIThread.Invoke(() => servers.Status));
        ScrollTo(page, "multiplayer.jayserver.title");
        await ShotAsync("客户端-已加入");
        if (!enrolled) return;

        var item = Dispatcher.UIThread.Invoke(() => servers.Servers[0]);
        var log = Page<LogViewModel>();
        Check("客户端：点“进服”", Press(await ButtonFor(servers.PlayCommand, item)));
        var home = Page<HomeViewModel>();
        bool LogHas(string text) => Dispatcher.UIThread.Invoke(() => log.Lines.Any(l => l.Message.Contains(text)));
        var inWorld = await WaitAsync(() => LogHas("Connecting to 127.0.0.1") && LogHas("advancements"), 600);
        Check("客户端：真 MC 客户端进了世界（连上 127.0.0.1 并加载了世界里的进度）", inWorld, Dispatcher.UIThread.Invoke(() => servers.Status + "；" + home.Status));
        File.WriteAllText(Path.Combine(signalDir, "client-in-world.txt"), DateTime.Now.ToString("O"));
        await ShotAsync("客户端-进服后");

        var revoked = await WaitAsync(() => File.Exists(Path.Combine(signalDir, "revoked.txt")), 600);
        if (revoked)
        {
            await Task.Delay(5000);
            Log("客户端：撤销后状态 " + Dispatcher.UIThread.Invoke(() => servers.Status));
            Log("客户端：日志末尾 " + Dispatcher.UIThread.Invoke(() => string.Join(" | ", log.Lines.TakeLast(6).Select(l => l.Message))));
        }
        await ShotAsync("客户端-撤销后");
    }
}
