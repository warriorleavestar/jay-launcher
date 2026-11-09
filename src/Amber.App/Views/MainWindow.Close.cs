using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;

namespace Amber.App.Views;

/// <summary>
/// Close guard (official design §5.9, old-spec gap N2): closing the window while something would be cut off
/// asks first in a locked safety dialog: [Minimize] [Cancel (default)] [End all and quit].
/// </summary>
public partial class MainWindow
{
    private bool _closeConfirmed;
    private bool _closeAsking;

    /// <summary>Self-check hook: replaces the dialog with a fixed answer.</summary>
    internal Func<IReadOnlyList<string>, Task<SafetyChoice>>? CloseChooser { get; set; }

    /// <summary>The blockers shown by the last close guard (self-check).</summary>
    internal IReadOnlyList<string>? LastCloseBlockers { get; private set; }

    /// <summary>The answer to the last close guard (self-check; minimizing needs a window manager, so the state alone can't tell).</summary>
    internal SafetyChoice? LastCloseChoice { get; private set; }

    /// <summary>Returns true when the close was cancelled to ask the user first.</summary>
    private bool GuardClose(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || e.IsProgrammatic) return false;
        if (StartCloseGuard() is null) return false;
        e.Cancel = true;
        return true;
    }

    /// <summary>
    /// What a user close does: null when nothing is blocking (the caller closes), otherwise the pending question.
    /// The self-check calls this directly because a scripted <see cref="Window.Close()"/> counts as programmatic.
    /// </summary>
    internal Task? StartCloseGuard()
    {
        if (DataContext is not MainViewModel main) return null;
        var blockers = main.CloseBlockers();
        if (blockers.Count == 0) return null;
        if (_closeAsking) return Task.CompletedTask;
        _closeAsking = true;
        LastCloseBlockers = blockers;
        return AskCloseAsync(main, blockers);
    }

    private async Task AskCloseAsync(MainViewModel main, IReadOnlyList<string> blockers)
    {
        SafetyChoice choice;
        try
        {
            choice = CloseChooser is { } chooser
                ? await chooser(blockers)
                : await SafetyDialog.ChooseAsync(this, L.T("close.title"), L.T("close.body"), L.T("close.quit_all"),
                    L.T("common.cancel"), L.T("close.minimize"), blockers);
        }
        finally { _closeAsking = false; }
        LastCloseChoice = choice;
        switch (choice)
        {
            case SafetyChoice.Confirm:
                main.EndAllForExit();
                _closeConfirmed = true;
                Close();
                break;
            case SafetyChoice.Alternate:
                WindowState = WindowState.Minimized;
                break;
        }
    }
}
