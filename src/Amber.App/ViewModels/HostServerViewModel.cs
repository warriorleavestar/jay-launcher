using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Gate;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>名单里的一个人（开服用）。</summary>
public sealed class HostMemberItem(GateMember member)
{
    public string Name { get; } = member.Name;
    public string Detail { get; } = member.Disabled
        ? L.F("multiplayer.hostserver.member_disabled", member.Devices.Count)
        : L.F("multiplayer.hostserver.member_detail", member.Devices.Count,
            member.Devices.Select(d => d.LastSeen).Max() is { } seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : L.T("multiplayer.hostserver.never"));
    public bool HasDevices { get; } = member.Devices.Count > 0;
}

/// <summary>
/// 联机页里的“开服”：在这台电脑上跑 Jay 服务器版（网关 + 原版 MC 服务端），和命令行 jay-server 用同一套 Core。
/// 步骤：填玩家连的地址 → 准备 MC 服务端（下载、Java、EULA）→ 启动 → 给朋友发邀请码。
/// 隐私+/++、节点、端口轮换这些进阶设置仍用命令行（数据目录相同，见 <see cref="DataDirectory"/>）。关掉 Jay 时服务器存盘停止。
/// </summary>
public sealed partial class HostServerViewModel : ObservableObject
{
    private const int MaxLogLines = 200;
    private static JayServerRunner? s_running;

    private readonly LauncherService _service;
    private readonly List<string> _log = [];
    /// <summary>卡片上显示的邀请码是给谁的：这个人的邀请用掉（或过期、作废）后就把码从卡片上清掉。</summary>
    private string? _shownInviteFor;
    /// <summary>是用户（或关 Jay）要停的：MC 随之退出不算“意外退出”。</summary>
    private bool _stopRequested;
    private DispatcherTimer? _timer;

    public HostServerViewModel(LauncherService service, string? root = null)
    {
        _service = service;
        DataDirectory = root ?? Path.Combine(AppSettings.DataDir, "jay-server");
    }

    /// <summary>服务器数据目录（命令行用 jay-server --dir 指向它就能管同一台服务器）。</summary>
    public string DataDirectory { get; }
    public string DataDirectoryText => L.F("multiplayer.hostserver.data_dir", DataDirectory);

    public ObservableCollection<HostMemberItem> Members { get; } = [];

    [ObservableProperty] public partial bool IsInitialized { get; set; }
    [ObservableProperty] public partial bool IsSetUp { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string PublicHost { get; set; } = "";
    [ObservableProperty] public partial string PublicPort { get; set; } = "47670";
    [ObservableProperty] public partial string McVersion { get; set; } = "latest";
    [ObservableProperty] public partial string MemoryMb { get; set; } = "2048";
    [ObservableProperty] public partial bool AcceptEula { get; set; }
    [ObservableProperty] public partial string InviteName { get; set; } = "";
    [ObservableProperty] public partial string InviteCode { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string LogText { get; set; } = "";
    [ObservableProperty] public partial string McCommand { get; set; } = "";

    /// <summary>The wizard's step bar (official §6.17): 1 address, 2 prepare the server, 3 start, 4 invite.</summary>
    public int WizardStep => !IsInitialized ? 1 : !IsSetUp ? 2 : !IsRunning ? 3 : 4;

    public IReadOnlyList<WizardStepItem> WizardSteps =>
    [
        new(1, "multiplayer.hostserver.wizard.address", WizardStep),
        new(2, "multiplayer.hostserver.wizard.prepare", WizardStep),
        new(3, "multiplayer.hostserver.wizard.start", WizardStep),
        new(4, "multiplayer.hostserver.wizard.invite", WizardStep),
    ];

    private readonly Amber.Core.Multiplayer.ConsoleHistory _history = new();

    /// <summary>↑ in the console: the previous command.</summary>
    public void HistoryUp()
    {
        if (_history.Previous() is { } line) McCommand = line;
    }

    /// <summary>↓ in the console: the next command (empty after the newest).</summary>
    public void HistoryDown()
    {
        if (_history.Next() is { } line) McCommand = line;
    }

    public bool NeedsInit => !IsInitialized;
    public bool NeedsSetup => IsInitialized && !IsSetUp;
    public bool CanStart => IsInitialized && IsSetUp && !IsRunning;

    partial void OnIsInitializedChanged(bool value) => NotifyStates();
    partial void OnIsSetUpChanged(bool value) => NotifyStates();
    partial void OnIsRunningChanged(bool value) => NotifyStates();

    private void NotifyStates()
    {
        OnPropertyChanged(nameof(NeedsInit));
        OnPropertyChanged(nameof(WizardStep));
        OnPropertyChanged(nameof(WizardSteps));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(CanStart));
    }

    public void Load()
    {
        IsInitialized = JayServerRunner.IsInitialized(DataDirectory);
        var host = new McServerHost(DataDirectory);
        IsSetUp = host.IsSetUp && host.EulaAccepted;
        IsRunning = s_running is not null;
        if (IsInitialized)
        {
            var config = Store().LoadConfig();
            PublicHost = config.PublicHost;
            PublicPort = config.PublicPort.ToString();
        }
        if (host.IsSetUp)
        {
            var settings = host.LoadSettings();
            McVersion = settings.Version;
            MemoryMb = settings.MemoryMb.ToString();
        }
        AcceptEula = host.EulaAccepted;
        Refresh();
    }

    private GateStore Store() => new(JayServerRunner.GateDirectory(DataDirectory));

    /// <summary>第一步：生成服务器身份，记下玩家连的地址和端口。</summary>
    [RelayCommand]
    private void Init()
    {
        var host = PublicHost.Trim();
        if (!GateInvite.IsValidHost(host))
        {
            Status = L.T("multiplayer.hostserver.bad_host");
            return;
        }
        if (!int.TryParse(PublicPort, out var port) || port is < 1024 or > 65535)
        {
            Status = L.T("multiplayer.hostserver.bad_port");
            return;
        }
        try
        {
            if (JayServerRunner.IsInitialized(DataDirectory))
            {
                var store = Store();
                var config = store.LoadConfig();
                config.PublicHost = host;
                config.PublicPort = port;
                config.ListenPort = port;
                store.SaveConfig(config);
            }
            else GateStore.Initialize(JayServerRunner.GateDirectory(DataDirectory), host, port, port);
            Status = L.F("multiplayer.hostserver.initialized", host, port);
            Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = e.Message;
        }
    }

    /// <summary>第二步：下载原版服务端（按官方清单核对）、准备 Java、写 server.properties（只听本机、离线账号由网关核对名字）。</summary>
    [RelayCommand]
    private async Task SetupAsync()
    {
        if (!AcceptEula)
        {
            Status = L.T("multiplayer.hostserver.need_eula");
            return;
        }
        if (!int.TryParse(MemoryMb, out var memory) || memory is < 512 or > 65536)
        {
            Status = L.T("multiplayer.hostserver.bad_memory");
            return;
        }
        IsBusy = true;
        try
        {
            var host = new McServerHost(DataDirectory);
            var settings = await host.SetupAsync(_service.Downloader, string.IsNullOrWhiteSpace(McVersion) ? "latest" : McVersion.Trim(), memory,
                line => Dispatcher.UIThread.Post(() => Status = line));
            host.AcceptEula();
            Status = L.F("multiplayer.hostserver.ready", settings.Version);
            Load();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or ArgumentException or InvalidOperationException
                                       or UnauthorizedAccessException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Status = L.F("multiplayer.hostserver.setup_failed", e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Start()
    {
        if (s_running is not null) return;
        try
        {
            var runner = JayServerRunner.Start(DataDirectory, true, line => Log("[Jay] " + line), Log);
            s_running = runner;
            _stopRequested = false;
            IsRunning = true;
            Status = L.T("multiplayer.hostserver.started");
            _ = runner.Exited.ContinueWith(_ => Dispatcher.UIThread.Post(async () =>
            {
                if (!ReferenceEquals(s_running, runner) || _stopRequested) return;
                // MC 自己退出了（崩溃、端口被占、Java 不对……）：状态里写明，最后几行日志在控制台里。
                var last = _log.LastOrDefault(l => !l.StartsWith("[Jay]", StringComparison.Ordinal)) ?? "";
                await StopAsync();
                Status = L.F("multiplayer.hostserver.mc_exited", last);
            }), TaskScheduler.Default);
            StartTimer();
            Refresh();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.Net.Sockets.SocketException
                                       or System.ComponentModel.Win32Exception)
        {
            Status = L.F("multiplayer.hostserver.start_failed", e.Message);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (s_running is not { } runner) return;
        _stopRequested = true;
        IsBusy = true;
        Status = L.T("multiplayer.hostserver.stopping");
        try
        {
            await runner.StopAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            s_running = null;
            _timer?.Stop();
            IsRunning = false;
            IsBusy = false;
            Status = L.T("multiplayer.hostserver.stopped");
            Refresh();
        }
    }

    /// <summary>Whether a server started from Jay is still running (used by the close guard).</summary>
    public static bool ServerRunning => Volatile.Read(ref s_running) is not null;

    /// <summary>关掉 Jay 时调用：服务器存盘退出（最多等 30 秒）。</summary>
    public static void StopRunningServer()
    {
        var runner = Interlocked.Exchange(ref s_running, null);
        if (runner is null) return;
        // 这里在界面线程上同步等。StopAsync 里的 await 会想回到界面线程继续，界面线程又在等它，
        // 结果卡满 40 秒、"stop" 根本没发出去，Jay 退出后 MC 还在跑（不存盘、占着存档锁）。所以放到线程池上跑。
        var stopping = Task.Run(() => runner.StopAsync(TimeSpan.FromSeconds(30)));
        try
        {
            if (!stopping.Wait(TimeSpan.FromSeconds(40)) && runner.Mc is { HasExited: false } mc)
                mc.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is AggregateException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [RelayCommand]
    private void SendCommand()
    {
        var line = McCommand.Trim();
        if (line.Length == 0 || s_running is not { } runner) return;
        runner.Command(line);
        _history.Add(line);
        Log("> " + line);
        McCommand = "";
    }

    /// <summary>给一个用户名发邀请码（24 小时内有效，用一次；对方在“Jay 服务器”里粘贴）。</summary>
    [RelayCommand]
    private void CreateInvite()
    {
        var name = InviteName.Trim();
        if (!GateInvite.IsValidUsername(name))
        {
            Status = L.T("core.gate.admin.badname");
            return;
        }
        try
        {
            var store = Store();
            using var key = store.LoadServerKey();
            InviteCode = store.CreateInvite(name, TimeSpan.FromHours(24), 1, key.PublicKey).Encode();
            _shownInviteFor = name;
            Status = L.F("multiplayer.hostserver.invite_created", name);
            InviteName = "";
            Refresh();
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task CopyInviteAsync()
    {
        if (InviteCode.Length > 0 && await SensitiveClipboard.Shared.CopyAsync(InviteCode)) Status = L.T("multiplayer.hostserver.invite_copied");
    }

    /// <summary>撤销这个人的所有设备（在线的立刻断开）；名字留在名单里，再发邀请就能重新加入。</summary>
    [RelayCommand]
    private void Revoke(HostMemberItem item)
    {
        var n = Store().RevokeDevices(item.Name, null);
        Status = L.F("multiplayer.hostserver.revoked", item.Name, n);
        Refresh();
    }

    [RelayCommand]
    private void RemoveMember(HostMemberItem item)
    {
        Store().RemoveMember(item.Name);
        Status = L.F("multiplayer.hostserver.removed", item.Name);
        Refresh();
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
    }

    /// <summary>刷新名单和运行状态（隐私等级、在线人数、节点心跳、熔断）。</summary>
    public void Refresh()
    {
        if (!JayServerRunner.IsInitialized(DataDirectory))
        {
            Members.Clear();
            Summary = "";
            return;
        }
        GateState state;
        try { state = Store().Read(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return; }
        if (_shownInviteFor is { } invitee && !state.Invites.Any(i => string.Equals(i.Name, invitee, StringComparison.OrdinalIgnoreCase) && i.UsesLeft > 0 && i.Expires > DateTimeOffset.UtcNow))
        {
            InviteCode = "";
            _shownInviteFor = null;
        }
        var names = state.Members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => new HostMemberItem(m)).ToList();
        if (!names.Select(n => n.Detail + n.Name).SequenceEqual(Members.Select(n => n.Detail + n.Name)))
        {
            Members.Clear();
            foreach (var m in names) Members.Add(m);
        }
        var level = state.Privacy switch
        {
            PrivacyLevel.Plus => L.T("multiplayer.hostserver.privacy_plus"),
            PrivacyLevel.PlusPlus => L.T("multiplayer.hostserver.privacy_plusplus"),
            _ => L.T("multiplayer.hostserver.privacy_basic"),
        };
        var parts = new List<string> { level };
        if (state.Rotation is { Enabled: true }) parts.Add(L.T("multiplayer.hostserver.rotating"));
        if (s_running is { } runner)
        {
            parts.Add(L.F("multiplayer.hostserver.online", runner.Gate.ActiveSessions));
            if (state.Relays.Count > 0 && runner.Health() is { } health)
                parts.Add(L.F("multiplayer.hostserver.relays", health.Relays.Count(r => r.Connected), state.Relays.Count));
            if (runner.Gate.BreakerOpen) parts.Add(L.T("multiplayer.hostserver.breaker_open"));
        }
        else parts.Add(L.T("multiplayer.hostserver.not_running"));
        Summary = string.Join(" · ", parts);
    }

    private void Log(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            LogText = string.Join(Environment.NewLine, _log.TakeLast(40));
        });
    }
}

/// <summary>One step on the server wizard's bar: done (✓), current (●) or still to come.</summary>
public sealed record WizardStepItem(int Number, string Key, int Current) : Amber.App.Localization.LocalizedRecord
{
    public bool IsDone => Number < Current;
    public bool IsCurrent => Number == Current;
    public string Label => $"{"①②③④"[Number - 1]} {L.T(Key)}{(IsDone ? " ✓" : IsCurrent ? " ●" : "")}";
}
