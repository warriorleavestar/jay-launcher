using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The views of the multiplayer page. <see cref="Play"/> is the page itself (the lobby, or the room when in one); the
/// others are sub-views with a way back. They used to be four tabs (official design §6.14); the settings tab moved to
/// Settings › Multiplayer and Settings › Network (redesign batch 7).
/// </summary>
public enum MultiplayerTab
{
    Play,
    Public,
    Servers,
}

/// <summary>
/// The multiplayer lobby: one paste box that recognises every kind of code, the open-a-room card, "where to play", the
/// sub-views behind it, and the "one lamp" rule (only one amber button on screen).
/// </summary>
public sealed partial class MultiplayerViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayTab), nameof(IsPublicTab), nameof(IsServersTab), nameof(TabIndex), nameof(IsSubView), nameof(SubViewTitle))]
    public partial MultiplayerTab Tab { get; set; }

    /// <summary>Not the lobby or the room: the network view or my server.</summary>
    public bool IsSubView => Tab != MultiplayerTab.Play;

    public string SubViewTitle => Tab switch
    {
        MultiplayerTab.Public => L.T("multiplayer.subview.network"),
        MultiplayerTab.Servers => L.T("multiplayer.subview.server"),
        _ => "",
    };

    public bool IsPlayTab => Tab == MultiplayerTab.Play;
    public bool IsPublicTab => Tab == MultiplayerTab.Public;
    public bool IsServersTab => Tab == MultiplayerTab.Servers;

    /// <summary>The view as a number (0 lobby or room, 1 network, 2 my server); routes set it.</summary>
    public int TabIndex
    {
        get => (int)Tab;
        set
        {
            var tab = (MultiplayerTab)Math.Clamp(value, 0, 2);
            // Child mode has no public networks.
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
        _ => null,
    };

    /// <summary>Navigated to play/… : show that tab (unknown parts leave the tab as it was).</summary>
    public void ShowRoute(string? part)
    {
        if (ParseTab(part) is { } tab) TabIndex = (int)tab;
    }

    /// <summary>"Open an always-on server…" under the open-a-room card, and the room menu: the server I run.</summary>
    [RelayCommand]
    private void OpenMyServer() => Tab = MultiplayerTab.Servers;

    /// <summary>The room menu's way to Settings › Multiplayer.</summary>
    [RelayCommand]
    private void OpenMultiplayerSettings() => Navigate?.Invoke(LegacyRoutes.MultiplayerSettings);

    /// <summary>"Advanced settings" under the open-a-room card: what a new room starts with, in Settings › Multiplayer.</summary>
    [RelayCommand]
    private void OpenRoomDefaults() => Navigate?.Invoke(LegacyRoutes.RoomDefaults);

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
        PastedCodeKind.JayServer => L.T("multiplayer.lobby.code.server"),
        PastedCodeKind.FriendCode => L.T("multiplayer.lobby.code.friend"),
        PastedCodeKind.NodeShare => L.T("multiplayer.lobby.code.relay_share"),
        PastedCodeKind.PublicNetwork => L.T("multiplayer.lobby.code.public_network"),
        PastedCodeKind.ContactCode => L.T("multiplayer.lobby.code.contact"),
        PastedCodeKind.SealedOffer => L.T("multiplayer.lobby.code.sealed_offer"),
        PastedCodeKind.SealedReply => L.T("multiplayer.lobby.code.sealed_reply"),
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
        OnPropertyChanged(nameof(JoinCodeIsSealed));
        OnPropertyChanged(nameof(JoinNeedsPassphrase));
        OnPropertyChanged(nameof(JoinButtonText));
        RepickJoinInstance();
    }

    /// <summary>
    /// Ctrl+V anywhere outside a text box (§6.14 keyboard): a code on the clipboard is put where it belongs, and the
    /// route of that place is returned. Nothing is joined or sent until the player clicks. Null for text that is not
    /// a code.
    /// </summary>
    internal string? PlacePastedCode(string? text)
    {
        var kind = PastedCode.Detect(text);
        if (kind == PastedCodeKind.Unknown || text is null) return null;
        if (kind == PastedCodeKind.FriendCode && ShowPublicNetworks)
        {
            Tab = MultiplayerTab.Public;
            FindCodeInput = text.Trim();
            return "play/public";
        }
        if (kind == PastedCodeKind.ContactCode && ShowPublicNetworks)
        {
            // A friend code: into the add panel of the Friends page, added only when the player clicks [Add].
            ContactCodeInput = text.Trim();
            return "people/add";
        }
        Tab = MultiplayerTab.Play;
        JoinCode = text.Trim();
        return "play";
    }

    /// <summary>
    /// The same from inside the multiplayer page: the lobby and its sub-pages follow <see cref="Tab"/>; a friend code
    /// asks for the Friends page. Returns false for text that is not a code.
    /// </summary>
    public bool PasteCode(string? text)
    {
        if (PlacePastedCode(text) is not { } route) return false;
        if (route == "people/add") OpenAddFriend();
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
    /// The lobby's open-a-room button. With the component missing it installs that first. Otherwise it opens the room
    /// the four choices describe, makes the first invite (1 use, 10 minutes, needs my approval), shows the invite card
    /// and, when ticked, copies the code (cleared from the clipboard later). "No server" makes a direct room and its
    /// sealed code instead; "friends" and "public" then tell the friends or list the room.
    /// </summary>
    [RelayCommand]
    private async Task HostFromLobbyAsync()
    {
        if (RefuseHostWhileKnocking()) return;
        if (!IsComponentReady)
        {
            await InstallComponentStepAsync();
            return;
        }
        var access = OpenAccess;
        var route = OpenRoute;
        if (route == RoomRoute.Serverless)
        {
            await CreateDirectRoomAsync();
            if (!IsDirectHost) return;
            RememberLastRoom(1, InviteOffer.Lifetime(InviteLife.TenMinutes), false);
            await CreateSealedOfferAsync();
            return;
        }
        if (StartGameToHost()) return;
        await CreateRoomAsync();
        if (!IsHosting) return;
        InviteDialogOpen = true;
        if (await IssueInviteAsync(1, InviteOffer.Lifetime(InviteLife.TenMinutes), false) is null) return;
        RememberLastRoom(1, InviteOffer.Lifetime(InviteLife.TenMinutes), false);
        if (AutoCopyInvite && _lastInvite is { } invite && await SecretClipboard.CopyAsync(invite.ToString()))
            SetStatus(StatusMessage.Success("multiplayer.lobby.invite_copied"));
        await ApplyAccessAsync(access);
    }

    /// <summary>After the room is open: tell the friends, or list the room, as "who can come in" says.</summary>
    private async Task ApplyAccessAsync(RoomAccess access)
    {
        if (!IsHosting || RoomOpening.Blocked(access, OpenRoute, OpenContext) is not null) return;
        if (access == RoomAccess.Friends) await InviteFriendsToKnockAsync();
        else if (access == RoomAccess.Public) await PublishFromChoiceAsync();
    }

    /// <summary>Installs the multiplayer component from the button that needed it.</summary>
    private async Task InstallComponentStepAsync()
    {
        if (IsDownloading) return;
        if (TestInstallComponent is { } install) await install();
        else await DownloadComponentCommand.ExecuteAsync(null);
    }

    /// <summary>Tests: stands in for the component download.</summary>
    internal Func<Task>? TestInstallComponent { get; set; }

    /// <summary>
    /// A server invite in the paste box: joins and saves the server (the invite works once, so it is used right away).
    /// A sealed one first needs the passphrase, typed in the box under the code.
    /// </summary>
    private async Task JoinServerFromPasteBoxAsync()
    {
        JayServers.InviteText = JoinCode.Trim();
        JayServers.InvitePassphrase = JoinPassphrase;
        bool joined;
        try
        {
            joined = await JayServers.EnrollFromInviteAsync();
        }
        finally
        {
            // The server page that used to show these fields is gone: nothing should keep the invite or passphrase.
            JayServers.InviteText = "";
            JayServers.InvitePassphrase = "";
            JoinPassphrase = "";
        }
        if (JayServers.Status.Length > 0) SetStatus(new StatusMessage("common.raw", [JayServers.Status], StatusKind.Info));
        if (!joined) return;
        JoinCode = "";
        Where = WhereFilter.Servers;
    }

    /// <summary>What the join button does with what is in the paste box.</summary>
    public string JoinButtonText => JoinCodeKind switch
    {
        PastedCodeKind.JayServer => L.T("multiplayer.lobby.join.server"),
        PastedCodeKind.ContactCode => L.T("multiplayer.lobby.join.friend"),
        PastedCodeKind.PublicNetwork => L.T("multiplayer.lobby.join.network"),
        // The component is a step on the button once there is something to join with.
        PastedCodeKind.Invite or PastedCodeKind.SealedOffer when !IsComponentReady => ComponentStepText,
        _ => L.T("multiplayer.lobby.join_button"),
    };

    /// <summary>The passphrase box under the code: a sealed direct code, or a sealed server invite.</summary>
    public bool JoinNeedsPassphrase => JoinCodeIsSealed || JoinCodeKind == PastedCodeKind.JayServer && Amber.Core.Multiplayer.Gate.GateInvite.IsSealed(JoinCode);

    /// <summary>Opens multiplayer help (set by the window: help/{id}).</summary>
    public Action<string>? Navigate { get; set; }

    /// <summary>
    /// "Launch without sandbox" is on: the page keeps a line saying the game (and its mods) can read Jay's keys, with a
    /// way back to the setting (privacy plan item 15).
    /// </summary>
    public bool RunsWithoutSandbox => _service.Settings.LaunchWithoutSandbox;

    [RelayCommand]
    private void OpenSandboxSettings() => Navigate?.Invoke("settings#LaunchWithoutSandbox");

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
