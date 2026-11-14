using Amber.App.Localization;
using Amber.Core.GameLibrary;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// One row of the "who can see what" table, in the current language. <see cref="Now"/> marks the row that describes
/// me right now: with the word "Now" next to it (not by colour alone), and in the name a screen reader gets.
/// </summary>
public sealed record WhoSeesLine(WhoSeesRow Row) : LocalizedRecord
{
    private bool _now;

    public string Who => L.T(Row.WhoKey);
    public string Ip => L.T(Row.IpKey);
    public string Content => L.T(Row.ContentKey);

    public bool Now
    {
        get => _now;
        set
        {
            if (_now == value) return;
            _now = value;
            OnPropertyChanged(nameof(Now));
            OnPropertyChanged(nameof(SpokenName));
        }
    }

    /// <summary>The whole row as one sentence, starting with "Now" when it is the row that applies.</summary>
    public string SpokenName => L.F(Now ? "multiplayer.sees.row_name_now" : "multiplayer.sees.row_name", Who, Ip, Content);
}

/// <summary>One line of the room's facts card ("Who can come in · Only people I invite").</summary>
public sealed record RoomFact(string LabelKey, string Value) : LocalizedRecord
{
    public string Label => L.T(LabelKey);
}

/// <summary>
/// The room as a scene (UI restructure: people, rooms, networks): the banner with everyone's face and how each one is
/// reached, the connection mark that opens "who can see what", the "relays only" switch under that table, the one amber
/// action of the room (enter the game), and what the room is made of.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    // ---- the banner ----

    /// <summary>The cover behind the banner: the game this room is about (the host's instance, or the one I join with).</summary>
    public int RoomCoverSlot => CoverArt.SlotFor((IsHosting ? HostGameInstance : TargetInstance) ?? RoomTitle);

    /// <summary>"3 people".</summary>
    public string PeopleCountText => L.P("multiplayer.room.people", PeopleCount, PeopleCount);

    /// <summary>"Protection: Standard", from the room's own transport.</summary>
    public string RoomProtectionText => _room is { } room
        ? L.F("multiplayer.room.protection", ProtectionLabel(room.Tunnel))
        : L.F("multiplayer.room.protection", ProtectionLabel(new(SelectedTransport.Mode, CanDisguise && DisguiseAsMinecraft)));

    private static string ProtectionLabel(Amber.Core.Multiplayer.Transport.TunnelSettings tunnel) => RoomOpening.ProtectionOf(tunnel) switch
    {
        RoomProtection.Standard => L.T("multiplayer.open.protection.standard"),
        RoomProtection.Stronger => L.T("multiplayer.open.protection.stronger"),
        RoomProtection.Highest => L.T("multiplayer.open.protection.highest"),
        _ => L.T("multiplayer.open.protection.custom_short"),
    };

    // ---- the connection mark ----

    /// <summary>How the room as a whole is reached right now.</summary>
    public RoomLink RoomLinkNow => RoomLinks.Room(Peers.Select(p => p.Peer));

    public bool IsMarkDirect => RoomLinkNow.Kind == LinkKind.Direct;
    public bool IsMarkRelayed => RoomLinkNow.Kind == LinkKind.Relay;
    public bool IsMarkMixed => RoomLinkNow.Kind == LinkKind.Mixed;
    public bool IsMarkWaiting => RoomLinkNow.Kind == LinkKind.None;

    /// <summary>"Direct · 18 ms", "Relayed · 82 ms", "1 direct · 1 relayed", or what the room is waiting for.</summary>
    public string ConnectionMarkText
    {
        get
        {
            var link = RoomLinkNow;
            if (link.Kind == LinkKind.None) return IsHosting ? L.T("multiplayer.room.waiting_friends") : L.T("multiplayer.room.connecting");
            if (link.Kind != LinkKind.Mixed) return LinkLabel(link);
            var players = Peers.Select(p => p.Peer).Where(RoomLinks.IsPlayer).ToList();
            var direct = players.Count(p => RoomLinks.KindOf(p) == LinkKind.Direct);
            return L.F("multiplayer.room.mark_mixed", direct, players.Count - direct);
        }
    }

    /// <summary>"Direct · 18 ms" / "Relayed"; empty when the link is not known.</summary>
    internal static string LinkLabel(RoomLink link) => link.Kind is LinkKind.Direct or LinkKind.Relay
        ? string.Join(" · ", new[]
        {
            L.T(link.Kind == LinkKind.Direct ? "multiplayer.room.direct" : "multiplayer.room.relayed"),
            link.LatencyMs is { } ms ? $"{ms:F0} ms" : null,
        }.Where(s => s is not null))
        : "";

    /// <summary>Peers changed: the mark, and the line under each person.</summary>
    private void RefreshLinks()
    {
        var peers = Peers.Select(p => p.Peer).ToList();
        var host = RoomLinks.ToHost(peers);
        var guest = RoomLinks.ToGuest(peers);
        foreach (var person in People)
            person.Link = person.IsMe || !person.IsOnline ? RoomLink.None : person.IsHost ? host : guest;
        RefreshWhoSeesNow();
        foreach (var name in (string[])[nameof(RoomLinkNow), nameof(IsMarkDirect), nameof(IsMarkRelayed), nameof(IsMarkMixed), nameof(IsMarkWaiting), nameof(ConnectionMarkText)])
            OnPropertyChanged(name);
    }

    // ---- who can see what ----

    /// <summary>The fixed table: the same five rows here and on a network's card.</summary>
    public IReadOnlyList<WhoSeesLine> WhoSeesLines { get; } = [.. WhoSeesWhat.Rows.Select(r => new WhoSeesLine(r))];

    /// <summary>The row that describes me right now: it follows my links and my "relays only" switch.</summary>
    private void RefreshWhoSeesNow()
    {
        var now = WhoSeesWhat.AppliesNow(RoomLinkNow.Kind, MyRelayOnly);
        foreach (var line in WhoSeesLines) line.Now = now.Contains(line.Row.Id);
    }

    /// <summary>The table and the "relays only" switch are open (the connection mark toggles them).</summary>
    [ObservableProperty] public partial bool WhoSeesOpen { get; set; }

    /// <summary>Set by the page: bring the table into view.</summary>
    public Action? WhoSeesFocusRequested { get; set; }

    [RelayCommand]
    private void ToggleWhoSees()
    {
        WhoSeesOpen = !WhoSeesOpen;
        if (!WhoSeesOpen) return;
        CompactSection = 2;
        WhoSeesFocusRequested?.Invoke();
    }

    // ---- relays only ----

    private bool _joinedRelayRule;

    /// <summary>
    /// The multiplayer component is being restarted by one of the room's switches ("relays only", forwarding their
    /// game). Both are start-up settings of the same component, so only one restart runs at a time: the second
    /// would start from options the first is about to replace.
    /// </summary>
    private bool _componentRestarting;

    private void SetComponentRestarting(bool value)
    {
        _componentRestarting = value;
        NotifyRelayOnly();
        NotifyShare();
    }

    /// <summary>The room was opened as "relays only": the rule came with the room and holds for everyone in it.</summary>
    public bool RoomRuleRelayOnly => IsHosting ? _credentials?.RelayOnly == true : IsJoined && _joinedRelayRule;

    /// <summary>My own computer reaches the room through relays only (the room's rule, or my switch).</summary>
    public bool MyRelayOnly
    {
        get => _room?.RelayOnly == true;
        set => _ = SetRelayOnlyAsync(value);
    }

    /// <summary>The switch can be flipped: in a room that is not a sealed direct one, and whose rule does not already decide it.</summary>
    public bool CanChangeRelayOnly => _room is not null && !IsDirect && !RoomRuleRelayOnly && !_componentRestarting && !IsRekeying;

    /// <summary>Why the switch is fixed (empty when it can be flipped).</summary>
    public string RelayOnlyNote => _room is null ? ""
        : IsDirect ? L.T("multiplayer.sees.relay_only.direct_room")
        : RoomRuleRelayOnly ? L.T("multiplayer.sees.relay_only.room_rule")
        : "";

    public bool HasRelayOnlyNote => RelayOnlyNote.Length > 0;

    /// <summary>Multiplayer settings: every room I open or join starts with my side on relays only.</summary>
    public bool DefaultRelayOnly
    {
        get => _service.Settings.MultiplayerRelayOnly;
        set
        {
            if (_service.Settings.MultiplayerRelayOnly == value) return;
            _service.Settings.MultiplayerRelayOnly = value;
            _service.TrySaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Tests and self-checks: stands in for restarting the multiplayer component on the new room.</summary>
    internal Func<RoomCode, bool>? TestSwitchNetwork { get; set; }

    /// <summary>
    /// Turns direct connections of this computer off or on. EasyTier takes it as a start-up flag, so the component
    /// restarts on the same room (a few seconds; chat reconnects). The component then shares no address list, refuses
    /// hole punching and asks no STUN server, so nobody in the room learns this computer's IP address; only the relay
    /// sees it.
    /// </summary>
    internal async Task SetRelayOnlyAsync(bool on)
    {
        if (_room is not { } room || room.RelayOnly == on) return;
        if (!CanChangeRelayOnly)
        {
            NotifyRelayOnly();
            return;
        }
        SetComponentRestarting(true);
        try
        {
            // Let the switch show its new position before the component restarts.
            await Task.Yield();
            var next = room with { RelayOnly = on };
            bool switched;
            if (TestSwitchNetwork is { } stand)
            {
                switched = stand(next);
                if (switched) _room = next;
            }
            else switched = ReferenceEquals(_room, room) && SwitchNetwork(next);
            if (switched) SetStatus(StatusMessage.Info(on ? "multiplayer.sees.relay_only.turned_on" : "multiplayer.sees.relay_only.turned_off"));
            // Still in the room, so nothing was restarted: say the switch did not take. (When the restart itself
            // failed, the room is gone and its own message stands.)
            else if (_room is not null) SetStatus(StatusMessage.Warning("multiplayer.sees.relay_only.failed"));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            SetComponentRestarting(false);
            RefreshWhoSeesNow();
            RefreshRoomFacts();
        }
    }

    private void NotifyRelayOnly()
    {
        foreach (var name in (string[])[nameof(MyRelayOnly), nameof(CanChangeRelayOnly), nameof(RelayOnlyNote), nameof(HasRelayOnlyNote), nameof(RoomRuleRelayOnly)])
            OnPropertyChanged(name);
    }

    /// <summary>My own default on top of what the room says: used when my side of a room starts.</summary>
    private RoomCode WithMyRelayChoice(RoomCode room) => _service.Settings.MultiplayerRelayOnly && !room.RelayOnly ? room with { RelayOnly = true } : room;

    // ---- the one amber action: enter the game ----

    /// <summary>
    /// A guest can start the game and go straight in; a host who opened the room before the game can start it from here.
    /// A host whose world is already open has nothing to enter.
    /// </summary>
    public bool ShowEnterGame => IsPlayTab && (IsJoined ? JoinAddress.Length > 0 : IsHosting && IsWaitingForGame && HostGameInstance is not null && StartInstance is not null);

    [RelayCommand]
    private void EnterGame()
    {
        if (IsJoined)
        {
            if (TargetInstance is null)
            {
                CompactSection = 2;
                SetStatus(StatusMessage.Warning("multiplayer.room.enter_game_pick"));
                return;
            }
            LaunchAndJoinCommand.Execute(null);
        }
        else if (IsHosting && IsWaitingForGame && HostGameInstance is { } id) StartInstance?.Invoke(id);
    }

    // ---- what the room is made of ----

    private RoomAccess? _openedAccess;

    /// <summary>The facts card: who can come in (host), how people connect, protection, scope, mod sync (host).</summary>
    public System.Collections.ObjectModel.ObservableCollection<RoomFact> RoomFacts { get; } = [];

    internal void RefreshRoomFacts()
    {
        RoomFacts.Clear();
        if (IsIdle) return;
        if (IsHosting && _openedAccess is { } access)
            RoomFacts.Add(new RoomFact("multiplayer.open.who", L.T(access switch
            {
                RoomAccess.Friends => "multiplayer.open.who.friends",
                RoomAccess.Public => "multiplayer.open.who.public",
                _ => "multiplayer.open.who.invited",
            })));
        RoomFacts.Add(new RoomFact("multiplayer.open.route", L.T(IsDirect ? "multiplayer.open.route.serverless"
            : MyRelayOnly ? "multiplayer.open.route.relay" : "multiplayer.open.route.auto")));
        if (_room is { } room) RoomFacts.Add(new RoomFact("multiplayer.open.protection", ProtectionLabel(room.Tunnel)));
        RoomFacts.Add(new RoomFact("multiplayer.open.scope", L.T(_session?.Options.Tun == true ? "multiplayer.open.scope.lan" : "multiplayer.open.scope.game")));
        if (IsHosting) RoomFacts.Add(new RoomFact("multiplayer.room.mod_sync", L.T(AllowModSync ? "multiplayer.room.fact.allowed" : "multiplayer.room.fact.off")));
    }

    /// <summary>My side of a room is up: the banner, the switch and the facts read from it.</summary>
    private void OnRoomEntered()
    {
        NotifyRelayOnly();
        RefreshRoomFacts();
        NotifyScene();
    }

    /// <summary>
    /// Tests and self-checks: a room without a component, as if my side had just started. A host can get a stand-in
    /// lobby too, so the invite panel works.
    /// </summary>
    internal void EnterTestRoom(RoomCode room, bool host, bool relayRule = false, bool lobby = false)
    {
        _room = room;
        _joinedRelayRule = relayRule;
        if (host && lobby) StartTestLobby(room);
        Mode = host ? "host" : "join";
        OnRoomEntered();
    }

    /// <summary>Tests and self-checks: leaves the room made by <see cref="EnterTestRoom"/>.</summary>
    internal void LeaveTestRoom()
    {
        StopHostLobby();
        _room = null;
        _currentOptions = null;
        Mode = "";
    }

    private void InitScene()
    {
        Peers.CollectionChanged += (_, _) => RefreshLinks();
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Mode):
                    if (IsIdle)
                    {
                        WhoSeesOpen = false;
                        _openedAccess = null;
                    }
                    NotifyRelayOnly();
                    RefreshRoomFacts();
                    NotifyScene();
                    break;
                case nameof(IsRekeying):
                    OnPropertyChanged(nameof(CanChangeRelayOnly));
                    break;
                case nameof(Tab) or nameof(JoinAddress) or nameof(IsWaitingForGame) or nameof(TargetInstance):
                    NotifyScene();
                    break;
                case nameof(AllowModSync):
                    RefreshRoomFacts();
                    break;
            }
        };
    }

    private void NotifyScene()
    {
        foreach (var name in (string[])[nameof(ShowEnterGame), nameof(RoomCoverSlot), nameof(RoomProtectionText), nameof(PeopleCountText)])
            OnPropertyChanged(name);
    }
}
