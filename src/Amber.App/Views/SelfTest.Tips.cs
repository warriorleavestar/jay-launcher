using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Tutorial;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=tips (official design §9.3): contextual tips wait for 800 ms without input and for nothing busy,
/// show one at a time as a bubble at their control or as the one-line bar on short windows, never take focus, show only
/// once, can mute their kind, two quick closes mute the run, and Help › "Show all tips again" brings them back.
/// No game directory, nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task TipsAsync()
    {
        var rules = TutorialService.Rules;
        Check("Tips: the tutorial section is loaded from settings", rules is not null);
        if (rules is null) return;
        TutorialService.ResetAll();
        try
        {
            // 1. A short window gets the bar; the request waits for 800 ms of quiet first.
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("library");
            await Task.Delay(600);
            TutorialService.ClearQueue();
            window.Width = 600;
            window.Height = 420;
            await Task.Delay(150);
            Check("Tips: nothing shows before the 800 ms quiet time", rules.Current is null && !window.TipBarShown);
            await Task.Delay(1500);
            Check("Tips: a narrow window brings the sidebar tip", rules.Current == Tips.NarrowWindow, rules.Current?.Id ?? "none");
            Check("Tips: a short window shows it as the one-line bar", window.CurrentTipMode == TipMode.Bar && window.TipBarShown
                && window.TipBarShownText == L.T("tip.narrow_window"), window.TipBarShownText);
            await ShotAsync("tips-bar-600");
            window.CloseTip(false);
            Check("Tips: \"Got it\" closes it and it is remembered", rules.Current is null && !window.TipBarShown
                && rules.State.Seen.Contains(Tips.NarrowWindow.Id));
            TutorialService.Request(Tips.NarrowWindow);
            await Task.Delay(1200);
            Check("Tips: a seen tip never shows again", rules.Current is null);

            // 2. A tall window points a bubble at the control, without moving focus.
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("library");
            await Task.Delay(600);
            TutorialService.ResetAll();
            var focusBefore = Dispatcher.UIThread.Invoke(() => window.FocusManager?.GetFocusedElement());
            window.Width = 700;
            window.Height = 700;
            await Task.Delay(1600);
            Check("Tips: a tall window shows a bubble next to the sidebar", window.CurrentTipMode == TipMode.Bubble && window.TipBubbleShown
                && window.TipBubbleAnchor?.Name == "NavList" && window.TipBubbleShownText == L.T("tip.narrow_window"),
                $"{window.CurrentTipMode} {window.TipBubbleAnchor?.Name}");
            Check("Tips: the bubble doesn't take focus", Dispatcher.UIThread.Invoke(() => window.FocusManager?.GetFocusedElement()) == focusBefore);
            window.CloseTip(muteKind: true);
            Check("Tips: \"Don't show tips like this\" mutes the kind", rules.IsKindMuted(TipKind.Appearance));
            TutorialService.Request(Tips.ThemeImport);
            await Task.Delay(1200);
            Check("Tips: a muted kind stays quiet", rules.Current is null);

            // 3. Nothing while something busy is open; it shows once that closes.
            TutorialService.ResetAll();
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(400);
            if (rules.Current is not null) window.CloseTip(false);
            TutorialService.ResetAll();
            main.Palette.Open();
            TutorialService.Request(Tips.FirstCrash);
            await Task.Delay(1500);
            Check("Tips: no tip while the command palette is open", rules.Current is null);
            main.Palette.Close();
            await Task.Delay(1500);
            Check("Tips: it shows after the palette closes (bar when its control isn't on screen)", rules.Current == Tips.FirstCrash
                && window.CurrentTipMode == TipMode.Bar, window.CurrentTipMode?.ToString() ?? "none");
            await ShotAsync("tips-bar-1440");

            // 4. Two tips closed within 10 seconds: no more tips this run.
            window.CloseTip(false);
            TutorialService.Request(Tips.ModsToggle);
            await Task.Delay(1500);
            Check("Tips: one at a time, the next one follows", rules.Current == Tips.ModsToggle, rules.Current?.Id ?? "none");
            window.CloseTip(false);
            Check("Tips: two quick closes mute this run", rules.MutedThisRun);
            TutorialService.Request(Tips.FirstKnock);
            await Task.Delay(1200);
            Check("Tips: nothing more this run", rules.Current is null);

            // 5. Help center › "Show all tips again".
            main.Shell.Navigate("help");
            await Task.Delay(500);
            Page<HelpCenterViewModel>().ResetTipsCommand.Execute(null);
            Check("Tips: \"Show all tips again\" forgets seen and muted tips", rules.State.Seen.Count == 0 && rules.State.MutedKinds.Count == 0 && !rules.MutedThisRun);
            PressKey(window, Key.Escape);

            // 6. The popped-out game log is a long-lived owned window, not a dialog: tips still show while it is open.
            main.Shell.Navigate("library");
            await Task.Delay(400);
            main.PopOutLog();
            await Task.Delay(400);
            TutorialService.Request(Tips.FirstCrash);
            await Task.Delay(1500);
            Check("Tips: a tip still shows while the game log is popped out", Dispatcher.UIThread.Invoke(() => window.PoppedOutLog is { IsVisible: true })
                && rules.Current == Tips.FirstCrash, rules.Current?.Id ?? "none");
            if (rules.Current is not null) window.CloseTip(false);
        }
        finally
        {
            Dispatcher.UIThread.Invoke(() => window.PoppedOutLog?.Close());
            if (rules.Current is not null) window.CloseTip(false);
            TutorialService.ResetAll();
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
