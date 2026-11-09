using Amber.App.ViewModels;
using Amber.Core.Layout;
using Avalonia.Controls;

namespace Amber.App.Views;

/// <summary>About and privacy (official design §6.21). Compact width moves each status under its item.</summary>
public partial class AboutPage : UserControl
{
    /// <summary>The width tier the page is laid out for (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Wide;

    public AboutPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => ApplyWidth(Bounds.Width);
    }

    private void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = LayoutTiers.ForWidth(width, Tier);
        if (DataContext is AboutViewModel vm) vm.List.IsCompact = Tier == WidthTier.Compact;
    }
}
