using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Localization;
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
                main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.LogDocked)) ApplyLogDock(); };
                main.LogPopOutRequested += PopOutLog;
                MovedNotice.IsVisible = _settings is { ShellMovedNoticeSeen: false };
            }
            ApplyShell();
        };
        SizeChanged += (_, _) => ApplyShell();
        ContentColumn.SizeChanged += (_, e) => { ApplyContentTier(e.NewSize.Width); ApplyLogDock(); };
        AttachLogDock();
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
        HelpDrawer.Width = ContentTier == WidthTier.Compact ? double.NaN : 400;
        HelpDrawer.HorizontalAlignment = ContentTier == WidthTier.Compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        PalettePanel.Width = ContentTier == WidthTier.Compact ? double.NaN : 640;
        PalettePanel.HorizontalAlignment = ContentTier == WidthTier.Compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        ContentColumn.Classes.Set("compact", ContentTier == WidthTier.Compact);
        ContentColumn.Classes.Set("wide", ContentTier == WidthTier.Wide);
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { Shell: var shell } main) return;
        if (main.Palette.IsOpen)
        {
            HandlePaletteKey(main.Palette, e);
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K && !main.FirstRun.IsOpen)
        {
            OpenPalette(main.Palette);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && main.Activity.IsOpen) { main.Activity.Close(); e.Handled = true; return; }
        // Help (§6.24): F1 this page's article, Shift+F1 the help center; in the drawer Ctrl+F searches and Esc closes.
        if (e.Key == Key.F1 && !main.FirstRun.IsOpen)
        {
            if (e.KeyModifiers == KeyModifiers.Shift) { main.Help.Close(); shell.Navigate("help"); }
            else if (e.KeyModifiers == KeyModifiers.None) main.OpenHelp();
            e.Handled = true;
            return;
        }
        if (main.Help.IsOpen)
        {
            if (e.Key == Key.Escape) { main.Help.Close(); HelpButton.Focus(); e.Handled = true; return; }
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) { HelpDrawerSearch.Focus(); HelpDrawerSearch.SelectAll(); e.Handled = true; return; }
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.J) { main.Activity.Toggle(); e.Handled = true; return; }
        // Ctrl+` docks / undocks the game log (§6.9).
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.OemTilde) { main.ToggleLogDock(); e.Handled = true; return; }
        if (e.Key == Key.Escape && main.LogDocked && LogDockIsDrawer) { main.LogDocked = false; e.Handled = true; return; }
        // Ctrl+V outside a text box: a pasted invite (or any other Jay code) opens multiplayer with it filled in.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V && FocusManager?.GetFocusedElement() is not TextBox)
        {
            _ = PasteCodeAsync(main);
            return;
        }
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

    private static async Task PasteCodeAsync(MainViewModel main)
    {
        if (Amber.App.Services.AvaloniaClipboard.FromMainWindow() is not { } board) return;
        string? text;
        try { text = await board.GetTextAsync(); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { return; }
        main.PasteCode(text);
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
    internal void RevealField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return;
        // Settings show one group at a time: open the field's group first and lay it out.
        if (DataContext is MainViewModel { CurrentPage: SettingsViewModel settings })
        {
            settings.ShowField(field);
            PageHost.UpdateLayout();
        }
        var target = PageHost.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
            c.Name is { } n && (n.Equals(field, StringComparison.OrdinalIgnoreCase) || n.Equals(field + "Box", StringComparison.OrdinalIgnoreCase)));
        if (target is null) return;
        RevealedField = target;
        target.BringIntoView(new Rect(0, -target.Bounds.Height * 2, target.Bounds.Width, target.Bounds.Height * 5));
        target.Focus(NavigationMethod.Tab);
        target.Classes.Add("field-flash");
        DispatcherTimer.RunOnce(() => target.Classes.Remove("field-flash"), TimeSpan.FromSeconds(2));
    }

    private IInputElement? _focusBeforePalette;

    /// <summary>Ctrl+K: opens the palette with the cursor in its box; focus comes back where it was on close.</summary>
    internal void OpenPalette(CommandPaletteViewModel palette)
    {
        _focusBeforePalette = FocusManager?.GetFocusedElement();
        palette.Open();
        Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
    }

    private void ClosedPalette()
    {
        (_focusBeforePalette ?? this).Focus();
        _focusBeforePalette = null;
    }

    /// <summary>While the palette is open: ↑↓ choose, Enter runs (Ctrl+Enter starts an instance), Tab fills, Esc goes back.</summary>
    private void HandlePaletteKey(CommandPaletteViewModel palette, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                palette.Back();
                break;
            case Key.K when e.KeyModifiers == KeyModifiers.Control:
                palette.Close();
                break;
            case Key.Up or Key.Down:
                palette.Move(e.Key == Key.Down ? 1 : -1);
                if (palette.SelectedIndex >= 0) PaletteList.ScrollIntoView(palette.SelectedIndex);
                break;
            case Key.Enter:
                palette.Run(alt: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                break;
            case Key.Tab:
                palette.Fill();
                PaletteBox.CaretIndex = PaletteBox.Text?.Length ?? 0;
                break;
            default:
                return;
        }
        e.Handled = true;
        if (!palette.IsOpen) ClosedPalette();
    }

    private void PaletteScrim_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.Palette.Close();
        ClosedPalette();
        e.Handled = true;
    }

    private void PaletteList_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel main || (e.Source as Control)?.DataContext is not PaletteEntry entry) return;
        main.Palette.Run(entry);
        if (!main.Palette.IsOpen) ClosedPalette();
        else PaletteBox.Focus();
    }

    private void HelpScrim_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main) main.Help.Close();
        e.Handled = true;
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

    // ---- Docked game log (§6.9) ----

    /// <summary>Below this content height the docked log becomes a drawer over the page instead of taking rows.</summary>
    internal const double LogDrawerBelow = 560;

    /// <summary>The docked log is shown as a drawer (short window).</summary>
    internal bool LogDockIsDrawer { get; private set; }

    private LogWindow? _logWindow;

    private void AttachLogDock()
    {
        var popOut = new Button { Content = L.T("log.dock.pop_out"), Classes = { "ghost", "small" } };
        popOut.Click += (_, _) => (DataContext as MainViewModel)?.PopOutLog();
        var close = new Button { Content = L.T("log.dock.close"), Classes = { "ghost", "small" } };
        close.Click += (_, _) => { if (DataContext is MainViewModel m) m.LogDocked = false; };
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            popOut.Content = L.T("log.dock.pop_out");
            close.Content = L.T("log.dock.close");
        });
        LogDockContent.Buttons.Add(popOut);
        LogDockContent.Buttons.Add(close);
        LogDockSplitter.DragDelta += (_, e) =>
        {
            if (LogDockIsDrawer) return;
            LogDock.Height = Math.Clamp(LogDock.Height - e.Vector.Y, 120, Math.Max(160, ContentColumn.Bounds.Height - 200));
        };
    }

    private void ApplyLogDock()
    {
        var docked = DataContext is MainViewModel { LogDocked: true };
        LogDockIsDrawer = ContentColumn.Bounds.Height > 0 && ContentColumn.Bounds.Height < LogDrawerBelow;
        LogDock.IsVisible = docked;
        LogDockSplitter.IsVisible = docked && !LogDockIsDrawer;
        if (LogDockIsDrawer)
        {
            // Short window: a drawer from the bottom over the page (60% of the content height).
            Grid.SetRow(LogDock, 4);
            LogDock.VerticalAlignment = VerticalAlignment.Bottom;
            LogDock.Height = Math.Max(160, ContentColumn.Bounds.Height * 0.6);
            LogDock.BoxShadow = Avalonia.Media.BoxShadows.Parse("0 -4 16 0 #40000000");
        }
        else
        {
            Grid.SetRow(LogDock, 6);
            LogDock.VerticalAlignment = VerticalAlignment.Stretch;
            if (LogDock.Height is < 120 or double.NaN) LogDock.Height = 240;
            LogDock.BoxShadow = default;
        }
    }

    private void PopOutLog()
    {
        if (DataContext is not MainViewModel main) return;
        if (_logWindow is { } open) { open.Activate(); return; }
        _logWindow = new LogWindow(main.Log, () => main.LogDocked = true);
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show(this);
    }

    /// <summary>The popped-out log window, if open (self-check).</summary>
    internal LogWindow? PoppedOutLog => _logWindow;
}
