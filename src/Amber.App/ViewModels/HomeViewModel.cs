using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Amber.App.Services;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Install;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Game;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>启动页：选版本、账号、启动，退出后给出崩溃分析。</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    /// <summary>
    /// 有“仅游戏”转发时改启动参数：加 SOCKS5 的 JVM 参数；要进的服务器（不是本机或房间里的地址）改成经出口的本机转发端口。
    /// 返回那个转发（游戏结束时关掉），没有时返回 null。
    /// </summary>
    private readonly List<string> _proxyNotes = [];

    private async Task<(LaunchOptions Options, Amber.Core.Multiplayer.Transport.TunnelClient? Relay)> ApplyGameProxyAsync(LaunchOptions options)
    {
        _proxyNotes.Clear();
        if (_service.ActiveGameProxy is not { } proxy) return (options, null);
        // 用户自己在游戏参数里写了 --proxyHost（或 --proxyHost=地址）时不再加第二个。
        var hasProxyArg = options.ExtraGameArgs.Any(a => a.Equals("--proxyHost", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--proxyHost=", StringComparison.OrdinalIgnoreCase));
        options = options with
        {
            ExtraJvmArgs = [.. options.ExtraJvmArgs, .. proxy.JvmArgs],
            ExtraGameArgs = hasProxyArg ? options.ExtraGameArgs : [.. options.ExtraGameArgs, .. proxy.GameArgs],
        };
        if (!hasProxyArg)
            _proxyNotes.Add(L.F("home.proxy.http_forwarding", proxy.ExitIp));
        else
            _proxyNotes.Add(L.T("home.proxy.custom_proxy"));
        if (options.JoinServer is not { Length: > 0 } server) return (options, null);
        // 本机、局域网、本机解析成私有地址的服务器不经出口（出口也会拒绝），游戏照常直连；端口是 25565 时像游戏一样查 SRV。
        if (await Amber.Core.Multiplayer.Transport.JoinTarget.ResolveForRelayAsync(server, onSkip: reason => _proxyNotes.Add(L.T("home.proxy.direct_join") + reason)) is not { } plan)
            return (options, null);
        var relay = proxy.RelayTo(plan);
        _proxyNotes.Add(L.F("home.proxy.server_forwarding", plan.HandshakeHost, proxy.ExitIp, relay.Port));
        return (options with { JoinServer = $"127.0.0.1:{relay.Port}" }, relay);
    }


    /// <summary>进程起来后这么久还没在日志里看到进入游戏的标志，就当作“可能已进入游戏”（老版本、Forge 的日志写法不一样）。</summary>
    private static readonly TimeSpan EnterGameFallback = TimeSpan.FromMinutes(3);

    /// <summary>点“退出游戏”后这么久进程还在，就给出“强制结束”。</summary>
    private static readonly TimeSpan ForceKillDelay = TimeSpan.FromSeconds(30);

    private readonly LauncherService _service;

    /// <summary>自检用。</summary>
    internal LauncherService Service => _service;
    private readonly LogViewModel _log;
    private List<string> _lastRunFiles = [];
    private string[] _lastRunOutput = [];
    private CrashAttachment[] _lastRunAttachments = [];
    private bool _refreshingAccounts;
    private CrashInput? _crashInput;
    /// <summary>每次显示新的崩溃结果（或清掉）加一：换语言的重新分析只在期间没有新结果时才替换。</summary>
    private int _crashGeneration;

    /// <summary>一次启动，从准备到游戏退出。可以同时有好几个（同时开两个客户端测联机）。</summary>
    private sealed class GameRun(string id)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cts { get; } = new();
        public Process? Game { get; set; }
        public bool StoppedByUser { get; set; }
        public DateTime Started { get; set; } = DateTime.UtcNow;
        /// <summary>日志里出现了进入游戏的标志，或者等够了兜底时间。</summary>
        public bool InGame { get; set; }
        /// <summary>是兜底时间到了才算进入的（没看到标志）。</summary>
        public bool InGameGuessed { get; set; }
        public bool Exiting { get; set; }
        /// <summary>这一局自己的原始输出、无痕设置和多行事件状态，多开时不能共用。</summary>
        public RunOutput Output { get; } = new();
        /// <summary>这次启动自己的日志（多开时各存各的）。</summary>
        public RunLog? Log { get; set; }
        public string WorkDir { get; set; } = "";
        /// <summary>运行期间有另一局游戏用着同一个游戏目录：latest.log、崩溃报告可能是那一局的。</summary>
        public bool SharedWorkDir { get; set; }
        /// <summary>这一局用的是烘焙后的 classpath。</summary>
        public bool Baked { get; set; }
        /// <summary>Problems the universal loader reported while this baked run was going (for example Mixins it switched off).</summary>
        public List<Amber.Core.Bake.BakeRuntimeProblem> BakeProblems { get; } = [];
        /// <summary>Where the loader writes this run's problems (interface 1.3); null for an older loader, whose text lines are read instead.</summary>
        public string? BakeProblemsFile { get; set; }

        /// <summary>Adds a runtime problem once (the same one comes on stdout and in the problems file). True when it is new.</summary>
        public bool AddBakeProblem(Amber.Core.Bake.BakeRuntimeProblem problem)
        {
            lock (BakeProblems)
            {
                if (BakeProblems.Any(p => p.Key == problem.Key)) return false;
                BakeProblems.Add(problem);
                return true;
            }
        }
        /// <summary>这一局的状态机（正在玩胶囊、活动中心“运行记录”看它）。</summary>
        public LaunchSession Session { get; } = new(id, DateTimeOffset.Now);
    }

    /// <summary>
    /// 烘焙后的启动很可能是被烘焙弄崩的：用了烘焙、不是玩家结束的、非 0 退出，而且没看到进入主界面的标志
    /// （只靠兜底时间“猜”进入的也算没进）。烘焙出错时已经按普通方式启动，这里管的是烘焙成功但游戏崩溃。
    /// </summary>
    /// <summary>不是玩家结束的、退出码 0，却还没进主界面（或只是按时间猜的“进了”）：游戏多半根本没打开窗口。</summary>
    internal static bool ExitedBeforeGame(bool stoppedByUser, int exitCode, bool inGame, bool inGameGuessed) =>
        !stoppedByUser && exitCode == 0 && (!inGame || inGameGuessed);

    /// <summary>崩溃分析里有“确定”的原因（比如显卡不支持 OpenGL）；只有“可能”的猜测不算。</summary>
    internal static bool HasClearCause(CrashAnalysis analysis) =>
        analysis.Findings.Count > 0 && analysis.Findings[0].Severity == CrashSeverity.Likely;

    /// <summary>
    /// The mods the loader had to switch Mixins off for, or reported an error for, in this run, as one list ("" when
    /// none). Informational lines (a bridged Fabric API module, a launch-time rebake) don't count.
    /// </summary>
    private static string BakeProblemMods(GameRun run)
    {
        List<string> mods;
        lock (run.BakeProblems)
            mods = run.BakeProblems.Where(p => (p.DisablesFeatures || p.IsError) && p.Mod.Length > 0).Select(p => p.Mod).Distinct(StringComparer.Ordinal).ToList();
        return string.Join(L.T("core.mp.format.listseparator"), mods.Take(3)) + (mods.Count > 3 ? "…" : "");
    }

    /// <summary>
    /// After a baked run: the problems file has every problem even when the game died before stdout was read to the
    /// end, so it is merged in; new ones go to the log too.
    /// </summary>
    private void MergeBakeProblemsFile(GameRun run)
    {
        if (run.BakeProblemsFile is not { } file || !File.Exists(file)) return;
        foreach (var problem in Amber.Core.Bake.BakeRuntimeProblem.ReadFile(file))
            if (run.AddBakeProblem(problem)) _log.AddRaw(run.Log, "[Bake] " + problem.Describe());
    }

    internal static bool LikelyBakeCrash(bool baked, bool stoppedByUser, int exitCode, bool inGame, bool inGameGuessed) =>
        baked && !stoppedByUser && exitCode != 0 && (!inGame || inGameGuessed);

    /// <summary>“强制结束”是给哪一局的（不是“最近一局”：期间可能又启动了一局）。</summary>
    private GameRun? _forceKillRun;

    /// <summary>运行计时上次写的状态：Tick 只覆盖自己写的，别的消息（第二局启动失败、崩溃）留着。</summary>
    private StatusMessage? _ownedStatus;

    /// <summary>崩溃卡对应的那一局的日志：“查看日志”打开它。</summary>
    private RunLog? _crashLog;

    private readonly List<GameRun> _runs = [];
    private GameRun? Latest => _runs.Count > 0 ? _runs[^1] : null;

    /// <summary>The instance of the most recent run still going (the one the progress is about).</summary>
    public string? LatestInstance => Latest?.Id;

    /// <summary>The instance of the most recent start, running or over: the status text is about it.</summary>
    public string? LastRunInstance { get; private set; }

    private readonly Amber.Core.Activity.ProgressMeter _launchMeter = new();

    /// <summary>Download speed while game files are being fetched; null otherwise.</summary>
    public double? LaunchBytesPerSecond => IsPreparing ? _launchMeter.BytesPerSecond : null;

    /// <summary>Time left for the current preparation step, when it can be told.</summary>
    public TimeSpan? LaunchRemaining => IsPreparing ? _launchMeter.Remaining : null;

    /// <summary>进行中的启动有几个（自检用）。</summary>
    internal int RunCount => _runs.Count;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    partial void OnCrashChanged(CrashAnalysis? value) => _crashGeneration++;

    protected override void OnLanguageChanged()
    {
        UpdateRunState();
        if (Crash is null || _crashInput is not { } input) return;
        var generation = _crashGeneration;
        _ = Task.Run(input.Analyze).ContinueWith(
            t => { if (t.IsCompletedSuccessfully && Crash is not null && generation == _crashGeneration) Crash = t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public override string Title => L.T("home.title");
    public override string Icon => "M8 5v14l11-7z";

    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    public partial string? SelectedVersion { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountHint))]
    public partial Account? SelectedAccount { get; set; }

    /// <summary>启动按钮下面的一行：“正版 · Steve”，免得拿离线账号去进正版服。</summary>
    public string AccountHint => SelectedAccount is { } a
        ? L.F(a.Kind switch { AccountKind.Microsoft => "home.account.online", AccountKind.External => "home.account.external", _ => "home.account.offline" }, a.Name)
        : "";


    [ObservableProperty]
    public partial string VersionInfo { get; set; } = "";

    /// <summary>有启动在进行（准备中或游戏在运行）。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>最近一次启动还在准备（检查文件、Java、账号）。</summary>
    [ObservableProperty]
    public partial bool IsPreparing { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>点过“退出游戏”，等它保存退出。</summary>
    [ObservableProperty]
    public partial bool IsExiting { get; set; }

    /// <summary>退出等太久了，给出“强制结束”。</summary>
    [ObservableProperty]
    public partial bool ShowForceKill { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>时长不知道的阶段（准备里没有文件进度、游戏加载中、正在退出）用不确定进度条。</summary>
    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial bool ShowProgress { get; set; }

    /// <summary>主按钮的文字：不禁用，缺什么就写什么（先添加账号、先安装游戏），运行中变成退出游戏。</summary>
    [ObservableProperty]
    public partial string PrimaryLabel { get; set; } = "";

    /// <summary>游戏在运行时又要启动：先问一声（多开是允许的，测联机常用）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingLaunch), nameof(PendingLaunchText))]
    public partial LaunchRequest? PendingLaunch { get; set; }

    private string _pendingRunningId = "";
    public bool HasPendingLaunch => PendingLaunch is not null;
    public string PendingLaunchText => L.F("home.multi.warning", _pendingRunningId);

    /// <summary>主按钮在缺账号、缺实例时去的地方，崩溃卡的“查看日志”（MainViewModel 接上）。</summary>
    public Action? NavigateToAccounts { get; set; }
    public Action? NavigateToNewInstance { get; set; }
    public Action? NavigateToLog { get; set; }

    /// <summary>Opens an instance's page: the crash toast's "show", and the multi-launch question when nobody else asks it.</summary>
    public Action<string>? NavigateToInstance { get; set; }

    /// <summary>True while that instance's page is on screen (its last-session panel already says what a toast would).</summary>
    public Func<string, bool>? IsShowingInstance { get; set; }

    /// <summary>The engine has no page of its own: the instance page shows its state.</summary>
    public override bool HasView => false;

    /// <summary>新的一局开始了（启动协调器记进运行记录、更新正在玩胶囊）。</summary>
    public event Action<LaunchSession>? SessionStarted;

    /// <summary>别的实例在跑时又要开：发到请求条问一声（参数：在跑的实例）。没人接时停在启动页的卡片上。</summary>
    public event Action<string>? MultiLaunchRequested;

    /// <summary>600ms 内连点开始只算一次。</summary>
    private readonly LaunchGate _gate = new();

    /// <summary>上一次启动失败：原因和修复按钮（重新登录、下载 Java、切镜像重试、重试、查看日志、导出错误报告）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial LaunchFailureCard? Failure { get; set; }

    public bool HasFailure => Failure is not null;

    /// <summary>游戏进程起来了 / 退出了（参数：是不是崩溃）。窗口按“启动后启动器”的设置最小化、恢复。</summary>
    public event Action? GameStarted;
    public event Action<bool>? GameExited;

    /// <summary>某一局崩溃了（参数：实例）。多开时别的游戏还在跑，GameExited 不会来，窗口靠这个恢复。</summary>
    public event Action<string>? GameCrashed;

    protected override StatusMessage? DefaultStatus => StatusMessage.Info("home.status.ready");

    private void SetIdleStatus(string key) => SetStatus(StatusMessage.Info(key));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrash))]
    public partial CrashAnalysis? Crash { get; set; }

    public bool HasCrash => Crash is not null;

    /// <summary>The first-run steps (account, then a game); the first-run wizard shows them.</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary>Opens Settings at the loader and starts "Download loader" (set by the main view model).</summary>
    public Action? DownloadBakeLoader { get; set; }

    public HomeViewModel(LauncherService service, LogViewModel log)
    {
        _service = service;
        _log = log;
        Onboarding = new OnboardingViewModel(service);
        service.InstalledVersionsChanged += () => Dispatcher.UIThread.Post(RefreshVersions);
        service.AccountsChanged += RefreshAccounts;
        service.LaunchRequested += request => Dispatcher.UIThread.Post(() => _ = RunAsync(request));
        service.CurrentInstanceChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_service.CurrentInstance is { } id && Versions.Contains(id)) SelectedVersion = id;
        });
        // 安装开始、完成、取消、失败、修复：“安装未完成”“修复后启动”这些标注和按钮文字跟着刷新。
        service.InstanceStateChanged += id => Dispatcher.UIThread.Post(() =>
        {
            if (id == SelectedVersion) OnSelectedVersionChanged(SelectedVersion);
        });
        _clock.Tick += (_, _) => Tick();
        RefreshVersions();
        RefreshAccounts();
        UpdateRunState();
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnSelectedVersionChanged(SelectedVersion);
            OnPropertyChanged(nameof(AccountHint));
            OnPropertyChanged(nameof(PendingLaunchText));
        });
    }

    private void RefreshAccounts()
    {
        _refreshingAccounts = true;
        Accounts.Clear();
        foreach (var a in _service.Accounts.Store.Accounts) Accounts.Add(a);
        SelectedAccount = _service.Accounts.Store.Selected;
        _refreshingAccounts = false;
        UpdateRunState();
    }

    partial void OnSelectedAccountChanged(Account? value)
    {
        UpdateRunState();
        if (_refreshingAccounts || value is null || value.Id == _service.Accounts.Store.SelectedId) return;
        _service.Accounts.Store.SelectedId = value.Id;
        _service.Accounts.Store.Save();
        _service.NotifyAccountsChanged();
    }

    private void RefreshVersions()
    {
        Versions.Clear();
        foreach (var v in _service.InstalledVersions()) Versions.Add(v);
        SelectedVersion = _service.CurrentOr(Versions, Versions.FirstOrDefault());
        if (Versions.Count == 0) SetIdleStatus("home.status.no_versions");
        // 新手卡或下载页刚装好第一个实例：“还没有安装实例”不能一直挂到重开启动器。
        else if (StatusInfo?.Key == "home.status.no_versions") SetStatus(null);
        UpdateRunState();
    }

    partial void OnSelectedVersionChanged(string? value)
    {
        VersionInfo = "";
        UpdateRunState();
        if (value is null) return;
        _service.SetCurrentInstance(value);
        try
        {
            var instance = _service.GetInstance(value);
            var loader = instance.Loader is null ? L.T("home.version.vanilla") : $"{instance.Loader} {instance.LoaderVersion}";
            var mods = new ModManager(instance.ModsDir).CountEnabled();
            VersionInfo = _service.WithStateNote(value, L.F("home.version.info", instance.McVersion, loader, mods == 0 ? "" : L.P("home.version.mod_count", mods, mods)));
        }
        catch (Exception e) when (Amber.Core.Game.GameDirectory.IsUnreadable(e))
        {
            VersionInfo = L.F("home.version.read_failed", e.Message);
        }
    }

    /// <summary>按最近一次启动的阶段和当前选择，重算主按钮文字和进度条。</summary>
    /// <summary>The latest session of each instance in this run of Jay (instance page start button).</summary>
    private readonly Dictionary<string, LaunchSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public LaunchSession? SessionOf(string id) => _sessions.GetValueOrDefault(id);

    /// <summary>Force quit is being offered for this instance's session.</summary>
    public bool ForceKillOfferedFor(string id) => _forceKillRun is { } r && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Any run started, changed phase or ended. On the UI thread.</summary>
    public event Action? RunStateChanged;

    /// <summary>Ctrl+Shift+Q (§8.5): ask the newest game that is actually playing to save and quit. False when none is.</summary>
    public bool QuitRunningGame()
    {
        if (_runs.LastOrDefault(r => r.Game is { HasExited: false } && !r.Exiting) is not { } run) return false;
        ExitGame(run);
        return true;
    }

    /// <summary>The instance page's side button: cancel while preparing, otherwise ask the game to save and quit.</summary>
    public void StopRun(string id)
    {
        if (_runs.LastOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } run) return;
        if (run.Game is null) CancelRun(run);
        else if (!run.Exiting) ExitGame(run);
    }

    /// <summary>The instance whose session the crash panel is about.</summary>
    [ObservableProperty]
    public partial string? CrashInstance { get; set; }

    /// <summary>When that session ended.</summary>
    public DateTimeOffset? CrashTime { get; private set; }

    private void UpdateRunState()
    {
        var run = Latest;
        IsBusy = _runs.Count > 0;
        IsPreparing = run is { Game: null };
        IsRunning = run is { Game: not null };
        IsExiting = run is { Exiting: true };
        // “强制结束”只对给出它的那一局有效：它不再是最近一局、已经退出了，就收回。
        if (_forceKillRun is { } offered && (offered != run || !_runs.Contains(offered) || offered.Game is not { HasExited: false }))
            _forceKillRun = null;
        ShowForceKill = _forceKillRun is not null;
        ShowProgress = run is not null && (run.Game is null || !run.InGame || run.Exiting);
        ProgressIndeterminate = run is not null && (run.Game is not null || Progress <= 0);
        PrimaryLabel = L.T(PrimaryKey(run));
        Onboarding.Update(Accounts.Count == 0, Versions.Count == 0, SelectedAccount?.Name);
        if (IsRunning) _clock.Start();
        else _clock.Stop();
        RunStateChanged?.Invoke();
    }

    private string PrimaryKey(GameRun? run) => run is not null ? RunKey(run) : IdleKey(SelectedVersion);

    private static string RunKey(GameRun run) =>
        run.Game is null ? "home.button.cancel_launch" : run.Exiting ? "home.button.exiting" : "home.button.exit_game";

    private string IdleKey(string? id)
    {
        if (SelectedAccount is null) return "home.button.need_account";
        if (id is null) return "home.button.need_instance";
        return _service.Directory.StateOf(id) switch
        {
            Amber.Core.Game.InstanceState.Incomplete => "home.button.resume_install",
            Amber.Core.Game.InstanceState.Broken => "home.button.repair",
            _ => "home.launch",
        };
    }

    partial void OnProgressChanged(double value)
    {
        if (Latest is { Game: null }) ProgressIndeterminate = value <= 0;
    }

    /// <summary>主按钮：缺什么去补什么；准备中是取消，运行中是退出游戏。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PrimaryAsync()
    {
        if (Latest is { } run)
        {
            if (run.Game is null) CancelRun(run);
            else if (!run.Exiting) ExitGame(run);
            return;
        }
        if (SelectedAccount is null)
        {
            Toasts.Show(L.T("home.status.account_required"), Avalonia.Controls.Notifications.NotificationType.Warning);
            NavigateToAccounts?.Invoke();
            return;
        }
        if (SelectedVersion is not { } id)
        {
            NavigateToNewInstance?.Invoke();
            return;
        }
        await RunAsync(new LaunchRequest(id));
    }

    private GameRun? RunOf(string id) => _runs.LastOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The primary action for one instance (the library's continue card): cancel or quit that instance's own run, and
    /// otherwise start it. Another instance's run is never touched.
    /// </summary>
    public Task PrimaryForAsync(string id)
    {
        if (RunOf(id) is { } run)
        {
            if (run.Game is null) CancelRun(run);
            else if (!run.Exiting) ExitGame(run);
            return Task.CompletedTask;
        }
        if (SelectedAccount is null)
        {
            Toasts.Show(L.T("home.status.account_required"), Avalonia.Controls.Notifications.NotificationType.Warning);
            NavigateToAccounts?.Invoke();
            return Task.CompletedTask;
        }
        return RunAsync(new LaunchRequest(id));
    }

    /// <summary>The label of <see cref="PrimaryForAsync"/> for that instance.</summary>
    public string PrimaryLabelFor(string id) => L.T(RunOf(id) is { } run ? RunKey(run) : IdleKey(id));

    /// <summary>
    /// Ctrl+Enter（改版规格 7.1）：没在启动、没在运行时等于点主按钮；正在准备或运行时什么也不做（快捷键不拿来取消或退出游戏）。
    /// </summary>
    public bool LaunchShortcut()
    {
        if (Latest is not null || !PrimaryCommand.CanExecute(null)) return false;
        PrimaryCommand.Execute(null);
        return true;
    }

    [RelayCommand]
    private Task LaunchAsync() => SelectedVersion is { } id ? RunAsync(new LaunchRequest(id)) : Task.CompletedTask;

    [RelayCommand]
    private void LaunchAnyway()
    {
        if (PendingLaunch is not { } request) return;
        PendingLaunch = null;
        _ = RunAsync(request, allowSecond: true);
    }

    [RelayCommand]
    private void DismissPendingLaunch() => PendingLaunch = null;

    /// <summary>
    /// Before the first strong-sandbox launch: explain and ask whether to show Windows' admin prompt for the loopback
    /// exemption. Replaced in the self-check and tests.
    /// </summary>
    internal Func<Task<bool>> AskSandboxConsent { get; set; } = DefaultSandboxConsentAsync;

    private static Task<bool> DefaultSandboxConsentAsync() =>
        Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner }
            ? Views.ConfirmDialog.AskAsync(owner, L.T("home.sandbox.consent.title"), L.T("home.sandbox.consent.body"), L.T("home.sandbox.consent.confirm"))
            : Task.FromResult(false);

    private async Task RunAsync(LaunchRequest request, bool allowSecond = false)
    {
        Toasts.DismissBakeFailures();
        if (!BakeRetryDirectoryMatches(_service, request))
        {
            Toasts.Show(L.T("home.bake.retry_directory_changed"));
            return;
        }
        // 手抖连点：600ms 内只算一次（多开确认后的“仍然开始”是另一次点击，不受影响）。
        // Checked after the directory check: a request refused for another reason must not swallow the next one.
        if (!allowSecond && !_gate.TryPass(DateTimeOffset.Now)) return;
        if (_runs.Any(r => r.Game is null))
        {
            // Said in a toast: the start may have come from a library card or the command palette, where no status line is near.
            Toasts.Show(L.T("home.status.preparing_other"), Avalonia.Controls.Notifications.NotificationType.Warning);
            return;
        }
        if (!allowSecond && _runs.FirstOrDefault(r => r.Game is not null) is { } other)
        {
            _pendingRunningId = other.Id;
            PendingLaunch = request;
            // No page change (§2.5 rule 5): the question goes to the request bar.
            if (MultiLaunchRequested is { } ask) ask(other.Id);
            else NavigateToInstance?.Invoke(request.VersionId);
            return;
        }
        if (SelectedAccount is not { } account)
        {
            Toasts.WithAction(L.T("home.status.account_required"), L.T("home.button.need_account"), () => NavigateToAccounts?.Invoke(),
                Avalonia.Controls.Notifications.NotificationType.Warning);
            return;
        }
        var id = request.VersionId;
        LastRunInstance = id;
        _launchMeter.Reset();
        SelectedVersion = id;
        _service.SetCurrentInstance(id);

        // 这一局全程用开始时的游戏目录、安装器和 Java：准备到一半换了目录，也还是在原目录里补文件、启动。
        var ctx = request.ExpectedContext ?? _service.Context;
        var workDir = _service.WorkDirFor(ctx, id);
        var run = new GameRun(id) { WorkDir = workDir };
        // 每局一份日志：多开时第二局不会清掉第一局的，也不会混在一起。
        // 从准备阶段就开始记：补文件、Java、登录这些步骤出错时，日志页也看得到原因。
        run.Log = _log.BeginRun(id, DateTimeOffset.Now);
        run.Log.Store = RunLogStore.Begin(workDir, run.Log.StartedAt, _service.Settings.NoTrace);
        RunStarted?.Invoke(id);
        _runs.Add(run);
        _sessions[id] = run.Session;
        _lastRequest = request;
        SessionStarted?.Invoke(run.Session);
        Crash = null;
        Failure = null;
        Progress = 0;
        UpdateRunState();
        var ct = run.Cts.Token;
        var crashed = false;
        var gameStarted = false;
        try
        {
            var settings = _service.EffectiveSettings(ctx, id);
            try { await RepairIfBrokenAsync(ctx, id, ct); }
            finally { if (_service.IsCurrentDirectory(ctx)) _service.NotifyInstanceStateChanged(id); }
            SetStatus(StatusMessage.Info("home.status.checking_game_files"));
            await ctx.Installer.CompleteAsync(id, new Progress<InstallProgress>(p =>
            {
                if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0)
                {
                    _launchMeter.Add(DateTimeOffset.Now, (double)p.Files.CompletedFiles / p.Files.TotalFiles, p.Files.BytesDownloaded);
                    Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
                    Status = p.Files.BytesDownloaded > 0
                        ? L.F("home.status.completing_files", p.Files.CompletedFiles, p.Files.TotalFiles)
                        : L.F("home.status.checking_files", p.Files.CompletedFiles, p.Files.TotalFiles);
                }
            }), ct);
            // 文件补齐了：上次没装完的实例到这里也算装完了。
            try { _service.MarkInstallFinished(ctx, id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var version = ctx.Directory.ResolveVersion(id);
            var javaPath = settings.JavaPath;
            if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            {
                Progress = 0;
                ctx.Java.Status = new Progress<string>(text => Status = text);
                javaPath = await ctx.Java.ForVersionAsync(version, ct);
            }

            Progress = 0;
            SetStatus(StatusMessage.Info("home.status.verifying_account"));
            var session = await _service.Accounts.ResolveAsync(account, ct);

            var options = _service.BaseLaunchOptions(ctx, id, javaPath, session.Session, session.ExtraJvmArgs) with
            {
                JoinServer = request.JoinServer ?? settings.JoinServer,
                JoinWorld = request.JoinWorld,
            };
            // 联机页开了“仅游戏”转发：游戏的 HTTP 走本机 SOCKS5，要进的服务器另开一个经出口的转发端口。
            var (proxied, relayClient) = await ApplyGameProxyAsync(options);
            options = proxied;
            await using var relay = relayClient;
            var memory = options.MaxMemoryMb;

            if (_service.UsesUniversalLoader(ctx, id) && !request.SkipBake)
            {
                SetStatus(StatusMessage.Info("home.status.baking"));
                try
                {
                    var (baked, reason, bakeJava) = await _service.PrepareBakeAsync(ctx, id, javaPath, new Progress<Amber.Core.Bake.BakeEvent>(e =>
                    {
                        Status = e.Message;
                        if (e.IsProblem) _log.AddRaw(run.Log, "[Bake] " + e.Message);
                    }), ct);
                    if (reason.Length > 0) _log.AddRaw(run.Log, "[Bake] " + reason);
                    // Baking is on but no loader is installed: offer the download right where the launch status is.
                    // Before the loader is published there is nothing to download: the notice just says why baking was skipped.
                    if (baked is null && reason == L.T("core.bake.loader_missing"))
                    {
                        if (Amber.Core.Bake.BakeLoaderRelease.Pinned.IsPublished && DownloadBakeLoader is { } download)
                            Toasts.BakeAction(L.T("home.bake.loader_missing_notice"), L.T("home.bake.download_loader"), L.T("settings.bake.download_hint"), download);
                        else
                            Toasts.BakeProblem(L.T("home.bake.loader_missing_notice") + " " + L.T("core.bake.download_unpublished"));
                    }
                    if (baked is not null)
                    {
                        // The loader's launcher needs Java 21 even where the version asks for less.
                        options = options with { Baked = baked, JavaPath = bakeJava };
                        javaPath = bakeJava;
                        run.Baked = true;
                        run.BakeProblemsFile = baked.ProblemsFile;
                    }
                }
                catch (Exception e) when (e is Amber.Core.Bake.BakeException or IOException or InvalidDataException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException
                                           or InvalidOperationException or FormatException)
                {
                    // 实验功能失败时不挡住游戏：记下原因，按普通方式启动。
                    _log.AddRaw(run.Log, "[Bake] " + L.F("home.bake.fallback", e.Message));
                    Toasts.Show(L.T("home.bake.fallback_toast"));
                    // The reason (a missing dependency, an unsupported platform, a loader that is too old) in the launch status box too.
                    Toasts.BakeProblem(L.F("home.bake.fallback", e.Message));
                }
            }
            if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
            {
                SetStatus(StatusMessage.Info("home.status.pre_launch_command"));
                await RunUserCommandAsync(settings.PreLaunchCommand, id, workDir, run.Log, ct);
            }

            // The game sandbox (privacy plan item 15): on Windows this picks standard or strong, and the first strong
            // launch explains the one admin prompt. A fallback is said once as a toast and on every launch in the log.
            var fallbackBefore = _service.Settings.StrongSandboxFallback;
            options = options with
            {
                Sandbox = await _service.SandboxForLaunchAsync(javaPath, AskSandboxConsent, note => _log.AddRaw(run.Log, "[Sandbox] " + note), ct),
            };
            if (_service.Settings.StrongSandboxFallback is { } fallback && fallback != fallbackBefore)
                Toasts.Show(L.F("home.sandbox.standard_because", WindowsSandboxPolicy.Describe(fallback, _service.Settings.StrongSandboxFallbackDetail)));

            SetOwnedStatus(StatusMessage.Info("home.status.launching"));
            run.Output.Begin(options);
            _log.AddRaw(run.Log, L.F("home.log.launch", id, javaPath, memory));
            foreach (var note in _proxyNotes) _log.AddRaw(run.Log, note);
            var parser = new GameLogParser();
            var launcher = new GameLauncher(ctx.Directory);
            using var running = _service.MarkRunning(workDir);
            var game = launcher.Launch(options, line =>
            {
                lock (parser)
                {
                    run.Output.Add(line);
                    if (parser.Feed(line) is { } entry) _log.Add(run.Log, entry);
                    if (run.Baked && Amber.Core.Bake.BakeRuntimeProblem.Parse(line, legacy: run.BakeProblemsFile is null) is { } bakeProblem
                        && run.AddBakeProblem(bakeProblem))
                        _log.AddRaw(run.Log, "[Bake] " + bakeProblem.Describe());
                }
                if (!run.InGame && IsEnterGameLine(line)) Dispatcher.UIThread.Post(() => EnterGame(run, guessed: false));
                _service.NotifyGameOutput(id, line);
            });
            run.Started = DateTime.UtcNow;
            run.Game = game;
            run.Session.ProcessUp(DateTimeOffset.Now);
            // 同一个游戏目录里同时开着两局：两局的崩溃报告、latest.log 分不清，崩溃分析只用各自的输出。
            foreach (var sibling in _runs.Where(r => r != run && r.Game is not null && string.Equals(r.WorkDir, workDir, StringComparison.OrdinalIgnoreCase)))
                sibling.SharedWorkDir = run.SharedWorkDir = true;
            UpdateRunState();
            Tick();
            gameStarted = true;
            GameStarted?.Invoke();
            await game.WaitForExitAsync();

            var code = game.ExitCode;
            if (run.Baked) MergeBakeProblemsFile(run);
            // 退出码 0 但没进主界面：比如没有 OpenGL 时游戏弹个框就“正常”退出了，也要看一眼日志。
            CrashInput? input = null;
            CrashAnalysis? analysis = null;
            if (!run.StoppedByUser && (code != 0 || ExitedBeforeGame(run.StoppedByUser, code, run.InGame, run.InGameGuessed)))
            {
                lock (parser) input = CrashInputFor(run, SafePid(game));
                var pending = input;
                analysis = await Task.Run(pending.Analyze);
            }

            run.Session.ProcessEnded(DateTimeOffset.Now, code, crashed: !run.StoppedByUser && input is not null && analysis is not null
                && !(code == 0 && !HasClearCause(analysis)), analysis?.Findings.FirstOrDefault()?.Title);
            if (run.StoppedByUser)
            {
                SetStatus(StatusMessage.Info("home.status.stopped"));
            }
            else if (input is null || analysis is null)
            {
                SetStatus(StatusMessage.Info("home.status.exited"));
            }
            else if (code == 0 && !HasClearCause(analysis))
            {
                // 日志里没有明确原因：不弹崩溃卡，只说清楚游戏没进主界面就退了。
                SetStatus(StatusMessage.Warning("home.status.exited_early"));
                _log.AddRaw(run.Log, "[Jay] " + L.T("home.status.exited_early"));
            }
            else
            {
                crashed = true;
                SetStatus(code == 0
                    ? StatusMessage.Error("home.status.exited_early_cause", analysis.Findings[0].Title)
                    : StatusMessage.Error("home.status.crashed", code));
                RememberCrash(run, input, analysis);
                if (LikelyBakeCrash(run.Baked, run.StoppedByUser, code, run.InGame, run.InGameGuessed))
                {
                    // 烘焙成功了但游戏没进主界面就退出：多半是加载器原型不兼容某个模组（比如 Sodium 的 Mixin），给一个不烘焙再启动的入口。
                    _log.AddRaw(run.Log, "[Bake] " + L.T("home.bake.crashed"));
                    Toasts.BakeFailed(BakeProblemMods(run) is { Length: > 0 } suspects ? L.F("home.bake.crashed_mixin_toast", id, suspects) : L.F("home.bake.crashed_toast", id), () =>
                    {
                        if (!TryRequestBakeRetry(_service, ctx, request))
                            Toasts.Show(L.T("home.bake.retry_directory_changed"));
                    });
                }
                // 玩家在别的页面：弹个出错级的 Toast（不自动消失），带“查看”。
                if (IsShowingInstance?.Invoke(id) != true) Toasts.WithAction(L.F("home.crash.toast", id), L.T("home.crash.show"), () => NavigateToInstance?.Invoke(id), Avalonia.Controls.Notifications.NotificationType.Error);
                GameCrashed?.Invoke(id);
            }

            // A baked run that did not crash but had Mixins switched off (or a mod whose Fabric entrypoints never ran):
            // say which mods may be missing features, with a way back.
            if (!crashed && BakeProblemMods(run) is { Length: > 0 } partial)
            {
                int count;
                lock (run.BakeProblems) count = run.BakeProblems.Count(p => p.DisablesFeatures || p.IsError);
                Toasts.BakeFailed(L.F("home.bake.mixin_disabled", count, partial), () =>
                {
                    if (!TryRequestBakeRetry(_service, ctx, request))
                        Toasts.Show(L.T("home.bake.retry_directory_changed"));
                });
            }
            // The launch had to bake something itself: warm again in the background so the next launch starts from a full cache.
            bool rebaked;
            lock (run.BakeProblems) rebaked = run.BakeProblems.Any(p => p.AsksForWarm);
            if (run.Baked && rebaked) _service.WarmBakeInBackground(ctx, id);
            if (!string.IsNullOrWhiteSpace(settings.PostExitCommand))
            {
                // The game has exited, so its run ends now: the start buttons, the multi-launch question and Ctrl+Shift+Q
                // stop treating it as running while the command works. The work folder stays marked in use until the
                // command is done, and the command's output still goes to this run's log.
                EndRun(run, gameStarted, crashed);
                await RunUserCommandAsync(settings.PostExitCommand, id, workDir, run.Log, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Session.Cancel(DateTimeOffset.Now);
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        // 其他的取消异常不是玩家点的：多半是连接超时（HttpClient 的 ConnectTimeout 也抛 TaskCanceledException），按失败显示原因。
        catch (Exception e)
        {
            run.Session.Fail(DateTimeOffset.Now, ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("home.status.launch_failed", e, ErrorText.Of(e)));
            _log.AddRaw(run.Log, "[Jay] " + e);
            // 游戏没起来才给修复卡；进程起来以后的异常（比如退出后的命令出错）不算启动失败。
            if (!gameStarted) ShowFailure(run, request, e);
        }
        finally
        {
            FinishStore(run, crashed);
            if (_runs.Contains(run)) EndRun(run, gameStarted, crashed);
            run.Cts.Dispose();
        }
    }

    /// <summary>The run is over (its game exited, or it never started): it no longer counts as running.</summary>
    private void EndRun(GameRun run, bool gameStarted, bool crashed)
    {
        _runs.Remove(run);
        Progress = 0;
        UpdateRunState();
        if (gameStarted && _runs.Count == 0) GameExited?.Invoke(crashed);
    }

    /// <summary>A run began (its saved log exists from now on): the Logs tab lists it.</summary>
    public event Action<string>? RunStarted;

    /// <summary>A run ended and its saved log is complete.</summary>
    public event Action<string>? RunFinished;

    /// <summary>Writes how the run ended next to its saved log (Logs tab › run records).</summary>
    private void FinishStore(GameRun run, bool crashed)
    {
        if (run.Log?.Store is not { } store) return;
        var session = run.Session;
        var outcome = session.State switch
        {
            LaunchState.Crashed => RunOutcome.Crashed,
            LaunchState.Failed => RunOutcome.Failed,
            LaunchState.Cancelled => RunOutcome.Stopped,
            _ when crashed => RunOutcome.Crashed,
            _ when run.StoppedByUser => RunOutcome.Stopped,
            _ => RunOutcome.Exited,
        };
        store.Finish(session.Ended ?? DateTimeOffset.Now, outcome, session.ExitCode, session.Reason, LatestCrashReport(run));
        store.Dispose();
        RunFinished?.Invoke(run.Id);
    }

    /// <summary>The crash report the game wrote during this run, if any.</summary>
    private static string? LatestCrashReport(GameRun run)
    {
        try
        {
            var dir = Path.Combine(run.WorkDir, "crash-reports");
            if (!Directory.Exists(dir)) return null;
            var started = run.Log?.StartedAt.UtcDateTime ?? run.Started;
            return new DirectoryInfo(dir).EnumerateFiles("crash-*.txt").Where(f => f.LastWriteTimeUtc >= started)
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>旧目录的烘焙提示不能启动新目录里同名的实例；只跳过这一次烘焙。</summary>
    internal static bool TryRequestBakeRetry(LauncherService service, GameContext ctx, LaunchRequest request)
    {
        if (!service.IsCurrentDirectory(ctx)) return false;
        service.RequestLaunch(request with { SkipBake = true, ExpectedContext = ctx });
        return true;
    }

    internal static bool BakeRetryDirectoryMatches(LauncherService service, LaunchRequest request) =>
        request.ExpectedContext is not { } expected || service.IsCurrentDirectory(expected);

    /// <summary>“已损坏”最常见的原因是继承的原版不在了：补装原版。版本文件本身坏了修不了，提示重新安装。</summary>
    private async Task RepairIfBrokenAsync(GameContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Directory.StateOf(id) != Amber.Core.Game.InstanceState.Broken) return;
        Amber.Core.Game.VersionJson own;
        try { own = ctx.Directory.ReadVersion(id); }
        catch (Exception e) when (Amber.Core.Game.GameDirectory.IsUnreadable(e))
        {
            throw new InvalidDataException(L.T("instance.state.broken"), e);
        }
        if (own.InheritsFrom is not { } parent || File.Exists(ctx.Directory.VersionJsonPath(parent)))
            throw new InvalidDataException(L.T("instance.state.broken"));
        SetStatus(StatusMessage.Info("home.status.repairing", parent));
        await ctx.Installer.InstallAsync(parent, new Progress<InstallProgress>(p =>
        {
            if (p.Stage == InstallStage.Files && p.Files.TotalFiles > 0) Progress = 100.0 * p.Files.CompletedFiles / p.Files.TotalFiles;
        }), ct);
    }

    /// <summary>日志里表示“已经到了游戏主界面”的几行（新版本是声音引擎，老版本是 OpenAL）。</summary>
    internal static bool IsEnterGameLine(string line) =>
        line.Contains("Sound engine started", StringComparison.Ordinal) || line.Contains("OpenAL initialized", StringComparison.Ordinal);

    private void EnterGame(GameRun run, bool guessed)
    {
        if (run.InGame) return;
        run.InGame = true;
        run.Session.EnteredGame(DateTimeOffset.Now);
        run.InGameGuessed = guessed;
        UpdateRunState();
        Tick();
    }

    private void SetOwnedStatus(StatusMessage message)
    {
        SetStatus(message);
        _ownedStatus = message;
    }

    /// <summary>
    /// 每秒更新一次运行状态：加载中显示已等了多久，进了游戏显示运行时长。
    /// 只覆盖自己（或启动流程里“正在启动”这类）写的状态：另一局启动失败、崩溃的消息不能一秒后就被冲掉。
    /// </summary>
    private void Tick()
    {
        if (Latest is not { Game: not null } run || run.Exiting) return;
        if (!ReferenceEquals(StatusInfo, _ownedStatus)) return;
        var elapsed = DateTime.UtcNow - run.Started;
        if (!run.InGame && elapsed >= EnterGameFallback)
        {
            EnterGame(run, guessed: true);
            return;
        }
        if (!run.InGame)
            SetOwnedStatus(StatusMessage.Info("home.status.loading_game", (int)elapsed.TotalSeconds));
        else if (run.InGameGuessed)
            SetOwnedStatus(StatusMessage.Info("home.status.maybe_in_game", run.Id, (int)elapsed.TotalMinutes));
        else
            SetOwnedStatus(StatusMessage.Info("home.status.in_game", run.Id, (int)elapsed.TotalMinutes));
    }

    /// <summary>Before a confirmed close: cancels launches still preparing. Games already running keep running.</summary>
    public void CancelPreparingForExit()
    {
        foreach (var run in _runs.Where(r => r.Game is null).ToList()) CancelRun(run);
    }

    private void CancelRun(GameRun run)
    {
        run.StoppedByUser = true;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>像点窗口的关闭按钮一样让游戏自己保存退出；等太久了再给“强制结束”。</summary>
    private void ExitGame(GameRun run)
    {
        if (run.Game is not { } game) return;
        run.StoppedByUser = true;
        run.Exiting = true;
        run.Session.ExitRequested(DateTimeOffset.Now);
        UpdateRunState();
        SetOwnedStatus(StatusMessage.Info("home.status.exiting", run.Id));
        ExitRequest request;
        try { request = GameLauncher.RequestExitDetailed(game); }
        catch (InvalidOperationException) { request = ExitRequest.NotSent; }
        // Linux 上找不到能关的游戏窗口（Wayland）时发的是结束信号：新版本照常保存，老版本可能来不及。
        if (request == ExitRequest.Signal) SetOwnedStatus(StatusMessage.Info("home.status.exiting_signal", run.Id));
        if (request == ExitRequest.NotSent)
        {
            // 没有能关的主窗口（还在加载、或者是无窗口的老版本）：游戏没收到退出请求，还在正常运行，
            // “正在退出”撤回；直接给出这一局的强制结束。
            run.StoppedByUser = false;
            run.Exiting = false;
            OfferForceKill(run);
            Tick();
            return;
        }
        _ = Task.Delay(ForceKillDelay).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_runs.Contains(run) && run.Exiting && run.Game is { HasExited: false }) OfferForceKill(run);
        }), TaskScheduler.Default);
    }

    private void OfferForceKill(GameRun run)
    {
        _forceKillRun = run;
        UpdateRunState();
    }

    /// <summary>强制结束给出“强制结束”的那一局（Flyout 里确认过）。那一局已经结束、或者不再是显示的那一局时什么都不做。</summary>
    [RelayCommand]
    private void ForceKill()
    {
        var run = _forceKillRun;
        _forceKillRun = null;
        ShowForceKill = false;
        if (run is null || !_runs.Contains(run)) return;
        Kill(run);
    }

    /// <summary>立即结束最近一次启动：准备中取消，运行中结束进程（自检和强制结束用）。</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (Latest is not { } run) return;
        Kill(run);
    }

    private void Kill(GameRun run)
    {
        CancelRun(run);
        try
        {
            if (run.Game is { HasExited: false } game) game.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    // ---- 测试用：不真的启动游戏，直接放进一局 ----

    internal object AddRunForTest(string id, Process game, string workDir = "")
    {
        var run = new GameRun(id) { Game = game, WorkDir = workDir, Started = DateTime.UtcNow };
        _runs.Add(run);
        _sessions[id] = run.Session;
        LastRunInstance = id;
        SessionStarted?.Invoke(run.Session);
        UpdateRunState();
        return run;
    }

    internal static LaunchSession SessionForTest(object run) => ((GameRun)run).Session;

    /// <summary>Self-check: put a session for an instance into a given state without a game process.</summary>
    internal LaunchSession SetSessionForTest(string id, LaunchSession session)
    {
        _sessions[id] = session;
        SessionStarted?.Invoke(session);
        RunStateChanged?.Invoke();
        return session;
    }

    /// <summary>Self-check: show the crash panel for an instance with a ready-made analysis.</summary>
    internal void ShowCrashForTest(string id, CrashAnalysis analysis)
    {
        CrashInstance = id;
        CrashTime = DateTimeOffset.Now;
        Crash = analysis;
    }

    /// <summary>A crash as the window sees it (journeys self-check): the last session plus <see cref="GameCrashed"/>.</summary>
    internal void SimulateCrashForTest(string id, CrashAnalysis analysis)
    {
        ShowCrashForTest(id, analysis);
        GameCrashed?.Invoke(id);
    }

    /// <summary>Self-check: a start that is still fetching game files, without starting anything.</summary>
    internal object AddPreparingRunForTest(string id)
    {
        var run = new GameRun(id);
        _runs.Add(run);
        LastRunInstance = id;
        _launchMeter.Reset();
        UpdateRunState();
        return run;
    }

    /// <summary>Self-check: the game-file download of the preparing start is this far at this moment.</summary>
    internal void ReportFilesForTest(int done, int total, long bytes, DateTimeOffset at)
    {
        _launchMeter.Add(at, (double)done / total, bytes);
        Progress = 100.0 * done / total;
        Status = L.F("home.status.completing_files", done, total);
    }

    internal static bool CancelledForTest(object run) => ((GameRun)run).Cts.IsCancellationRequested;

    internal void RemoveRunForTest(object run)
    {
        _runs.Remove((GameRun)run);
        UpdateRunState();
    }

    internal void ExitGameForTest(object run) => ExitGame((GameRun)run);
    internal void OfferForceKillForTest(object run) => OfferForceKill((GameRun)run);
    internal static bool IsExitingForTest(object run) => ((GameRun)run).Exiting;
    internal static bool StoppedByUserForTest(object run) => ((GameRun)run).StoppedByUser;
    internal void TickForTest() => Tick();
    internal void SetOwnedStatusForTest(StatusMessage message) => SetOwnedStatus(message);

    internal static void BeginRunOutputForTest(object run, LaunchOptions options) => ((GameRun)run).Output.Begin(options);
    internal static void AddRunOutputForTest(object run, string line) => ((GameRun)run).Output.Add(line);
    internal static string[] RunOutputForTest(object run) => ((GameRun)run).Output.Lines;
    internal void RememberCrashForTest(object run)
    {
        var gameRun = (GameRun)run;
        var input = CrashInputFor(gameRun, null);
        RememberCrash(gameRun, input, input.Analyze());
    }

    private static int? SafePid(Process game)
    {
        try { return game.Id; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>每个磁盘附件最多保留末尾 1 MiB；一局最多选一份崩溃报告、一份 JVM 日志和 latest.log。</summary>
    internal const int MaxCrashAttachmentBytes = 1024 * 1024;

    private static CrashInput CrashInputFor(GameRun run, int? pid)
    {
        // 必须在让出界面线程前读取：下一局可能马上复用这个目录并覆盖 latest.log。
        var attachments = CrashAnalyzer.FindRunFiles(run.WorkDir, run.Started, pid, run.SharedWorkDir)
            .Select(path => SnapshotAttachment(path, run.Output.NoTrace)).OfType<CrashAttachment>().ToArray();
        return new(run.Output.Lines, run.Output.ForExport().ToArray(), attachments);
    }

    private sealed record CrashAttachment(string SourcePath, string Text, string ExportText, bool Truncated)
    {
        public string Name => Truncated
            ? Path.GetFileNameWithoutExtension(SourcePath) + ".tail" + Path.GetExtension(SourcePath)
            : Path.GetFileName(SourcePath);
    }

    private static CrashAttachment? SnapshotAttachment(string path, bool noTrace)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            var truncated = length > MaxCrashAttachmentBytes;
            var bytes = new byte[(int)Math.Min(length, MaxCrashAttachmentBytes)];
            stream.Position = length - bytes.Length;
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            using var reader = new StreamReader(new MemoryStream(bytes, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            // 截断可能落在 UTF-8 字符或消息中间，第一条不完整的物理行不保留。
            if (truncated) text = text.IndexOf('\n') is var newline and >= 0 ? text[(newline + 1)..] : "";
            var export = noTrace ? FilterAttachment(text, truncated) : text;
            return new(path, text, export, truncated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string FilterAttachment(string text, bool truncated)
    {
        var filter = new ChatLogFilter.OutputFilter();
        // 被裁掉的首行可能带着 [CHAT]：直到下一条明确的日志事件前，都视作聊天的续行。
        if (truncated) filter.Add("[00:00:00] [main/INFO]: [CHAT]");
        var lines = new List<ChatLogFilter.OutputLine>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) lines.Add(filter.Add(line));
        var kept = lines.Where(l => !l.IsChat).Select(l => l.Text).ToList();
        return kept.Count == 0 ? "" : string.Join('\n', kept) + "\n";
    }

    /// <summary>崩溃分析保留原始输入；导出单独保存这一局已经过滤好的快照，后续启动和设置变化都不能替换它。</summary>
    private sealed record CrashInput(string[] Output, string[] ExportOutput, CrashAttachment[] Attachments)
    {
        public CrashAnalysis Analyze()
        {
            // 和原来的分析一样，每个文件最多看末尾 400,000 字；换语言也不重新读取可能已被复用的磁盘路径。
            var parts = Attachments.Select(a => a.Text.Length > 400_000 ? a.Text[^400_000..] : a.Text)
                .Append(string.Join('\n', Output));
            return CrashAnalyzer.Analyze(string.Join("\n\n", parts), Attachments.Select(a => a.SourcePath).ToList());
        }
    }

    private void RememberCrash(GameRun run, CrashInput input, CrashAnalysis analysis)
    {
        // 崩溃结果和“查看日志”继续绑定这一局；换语言时仍用原来的输入重新分析。
        _crashInput = input;
        _crashLog = run.Log;
        _lastRunFiles = [.. analysis.SourceFiles];
        _lastRunOutput = input.ExportOutput;
        _lastRunAttachments = input.Attachments;
        _log.AddRaw(run.Log, L.F("home.log.crash_analysis", analysis.Summary));
        CrashInstance = run.Id;
        CrashTime = run.Session.Ended ?? DateTimeOffset.Now;
        Crash = analysis;
    }

    /// <summary>崩溃卡上的“查看日志”：打开日志页，只看警告和错误。</summary>
    [RelayCommand]
    private void ViewLog()
    {
        _log.Show(_crashLog);
        _log.OnlyProblems = true;
        NavigateToLog?.Invoke();
    }

    /// <summary>用户自己配置的启动前 / 退出后命令。命令失败只记日志，不拦启动。</summary>
    internal async Task RunUserCommandAsync(string command, string id, string workDir, RunLog? log, CancellationToken ct)
    {
        // cmd 不按 C 规则解析引号，整条命令要原样放在 /s /c "…" 里。
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/s /c \"{command}\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = workDir;
        psi.Environment["AMBER_VERSION"] = id;
        psi.Environment["AMBER_GAME_DIR"] = workDir;
        System.IO.Directory.CreateDirectory(workDir);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return;
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                // A cancelled start also stops the command, with everything it started.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                throw;
            }
            _log.AddRaw(log, L.F("home.log.command_exited", command, process.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.AddRaw(log, L.F("home.log.command_failed", command, e.Message));
        }
    }

    private LaunchRequest? _lastRequest;
    private string[] _failureOutput = [];

    private void ShowFailure(GameRun run, LaunchRequest request, Exception e)
    {
        var info = LaunchFailure.Classify(e, _service.Settings.DownloadSource);
        _failureOutput = [.. run.Log?.All.Select(l => $"[{l.Timestamp:HH:mm:ss}] [{l.Level}] {l.Message}") ?? [], "", e.ToString()];
        var log = run.Log;
        Failure = new LaunchFailureCard(info, ErrorText.Describe(e), request, log);
    }

    /// <summary>Self-check only: shows the failure card for an exception without launching.</summary>
    internal void ShowFailureForSelfTest(Exception e, string instance = "self-check")
    {
        var info = LaunchFailure.Classify(e, _service.Settings.DownloadSource);
        _failureOutput = ["self-check", e.ToString()];
        Failure = new LaunchFailureCard(info, ErrorText.Describe(e), new LaunchRequest(instance), null);
    }

    /// <summary>启动失败卡上的修复按钮。</summary>
    [RelayCommand]
    private async Task FixAsync(LaunchFix fix)
    {
        if (Failure is not { } card) return;
        switch (fix)
        {
            case LaunchFix.Relogin:
                NavigateToAccounts?.Invoke();
                break;
            case LaunchFix.Retry:
                Failure = null;
                await RunAsync(card.Request, allowSecond: false);
                break;
            case LaunchFix.RetryWithMirror:
                Failure = null;
                _service.SetDownloadSource("mirror");
                await RunAsync(card.Request with { ExpectedContext = null });
                break;
            case LaunchFix.DownloadJava when card.Info.JavaMajor is { } major:
                Failure = null;
                var task = _service.Tasks.Start("java", L.F("tools.java.downloading", major), async (ctx, ct) =>
                {
                    await new Amber.Core.Java.JavaRuntimeInstaller(_service.Downloader, _service.Directory.Root).InstallAsync(null, major,
                        new Progress<Amber.Core.Net.DownloadProgress>(p => ctx.Report((double)p.CompletedFiles / Math.Max(1, p.TotalFiles),
                            L.F("tools.java.progress", major, p.CompletedFiles, p.TotalFiles))), ct);
                    await _service.Java.ScanAsync(true);
                    return Amber.Core.Activity.TaskOutcome.Success();
                }, $"java:{major}");
                while (task.IsRunning) await Task.Delay(200);
                if (task.State == Amber.Core.Activity.TaskState.Succeeded) await RunAsync(card.Request);
                else SetStatus(StatusMessage.Error("home.status.launch_failed", task.Message ?? ""));
                break;
            case LaunchFix.ViewLog:
                _log.Show(card.Log);
                _log.OnlyProblems = true;
                NavigateToLog?.Invoke();
                break;
        }
    }

    [RelayCommand]
    private void DismissFailure() => Failure = null;

    /// <summary>导出启动失败的错误报告（这一局的日志和异常）。</summary>
    public void ExportFailureReport(string zipPath) =>
        SaveFile(zipPath, () => CrashAnalyzer.ExportBundle(zipPath, [], _failureOutput), "home.status.report_exported");

    /// <summary>导出崩溃卡对应的那一局：无痕过滤已在保存快照时完成，多开和后续启动不会串日志。</summary>
    public void ExportCrashBundle(string zipPath)
    {
        // 先写内存中的输出，再附上崩溃当时的磁盘快照；导出时绝不重新打开原日志路径。
        SaveFile(zipPath, () => CrashAnalyzer.ExportBundle(zipPath, [], _lastRunOutput, _lastRunAttachments.Select(a => (a.Name, a.ExportText))),
            "home.status.report_exported");
    }

    [RelayCommand]
    private void OpenLatestCrashFile()
    {
        if (_lastRunFiles.FirstOrDefault() is { } file && ShellOpen.TryOpen(file, createFolder: false) is { } e)
            SetStatus(StatusMessage.Failure("common.open_failed", e, file, ErrorText.Of(e)));
    }
}

/// <summary>启动失败卡：原因一句话和按顺序排好的修复按钮。</summary>
public sealed class LaunchFailureCard(LaunchFailure info, string reason, LaunchRequest request, RunLog? log)
{
    public LaunchFailure Info { get; } = info;
    public string Reason { get; } = reason;
    public LaunchRequest Request { get; } = request;
    public RunLog? Log { get; } = log;

    public string Title => Info.Kind switch
    {
        LaunchFailureKind.Login => L.T("home.failure.login"),
        LaunchFailureKind.Java => L.F("home.failure.java", Info.JavaMajor ?? 0),
        LaunchFailureKind.Network => L.T("home.failure.network"),
        _ => L.T("home.failure.other"),
    };

    /// <summary>卡上的按钮（导出错误报告要选保存位置，由页面处理，不在这里）。</summary>
    public IReadOnlyList<LaunchFixButton> Buttons => Info.Fixes.Where(f => f != LaunchFix.ExportReport)
        .Select((f, i) => new LaunchFixButton(f, i == 0)).ToList();

    public bool CanExport => Info.Fixes.Contains(LaunchFix.ExportReport);
}

public sealed record LaunchFixButton(LaunchFix Fix, bool IsPrimary)
{
    public string Label => Fix switch
    {
        LaunchFix.Relogin => L.T("home.failure.fix.relogin"),
        LaunchFix.DownloadJava => L.T("home.failure.fix.java"),
        LaunchFix.RetryWithMirror => L.T("home.failure.fix.mirror"),
        LaunchFix.Retry => L.T("home.failure.fix.retry"),
        _ => L.T("home.failure.fix.log"),
    };
}
