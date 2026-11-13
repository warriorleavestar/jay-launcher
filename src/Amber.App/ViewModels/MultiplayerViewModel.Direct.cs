using Amber.Core.Localization;
using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>联机页的直连房间（不用联机节点）、网络检测、调试模式和全条件测试。</summary>
public sealed partial class MultiplayerViewModel
{
    private DirectSession? _direct;
    private readonly DiagnosticLog _diag = new();
    private CancellationTokenSource? _matrixCts;
    private List<MatrixResult> _lastMatrix = [];

    public ObservableCollection<string> DebugLines { get; } = [];
    public ObservableCollection<string> MatrixLines { get; } = [];

    [ObservableProperty] public partial bool DebugMode { get; set; }
    [ObservableProperty] public partial bool IncludePublicIps { get; set; }
    /// <summary>
    /// A direct room also connects to the relays. Off: the lobby's "No server" means no relay, so a direct room that
    /// can't connect says so instead of quietly going through one. Only the connection tests turn it on.
    /// </summary>
    internal bool DirectUseNodes { get; set; }
    [ObservableProperty] public partial bool DirectSimulate { get; set; }
    [ObservableProperty] public partial string StunText { get; set; } = string.Join(", ", Stun.DefaultServers);
    [ObservableProperty] public partial string DirectHint { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentPathText))] public partial string PathText { get; set; } = "";
    public string CurrentPathText => L.F("multiplayer.direct.current_path", PathText);
    [ObservableProperty] public partial string NetworkCheckText { get; set; } = "";
    [ObservableProperty] public partial bool IsCheckingNetwork { get; set; }
    [ObservableProperty] public partial bool MatrixRunning { get; set; }
    [ObservableProperty] public partial bool MatrixFullLan { get; set; }

    partial void OnMatrixFullLanChanged(bool value)
    {
        if (value && !CanUseFullLan) MatrixFullLan = false;
    }

    /// <summary>加入方：房主请求全条件测试，等用户确认。</summary>
    [ObservableProperty] public partial bool TestRequestPending { get; set; }

    /// <summary>加入方：正在跑房主发起的测试（显示“测试中”横幅和“停止测试”）。</summary>
    [ObservableProperty] public partial bool GuestTestRunning { get; set; }

    [ObservableProperty] public partial string GuestTestBanner { get; set; } = "";

    /// <summary>测试把出口设成了房主：测试前的出口设置，结束时还原。</summary>
    private string? _exitBeforeTest;
    private string? _guestTestRun;
    [ObservableProperty] public partial bool TestRequestWantsFullLan { get; set; }
    [ObservableProperty] public partial bool TestAllowFullLan { get; set; }
    [ObservableProperty] public partial bool TestAllowExit { get; set; }

    partial void OnTestAllowFullLanChanged(bool value)
    {
        if (value && !CanUseFullLan)
        {
            TestAllowFullLan = false;
            return;
        }
        if (!value) TestAllowExit = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirectHost))]
    public partial bool IsDirect { get; set; }

    public bool IsDirectHost => IsDirect && IsHosting;

    /// <summary>测试用：现在的直连会话。</summary>
    internal DirectSession? DirectForTest => _direct;

    /// <summary>测试用：现在房间的密钥（检查诊断报告里没有它们）。</summary>
    internal IReadOnlyList<string> RoomSecretsForTest => _room is { } room ? [.. room.Secrets] : [];

    private void InitDirect()
    {
        _diag.Added += entry => Dispatcher.UIThread.Post(() =>
        {
            if (!DebugMode && entry.Category == "EasyTier") return;
            DebugLines.Add(NetDiagnostics.Redact(entry.ToString(), _room is { } r ? [.. r.Secrets] : [], includePublicIps: true));
            while (DebugLines.Count > 400) DebugLines.RemoveAt(0);
        });
    }

    private List<string> StunServers() =>
        StunText.Split([',', '\uFF0C', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal DirectOptions MakeDirectOptions() => new(
        StunServers(),
        !DirectSimulate,
        DirectUseNodes ? (TestPeers ?? _service.MultiplayerPeers).ToList() : [],
        FullLanMode,
        FullLanMode && AllowExitNode,
        DebugMode,
        DirectSimulate);

    /// <summary>检测网络：IPv6、STUN 外部地址和 NAT 类型、路由器能不能映射端口（试完马上删掉）。</summary>
    [RelayCommand]
    private async Task CheckNetworkAsync()
    {
        if (IsCheckingNetwork) return;
        IsCheckingNetwork = true;
        NetworkCheckText = L.T("multiplayer.network.checking");
        try
        {
            using var socket = DirectBridge.CreateSocket();
            var lines = new List<string>();
            var gathered = await DirectGatherer.GatherAsync(socket, StunServers(), true, l => { lines.Add(l); _diag.Add(L.T("multiplayer.network.diagnostic_category"), l); });
            if (gathered.Mapping?.Remove is { } remove)
            {
                try { await remove(); } catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException) { }
            }
            var v6 = gathered.Candidates.Any(c => c.Kind == PathKind.IPv6);
            var verdict = gathered.Nat switch
            {
                NatKind.Open => L.T("multiplayer.network.open"),
                NatKind.Cone => L.T("multiplayer.network.cone"),
                NatKind.Symmetric => v6 ? L.T("multiplayer.network.symmetric_ipv6") : L.T("multiplayer.network.symmetric"),
                _ => L.T("multiplayer.network.unknown"),
            };
            NetworkCheckText = $"{verdict}\n{string.Join("\n", lines)}";
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            NetworkCheckText = L.F("multiplayer.network.check_failed", e.Message);
        }
        finally
        {
            IsCheckingNetwork = false;
        }
    }

    /// <summary>
    /// 房主：创建直连房间。和经节点的房间一样是邀请房间：随机的房间凭据、成员表、大厅和敲门；
    /// 大厅密钥 L 在整个房间期间不换（直连桥的 HMAC 钥匙由它派生）。马上发第一张邀请（一次、要房主批准），生成直连邀请码。
    /// </summary>
    [RelayCommand]
    private async Task CreateDirectRoomAsync()
    {
        if (!IsIdle) return;
        if (!IsComponentReady)
        {
            SetStatus(StatusMessage.Warning("multiplayer.component.download_required"));
            return;
        }
        // No LAN world yet: the room and its invite are made now, and the game is picked up when it's opened to LAN.
        if (await HostGamePortAsync(checkManual: false) is not (var gamePort, var relay)) return;
        if (!IsIdle)
        {
            if (relay is not null) await relay.DisposeAsync();
            return;
        }
        var credentials = NewCredentials(gamePort, TunnelSettings.Default);
        var members = new MemberTable();
        var room = credentials.ToHostRoom(members, ChatName);
        var book = new InviteBook { KeepLobbyKey = true };
        var lobbyKey = book.LobbyKeyCopy();
        var bridgeKey = DirectKeys.Bridge(lobbyKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(lobbyKey);
        SetStatus(StatusMessage.Info("multiplayer.direct.gathering_host"));
        var direct = new DirectSession(_service.EasyTier, EasyTierWorkRoot, room, true, MakeDirectOptions(), _diag)
        {
            BridgeKey = bridgeKey,
            // 桥钥匙整个房间都不换：没有待用邀请时不接新的直连加入方。
            AcceptNewPeers = () => book.HasPending,
        };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bridgeKey);
        try
        {
            var addresses = await direct.PrepareAsync();
            AttachDirect(direct, room);
            AdoptGameRelay(relay);
            relay = null;
            Mode = "host";
            RoomCodeText = "";
            RestoreChatHistory(room);
            StartHub(room);
            // Guests knock through the relays (or the LAN) like in any invite room; the host's addresses go only to approved
            // guests, inside the encrypted knock reply, and never into the invite code.
            StartHostLobby(credentials, members, book, direct.LobbyUdpPort, direct: direct);
            StartAnnouncerIfHosting();
            DirectHint = L.F("multiplayer.direct.host_hint", DirectText.Nat(addresses.Nat));
            if (await IssueInviteAsync(1, InviteExpiryChoices[0].Lifetime, false) is null) return;
            SetStatus(HasGameRelay ? StatusMessage.Success("multiplayer.relay.created") : StatusMessage.Success("multiplayer.direct.created"));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            if (_direct == direct) StopSession();
            else await direct.DisposeAsync();
            if (relay is not null) await relay.DisposeAsync();
            SetStatus(StatusMessage.Failure("multiplayer.direct.create_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>
    /// Guest, during the knock: the host of a direct room approved us and sent its addresses inside the encrypted reply.
    /// Makes the direct session, gathers our addresses and returns them for the encrypted ack. Nothing here is ever
    /// shown or put into a code.
    /// </summary>
    private async Task<DirectAddresses?> AnswerDirectOfferAsync(JayInvite invite, DirectAddresses host, Action<DirectSession> created, CancellationToken ct)
    {
        var bridgeKey = DirectKeys.Bridge(invite.LobbyKey);
        var options = await Dispatcher.UIThread.InvokeAsync(MakeDirectOptions);
        var direct = new DirectSession(_service.EasyTier, EasyTierWorkRoot, null, false, options, _diag) { BridgeKey = bridgeKey };
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bridgeKey);
        created(direct);
        await Dispatcher.UIThread.InvokeAsync(() => SetStatus(StatusMessage.Info("multiplayer.direct.gathering_guest")));
        return await direct.AnswerAsync(host, ct);
    }

    /// <summary>
    /// Guest, after the knock: the host approved us and both sides swapped addresses inside the encrypted knock. Punches,
    /// proves membership on the bridge and runs the room over it; falls back to the relays when punching fails.
    /// </summary>
    private async Task CompleteDirectJoinAsync(DirectSession direct, RoomCode room, KnockOutcome outcome)
    {
        SubscribeGuestDirect(direct);
        try
        {
            Mode = "join";
            IsDirect = true;
            _direct = direct;
            RoomCodeText = "";
            HostFingerprint = outcome.HostFingerprint;
            HostJayAccount = outcome.HostJayAccount;
            SeenJayDevice(outcome.HostPublicKey, outcome.HostJayDevice);
            DirectHint = L.T("multiplayer.direct.guest_hint");
            SetStatus(StatusMessage.Info("multiplayer.direct.punching"));
            await direct.CompleteJoinAsync(room);
            FinishDirectJoin(direct, room);
        }
        catch (TimeoutException e)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(new StatusMessage("common.raw", [e.Message], StatusKind.Warning));
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException or InvalidDataException or System.Text.Json.JsonException)
        {
            await LeaveFailedDirectAsync(direct);
            SetStatus(StatusMessage.Failure("multiplayer.direct.connect_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>Guest: subscribe before punching, since the host may send a test request right after. Only this session's events count.</summary>
    private void SubscribeGuestDirect(DirectSession direct)
    {
        direct.TestRequested += request => Dispatcher.UIThread.Post(() =>
        {
            // 事件排到界面线程时请求可能已经作废或撤回：只显示还在等确认的那个。
            if (_direct != direct || direct.TestConsent.Pending?.Run != request.Run) return;
            ShowTestRequest(request);
        });
        direct.GuestStepStarted += (run, step) => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct || _guestTestRun != run || direct.TestConsent.ActiveRun != run) return;
            OnGuestStep(step);
        });
        direct.TestEnded += run => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct || _guestTestRun != run) return;
            ResetGuestTest();
        });
    }

    /// <summary>Guest: the room runs (over the bridge, or the relays after a failed punch).</summary>
    private void FinishDirectJoin(DirectSession direct, RoomCode room)
    {
        AttachDirect(direct, room);
        JoinAddress = FullLanMode ? $"{EasyTierArgs.HostIp}:{room.Port}" : $"127.0.0.1:{direct.LocalGamePort}";
        _localChatPort = FullLanMode ? null : direct.LocalChatPort;
        _localRemotePort = FullLanMode ? null : direct.LocalRemotePort;
        RestoreChatHistory(room);
        ChatStatus = L.T("multiplayer.chat.awaiting_host");
        Status = direct.UsingFallbackNode ? L.T("multiplayer.direct.fallback") : L.T("multiplayer.direct.connected");
        direct.GuestChatProbe = ChatProbeAsync;
        // 万一请求在界面订阅前就到了（或者事件被错过）：按会话里记着的待确认请求补上。
        if (direct.TestConsent.Pending is { } pending && !TestRequestPending) ShowTestRequest(pending);
    }

    private async Task LeaveFailedDirectAsync(DirectSession direct)
    {
        await direct.DisposeAsync();
        if (_direct != direct) return;
        _direct = null;
        IsDirect = false;
        Mode = "";
        ClearSealed();
        // 打洞期间可能已经收到测试请求：一起收起。
        ResetGuestTest();
    }

    private void ShowTestRequest(TestRequest request)
    {
        ResetGuestTest();
        _guestTestRun = request.Run;
        TestRequestWantsFullLan = request.WantsFullLan;
        TestAllowFullLan = false;
        TestAllowExit = false;
        TestRequestPending = true;
        SetStatus(StatusMessage.Info("multiplayer.matrix.request_status"));
    }

    /// <summary>加入方：测试的一步开始了。完整局域网/出口时显示提示；出口那几步把出口横幅也显示出来（经房主上网）。</summary>
    private void OnGuestStep(MatrixStep step)
    {
        GuestTestRunning = true;
        GuestTestBanner = step.Exit ? L.T("multiplayer.matrix.banner_exit")
            : step.FullLan ? L.T("multiplayer.matrix.banner_full_lan")
            : L.T("multiplayer.matrix.banner_running");
        if (step.Exit)
        {
            _exitBeforeTest ??= ExitNodeIp;
            ExitNodeIp = EasyTierArgs.HostIp;
        }
        else if (_exitBeforeTest is { } before)
        {
            ExitNodeIp = before;
            _exitBeforeTest = null;
        }
    }

    /// <summary>测试结束、会话结束：收起确认卡片和测试横幅，还原出口显示。</summary>
    private void ResetGuestTest()
    {
        _guestTestRun = null;
        TestRequestPending = false;
        GuestTestRunning = false;
        GuestTestBanner = "";
        if (_exitBeforeTest is { } before)
        {
            ExitNodeIp = before;
            _exitBeforeTest = null;
        }
    }

    /// <summary>加入方：停止房主发起的测试，恢复原来的联机设置。</summary>
    [RelayCommand]
    private async Task StopGuestTestAsync()
    {
        if (_direct is not { IsHost: false } direct) return;
        var run = _guestTestRun;
        await direct.StopGuestTestAsync();
        // 同一次结束事件可能已先收起横幅；只有换会话或显示了另一轮时才忽略这次停止结果。
        if (_direct != direct || _guestTestRun is not null && _guestTestRun != run) return;
        ResetGuestTest();
        SetStatus(StatusMessage.Warning("multiplayer.matrix.cancelled"));
    }

    private void AttachDirect(DirectSession direct, RoomCode room)
    {
        _direct = direct;
        IsDirect = true;
        _room = room;
        _session = direct.EasyTier;
        // 直连从组件真正开起来时开始计时（之前在敲门、打洞）。
        MarkJoinStarted(newRoom: true);
        _sessionCts = new CancellationTokenSource();
        _service.ActiveMultiplayer = new DirectHandle(direct);
        var starts = direct.EasyTierStarts;
        direct.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_direct != direct) return;
            // 直连会话自己启动或重启了 EasyTier（打不通改用节点、已提权的原地重启）：从这次开始重新计时。
            if (direct.EasyTierStarts != starts)
            {
                starts = direct.EasyTierStarts;
                MarkJoinStarted(newRoom: false);
            }
            _session = direct.EasyTier;
            OnPropertyChanged(nameof(CanUseExit));
            SyncHotspotSharing();
        });
        if (FullLanMode && direct.EasyTier?.Options is { } o) MyVirtualIp = EasyTierArgs.Build(o)[EasyTierArgs.Build(o).IndexOf("--ipv4") + 1];
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _ = RefreshPeersAsync());
        _timer.Start();
        OnPropertyChanged(nameof(CanUseExit));
        SyncHotspotSharing();
    }

    /// <summary>退出 Amber 时一起关掉。</summary>
    private sealed class DirectHandle(DirectSession direct) : IDisposable
    {
        public void Dispose() => direct.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>刷新时更新“当前路径”。</summary>
    private void TickDirect(IReadOnlyList<PeerInfo> peers)
    {
        if (_direct is not { } direct) return;
        PathText = direct.DescribePath(peers);
    }

    private async Task<bool?> ChatProbeAsync(MatrixStep step, CancellationToken ct)
    {
        for (var i = 0; i < 30 && !ChatReady; i++) await Task.Delay(500, ct);
        if (_chat is not { } chat) return false;
        var text = L.F("multiplayer.matrix.chat_probe", step.Index);
        await chat.SendAsync(text, ct);
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250, ct);
            if (await Dispatcher.UIThread.InvokeAsync(() => ChatLines.Any(l => l.Message.Text == text))) return true;
        }
        return false;
    }

    /// <summary>全条件测试：房主点，双方一起按顺序切换每种组合。</summary>
    [RelayCommand]
    private async Task RunMatrixAsync()
    {
        if (_direct is not { IsHost: true } direct || MatrixRunning) return;
        if (direct.Bridge?.Peers.Count is not > 0)
        {
            SetStatus(StatusMessage.Warning("multiplayer.matrix.peer_required"));
            return;
        }
        MatrixRunning = true;
        MatrixLines.Clear();
        _matrixCts = new CancellationTokenSource();
        var results = new List<MatrixResult>();
        try
        {
            SetStatus(StatusMessage.Info("multiplayer.matrix.waiting_guest"));
            var progress = new Progress<MatrixResult>(r =>
            {
                results.Add(r);
                MatrixLines.Clear();
                foreach (var line in TestMatrix.FormatTable(results)) MatrixLines.Add(line);
                SetStatus(StatusMessage.Info("multiplayer.matrix.progress", r.Step.Index, (r.Passed switch { true => L.T("common.passed"), false => L.T("common.failed"), _ => L.T("common.skipped") })));
            });
            _lastMatrix = await direct.RunMatrixAsync(MatrixFullLan, progress, null, _matrixCts.Token);
            MatrixLines.Clear();
            foreach (var line in TestMatrix.FormatTable(_lastMatrix)) MatrixLines.Add(line);
            SetStatus(StatusMessage.Success("multiplayer.matrix.completed", _lastMatrix.Count(r => r.Passed == true), _lastMatrix.Count));
        }
        catch (OperationCanceledException)
        {
            SetStatus(StatusMessage.Warning("multiplayer.matrix.cancelled"));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException)
        {
            SetStatus(StatusMessage.Failure("multiplayer.matrix.failed", e, ErrorText.Of(e)));
        }
        finally
        {
            MatrixRunning = false;
        }
    }

    [RelayCommand]
    private void CancelMatrix() => _matrixCts?.Cancel();

    /// <summary>加入方：同意房主的全条件测试（只同意勾上的部分）。</summary>
    [RelayCommand]
    private Task AcceptTestRequestAsync() => AnswerTestRequestAsync(true);

    [RelayCommand]
    private Task DeclineTestRequestAsync() => AnswerTestRequestAsync(false);

    private async Task AnswerTestRequestAsync(bool accept)
    {
        if (_direct is not { IsHost: false } direct || !TestRequestPending) return;
        var run = _guestTestRun;
        TestRequestPending = false;
        var accepted = await direct.AnswerTestRequestAsync(accept, TestRequestWantsFullLan && TestAllowFullLan, TestRequestWantsFullLan && TestAllowFullLan && TestAllowExit);
        if (_direct != direct || _guestTestRun != run) return;
        if (accepted && direct.TestConsent.ActiveRun == run)
        {
            // 同意后马上显示横幅和“停止测试”（不等第一步到）。
            GuestTestRunning = true;
            GuestTestBanner = L.T("multiplayer.matrix.banner_running");
            SetStatus(StatusMessage.Info("multiplayer.matrix.running"));
        }
        else
        {
            ResetGuestTest();
            SetStatus(StatusMessage.Warning(accept ? "multiplayer.matrix.request_gone" : "multiplayer.matrix.cancelled"));
        }
    }

    /// <summary>导出诊断报告：去掉房间密钥和各种码；默认遮住公网 IP。</summary>
    public void ExportDiagnostics(string path)
    {
        var table = _lastMatrix.Count > 0 ? TestMatrix.FormatTable(_lastMatrix) : MatrixLines.Count > 0 ? MatrixLines.ToList() : null;
        var secrets = _room is { } r ? r.Secrets : [];
        var extra = new List<DiagnosticEntry>();
        if (_session is { } s) extra.AddRange(s.RecentOutput.Select(l => new DiagnosticEntry(DateTime.Now, L.T("multiplayer.diagnostics.recent_output"), l)));
        var report = NetDiagnostics.BuildReport(Amber.Core.Launch.UpdateChecker.CurrentVersion, _diag.Snapshot().Concat(extra), table, secrets, IncludePublicIps);
        SaveFile(path, () => File.WriteAllText(path, report), "multiplayer.diagnostics.exported");
    }

    private async Task StopDirectAsync()
    {
        _matrixCts?.Cancel();
        // 会话结束：确认卡片、测试横幅跟着收起（之后旧会话的事件不再理会）。
        ResetGuestTest();
        if (_direct is { } direct)
        {
            _direct = null;
            await direct.DisposeAsync();
        }
        IsDirect = false;
        DirectHint = "";
        PathText = "";
        ClearSealed();
    }
}
