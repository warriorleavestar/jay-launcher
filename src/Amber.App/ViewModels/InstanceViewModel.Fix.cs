using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// [Help me fix…] (official design §6.3, J6): when no known cause was found, turn half of the mods off, play, say whether
/// it still crashes, and repeat until one mod is left. The session lives in the instance (Core <see cref="CrashBisect"/>),
/// so the card comes back after Jay restarts; [Restore everything] is always there.
/// </summary>
public sealed partial class InstanceViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBisect), nameof(BisectTitle), nameof(BisectBody), nameof(BisectAwaiting), nameof(BisectFound), nameof(BisectFoundText),
        nameof(BisectDisableFoundLabel))]
    public partial CrashBisect? Bisect { get; private set; }

    public bool HasBisect => Bisect is not null;
    public bool BisectAwaiting => Bisect?.AwaitingAnswer == true;
    public bool BisectFound => Bisect?.Found is not null;

    public string BisectTitle => Bisect is not { } b ? "" : b.Found is not null
        ? L.T("fix.bisect.found_title")
        : L.F("fix.bisect.title", Math.Max(1, b.Round), b.RoundsLeft);

    public string BisectBody => Bisect is not { } b ? "" : b.Found is not null
        ? L.T("fix.bisect.found_body")
        : L.P("fix.bisect.body", b.DisabledNow.Count, b.DisabledNow.Count, b.Candidates.Count);

    public string BisectFoundText => Bisect?.Found is { } name ? L.F("fix.bisect.found", name) : "";
    public string BisectDisableFoundLabel => Bisect?.Found is { } name ? L.F("fix.bisect.disable_found", name) : "";

    /// <summary>Reads the saved session for the shown instance (after a restart the card is back).</summary>
    private void LoadBisect() => Bisect = Current is { } c ? CrashBisect.Load(c.WorkDir) : null;

    private void RefreshBisect()
    {
        // The session object changes in place: raise every dependent property again.
        var b = Bisect;
        Bisect = null;
        Bisect = b;
        _loadedTabs.Remove((int)InstanceTab.Mods);
        if (TabIndex == (int)InstanceTab.Mods) LoadTab(TabIndex);
    }

    private bool BlockedByRunningGame()
    {
        if (Current is not { } c || !_service.IsRunningIn(c.WorkDir)) return false;
        SetStatus(StatusMessage.Warning("fix.bisect.running"));
        return true;
    }

    /// <summary>[Help me fix…]: starts halving the mods and turns the first half off.</summary>
    [RelayCommand]
    private void StartFix()
    {
        if (Current is not { } c || BlockedByRunningGame()) return;
        try
        {
            var bisect = CrashBisect.Load(c.WorkDir) ?? CrashBisect.Start(c.WorkDir);
            if (bisect.Candidates.Count < 2)
            {
                bisect.End();
                SetStatus(StatusMessage.Info("fix.bisect.too_few"));
                return;
            }
            bisect.NextRound();
            Bisect = bisect;
            RefreshBisect();
            SetStatus(StatusMessage.Info("fix.bisect.started", bisect.DisabledNow.Count) with { Count = bisect.DisabledNow.Count });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("fix.bisect.failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>[Still crashes] / [Works now] after playing with the current half off; then the next half goes off.</summary>
    [RelayCommand]
    private void BisectAnswer(string? stillCrashes)
    {
        if (Bisect is not { } b || BlockedByRunningGame()) return;
        try
        {
            b.Answer(stillCrashes == "true");
            if (b.Found is null) b.NextRound();
            RefreshBisect();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("fix.bisect.failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>[Turn off X]: found it. X goes off, everything else is as before, the session ends.</summary>
    [RelayCommand]
    private void BisectDisableFound()
    {
        if (Current is not { } c || Bisect is not { Found: { } name } b || BlockedByRunningGame()) return;
        try
        {
            var path = Path.Combine(c.ModsDir, name);
            if (File.Exists(path)) File.Move(path, path + ".disabled");
            b.RestoreAll();
            Bisect = null;
            _loadedTabs.Remove((int)InstanceTab.Mods);
            SetStatus(StatusMessage.Success("fix.bisect.disabled", name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("fix.bisect.failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>[Restore everything]: every mod the session switched off goes back on.</summary>
    [RelayCommand]
    private void BisectRestore()
    {
        if (Bisect is not { } b || BlockedByRunningGame()) return;
        b.RestoreAll();
        Bisect = null;
        _loadedTabs.Remove((int)InstanceTab.Mods);
        if (TabIndex == (int)InstanceTab.Mods) LoadTab(TabIndex);
        SetStatus(StatusMessage.Success("fix.bisect.restored"));
    }
}
