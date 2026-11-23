using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
}
