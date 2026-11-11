using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// Part of AMBER_SELFTEST_SCOPE=shell: the §8.5 keyboard scope rules. Global Ctrl+N, Ctrl+Shift+Q only while playing,
/// F6 / Shift+F6 between regions, Ctrl+Alt+Shift+T back to the default look with Undo, and text boxes and an
/// input method keep their keys. No game directory, no network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task KeyboardScopeAsync(ShellViewModel shell)
    {
        // Ctrl+N from another section opens the new-instance panel.
        shell.Navigate("discover");
        await Task.Delay(300);
        window.Focus();
        Press(Key.N, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+N from Discover opens New instance", main.CurrentPage is DownloadViewModel && shell.SelectedSection?.Id == "library",
            shell.Router.Current.ToString());

        // In a text box Ctrl+N belongs to the text box.
        // An empty library has no search box, so fall back to Discover's.
        shell.Navigate("library");
        await Task.Delay(400);
        TextBox? VisibleBox() => window.PageHost.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
        var box = VisibleBox();
        if (box is null)
        {
            shell.Navigate("discover");
            await Task.Delay(400);
            box = VisibleBox();
        }
        if (box is null)
            Check("Library or Discover has a visible text box to test with", false);
        else
        {
            var before = main.CurrentPage;
            box.Focus();
            await Task.Delay(150);
            Press(Key.N, KeyModifiers.Control);
            await Task.Delay(300);
            Check("Ctrl+N inside a text box does not navigate", main.CurrentPage == before, shell.Router.Current.ToString());
            window.Focus();
        }

        // While an input method composes, nothing fires (Windows reports the key as ImeProcessed).
        shell.Navigate("discover");
        await Task.Delay(300);
        window.Focus();
        Press(Key.ImeProcessed, KeyModifiers.Control);
        await Task.Delay(200);
        Check("A key the input method is composing does nothing", main.CurrentPage is ContentViewModel);

        // Ctrl+Shift+Q only acts while a game runs; with none running it changes nothing.
        Press(Key.Q, KeyModifiers.Control | KeyModifiers.Shift);
        await Task.Delay(200);
        Check("Ctrl+Shift+Q with no game running does nothing", main.CurrentPage is ContentViewModel && window.IsVisible);

        // F6 walks the visible regions in order and wraps; Shift+F6 walks back.
        shell.Navigate("library");
        await Task.Delay(400);
        window.Focus();
        var visited = new List<Control?>();
        var expected = window.ActiveFocusRegions.Count;
        for (var i = 0; i < expected + 1; i++)
        {
            Press(Key.F6, KeyModifiers.None);
            await Task.Delay(150);
            visited.Add(window.FocusedRegion);
        }
        var distinct = visited.Take(expected).Where(r => r is not null).Distinct().Count();
        Check("F6 reaches each visible region once", expected >= 2 && distinct == expected,
            $"{distinct}/{expected}: {string.Join(", ", visited.Select(r => r?.Name ?? "-"))}");
        Check("F6 wraps back to the first region", visited.Count > expected && visited[expected] == visited[0]);
        Press(Key.F6, KeyModifiers.Shift);
        await Task.Delay(150);
        Check("Shift+F6 goes back one region", expected >= 2 && window.FocusedRegion == visited[expected - 1],
            window.FocusedRegion?.Name ?? "-");
        await ShotAsync("shell-f6");

        // Ctrl+Alt+Shift+T: back to the default look, with Undo; on the default look it only says so.
        var settings = main.Pages.OfType<SettingsViewModel>().Single();
        var toasts = new List<string>();
        void Seen(string text, Avalonia.Controls.Notifications.NotificationType _) => toasts.Add(text);
        Toasts.Shown += Seen;
        try
        {
            settings.SelectedThemePack = settings.ThemePacks[0];
            await Task.Delay(200);
            Press(Key.T, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
            await Task.Delay(300);
            Check("Ctrl+Alt+Shift+T on the default look says it is already default", toasts.Contains(L.T("shortcut.look.already")));
            var other = settings.ThemePacks.FirstOrDefault(p => !p.IsDefault);
            if (other is null)
                Check("A non-default theme is listed", false);
            else
            {
                settings.SelectedThemePack = other;
                await Task.Delay(300);
                Press(Key.T, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
                await Task.Delay(400);
                Check("Ctrl+Alt+Shift+T restores the default look", settings.SelectedThemePack?.IsDefault == true
                    && toasts.Contains(L.T("shortcut.look.restored")), settings.SelectedThemePack?.Id);
            }
        }
        finally
        {
            Toasts.Shown -= Seen;
            settings.SelectedThemePack = settings.ThemePacks[0];
            await Task.Delay(200);
        }
    }
}
