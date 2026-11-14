using System.ComponentModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Settings › Multiplayer as the multiplayer advanced settings panel (redesign batch 10): groups that open one at a
/// time, a "Customised" tag on every group someone changed, and [Reset all to defaults] behind a confirmation.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>The groups with a value that is not the default (<see cref="AdvancedSettings.Groups"/> ids).</summary>
    public IReadOnlyList<string> AdvancedCustomized { get; private set; } = [];

    public bool ProtectionCustomized => AdvancedCustomized.Contains(AdvancedSettings.Protection);
    public bool ConnectionCustomized => AdvancedCustomized.Contains(AdvancedSettings.Connection);
    public bool InvitesCustomized => AdvancedCustomized.Contains(AdvancedSettings.Invites);
    public bool RoomCustomized => AdvancedCustomized.Contains(AdvancedSettings.Room);
    public bool AnyAdvancedCustomized => AdvancedCustomized.Count > 0;

    /// <summary>[Reset all to defaults] was pressed once: the confirmation line shows, focus on [Cancel].</summary>
    [ObservableProperty] public partial bool ConfirmingAdvancedReset { get; set; }

    /// <summary>What the last reset did ("back to defaults", or that the open room keeps its settings).</summary>
    [ObservableProperty] public partial string AdvancedResetStatus { get; set; } = "";

    private static readonly HashSet<string> AdvancedInputs =
    [
        nameof(MultiplayerViewModel.SelectedTransport), nameof(MultiplayerViewModel.DisguiseAsMinecraft),
        nameof(MultiplayerViewModel.FullLanMode), nameof(MultiplayerViewModel.AllowExitNode), nameof(MultiplayerViewModel.AllowGameProxy),
        nameof(MultiplayerViewModel.DefaultRelayOnly), nameof(MultiplayerViewModel.AutoCopyInvite), nameof(MultiplayerViewModel.SelectedDefaultKnockWait),
        nameof(MultiplayerViewModel.RequestPopup), nameof(MultiplayerViewModel.AllowModSync),
    ];

    private void OnMultiplayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && AdvancedInputs.Contains(name)) RefreshAdvanced();
    }

    /// <summary>What the panel shows now, as plain values.</summary>
    internal AdvancedValues CurrentAdvanced()
    {
        var s = _service.Settings;
        var m = Multiplayer;
        return new AdvancedValues(
            Transport: m?.SelectedTransport.Mode ?? AdvancedSettings.Defaults.Transport,
            Disguise: m is { CanDisguise: true, DisguiseAsMinecraft: true },
            Attestation: AttestationKeys[Math.Clamp(AttestationIndex, 0, AttestationKeys.Length - 1)],
            Breaker: SecurityBreaker,
            BreakerDelay: int.TryParse(BreakerRestartDelay.Trim(), out var delay) ? delay : s.BreakerRestartDelay,
            BreakerSkip: BreakerSkipDelay,
            RelayOnly: s.MultiplayerRelayOnly,
            FullLan: m?.FullLanMode ?? false,
            AutoCopyInvite: s.MultiplayerAutoCopyInvite,
            KnockWait: s.MultiplayerKnockWait,
            RequestPopup: s.MultiplayerRequestPopup,
            ModSync: s.MultiplayerAllowModSync,
            GameProxy: m?.AllowGameProxy ?? false,
            NetworkExit: m?.AllowExitNode ?? false,
            CallsWho: s.CallsWhoCanCall,
            CallsNoise: s.CallsNoiseSuppression,
            CallsPushToTalk: s.CallsPushToTalk,
            CallsQuality: s.CallsVideoQuality,
            CallsShareGameOnly: s.CallsShareGameOnly,
            CallsAllowControl: s.CallsAllowControlRequests,
            FilesCustomFolder: s.FilesSaveFolder.Length > 0,
            FilesAutoAccept: s.FilesAutoAcceptFriends,
            FilesQueue: s.FilesQueueOffline,
            FilesSpeed: s.FilesSpeed,
            FilesNotify: s.FilesNotifyDone);
    }

    private void RefreshAdvanced()
    {
        var now = AdvancedSettings.Customized(CurrentAdvanced());
        if (now.SequenceEqual(AdvancedCustomized)) return;
        AdvancedCustomized = now;
        OnPropertyChanged(nameof(AdvancedCustomized));
        OnPropertyChanged(nameof(ProtectionCustomized));
        OnPropertyChanged(nameof(ConnectionCustomized));
        OnPropertyChanged(nameof(InvitesCustomized));
        OnPropertyChanged(nameof(RoomCustomized));
        OnPropertyChanged(nameof(CallsCustomized));
        OnPropertyChanged(nameof(FilesCustomized));
        OnPropertyChanged(nameof(AnyAdvancedCustomized));
        if (now.Count > 0) AdvancedResetStatus = "";
        else ConfirmingAdvancedReset = false;
    }

    [RelayCommand]
    private void AskAdvancedReset()
    {
        AdvancedResetStatus = "";
        ConfirmingAdvancedReset = AnyAdvancedCustomized;
    }

    [RelayCommand]
    private void CancelAdvancedReset() => ConfirmingAdvancedReset = false;

    /// <summary>
    /// Puts every value in the panel back to its default. What a room that is open opened with stays (the protection,
    /// the whole LAN and what members may route through me are fixed for the room); those go back when it closes.
    /// </summary>
    [RelayCommand]
    private void ResetAdvanced()
    {
        ConfirmingAdvancedReset = false;
        var d = AdvancedSettings.Defaults;
        AttestationIndex = Math.Max(0, Array.IndexOf(AttestationKeys, d.Attestation));
        SecurityBreaker = d.Breaker;
        BreakerRestartDelay = d.BreakerDelay.ToString();
        BreakerSkipDelay = d.BreakerSkip;
        ResetCalls(d);
        ResetFiles(d);
        var roomOpen = false;
        if (Multiplayer is { } m)
        {
            m.DefaultRelayOnly = d.RelayOnly;
            m.AutoCopyInvite = d.AutoCopyInvite;
            m.SelectedDefaultKnockWait = m.KnockWaitChoices.First(c => c.Wait == KnockWaits.Parse(d.KnockWait));
            m.RequestPopup = d.RequestPopup;
            m.AllowModSync = d.ModSync;
            roomOpen = !m.IsIdle;
            // While a room is open these are what it opened with: they go back to the defaults when it closes, unless
            // one was changed again meanwhile (that change stands).
            var (transport, disguise, fullLan, exit, proxy) = (m.SelectedTransport, m.DisguiseAsMinecraft, m.FullLanMode, m.AllowExitNode, m.AllowGameProxy);
            m.ApplyNowOrAfterRoom(() =>
            {
                if (m.SelectedTransport == transport && m.TransportChoices.FirstOrDefault(c => c.Mode == d.Transport) is { } standard) m.SelectedTransport = standard;
                if (m.DisguiseAsMinecraft == disguise) m.DisguiseAsMinecraft = d.Disguise;
                if (m.FullLanMode == fullLan) m.FullLanMode = d.FullLan;
                if (m.AllowExitNode == exit) m.AllowExitNode = d.NetworkExit;
                if (m.AllowGameProxy == proxy) m.AllowGameProxy = d.GameProxy;
                RefreshAdvanced();
            });
        }
        RefreshAdvanced();
        AdvancedResetStatus = L.T(roomOpen ? "settings.mp.reset_done_room" : "settings.mp.reset_done");
    }

    /// <summary>Troubleshooting › [Connection problems?]: the drawer on the multiplayer page.</summary>
    [RelayCommand]
    private void OpenConnectionProblems()
    {
        if (Multiplayer is not { } m) return;
        Navigate?.Invoke("play");
        m.DiagnosticsOpen = true;
    }

    /// <summary>Networks and relays: the links into Settings › Network.</summary>
    [RelayCommand]
    private void RevealNetworkField(string? field)
    {
        if (!string.IsNullOrEmpty(field)) RevealField?.Invoke(field);
    }
}
