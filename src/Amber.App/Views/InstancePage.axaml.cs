using Amber.Core.Layout;
using Amber.Core.Localization;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
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
        Hero.MinHeight = wide ? 200 : compact ? 112 : 168;
        HeroThumb.Width = HeroThumb.Height = compact ? 88 : 64;
        HeroThumb.VerticalAlignment = compact ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Bottom;
        HeroWatermark.IsVisible = !compact;
        HeroPin.IsVisible = HeroFolder.IsVisible = !compact;
        HeroAccount.IsVisible = !compact;
        HeroFacts.IsVisible = true;
        HeroContent.Margin = compact ? new Thickness(12, 10) : new Thickness(20, 14);
        HeroName.FontSize = compact ? 18 : wide ? 26 : 22;
        StartButton.MinWidth = compact ? 0 : 168;
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
