using System.Collections.ObjectModel;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using Amber.App.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One person in the room console's "people in the room" list (official §6.15).</summary>
public sealed record PersonItem(string Name, string Fingerprint, bool IsMe, bool IsHost, MemberItem? Member, OnlinePeerItem? Online) : LocalizedRecord
{
    public string DisplayName => IsMe
        ? IsHost ? L.F("multiplayer.people.me_host", Name) : L.F("multiplayer.people.me", Name)
        : IsHost ? L.F("multiplayer.people.host", Name) : Name.Length > 0 ? Name : L.T("multiplayer.peers.player");

    /// <summary>What the list says under the name: where they are, or how they are reached ("Direct · 18 ms").</summary>
    public string Status => IsMe ? L.T("multiplayer.people.here")
        : Member is { NeedsInvite: true } ? L.T("multiplayer.kick.needs_invite")
        : Online is not null ? (LinkText.Length > 0 ? LinkText : L.T("multiplayer.people.online"))
        : Member is { Confirmed: false } ? L.T("multiplayer.invite.member_unconfirmed")
        : L.T("multiplayer.people.offline");

    /// <summary>Their verified Jay account: from their knock (host), or the host's approval (guest looking at the host).</summary>
    public string? JayAccount { get; init; }
    public bool HasJayAccount => JayAccount is not null;

    /// <summary>A friend I compared emoji with (the small tick on the face). Everyone else in a room is "someone I've met".</summary>
    public bool IsFriend { get; init; }
    public string FriendText => IsMe ? "" : L.T(IsFriend ? "multiplayer.people.friend" : "multiplayer.people.met");

    private RoomLink _link;

    /// <summary>How my computer reaches theirs right now (<see cref="RoomLinks"/>); none for me and for people who are away.</summary>
    public RoomLink Link
    {
        get => _link;
        set
        {
            if (_link == value) return;
            _link = value;
            foreach (var name in (string[])[nameof(Link), nameof(LinkText), nameof(IsDirect), nameof(IsRelayed), nameof(HasLink), nameof(Status), nameof(Summary)]) OnPropertyChanged(name);
        }
    }

    public string LinkText => MultiplayerViewModel.LinkLabel(Link);
    public bool HasLink => LinkText.Length > 0;
    public bool IsDirect => Link.Kind == LinkKind.Direct;
    public bool IsRelayed => Link.Kind == LinkKind.Relay;

    /// <summary>For screen readers and the tooltip on a face in the banner: "Alex · Friend · Direct · 18 ms".</summary>
    public string Summary => string.Join(" · ", new[] { DisplayName, FriendText, Status }.Where(s => s.Length > 0));

    public bool IsOnline => IsMe || Online is not null;
    public bool CanPrivate => !IsMe && Online is not null;
    public bool CanTrust => Member is { Trusted: false };
    public bool IsTrusted => Member is { Trusted: true };
    public bool CanRemove => Member is not null;
}

/// <summary>A choice in "how long to wait for my answer".</summary>
public sealed record KnockWaitChoice(KnockWait Wait, string Key) : LocalizedRecord
{
    public string Label => L.T(Key);
    public override string ToString() => Label;
}

/// <summary>
/// The room console (official §6.15): a sticky header, the locked knock panel, people | chat | details, the invite card
/// with "copy a message", the room settings drawer (who can join, who can find it, how long to wait, removed people).
/// </summary>
public sealed partial class MultiplayerViewModel
{
    // ---- header ----

    public string RoomTitle => IsHosting ? L.T("multiplayer.room.my_room") : IsDirect ? L.T("multiplayer.direct.title") : L.T("multiplayer.room.friend_room");

    /// <summary>Peers other than me and relay servers: the people actually connected (the same test as the connection mark).</summary>
    private IEnumerable<PeerItem> OtherPlayers => Peers.Where(p => RoomLinks.IsPlayer(p.Peer));

    public int PeopleCount => Math.Max(1, People.Count(p => p.IsOnline));

    /// <summary>The connection mark as words: "Direct · 31 ms", "1 direct · 1 relayed", or "waiting for friends".</summary>
    public string ConnectionSummary => ConnectionMarkText;

    public bool IsConnectedToOthers => OtherPlayers.Any();

    public string RoomHeaderText => string.Join(" · ", RoomTitle, L.P("multiplayer.room.people", PeopleCount, PeopleCount), ConnectionSummary);

    public bool HasVirtualIp => !string.IsNullOrEmpty(MyVirtualIp);

    public string VirtualIpLabel => L.F("multiplayer.room.my_address", MyVirtualIp);

    [RelayCommand]
    private async Task CopyVirtualIpAsync()
    {
        if (!HasVirtualIp) return;
        if (await Amber.App.Services.AvaloniaClipboard.TrySetTextAsync(MyVirtualIp))
            SetStatus(StatusMessage.Success("multiplayer.room.address_copied"));
    }

    private void NotifyRoomHeader()
    {
        OnPropertyChanged(nameof(RoomTitle));
        OnPropertyChanged(nameof(PeopleCount));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(IsConnectedToOthers));
        OnPropertyChanged(nameof(RoomHeaderText));
        OnPropertyChanged(nameof(HasVirtualIp));
        OnPropertyChanged(nameof(VirtualIpLabel));
        OnPropertyChanged(nameof(EndRoomLabel));
        OnPropertyChanged(nameof(PeopleCountText));
    }

    /// <summary>The sticky room header (and the knock panel under it) on the play tab while in a room.</summary>
    public bool ShowRoomHeader => IsPlayTab && !IsIdle;

    /// <summary>Header "Invite friends" and Ctrl+Shift+I: show the invite card (compact: its section) with a fresh invite if there is none.</summary>
    [RelayCommand]
    private async Task InviteFriendsAsync()
    {
        if (!IsHosting) return;
        Tab = MultiplayerTab.Play;
        CompactSection = 2;
        // A "No server" room is joined with a sealed code: make one if there is none yet, and go to it.
        if (IsDirectHost)
        {
            if (!HasSealedOffer) await CreateSealedOfferAsync();
        }
        else if (!HasInvite) await CreateInviteCommand.ExecuteAsync(null);
        InviteFocusRequested?.Invoke();
    }

    /// <summary>Set by the page: bring the invite card into view and focus its copy button.</summary>
    public Action? InviteFocusRequested { get; set; }

    /// <summary>"End room" for the host, "Leave" for a guest.</summary>
    public string EndRoomLabel => IsHosting ? L.T("multiplayer.room.end") : L.T("multiplayer.room.leave");

    // ---- people ----

    public ObservableCollection<PersonItem> People { get; } = [];

    /// <summary>How many faces the banner shows before it says "+3".</summary>
    internal const int BannerFaces = 10;

    /// <summary>The faces in the banner: the first few people; the rest are counted in <see cref="ExtraPeopleText"/>.</summary>
    public ObservableCollection<PersonItem> BannerPeople { get; } = [];

    public bool HasExtraPeople => People.Count > BannerFaces;

    public string ExtraPeopleText => HasExtraPeople ? "+" + (People.Count - BannerFaces) : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPerson))]
    public partial PersonItem? SelectedPerson { get; set; }

    public bool HasSelectedPerson => SelectedPerson is not null;

    private void InitRoom()
    {
        Members.CollectionChanged += (_, _) => RefreshPeople();
        OnlinePeers.CollectionChanged += (_, _) => RefreshPeople();
        ContactList.CollectionChanged += (_, _) => RefreshPeople();
        Peers.CollectionChanged += (_, _) => NotifyRoomHeader();
        ApprovalCards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasKnocks));
            OnPropertyChanged(nameof(FirstKnock));
            OnPropertyChanged(nameof(HasMoreKnocks));
            OnPropertyChanged(nameof(MoreKnocksText));
            UpdateKnockTimer();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Mode) or nameof(Tab)) OnPropertyChanged(nameof(ShowRoomHeader));
            if (e.PropertyName == nameof(NetworkCheckText))
            {
                OnPropertyChanged(nameof(NetworkVerdict));
                OnPropertyChanged(nameof(NetworkDetails));
                OnPropertyChanged(nameof(HasNetworkVerdict));
            }
            if (e.PropertyName is nameof(Mode) or nameof(MyVirtualIp))
            {
                RefreshPeople();
                NotifyRoomHeader();
                if (e.PropertyName == nameof(Mode) && IsIdle) RoomSettingsOpen = false;
            }
        };
    }

    /// <summary>Me first, then (host) everyone let in, with whoever is online in the roster merged in; guests see the roster.</summary>
    internal void RefreshPeople()
    {
        var selected = SelectedPerson?.Fingerprint;
        People.Clear();
        if (IsIdle)
        {
            RefreshBannerPeople();
            return;
        }
        var me = Identity.Fingerprint;
        People.Add(new PersonItem(ChatName, me, true, IsHosting, null, null));
        var friends = ContactList.SelectMany(f => f.Devices).Where(d => d.Verified).Select(d => MemberIdentity.FingerprintOf(d.PublicKey)).ToHashSet();
        var online = OnlinePeers.ToDictionary(p => p.Entry.Fingerprint);
        foreach (var m in Members)
        {
            online.Remove(m.Fingerprint, out var peer);
            People.Add(new PersonItem(m.Name, m.Fingerprint, false, false, m, peer) { JayAccount = m.JayAccount, IsFriend = friends.Contains(m.Fingerprint) });
        }
        foreach (var peer in online.Values.Where(p => p.Entry.Fingerprint != me))
        {
            var isHost = !IsHosting && peer.Entry.Fingerprint == HostFingerprint;
            People.Add(new PersonItem(peer.Entry.Name, peer.Entry.Fingerprint, false, isHost, null, peer)
            {
                JayAccount = isHost ? HostJayAccount : null,
                IsFriend = friends.Contains(peer.Entry.Fingerprint),
            });
        }
        SelectedPerson = People.FirstOrDefault(p => p.Fingerprint == selected);
        RefreshBannerPeople();
        RefreshLinks();
        NotifyRoomHeader();
    }

    private void RefreshBannerPeople()
    {
        BannerPeople.Clear();
        foreach (var person in People.Take(BannerFaces)) BannerPeople.Add(person);
        OnPropertyChanged(nameof(HasExtraPeople));
        OnPropertyChanged(nameof(ExtraPeopleText));
    }

    [RelayCommand]
    private void ChatWith(PersonItem? person)
    {
        if (person?.Online is not { } peer) return;
        SelectedPeer = peer;
        PrivateChatMode = true;
        CompactSection = 1;
    }

    [RelayCommand]
    private void TrustPerson(PersonItem? person)
    {
        if (person?.Member is { } member) TrustMemberCommand.Execute(member);
    }

    [RelayCommand]
    private async Task RemovePersonAsync(PersonItem? person)
    {
        if (person?.Member is { } member) await AskKickMemberCommand.ExecuteAsync(member);
    }

    /// <summary>A face in the banner: show that person in the list (compact windows switch to the people section).</summary>
    [RelayCommand]
    private void SelectPerson(PersonItem? person)
    {
        if (person is null || !People.Contains(person)) return;
        SelectedPerson = person;
        CompactSection = 0;
    }

    // ---- chat: one panel, room or private ----

    /// <summary>false = room chat, true = private chat (end-to-end, the host only passes it on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomChatMode))]
    public partial bool PrivateChatMode { get; set; }

    public bool RoomChatMode
    {
        get => !PrivateChatMode;
        set => PrivateChatMode = !value;
    }

    /// <summary>Compact windows show one of people (0), chat (1), invite (2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactPeople), nameof(CompactChat), nameof(CompactInvite))]
    public partial int CompactSection { get; set; }

    public bool CompactPeople { get => CompactSection == 0; set { if (value) CompactSection = 0; } }
    public bool CompactChat { get => CompactSection == 1; set { if (value) CompactSection = 1; } }
    public bool CompactInvite { get => CompactSection == 2; set { if (value) CompactSection = 2; } }

    // ---- knocks ----

    public bool HasKnocks => ApprovalCards.Count > 0;

    /// <summary>The knock on screen: one at a time, oldest first.</summary>
    public ApprovalCard? FirstKnock => ApprovalCards.FirstOrDefault();

    public bool HasMoreKnocks => ApprovalCards.Count > 1;

    /// <summary>"2 more people are knocking".</summary>
    public string MoreKnocksText => L.P("multiplayer.knock.more", ApprovalCards.Count - 1, ApprovalCards.Count - 1);

    private DispatcherTimer? _knockTimer;

    private void UpdateKnockTimer()
    {
        if (ApprovalCards.Count == 0)
        {
            _knockTimer?.Stop();
            return;
        }
        _knockTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var card in ApprovalCards) card.Tick();
        });
        _knockTimer.Start();
    }

    /// <summary>Alt+Y on the first knock.</summary>
    [RelayCommand]
    private void ApproveFirstKnock()
    {
        if (ApprovalCards.FirstOrDefault() is { } card) ApproveKnock(card);
    }

    /// <summary>Alt+N or Esc on the first knock.</summary>
    [RelayCommand]
    private void DenyFirstKnock()
    {
        if (ApprovalCards.FirstOrDefault() is { } card) DenyKnock(card);
    }

    private void OnKnockTimedOut(KnockRequest request) =>
        SetStatus(StatusMessage.Warning("multiplayer.knock.timed_out", request.Name.Length > 0 ? request.Name : L.T("multiplayer.peers.player"), WaitLabel(RoomKnockWait)));

    // ---- how long to wait ----

    public IReadOnlyList<KnockWaitChoice> KnockWaitChoices { get; } =
    [
        new(KnockWait.TwoMinutes, "multiplayer.knock.wait.two"),
        new(KnockWait.TenMinutes, "multiplayer.knock.wait.ten"),
        new(KnockWait.Forever, "multiplayer.knock.wait.forever"),
    ];

    private static string WaitLabel(KnockWait wait) => L.T(wait switch
    {
        KnockWait.TwoMinutes => "multiplayer.knock.wait.two",
        KnockWait.Forever => "multiplayer.knock.wait.forever",
        _ => "multiplayer.knock.wait.ten",
    });

    private KnockWait? _roomKnockWait;

    /// <summary>This room's wait (starts from the default in multiplayer settings).</summary>
    public KnockWait RoomKnockWait
    {
        get => _roomKnockWait ?? KnockWaits.Parse(_service.Settings.MultiplayerKnockWait);
        set
        {
            _roomKnockWait = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedRoomKnockWait));
        }
    }

    public KnockWaitChoice SelectedRoomKnockWait
    {
        get => KnockWaitChoices.First(c => c.Wait == RoomKnockWait);
        set { if (value is not null) RoomKnockWait = value.Wait; }
    }

    /// <summary>The default for new rooms (multiplayer settings › invites and alerts).</summary>
    public KnockWaitChoice SelectedDefaultKnockWait
    {
        get => KnockWaitChoices.First(c => c.Wait == KnockWaits.Parse(_service.Settings.MultiplayerKnockWait));
        set
        {
            if (value is null) return;
            _service.Settings.MultiplayerKnockWait = value.Wait.ToSetting();
            _service.TrySaveSettings();
            OnPropertyChanged();
            if (_roomKnockWait is null) OnPropertyChanged(nameof(SelectedRoomKnockWait));
        }
    }

    // ---- invite card ----

    private (int Uses, TimeSpan? Lifetime, bool Auto)? _inviteTermsOf;

    internal void RememberInviteTerms(int uses, TimeSpan? lifetime, bool autoApprove)
    {
        _inviteTermsOf = (uses, lifetime, autoApprove);
        OnPropertyChanged(nameof(InviteTerms));
        OnPropertyChanged(nameof(InviteTermsFact));
    }

    /// <summary>"This invite: 1 use · valid for 10 minutes · asks you first". Written when read, so it follows the language.</summary>
    public string InviteTerms => _inviteTermsOf is not { } terms ? "" : L.F("multiplayer.invite.terms",
        terms.Uses == InviteBook.UnlimitedUses ? L.T("multiplayer.invite.use.unlimited") : L.P("multiplayer.invite.terms_uses", terms.Uses, terms.Uses),
        terms.Lifetime is { } life ? L.F("multiplayer.invite.terms_life", Span(life)) : L.T("multiplayer.invite.terms_room"),
        terms.Auto ? L.T("multiplayer.invite.terms_auto") : L.T("multiplayer.invite.terms_ask"));

    /// <summary>"10 minutes", "2 hours", "30 days": the largest whole unit.</summary>
    private static string Span(TimeSpan span) =>
        span.TotalDays >= 1 ? L.P("multiplayer.invite.days", (int)span.TotalDays, (int)span.TotalDays)
        : span.TotalHours >= 1 ? L.P("multiplayer.invite.hours", (int)span.TotalHours, (int)span.TotalHours)
        : L.P("multiplayer.invite.minutes", (int)span.TotalMinutes, (int)span.TotalMinutes);

    // ---- room settings drawer ----

    [ObservableProperty] public partial bool RoomSettingsOpen { get; set; }

    [RelayCommand]
    private void ToggleRoomSettings() => RoomSettingsOpen = !RoomSettingsOpen;

    // ---- "Connection problems?" drawer (official §6.18) ----

    /// <summary>The diagnostics drawer (lobby footer, room ⋯ menu, Ctrl+Shift+D).</summary>
    [ObservableProperty] public partial bool DiagnosticsOpen { get; set; }

    /// <summary>The drawer's "Try a download" (it used to be in the toolbox). Set once by the main view model.</summary>
    public ToolsViewModel? Tools { get; set; }

    [RelayCommand]
    private void OpenDiagnostics() => DiagnosticsOpen = true;

    [RelayCommand]
    private void CloseDiagnostics() => DiagnosticsOpen = false;

    /// <summary>The plain-language conclusion of the last check (its first line).</summary>
    public string NetworkVerdict => NetworkCheckText.Split('\n', 2)[0];

    /// <summary>What the check saw (addresses, NAT, STUN), under "technical details".</summary>
    public string NetworkDetails => NetworkCheckText.Split('\n', 2) is [_, var rest] ? rest : "";

    public bool HasNetworkVerdict => NetworkCheckText.Length > 0;

    /// <summary>"Try another relay server": Settings › Network › relay servers.</summary>
    [RelayCommand]
    private void SwitchRelay()
    {
        DiagnosticsOpen = false;
        Navigate?.Invoke(LegacyRoutes.RelaySettings);
    }

    [RelayCommand]
    private async Task CopyRoomInfoAsync()
    {
        var text = string.Join(Environment.NewLine, new[] { RoomHeaderText, HasVirtualIp ? VirtualIpLabel : null, TransportText }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (await Amber.App.Services.AvaloniaClipboard.TrySetTextAsync(text))
            SetStatus(StatusMessage.Success("multiplayer.room.info_copied"));
    }
}
