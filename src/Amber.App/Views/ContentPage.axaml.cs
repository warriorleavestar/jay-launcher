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
        // Another language changes the labels' widths: lay the header out again (only while the page is shown).
        AttachedToVisualTree += (_, _) => Amber.Core.Localization.L.Changed += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => Amber.Core.Localization.L.Changed -= OnLanguageChanged;
        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            var height = e.NewSize.Height;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ApplyWidth(width);
                ApplyHeight(height);
            });
        };
        // Space on a result row: queue it or take it out (§6.12 keyboard).
        ResultsList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Space && DataContext is ContentViewModel vm && ResultsList.SelectedItem is ProjectItem item && !vm.IsModpack
                && KeyboardScope.Allows(Amber.Core.Navigation.KeyScope.List, e))
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
        ApplyHeader(width, tier);
        if (tier == _tier) return;
        _tier = tier;
        Classes.Set("compact", tier == "compact");
        var overlay = tier != "wide";
        Grid.SetColumn(DetailPane, overlay ? 0 : 1);
        Grid.SetColumnSpan(DetailPane, overlay ? 2 : 1);
        DetailPane.ZIndex = overlay ? 1 : 0;
        DetailPane.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailPane.Width = tier switch { "wide" => 400, "medium" => Math.Min(380, width - 40), _ => double.NaN };
        DetailPane.Margin = overlay ? new Thickness(0) : new Thickness(14, 0, 0, 0);
        if (tier == "compact") DetailPane.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>
    /// The header by what fits, not only by tier (long languages): everything on one row; else the source segment and
    /// [Import modpack file…] move to a second row; and when even the type chips don't fit next to the title (or the page
    /// is compact), the chips become a drop-down.
    /// </summary>
    private void ApplyHeader(double width, string tier)
    {
        if (width <= 0) return;
        // Measured unconstrained, then handed back to the real layout pass.
        static double Wanted(Control c)
        {
            c.Measure(Size.Infinity);
            var w = c.DesiredSize.Width;
            c.InvalidateMeasure();
            return w;
        }
        var title = Wanted(ContentTitle);
        var chips = ChipsWidth();
        var oneRow = title + chips + Wanted(SourceChips) + 16 + Wanted(ImportPackButton) <= width;
        var combo = tier == "compact" || title + chips > width;
        TypeChips.IsVisible = !combo;
        TypeCombo.IsVisible = combo;
        var stacked = !oneRow || tier == "compact";
        Grid.SetRow(SourceChips, stacked ? 1 : 0);
        Grid.SetColumn(SourceChips, stacked ? 0 : 2);
        Grid.SetColumnSpan(SourceChips, stacked ? 2 : 1);
        SourceChips.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SourceChips.Margin = stacked ? new Thickness(0, 6, 8, 0) : new Thickness(8, 0);
        // Row 2 uses only the first two columns, so the auto columns on the right don't steal width from the chips.
        Grid.SetRow(ImportPackButton, stacked ? 1 : 0);
        Grid.SetColumn(ImportPackButton, stacked ? 1 : 3);
        ImportPackButton.HorizontalAlignment = stacked ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ImportPackButton.Margin = stacked ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        Stacked = stacked;
        HeaderGrid.InvalidateMeasure();
    }

    private void OnLanguageChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyWidth(Bounds.Width));

    /// <summary>The type chips on one line (measured from their labels, so it also works while the drop-down shows).</summary>
    private double ChipsWidth()
    {
        if (DataContext is not ContentViewModel vm) return 0;
        var total = 0.0;
        foreach (var name in vm.TypeNames)
        {
            var text = new TextBlock { Text = name.Label, FontSize = FontSize, FontFamily = FontFamily };
            text.Measure(Size.Infinity);
            total += text.DesiredSize.Width + 24 + 4; // chip padding 12 + 12, spacing 4
        }
        return total;
    }

    /// <summary>The source segment is on the header's second row (self-check).</summary>
    internal bool Stacked { get; private set; }

    /// <summary>Below this page height the whole page scrolls and the result list keeps a fixed height.</summary>
    internal const double ShortBelow = 520;

    private void ApplyHeight(double height)
    {
        var tall = height >= ShortBelow;
        PageScroll.VerticalScrollBarVisibility = tall ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        BodyGrid.Height = tall ? double.NaN : 320;
        // Short windows: the body is taller than what is visible, so the state cards sit at its top where their
        // buttons ([Retry], [Show all], …) can be reached without scrolling.
        foreach (var state in new Control[] { SearchingState, EmptyState, ErrorState, KeyState })
        {
            state.VerticalAlignment = tall ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Top;
            state.Margin = tall ? default : new Avalonia.Thickness(0, 12, 0, 0);
        }
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
