using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>Where a new player starts (step 2 of the first-run wizard).</summary>
public enum FirstRunChoice { Vanilla, Fabric, Pack, Other }

/// <summary>Show the wizard, keep it hidden, or never show it because this Jay already has an account or a game.</summary>
public enum FirstRunDecision { Show, Hide, AlreadySetUp }

/// <summary>One row of "where to start": a title and why you'd pick it.</summary>
public sealed partial class FirstRunOption : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
}

/// <summary>
/// The first-run wizard (official design §6.1): a full-window layer over the shell with two steps, account and
/// "where to start", and the install progress in place. It reuses the onboarding logic of the old home card for
/// the offline name, the detected game folder and the install with Java.
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly LauncherService _service;
    private bool _waitingForLogin;

    public OnboardingViewModel Onboarding { get; }

    /// <summary>Opens the Microsoft or skin-site login on the accounts page (MainViewModel).</summary>
    public Action<int>? OpenLogin { get; set; }

    /// <summary>Opens the new-instance panel for "pick another version".</summary>
    public Action? OpenNewInstance { get; set; }

    /// <summary>Lands on the library once the wizard closes.</summary>
    public Action? ShowLibrary { get; set; }

    /// <summary>Starts an installed instance.</summary>
    public Action<string>? Launch { get; set; }

    /// <summary>Asks the window for a modpack file (null when the player cancels).</summary>
    public Func<Task<string?>>? PickPack { get; set; }

    /// <summary>Imports a modpack file and starts it.</summary>
    public Func<string, Task>? ImportPackAndPlay { get; set; }

    /// <summary>Puts focus on the name box ("Continue" with nothing to continue with).</summary>
    public event Action? FocusName;

    public FirstRunViewModel(LauncherService service, OnboardingViewModel onboarding)
    {
        _service = service;
        Onboarding = onboarding;
        Onboarding.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(OnboardingViewModel.PlayerName):
                    ContinueHint = "";
                    break;
                case nameof(OnboardingViewModel.LatestRelease):
                case nameof(OnboardingViewModel.LatestFailed):
                    OnPropertyChanged(nameof(VanillaTitle));
                    OnPropertyChanged(nameof(ShowLatestFailed));
                    RefreshOptions();
                    InstallAndPlayCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(OnboardingViewModel.Detected):
                    OnPropertyChanged(nameof(ShowDetected));
                    break;
                case nameof(OnboardingViewModel.IsInstalling):
                case nameof(OnboardingViewModel.Progress):
                case nameof(OnboardingViewModel.StageText):
                    OnPropertyChanged(nameof(ShowChoices));
                    OnPropertyChanged(nameof(GameStage));
                    OnPropertyChanged(nameof(JavaStage));
                    OnPropertyChanged(nameof(StartStage));
                    InstallAndPlayCommand.NotifyCanExecuteChanged();
                    break;
            }
        };
        _service.AccountsChanged += () => Dispatcher.UIThread.Post(OnAccountsChanged);
        L.Changed += () => Dispatcher.UIThread.Post(RefreshTexts);
        RefreshOptions();
        LanguageIndex = Math.Max(0, Array.FindIndex(LanguageCodes, c => c == L.Language));
    }

    // —— Whether to show it ——

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>
    /// A brand-new Jay: never finished the wizard, no account and no game yet. Someone upgrading with accounts or
    /// instances never sees it. Self-checks only see it in their own scope.
    /// </summary>
    public bool ShouldOpen()
    {
        // An accounts file that could not be read is someone's accounts, not a new install.
        var hasAccount = _service.Accounts.Store.Accounts.Count > 0 || _service.Accounts.Store.Unreadable;
        var decision = Decide(_service.Settings.FirstRunDone, hasAccount, _service.InstalledVersions().Count > 0,
            Environment.GetEnvironmentVariable("AMBER_SELFTEST"), Environment.GetEnvironmentVariable("AMBER_SELFTEST_SCOPE"),
            Environment.GetEnvironmentVariable("AMBER_SNAPSHOT_DIR"));
        if (decision == FirstRunDecision.AlreadySetUp) MarkDone();
        return decision == FirstRunDecision.Show;
    }

    /// <summary>Whether a Jay starting up shows the wizard (pure, unit-tested).</summary>
    internal static FirstRunDecision Decide(bool done, bool hasAccount, bool hasInstance, string? selfTest, string? scope, string? snapshotDir)
    {
        if (done) return FirstRunDecision.Hide;
        if (snapshotDir is { Length: > 0 }) return FirstRunDecision.Hide;
        if (selfTest is { Length: > 0 } && scope != "firstrun") return FirstRunDecision.Hide;
        return hasAccount || hasInstance ? FirstRunDecision.AlreadySetUp : FirstRunDecision.Show;
    }

    public void Open()
    {
        Step = HasAccount ? 2 : 1;
        Onboarding.EnsureLoaded();
        IsOpen = true;
    }

    private void MarkDone()
    {
        _service.Settings.FirstRunDone = true;
        _service.TrySaveSettings();
    }

    private void Close()
    {
        IsOpen = false;
        _waitingForLogin = false;
        MarkDone();
        ShowLibrary?.Invoke();
    }

    /// <summary>[Skip, look around first]: an empty library that explains itself (§9.4).</summary>
    [RelayCommand]
    private void Skip()
    {
        if (Onboarding.IsInstalling) Onboarding.CancelInstallCommand.Execute(null);
        Close();
    }

    // —— Language (top right, every language written in itself) ——

    private static readonly string[] LanguageCodes = L.Languages.Where(l => !L.Playful.Contains(l.Code)).Select(l => l.Code).ToArray();

    public OptionItem[] LanguageOptions { get; } = L.Languages.Where(l => !L.Playful.Contains(l.Code)).Select(l => OptionItem.Fixed(l.Name)).ToArray();

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    partial void OnLanguageIndexChanged(int value)
    {
        if (value < 0 || value >= LanguageCodes.Length || LanguageCodes[value] == L.Language) return;
        _service.Settings.Language = LanguageCodes[value];
        L.SetLanguage(LanguageCodes[value]);
        _service.TrySaveSettings();
    }

    // —— Steps ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountStep), nameof(IsStartStep), nameof(StepText), nameof(AccountDot), nameof(StartDot))]
    public partial int Step { get; set; } = 1;

    public bool IsAccountStep => Step == 1;
    public bool IsStartStep => Step == 2;
    public string AccountDot => Step == 1 ? "●" : "✓";
    public string StartDot => Step == 2 ? "●" : "○";

    /// <summary>"Step 1 of 2 · Account" (the compact header; wide windows show the step dots).</summary>
    public string StepText => L.F("firstrun.step", Step, 2, L.T(Step == 1 ? "firstrun.step.account" : "firstrun.step.start"));

    [RelayCommand]
    private void Back()
    {
        if (!Onboarding.IsInstalling) Step = 1;
    }

    // —— Step 1: account ——

    public bool HasAccount => _service.Accounts.Store.Accounts.Count > 0;

    public string AccountText => _service.Accounts.Store.Accounts.FirstOrDefault() is { } a ? L.F("firstrun.account.signed_in", a.Name) : "";

    /// <summary>Under the name box after [Continue] with nothing filled in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContinueHint))]
    public partial string ContinueHint { get; set; } = "";

    public bool HasContinueHint => ContinueHint.Length > 0;

    [RelayCommand]
    private void Microsoft()
    {
        _waitingForLogin = true;
        IsOpen = false;
        OpenLogin?.Invoke(AccountsViewModel.MicrosoftTab);
    }

    [RelayCommand]
    private void SkinSite()
    {
        _waitingForLogin = true;
        IsOpen = false;
        OpenLogin?.Invoke(AccountsViewModel.ExternalTab);
    }

    /// <summary>A login finished on the accounts page: come back at step 2.</summary>
    private void OnAccountsChanged()
    {
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(AccountText));
        if (_waitingForLogin && HasAccount)
        {
            _waitingForLogin = false;
            Step = 2;
            Onboarding.EnsureLoaded();
            IsOpen = true;
        }
    }

    /// <summary>[Continue]: an offline name if one is typed, otherwise the account already signed in.</summary>
    [RelayCommand]
    private void Continue()
    {
        if (Step == 2)
        {
            if (InstallAndPlayCommand.CanExecute(null)) InstallAndPlayCommand.Execute(null);
            return;
        }
        var name = Onboarding.PlayerName.Trim();
        if (name.Length > 0)
        {
            if (!Onboarding.CanAddOffline)
            {
                FocusName?.Invoke();
                return;
            }
            Onboarding.AddOfflineCommand.Execute(null);
        }
        else if (!HasAccount)
        {
            ContinueHint = L.T("firstrun.account.need");
            FocusName?.Invoke();
            return;
        }
        Step = 2;
        Onboarding.EnsureLoaded();
    }

    // —— Step 2: where to start ——

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Choice), nameof(InstallLabel))]
    [NotifyCanExecuteChangedFor(nameof(InstallAndPlayCommand))]
    public partial int ChoiceIndex { get; set; }

    public FirstRunChoice Choice => (FirstRunChoice)Math.Clamp(ChoiceIndex, 0, 3);

    /// <summary>The four rows, in <see cref="FirstRunChoice"/> order (arrow keys move between them).</summary>
    public FirstRunOption[] Options { get; } = [new(), new(), new(), new()];

    private void RefreshOptions()
    {
        Options[0].Title = VanillaTitle;
        Options[0].Description = L.T("firstrun.start.vanilla.hint");
        Options[1].Title = L.T("firstrun.start.fabric");
        Options[1].Description = L.T("firstrun.start.fabric.hint");
        Options[2].Title = L.T("firstrun.start.pack");
        Options[2].Description = L.T("firstrun.start.pack.hint");
        Options[3].Title = L.T("firstrun.start.other");
        Options[3].Description = L.T("firstrun.start.other.hint");
    }

    public string VanillaTitle => Onboarding.LatestRelease is { } v ? L.F("firstrun.start.vanilla", v) : L.T("firstrun.start.vanilla_loading");

    public bool ShowLatestFailed => Onboarding.LatestFailed && Onboarding.LatestRelease is null;

    public bool ShowDetected => Onboarding.Detected is not null && !Onboarding.IsInstalling;

    public bool ShowChoices => !Onboarding.IsInstalling && !_finished;

    public string InstallLabel => L.T(Choice switch
    {
        FirstRunChoice.Pack => "firstrun.start.pick_pack",
        FirstRunChoice.Other => "firstrun.start.open_new",
        _ => "firstrun.start.install_play",
    });

    private bool _finished;

    private bool CanInstallAndPlay() => !Onboarding.IsInstalling && (Choice is FirstRunChoice.Pack or FirstRunChoice.Other || Onboarding.LatestRelease is not null);

    /// <summary>[Install and play]: install in place, then start the game and land in the library.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallAndPlay))]
    private async Task InstallAndPlayAsync()
    {
        switch (Choice)
        {
            case FirstRunChoice.Other:
                IsOpen = false;
                MarkDone();
                OpenNewInstance?.Invoke();
                return;
            case FirstRunChoice.Pack:
                if (PickPack is null || await PickPack() is not { } path) return;
                Close();
                if (ImportPackAndPlay is not null) await ImportPackAndPlay(path);
                return;
        }
        if (Onboarding.LatestRelease is not { } latest) return;
        var id = await Onboarding.InstallAsync(latest, Choice == FirstRunChoice.Fabric ? LoaderKind.Fabric : null, fallbackToVanilla: true);
        if (id is null) return;
        _finished = true;
        OnPropertyChanged(nameof(ShowChoices));
        Close();
        Launch?.Invoke(id);
    }

    /// <summary>[Add to library] on "found a game on your computer".</summary>
    [RelayCommand]
    private void AddDetected()
    {
        if (Onboarding.Detected is not { } found) return;
        Onboarding.UseDirectory(found.Path);
        _service.NotifyInstalledChanged();
        if (GameDirectoryDetector.CountInstances(found.Path) > 0) Close();
    }

    // —— Progress stages ——

    /// <summary>The glyph in front of each stage: waiting ○, running ◔, done ✓.</summary>
    private static string Glyph(int state) => state switch { 2 => "✓", 1 => "◔", _ => "○" };

    public string GameStage => Glyph(!Onboarding.IsInstalling ? 0 : Onboarding.Progress < 90 ? 1 : 2);
    public string JavaStage => Glyph(!Onboarding.IsInstalling || Onboarding.Progress < 90 ? 0 : Onboarding.Progress < 100 ? 1 : 2);
    public string StartStage => Glyph(Onboarding.IsInstalling && Onboarding.Progress >= 100 ? 1 : 0);

    // —— "Learn while you wait" (paged by hand, never on a timer) ——

    private static readonly string[] Tips = ["firstrun.tip.instance", "firstrun.tip.mods", "firstrun.tip.multiplayer"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TipText), nameof(TipCounter))]
    public partial int TipIndex { get; set; }

    public string TipText => L.T(Tips[TipIndex]);
    public string TipCounter => L.F("firstrun.tip.title", TipIndex + 1, Tips.Length);

    [RelayCommand] private void NextTip() => TipIndex = (TipIndex + 1) % Tips.Length;
    [RelayCommand] private void PreviousTip() => TipIndex = (TipIndex + Tips.Length - 1) % Tips.Length;

    // —— Privacy block ——

    [ObservableProperty]
    public partial bool ShowNetworkList { get; set; }

    [RelayCommand] private void ToggleNetworkList() => ShowNetworkList = !ShowNetworkList;

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(VanillaTitle));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(TipText));
        OnPropertyChanged(nameof(TipCounter));
        OnPropertyChanged(nameof(AccountText));
        RefreshOptions();
        if (ContinueHint.Length > 0) ContinueHint = L.T("firstrun.account.need");
        var index = Array.FindIndex(LanguageCodes, c => c == L.Language);
        if (index >= 0 && index != LanguageIndex) LanguageIndex = index;
    }
}
