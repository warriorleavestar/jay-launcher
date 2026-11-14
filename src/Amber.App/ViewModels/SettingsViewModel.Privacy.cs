using Amber.Core.Localization;
using Amber.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Settings › Privacy and safety: the same network list as "About and privacy" (§8.8) and "For a child" (§9.6) with its
/// main switch, six items and the time of the last change.
/// </summary>
public sealed partial class SettingsViewModel
{
    private NetworkListModel? _networkList;

    /// <summary>The network list with its status.</summary>
    public NetworkListModel NetworkList => _networkList ??= new NetworkListModel(_service);

    /// <summary>Called after child mode changed (multiplayer hides what it forbids; About refreshes).</summary>
    public Action? ChildModeChanged { get; set; }

    private ChildModeSettings Child => _service.Settings.ChildMode ??= new ChildModeSettings();

    public bool ChildMode
    {
        get => Child.Enabled;
        set => ChangeChild(() => Child.SetEnabled(value, DateTimeOffset.Now));
    }

    public bool ChildNoRemote { get => Child.NoRemoteControl; set => SetItem(ChildModeItem.NoRemoteControl, value); }
    public bool ChildNoPublic { get => Child.NoPublicNetworks; set => SetItem(ChildModeItem.NoPublicNetworks, value); }
    public bool ChildNoExit { get => Child.NoNetworkExit; set => SetItem(ChildModeItem.NoNetworkExit, value); }
    public bool ChildFriendsFiles { get => Child.FriendsFilesOnly; set => SetItem(ChildModeItem.FriendsFilesOnly, value); }
    public bool ChildAlwaysAsk { get => Child.AlwaysAskKnock; set => SetItem(ChildModeItem.AlwaysAskKnock, value); }
    public bool ChildQuietNotify { get => Child.QuietNotifications; set => SetItem(ChildModeItem.QuietNotifications, value); }

    /// <summary>"For a child: on · last changed 2 March 20:14".</summary>
    public string ChildModeStatusText => ChildModeStatus.Text(Child);

    private void SetItem(ChildModeItem item, bool value) => ChangeChild(() => Child.Set(item, value, DateTimeOffset.Now));

    private void ChangeChild(Action change)
    {
        var before = Child.ChangedAt;
        change();
        if (Child.ChangedAt == before) return;
        _service.TrySaveSettings();
        foreach (var name in (string[])[nameof(ChildMode), nameof(ChildNoRemote), nameof(ChildNoPublic), nameof(ChildNoExit), nameof(ChildFriendsFiles),
                     nameof(ChildAlwaysAsk), nameof(ChildQuietNotify), nameof(ChildModeStatusText)])
            OnPropertyChanged(name);
        NotifyCalls();
        ChildModeChanged?.Invoke();
    }

    // ---- Game sandbox (privacy plan item 15)

    /// <summary>Strong sandbox mode (Windows only): AppContainer, on by default.</summary>
    public bool StrongSandbox
    {
        get => _service.Settings.StrongSandbox;
        set
        {
            if (_service.Settings.StrongSandbox == value) return;
            _service.Settings.StrongSandbox = value;
            _service.TrySaveSettings();
            SandboxChanged();
        }
    }

    /// <summary>"Launch without sandbox (not recommended)": off by default; the multiplayer page says so while it is on.</summary>
    public bool LaunchWithoutSandbox
    {
        get => _service.Settings.LaunchWithoutSandbox;
        set
        {
            if (_service.Settings.LaunchWithoutSandbox == value) return;
            _service.Settings.LaunchWithoutSandbox = value;
            _service.TrySaveSettings();
            SandboxChanged();
        }
    }

    /// <summary>Strong mode and its Java note exist only on Windows.</summary>
    public bool IsWindowsSandbox => OperatingSystem.IsWindows();

    /// <summary>Which sandbox the next launch uses and, after a fallback, why.</summary>
    public string SandboxStatusText => _service.SandboxStatusText;

    /// <summary>"Retry strong mode" shows after strong mode fell back (declined, no admin, failed).</summary>
    public bool CanRetryStrongSandbox => OperatingSystem.IsWindows() && !LaunchWithoutSandbox && _service.Settings.StrongSandboxFallback is not null;

    [RelayCommand]
    private void RetryStrongSandbox()
    {
        _service.RetryStrongSandbox();
        SandboxChanged();
    }

    /// <summary>"Remove sandbox permissions" shows while Jay's admin step left something behind.</summary>
    public bool CanRemoveSandboxPermissions => _service.HasSandboxPermissions;

    /// <summary>Why removing the sandbox permissions did not work (null when it did or was not tried).</summary>
    [ObservableProperty]
    private string? _sandboxPermissionsError;

    [RelayCommand]
    private async Task RemoveSandboxPermissions()
    {
        SandboxPermissionsError = await _service.RemoveSandboxPermissionsAsync() is { } error
            ? L.F("settings.sandbox.remove_failed", error)
            : null;
        SandboxChanged();
    }

    private void SandboxChanged()
    {
        foreach (var name in (string[])[nameof(StrongSandbox), nameof(LaunchWithoutSandbox), nameof(SandboxStatusText), nameof(CanRetryStrongSandbox), nameof(CanRemoveSandboxPermissions)])
            OnPropertyChanged(name);
        _service.NotifySandboxChanged();
    }

    /// <summary>Refresh the statuses whenever settings is shown.</summary>
    internal void RefreshPrivacy()
    {
        NetworkList.Refresh();
        OnPropertyChanged(nameof(ChildModeStatusText));
        foreach (var name in (string[])[nameof(StrongSandbox), nameof(LaunchWithoutSandbox), nameof(SandboxStatusText), nameof(CanRetryStrongSandbox), nameof(CanRemoveSandboxPermissions)])
            OnPropertyChanged(name);
    }
}
