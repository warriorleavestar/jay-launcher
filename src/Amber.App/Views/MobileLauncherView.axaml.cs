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
        // Normal navigation to the new-instance panel adds library/new to history, so pop it.
        // CloseAfterStart navigates to library and would append a new root entry; using it first
        // creates a root -> new -> root cycle where repeated Android Back reopens the panel.
        // Keep the close action only as a fallback for legacy/directly assigned page state.
        if (main.CurrentPage is ViewModels.DownloadViewModel { IsBusy: false } download)
        {
            if (main.Shell.Router.CanGoBack) return main.Shell.Router.Back();
            download.CloseAfterStart?.Invoke();
            return true;
        }
        return main.Shell.Router.Back();
    }
}
