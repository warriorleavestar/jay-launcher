using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// The small topmost window for a safety request (a knock, an incoming file) while Jay is in the background
/// (official §6.15): 360 wide, as tall as its content (at least 200), locked styling, the same buttons as the room.
/// It never takes the keyboard from the game: it is shown without being activated. Closing it, Esc and the timeout
/// all pick the default (safe) choice.
/// </summary>
internal sealed class RequestPopupWindow : Window
{
    private readonly RequestCenter _center;
    private readonly RequestItem _item;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _remaining;
    private bool _answered;

    public RequestHandle Handle { get; }

    /// <summary>The default (safe) button: it gets the focus once the user clicks into the window.</summary>
    internal SafetyButton DefaultButton { get; }

    internal IReadOnlyList<SafetyButton> Buttons { get; }

    public RequestPopupWindow(RequestCenter center, RequestHandle handle)
    {
        _center = center;
        Handle = handle;
        _item = new RequestItem(center, handle);
        Title = L.T("multiplayer.popup.window_title");
        Width = 360;
        MinHeight = 200;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var buttons = handle.Spec.Choices.Select(c =>
        {
            var button = new SafetyButton
            {
                Content = c.Label,
                Kind = c.IsDanger ? SafetyKind.Danger : c.IsDefault ? SafetyKind.Safe : SafetyKind.Confirm,
                Tag = c.Id,
            };
            button.Click += (_, _) => Answer(c.Id);
            return button;
        }).ToList();
        Buttons = buttons;
        DefaultButton = buttons.FirstOrDefault(b => handle.Spec.Choices.First(c => c.Id == (string)b.Tag!).IsDefault) ?? buttons[0];

        _remaining = new TextBlock { FontSize = 12, IsVisible = _item.HasRemaining };
        var body = new StackPanel { Spacing = 6 };
        if (_item.HasBody) body.Children.Add(new TextBlock { Text = _item.Body, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(_remaining);
        // Body scrolls when the screen is short; the buttons stay put underneath.
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var bar = new WrapPanel { ItemSpacing = 8, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) bar.Children.Add(b);
        var layout = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        bar.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(bar);
        layout.Children.Add(scroll);
        Content = new SafetyPanel { Title = handle.Spec.Title, Body = layout, Margin = new Thickness(10) };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateRemaining());
        UpdateRemaining();
        Opened += (_, _) =>
        {
            _timer.Start();
            PlaceInCorner();
            if (Screens.ScreenFromWindow(this) is { } screen) scroll.MaxHeight = Math.Max(120, screen.WorkingArea.Height / screen.Scaling - 200);
        };
        Activated += (_, _) => DefaultButton.Focus();
        Closing += (_, _) =>
        {
            _timer.Stop();
            // The window's ✕ counts as the safe answer, like Esc and the timeout.
            if (!_answered) Answer(DefaultChoice, close: false);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Answer(DefaultChoice); e.Handled = true; }
        };
    }

    private string DefaultChoice => (Handle.Spec.Choices.FirstOrDefault(c => c.IsDefault) ?? Handle.Spec.Choices[0]).Id;

    private void UpdateRemaining() => _remaining.Text = _item.RemainingText;

    /// <summary>Answer from this window (no-op when another place already did).</summary>
    internal void Answer(string choice, bool close = true)
    {
        _answered = true;
        _center.Resolve(Handle, choice, RequestSource.Popup);
        if (close) Close();
    }

    /// <summary>The request was answered somewhere else: just go away.</summary>
    internal void Dismiss()
    {
        _answered = true;
        Close();
    }

    /// <summary>Bottom-right corner of the screen Jay is on.</summary>
    private void PlaceInCorner()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        var stack = RequestPopups.IndexOf(this);
        Position = new PixelPoint(area.Right - size.Width - 16, Math.Max(area.Y, area.Bottom - (size.Height + 16) * (stack + 1)));
    }
}

/// <summary>Opens a popup for each safety request posted while Jay's window is not active; closes it when answered anywhere.</summary>
internal sealed class RequestPopups
{
    private static readonly List<RequestPopupWindow> OpenWindows = [];

    private readonly Window _owner;
    private readonly RequestCenter _center;
    private readonly Func<bool> _enabled;

    public RequestPopups(Window owner, RequestCenter center, Func<bool> enabled)
    {
        _owner = owner;
        _center = center;
        _enabled = enabled;
        center.Posted += handle => Dispatcher.UIThread.Post(() =>
        {
            if (handle.Spec.IsSafety && !handle.IsResolved && !_owner.IsActive && _enabled()) Show(handle);
        });
        center.Resolved += handle => Dispatcher.UIThread.Post(() =>
        {
            foreach (var w in OpenWindows.Where(w => w.Handle == handle).ToList()) w.Dismiss();
        });
        // Back in Jay: the room panel and the request bar take over.
        owner.Activated += (_, _) =>
        {
            foreach (var w in OpenWindows.ToList()) w.Dismiss();
        };
    }

    public static IReadOnlyList<RequestPopupWindow> Open => OpenWindows;

    internal static int IndexOf(RequestPopupWindow window) => Math.Max(0, OpenWindows.IndexOf(window));

    /// <summary>Show the popup for one request (also used by the self-check).</summary>
    internal RequestPopupWindow Show(RequestHandle handle)
    {
        if (OpenWindows.FirstOrDefault(w => w.Handle == handle) is { } existing) return existing;
        var window = new RequestPopupWindow(_center, handle);
        OpenWindows.Add(window);
        window.Closed += (_, _) => OpenWindows.Remove(window);
        window.Show();
        return window;
    }
}
