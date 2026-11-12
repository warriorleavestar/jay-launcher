using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace Amber.App.ViewModels;

/// <summary>房主在公共网络上怎么出现（下拉框的一项）。</summary>
public sealed record VisibilityChoice(RoomVisibility Mode) : LocalizedRecord
{
    public string Label => L.T(Mode switch
    {
        RoomVisibility.Public => "multiplayer.public.visibility.public",
        RoomVisibility.Password => "multiplayer.public.visibility.password",
        RoomVisibility.Direct => "multiplayer.public.visibility.direct",
        _ => "multiplayer.public.visibility.hidden",
    });
    public string Detail => L.T(Mode switch
    {
        RoomVisibility.Public => "multiplayer.public.visibility.public_detail",
        RoomVisibility.Password => "multiplayer.public.visibility.password_detail",
        RoomVisibility.Direct => "multiplayer.public.visibility.direct_detail",
        _ => "multiplayer.public.visibility.hidden_detail",
    });
    public override string ToString() => Label;
}

/// <summary>房间列表里的一行。</summary>
public sealed record PublicRoomItem(RoomListing Listing) : LocalizedRecord
{
    public string Title => (Listing.Name.Length > 0 ? Listing.Name : L.T("multiplayer.public.unnamed")) + (Listing.Mode == RoomVisibility.Password ? " 🔒" : "");
    public string Meta => string.Join(" · ", new[]
    {
        Listing.Host.Length > 0 ? Listing.Host : null,
        Listing.Game.Length > 0 ? Listing.Game : null,
        L.P("multiplayer.public.players", Listing.Players, Listing.Players),
        L.T(Listing.Mode switch
        {
            RoomVisibility.Password => "multiplayer.public.visibility.password",
            RoomVisibility.Direct => "multiplayer.public.visibility.direct",
            RoomVisibility.Hidden => "multiplayer.public.visibility.hidden",
            _ => "multiplayer.public.visibility.public",
        }),
        Listing.Approval && Listing.Mode != RoomVisibility.Direct ? L.T("multiplayer.public.needs_approval") : null,
        L.F("multiplayer.invite.fingerprint", Listing.HostFingerprint),
    }.Where(s => !string.IsNullOrEmpty(s)));
    public bool NeedsPassword => Listing.Mode == RoomVisibility.Password;
}

/// <summary>
/// 联机页的公共网络：连上以后能看房间列表、点一下申请加入；房主可以把房间公开、设密码、或只给直连码。
/// 公共网络只用来找人和敲门，进房间后的游戏、聊天都在房主自己的网络（子网）里，用的是敲门后拿到的每人一把的钥匙。
/// </summary>
public sealed partial class MultiplayerViewModel
{
    private PublicNetworkSession? _public;
    private CancellationTokenSource? _publicCts;
    private RoomAnnouncer? _announcer;
    private PendingInvite? _standing;
    private byte[]? _standingSalt;
    /// <summary>直连房间公开时列表里放的直连码（用长期邀请生成，和“邀请朋友”生成的单次直连码分开）。</summary>
    /// <summary>加入方：从列表点了直连房间，生成回复码后经目录捎给房主。</summary>

    public ObservableCollection<PublicRoomItem> PublicRooms { get; } = [];

    /// <summary>自检用：开本机的种子节点要用同一个 EasyTier 组件和设置。</summary>
    internal LauncherService TestService => _service;

    /// <summary>
    /// EasyTier 各实例的工作目录。自检在同一个进程里开房主和加入方时给加入方另一个目录：
    /// Mac/Linux 上启动时会清理同一目录里登记过的旧进程，共用目录会把对方的实例当残留关掉。
    /// </summary>
    internal string EasyTierWorkRoot { get; set; } = Path.Combine(AppSettings.DataDir, "easytier");

    public IReadOnlyList<VisibilityChoice> VisibilityChoices { get; } =
        [new(RoomVisibility.Hidden), new(RoomVisibility.Public), new(RoomVisibility.Password), new(RoomVisibility.Direct)];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PublicStateText))] public partial bool IsPublicConnected { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PublicStateText))] public partial bool IsPublicConnecting { get; set; }
    [ObservableProperty] public partial bool PublicNoticeOpen { get; set; }
    [ObservableProperty] public partial bool IsRefreshingRooms { get; set; }
    [ObservableProperty] public partial PublicRoomItem? SelectedPublicRoom { get; set; }
    [ObservableProperty] public partial string PublicRoomPassword { get; set; } = "";
    [ObservableProperty] public partial string PublicNodesText { get; set; } = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NeedsPublishPassword))] public partial VisibilityChoice? SelectedVisibility { get; set; }
    [ObservableProperty] public partial string PublishPassword { get; set; } = "";
    [ObservableProperty] public partial string PublishRoomName { get; set; } = "";
    [ObservableProperty] public partial bool PublishAutoApprove { get; set; }
    [ObservableProperty] public partial string PublishStatus { get; set; } = "";
    public bool NeedsPublishPassword => SelectedVisibility?.Mode == RoomVisibility.Password;

    public bool HasPublicNodes => _service.PublicNetwork.IsUsable;
    public string PublicStateText => IsPublicConnected ? L.F("multiplayer.public.connected", _service.PublicNetwork.Name)
        : IsPublicConnecting ? L.T("multiplayer.public.connecting")
        : HasPublicNodes ? L.F("multiplayer.public.disconnected", _service.PublicNetwork.Name) : L.T("multiplayer.public.no_nodes");

    private void InitPublic()
    {
        SelectedVisibility = VisibilityChoices[0];
        PublicNodesText = string.Join("\n", _service.Settings.PublicNetworkNodes ?? []);
        InitVolunteer();
        LoadNetworks();
    }

    [RelayCommand]
    private void SavePublicNodes()
    {
        var nodes = PublicNodesText.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var bad = nodes.FirstOrDefault(n => !RelayNodes.IsValid(n));
        if (bad is not null)
        {
            SetStatus(StatusMessage.Error("multiplayer.public.bad_node", bad));
            return;
        }
        _service.Settings.PublicNetworkNodes = nodes.Distinct().ToList();
        _service.Settings.Save();
        // Saving the nodes is how the user accepts a new directory key for the built-in network (its directory moved, or
        // its owner re-keyed it; core.mp.public.directory_changed): the network can't be removed and added again. Only
        // matters while the built-in network trusts its directory on first use (no trustee roots yet).
        DirectoryKeys.Forget(PublicNetworkProfile.JayNetworkName);
        LoadNetworks();
        OnPropertyChanged(nameof(HasPublicNodes));
        OnPropertyChanged(nameof(PublicStateText));
        SetStatus(StatusMessage.Success("multiplayer.public.nodes_saved"));
    }

    /// <summary>连公共网络：第一次先显示隐私说明，同意后才连。</summary>
    [RelayCommand]
    private async Task ConnectPublicAsync()
    {
        if (IsPublicConnected || IsPublicConnecting || Child.InviteOnly) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        if (!HasPublicNodes)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.no_nodes"));
            return;
        }
        if (!_service.Settings.PublicNetworkNoticeAccepted)
        {
            PublicNoticeOpen = true;
            return;
        }
        IsPublicConnecting = true;
        var cts = new CancellationTokenSource();
        _publicCts = cts;
        PublicNetworkSession? session = null;
        try
        {
            var profile = _service.PublicNetwork;
            if (!await LoadNetworkConfigAsync(profile, cts.Token) || _publicCts != cts) return;
            session = PublicNetworkSession.Start(_service.EasyTier, new PublicNetworkOptions(profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort(),
                    ExtraPeers: RememberedVolunteers(profile), Volunteer: VolunteerFor(profile)),
                Path.Combine(EasyTierWorkRoot, "public"));
            session.Output += line => _diag.Add(L.T("multiplayer.public.diagnostics_category"), line);
            await session.WaitForDirectoryAsync(TimeSpan.FromSeconds(45), cts.Token);
            if (_publicCts != cts) return;
            _public = session;
            session = null;
            IsPublicConnected = true;
            // Keep this network's directory snapshot fresh on a fixed schedule: lookups and the room list read it locally.
            var connected = _public;
            _snapshotPolling = DirectorySnapshots.For(connected).StartPolling(() => DirectoryClient(connected));
            SetStatus(StatusMessage.Success("multiplayer.public.connected", profile.Name));
            StartAnnouncerIfHosting();
            StartVolunteerWork(_public, profile);
            _ = RenewRelayRecordAsync(_public);
            await RefreshRoomsAsync();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        finally
        {
            session?.Dispose();
            if (_publicCts == cts)
            {
                _publicCts = null;
                IsPublicConnecting = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    private async Task AcceptPublicNoticeAsync()
    {
        _service.Settings.PublicNetworkNoticeAccepted = true;
        _service.Settings.Save();
        PublicNoticeOpen = false;
        await ConnectPublicAsync();
    }

    [RelayCommand]
    private void ClosePublicNotice() => PublicNoticeOpen = false;

    [RelayCommand]
    private void DisconnectPublic()
    {
        _publicCts?.Cancel();
        StopAnnouncer();
        StopVolunteering();
        if (VolunteerEnabled) VolunteerStatus = L.T("multiplayer.volunteer.status_next_connect");
        _snapshotPolling?.Dispose();
        _snapshotPolling = null;
        _public?.Dispose();
        _public = null;
        IsPublicConnected = false;
        PublicRooms.Clear();
        ApplyNetworkConfig(null);
    }

    [RelayCommand]
    private async Task RefreshRoomsAsync()
    {
        if (_public is not { } network || IsRefreshingRooms) return;
        IsRefreshingRooms = true;
        try
        {
            var rooms = await DirectoryClient(network).ListAsync(default);
            PublicRooms.Clear();
            foreach (var room in rooms) PublicRooms.Add(new PublicRoomItem(room));
            if (rooms.Count == 0) SetStatus(StatusMessage.Info("multiplayer.public.no_rooms"));
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException or OperationCanceledException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.public.list_failed", e, ErrorText.Of(e)));
        }
        finally
        {
            IsRefreshingRooms = false;
        }
    }

    /// <summary>
    /// 从列表加入：公开 / 直连 / 密码房间都经公共网络敲门。直连房间批准后在加密的敲门里交换地址再打洞（列表里没有地址）。
    /// </summary>
    [RelayCommand]
    private async Task JoinPublicRoomAsync(PublicRoomItem? item)
    {
        item ??= SelectedPublicRoom;
        if (item is null || _public is not { } network || !IsIdle || IsKnocking) return;
        var listing = item.Listing;
        if (listing.Mode == RoomVisibility.Password && PublicRoomPassword.Length == 0)
        {
            SelectedPublicRoom = item;
            SetStatus(StatusMessage.Warning("multiplayer.public.password_required"));
            return;
        }
        var password = PublicRoomPassword;
        // 派生令牌要 0.3 秒左右，放到后台线程。
        var invite = await Task.Run(() => listing.ToInvite(listing.Mode == RoomVisibility.Password ? password : null));
        PublicRoomPassword = "";
        if (invite is null)
        {
            SetStatus(StatusMessage.Error("multiplayer.room.invalid_code"));
            return;
        }
        var device = Device;
        var jay = JayCertificate;
        await KnockAndJoinAsync(invite, async (me, name, direct, ct) =>
        {
            var outcome = await PublicJoin.KnockAsync(network, listing.Ip, invite, me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, listing.HostKey, ct, device, AccountToken, jay, direct);
            // 密码房间握手失败多半是密码不对（长期邀请不会用完或过期）。
            return listing.Mode == RoomVisibility.Password && outcome.Reason == "invalid" ? outcome with { Reason = "password" } : outcome;
        });
    }

    /// <summary>加入方拿着邀请码、又连着公共网络：先在目录里找房主（隐藏房间也行），找不到再走邀请码自己的大厅。</summary>
    private async Task<KnockOutcome?> TryPublicKnockAsync(JayInvite invite, X25519KeyPair me, string name, CancellationToken ct, byte[]? expectedHost = null,
        Func<DirectAddresses, CancellationToken, Task<DirectAddresses?>>? direct = null)
    {
        if (_public is not { } network) return null;
        try
        {
            return await PublicJoin.TryKnockByInviteAsync(network, invite, me, name, Amber.Core.Launch.UpdateChecker.CurrentVersion, ct, client: DirectoryClient(network), device: Device, account: AccountToken, jay: JayCertificate, expectedHost: expectedHost, direct: direct);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidDataException && !ct.IsCancellationRequested)
        {
            _diag.Add(L.T("multiplayer.public.diagnostics_category"), e.Message);
            return null;
        }
    }

    // ---- 房主 ----

    partial void OnSelectedVisibilityChanged(VisibilityChoice? value)
    {
        if (value?.Mode == RoomVisibility.Direct && !IsDirectHost && IsHosting) SetStatus(StatusMessage.Warning("multiplayer.public.direct_needs_direct_room"));
    }

    /// <summary>开着房间、连着公共网络：先以“隐藏”登记（拿着邀请码的人能经公共网络找到你），房主再选要不要公开。</summary>
    private void StartAnnouncerIfHosting()
    {
        if (_public is not { } network || _announcer is not null || !IsHosting || _lobby is null) return;
        var announcer = new RoomAnnouncer(network, BuildListing, LobbyKeys,
            line => _diag.Add(L.T("multiplayer.public.diagnostics_category"), line), client: DirectoryClient(network), searchKey: SearchKeyForAnnounce,
            account: () => AccountToken);
        announcer.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_announcer != announcer) return;
            PublishStatus = announcer.Published
                ? announcer.Warning is { } warning ? L.F("core.mp.public.published_warning", warning)
                : L.T(CurrentVisibility == RoomVisibility.Hidden ? "multiplayer.public.registered_hidden" : "multiplayer.public.published")
                : announcer.LastError is { } error ? L.F("core.mp.public.announce_failed", error) : "";
        });
        _announcer = announcer;
    }

    private RoomVisibility CurrentVisibility { get; set; } = RoomVisibility.Hidden;

    /// <summary>Stops the directory snapshot poller started when the public network connected.</summary>
    private IDisposable? _snapshotPolling;

    /// <summary>
    /// The lobby key for hidden entries, so invite codes find the room through the directory (all of a room's invites
    /// share it). The announcer only uses it while the room is hidden: a room listed with its invite in the open
    /// (public, direct, password) is found from its listing.
    /// </summary>
    private IReadOnlyList<byte[]> LobbyKeys() => _lobby?.Book.Pending.Count > 0 && _lobby.Book.Pending[0].Invite.LobbyKey is { } key
        ? [key] : [];

    private RoomListing BuildListing()
    {
        var mode = CurrentVisibility;
        var standing = _standing;
        return new RoomListing
        {
            Id = "",
            Name = PublishRoomName.Trim().Length > 0 ? PublishRoomName.Trim() : L.F("multiplayer.public.default_name", ChatName),
            Host = ChatName,
            Mode = standing is null && mode != RoomVisibility.Hidden ? RoomVisibility.Hidden : mode,
            // 隐藏房间开了指纹查找：带一张要房主批准的长期邀请，只有拿着指纹码的人能从目录里拿到。
            HostKey = Identity.PublicKey,
            Game = TargetInstance ?? "",
            Version = Amber.Core.Launch.UpdateChecker.CurrentVersion,
            Players = 1 + Members.Count,
            Approval = !(standing?.AutoApprove ?? false),
            Invite = mode is RoomVisibility.Public or RoomVisibility.Direct || mode == RoomVisibility.Hidden && _searchPublished ? standing?.Invite.ToString() : null,
            Lobby = mode == RoomVisibility.Password && standing is not null ? Convert.ToHexStringLower(standing.Invite.LobbyKey) : null,
            Salt = mode == RoomVisibility.Password && _standingSalt is { } salt ? Convert.ToHexStringLower(salt) : null,
            Tunnel = _credentials?.Tunnel ?? TunnelSettings.Default,
        };
    }

    /// <summary>按选的方式发布：公开、直连、密码房间开一张长期邀请（列表里都没有地址）；隐藏只登记邀请码的查找键。</summary>
    [RelayCommand]
    private async Task PublishRoomAsync()
    {
        if (!IsHosting || _lobby is not { } lobby || _credentials is not { } credentials) return;
        if (Child.InviteOnly)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.child_invite_only"));
            return;
        }
        if (_public is null)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.connect_first"));
            return;
        }
        var mode = SelectedVisibility?.Mode ?? RoomVisibility.Hidden;
        if (!NetworkPolicy.Allows(mode))
        {
            SetStatus(StatusMessage.Warning("multiplayer.netconfig.mode_not_allowed", SelectedVisibility?.Label ?? ""));
            return;
        }
        if (mode == RoomVisibility.Password && PublishPassword.Length < RoomPassword.MinLength)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.password_short", RoomPassword.MinLength));
            return;
        }
        if (mode == RoomVisibility.Direct && !IsDirectHost)
        {
            SetStatus(StatusMessage.Warning("multiplayer.public.direct_needs_direct_room"));
            return;
        }
        if (AllowFingerprintSearch && Device is null)
        {
            SetStatus(StatusMessage.Warning("multiplayer.device.enable_first"));
            return;
        }
        _searchPublished = AllowFingerprintSearch;
        // 换方式时先收回旧的长期邀请。
        if (_standing is { } old) lobby.Book.Revoke(old);
        _standing = null;
        _standingSalt = null;
        switch (mode)
        {
            case RoomVisibility.Hidden when _searchPublished:
                // 只有拿着指纹码的人看得到这张邀请，一律要房主批准。
                _standing = lobby.Book.CreateStanding(false, credentials.Tunnel);
                break;
            case RoomVisibility.Public:
                _standing = lobby.Book.CreateStanding(Child.MayAutoAdmit(PublishAutoApprove), credentials.Tunnel);
                break;
            case RoomVisibility.Password:
                var salt = RoomPassword.NewSalt();
                var password = PublishPassword;
                var token = await Task.Run(() => RoomPassword.Token(password, salt));
                _standing = lobby.Book.CreateStanding(Child.MayAutoAdmit(PublishAutoApprove), credentials.Tunnel, token);
                _standingSalt = salt;
                PublishPassword = "";
                break;
            case RoomVisibility.Direct:
                _standing = lobby.Book.CreateStanding(Child.MayAutoAdmit(PublishAutoApprove), credentials.Tunnel);
                break;
        }
        try { await lobby.UpdateAsync(); }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (_standing is { } failed) lobby.Book.Revoke(failed);
            _standing = null;
            SetStatus(StatusMessage.Failure("multiplayer.invite.lobby_failed", e, KnockProtocol.Port, ErrorText.Of(e)));
            return;
        }
        CurrentVisibility = mode;
        RememberLastVisibility(mode);
        StartAnnouncerIfHosting();
        _announcer?.Refresh();
        SetStatus(StatusMessage.Success(mode == RoomVisibility.Hidden ? "multiplayer.public.registered_hidden" : "multiplayer.public.published"));
    }

    /// <summary>撤下（回到隐藏）：长期邀请作废，列表里不再显示；拿着普通邀请码的人仍能经公共网络找到你。</summary>
    /// <summary>Child mode turned on (invite only): take the room off the public network and leave the network.</summary>
    private void LeavePublicForChildMode()
    {
        var was = _standing is not null || _public is not null;
        if (_standing is not null) UnpublishRoom();
        if (_public is not null || IsPublicConnecting) DisconnectPublic();
        if (was) SetStatus(StatusMessage.Warning("multiplayer.public.child_left"));
    }

    [RelayCommand]
    private void UnpublishRoom()
    {
        if (_standing is { } standing) _lobby?.Book.Revoke(standing);
        _standing = null;
        _standingSalt = null;
        _searchPublished = false;
        CurrentVisibility = RoomVisibility.Hidden;
        _announcer?.Refresh();
        SetStatus(StatusMessage.Info("multiplayer.public.unpublished"));
    }

    private void StopAnnouncer()
    {
        var announcer = _announcer;
        _announcer = null;
        _standing = null;
        _standingSalt = null;
        _searchPublished = false;
        CurrentVisibility = RoomVisibility.Hidden;
        PublishStatus = "";
        if (announcer is not null) _ = announcer.DisposeAsync().AsTask();
    }
}
