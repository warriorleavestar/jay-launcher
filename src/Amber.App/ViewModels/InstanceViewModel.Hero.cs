using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Diagnostics;
using Amber.Core.GameLibrary;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One fix button on the "last session" panel. Done marks it with a check after it ran.</summary>
public sealed partial class LastRunFixButton(CrashFix fix, string label) : ObservableObject
{
    public CrashFix Fix { get; } = fix;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool Done { get; set; }
}

/// <summary>One cause on the "last session" panel, with its fixes right next to it.</summary>
public sealed class LastRunCause(CrashFinding finding, IReadOnlyList<LastRunFixButton> fixes)
{
    public string Title => finding.Title;
    public string Advice => finding.Advice;
    public bool IsLikely => finding.Severity == CrashSeverity.Likely;
    public string SeverityText => L.T(IsLikely ? "instance.lastrun.likely" : "instance.lastrun.possible");
    public string? Evidence => finding.Evidence;
    public bool HasEvidence => !string.IsNullOrWhiteSpace(finding.Evidence);
    public IReadOnlyList<LastRunFixButton> Fixes { get; } = fixes;
}

/// <summary>
/// The instance page's hero area and "last session" panel (official design §6.3): cover, name, facts, the start
/// button state machine with a separate cancel or quit button, and crash causes with in-place fixes.
/// </summary>
public sealed partial class InstanceViewModel
{
    private HomeViewModel? _home;
    private readonly DispatcherTimer _heroClock = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _dismissedCrash = -1;

    /// <summary>The launch engine. Set once by the main window; tests that only use the tabs leave it null.</summary>
    public HomeViewModel? Home => _home;

    public void AttachHome(HomeViewModel home)
    {
        _home = home;
        home.RunStateChanged += UpdateHero;
        home.SessionStarted += session => session.Changed += _ => Dispatcher.UIThread.Post(UpdateHero);
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.SelectedAccount) or nameof(HomeViewModel.ShowForceKill)) UpdateHero();
            else if (e.PropertyName is nameof(HomeViewModel.Crash) or nameof(HomeViewModel.CrashInstance)) UpdateLastRun();
        };
        _service.LibraryChanged += () => Dispatcher.UIThread.Post(UpdateHero);
        _service.InstanceStateChanged += id => Dispatcher.UIThread.Post(() => { if (Current?.Id == id) UpdateHero(); });
        _heroClock.Tick += (_, _) => UpdateHero();
        L.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateHero();
            UpdateLastRun();
            if (Current is not null) InvalidateTab(InstanceTab.Overview);
        });
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Current)) { UpdateHero(); UpdateLastRun(); }
        };
        UpdateHero();
        UpdateLastRun();
    }

    // ---- Hero ----

    [ObservableProperty] public partial string HeroName { get; set; } = "";
    [ObservableProperty] public partial string HeroFacts { get; set; } = "";
    [ObservableProperty] public partial string HeroLoader { get; set; } = "";
    [ObservableProperty] public partial string HeroLoaderClass { get; set; } = "vanilla";
    [ObservableProperty] public partial string HeroVersion { get; set; } = "";
    [ObservableProperty] public partial int CoverSlot { get; set; }
    [ObservableProperty] public partial string CoverInitial { get; set; } = "";
    [ObservableProperty] public partial bool IsPinned { get; set; }
    [ObservableProperty] public partial string PinTip { get; set; } = "";

    [ObservableProperty] public partial StartPlan? Start { get; set; }
    [ObservableProperty] public partial string StartLabel { get; set; } = "";
    [ObservableProperty] public partial bool StartAmber { get; set; } = true;
    [ObservableProperty] public partial bool StartBusy { get; set; }
    [ObservableProperty] public partial bool StartPlaying { get; set; }
    [ObservableProperty] public partial bool ShowPlayIcon { get; set; } = true;
    [ObservableProperty] public partial string SideLabel { get; set; } = "";
    [ObservableProperty] public partial bool HasSide { get; set; }
    [ObservableProperty] public partial bool SideIsForceKill { get; set; }
    [ObservableProperty] public partial bool ShowSideButton { get; set; }
    /// <summary>Blue primary button (sign in first): the amber colour is kept for "this starts the game".</summary>
    [ObservableProperty] public partial bool StartPrimary { get; set; }
    [ObservableProperty] public partial string HeroNote { get; set; } = "";
    [ObservableProperty] public partial bool HasHeroNote { get; set; }
    [ObservableProperty] public partial string AccountText { get; set; } = "";

    internal void UpdateHero()
    {
        if (Current is not { } c)
        {
            HeroName = HeroFacts = "";
            Start = null;
            _heroClock.Stop();
            return;
        }
        var meta = _service.MetaOf(c.Id);
        HeroName = string.IsNullOrWhiteSpace(meta?.DisplayName) ? c.Id : meta!.DisplayName!;
        HeroVersion = c.McVersion;
        HeroLoaderClass = c.Loader ?? "vanilla";
        HeroLoader = c.Loader is null ? "" : string.IsNullOrWhiteSpace(c.LoaderVersion)
            ? LibraryViewModel.LoaderName(c.Loader)
            : LibraryViewModel.LoaderName(c.Loader) + " " + c.LoaderVersion;
        CoverSlot = CoverArt.SlotFor(c.Id);
        CoverInitial = CoverArt.InitialOf(HeroName);
        IsPinned = meta?.Pinned == true;
        PinTip = L.T(IsPinned ? "library.unpin" : "library.pin");

        var facts = new List<string>();
        if (c.Loader is not null && _heroModCount is { } mods && mods.For == c.Id) facts.Add(L.P("library.mods", mods.Count, mods.Count));
        facts.Add(meta?.LastPlayed is { } played ? L.F("library.last_played", Fmt.Ago(played, DateTimeOffset.Now)) : L.T("library.never_played"));
        if (meta?.PlayMinutes is >= 1 and var minutes) facts.Add(L.F("library.play_time", Fmt.PlayTime(TimeSpan.FromMinutes(minutes))));
        HeroFacts = string.Join(" · ", facts);
        if (_heroModCount?.For != c.Id && c.Loader is not null) CountModsForHero(c);

        var session = _home?.SessionOf(c.Id);
        var plan = StartButton.For(session, _home?.SelectedAccount is not null, _service.Directory.StateOf(c.Id), _home?.ForceKillOfferedFor(c.Id) == true);
        Start = plan;
        StartLabel = plan.Kind == StartKind.Playing && session is not null
            ? L.F("instance.hero.playing", Math.Max(0, (int)session.PlayTime(DateTimeOffset.Now).TotalMinutes))
            : L.T(plan.LabelKey);
        StartAmber = plan.Amber;
        StartBusy = plan.Busy;
        StartPlaying = plan.Kind == StartKind.Playing;
        ShowPlayIcon = !plan.Busy && !StartPlaying;
        HasSide = plan.Side != StartSide.None;
        SideIsForceKill = plan.Side == StartSide.ForceKill;
        ShowSideButton = HasSide && !SideIsForceKill;
        StartPrimary = plan.Kind == StartKind.NeedAccount;
        SideLabel = HasSide ? L.T(plan.SideKey) : "";
        HeroNote = plan.NoteKey is { } note ? L.T(note) : "";
        HasHeroNote = HeroNote.Length > 0;
        AccountText = _home?.SelectedAccount is { } account ? L.F("instance.hero.as_account", account.Name) : "";
        UpdateGameRunning();
        if (session?.IsActive == true) _heroClock.Start();
        else _heroClock.Stop();
    }

    private (string For, int Count)? _heroModCount;

    private void CountModsForHero(GameInstance c)
    {
        _heroModCount = (c.Id, 0);
        _ = Task.Run(() => new ModManager(c.ModsDir).List().Count).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                _heroModCount = (c.Id, t.Result);
                if (Current?.Id == c.Id) UpdateHero();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The start button. It is never disabled: when something is missing it goes to get it, and while a game is
    /// starting or running it does nothing (stop is the separate button next to it).
    /// </summary>
    [RelayCommand]
    private void StartGame()
    {
        if (Current is not { } c || Start is not { } plan) return;
        switch (plan.Kind)
        {
            case StartKind.NeedAccount:
                _home?.NavigateToAccounts?.Invoke();
                break;
            case StartKind.Ready or StartKind.RetryAfterCrash or StartKind.ResumeInstall or StartKind.Repair:
                _service.SetCurrentInstance(c.Id);
                _service.RequestLaunch(new LaunchRequest(c.Id));
                break;
        }
    }

    /// <summary>The button next to start: cancel, quit the game, finish installing only, or open the folder.</summary>
    [RelayCommand]
    private void SideAction()
    {
        if (Current is not { } c || Start is not { } plan) return;
        switch (plan.Side)
        {
            case StartSide.Cancel or StartSide.QuitGame:
                _home?.StopRun(c.Id);
                break;
            case StartSide.ForceKill:
                _home?.ForceKillCommand.Execute(null);
                break;
            case StartSide.InstallOnly:
                CompleteFilesCommand.Execute(null);
                break;
            case StartSide.OpenFolder:
                OpenFolderCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Current is not { } c) return;
        _service.SetPinned(c.Id, !IsPinned);
    }

    // ---- Last session panel ----

    public ObservableCollection<LastRunCause> LastRunCauses { get; } = [];

    [ObservableProperty] public partial bool ShowLastRun { get; set; }
    [ObservableProperty] public partial string LastRunTitle { get; set; } = "";
    [ObservableProperty] public partial bool LastRunUnknown { get; set; }
    [ObservableProperty] public partial bool HasLikelyCause { get; set; }

    private int _crashSerial;
    private object? _shownCrash;

    private void UpdateLastRun()
    {
        var crash = _home?.Crash;
        if (!ReferenceEquals(crash, _shownCrash))
        {
            _shownCrash = crash;
            _crashSerial++;
        }
        var show = crash is not null && Current is { } c && string.Equals(_home!.CrashInstance, c.Id, StringComparison.OrdinalIgnoreCase)
                   && _dismissedCrash != _crashSerial;
        ShowLastRun = show;
        LastRunCauses.Clear();
        if (!show || crash is null) return;
        var when = _home!.CrashTime is { } t ? Fmt.Ago(t, DateTimeOffset.Now) : "";
        LastRunTitle = L.F("instance.lastrun.title", when);
        // Joined a room with other mods than the host's: that comes first.
        if (Current is { } instance && _service.MetaOf(instance.Id)?.ModsDifferFromHost is { } host)
            LastRunCauses.Add(new LastRunCause(new CrashFinding(L.F("instance.lastrun.mods_differ", host), L.T("instance.lastrun.mods_differ_advice")), []));
        foreach (var finding in crash.Findings)
        {
            var fixes = CrashFixes.For(finding, crash.SuspectMods)
                .Select(f => new LastRunFixButton(f, f.Argument is { } arg ? L.F(f.LabelKey, CrashFixes.SplitSuspect(arg).Name) : L.T(f.LabelKey)))
                .ToList();
            LastRunCauses.Add(new LastRunCause(finding, fixes));
        }
        LastRunUnknown = LastRunCauses.Count == 0;
        HasLikelyCause = LastRunCauses.Any(c => c.IsLikely);
    }

    [RelayCommand]
    private void DismissLastRun()
    {
        _dismissedCrash = _crashSerial;
        UpdateLastRun();
    }

    [RelayCommand]
    private void ViewLastRunLog() => _home?.ViewLogCommand.Execute(null);

    [RelayCommand]
    private void OpenLastRunReport() => _home?.OpenLatestCrashFileCommand.Execute(null);

    internal void ExportLastRun(string path) => _home?.ExportCrashBundle(path);

    /// <summary>The last fix that ran (self-check).</summary>
    internal CrashFixKind? LastFixApplied { get; private set; }

    /// <summary>Runs a fix in place. Changes that can be undone get a toast with an undo button.</summary>
    [RelayCommand]
    private async Task ApplyFixAsync(LastRunFixButton? button)
    {
        if (button is null || Current is not { } c) return;
        LastFixApplied = button.Fix.Kind;
        switch (button.Fix.Kind)
        {
            case CrashFixKind.RaiseMemory:
                if (RaiseMemory(c.Id) is not { } change) return;
                Toasts.WithAction(change.Text, L.T("common.undo"), change.Undo);
                break;
            case CrashFixKind.ChooseJava:
                TabIndex = (int)InstanceTab.Settings;
                return;
            case CrashFixKind.OpenMods:
                TabIndex = (int)InstanceTab.Mods;
                return;
            case CrashFixKind.DisableMod:
                if (!await DisableModAsync(c, button.Fix.Argument ?? "")) return;
                break;
            case CrashFixKind.RepairFiles:
                CompleteFilesCommand.Execute(null);
                break;
            case CrashFixKind.ShadersOff:
                bool off;
                try { off = ShaderToggle.TurnOff(c.WorkDir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SetStatus(StatusMessage.Failure("instance.lastrun.fix_failed", e, ErrorText.Of(e)));
                    return;
                }
                Toasts.Show(L.T(off ? "instance.lastrun.shaders_off_done" : "instance.lastrun.shaders_off_none"), off ? NotificationType.Success : NotificationType.Information);
                if (!off) return;
                break;
            default:
                ViewLastRunLog();
                return;
        }
        button.Done = true;
    }

    /// <summary>Turns automatic memory on for this instance, or raises its maximum. Returns the toast text, or null if nothing changed.</summary>
    private (string Text, Action Undo)? RaiseMemory(string id)
    {
        var effective = _service.EffectiveSettings(id);
        var own = InstanceSettings.Load(_service.Directory, id);
        var before = (own.AutoMemory, own.MaxMemoryMb);
        string text;
        if (effective.AutoMemory != true)
        {
            var current = effective.MaxMemoryMb ?? 4096;
            var raised = MemoryPlanner.RaisedMemoryMb(current, MemoryPlanner.TotalPhysicalMb());
            if (raised == current)
            {
                Toasts.Show(L.T("instance.lastrun.memory_at_max"));
                return null;
            }
            own.AutoMemory = false;
            own.MaxMemoryMb = raised;
            text = L.F("instance.lastrun.memory_done", raised);
        }
        else
        {
            var auto = MemoryPlanner.AutoMemoryMb(new ModManager(Current!.ModsDir).CountEnabled(), MemoryPlanner.FreePhysicalMb());
            var raised = MemoryPlanner.RaisedMemoryMb(auto, MemoryPlanner.TotalPhysicalMb());
            if (raised == auto)
            {
                Toasts.Show(L.T("instance.lastrun.memory_at_max"));
                return null;
            }
            own.AutoMemory = false;
            own.MaxMemoryMb = raised;
            text = L.F("instance.lastrun.memory_done", raised);
        }
        own.Save(_service.Directory, id);
        InvalidateTab(InstanceTab.Settings);
        InvalidateTab(InstanceTab.Overview);
        return (text, () =>
        {
            var s = InstanceSettings.Load(_service.Directory, id);
            (s.AutoMemory, s.MaxMemoryMb) = before;
            s.Save(_service.Directory, id);
            InvalidateTab(InstanceTab.Settings);
            InvalidateTab(InstanceTab.Overview);
        });
    }

    private async Task<bool> DisableModAsync(GameInstance c, string name)
    {
        var list = await Task.Run(() => new ModManager(c.ModsDir).List());
        var (display, modId) = CrashFixes.SplitSuspect(name);
        var mod = list.FirstOrDefault(m => m.Enabled && modId is not null && Matches(m, modId)) ?? list.FirstOrDefault(m => m.Enabled && Matches(m, display));
        name = display;
        if (mod is null)
        {
            Toasts.Show(L.F("instance.lastrun.mod_not_found", name));
            TabIndex = (int)InstanceTab.Mods;
            return false;
        }
        string disabledPath;
        try { disabledPath = ModManager.SetEnabled(mod, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusMessage.Failure("instance.lastrun.fix_failed", e, ErrorText.Of(e)));
            return false;
        }
        InvalidateTab(InstanceTab.Mods);
        _heroModCount = null;
        Toasts.WithAction(L.F("instance.lastrun.mod_disabled", mod.Name), L.T("common.undo"), () =>
        {
            try { ModManager.SetEnabled(mod with { Path = disabledPath, FileName = Path.GetFileName(disabledPath), Enabled = false }, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toasts.Show(L.F("instance.lastrun.fix_failed", ErrorText.Of(e)), NotificationType.Error); }
            InvalidateTab(InstanceTab.Mods);
        });
        return true;
    }

    /// <summary>Forget a tab's loaded state; reload it now if it is the one showing.</summary>
    private void InvalidateTab(InstanceTab tab)
    {
        _loadedTabs.Remove((int)tab);
        if (TabIndex == (int)tab) LoadTab(TabIndex);
    }

    /// <summary>Crash reports name a mod by its id or display name; match either, ignoring case.</summary>
    internal static bool Matches(LocalMod mod, string name) =>
        !string.IsNullOrWhiteSpace(name) && (string.Equals(mod.ModId, name, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase)
                                             || mod.FileName.StartsWith(name, StringComparison.OrdinalIgnoreCase));
}
