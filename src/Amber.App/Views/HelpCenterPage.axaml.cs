using System.ComponentModel;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Amber.App.Views;

/// <summary>Help center (§6.24): lays itself out for the width; Ctrl+F searches, Esc clears the search.</summary>
public partial class HelpCenterPage : UserControl
{
    private HelpCenterViewModel? _vm;

    /// <summary>The width tier the page is laid out for (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Wide;
    private bool _tierSet;

    public HelpCenterPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as HelpCenterViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            ApplyLayout();
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HelpCenterViewModel.ArticleEntered) or nameof(HelpCenterViewModel.HasQuery)) ApplyLayout();
        if (e.PropertyName is nameof(HelpCenterViewModel.Article)) ArticleScroll.Offset = default;
    }

    internal void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = _tierSet ? LayoutTiers.ForWidth(width, Tier) : LayoutTiers.ForWidth(width);
        _tierSet = true;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var wide = Tier == WidthTier.Wide;
        var compact = Tier == WidthTier.Compact;
        var entered = _vm?.ArticleEntered == true || _vm?.HasQuery == true;
        if (_vm is not null) _vm.IsCompact = compact;
        // Compact without an article: the catalog takes the whole width.
        var catalogOnly = compact && !entered;
        Body.ColumnDefinitions[0].Width = wide ? new GridLength(220) : catalogOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Body.ColumnDefinitions[1].Width = catalogOnly ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = wide ? 16 : 0;
        CatalogPane.IsVisible = wide || catalogOnly;
        ArticleScroll.IsVisible = !catalogOnly;
        ArticlePicker.IsVisible = Tier == WidthTier.Medium;
        CatalogBackButton.IsVisible = compact && entered;
        HelpSearchBox.Width = compact ? 150 : 260;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            HelpSearchBox.Focus();
            HelpSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && HelpSearchBox.IsFocused && _vm.HasQuery)
        {
            _vm.Query = "";
            e.Handled = true;
        }
    }
}
