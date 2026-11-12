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

/// <summary>侧栏上的一级入口：游戏库、发现、联机、朋友。</summary>
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

    /// <summary>主题包 shell.json 里的名字：library / discover / multiplayer / people。</summary>
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

    /// <summary>快捷键（Ctrl+1/2/3/4），显示在提示里。</summary>
    public string Shortcut { get; init; } = "";

    public string ToolTip => Shortcut.Length == 0 ? Title : $"{Title} ({Shortcut})";
}

/// <summary>
/// The shell (official design §3, §11.5): routes and history, the sidebar sections, back / forward and "done, go back".
/// The library has one path and no sub-pages: library → library/{instance} → its tabs, with library/new for a new
/// instance. Settings is one page with groups. Page state lives in each view model, so leaving and coming back keeps it.
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
        new("people", Section.People, "shell.nav.people", "shell.nav.people_short", "Icon.Chat") { Shortcut = "Ctrl+4" },
    ];

    /// <summary>侧栏上显示的入口（主题包可以排顺序、藏掉发现或联机；藏掉的还能从别处跳过去）。</summary>
    public ObservableCollection<NavSection> Sections { get; } = [];

    public ShellViewModel(MainViewModel main, LauncherService service)
    {
        _main = main;
        _service = service;
        // Routes of removed pages (library/start, library/logs, settings/tools) go to what replaced them, unless an
        // instance really has that name.
        Router.Rewrite = route => route.Section == Section.Library && route[1] is { } id && _service.InstalledVersions().Contains(id)
            ? route
            : LegacyRoutes.Resolve(route);
        Router.Navigated += OnNavigated;
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Crumb));
            OnPropertyChanged(nameof(Heading));
        });
        _service.LibraryChanged += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(Crumb));
            OnPropertyChanged(nameof(HasCrumb));
            OnPropertyChanged(nameof(Heading));
        });
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
                "new" => Find<DownloadViewModel>(),
                _ => Find<InstanceViewModel>(),
            },
            Section.Discover => Find<ContentViewModel>(),
            Section.Play => Find<MultiplayerViewModel>(),
            Section.People => Find<PeopleViewModel>(),
            Section.Settings => Find<SettingsViewModel>(),
            Section.Me => Find<AccountsViewModel>(),
            Section.About => Find<AboutViewModel>(),
            Section.Help => Find<HelpCenterViewModel>(),
            _ => Find<SettingsViewModel>(),
        };
    }

    /// <summary>页面的路由（从别处直接换了当前页时，按它补一条历史）。</summary>
    public Route RouteOf(PageViewModel page) => page switch
    {
        InstanceViewModel => Route.Of(Section.Library, _service.CurrentInstance ?? "current"),
        DownloadViewModel => Route.Parse(LegacyRoutes.NewInstance),
        ContentViewModel => Route.Parse("discover"),
        MultiplayerViewModel => Route.Parse("play"),
        PeopleViewModel => Route.Parse("people"),
        SettingsViewModel => Route.Parse("settings"),
        AboutViewModel => Route.Parse("about"),
        HelpCenterViewModel help => Route.Parse(help.Selected is { } a ? "help/" + a.Id : "help"),
        AccountsViewModel => Route.Parse("me"),
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
                if (route.Section == Section.Library && route[1] is { } id && id is not ("new" or "current")
                    && _service.InstalledVersions().Contains(id))
                {
                    _service.SetCurrentInstance(id);
                    instanceId = id;
                }
                _main.CurrentPage = PageFor(route);
                if (_main.CurrentPage is HelpCenterViewModel help) help.ShowArticle(route[1]);
                if (_main.CurrentPage is MultiplayerViewModel multiplayer) multiplayer.ShowRoute(route[1]);
                // The instance page was already showing another instance: it does not follow the focus change by itself
                // (so work in progress is not moved to another instance), but an explicit library/{id} switches it.
                if (instanceId is not null && _main.CurrentPage is InstanceViewModel page && !page.IsBusy
                    && page.SelectedId != instanceId && page.Instances.Contains(instanceId))
                    page.SelectedId = instanceId;
                // library/{id}/{tab} opens that tab (help links, the command palette, journeys).
                if (_main.CurrentPage is InstanceViewModel shown && InstanceTabs.IndexOf(route[2]) is { } tab) shown.TabIndex = tab;
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
        Section.People => L.T("shell.nav.people"),
        Section.Me => L.T("me.title"),
        Section.About => L.T("about.title"),
        Section.Help => L.T("help.center.title"),
        _ => L.T("shell.settings"),
    };

    public bool CanGoBack => Router.CanGoBack;
    public bool CanGoForward => Router.CanGoForward;

    /// <summary>带上下文跳过来时的面包屑（“生存整合包 › 模组 › 添加”），没有时是空。</summary>
    public string? ReturnLabel => Router.CurrentReturn?.Label;
    public bool HasReturn => Router.CurrentReturn is not null;

    /// <summary>
    /// What comes after the section name in the top bar: the instance's name on library/{instance}, "New instance" on
    /// library/new. Empty on a section's home.
    /// </summary>
    public string Crumb
    {
        get
        {
            var route = Router.Current;
            if (route.Section != Section.Library || route[1] is not { } part) return "";
            if (part == "new") return L.T("shell.crumb.new_instance");
            var id = part == "current" ? _service.CurrentInstance : part;
            if (id is null) return "";
            return _service.MetaOf(id)?.DisplayName is { Length: > 0 } name ? name : id;
        }
    }

    public bool HasCrumb => Crumb.Length > 0;

    /// <summary>The top bar's heading: the crumb when there is one, the section's name otherwise.</summary>
    public string Heading => HasCrumb ? Crumb : Title;

    /// <summary>The section name in the top bar goes back to the section's home when a crumb follows it.</summary>
    [RelayCommand]
    private void GoSectionHome()
    {
        if (HasCrumb) Router.Navigate(Route.Parse(Route.RootOf(Router.CurrentSection)));
    }

    public bool IsSettingsActive => Router.CurrentSection is Section.Settings or Section.About or Section.Help;
    public bool IsMeActive => Router.CurrentSection == Section.Me;

    [ObservableProperty] public partial NavSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(NavSection? value)
    {
        if (value is null || _updating || value.Section == Router.CurrentSection) return;
        Router.SwitchTo(value.Section);
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
        }
        finally { _updating = false; }
        OnPropertyChanged(nameof(CurrentSection));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ReturnLabel));
        OnPropertyChanged(nameof(HasReturn));
        OnPropertyChanged(nameof(Crumb));
        OnPropertyChanged(nameof(HasCrumb));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(IsSettingsActive));
        OnPropertyChanged(nameof(IsMeActive));
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

    /// <summary>按 id 切入口（侧栏、品牌、Ctrl+1/2/3/4）。藏起来的入口也能切过去。</summary>
    [RelayCommand]
    private void GoSection(string id)
    {
        if (AllSections.FirstOrDefault(s => s.Id == id) is { } s) Router.SwitchTo(s.Section);
    }

    [RelayCommand]
    private void OpenSettings() => Router.SwitchTo(Section.Settings);

    /// <summary>The sidebar avatar: the "Me" page (game accounts, Jay account, friend code, devices, appearance).</summary>
    [RelayCommand]
    private void OpenMe() => Router.SwitchTo(Section.Me);

    private NetworkStatusViewModel? _network;

    /// <summary>The network status in the top bar and its panel.</summary>
    public NetworkStatusViewModel Network => _network ??= new(_main.Multiplayer, r => Navigate(r));

    // ---- 侧栏底部 ----

    /// <summary>账号按钮上的名字（没有账号时是“添加账号”）。</summary>
    public string AccountName => _main.Pages.OfType<HomeViewModel>().FirstOrDefault()?.SelectedAccount?.Name ?? L.T("shell.account.none");

    public string AccountInitial => AccountName.Length > 0 ? AccountName[..1].ToUpperInvariant() : "?";

    /// <summary>设置按钮上的角标（有更新这类提示时）。</summary>
    public int SettingsBadge => _main.Pages.OfType<SettingsViewModel>().Sum(p => p.Badge);
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
        PeopleViewModel => Section.People,
        SettingsViewModel or AboutViewModel or HelpCenterViewModel => Section.Settings,
        AccountsViewModel => Section.Me,
        _ => Section.Library,
    };
}
