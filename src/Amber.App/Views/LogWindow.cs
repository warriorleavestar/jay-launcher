using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;

namespace Amber.App.Views;

/// <summary>The game log popped out into its own window (§6.9). Uses the current theme; not a safety surface.</summary>
public sealed class LogWindow : Window
{
    public LogWindow(LogViewModel log, Action dockBack)
    {
        Title = L.T("log.dock.window_title");
        Width = 900;
        Height = 420;
        MinWidth = 480;
        MinHeight = 240;
        var view = new LogDockView { DataContext = log, Margin = new Avalonia.Thickness(12) };
        var dock = new Button { Content = L.T("log.dock.dock_back"), Classes = { "ghost", "small" } };
        dock.Click += (_, _) => { dockBack(); Close(); };
        view.Buttons.Add(dock);
        Content = view;
        Amber.App.Services.DensityService.Attach(this);
    }
}
