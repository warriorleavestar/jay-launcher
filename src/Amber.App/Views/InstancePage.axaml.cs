using Amber.Core.Layout;
using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class InstancePage : UserControl
{
    /// <summary>主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public InstancePage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "instances");
        SizeChanged += (_, e) =>
        {
            ApplyWidth(e.NewSize.Width);
            ApplyHeight(e.NewSize.Height);
        };
        ModsList.AddHandler(KeyDownEvent, OnModsKeyDown, RoutingStrategies.Tunnel);
        EnabledPackList.AddHandler(KeyDownEvent, OnPacksKeyDown, RoutingStrategies.Tunnel);
        WorldsList.AddHandler(KeyDownEvent, OnWorldsKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => WireLogs();
        // The world panel opens under the list: scroll to it so it is seen even in a short window.
        WorldPanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && WorldPanel.IsVisible)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => WorldPanel.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        };
        DisabledPackList.AddHandler(KeyDownEvent, OnPacksKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>§6.6 world list: Enter opens the world panel (data packs, backups), B backs the world up.</summary>
    private void OnWorldsKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || WorldsList.SelectedItem is not ViewModels.WorldItem world || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.ShowDatapacksCommand.Execute(world);
                break;
            case Key.B:
                vm.BackupWorldCommand.Execute(world);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>§6.7 pack lists: Space on / off, Alt+↑↓ move (enabled list only), Delete moves the pack to Recently deleted.</summary>
    private void OnPacksKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || sender is not ListBox list || list.SelectedItem is not ViewModels.PackFileItem item) return;
        switch (e.Key)
        {
            case Key.Space when e.KeyModifiers == KeyModifiers.None:
                vm.TogglePackCommand.Execute(item);
                (item.IsOn ? EnabledPackList : DisabledPackList).SelectedItem = item;
                break;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.Alt && list == EnabledPackList:
                vm.MovePack(item, e.Key == Key.Up ? -1 : 1);
                list.SelectedItem = item;
                list.ContainerFromItem(item)?.Focus();
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                vm.DeletePackCommand.Execute(item);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Current layout tier (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Medium;

    /// <summary>
    /// §6.3: the hero is 200 high when wide, 168 medium and 112 compact. Compact turns the cover into a square on the left,
    /// keeps only ⋯ on the right and moves the account line under the button. §6.4: the overview has two columns only when wide.
    /// </summary>
    private void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = LayoutTiers.ForWidth(width, Tier);
        var compact = Tier == WidthTier.Compact;
        var wide = Tier == WidthTier.Wide;
        // A minimum, not a fixed height: long translations and the compact layout grow the hero instead of cutting the button off.
        Hero.MinHeight = Short ? 0 : wide ? 200 : compact ? 112 : 168;
        HeroThumb.Width = HeroThumb.Height = compact ? 88 : 64;
        HeroThumb.VerticalAlignment = compact ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Bottom;
        HeroWatermark.IsVisible = !compact && !Short;
        HeroPin.IsVisible = HeroFolder.IsVisible = !compact;
        HeroAccount.IsVisible = !compact;
        HeroFacts.IsVisible = true;
        HeroContent.Margin = compact ? new Thickness(12, 10) : new Thickness(20, 14);
        HeroName.FontSize = compact ? 18 : wide ? 26 : 22;
        StartButton.MinWidth = compact ? 0 : 168;
        // Mods (§6.5): the inspector is a fixed column when wide and covers the right of the table otherwise; compact keeps only ＋ and ⋯.
        Grid.SetColumn(ModInspector, wide ? 1 : 0);
        Grid.SetColumnSpan(ModInspector, wide ? 1 : 2);
        ModInspector.HorizontalAlignment = wide ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Right;
        ModInspector.Width = compact ? double.NaN : 320;
        ModInspector.Margin = wide ? new Thickness(10, 0, 0, 0) : new Thickness(0);
        ModInspector.BoxShadow = wide ? default : Avalonia.Media.BoxShadows.Parse("0 6 24 0 #40000000");
        CheckUpdatesButton.IsVisible = !compact;
        // Logs (§6.9): the run list is a column only when wide; otherwise a picker above the log.
        LogRunsPane.IsVisible = wide;
        LogsGrid.ColumnDefinitions[0].Width = wide ? new GridLength(240) : new GridLength(0);
        LogRunPicker.IsVisible = !wide;
        LogSearchBox.Width = compact ? 160 : 220;
        LogActions.IsVisible = true;
        AddModText.IsVisible = !compact;
        // Overview: two columns only when wide; otherwise "this instance" goes on top.
        if (wide)
        {
            OverviewGrid.ColumnDefinitions = new ColumnDefinitions("2*,*");
            Grid.SetColumn(OverviewSide, 1);
            Grid.SetRow(OverviewSide, 0);
            Grid.SetRow(OverviewMain, 0);
        }
        else
        {
            OverviewGrid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(OverviewSide, 0);
            Grid.SetRow(OverviewSide, 0);
            Grid.SetRow(OverviewMain, 1);
        }
    }

    /// <summary>§3.3 short windows: the hero drops its minimum height and watermark so the tabs keep room for their lists.</summary>
    private void ApplyHeight(double height)
    {
        if (height <= 0) return;
        Short = height < 520;
        // Very short (the minimum window with a notice above): even a trimmed hero leaves the tabs card only a sliver,
        // so the page scrolls as a whole and the card keeps a usable fixed height.
        var scrolls = height < ScrollBelow;
        PageScroll.VerticalScrollBarVisibility = scrolls ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        DetailsCard.Height = scrolls ? DetailsHeightWhenScrolling : double.NaN;
        // The last session / [Help me fix…] cards may take at most about half the page, then scroll.
        NoticeScroll.MaxHeight = Math.Max(160, height * 0.5);
        if (Short)
        {
            Hero.MinHeight = 0;
            HeroWatermark.IsVisible = false;
        }
        else ApplyWidth(Bounds.Width);
    }

    /// <summary>Below this page height the page scrolls as a whole (see <see cref="ApplyHeight"/>).</summary>
    internal const double ScrollBelow = 640;

    /// <summary>The tabs card's height while the page scrolls: room for the toolbar and a few list rows.</summary>
    internal const double DetailsHeightWhenScrolling = 380;

    /// <summary>The page is short enough that the hero is trimmed (self-check).</summary>
    internal bool Short { get; private set; }

    private TopLevel? _top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>§6.3 keys: Ctrl+Tab / Ctrl+Shift+Tab switch tabs, Alt+1…8 go straight to a tab, Ctrl+Shift+Q quits the running game.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || Vm is not { HasInstance: true } vm) return;
        var count = Enum.GetValues<InstanceTab>().Length;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && vm.TabIndex == (int)InstanceTab.Mods)
        {
            ModSearchBox.Focus(NavigationMethod.Tab);
            e.Handled = true;
            return;
        }
        if (vm.TabIndex == (int)InstanceTab.Logs && HandleLogsKey(vm, e)) return;
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            vm.TabIndex = (vm.TabIndex + step + count) % count;
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key >= Key.D1 && e.Key < Key.D1 + count)
        {
            vm.TabIndex = e.Key - Key.D1;
            e.Handled = true;
        }
        else if (e.Key == Key.Q && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && vm.Start?.Side is Amber.Core.GameLibrary.StartSide.QuitGame)
        {
            vm.SideActionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private InstanceViewModel? _logsWired;

    /// <summary>Gives the Logs tab its clipboard, save picker, locked privacy preview and scrolling.</summary>
    private void WireLogs()
    {
        if (Vm is not { } vm || ReferenceEquals(vm, _logsWired)) return;
        if (_logsWired is { } old) old.LogJumpRequested -= OnLogJump;
        _logsWired = vm;
        vm.LogJumpRequested += OnLogJump;
        vm.CopyText = async text =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
                await clipboard.SetTextAsync(text);
                return true;
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        };
        vm.SaveReportAs = async name =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L.T("logs.report.export"),
                SuggestedFileName = name,
                DefaultExtension = "txt",
            });
            return file?.TryGetLocalPath();
        };
        vm.AskExportReport = async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return Amber.App.Controls.Safety.SafetyChoice.Keep;
            return await Amber.App.Controls.Safety.SafetyDialog.ChooseAsync(owner, L.T("logs.report.title"), L.T("logs.report.body"),
                L.T("logs.report.save"), L.T("common.cancel"), L.T("logs.report.copy"),
                [L.T("logs.report.has"), L.T("logs.report.removed")], Amber.App.Controls.Safety.SafetyKind.Safe);
        };
    }

    private void OnLogJump(ViewModels.LogRow row) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (LogList.IsEffectivelyVisible) LogList.ScrollIntoView(row);
    }, Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>§6.9 keys: F8 / Shift+F8 next / previous error, Ctrl+F search, End back to the latest (follow).</summary>
    private bool HandleLogsKey(InstanceViewModel vm, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F8 when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                vm.JumpLogError(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                LogSearchBox.Focus(NavigationMethod.Tab);
                break;
            case Key.End when e.KeyModifiers == KeyModifiers.None && !LogSearchBox.IsFocused:
                vm.LogFollow = true;
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    private int _modAnchor = -1;

    /// <summary>
    /// §6.5 table keys (only while the table has focus, so typing in the search box or an input method never triggers them):
    /// Space tick, Shift+↑↓ tick a range, Ctrl+A tick all, E on / off, U update, Delete delete (undo), Enter inspector,
    /// Esc closes the inspector, then clears ticks, then clears filters.
    /// </summary>
    private void OnModsKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var rows = vm.ModsView.ToList();
        var current = ModsList.SelectedItem as ModItem;
        var index = current is null ? -1 : rows.IndexOf(current);
        switch (e.Key)
        {
            case Key.Space when current is not null && e.KeyModifiers == KeyModifiers.None:
                current.Selected = !current.Selected;
                _modAnchor = index;
                break;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.Shift && rows.Count > 0:
                var target = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, rows.Count - 1);
                if (_modAnchor < 0) _modAnchor = Math.Max(index, 0);
                foreach (var i in Amber.Core.Content.ModQuery.Range(_modAnchor, target, rows.Count)) rows[i].Selected = true;
                ModsList.SelectedIndex = target;
                ModsList.ContainerFromIndex(target)?.Focus();
                break;
            case Key.A when e.KeyModifiers == KeyModifiers.Control:
                vm.SelectAllModsCommand.Execute(null);
                break;
            case Key.E when current is not null && e.KeyModifiers == KeyModifiers.None:
                vm.ToggleModCommand.Execute(current);
                break;
            case Key.U when current is { HasUpdate: true } && e.KeyModifiers == KeyModifiers.None:
                vm.UpdateModCommand.Execute(current);
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None:
                var targets = vm.Mods.Where(m => m.Selected).ToList();
                if (targets.Count == 0 && current is not null) targets.Add(current);
                if (targets.Count == 0) return;
                vm.DeleteModsWithUndo(targets);
                break;
            case Key.Escape:
                if (vm.HasInspected) vm.CloseInspectorCommand.Execute(null);
                else if (vm.HasModSelection) vm.ClearModSelectionCommand.Execute(null);
                else if (vm.IsModFiltered) vm.ClearModFiltersCommand.Execute(null);
                else return;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void CloseHeroForceKill(object? sender, RoutedEventArgs e) => HeroForceKill.Flyout?.Hide();

    private async void ExportLastRun_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("home.crash.export_title"),
            SuggestedFileName = $"jay-error-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportLastRun(path);
    }

    private InstanceViewModel? Vm => DataContext as InstanceViewModel;
    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async Task PickAndAddAsync(string title, string filterName, string[] patterns, string? target)
    {
        if (Vm is not { } vm || Storage is not { } storage || target is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) vm.AddFiles(paths, target);
    }

    private async void AddMods_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.mods.picker"), L.T("instance.picker.mods_filter"), ["*.jar", "*.zip"], Vm?.ModsDir);

    private async void AddResourcePacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.resourcepacks.picker"), L.T("instance.picker.resourcepacks_filter"), ["*.zip"], Vm?.ResourcePacksDir);

    private async void AddShaderPacks_Click(object? sender, RoutedEventArgs e) =>
        await PickAndAddAsync(L.T("instance.shaderpacks.picker"), L.T("instance.picker.shaderpacks_filter"), ["*.zip"], Vm?.ShaderPacksDir);

    private async void AddDatapacks_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("instance.datapacks.picker"),
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(L.T("instance.picker.datapacks_filter")) { Patterns = ["*.zip"] }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) vm.AddDatapacks(paths);
    }

    private async void ImportWorld_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("instance.worlds.picker"),
            FileTypeFilter = [new FilePickerFileType(L.T("common.archive")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.ImportWorld(path);
    }

    /// <summary>【⋯ 更多】→“导出整合包…”：按当前实例填好选项，打开对话框。</summary>
    private async void ExportPackDialog_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { HasInstance: true } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        vm.PrepareExport();
        await new ExportPackDialog { DataContext = vm }.ShowDialog(owner);
    }

    private async void ExportScript_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Storage is not { } storage) return;
        var extension = OperatingSystem.IsWindows() ? "bat" : "sh";
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("instance.export.script_picker"),
            SuggestedFileName = $"{vm.SelectedId}.{extension}",
            DefaultExtension = extension,
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportLaunchScriptAsync(path);
    }
}
