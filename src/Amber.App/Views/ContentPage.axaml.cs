using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

/// <summary>
/// Discover (official design §6.12). Wide: results with a 400 detail column. Medium: the detail is a drawer over the
/// results. Compact: the type chips become a drop-down and the detail covers the result area.
/// </summary>
public partial class ContentPage : UserControl
{
    /// <summary>主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。</summary>
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public ContentPage()
    {
        InitializeComponent();
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "content");
        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyWidth(width));
        };
        // Space on a result row: queue it or take it out (§6.12 keyboard).
        ResultsList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space && DataContext is ContentViewModel vm && ResultsList.SelectedItem is ProjectItem item && !vm.IsModpack)
            {
                vm.ToggleQueueCommand.Execute(item);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The page widths where the layout changes, matching the shell's tiers.</summary>
    internal const double WideFrom = 1000, CompactBelow = 600;

    private string? _tier;

    private void ApplyWidth(double width)
    {
        var tier = width >= WideFrom ? "wide" : width >= CompactBelow ? "medium" : "compact";
        if (tier == _tier) return;
        _tier = tier;
        Classes.Set("compact", tier == "compact");
        TypeChips.IsVisible = tier != "compact";
        TypeCombo.IsVisible = tier == "compact";
        // Compact: the source segment and the import button move to a second header row so the type drop-down fits.
        var stacked = tier == "compact";
        Grid.SetRow(SourceChips, stacked ? 1 : 0);
        Grid.SetColumn(SourceChips, stacked ? 0 : 2);
        Grid.SetColumnSpan(SourceChips, stacked ? 2 : 1);
        SourceChips.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SourceChips.Margin = stacked ? new Thickness(0, 6, 8, 0) : new Thickness(8, 0);
        Grid.SetRow(ImportPackButton, stacked ? 1 : 0);
        ImportPackButton.Margin = stacked ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        var overlay = tier != "wide";
        Grid.SetColumn(DetailPane, overlay ? 0 : 1);
        Grid.SetColumnSpan(DetailPane, overlay ? 2 : 1);
        DetailPane.ZIndex = overlay ? 1 : 0;
        DetailPane.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailPane.Width = tier switch { "wide" => 400, "medium" => Math.Min(380, width - 40), _ => double.NaN };
        DetailPane.Margin = overlay ? new Thickness(0) : new Thickness(14, 0, 0, 0);
        if (tier == "compact") DetailPane.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ContentViewModel vm) return;
        // "/" focuses the search box (not while typing somewhere).
        if (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            SearchInput.Focus();
            e.Handled = true;
        }
        // Esc closes the detail, then (in context mode) goes back.
        else if (e.Key == Key.Escape)
        {
            if (vm.HasSelection) vm.CloseDetailCommand.Execute(null);
            else if (vm.HasReturn) vm.ReturnCommand.Execute(null);
            else return;
            e.Handled = true;
        }
    }

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ContentViewModel vm) vm.SearchCommand.Execute(null);
    }

    private async void ImportPack_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContentViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("content.modpack.picker"),
            FileTypeFilter = [new FilePickerFileType(L.T("content.type.modpack")) { Patterns = ["*.mrpack", "*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ImportLocalPackAsync(path);
    }
}
