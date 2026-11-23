using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Amber.App.Views;

public sealed partial class MobileLauncherView : UserControl
{
    private const double CompactBelow = 720;
    private readonly Grid _shellGrid;

    public MobileLauncherView()
    {
        AvaloniaXamlLoader.Load(this);
        _shellGrid = this.FindControl<Grid>("ShellGrid")
            ?? throw new InvalidOperationException("Mobile launcher shell grid is missing.");
        AddHandler(GotFocusEvent, OnElementGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, e) => ApplyResponsiveShell(e.NewSize.Width, e.NewSize.Height);
        AttachedToVisualTree += (_, _) => ApplyResponsiveShell(Bounds.Width, Bounds.Height);
    }

    private void ApplyResponsiveShell(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var compact = width < CompactBelow || height > width;
        Classes.Set("compact", compact);
        _shellGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "76,*" : "220,*");
    }

    private static void OnElementGotFocus(object? sender, RoutedEventArgs e)
    {
        var textInput = e.Source is TextBox
            || e.Source is Visual visual && visual.FindAncestorOfType<TextBox>() is not null;
        App.MobileTextInputFocusChanged?.Invoke(textInput);
    }
}
