using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>The four tabs of the multiplayer page (official design §6.14).</summary>
public enum MultiplayerTab
{
    Play,
    Public,
    Servers,
    Settings,
}

/// <summary>
/// The multiplayer lobby (§6.14): tabs, the two cards (open a room / join a friend), one paste box that recognises
/// every kind of code, and the "one lamp" rule (only one amber button on screen).
/// </summary>
public sealed partial class MultiplayerViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayTab), nameof(IsPublicTab), nameof(IsServersTab), nameof(IsSettingsTab), nameof(TabIndex))]
    public partial MultiplayerTab Tab { get; set; }

    public bool IsPlayTab => Tab == MultiplayerTab.Play;
    public bool IsPublicTab => Tab == MultiplayerTab.Public;
    public bool IsServersTab => Tab == MultiplayerTab.Servers;
    public bool IsSettingsTab => Tab == MultiplayerTab.Settings;

    /// <summary>For the segmented header (0 play, 1 public, 2 servers, 3 settings).</summary>
    public int TabIndex
    {
        get => (int)Tab;
        set
        {
            var tab = (MultiplayerTab)Math.Clamp(value, 0, 3);
            // Child mode hides the public tab.
            if (tab == MultiplayerTab.Public && !ShowPublicNetworks) tab = MultiplayerTab.Play;
            Tab = tab;
        }
    }

    [RelayCommand]
    private void ShowTab(string? name) => Tab = ParseTab(name) ?? MultiplayerTab.Play;

    /// <summary>The second part of a play/… route.</summary>
    public static MultiplayerTab? ParseTab(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" or "lobby" or "room" => MultiplayerTab.Play,
        "public" => MultiplayerTab.Public,
        "servers" or "server" => MultiplayerTab.Servers,
        "settings" => MultiplayerTab.Settings,
        _ => null,
    };

    /// <summary>Navigated to play/… : show that tab (unknown parts leave the tab as it was).</summary>
    public void ShowRoute(string? part)
    {
        if (ParseTab(part) is { } tab) TabIndex = (int)tab;
    }

    /// <summary>Servers tab: false = Jay servers (join), true = the server I run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJayServerList))]
    public partial bool ShowMyServer { get; set; }

    public bool ShowJayServerList
    {
        get => !ShowMyServer;
        set => ShowMyServer = !value;
    }

    /// <summary>The lobby's "more ways" (manual port, direct connection) is open.</summary>
    [ObservableProperty] public partial bool MoreWaysOpen { get; set; }

    // ---- the paste box ----

    public PastedCodeKind JoinCodeKind => PastedCode.Detect(JoinCode);

    /// <summary>A code we know how to handle is in the box: the join button takes the amber.</summary>
    public bool JoinCodeRecognized => JoinCodeKind is not PastedCodeKind.Unknown;

    public bool HasJoinCodeText => !string.IsNullOrWhiteSpace(JoinCode);

    /// <summary>What the pasted code is and where it will go, in plain words.</summary>
    public string JoinCodeRecognition => JoinCodeKind switch
    {
        PastedCodeKind.Invite => L.T("multiplayer.lobby.code.invite"),
        PastedCodeKind.DirectInvite => L.T("multiplayer.lobby.code.direct_invite"),
        PastedCodeKind.DirectReply => L.T("multiplayer.lobby.code.direct_reply"),
        PastedCodeKind.JayServer => L.T("multiplayer.lobby.code.server"),
        PastedCodeKind.FriendCode => L.T("multiplayer.lobby.code.friend"),
        PastedCodeKind.NodeShare => L.T("multiplayer.lobby.code.relay_share"),
        PastedCodeKind.PublicNetwork => L.T("multiplayer.lobby.code.public_network"),
        PastedCodeKind.Room or PastedCodeKind.OldDirect => L.T("multiplayer.lobby.code.old"),
        _ => HasJoinCodeText ? L.T("multiplayer.lobby.code.unknown") : "",
    };

    /// <summary>Old codes and unknown text are shown in the attention colour.</summary>
    public bool JoinCodeRecognitionWarns => HasJoinCodeText && JoinCodeKind is PastedCodeKind.Unknown or PastedCodeKind.Room or PastedCodeKind.OldDirect;

    /// <summary>One lamp: the host button is amber until a code is pasted, then the join button is.</summary>
    public bool HostButtonIsAmber => !JoinCodeRecognized;

    partial void OnJoinCodeChanged(string value)
    {
        OnPropertyChanged(nameof(JoinCodeKind));
        OnPropertyChanged(nameof(JoinCodeRecognized));
        OnPropertyChanged(nameof(HasJoinCodeText));
        OnPropertyChanged(nameof(JoinCodeRecognition));
        OnPropertyChanged(nameof(JoinCodeRecognitionWarns));
        OnPropertyChanged(nameof(HostButtonIsAmber));
        RepickJoinInstance();
    }

    /// <summary>
    /// Ctrl+V anywhere outside a text box (§6.14 keyboard): a code on the clipboard opens the lobby with it in the
    /// paste box. Nothing is joined until the player clicks. Returns false for text that is not a code.
    /// </summary>
    public bool PasteCode(string? text)
    {
        var kind = PastedCode.Detect(text);
        if (kind == PastedCodeKind.Unknown || text is null) return false;
        if (kind == PastedCodeKind.JayServer)
        {
            Tab = MultiplayerTab.Servers;
            ShowMyServer = false;
            JayServers.InviteText = text.Trim();
            return true;
        }
        if (kind == PastedCodeKind.FriendCode && ShowPublicNetworks)
        {
            Tab = MultiplayerTab.Public;
            FindCodeInput = text.Trim();
            return true;
        }
        Tab = MultiplayerTab.Play;
        JoinCode = text.Trim();
        return true;
    }

    // ---- opening a room from the lobby ----

    /// <summary>"Copy the invite when the room is open" (visible tick box under the button; default on).</summary>
    public bool AutoCopyInvite
    {
        get => _service.Settings.MultiplayerAutoCopyInvite;
        set
        {
            if (_service.Settings.MultiplayerAutoCopyInvite == value) return;
            _service.Settings.MultiplayerAutoCopyInvite = value;
            _service.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The lobby's open-a-room button: opens the room, makes the first invite (1 use, 10 minutes, needs my approval),
    /// shows the invite card and, when ticked, copies the code (cleared from the clipboard later).
    /// </summary>
    [RelayCommand]
    private async Task HostFromLobbyAsync()
    {
        await CreateRoomAsync();
        if (!IsHosting) return;
        SelectedInviteExpiry ??= InviteExpiryChoices[0];
        InviteDialogOpen = true;
        if (await IssueInviteAsync(1, InviteExpiryChoices[0].Lifetime, false) is null) return;
        if (AutoCopyInvite && _lastInvite is { } invite && await SecretClipboard.CopyAsync(invite.ToString()))
            SetStatus(StatusMessage.Success("multiplayer.lobby.invite_copied"));
    }

    /// <summary>Opens multiplayer help (set by the window: help/{id}).</summary>
    public Action<string>? Navigate { get; set; }

    [RelayCommand]
    private void OpenHelp(string? article) => Navigate?.Invoke(string.IsNullOrEmpty(article) ? "help" : "help/" + article);

    /// <summary>Lobby footer: component and relay servers at a glance.</summary>
    public string LobbyStatusText => L.F("multiplayer.lobby.status",
        IsComponentReady ? L.T("multiplayer.lobby.status_component_ready") : L.T("multiplayer.lobby.status_component_missing"),
        HasNodes ? L.F("multiplayer.lobby.status_relays", EffectiveNodeCount) : L.T("multiplayer.lobby.status_no_relays"));

    public bool LobbyStatusWarns => !IsComponentReady || !HasNodes;

    private void NotifyLobbyStatus()
    {
        OnPropertyChanged(nameof(LobbyStatusText));
        OnPropertyChanged(nameof(LobbyStatusWarns));
    }
}
