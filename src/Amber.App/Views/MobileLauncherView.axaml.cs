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
        // The Android single-view root lives for the process lifetime. Register immediately:
        // Avalonia.Android can install the root without raising the desktop-style visual-tree
        // attach event, which would otherwise leave native Back with no launcher callback.
        App.MobileBackRequested = HandleMobileBack;
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

    private bool HandleMobileBack()
    {
        if (DataContext is not ViewModels.MainViewModel main) return false;
        if (main.Activity.IsOpen)
        {
            main.Activity.Close();
            return true;
        }
        if (main.Help.IsOpen)
        {
            main.Help.Close();
            return true;
        }
        if (main.Palette.IsOpen)
        {
            main.Palette.Close();
            return true;
        }
        // The new-instance panel can be opened from a fresh library without creating a history
        // entry. Its visible X and Escape key both use CloseAfterStart, so Android Back must use
        // that same route instead of relying only on Router.Back().
        if (main.CurrentPage is ViewModels.DownloadViewModel { IsBusy: false } download)
        {
            download.CloseAfterStart?.Invoke();
            return true;
        }
        return main.Shell.Router.Back();
    }
}
