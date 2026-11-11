using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Tutorial;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>Shows contextual tips (§9.3): a bubble next to what it explains, or the one-line bar on short windows.</summary>
public partial class MainWindow
{
    /// <summary>Which control each tip points at; a tip whose control isn't on screen uses the bar.</summary>
    private static readonly Dictionary<string, string> TipAnchors = new()
    {
        [Tips.ModsToggle.Id] = "ModSearchBox",
        [Tips.FirstCrash.Id] = "LastRunPanel",
        [Tips.FirstKnock.Id] = "KnockList",
        [Tips.NarrowWindow.Id] = "NavList",
    };

    private DateTimeOffset _lastInput = DateTimeOffset.UtcNow;
    private DispatcherTimer? _tipTimer;

    /// <summary>How the current tip is shown (for the self-check).</summary>
    internal TipMode? CurrentTipMode { get; private set; }

    private void AttachTips()
    {
        if (_settings is { } settings) TutorialService.Attach(settings.Tutorial, settings.Save);
        void Touch(object? _, RoutedEventArgs __) => _lastInput = DateTimeOffset.UtcNow;
        AddHandler(KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tipTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => TipTick());
        Opened += (_, _) => _tipTimer.Start();
        Closed += (_, _) => _tipTimer.Stop();
    }

    /// <summary>§9.3 rule 1: no tip while a game is starting, someone is knocking (except the knock tip), or a dialog or overlay is open.</summary>
    private bool TipBusy(Tip tip)
    {
        // A dialog is an owned window; while one is open the main window waits.
        if (OwnedWindows.Any(w => w.IsVisible) || DataContext is not MainViewModel main) return true;
        if (main.Palette.IsOpen || main.Help.IsOpen || main.FirstRun.IsOpen) return true;
        if (main.Pages.OfType<HomeViewModel>().Any(h => h.IsBusy)) return true;
        return tip != Tips.FirstKnock && main.Multiplayer.HasKnocks;
    }

    private void TipTick()
    {
        if (TutorialService.Suspended || TutorialService.Rules is not { } rules || rules.Current is not null || TutorialService.Next() is not { } next) return;
        var sinceInput = DateTimeOffset.UtcNow - (next.At > _lastInput ? next.At : _lastInput);
        if (!rules.CanShow(next.Tip, sinceInput, TipBusy(next.Tip))) return;
        ShowTip(next.Tip);
    }

    private void ShowTip(Tip tip)
    {
        var anchor = TipAnchors.TryGetValue(tip.Id, out var name)
            ? this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible && c.Bounds.Width > 0)
            : null;
        var mode = TipRules.ModeFor(Bounds.Height, anchor is not null);
        TutorialService.Showing(tip);
        CurrentTipMode = mode;
        var text = L.T(tip.TextKey);
        if (mode == TipMode.Bar)
        {
            TipBarText.Text = text;
            TipBar.IsVisible = true;
        }
        else
        {
            TipBubbleText.Text = text;
            TipPopup.PlacementTarget = anchor;
            TipPopup.IsOpen = true;
            // The bubble belongs to its page: leaving the page takes it away (not counted as closing it).
            void Gone(object? s, Avalonia.VisualTreeAttachmentEventArgs e)
            {
                anchor!.DetachedFromVisualTree -= Gone;
                if (!TipPopup.IsOpen) return;
                TipPopup.IsOpen = false;
                CurrentTipMode = null;
                TutorialService.Hidden();
            }
            anchor!.DetachedFromVisualTree += Gone;
        }
    }

    /// <summary>Closes the tip on screen ("Got it", or "Don't show tips like this" when <paramref name="muteKind"/>).</summary>
    internal void CloseTip(bool muteKind)
    {
        if (TutorialService.Rules?.Current is null) return;
        TipBar.IsVisible = false;
        TipPopup.IsOpen = false;
        CurrentTipMode = null;
        TutorialService.Closed(muteKind);
    }

    private void TipOk_Click(object? sender, RoutedEventArgs e) => CloseTip(false);
    private void TipMute_Click(object? sender, RoutedEventArgs e) => CloseTip(true);

    internal bool TipBarShown => TipBar.IsVisible;
    internal string TipBarShownText => TipBarText.Text ?? "";
    internal bool TipBubbleShown => TipPopup.IsOpen;
    internal string TipBubbleShownText => TipBubbleText.Text ?? "";
    internal Control? TipBubbleAnchor => TipPopup.PlacementTarget;
}
