using Amber.Core.Navigation;
using Amber.Core.Themes;
using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.App.Services;
using Amber.Core.Launch;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel()
    {
        // 换语言时让这个页面的所有绑定重新取值（标题、下拉选项这些在 C# 里生成的文字跟着换）。
        Amber.Core.Localization.L.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnLanguageChanged();
            OnPropertyChanged((string?)null);
        });
    }

    /// <summary>换语言后（界面线程上）：把存成文字的内容（汇总、提示）按新语言重新算一遍。</summary>
    protected virtual void OnLanguageChanged() { }

    public abstract string Title { get; }

    /// <summary>导航图标的路径数据（24×24）。</summary>
    public abstract string Icon { get; }

    private Geometry? _iconGeometry;
    public Geometry IconGeometry => _iconGeometry ??= Geometry.Parse(Icon);

    private bool _activated;
    private int _badge;
    private bool _isCurrent;

    /// <summary>导航栏上的未读数，0 不显示。</summary>
    public int Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value)) OnPropertyChanged(nameof(HasBadge));
        }
    }

    public bool HasBadge => Badge > 0;

    /// <summary>是不是当前显示的页面。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (!SetProperty(ref _isCurrent, value)) return;
            if (value)
            {
                Badge = 0;
                OnBecameCurrent();
            }
            else OnLeftCurrent();
        }
    }

    /// <summary>切到这一页时调用（每次）。</summary>
    protected virtual void OnBecameCurrent() { }

    /// <summary>从这一页切走时调用（每次）。</summary>
    protected virtual void OnLeftCurrent() { }

    /// <summary>页面第一次显示时调用一次。启动时不联网、不扫描，都放在这里。</summary>
    public void Activate()
    {
        if (_activated) return;
        _activated = true;
        OnFirstActivated();
    }

    protected virtual void OnFirstActivated() { }

    private StatusMessage? _statusInfo;

    /// <summary>
    /// 页面底部状态条显示的消息（null 时显示 DefaultStatus）。用 SetStatus 设：带翻译键和种类（成功/警告/错误），换语言时跟着换。
    /// </summary>
    public StatusMessage? StatusInfo => _statusInfo ?? DefaultStatus;

    /// <summary>
    /// 状态文字（StatusInfo 按当前语言格式化的结果）。直接赋字符串是旧写法：当作普通信息显示，不跟着换语言。
    /// </summary>
    public string Status
    {
        get => StatusInfo?.Text ?? "";
        set => SetStatus(string.IsNullOrEmpty(value) ? null : StatusMessage.Plain(value));
    }

    /// <summary>没有消息时显示的内容（比如首页的“准备就绪”）。</summary>
    protected virtual StatusMessage? DefaultStatus => null;

    public void SetStatus(StatusMessage? message)
    {
        _statusInfo = message;
        OnPropertyChanged(nameof(StatusInfo));
        OnPropertyChanged(nameof(Status));
        OnStatusChanged();
    }

    protected virtual void OnStatusChanged() { }
}

/// <summary>开发用：截图模式下让页面走一遍真实流程。</summary>
public interface ISnapshotDemo
{
    Task RunSnapshotDemoAsync();
}

public static class Ui
{
    /// <summary>等已排队的进度回调跑完，这样最后写的状态文字不会被迟到的进度盖掉。</summary>
    public static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}

public sealed partial class MainViewModel : ObservableObject
{
    public ObservableCollection<PageViewModel> Pages { get; }

    /// <summary>联机页（熔断倒计时盖在整个窗口上，窗口直接绑它）。</summary>
    public MultiplayerViewModel Multiplayer { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    public string VersionText => L.F("main.version", UpdateChecker.CurrentVersion);

    /// <summary>外壳：路由、历史栈、侧栏入口、子页（官方设计 §11.5）。</summary>
    public ShellViewModel Shell { get; }

    /// <summary>活动中心、请求条、正在玩胶囊（§6.22）。</summary>
    public ActivityViewModel Activity { get; }

    partial void OnCurrentPageChanged(PageViewModel oldValue, PageViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsCurrent = false;
        newValue.IsCurrent = true;
        newValue.Activate();
        Shell?.OnPageChanged(newValue);
    }

    private readonly LauncherService _main;

    /// <summary>The Core services behind this window (self-check).</summary>
    internal LauncherService Service => _main;

    /// <summary>
    /// Things that would be cut off if Jay closed now (§5.9 close guard): remote control, a running server,
    /// a room with players, running tasks, a game still preparing. Empty means close right away.
    /// </summary>
    public IReadOnlyList<string> CloseBlockers()
    {
        var items = new List<string>();
        if (Multiplayer.RemoteSessionCount > 0) items.Add(L.F("close.item.remote", Multiplayer.RemoteSessionCount));
        if (HostServerViewModel.ServerRunning) items.Add(L.T("close.item.server"));
        var others = Multiplayer.Peers.Count(p => p.Peer.Ip != Amber.Core.Multiplayer.EasyTierArgs.HostIp);
        if (Multiplayer.IsHosting && others > 0) items.Add(L.F("close.item.room_host", others));
        else if (Multiplayer.IsJoined) items.Add(L.T("close.item.room_join"));
        var tasks = _main.Tasks.Active.Count;
        if (tasks > 0) items.Add(L.F("close.item.tasks", tasks));
        if (Pages.OfType<HomeViewModel>().FirstOrDefault() is { IsPreparing: true }) items.Add(L.T("close.item.preparing"));
        return items;
    }

    /// <summary>Cancels running tasks before a confirmed close (servers and remote control are stopped by the exit path).</summary>
    public void EndAllForExit()
    {
        _main.Tasks.CancelAll();
        Pages.OfType<HomeViewModel>().FirstOrDefault()?.CancelPreparingForExit();
    }

    public MainViewModel(LauncherService service)
    {
        _main = service;
        L.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(VersionText)));
        var log = new LogViewModel();
        var home = new HomeViewModel(service, log);
        var instance = new InstanceViewModel(service) { NavigateHome = null };
        instance.AttachHome(home);
        var content = new ContentViewModel(service);
        var multiplayer = new MultiplayerViewModel(service);
        Multiplayer = multiplayer;
        var download = new DownloadViewModel(service);
        Pages =
        [
            home,
            new AccountsViewModel(service),
            instance,
            download,
            content,
            multiplayer,
            new ToolsViewModel(service),
            new SettingsViewModel(service),
            log,
        ];
        var accounts = (AccountsViewModel)Pages[1];
        Shell = new ShellViewModel(this, service);
        var shell = Shell;
        home.NavigateToAccounts = () => shell.Navigate("accounts");
        home.NavigateToNewInstance = () => shell.Navigate(LegacyRoutes.NewInstance);
        home.NavigateToLog = () => shell.Navigate(LegacyRoutes.Logs);
        home.NavigateHere = () => shell.Navigate("library");
        home.Onboarding.MicrosoftLogin = () =>
        {
            accounts.LoginTab = AccountsViewModel.MicrosoftTab;
            shell.Navigate("accounts");
        };
        home.Onboarding.ChooseVersion = () => shell.Navigate(LegacyRoutes.NewInstance);
        home.Onboarding.ImportPack = async path =>
        {
            shell.Navigate("discover");
            await content.ImportLocalPackAsync(path);
        };
        instance.NavigateHome = () => shell.Navigate("library");
        instance.NavigateToNewInstance = () => shell.Navigate(LegacyRoutes.NewInstance);
        content.NavigateToSettings = () => shell.Navigate("settings");
        instance.NavigateToContent = (id, type) =>
        {
            var back = shell.Router.Current;
            shell.Navigate("discover");
            content.BrowseFor(id, type, back: () => shell.Router.Navigate(back));
        };
        multiplayer.NavigateHome = () => shell.Navigate("library");
        // 开始游戏不换页（§2.5 第 5 条）：进度在顶栏胶囊和启动页。
        download.LaunchInstance = id =>
        {
            service.SetCurrentInstance(id);
            service.RequestLaunch(new LaunchRequest(id));
        };
        download.CloseAfterStart = () => shell.Navigate("library");
        content.LaunchInstance = id => download.LaunchInstance?.Invoke(id);
        download.BrowseModpacks = () =>
        {
            shell.Navigate("discover");
            content.BrowseModpacks();
        };
        // The import runs on the Discover page: it shows the files that need a manual download.
        download.ImportPackFile = async (path, play) =>
        {
            shell.Navigate("discover");
            if (await content.ImportLocalPackAsync(path) is { } id && play) download.LaunchInstance?.Invoke(id);
        };
        Activity = new ActivityViewModel(service) { OpenRun = _ => shell.Navigate("library") };
        home.SessionStarted += Activity.Track;
        // 游戏库的“上次玩”和“共玩”：只记本机的运行记录。
        home.SessionStarted += session =>
        {
            service.RecordPlayed(session.InstanceId, session.Started);
            var counted = false;
            session.Changed += s =>
            {
                if (counted || s.IsActive || s.Ended is not { } ended || s.ProcessStarted is null) return;
                counted = true;
                service.AddPlayTime(s.InstanceId, s.PlayTime(ended));
            };
        };
        var library = new LibraryViewModel(service, home, Activity) { Navigate = r => shell.Navigate(r) };
        Pages.Add(library);
        // 多开确认走请求条；启动页的卡片也还在，哪边处理都会让另一边一起消失。
        const string MultiKey = "multi-launch";
        home.MultiLaunchRequested += running => service.Requests.Post(new Amber.Core.Activity.RequestSpec("multi",
            L.F("home.multi.warning", running), null,
            [new("start", L.T("home.multi.launch_anyway")), new("cancel", L.T("common.cancel"), IsDefault: true)], DedupeKey: MultiKey));
        service.Requests.Resolved += r =>
        {
            if (r.Spec.Kind != "multi" || r.ResolvedBy == Amber.Core.Activity.RequestSource.Program) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (r.Choice == "start") home.LaunchAnywayCommand.Execute(null);
                else home.DismissPendingLaunchCommand.Execute(null);
            });
        };
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.PendingLaunch) && home.PendingLaunch is null) service.Requests.Withdraw(MultiKey);
        };
        download.OpenInstance = id =>
        {
            service.SetCurrentInstance(id);
            shell.Router.Navigate(Route.Of(Section.Library, id));
        };
        download.BrowseModsFor = id =>
        {
            shell.Navigate("discover");
            content.BrowseModsFor(id);
        };
        instance.NavigateToDatapacks = (id, world) =>
        {
            var back = shell.Router.Current;
            shell.Navigate("discover");
            content.BrowseDatapacksFor(id, world, back: () => shell.Router.Navigate(back));
        };
        foreach (var page in Pages) Shell.Watch(page);
        CurrentPage = library;
        CurrentPage.Activate();
    }
}

public sealed record JavaOption(string Label, string? Path);
public sealed record ThemePackOption(string Id, string Label, bool IsDefault)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly LauncherService _service;

    public override string Title => L.T("instance.settings.title");
    public override string Icon => "M4 6h8v2H4zM16 6h4v2h-4zM12 4h4v6h-4zM4 16h4v2H4zM12 16h8v2h-8zM8 14h4v6H8z";

    /// <summary>下拉框的选项列表本身不变，换语言时只换文字（不然选中的序号会被清成 -1、保存时丢设置）。</summary>
    public Amber.App.Localization.OptionItem[] SourceOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.source.auto", "settings.source.official", "settings.source.mirror");
    internal static readonly string[] SourceKeys = ["auto", "official", "mirror"];

    public Amber.App.Localization.OptionItem[] AttestationOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.security.attest.off", "settings.security.attest.on", "settings.security.attest.required");
    internal static readonly string[] AttestationKeys = ["off", "on", "required"];

    public ObservableCollection<JavaOption> JavaOptions { get; } = [];

    [ObservableProperty] public partial string GameDirectory { get; set; }
    [ObservableProperty] public partial int SourceIndex { get; set; }
    [ObservableProperty] public partial string Concurrency { get; set; }
    [ObservableProperty] public partial JavaOption? SelectedJava { get; set; }
    [ObservableProperty] public partial bool AutoJavaDownload { get; set; }
    [ObservableProperty] public partial bool AllowNetworkThemes { get; set; }
    [ObservableProperty] public partial bool NoTrace { get; set; }
    [ObservableProperty] public partial int AttestationIndex { get; set; }
    [ObservableProperty] public partial bool SecurityBreaker { get; set; }
    [ObservableProperty] public partial string BreakerRestartDelay { get; set; } = "";
    [ObservableProperty] public partial bool BreakerSkipDelay { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBreakerDelayError))] public partial string? BreakerDelayError { get; set; }
    public bool HasBreakerDelayError => BreakerDelayError is not null;
    [ObservableProperty] public partial bool ExperimentalBake { get; set; }
    [ObservableProperty] public partial string BakeLoaderPath { get; set; }
    [ObservableProperty] public partial bool IsolateVersions { get; set; }
    [ObservableProperty] public partial string WindowWidth { get; set; }
    [ObservableProperty] public partial string WindowHeight { get; set; }
    [ObservableProperty] public partial bool Fullscreen { get; set; }
    [ObservableProperty] public partial string JvmArgs { get; set; }
    [ObservableProperty] public partial string GameArgs { get; set; }
    [ObservableProperty] public partial string PreLaunch { get; set; }
    [ObservableProperty] public partial string PostExit { get; set; }
    [ObservableProperty] public partial string CurseForgeKey { get; set; }
    [ObservableProperty] public partial string CurseForgeRoot { get; set; }
    [ObservableProperty] public partial string UpdateFeed { get; set; }

    public string SettingsPath => AppSettings.FilePath;
    public string SettingsPathLabel => L.F("settings.file_path", SettingsPath);

    public SettingsViewModel(LauncherService service)
    {
        _service = service;
        _loading = true;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            var automatic = JavaOptions.FirstOrDefault(o => o.Path is null);
            if (automatic is null) return;
            var selected = SelectedJava == automatic;
            var replacement = new JavaOption(L.T("settings.java.automatic"), null);
            JavaOptions[JavaOptions.IndexOf(automatic)] = replacement;
            if (selected) SelectedJava = replacement;
        });
        var s = service.Settings;
        GameDirectory = s.GameDirectory;
        SourceIndex = Math.Max(0, Array.IndexOf(SourceKeys, s.DownloadSource));
        service.SettingsChangedElsewhere += () => Dispatcher.UIThread.Post(() =>
        {
            _loading = true;
            GameDirectory = service.Settings.GameDirectory;
            SourceIndex = Math.Max(0, Array.IndexOf(SourceKeys, service.Settings.DownloadSource));
            _loading = false;
        });
        Concurrency = s.DownloadConcurrency.ToString();
        AutoJavaDownload = s.AutoJavaDownload;
        AllowNetworkThemes = s.AllowNetworkThemes;
        NoTrace = s.NoTrace;
        AttestationIndex = Math.Max(0, Array.IndexOf(AttestationKeys, s.SecurityAttestation));
        SecurityBreaker = s.SecurityBreaker;
        BreakerRestartDelay = s.BreakerRestartDelay.ToString();
        BreakerSkipDelay = s.BreakerSkipDelay;
        ExperimentalBake = s.ExperimentalBake;
        BakeLoaderPath = s.BakeLoaderPath ?? "";
        IsolateVersions = s.IsolateVersions;
        WindowWidth = s.WindowWidth?.ToString() ?? "";
        WindowHeight = s.WindowHeight?.ToString() ?? "";
        Fullscreen = s.Fullscreen;
        JvmArgs = s.JvmArgs ?? "";
        GameArgs = s.GameArgs ?? "";
        PreLaunch = s.PreLaunchCommand ?? "";
        PostExit = s.PostExitCommand ?? "";
        CurseForgeKey = s.CurseForgeApiKey ?? "";
        CurseForgeRoot = s.CurseForgeApiRoot ?? "";
        UpdateFeed = s.UpdateFeedUrl ?? "";
        SelectedThemeIndex = Math.Max(0, Array.IndexOf(ThemeKeys, s.Theme));
        SelectedMotionIndex = Math.Max(0, Array.IndexOf(Amber.Core.Layout.MotionPreference.Modes, Amber.Core.Layout.MotionPreference.Normalize(s.ReduceMotionMode)));
        SelectedSidebarIndex = (int)Amber.Core.Layout.ShellRules.ParseMode(s.SidebarMode ?? (s.SidebarCollapsed ? "collapsed" : null));
        ReloadThemePacks();
        // 开发模式下主题文件改了会重新套用，提示跟着更新。
        ThemeService.Changed += UpdateThemePackStatus;
        // 启动时只把选单对上已经生效的语言，不再重新应用、不写设置文件。
        _initializing = true;
        SelectedLanguageIndex = Math.Max(0, Array.IndexOf(LanguageKeys, Amber.Core.Localization.L.Resolve(s.Language) ?? "system"));
        _initializing = false;
        _loading = false;
    }

    private bool _initializing;

    /// <summary>正在把设置文件里的值填进界面：这期间的改动不是用户改的，不保存。</summary>
    private bool _loading;

    /// <summary>界面语言：第一项跟随系统，其余每种语言用自己的文字写名字（不管现在是什么语言都认得出来）。</summary>
    public Amber.App.Localization.OptionItem[] LanguageOptions { get; } =
        [Amber.App.Localization.OptionItem.Key("settings.language.system"), .. Amber.Core.Localization.L.Languages.Select(l => Amber.App.Localization.OptionItem.Fixed(l.Name))];
    private static readonly string[] LanguageKeys = ["system", .. Amber.Core.Localization.L.Languages.Select(l => l.Code)];

    [ObservableProperty] public partial int SelectedLanguageIndex { get; set; }

    /// <summary>选了就立刻换（不用点保存），同时记进设置。</summary>
    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_initializing || value < 0 || value >= LanguageKeys.Length) return;
        var code = LanguageKeys[Math.Clamp(value, 0, LanguageKeys.Length - 1)];
        _service.Settings.Language = code == "system" ? null : code;
        Amber.Core.Localization.L.SetLanguage(_service.Settings.Language);
        try { _service.Settings.Save(); } catch (IOException) { }
    }

    protected override void OnFirstActivated() => _ = LoadJavaAsync(false);

    public Amber.App.Localization.OptionItem[] ThemeOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.theme.system", "settings.theme.light", "settings.theme.dark");
    private static readonly string[] ThemeKeys = ["system", "light", "dark"];

    [ObservableProperty] public partial int SelectedThemeIndex { get; set; }

    /// <summary>减少动画：跟随系统 / 开 / 关（顺序和 MotionPreference.Modes 一致）。</summary>
    public Amber.App.Localization.OptionItem[] MotionOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.motion.system", "settings.motion.on", "settings.motion.off");

    [ObservableProperty] public partial int SelectedMotionIndex { get; set; }

    partial void OnSelectedMotionIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        var modes = Amber.Core.Layout.MotionPreference.Modes;
        _service.Settings.ReduceMotionMode = modes[Math.Min(value, modes.Length - 1)];
        _service.Settings.ReduceMotion = _service.Settings.ReduceMotionMode == Amber.Core.Layout.MotionPreference.On;
        SystemMotion.Apply(_service.Settings.ReduceMotionMode);
        Persist();
    }

    /// <summary>侧栏：自动 / 总是展开 / 总是收起（顺序和 SidebarMode 一致）。</summary>
    public Amber.App.Localization.OptionItem[] SidebarOptions { get; } = Amber.App.Localization.OptionItem.Keys("settings.sidebar.auto", "settings.sidebar.expanded", "settings.sidebar.collapsed");

    [ObservableProperty] public partial int SelectedSidebarIndex { get; set; }

    /// <summary>侧栏模式改了（窗口重新排外壳）。</summary>
    public static event Action? SidebarModeChanged;

    partial void OnSelectedSidebarIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _service.Settings.SidebarMode = Amber.Core.Layout.ShellRules.ModeName((Amber.Core.Layout.SidebarMode)Math.Min(value, 2));
        _service.Settings.SidebarCollapsed = false;
        Persist();
        SidebarModeChanged?.Invoke();
    }

    // ---- 界面主题包 ----

    /// <summary>第一项永远是 Jay 默认主题，后面是导入的主题包。</summary>
    public ObservableCollection<ThemePackOption> ThemePacks { get; } = [];

    [ObservableProperty] public partial ThemePackOption? SelectedThemePack { get; set; }

    /// <summary>主题包的说明：作者、读取时发现的问题、安全模式提示。</summary>
    [ObservableProperty] public partial string ThemePackStatus { get; set; } = "";

    public bool CanRemoveThemePack => SelectedThemePack is { IsDefault: false };

    private bool _themeLoading;

    internal void ReloadThemePacks(string? select = null)
    {
        _themeLoading = true;
        ThemePacks.Clear();
        ThemePacks.Add(new ThemePackOption(ThemeService.DefaultId, L.T("settings.themepack.default"), true));
        foreach (var pack in ThemeService.Store.List())
            ThemePacks.Add(new ThemePackOption(pack.Id, pack.Author is { Length: > 0 } a ? $"{pack.Name} · {a}" : pack.Name, false));
        var id = select ?? _service.Settings.ThemePack ?? ThemeService.DefaultId;
        SelectedThemePack = ThemePacks.FirstOrDefault(o => o.Id == id) ?? ThemePacks[0];
        _themeLoading = false;
        UpdateThemePackStatus();
    }

    partial void OnSelectedThemePackChanged(ThemePackOption? value)
    {
        OnPropertyChanged(nameof(CanRemoveThemePack));
        if (_themeLoading || value is null) return;
        ThemeService.Select(_service.Settings, value.Id);
        Persist();
        UpdateThemePackStatus();
    }

    private void UpdateThemePackStatus()
    {
        var lines = new List<string>();
        switch (ThemeService.SafeModeReason)
        {
            case "crashed": lines.Add(L.T("settings.themepack.safe.crashed")); break;
            case "requested": lines.Add(L.T("settings.themepack.safe.requested")); break;
            case "missing": lines.Add(L.T("settings.themepack.safe.missing")); break;
        }
        if (ThemeService.DevDirectory is { } devDir) lines.Add(L.F("settings.themepack.dev", devDir));
        if (ThemeService.DevError is { } devError) lines.Add(L.F("settings.themepack.dev_error", devError));
        if (ThemeService.Current is { } pack && pack.Problems.Count > 0)
            lines.Add(L.F("settings.themepack.problems", pack.Problems.Count, string.Join("; ", pack.Problems.Take(4))));
        ThemePackStatus = string.Join("\n", lines);
    }

    /// <summary>导入 .jaytheme：读取、校验、安装，然后直接切换过去。</summary>
    public void ImportThemePack(string path)
    {
        try
        {
            var pack = ThemeService.Store.Install(path);
            ReloadThemePacks(pack.Id);
            ThemeService.Select(_service.Settings, pack.Id);
            Persist();
            UpdateThemePackStatus();
            Toasts.Show(L.F("settings.themepack.imported", pack.Name));
        }
        catch (Amber.Core.Themes.ThemePackException e)
        {
            Toasts.Show(L.F("settings.themepack.import_failed", e.Code, e.Detail), Avalonia.Controls.Notifications.NotificationType.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toasts.Show(L.F("settings.themepack.import_failed", "io", e.Message), Avalonia.Controls.Notifications.NotificationType.Error);
        }
    }

    public void ExportThemePack(string path)
    {
        try
        {
            ThemeService.ExportCurrent(path);
            Toasts.Show(L.F("settings.themepack.exported", Path.GetFileName(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Amber.Core.Themes.ThemePackException)
        {
            Toasts.Show(e.Message, Avalonia.Controls.Notifications.NotificationType.Error);
        }
    }

    [RelayCommand]
    private void RemoveThemePack()
    {
        if (SelectedThemePack is not { IsDefault: false } option) return;
        var wasCurrent = (_service.Settings.ThemePack ?? ThemeService.DefaultId) == option.Id;
        if (wasCurrent) ThemeService.Select(_service.Settings, ThemeService.DefaultId);
        try { ThemeService.Store.Remove(option.Id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toasts.Show(e.Message, Avalonia.Controls.Notifications.NotificationType.Error); }
        Persist();
        ReloadThemePacks();
    }

    /// <summary>恢复默认主题：设置页固定位置的按钮，主题改不了它。</summary>
    [RelayCommand]
    private void ResetThemePack() => SelectedThemePack = ThemePacks.FirstOrDefault();

    [RelayCommand]
    private Task RefreshJavaAsync() => LoadJavaAsync(true);

    private async Task LoadJavaAsync(bool refresh)
    {
        var current = _service.Settings.JavaPath;
        var found = await _service.Java.ScanAsync(refresh);
        _loading = true;
        JavaOptions.Clear();
        JavaOptions.Add(new JavaOption(L.T("settings.java.automatic"), null));
        foreach (var j in found) JavaOptions.Add(new JavaOption(j.ToString(), j.JavaPath));
        SelectedJava = JavaOptions.FirstOrDefault(o => o.Path == current) ?? JavaOptions[0];
        _loading = false;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---- 即改即存（改版规格 3.10）----
    // 下拉框、复选框改了立即存；文本框绑定在失焦时才写回，写回时校验，通过才存，不通过就在字段下面提示、保留输入。

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasConcurrencyError))] public partial string? ConcurrencyError { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWindowError))] public partial string? WindowError { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasJvmArgsError))] public partial string? JvmArgsError { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGameArgsError))] public partial string? GameArgsError { get; set; }
    public bool HasConcurrencyError => ConcurrencyError is not null;
    public bool HasWindowError => WindowError is not null;
    public bool HasJvmArgsError => JvmArgsError is not null;
    public bool HasGameArgsError => GameArgsError is not null;

    /// <summary>改游戏目录前确认（会重新扫描实例）；界面接上对话框，返回 false 就不换。</summary>
    public Func<string, Task<bool>>? ConfirmDirectoryChange { get; set; }

    partial void OnSourceIndexChanged(int value) => Persist();
    partial void OnSelectedJavaChanged(JavaOption? value) => Persist();
    partial void OnAutoJavaDownloadChanged(bool value) => Persist();
    partial void OnAllowNetworkThemesChanged(bool value)
    {
        ThemeService.AllowNetworkThemes = value;
        ThemeService.Refresh();
        Persist();
    }
    partial void OnNoTraceChanged(bool value) => Persist();
    partial void OnAttestationIndexChanged(int value) => Persist();
    partial void OnSecurityBreakerChanged(bool value) => Persist();
    partial void OnBreakerRestartDelayChanged(string value) => Persist();
    partial void OnBreakerSkipDelayChanged(bool value) => Persist();
    partial void OnExperimentalBakeChanged(bool value) => Persist();
    partial void OnBakeLoaderPathChanged(string value) => Persist();
    partial void OnIsolateVersionsChanged(bool value) => Persist();
    partial void OnConcurrencyChanged(string value) => Persist();
    partial void OnWindowWidthChanged(string value) => Persist();
    partial void OnWindowHeightChanged(string value) => Persist();
    partial void OnFullscreenChanged(bool value) => Persist();
    partial void OnJvmArgsChanged(string value) => Persist();
    partial void OnGameArgsChanged(string value) => Persist();
    partial void OnPreLaunchChanged(string value) => Persist();
    partial void OnPostExitChanged(string value) => Persist();
    partial void OnCurseForgeKeyChanged(string value) => Persist();
    partial void OnCurseForgeRootChanged(string value) => Persist();
    partial void OnUpdateFeedChanged(string value) => Persist();

    /// <summary>主题选了立即生效。</summary>
    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _service.Settings.Theme = ThemeKeys[Math.Min(value, ThemeKeys.Length - 1)];
        ThemeService.SetVariant(_service.Settings.Theme);
        Persist();
    }

    partial void OnGameDirectoryChanged(string value)
    {
        if (_loading) return;
        _ = ChangeDirectoryAsync(value);
    }

    /// <summary>换游戏目录：先确认，取消就把输入框改回原来的目录。</summary>
    private async Task ChangeDirectoryAsync(string value)
    {
        var target = value.Trim();
        var s = _service.Settings;
        if (target.Length == 0 || string.Equals(s.GameDirectory, target, StringComparison.OrdinalIgnoreCase)) return;
        if (ConfirmDirectoryChange is { } confirm && !await confirm(target))
        {
            _loading = true;
            GameDirectory = s.GameDirectory;
            _loading = false;
            return;
        }
        s.GameDirectory = target;
        Persist();
        SetStatus(StatusMessage.Success("settings.saved_directory_changed"));
        _ = LoadJavaAsync(true);
    }

    /// <summary>把通过校验的字段写进设置并保存；没通过的字段留在设置里的旧值，提示写在字段下面。</summary>
    private void Persist()
    {
        if (_loading) return;
        var s = _service.Settings;
        if (SourceIndex >= 0) s.DownloadSource = SourceKeys[Math.Min(SourceIndex, SourceKeys.Length - 1)];
        if (SettingInput.TryParsePositiveInt(Concurrency, out var c, 128) && c is not null)
        {
            s.DownloadConcurrency = c.Value;
            ConcurrencyError = null;
            Normalize(() => Concurrency = c.Value.ToString());
        }
        else ConcurrencyError = L.T("settings.error.concurrency");
        if (SelectedJava is not null) s.JavaPath = SelectedJava.Path;
        s.AutoJavaDownload = AutoJavaDownload;
        s.AllowNetworkThemes = AllowNetworkThemes;
        s.NoTrace = NoTrace;
        s.SecurityAttestation = AttestationKeys[Math.Clamp(AttestationIndex, 0, AttestationKeys.Length - 1)];
        s.SecurityBreaker = SecurityBreaker;
        s.BreakerSkipDelay = BreakerSkipDelay;
        if (int.TryParse(BreakerRestartDelay.Trim(), out var delay) && delay is >= 1 and <= Amber.Core.Multiplayer.Transport.SecurityPolicy.MaxRestartDelay)
        {
            s.BreakerRestartDelay = delay;
            BreakerDelayError = null;
        }
        else BreakerDelayError = L.F("settings.security.delay_error", Amber.Core.Multiplayer.Transport.SecurityPolicy.MaxRestartDelay);
        s.ExperimentalBake = ExperimentalBake;
        s.BakeLoaderPath = Blank(BakeLoaderPath);
        s.IsolateVersions = IsolateVersions;
        if (SettingInput.TryParsePositiveInt(WindowWidth, out var w, 16384) && SettingInput.TryParsePositiveInt(WindowHeight, out var h, 16384))
        {
            s.WindowWidth = w;
            s.WindowHeight = h;
            WindowError = null;
            // 全角数字等认出来以后，输入框里也换成规范写法（不再触发保存）。
            // 绑定正在把输入框的值写回来，这时改属性输入框不会刷新，所以排到下一轮再改。
            Normalize(() => { WindowWidth = w?.ToString() ?? ""; WindowHeight = h?.ToString() ?? ""; });
        }
        else WindowError = L.T("settings.error.window");
        s.Fullscreen = Fullscreen;
        if (SettingInput.QuotesBalanced(JvmArgs))
        {
            s.JvmArgs = Blank(JvmArgs);
            JvmArgsError = null;
        }
        else JvmArgsError = L.T("settings.error.quotes");
        if (SettingInput.QuotesBalanced(GameArgs))
        {
            s.GameArgs = Blank(GameArgs);
            GameArgsError = null;
        }
        else GameArgsError = L.T("settings.error.quotes");
        s.PreLaunchCommand = Blank(PreLaunch);
        s.PostExitCommand = Blank(PostExit);
        s.CurseForgeApiKey = Blank(CurseForgeKey);
        s.CurseForgeApiRoot = Blank(CurseForgeRoot);
        s.UpdateFeedUrl = Blank(UpdateFeed);
        try
        {
            s.Save();
        }
        catch (IOException e)
        {
            SetStatus(StatusMessage.Failure("settings.error.save_failed", e, ErrorText.Of(e)));
            return;
        }
        _service.Reload();
        if (HasConcurrencyError || HasWindowError || HasJvmArgsError || HasGameArgsError)
            SetStatus(StatusMessage.Warning("settings.saved_with_errors"));
        else
            SetStatus(StatusMessage.Success(_service.AnyRunning ? "settings.saved_next_launch" : "settings.saved_tick"));
    }

    private void Normalize(Action apply) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _loading = true;
        try { apply(); }
        finally { _loading = false; }
    });

    /// <summary>↺ 恢复默认：把这一项改回新装时的值（随即保存）。</summary>
    [RelayCommand]
    private void ResetField(string? field)
    {
        var d = new AppSettings();
        switch (field)
        {
            case "source": SourceIndex = Math.Max(0, Array.IndexOf(SourceKeys, d.DownloadSource)); break;
            case "concurrency": Concurrency = d.DownloadConcurrency.ToString(); break;
            case "window":
                _loading = true;
                WindowWidth = d.WindowWidth?.ToString() ?? "";
                _loading = false;
                WindowHeight = d.WindowHeight?.ToString() ?? "";
                Fullscreen = d.Fullscreen;
                break;
            case "jvm": JvmArgs = d.JvmArgs ?? ""; break;
            case "game_args": GameArgs = d.GameArgs ?? ""; break;
            case "pre": PreLaunch = d.PreLaunchCommand ?? ""; break;
            case "post": PostExit = d.PostExitCommand ?? ""; break;
            case "java": SelectedJava = JavaOptions.FirstOrDefault(o => o.Path is null); break;
            case "theme": SelectedThemeIndex = Math.Max(0, Array.IndexOf(ThemeKeys, d.Theme)); break;
            case "motion": SelectedMotionIndex = 0; break;
            case "sidebar": SelectedSidebarIndex = 0; break;
            case "theme_pack": SelectedThemePack = ThemePacks.FirstOrDefault(); AllowNetworkThemes = true; break;
        }
    }
}

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message, GameLogLevel Severity)
{
    public string Time => Fmt.Time(Timestamp);
    public bool IsError => Severity >= GameLogLevel.Error;
    public bool IsWarning => Severity == GameLogLevel.Warn;
    public bool IsMuted => Severity <= GameLogLevel.Debug;
}

/// <summary>一次启动的日志。多开时各存各的，日志页选看哪一个。</summary>
public sealed class RunLog : ObservableObject
{
    public RunLog(string? instance, DateTimeOffset startedAt)
    {
        Instance = instance;
        StartedAt = startedAt;
    }

    /// <summary>启动的实例；null 是不属于任何一次启动的日志（还没启动过游戏时）。</summary>
    public string? Instance { get; }
    public DateTimeOffset StartedAt { get; }
    internal List<LogEntry> All { get; } = [];

    /// <summary>“实例 · 开始时间”，日志页页头和选择框里显示。</summary>
    public string Label => Instance is null ? "" : L.F("log.run_summary", Instance, Fmt.Time(StartedAt));

    internal void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

public sealed partial class LogViewModel : PageViewModel
{
    private const int MaxLines = 5000;

    /// <summary>最多留几次启动的日志（多开测联机时够用）。</summary>
    internal const int KeptRuns = 6;

    public override string Title => L.T("log.title");
    public override string Icon => "M4 5h16v2H4zM4 11h16v2H4zM4 17h11v2H4z";

    public ObservableCollection<LogEntry> Lines { get; } = [];

    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    [ObservableProperty]
    public partial bool FollowLatest { get; set; } = true;

    /// <summary>还没启动过游戏时的日志。</summary>
    private readonly RunLog _launcher = new(null, DateTimeOffset.Now);

    /// <summary>最近几次启动的日志，最新的在最后。</summary>
    public ObservableCollection<RunLog> Runs { get; } = [];

    /// <summary>日志页正在看的那次启动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(RunSummary))]
    public partial RunLog? SelectedRun { get; set; }

    public bool HasSeveralRuns => Runs.Count > 1;

    private RunLog Shown => SelectedRun ?? _launcher;
    private List<LogEntry> _all => Shown.All;

    /// <summary>待进界面的日志行和它属于哪次启动（null：正在看的那次）。</summary>
    private readonly List<(RunLog? Target, LogEntry Entry)> _pending = [];
    private bool _flushQueued;
    private RunLog? _clearTarget;
    private bool _clearPending;

    partial void OnSelectedRunChanged(RunLog? value)
    {
        Recount();
        Rebuild();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorSummary))]
    public partial int ErrorCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WarningSummary))]
    public partial int WarningCount { get; private set; }

    public string ErrorSummary => L.F("log.errors", ErrorCount);
    public string WarningSummary => L.F("log.warnings", WarningCount);
    public string EmptyMessage => L.T(_all.Count > 0 && OnlyProblems ? "log.empty_filtered" : "log.empty");

    partial void OnOnlyProblemsChanged(bool value) => Rebuild();

    protected override void OnLanguageChanged()
    {
        Rebuild();
        foreach (var run in Runs) run.RefreshLabel();
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(CopyLabel));
    }

    public void Add(GameLogLine line) => Add(null, line);

    /// <summary>加到某次启动的日志里（多开时游戏输出各进各的）；run 为 null 时加到正在看的那次。</summary>
    public void Add(RunLog? run, GameLogLine line)
    {
        var level = LogLevelParser.Parse(line.Message, line.Level);
        Post(run, new LogEntry(line.Time, level.ToString().ToUpperInvariant(), line.Message, level));
    }

    public void AddRaw(string message) => AddRaw(null, message);

    public void AddRaw(RunLog? run, string message)
    {
        var level = LogLevelParser.Parse(message);
        Post(run, new LogEntry(DateTimeOffset.Now, level == GameLogLevel.Info ? "JAY" : level.ToString().ToUpperInvariant(), message, level));
    }

    /// <summary>清空正在看的那次启动的日志（别的启动的不动）。</summary>
    public void Clear()
    {
        lock (_pending)
        {
            var target = Shown;
            _pending.RemoveAll(p => (p.Target ?? target) == target);
            _clearTarget = target;
            _clearPending = true;
        }
        // 清空和新到的日志在同一批中按顺序处理，旧的延迟回调不能把它们混在一起。
        Dispatcher.UIThread.Post(Flush);
    }

    /// <summary>
    /// 开始一次启动的日志（在界面线程上调）：新开一份，日志页切过去看它。别的还在运行的游戏的日志留着，页头可以切回去。
    /// </summary>
    public RunLog BeginRun(string instance, DateTimeOffset startedAt)
    {
        var run = new RunLog(instance, startedAt);
        Runs.Add(run);
        while (Runs.Count > KeptRuns) Runs.RemoveAt(0);
        OnPropertyChanged(nameof(HasSeveralRuns));
        SelectedRun = run;
        return run;
    }

    /// <summary>日志页显示某次启动的日志（崩溃卡的“查看日志”看崩的那一个）。已经不在列表里的不换。</summary>
    public void Show(RunLog? run)
    {
        if (run is not null && Runs.Contains(run)) SelectedRun = run;
    }

    /// <summary>模组加载时日志一秒上千行，攒成一批再进界面，不然界面会卡。</summary>
    private void Post(RunLog? target, LogEntry entry)
    {
        lock (_pending)
        {
            _pending.Add((target, entry));
            if (_flushQueued) return;
            _flushQueued = true;
        }
        // 游戏输出来自后台线程，这里不能用 DispatcherTimer（它绑定当前线程的调度器）。
        _ = Task.Delay(100).ContinueWith(_ => Dispatcher.UIThread.Post(Flush), TaskScheduler.Default);
    }

    internal void Flush()
    {
        List<(RunLog? Target, LogEntry Entry)> pending;
        RunLog? clear;
        lock (_pending)
        {
            pending = [.. _pending];
            _pending.Clear();
            _flushQueued = false;
            clear = _clearPending ? _clearTarget : null;
            _clearPending = false;
            _clearTarget = null;
        }
        if (clear is not null)
        {
            clear.All.Clear();
            if (clear == Shown)
            {
                Lines.Clear();
                ErrorCount = WarningCount = Badge = 0;
                OnPropertyChanged(nameof(EmptyMessage));
            }
        }
        if (pending.Count == 0) return;

        var shown = Shown;
        if (!IsCurrent) Badge += pending.Count(p => p.Entry.IsError);
        // 别的启动的日志只存起来，正在看的那次才进界面。
        foreach (var group in pending.Where(p => (p.Target ?? shown) != shown).GroupBy(p => p.Target!))
        {
            group.Key.All.AddRange(group.Select(p => p.Entry));
            if (group.Key.All.Count > MaxLines) group.Key.All.RemoveRange(0, group.Key.All.Count - MaxLines);
        }
        var batch = pending.Where(p => (p.Target ?? shown) == shown).Select(p => p.Entry).ToList();
        if (batch.Count == 0) return;

        var previousCount = _all.Count;
        _all.AddRange(batch);
        var removed = Math.Max(0, _all.Count - MaxLines);
        if (removed > 0)
        {
            var visibleRemoved = _all.Take(Math.Min(removed, previousCount)).Count(Visible);
            for (var i = 0; i < visibleRemoved; i++) Lines.RemoveAt(0);
            _all.RemoveRange(0, removed);
        }
        foreach (var entry in batch.Skip(Math.Max(0, removed - previousCount)).Where(Visible)) Lines.Add(entry);
        Recount();
    }

    private void Recount()
    {
        ErrorCount = _all.Count(e => e.IsError);
        WarningCount = _all.Count(e => e.IsWarning);
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool Visible(LogEntry e) => !OnlyProblems || e.Severity >= GameLogLevel.Warn;

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var e in _all.Where(Visible)) Lines.Add(e);
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var text = string.Join(Environment.NewLine, _all.Select(e => $"[{e.Time}] [{e.Level}] {e.Message}"));
        var ok = false;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow.Clipboard: { } clipboard })
            {
                await clipboard.SetTextAsync(text);
                ok = true;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        SetCopyLabel(ok ? "common.copied" : "log.copy_failed");
        await Task.Delay(1500);
        SetCopyLabel("log.copy_all");
    }

    private string _copyLabelKey = "log.copy_all";

    private void SetCopyLabel(string key)
    {
        _copyLabelKey = key;
        OnPropertyChanged(nameof(CopyLabel));
    }

    /// <summary>“复制全部”按钮的文字：点了之后短暂显示“已复制”或“复制失败”。存翻译键，换语言跟着换。</summary>
    public string CopyLabel => L.T(_copyLabelKey);

    // ---- 正在看的那次启动的实例和开始时间（页头小字） ----

    public bool HasRun => SelectedRun is not null;
    public string RunSummary => SelectedRun?.Label ?? "";

    // ---- 跳到下一个错误 / 警告 ----

    /// <summary>LogPage 订阅：滚动到这一行并选中它。</summary>
    public event Action<LogEntry>? JumpRequested;

    [ObservableProperty]
    public partial LogEntry? SelectedLine { get; set; }

    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;

    partial void OnErrorCountChanged(int value) => OnPropertyChanged(nameof(HasErrors));
    partial void OnWarningCountChanged(int value) => OnPropertyChanged(nameof(HasWarnings));

    [RelayCommand]
    private void NextError() => JumpTo(NextMatch(_all, SelectedLine, e => e.IsError));

    [RelayCommand]
    private void NextWarning() => JumpTo(NextMatch(_all, SelectedLine, e => e.IsWarning));

    private void JumpTo(LogEntry? target)
    {
        if (target is null) return;
        // 跳转时不再跟随最新，不然马上又滚回底部；筛选把它藏起来时（理论上不会，错误和警告都在筛选里）先关掉筛选。
        FollowLatest = false;
        if (!Visible(target)) OnlyProblems = false;
        SelectedLine = target;
        JumpRequested?.Invoke(target);
    }

    /// <summary>从 current 之后找下一个满足条件的行，到末尾回到开头；current 不在列表里时从头找。</summary>
    internal static LogEntry? NextMatch(IReadOnlyList<LogEntry> lines, LogEntry? current, Func<LogEntry, bool> match)
    {
        if (lines.Count == 0) return null;
        var start = current is null ? -1 : IndexOf(lines, current);
        for (var step = 1; step <= lines.Count; step++)
        {
            var entry = lines[(start + step + lines.Count) % lines.Count];
            if (match(entry)) return entry;
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<LogEntry> lines, LogEntry entry)
    {
        for (var i = 0; i < lines.Count; i++) if (ReferenceEquals(lines[i], entry)) return i;
        return -1;
    }
}
