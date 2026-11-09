using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Navigation;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 窗口外壳（官方设计 §3）：侧栏按窗口宽度展开（240）或收成图标栏（76），内容区按自己的宽度分三档定外边距，
/// 窗口矮时顶栏收到 40、图标栏只留图标。主题包的 layout/shell.json 还能把导航放到右、上、下，改宽度、顺序、藏掉发现或联机。
/// 快捷键：Ctrl+1/2/3 切入口，Ctrl+, 设置，Alt+←/→ 和鼠标侧键返回、前进。
/// </summary>
public partial class MainWindow
{
    private void AttachShell()
    {
        ThemeService.Changed += ApplyShell;
        SettingsViewModel.SidebarModeChanged += ApplyShell;
        Closed += (_, _) =>
        {
            ThemeService.Changed -= ApplyShell;
            SettingsViewModel.SidebarModeChanged -= ApplyShell;
        };
        MainViewModel? attached = null;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main && !ReferenceEquals(main, attached))
            {
                attached = main;
                // 联机网络带来的主题只在联机页里用：切页时告诉主题服务。
                main.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                        ThemeService.SetNetworkPageActive(main.CurrentPage is MultiplayerViewModel);
                };
                main.Shell.FieldRequested += field => Dispatcher.UIThread.Post(() => RevealField(field), DispatcherPriority.Loaded);
                MovedNotice.IsVisible = _settings is { ShellMovedNoticeSeen: false };
            }
            ApplyShell();
        };
        SizeChanged += (_, _) => ApplyShell();
        ContentColumn.SizeChanged += (_, e) => ApplyContentTier(e.NewSize.Width);
        AddHandler(KeyDownEvent, OnShellKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnShellPointerPressed, RoutingStrategies.Tunnel);
        ApplyShell();
    }

    /// <summary>当前导航位置（自检用）。</summary>
    internal string ShellNav { get; private set; } = "left";

    /// <summary>侧栏现在是不是展开的（自检用）。</summary>
    internal bool SidebarExpanded { get; private set; }

    internal WidthTier ContentTier { get; private set; } = WidthTier.Medium;
    internal HeightTier HeightTierNow { get; private set; } = HeightTier.Normal;

    /// <summary>侧栏模式：用户在设置里选的优先，选“自动”时听主题包的 navStyle。</summary>
    private SidebarMode SidebarModeNow()
    {
        var user = ShellRules.ParseMode(_settings?.SidebarMode);
        if (user != SidebarMode.Auto) return user;
        return ShellRules.ParseMode(ThemeService.Shell?.NavStyle);
    }

    private void ApplyShell()
    {
        var shell = ThemeService.Shell;
        (DataContext as MainViewModel)?.Shell.ApplyOrder(shell);
        var nav = shell?.Nav ?? "left";
        var horizontal = nav is "top" or "bottom";
        ShellNav = nav;

        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var sidebarWidth = shell?.SidebarWidth ?? ShellRules.SidebarWidth;
        var railWidth = shell?.RailWidth ?? ShellRules.RailWidth;
        SidebarExpanded = !horizontal && ShellRules.IsExpanded(width, SidebarModeNow(), sidebarWidth, SidebarExpanded);
        var rail = !horizontal && !SidebarExpanded;
        var navSize = SidebarExpanded ? sidebarWidth : railWidth;

        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        HeightTierNow = LayoutTiers.ForHeight(height, HeightTierNow);
        var tight = HeightTierNow == HeightTier.Tight;

        ShellGrid.ColumnDefinitions.Clear();
        ShellGrid.RowDefinitions.Clear();
        if (horizontal)
        {
            ShellGrid.RowDefinitions = nav == "top" ? new RowDefinitions("Auto,*") : new RowDefinitions("*,Auto");
            Place(SidebarHost, nav == "top" ? 0 : 1, 0);
            Place(ContentColumn, nav == "top" ? 1 : 0, 0);
        }
        else
        {
            ShellGrid.ColumnDefinitions = nav == "right" ? new ColumnDefinitions($"*,{navSize}") : new ColumnDefinitions($"{navSize},*");
            Place(SidebarHost, 0, nav == "right" ? 1 : 0);
            Place(ContentColumn, 0, nav == "right" ? 0 : 1);
        }
        Grid.SetRowSpan(ThemeBackground, 2);
        Grid.SetColumnSpan(ThemeBackground, 2);
        Grid.SetRowSpan(BreakerOverlay, 2);
        Grid.SetColumnSpan(BreakerOverlay, 2);

        // 侧栏里面：竖排是 品牌 / 入口 / 底部 三行；横排是 品牌 | 入口 | 底部 三列。
        SidebarGrid.RowDefinitions.Clear();
        SidebarGrid.ColumnDefinitions.Clear();
        if (horizontal)
        {
            SidebarGrid.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            Place(SidebarHeader, 0, 0);
            Place(NavList, 0, 1);
            Place(SidebarFooter, 0, 2);
            NavList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 });
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
            SidebarFooter.Orientation = Orientation.Horizontal;
            SidebarFooter.VerticalAlignment = VerticalAlignment.Center;
            SidebarFooter.Spacing = 8;
        }
        else
        {
            SidebarGrid.RowDefinitions = new RowDefinitions("Auto,*,Auto");
            Place(SidebarHeader, 0, 0);
            Place(NavList, 1, 0);
            Place(SidebarFooter, 2, 0);
            NavList.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
            ScrollViewer.SetHorizontalScrollBarVisibility(NavList, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(NavList, ScrollBarVisibility.Auto);
            SidebarFooter.Orientation = Orientation.Vertical;
            SidebarFooter.VerticalAlignment = VerticalAlignment.Bottom;
            SidebarFooter.Spacing = 2;
        }
        // 图标栏和横排导航：图标在上、短名在下；隐私行只留盾牌加“无遥测”。
        var compactNav = rail || horizontal;
        NavList.Classes.Set("rail", compactNav);
        NavList.Classes.Set("tight", tight);
        SidebarHost.Classes.Set("rail", compactNav);
        SidebarHost.Classes.Set("horizontal", horizontal);
        PrivacyRow.Compact = compactNav;
        // 图标栏里短名要有 64 宽（§3.4），列表两边只留 4。
        NavList.Margin = horizontal ? new Thickness(4, 6) : compactNav ? new Thickness(4, 4) : new Thickness(8, 4);
        PrivacyRow.Margin = compactNav ? new Thickness(0, 4) : new Thickness(8, 4);
        SidebarHeader.IsVisible = shell?.ShowBrand ?? true;
        BrandName.IsVisible = !compactNav;
        // 很矮时品牌行收起来，给入口和底部腾地方（点品牌回游戏库，Ctrl+1 也一样）。
        if (tight && !horizontal) SidebarHeader.IsVisible = false;
        TopBar.Height = ShellRules.TopBarFor(HeightTierNow);
        ContentColumn.MaxWidth = double.PositiveInfinity;
        PageHost.MaxWidth = shell?.ContentMaxWidth ?? double.PositiveInfinity;
        ApplyContentTier(ContentColumn.Bounds.Width);
    }

    /// <summary>按内容区宽度分档定外边距：紧凑 16、中 24、宽 32（§3.2），32px 回差。</summary>
    private void ApplyContentTier(double width)
    {
        if (width <= 0) return;
        ContentTier = LayoutTiers.ForWidth(width, ContentTier);
        var pad = ShellRules.PagePaddingFor(ContentTier);
        var vertical = HeightTierNow == HeightTier.Normal ? 16 : 8;
        PageHost.Margin = new Thickness(pad, 4, pad, vertical);
        TopBar.Margin = new Thickness(pad - 8, 0, pad, 0);
        SubPageTabs.Margin = new Thickness(pad - 4, 0, pad, 4);
        MovedNotice.Margin = new Thickness(pad, 0, pad, 8);
        RequestBar.Margin = new Thickness(pad, 0, pad, 8);
        // 活动抽屉：紧凑档铺满内容区，其余 400 宽从右边滑出（§5.3）。
        ActivityDrawer.Width = ContentTier == WidthTier.Compact ? double.NaN : 400;
        ActivityDrawer.HorizontalAlignment = ContentTier == WidthTier.Compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ContentColumn.Classes.Set("compact", ContentTier == WidthTier.Compact);
        ContentColumn.Classes.Set("wide", ContentTier == WidthTier.Wide);
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { Shell: var shell } main) return;
        if (e.Key == Key.Escape && main.Activity.IsOpen) { main.Activity.Close(); e.Handled = true; return; }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.J) { main.Activity.Toggle(); e.Handled = true; return; }
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            string? id = e.Key switch { Key.D1 or Key.NumPad1 => "library", Key.D2 or Key.NumPad2 => "discover", Key.D3 or Key.NumPad3 => "multiplayer", _ => null };
            if (id is not null) { shell.GoSectionCommand.Execute(id); e.Handled = true; return; }
            if (e.Key == Key.OemComma) { shell.OpenSettingsCommand.Execute(null); e.Handled = true; return; }
        }
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
        {
            e.Handled = e.Key == Key.Left ? shell.Router.Back() : shell.Router.Forward();
        }
        else if (e.Key == Key.BrowserBack) e.Handled = shell.Router.Back();
        else if (e.Key == Key.BrowserForward) e.Handled = shell.Router.Forward();
    }

    /// <summary>鼠标侧键：返回、前进。</summary>
    private void OnShellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel { Shell: var shell }) return;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.XButton1Pressed) e.Handled = shell.Router.Back();
        else if (kind == PointerUpdateKind.XButton2Pressed) e.Handled = shell.Router.Forward();
    }

    /// <summary>上一次被定位的字段（自检用）。</summary>
    internal Control? RevealedField { get; private set; }

    /// <summary>
    /// 路由带 #字段（§2.5 第 6 条）：在当前页里找 x:Name 是这个字段（或字段名 + Box）的控件，
    /// 滚到视口中间、给焦点、描边闪 2 秒。
    /// </summary>
    private void RevealField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return;
        var target = PageHost.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
            c.Name is { } n && (n.Equals(field, StringComparison.OrdinalIgnoreCase) || n.Equals(field + "Box", StringComparison.OrdinalIgnoreCase)));
        if (target is null) return;
        RevealedField = target;
        target.BringIntoView(new Rect(0, -target.Bounds.Height * 2, target.Bounds.Width, target.Bounds.Height * 5));
        target.Focus(NavigationMethod.Tab);
        target.Classes.Add("field-flash");
        DispatcherTimer.RunOnce(() => target.Classes.Remove("field-flash"), TimeSpan.FromSeconds(2));
    }

    private void ActivityScrim_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.Activity.Close();
        e.Handled = true;
    }

    private void MovedNotice_Close(object? sender, RoutedEventArgs e)
    {
        MovedNotice.IsVisible = false;
        if (_settings is null) return;
        _settings.ShellMovedNoticeSeen = true;
        try { _settings.Save(); } catch (IOException) { }
    }

    private static void Place(Control c, int row, int column)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
    }
}
