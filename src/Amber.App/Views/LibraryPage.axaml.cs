using Amber.App.ViewModels;
using Amber.Core.Layout;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Amber.App.Views;

public partial class LibraryPage : UserControl
{
    private TopLevel? _top;

    public LibraryPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    /// <summary>Current layout tier (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Medium;

    /// <summary>
    /// §6.2: wide puts "Needs you" beside the continue card; medium stacks them; compact shrinks the continue card to a row
    /// with a square cover and always lists instances as rows.
    /// </summary>
    private void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = LayoutTiers.ForWidth(width, Tier);
        var wide = Tier == WidthTier.Wide;
        var compact = Tier == WidthTier.Compact;
        Grid.SetColumn(NeedsCard, wide ? 1 : 0);
        Grid.SetRow(NeedsCard, wide ? 0 : 1);
        Grid.SetColumnSpan(ContinueCard, wide ? 1 : 2);
        NeedsCard.Width = wide ? 320 : double.NaN;
        NeedsCard.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 12, 0, 0);
        ContinueThumb.IsVisible = compact;
        ContinueCard.MinHeight = compact ? 112 : 168;
        if (compact) ContinueName.FontSize = 20;
        else ContinueName.ClearValue(TextBlock.FontSizeProperty);
        NewInstanceText.IsVisible = !compact;
        Grid.SetColumn(ViewToggles, compact ? 0 : 2);
        Grid.SetRow(ViewToggles, compact ? 1 : 0);
        Grid.SetColumnSpan(SearchBox, compact ? 2 : 1);
        Grid.SetRow(FilterChips, compact ? 2 : 1);
        if (DataContext is LibraryViewModel vm) vm.IsCompact = compact;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LibraryViewModel vm) vm.IsCompact = Tier == WidthTier.Compact;
    }

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

    /// <summary>§6.2 keys: / or Ctrl+F search, Ctrl+N new instance. Typing in a text box keeps "/".</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || DataContext is not LibraryViewModel vm) return;
        var inText = _top?.FocusManager?.GetFocusedElement() is TextBox;
        if ((e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Oem2 && e.KeyModifiers == KeyModifiers.None && !inText))
        {
            SearchBox.Focus(NavigationMethod.Tab);
            e.Handled = true;
        }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
        {
            vm.NewInstanceCommand.Execute(null);
            e.Handled = true;
        }
    }
}
