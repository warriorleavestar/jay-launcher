using System.Collections.ObjectModel;
using System.ComponentModel;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>侧栏上的一级入口：游戏库、发现、联机。</summary>
public sealed partial class NavSection : ObservableObject
{
    private readonly string _titleKey;
    private readonly string _shortKey;
    private readonly string _iconKey;

    public NavSection(string id, Section section, string titleKey, string shortKey, string iconKey)
    {
        Id = id;
        Section = section;
        _titleKey = titleKey;
        _shortKey = shortKey;
        _iconKey = iconKey;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShortTitle));
        });
    }

    /// <summary>主题包 shell.json 里的名字：library / discover / multiplayer。</summary>
    public string Id { get; }

    public Section Section { get; }

    public string Title => L.T(_titleKey);

    /// <summary>图标栏里图标下面的短名（日文的“联机”太长，单独翻译）。</summary>
    public string ShortTitle => L.T(_shortKey);

    public Geometry? Icon => Application.Current?.FindResource(_iconKey) as Geometry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int Badge { get; set; }

    public bool HasBadge => Badge > 0;

    [ObservableProperty] public partial bool IsActive { get; set; }

    /// <summary>快捷键（Ctrl+1/2/3），显示在提示里。</summary>
    public string Shortcut { get; init; } = "";

    public string ToolTip => Shortcut.Length == 0 ? Title : $"{Title} ({Shortcut})";
}

/// <summary>
/// 外壳（官方设计 §3、§11.5）：路由和历史栈、侧栏一级入口、入口里的子页、返回 / 前进、带上下文的返回。
/// 过渡期旧页面挂在 3 个入口下面（§11.9）：游戏库 = 启动页 + 实例 + 新建实例（旧下载页）+ 运行记录（旧日志页）；
/// 发现 = 旧资源页；联机 = 旧联机页；设置 = 设置 + 工具（旧工具箱）；账号在侧栏底部。
/// 页面状态在各自的 ViewModel 里，切走再切回还在原处。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly LauncherService _service;
    private bool _syncing;

    public Router Router { get; } = new();

    public NavSection[] AllSections { get; } =
    [
        new("library", Section.Library, "shell.nav.library", "shell.nav.library_short", "Icon.Library") { Shortcut = "Ctrl+1" },
        new("discover", Section.Discover, "shell.nav.discover", "shell.nav.discover_short", "Icon.Discover") { Shortcut = "Ctrl+2" },
        new("multiplayer", Section.Play, "shell.nav.multiplayer", "shell.nav.multiplayer_short", "Icon.Multiplayer") { Shortcut = "Ctrl+3" },
    ];

    /// <summary>侧栏上显示的入口（主题包可以排顺序、藏掉发现或联机；藏掉的还能从别处跳过去）。</summary>
    public ObservableCollection<NavSection> Sections { get; } = [];

    /// <summary>当前入口里的子页（多于一个时顶栏下面显示页签）。</summary>
    public ObservableCollection<PageViewModel> SubPages { get; } = [];

    public ShellViewModel(MainViewModel main, LauncherService service)
    {
        _main = main;
        _service = service;
        Router.Navigated += OnNavigated;
        L.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Title)));
        ApplyOrder(null);
    }

    // ---- 路由 ↔ 页面 ----

    /// <summary>路由对应的页面。按类型找（自检会把某页换成另一个同类型的实例）。</summary>
    public PageViewModel PageFor(Route route)
    {
        var pages = _main.Pages;
        T Find<T>() where T : PageViewModel => pages.OfType<T>().First();
        return route.Section switch
        {
            Section.Library => route[1] switch
            {
                null => Find<LibraryViewModel>(),
                "start" => Find<HomeViewModel>(),
                "new" => Find<DownloadViewModel>(),
                "logs" => Find<LogViewModel>(),
                _ => Find<InstanceViewModel>(),
            },
            Section.Discover => Find<ContentViewModel>(),
            Section.Play => Find<MultiplayerViewModel>(),
            Section.Settings => route[1] == "tools" ? Find<ToolsViewModel>() : Find<SettingsViewModel>(),
            Section.Accounts => Find<AccountsViewModel>(),
            Section.About => Find<AboutViewModel>(),
            Section.Help => Find<HelpCenterViewModel>(),
            _ => Find<SettingsViewModel>(),
        };
    }

    /// <summary>页面的路由（从别处直接换了当前页时，按它补一条历史）。</summary>
    public Route RouteOf(PageViewModel page) => page switch
    {
        HomeViewModel => Route.Parse("library/start"),
        InstanceViewModel => Route.Of(Section.Library, _service.CurrentInstance ?? "current"),
        DownloadViewModel => Route.Parse(LegacyRoutes.NewInstance),
        LogViewModel => Route.Parse(LegacyRoutes.Logs),
        ContentViewModel => Route.Parse("discover"),
        MultiplayerViewModel => Route.Parse("play"),
        ToolsViewModel => Route.Parse(LegacyRoutes.Tools),
        SettingsViewModel => Route.Parse("settings"),
        AboutViewModel => Route.Parse("about"),
        HelpCenterViewModel help => Route.Parse(help.Selected is { } a ? "help/" + a.Id : "help"),
        AccountsViewModel => Route.Parse("accounts"),
        _ => Route.Library,
    };

    /// <summary>去一处。returnLabel 不为空时是带上下文的跳转：顶栏显示【完成，返回】和这段面包屑。</summary>
    public void Navigate(string route, string? returnLabel = null) =>
        Router.Navigate(Route.Parse(route), returnLabel is null ? null : new ReturnContext(Router.Current, returnLabel));

    /// <summary>MainViewModel.CurrentPage 被直接换了（旧代码、自检）：路由跟上，不再反过来换页。</summary>
    internal void OnPageChanged(PageViewModel page)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (!ReferenceEquals(PageFor(Router.Current), page)) Router.Navigate(RouteOf(page));
            else UpdateState();
        }
        finally { _syncing = false; }
    }

    private void OnNavigated(Route route, NavigationKind kind)
    {
        if (!_syncing)
        {
            _syncing = true;
            try
            {
                // library/{实例}：把它设成焦点实例，实例页显示它。
                string? instanceId = null;
                if (route.Section == Section.Library && route[1] is { } id && id is not ("new" or "logs" or "current")
                    && _service.InstalledVersions().Contains(id))
                {
                    _service.SetCurrentInstance(id);
                    instanceId = id;
                }
                _main.CurrentPage = PageFor(route);
                if (_main.CurrentPage is HelpCenterViewModel help) help.ShowArticle(route[1]);
                // The instance page was already showing another instance: it does not follow the focus change by itself
                // (so work in progress is not moved to another instance), but an explicit library/{id} switches it.
                if (instanceId is not null && _main.CurrentPage is InstanceViewModel page && !page.IsBusy
                    && page.SelectedId != instanceId && page.Instances.Contains(instanceId))
                    page.SelectedId = instanceId;
            }
            finally { _syncing = false; }
        }
        UpdateState();
        FieldRequested?.Invoke(route.Fragment);
    }

    /// <summary>路由带 #字段 时（settings#motion）：窗口把这个字段滚到视口中间、给焦点、闪一下描边。</summary>
    public event Action<string?>? FieldRequested;

    // ---- 外壳状态 ----

    public Section CurrentSection => Router.CurrentSection;

    public string Title => Router.CurrentSection switch
    {
        Section.Library => L.T("shell.nav.library"),
        Section.Discover => L.T("shell.nav.discover"),
        Section.Play => L.T("shell.nav.multiplayer"),
        Section.Accounts => _main.Pages.OfType<AccountsViewModel>().First().Title,
        Section.About => L.T("about.title"),
        Section.Help => L.T("help.center.title"),
        _ => L.T("shell.settings"),
    };

    public bool CanGoBack => Router.CanGoBack;
    public bool CanGoForward => Router.CanGoForward;

    /// <summary>带上下文跳过来时的面包屑（“生存整合包 › 模组 › 添加”），没有时是空。</summary>
    public string? ReturnLabel => Router.CurrentReturn?.Label;
    public bool HasReturn => Router.CurrentReturn is not null;

    public bool HasSubPages => SubPages.Count > 1;

    public bool IsSettingsActive => Router.CurrentSection is Section.Settings or Section.About or Section.Help;
    public bool IsAccountsActive => Router.CurrentSection == Section.Accounts;

    [ObservableProperty] public partial NavSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(NavSection? value)
    {
        if (value is null || _updating || value.Section == Router.CurrentSection) return;
        Router.SwitchTo(value.Section);
    }

    [ObservableProperty] public partial PageViewModel? SelectedSubPage { get; set; }

    partial void OnSelectedSubPageChanged(PageViewModel? value)
    {
        if (value is null || _updating || ReferenceEquals(value, _main.CurrentPage)) return;
        Router.Navigate(RouteOf(value));
    }

    private bool _updating;

    private void UpdateState()
    {
        _updating = true;
        try
        {
            var section = Router.CurrentSection;
            foreach (var s in AllSections) s.IsActive = s.Section == section;
            SelectedSection = Sections.FirstOrDefault(s => s.Section == section);
            var subs = SubPagesOf(section);
            if (!subs.SequenceEqual(SubPages))
            {
                SubPages.Clear();
                foreach (var p in subs) SubPages.Add(p);
            }
            SelectedSubPage = SubPages.Contains(_main.CurrentPage) ? _main.CurrentPage : null;
        }
        finally { _updating = false; }
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ReturnLabel));
        OnPropertyChanged(nameof(HasReturn));
        OnPropertyChanged(nameof(HasSubPages));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsAccountsActive));
    }

    private List<PageViewModel> SubPagesOf(Section section)
    {
        var pages = _main.Pages;
        return section switch
        {
            Section.Library => [.. pages.OfType<LibraryViewModel>(), .. pages.OfType<HomeViewModel>(), .. pages.OfType<InstanceViewModel>(), .. pages.OfType<DownloadViewModel>(), .. pages.OfType<LogViewModel>()],
            Section.Settings => [.. pages.OfType<SettingsViewModel>(), .. pages.OfType<ToolsViewModel>()],
            _ => [],
        };
    }

    /// <summary>按主题包的 shell 布局排入口；传空恢复 Jay 自带的顺序。</summary>
    public void ApplyOrder(ShellLayout? shell)
    {
        var order = new List<NavSection>();
        foreach (var id in shell?.Sections ?? [])
            if (AllSections.FirstOrDefault(s => s.Id == id) is { } s && !order.Contains(s)) order.Add(s);
        foreach (var s in AllSections)
            if (!order.Contains(s)) order.Add(s);
        if (shell is not null) order.RemoveAll(s => shell.Hidden.Contains(s.Id) && ThemeLayout.HideableSections.Contains(s.Id));
        if (!order.SequenceEqual(Sections))
        {
            Sections.Clear();
            foreach (var s in order) Sections.Add(s);
        }
        UpdateState();
    }

    // ---- 命令 ----

    [RelayCommand]
    private void Back() => Router.Back();

    [RelayCommand]
    private void Forward() => Router.Forward();

    /// <summary>【完成，返回】：回到带上下文跳过来之前的地方。</summary>
    [RelayCommand]
    private void Return() => Router.Return();

    /// <summary>按 id 切入口（侧栏、品牌、Ctrl+1/2/3）。藏起来的入口也能切过去。</summary>
    [RelayCommand]
    private void GoSection(string id)
    {
        if (AllSections.FirstOrDefault(s => s.Id == id) is { } s) Router.SwitchTo(s.Section);
    }

    [RelayCommand]
    private void OpenSettings() => Router.SwitchTo(Section.Settings);

    [RelayCommand]
    private void OpenAccounts() => Router.SwitchTo(Section.Accounts);

    // ---- 侧栏底部 ----

    /// <summary>账号按钮上的名字（没有账号时是“添加账号”）。</summary>
    public string AccountName => _main.Pages.OfType<HomeViewModel>().FirstOrDefault()?.SelectedAccount?.Name ?? L.T("shell.account.none");

    public string AccountInitial => AccountName.Length > 0 ? AccountName[..1].ToUpperInvariant() : "?";

    /// <summary>设置按钮上的角标（有更新这类提示时）。</summary>
    public int SettingsBadge => _main.Pages.OfType<SettingsViewModel>().Sum(p => p.Badge) + _main.Pages.OfType<ToolsViewModel>().Sum(p => p.Badge);
    public bool HasSettingsBadge => SettingsBadge > 0;

    /// <summary>页面的角标汇总到所在入口；账号名跟着启动页选中的账号。</summary>
    internal void Watch(PageViewModel page)
    {
        page.PropertyChanged += (_, e) => OnPagePropertyChanged(page, e);
        SyncBadges();
    }

    private void OnPagePropertyChanged(PageViewModel page, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.Badge) or null) SyncBadges();
        if (page is HomeViewModel && e.PropertyName is nameof(HomeViewModel.SelectedAccount) or null)
        {
            OnPropertyChanged(nameof(AccountName));
            OnPropertyChanged(nameof(AccountInitial));
        }
    }

    private void SyncBadges()
    {
        foreach (var s in AllSections)
            s.Badge = _main.Pages.Where(p => PageSection(p) == s.Section).Sum(p => p.Badge);
        OnPropertyChanged(nameof(SettingsBadge));
        OnPropertyChanged(nameof(HasSettingsBadge));
    }

    private static Section PageSection(PageViewModel p) => p switch
    {
        ContentViewModel => Section.Discover,
        MultiplayerViewModel => Section.Play,
        SettingsViewModel or ToolsViewModel or AboutViewModel or HelpCenterViewModel => Section.Settings,
        AccountsViewModel => Section.Accounts,
        _ => Section.Library,
    };
}
