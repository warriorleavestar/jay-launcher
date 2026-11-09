using Amber.Core.Localization;
using Amber.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;

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
        _service.Settings.Save();
        foreach (var name in (string[])[nameof(ChildMode), nameof(ChildNoRemote), nameof(ChildNoPublic), nameof(ChildNoExit), nameof(ChildFriendsFiles),
                     nameof(ChildAlwaysAsk), nameof(ChildQuietNotify), nameof(ChildModeStatusText)])
            OnPropertyChanged(name);
        ChildModeChanged?.Invoke();
    }

    /// <summary>Refresh the statuses whenever settings is shown.</summary>
    internal void RefreshPrivacy()
    {
        NetworkList.Refresh();
        OnPropertyChanged(nameof(ChildModeStatusText));
    }
}
