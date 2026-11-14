using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Identity;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>还能用的一张邀请（房主的“待用邀请”列表）。</summary>
public sealed record PendingInviteItem(PendingInvite Invite) : LocalizedRecord
{
    private bool _shown;

    /// <summary>显示完整的邀请码（默认打码；离开联机页时重新打码）。</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            OnPropertyChanged(nameof(Shown));
            OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => Invite.Unlimited
        ? L.F("multiplayer.invite.pending_item_unlimited", Shown ? Invite.Invite.ToString() : Invite.Invite.Masked, Until,
            Invite.AutoApprove ? L.T("multiplayer.invite.auto_suffix") : "")
        : L.F("multiplayer.invite.pending_item", Shown ? Invite.Invite.ToString() : Invite.Invite.Masked, Invite.UsesLeft, Invite.Uses, Until,
            Invite.AutoApprove ? L.T("multiplayer.invite.auto_suffix") : "");

    /// <summary>"valid until 21:40"; with the date when that is not today (a one-day invite).</summary>
    private string Until
    {
        get
        {
            if (Invite.ExpiresAt is not { } at) return L.T("multiplayer.invite.expiry.room");
            var local = at.ToLocalTime();
            return L.F("multiplayer.invite.expires_at", local.ToString(local.Date == DateTimeOffset.Now.Date ? "HH:mm" : "MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>有人敲门、等房主批准的卡片。120 秒没点算拒绝（卡片自己消失）。</summary>
public sealed record ApprovalCard(KnockRequest Request, string? Warning) : LocalizedRecord
{
    internal TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When the card is declined by itself (null: the room keeps waiting).</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>"Let them in automatically next time (add as a friend)".</summary>
    public bool TrustAfter { get; set; }

    /// <summary>The checkbox is hidden in child mode (every knock is asked).</summary>
    public bool CanTrustAfter { get; init; } = true;

    public string Who => Request.Name.Length > 0 ? Request.Name : L.T("multiplayer.peers.player");
    public string Headline => L.F("multiplayer.knock.wants_in", Who);
    public string SafetyCode => Request.Fingerprint;
    public string Facts => string.Join(" · ", new[] { Request.Version, Request.DeviceId is { } device ? L.F("multiplayer.device.id", device) : null,
            MultiplayerViewModel.JayLine(Request.JayAccount) }
        .Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>The guest showed a Jay account: trusting them covers their other devices too.</summary>
    public bool HasJayAccount => Request.JayAccount is not null && CanTrustAfter;
    public string JayHint => L.T("multiplayer.knock.jay_hint");
    public string VerifyHint => L.F("multiplayer.knock.verify", Who);
    public string TrustLabel => L.F("multiplayer.knock.trust_after", Who);

    /// <summary>"9:47 left" or "waiting".</summary>
    public string RemainingText => Deadline is { } d
        ? L.F("multiplayer.knock.remaining", (d - DateTimeOffset.Now) is var left && left > TimeSpan.Zero ? $"{(int)left.TotalMinutes}:{left.Seconds:D2}" : "0:00")
        : L.T("multiplayer.knock.waiting_forever");

    internal void Tick() => OnPropertyChanged(nameof(RemainingText));
    public string Title => L.F("multiplayer.invite.knocking", Request.Name.Length > 0 ? Request.Name : L.T("multiplayer.peers.player"));
    public string Detail => L.F("multiplayer.invite.fingerprint", Request.Fingerprint) + (Request.Version.Length > 0 ? " · " + Request.Version : "")
        + (Request.DeviceId is { } device ? " · " + L.F("multiplayer.device.id", device) : "")
        + (MultiplayerViewModel.JayLine(Request.JayAccount) is { } jay ? " · " + jay : "");
    public bool HasWarning => Warning is not null;
}

/// <summary>经邀请进来的成员（房主看）。发出凭据时就列上；对方回了确认再标“已确认”。换网络时不在线的标“需要重新邀请”。</summary>
public sealed record MemberItem(string Name, byte[] PublicKey, bool Trusted, bool Confirmed = false, bool NeedsInvite = false) : LocalizedRecord
{
    /// <summary>Their Jay account, verified when they knocked.</summary>
    public string? JayAccount { get; init; }
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
    public string Label => (Name.Length > 0 ? Name : L.T("multiplayer.peers.player")) + " · " + Fingerprint
        + (MultiplayerViewModel.JayLine(JayAccount) is { } jay ? " · " + jay : "")
        + " · " + L.T(NeedsInvite ? "multiplayer.kick.needs_invite" : Confirmed ? "multiplayer.invite.member_confirmed" : "multiplayer.invite.member_unconfirmed");
    public bool CanTrust => !Trusted;
}

/// <summary>这个房间里踢出的人（房主看，可以解除）。</summary>
public sealed record BannedItem(BannedMember Member) : LocalizedRecord
{
    public string Label => (Member.Name.Length > 0 ? Member.Name : L.T("multiplayer.peers.player")) + " · " + Member.Fingerprint;
}

/// <summary>信任列表里的一个人（联机设置里管理）。</summary>
public sealed record TrustedItem(TrustedMember Member) : LocalizedRecord
{
    public string Label => (Member.Name.Length > 0 ? Member.Name : L.T("multiplayer.peers.player")) + " · " + Member.Fingerprint
        + (MultiplayerViewModel.JayLine(Member.JayAccount) is { } jay ? " · " + jay : "");
}

/// <summary>
/// 联机页的邀请加入：房主发 JAY2- 邀请码，朋友贴上后先进“大厅”敲门，房主批准才在加密通道里拿到房间的凭据。
/// 房间本身的网络名和密钥是随机的，从不显示、复制或保存。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private MemberIdentity? _identity;
    private TrustedMembers? _trusted;
    private HostLobby? _lobby;
    private RoomCredentials? _credentials;
    private MemberTable? _members;
    private CancellationTokenSource? _knockCts;
    private JayInvite? _lastInvite;

    /// <summary>这台电脑的联机身份（第一次用到时读取或新建）。</summary>
    internal MemberIdentity Identity
    {
        get
        {
            if (_identity is not null) return _identity;
            _identity = MemberIdentity.LoadOrCreate(AppSettings.DataDir);
            if (_identity.Warning is { } warning) SetStatus(new StatusMessage("common.raw", [warning], StatusKind.Warning));
            return _identity;
        }
    }

    private TrustedMembers Trusted => _trusted ??= CreateTrusted();

    private TrustedMembers CreateTrusted()
    {
        var trusted = new TrustedMembers(Path.Combine(AppSettings.DataDir, "trusted.json"));
        trusted.Changed += () => Dispatcher.UIThread.Post(RefreshTrusted);
        return trusted;
    }

    public string MyFingerprint => L.F("multiplayer.identity.mine", Identity.Fingerprint);

    public ObservableCollection<PendingInviteItem> PendingInvites { get; } = [];
    public ObservableCollection<ApprovalCard> ApprovalCards { get; } = [];
    public ObservableCollection<MemberItem> Members { get; } = [];
    public ObservableCollection<TrustedItem> TrustedPeople { get; } = [];
    public ObservableCollection<BannedItem> BannedPeople { get; } = [];

    /// <summary>房主正在踢人、换网络（这期间不能再踢）。</summary>
    [ObservableProperty] public partial bool IsRekeying { get; set; }

    /// <summary>测试用：当前房间网络的版本号。</summary>
    internal int RoomEpoch => _room?.Epoch ?? 0;

    /// <summary>测试用：换一个联机身份（自检里同一个进程开好几个成员，要各自不同的身份）。</summary>
    internal void UseTestIdentity(string directory) => _identity = MemberIdentity.LoadOrCreate(directory);

    [ObservableProperty] public partial bool InviteDialogOpen { get; set; }
    [ObservableProperty] public partial bool InviteAutoApprove { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InviteDisplay), nameof(HasInvite))]
    public partial bool InviteShown { get; set; }

    public bool HasInvite => _lastInvite is not null;
    /// <summary>
    /// Every room, direct or not, shares the same plain invite (JAY2-): it holds no address. A direct room's addresses go
    /// only to approved guests, inside the encrypted knock. Masked by default.
    /// </summary>
    public string InviteDisplay => _lastInvite is not { } invite ? "" : InviteShown ? invite.ToString() : invite.Masked;

    /// <summary>加入方：正在进大厅 / 敲门 / 等房主批准。</summary>
    [ObservableProperty] public partial bool IsKnocking { get; set; }

    /// <summary>加入方：房主的指纹（敲门握手里证明过的身份）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHostFingerprint), nameof(HostFingerprintText))] public partial string HostFingerprint { get; set; } = "";
    public bool HasHostFingerprint => HostFingerprint.Length > 0;
    public string HostFingerprintText => L.F("multiplayer.invite.host_fingerprint", HostFingerprint)
        + (JayLine(HostJayAccount) is { } jay ? " · " + jay : "");

    /// <summary>The host's Jay account, when they sent a valid certificate with the approval.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HostFingerprintText))] public partial string? HostJayAccount { get; set; }

    /// <summary>测试用：最近一次发出的完整邀请码（界面上默认打码）。</summary>
    internal string? TestInviteText => _lastInvite?.ToString();

    private void RefreshTrusted()
    {
        TrustedPeople.Clear();
        foreach (var m in Trusted.All) TrustedPeople.Add(new TrustedItem(m));
    }

    private void RefreshInvites()
    {
        PendingInvites.Clear();
        if (_lobby is null) return;
        // 公共网络的长期邀请由“公共网络”那一栏管，不列在这里。
        foreach (var invite in _lobby.Book.Pending.Where(i => !i.Standing)) PendingInvites.Add(new PendingInviteItem(invite));
    }

    // ---- 房主 ----

    /// <summary>开房间时调用：生成这个房间的随机凭据。</summary>
    /// <remarks>The whole-LAN scope doesn't split in this version, so a room opened with it carries no split settings.</remarks>
    private RoomCredentials NewCredentials(int port, TunnelSettings tunnel) =>
        RoomCredentials.Create(Identity.PublicKey, port, tunnel) with { Split = SplitSettings.ForNewRoom(tunnel.Wraps, FullLanMode, SplitSettings.Requested.FitTo(TestPeers ?? _service.MultiplayerPeers)) };

    private IReadOnlyList<string> LobbyPeers(bool host) =>
        [.. (host ? TestListeners ?? [] : []), .. TestPeers ?? _service.MultiplayerPeers];

    /// <summary>
    /// A "No server" room reaches no relay, its knock lobby included: guests knock over the direct bridge, which hands
    /// them to the lobby on loopback. Only the tests' local listeners are kept.
    /// </summary>
    private IReadOnlyList<string> ServerlessLobbyPeers() => [.. TestListeners ?? []];

    /// <summary>Tests: prepares the host's knock lobby as opening a room does (it starts nothing until an invite exists).</summary>
    internal void StartHostLobbyForTest(RoomCredentials credentials, MemberTable members, InviteBook? book = null, DirectSession? direct = null) =>
        StartHostLobby(credentials, members, book, direct?.LobbyUdpPort, direct: direct);

    /// <summary>Tests: closes the host's knock lobby.</summary>
    internal void StopHostLobbyForTest() => StopHostLobby();

    /// <summary>Tests: the nodes the host's knock lobby would connect to.</summary>
    internal IReadOnlyList<string>? HostLobbyPeersForTest => _hostLobbyPeers?.Invoke();
    private Func<IReadOnlyList<string>>? _hostLobbyPeers;

    /// <summary>房间开好之后：准备大厅（有邀请时才真的启动）。</summary>
    /// <param name="book">直连房间：大厅密钥不换的邀请簿。</param>
    /// <param name="listenUdpPort">直连房间：大厅 EasyTier 在本机回环上监听的 UDP 端口（直连桥把没批准的加入方转发到这里）。</param>
    /// <param name="peers">The nodes the lobby connects to; the room's nodes by default. A direct room ("No server") never takes any, whatever is passed.</param>
    /// <param name="direct">
    /// Direct rooms: approved guests get this session's addresses inside the encrypted knock reply, and the addresses they
    /// send back in the encrypted ack start the host's side of the punch.
    /// </param>
    private void StartHostLobby(RoomCredentials credentials, MemberTable members, InviteBook? book = null, int? listenUdpPort = null, Func<IReadOnlyList<string>>? peers = null,
        DirectSession? direct = null)
    {
        _credentials = credentials;
        _members = members;
        ConfigureSecurity(members);
        var jayCertificate = JayCertificate;
        var lobbyPeers = direct is not null ? ServerlessLobbyPeers : peers ?? (() => LobbyPeers(host: true));
        _hostLobbyPeers = lobbyPeers;
        var lobby = HostLobby.Create(_service.EasyTier, Path.Combine(EasyTierWorkRoot, "lobby"), lobbyPeers,
            Identity.KeyPair, () => _credentials ?? throw new InvalidOperationException(), pub => !Child.AlwaysAskKnock && Trusted.IsTrusted(pub), AskApprovalAsync, members,
            line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line), book, listenUdpPort, () => HostDevicePolicy, () => HostAccountPolicy,
            () => RoomKnockWait.ToTimeSpan(), request => Dispatcher.UIThread.Post(() => OnKnockTimedOut(request)),
            account => !Child.AlwaysAskKnock && Trusted.IsTrustedJayAccount(account), () => jayCertificate, () => Child.AlwaysAskKnock,
            direct is null ? null : direct.Offer,
            name => Dispatcher.UIThread.Post(() => OnRelayOnlyRefused(name)));
        if (direct is not null)
            lobby.DirectAnswered += (request, entry, addresses) => _ = AddDirectGuestAsync(direct, request, addresses);
        lobby.Book.Changed += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.StateChanged += () => Dispatcher.UIThread.Post(RefreshInvites);
        lobby.Paused += () => Dispatcher.UIThread.Post(() => SetStatus(StatusMessage.Warning("multiplayer.invite.paused")));
        // 发出凭据时就列上（对方不回确认也已经能进房间）；确认后改成“已确认”。同一个人重新敲门进来时替换旧的那行。
        lobby.Issued += (request, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = Members.Count - 1; i >= 0; i--)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey)) Members.RemoveAt(i);
            Members.Insert(0, new MemberItem(request.Name, request.PublicKey, request.Trusted) { JayAccount = request.JayAccount });
            SeenJayDevice(request.PublicKey, request.JayDevice);
            // A trusted friend on a new device: remember this device too, so the list shows it.
            if (request.TrustedByAccount)
            {
                Trusted.Add(request.PublicKey, request.Name, request.JayAccount);
                SetStatus(StatusMessage.Success("multiplayer.invite.admitted_account", request.Name, JayAddress.Short(request.JayAccount!)));
            }
            else SetStatus(StatusMessage.Success("multiplayer.invite.admitted", request.Name, request.Fingerprint));
        });
        // 凭据没发出去（连接断了）：钥匙已经撤回，列表里也去掉。
        lobby.Withdrawn += (request, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = Members.Count - 1; i >= 0; i--)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey) && !Members[i].Confirmed) Members.RemoveAt(i);
        });
        lobby.Admitted += request => Dispatcher.UIThread.Post(() =>
        {
            if (_lobby != lobby) return;
            for (var i = 0; i < Members.Count; i++)
                if (Members[i].PublicKey.AsSpan().SequenceEqual(request.PublicKey)) Members[i] = Members[i] with { Confirmed = true };
        });
        _lobby = lobby;
    }

    /// <summary>
    /// Tests and self-checks: a host lobby that never leaves this computer. No multiplayer component is started and the
    /// knock service listens on a free loopback port, so invites can be made, listed and revoked like in a real room.
    /// </summary>
    private void StartTestLobby(RoomCode room)
    {
        var members = new MemberTable();
        _credentials = NewCredentials(room.Port, room.Tunnel);
        _members = members;
        var lobby = new HostLobby(_ => new StandInLobby(), () => [],
            book => new KnockServer(book, Identity.KeyPair, () => _credentials ?? throw new InvalidOperationException(), _ => false,
                (_, _) => Task.FromResult(false), System.Net.IPAddress.Loopback, EasyTierArgs.FreeTcpPort(), null, members: members));
        lobby.Book.Changed += () => Dispatcher.UIThread.Post(RefreshInvites);
        _lobby = lobby;
    }

    private sealed class StandInLobby : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private void StopHostLobby()
    {
        var lobby = _lobby;
        _lobby = null;
        _hostLobbyPeers = null;
        _credentials = null;
        _members?.Clear();
        _members = null;
        ForgetShownInvite();
        InviteDialogOpen = false;
        foreach (var card in ApprovalCards) card.Decision.TrySetResult(false);
        ApprovalCards.Clear();
        PendingInvites.Clear();
        Members.Clear();
        BannedPeople.Clear();
        if (lobby is not null) _ = lobby.DisposeAsync().AsTask();
    }

    private void RefreshBanned()
    {
        BannedPeople.Clear();
        if (_members is null) return;
        foreach (var b in _members.Banned) BannedPeople.Add(new BannedItem(b));
    }

    /// <summary>
    /// 踢人：移出成员表（钥匙清零，他的游戏通道和聊天马上断开）、记进这个房间的禁入名单，然后换网络：
    /// 新的随机网络名和密钥（版本加一）经每个留下来的成员自己的通道发过去，等确认最多 5 秒，房主再切过去。旧网络不留。
    /// 当时不在线的成员收不到新网络，钥匙作废，界面上标“需要重新邀请”。
    /// </summary>
    [RelayCommand]
    private async Task KickMemberAsync(MemberItem? item)
    {
        if (item is null || IsRekeying || !IsHosting || _members is not { } members || _credentials is not { } credentials || _hub is not { } hub
            || _room is not { FromInvite: true } room) return;
        IsRekeying = true;
        try
        {
            // 连他进来用的那张邀请一起作废（禁入名单按公钥记，换个身份就能拿同一张再进来）。
            var inviteRevoked = false;
            if (members.FindGuest(item.PublicKey) is { } entry)
            {
                // 先告诉他“你已被移出房间”（他那边停止联机），等他确认收到（最多 2 秒，旧客户端不回确认就等满）再作废钥匙断开。
                await hub.NotifyKickedAsync(entry);
                inviteRevoked = members.Kick(entry, _lobby?.Book);
            }
            else members.Ban(item.PublicKey, item.Name);
            Members.Remove(item);
            RefreshInvites();
            RefreshBanned();
            var next = credentials.Rotate();
            _credentials = next;
            SetStatus(StatusMessage.Info("multiplayer.rekey.switching", item.Name));
            var (sent, _) = await hub.SendRekeyAsync(next.ToRekey(), TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(_room, room)) return;
            var missed = members.All.Where(e => !e.IsHost && !sent.Contains(e)).ToList();
            foreach (var e in missed) members.Remove(e);
            for (var i = 0; i < Members.Count; i++)
                if (missed.Any(e => e.PublicKey.AsSpan().SequenceEqual(Members[i].PublicKey))) Members[i] = Members[i] with { NeedsInvite = true };
            if (!SwitchNetwork(room.Rekeyed(next.ToRekey()))) return;
            SetStatus(missed.Count > 0 && inviteRevoked ? StatusMessage.Warning("multiplayer.kick.invite_revoked_missed", item.Name, missed.Count)
                : missed.Count > 0 ? StatusMessage.Warning("multiplayer.rekey.done_missed", item.Name, missed.Count)
                : inviteRevoked ? StatusMessage.Warning("multiplayer.kick.invite_revoked", item.Name)
                : StatusMessage.Success("multiplayer.rekey.done", item.Name));
        }
        finally
        {
            IsRekeying = false;
        }
    }

    [RelayCommand]
    private void Unban(BannedItem? item)
    {
        if (item is null || _members is not { } members) return;
        members.Unban(item.Member.PublicKey);
        RefreshBanned();
        SetStatus(StatusMessage.Info("multiplayer.kick.unbanned", item.Member.Name));
    }

    /// <summary>
    /// 换到新的房间网络：关掉旧的 EasyTier 和封装通道，按同样的设置用新网络名和密钥重开（旧网络不留）。
    /// 房主的聊天服务和本机聊天连接不动；加入方的聊天连接经旧网络，丢掉重连。
    /// </summary>
    private bool SwitchNetwork(RoomCode next)
    {
        // 换网络：远程控制会话一律断开（经旧网络的连接本来也要断）。
        StopAllRemote();
        if (_direct is { } direct)
        {
            // 直连房间：直连桥不动（钥匙和房间无关），只让直连会话换上新房间重启 EasyTier。
            _room = next;
            _ = SwitchDirectRoomAsync(direct, next);
            AfterNetworkSwitch();
            return true;
        }
        if (_session is not { } old) return false;
        var options = old.Options with { Room = next };
        StopTransport();
        old.Dispose();
        _session = null;
        _service.ActiveMultiplayer = null;
        try
        {
            var workDir = Path.Combine(EasyTierWorkRoot, options.Tun ? "run-lan" : "run");
            _session = options.Tun
                ? EasyTierElevatedSession.Start(_service.EasyTier, options, workDir)
                : EasyTierSession.Start(_service.EasyTier, options, workDir);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            StopSession();
            SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
            return false;
        }
        _service.ActiveMultiplayer = _session;
        if (!TryStartTransport(_session?.Options ?? options))
        {
            StopSession();
            return false;
        }
        _room = next;
        AfterNetworkSwitch();
        return true;
    }

    private async Task SwitchDirectRoomAsync(DirectSession direct, RoomCode next)
    {
        try
        {
            await direct.SwitchRoomAsync(next);
            if (_direct == direct) _session = direct.EasyTier;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            if (_direct == direct) SetStatus(StatusMessage.Failure("multiplayer.room.start_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>换了网络之后：加入方的聊天连接经旧网络，丢掉重连。</summary>
    private void AfterNetworkSwitch()
    {
        if (IsJoined)
        {
            var chat = _chat;
            _chat = null;
            StopPrivate();
            ChatReady = false;
            ChatStatus = L.T("multiplayer.chat.reconnecting");
            if (chat is not null) _ = chat.DisposeAsync().AsTask();
            MarkJoinStarted(newRoom: true);
        }
    }

    /// <summary>加入方：被房主踢出：停止联机（关掉 EasyTier 和聊天），告诉用户。</summary>
    private void OnKicked(ChatClient client)
    {
        if (_chat != client || !IsJoined || _room is not { FromInvite: true }) return;
        StopSession();
        SetStatus(StatusMessage.Warning("multiplayer.kick.you_were_kicked"));
    }

    /// <summary>加入方：房主换了网络（有人被踢）。经自己的成员通道收到、版本比当前大才换。</summary>
    private void OnRekey(ChatClient client, RoomRekey next)
    {
        if (_chat != client || !IsJoined || _room is not { FromInvite: true } room || next.Epoch <= room.Epoch) return;
        SetStatus(StatusMessage.Info("multiplayer.rekey.reconnecting"));
        SwitchNetwork(room.Rekeyed(next));
    }

    /// <summary>敲门的人通过了握手：在界面上放一张卡片，等房主点；超时或者房间关了算拒绝。</summary>
    internal Task<bool> AskApprovalAsync(KnockRequest request, CancellationToken ct)
    {
        var sameName = Trusted.All.Any(m => m.Name.Length > 0 && m.Name == request.Name && !m.PublicKey.AsSpan().SequenceEqual(request.PublicKey));
        var wait = RoomKnockWait.ToTimeSpan();
        var card = new ApprovalCard(request, sameName ? L.T("multiplayer.invite.name_mismatch") : null)
        {
            Deadline = wait == Timeout.InfiniteTimeSpan ? null : DateTimeOffset.Now + wait,
            CanTrustAfter = !Child.AlwaysAskKnock,
        };
        ct.Register(() => card.Decision.TrySetResult(false));
        Dispatcher.UIThread.Post(() =>
        {
            if (card.Decision.Task.IsCompleted) return;
            ApprovalCards.Add(card);
            Services.TutorialService.Request(Amber.Core.Tutorial.Tips.FirstKnock);
            if (!IsCurrent) Badge++;
            _ = card.Decision.Task.ContinueWith(_ => Dispatcher.UIThread.Post(() => ApprovalCards.Remove(card)), TaskScheduler.Default);
        });
        return card.Decision.Task;
    }

    [RelayCommand]
    private void ApproveKnock(ApprovalCard? card)
    {
        if (card is null || card.Decision.Task.IsCompleted) return;
        // "Let them in automatically next time": add to friends before answering.
        if (card.TrustAfter && card.CanTrustAfter && !Trusted.IsTrusted(card.Request.PublicKey))
        {
            Trusted.Add(card.Request.PublicKey, card.Request.Name, card.Request.JayAccount);
            RefreshTrusted();
        }
        card.Decision.TrySetResult(true);
    }

    [RelayCommand]
    private void DenyKnock(ApprovalCard? card) => card?.Decision.TrySetResult(false);

    [RelayCommand]
    private void OpenInviteDialog()
    {
        if (!IsHosting || _lobby is null) return;
        InviteDialogOpen = true;
    }

    [RelayCommand]
    private void CloseInviteDialog() => InviteDialogOpen = false;

    /// <summary>
    /// [Create invite] in the room's invite panel, with the two choices next to it: how long, and once or unlimited.
    /// Thirty days makes a long-lived invite, which every room I open accepts.
    /// </summary>
    [RelayCommand]
    private async Task CreateInviteAsync()
    {
        var life = SelectedInviteLife.Life;
        if (InviteOffer.IsLongLived(life))
        {
            await CreateLongInviteAsync();
            return;
        }
        var uses = InviteOffer.Uses(InviteOffer.Normalize(life, SelectedInviteUse.Use));
        if (await IssueInviteAsync(uses, InviteOffer.Lifetime(life), InviteAutoApproveNow) is not null)
            SetStatus(StatusMessage.Success(IsDirect ? "multiplayer.direct.invite_created" : "multiplayer.invite.created"));
    }

    /// <summary>Self-checks: an invite with exact terms, as the panel's button would make it.</summary>
    internal async Task<bool> CreateInviteForTestAsync(int uses, TimeSpan? lifetime, bool autoApprove) =>
        await IssueInviteAsync(uses, lifetime, autoApprove) is not null;

    /// <summary>
    /// 发一张邀请、确保大厅开着。直连房间也发同样的邀请：码里没有地址。
    /// 大厅开不起来时撤掉这张邀请、显示原因，返回 null。
    /// </summary>
    private async Task<PendingInvite?> IssueInviteAsync(int uses, TimeSpan? lifetime, bool autoApprove)
    {
        if (_lobby is not { } lobby || _credentials is not { } credentials) return null;
        // An invite without a use limit always asks me first, whatever the caller remembered.
        autoApprove = Child.MayAutoAdmit(autoApprove) && uses != InviteBook.UnlimitedUses;
        lobby.Book.Game = await HostGameAsync();
        if (_lobby != lobby) return null;
        var invite = lobby.Book.Create(uses, lifetime, autoApprove, credentials.Tunnel);
        _lastInvite = invite.Invite;
        _lastPending = invite;
        _lastLongInvite = null;
        _longInviteWaits = false;
        RememberInviteTerms(uses, lifetime, autoApprove);
        InviteShown = false;
        OnPropertyChanged(nameof(InviteDisplay));
        OnPropertyChanged(nameof(HasInvite));
        RefreshInvites();
        NotifyInvitePanel();
        try
        {
            await lobby.UpdateAsync();
            return invite;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 敲门端口被占（比如另一个 Amber 正在当房主）：这张邀请没法用，撤掉。
            lobby.Book.Revoke(invite);
            ForgetShownInvite();
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
            return null;
        }
    }

    [RelayCommand]
    private void ToggleInviteShown() => InviteShown = !InviteShown;

    [RelayCommand]
    private void TogglePendingInviteShown(PendingInviteItem? item)
    {
        if (item is not null) item.Shown = !item.Shown;
    }

    /// <summary>输入框里粘贴的码默认也打码显示。</summary>
    [ObservableProperty] public partial bool JoinCodeShown { get; set; }

    [RelayCommand] private void ToggleJoinCodeShown() => JoinCodeShown = !JoinCodeShown;

    /// <summary>离开联机页：所有码重新打码。</summary>
    protected override void OnLeftCurrent() => MaskAllCodes();

    internal void MaskAllCodes()
    {
        InviteShown = false;
        JoinCodeShown = false;
        foreach (var item in PendingInvites) item.Shown = false;
        OnPropertyChanged(nameof(InviteDisplay));
    }

    [RelayCommand]
    private async Task CopyInviteAsync()
    {
        if (_lastInvite is not { } invite) return;
        if (await SecretClipboard.CopyAsync(invite.ToString()))
            SetStatus(StatusMessage.Success("multiplayer.invite.copied"));
    }

    [RelayCommand]
    private void RevokeInvite(PendingInviteItem? item)
    {
        if (item is null || _lobby is not { } lobby) return;
        lobby.Book.Revoke(item.Invite);
        RefreshInvites();
        if (_lastInvite == item.Invite.Invite) ForgetShownInvite();
        SetStatus(StatusMessage.Info("multiplayer.invite.revoked"));
    }

    [RelayCommand]
    private void TrustMember(MemberItem? member)
    {
        if (member is null || member.Trusted) return;
        Trusted.Add(member.PublicKey, member.Name, member.JayAccount);
        RefreshTrusted();
        var index = Members.IndexOf(member);
        if (index >= 0) Members[index] = member with { Trusted = true };
        SetStatus(StatusMessage.Success("multiplayer.invite.trusted", member.Name, member.Fingerprint));
    }

    [RelayCommand]
    private void RemoveTrusted(TrustedItem? item)
    {
        if (item is null) return;
        Trusted.Remove(item.Member.PublicKey);
        RefreshTrusted();
    }

    [RelayCommand]
    private void ResetIdentity()
    {
        // 开着房间或在别人房间里时不许换（正在进行的握手和房主记住的指纹都基于当前身份）。
        if (!IsIdle || IsKnocking) return;
        Identity.Reset();
        OnPropertyChanged(nameof(MyFingerprint));
        SetStatus(StatusMessage.Success("multiplayer.identity.reset_done", Identity.Fingerprint));
    }

    // ---- 加入方 ----

    /// <summary>贴了邀请码：进大厅、敲门、等批准；批准后离开大厅，用拿到的凭据加入房间。</summary>
    /// <param name="knock">从公共网络的房间列表加入时：经公共网络敲门。不给时先在公共网络的目录里找（连着时），找不到再进邀请码的大厅。</param>
    /// <returns>How it ended, for callers that keep the invite around to try again.</returns>
    private async Task<KnockEnd> KnockAndJoinAsync(JayInvite invite,
        Func<X25519KeyPair, string, Func<DirectAddresses, CancellationToken, Task<DirectAddresses?>>, CancellationToken, Task<KnockOutcome>>? knock = null,
        byte[]? expectedHost = null)
    {
        IsKnocking = true;
        var cts = new CancellationTokenSource();
        _knockCts = cts;
        var peers = LobbyPeers(host: false);
        SetStatus(StatusMessage.Info(peers.Count > 0 ? "multiplayer.invite.connecting_relay" : "multiplayer.invite.entering_lobby"));
        try
        {
            var me = Identity.KeyPair;
            var name = ChatName;
            var device = Device;
            var jay = JayCertificate;
            // Direct rooms: the host sends its addresses only after approving, inside the encrypted reply. Then (and only
            // then) a direct session is made here and our addresses go back in the encrypted ack.
            DirectSession? guestDirect = null;
            Func<DirectAddresses, CancellationToken, Task<DirectAddresses?>> answerDirect =
                (host, ct) => AnswerDirectOfferAsync(invite, host, made => guestDirect = made, ct);
            KnockOutcome? viaPublic = null;
            var lookedAt = DateTimeOffset.UtcNow;
            if (knock is not null || _public is not null)
            {
                SetStatus(StatusMessage.Info("multiplayer.invite.waiting_approval"));
                viaPublic = knock is not null ? await knock(me, name, answerDirect, cts.Token) : await TryPublicKnockAsync(invite, me, name, cts.Token, expectedHost, answerDirect);
                if (viaPublic is null && _knockCts == cts) SetStatus(StatusMessage.Info(peers.Count > 0 ? "multiplayer.invite.connecting_relay" : "multiplayer.invite.entering_lobby"));
            }
            KnockOutcome outcome;
            try
            {
                outcome = viaPublic ?? await Task.Run(() => LobbyJoin.KnockAsync(_service.EasyTier, Path.Combine(EasyTierWorkRoot, "lobby-join"), peers, invite,
                    me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, line => _diag.Add(L.T("multiplayer.invite.diagnostics_category"), line),
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        if (_knockCts == cts) SetStatus(StatusMessage.Info("multiplayer.invite.waiting_approval"));
                    }), cts.Token, () => Dispatcher.UIThread.Post(() =>
                    {
                        if (_knockCts == cts) SetStatus(StatusMessage.Info("multiplayer.invite.entering_lobby"));
                    }), device, AccountToken, jay, expectedHost, answerDirect));
            }
            // Only "the lobby was reached but no host was there" looks again: not "no relay reachable", and never a timeout
            // after the knock was sent (such as waiting for approval).
            catch (LobbyNoHostException e) when (knock is null && viaPublic is null && _public is not null && !cts.IsCancellationRequested)
            {
                // No host in the invite's lobby. A room opened after our copy of the directory was taken isn't in it yet:
                // look once more in a copy refreshed since (by then there usually is one, so this rarely waits).
                if (_knockCts == cts) SetStatus(StatusMessage.Info("multiplayer.invite.finding_host"));
                var again = await TryPublicKnockAsync(invite, me, name, cts.Token, expectedHost, answerDirect, lookedAt);
                if (again is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
                outcome = again;
            }
            if (_knockCts != cts)
            {
                if (guestDirect is not null) await guestDirect.DisposeAsync();
                return KnockEnd.NotAnswered;
            }
            if (outcome.Credentials is not { } credentials)
            {
                if (guestDirect is not null) await guestDirect.DisposeAsync();
                SetStatus(StatusMessage.Warning(KnockReasonKey(outcome.Reason)));
                return outcome.Reason is "expired" or "invalid" or "removed" or "closed" ? KnockEnd.InviteSpent : KnockEnd.NotAnswered;
            }
            if (credentials.Tunnel.Mode == TransportMode.Extreme && !NoiseHandshake.HybridSupported)
            {
                if (guestDirect is not null) await guestDirect.DisposeAsync();
                SetStatus(StatusMessage.Warning("multiplayer.room.extreme_unsupported"));
                return KnockEnd.NotAnswered;
            }
            IsKnocking = false;
            if (guestDirect is not null)
            {
                // A failed punch leaves the room: the invite may still work (a friend's has more uses), so it stays.
                return await CompleteDirectJoinAsync(guestDirect, credentials.ToRoom(), outcome) ? KnockEnd.Joined : KnockEnd.NotAnswered;
            }
            if (!StartJoin(credentials.ToRoom())) return KnockEnd.NotAnswered;
            HostFingerprint = outcome.HostFingerprint;
            HostJayAccount = outcome.HostJayAccount;
            SeenJayDevice(outcome.HostPublicKey, outcome.HostJayDevice);
            return KnockEnd.Joined;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus(StatusMessage.Info("multiplayer.invite.cancelled"));
        }
        catch (TimeoutException e)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Cryptography.CryptographicException
            or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // A host that is not the friend the invite names: the invite is no use any more.
            var wrongHost = e is WrongHostException;
            SetStatus(StatusMessage.Failure("multiplayer.invite.knock_failed", e, ErrorText.Of(e)));
            if (wrongHost) return KnockEnd.InviteSpent;
        }
        finally
        {
            if (_knockCts == cts)
            {
                _knockCts = null;
                IsKnocking = false;
            }
            cts.Dispose();
        }
        return KnockEnd.NotAnswered;
    }

    /// <summary>How a knock ended.</summary>
    internal enum KnockEnd
    {
        /// <summary>Not in the room, and the invite may still work: no connection, cancelled, declined this time, the room full.</summary>
        NotAnswered,
        /// <summary>In the room.</summary>
        Joined,
        /// <summary>The host says the invite is over (run out, I was removed, the room closed), or the host is not who the invite names.</summary>
        InviteSpent,
    }

    [RelayCommand]
    private void CancelKnock() => _knockCts?.Cancel();

    /// <summary>Host of a direct room: an approved guest sent its addresses inside the encrypted knock ack; punch towards it.</summary>
    private async Task AddDirectGuestAsync(DirectSession direct, KnockRequest request, DirectAddresses addresses)
    {
        try
        {
            var ok = await direct.AddGuestAsync(addresses);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_direct != direct) return;
                SetStatus(ok ? StatusMessage.Success("multiplayer.direct.guest_connected", request.Name)
                    : StatusMessage.Warning("multiplayer.direct.guest_relay", request.Name));
            });
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            _diag.Add(L.T("multiplayer.invite.diagnostics_category"), e.Message);
        }
    }

    /// <summary>敲门没成功的原因 → 界面文字。</summary>
    internal static string KnockReasonKey(string? reason) => reason switch
    {
        "expired" => "multiplayer.invite.reason.expired",
        "invalid" => "multiplayer.invite.reason.expired",
        "full" => "multiplayer.invite.reason.full",
        "removed" => "multiplayer.invite.reason.removed",
        "closed" => "multiplayer.invite.reason.closed",
        "mismatch" => "multiplayer.public.reason.mismatch",
        "password" => "multiplayer.public.reason.password",
        "device" => "multiplayer.device.reason",
        "account" => "multiplayer.account.reason",
        _ => "multiplayer.invite.reason.denied",
    };

    private readonly Dictionary<string, DateTimeOffset> _relayOnlyRefusedShown = new(StringComparer.Ordinal);

    /// <summary>
    /// An older Jay was turned away from my "relays only" room. The name is the guest's own, so the line shows at
    /// most once a minute per name and the list of names stays small.
    /// </summary>
    internal void OnRelayOnlyRefused(string name)
    {
        var now = DateTimeOffset.UtcNow;
        if (_relayOnlyRefusedShown.TryGetValue(name, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        if (_relayOnlyRefusedShown.Count >= 32)
            foreach (var old in _relayOnlyRefusedShown.Where(p => now - p.Value >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList())
                _relayOnlyRefusedShown.Remove(old);
        if (_relayOnlyRefusedShown.Count >= 32) return;
        _relayOnlyRefusedShown[name] = now;
        SetStatus(StatusMessage.Warning("multiplayer.room.relay_only_old", name));
    }
}
