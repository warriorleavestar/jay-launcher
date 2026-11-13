using Amber.Core.Localization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

public sealed record DirectOptions(
    IReadOnlyList<string> StunServers,
    bool TryPortMapping,
    IReadOnlyList<string> FallbackNodes,
    bool FullLan,
    bool AllowExit,
    bool Verbose,
    bool Simulated = false);

/// <summary>
/// 一个直连房间：收集本机地址、打洞、在直连桥上跑 EasyTier；打不通又配置了联机节点时退回节点。
/// 地址从不写进任何码：加入方经中继（或局域网）进大厅敲门，房主批准后才在加密的敲门回复里给出自己的地址，
/// 加入方在加密的确认里回自己的地址（<see cref="KnockServer.DirectOffer"/>），然后两边一起打洞。
/// 也负责全条件测试：房主经直连通道告诉加入方每一步怎么配，双方一起切换，加入方测完把结果发回来。
/// </summary>
public sealed class DirectSession : IAsyncDisposable
{
    private readonly EasyTierInstaller _installer;
    private readonly string _workDir;
    private readonly DiagnosticLog _log;
    private readonly string _machineId = Guid.NewGuid().ToString();
    private DirectGatherer.Gathered? _gathered;
    private DirectAddresses? _remote;
    /// <summary>Host: an approved guest sent its addresses until then; the bridge takes new peers while this is in the future.</summary>
    private DateTime _expectGuestUntil;
    private IReadOnlyList<Candidate> _guestCandidates = [];
    private NatKind _guestNat = NatKind.Unknown;
    private int _easyTierUdpPort;
    private EasyTierOptions? _currentOptions;
    private SpeedTestServer? _speed;
    private bool _speedOnVirtualIp;
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = [];
    private TaskCompletionSource<bool>? _elevateReply;
    private TaskCompletionSource<JsonObject>? _testAnswer;
    private string? _hostRun;
    private Timer? _consentWatch;
    private Timer? _pendingWatch;
    private volatile bool _disposed;
    private CancellationTokenSource? _hostAbort;

    // 启动、换参数重启 EasyTier 一次只做一个（测试步骤、提权、结束恢复可能同时触发）。
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // 路径切换与恢复也要串行：结束时先取消旧步骤，之后它不能再清掉已恢复的连接。
    private readonly object _pathGate = new();

    // 加入方：每次同意的测试一个取消源。步骤和提权都用它；测试结束（房主说结束、超时、用户点停止）时取消，
    // 还在路上的步骤不会在恢复之后又把 EasyTier 切回测试模式。
    private readonly object _runGate = new();
    private CancellationTokenSource? _runCts;
    private string? _runCtsRun;

    /// <summary>测试用：代替真的启动 EasyTier。</summary>
    internal Func<EasyTierOptions, CancellationToken, Task<IEasyTierSession>>? StartForTest { get; init; }

    /// <summary>测试用：保留真实的新会话模式选择，只替代最后的进程创建。</summary>
    internal Func<bool, EasyTierOptions, IEasyTierSession>? CreateSessionForTest { get; init; }

    // 本机回归测试使用独立监听端口和短期限，不占用正在联机的房间。
    internal int SpeedTestPortForTest { get; init; } = SpeedTestServer.Port;
    internal TimeSpan MatrixStepTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan MatrixRecoveryDelay { get; init; } = TimeSpan.FromSeconds(35);

    /// <summary>加入方：开始执行测试的一步（界面显示“测试中”和完整局域网/出口提示）。</summary>
    public event Action<string, MatrixStep>? GuestStepStarted;

    /// <summary>加入方：全条件测试要本机用户同意（见 <see cref="GuestTestConsent"/>）。</summary>
    public GuestTestConsent TestConsent { get; } = new();

    /// <summary>加入方：房主请求全条件测试，界面要问用户。</summary>
    public event Action<TestRequest>? TestRequested;

    /// <summary>加入方：测试结束（或请求被撤回），界面收起确认。</summary>
    public event Action<string?>? TestEnded;

    private RoomCode? _room;
    private byte[]? _bridgeKey;

    /// <summary>
    /// 房间。房主一开始就有；加入方要等经直连桥敲门、房主批准后才拿到（之前读它会抛异常，见 <see cref="HasRoom"/>）。
    /// 房主踢人换网络后换成新的（<see cref="SwitchRoomAsync"/>）。
    /// </summary>
    public RoomCode Room => _room ?? throw new InvalidOperationException("no room yet");
    public bool HasRoom => _room is not null;

    /// <summary>直连桥探测和控制包的 HMAC 钥匙（<see cref="DirectKeys.Bridge"/>，由邀请里的大厅密钥 L 派生）。PrepareAsync 之前设好。</summary>
    public byte[] BridgeKey
    {
        init => _bridgeKey = value.Length == 32 ? (byte[])value.Clone() : throw new ArgumentException(null, nameof(BridgeKey));
    }

    /// <summary>
    /// 房主（邀请房间）：本机大厅 EasyTier 监听的 UDP 端口。新连上的直连桥先只转发到这里（只能敲门），批准后才接到房间。
    /// </summary>
    public int? LobbyUdpPort { get; }

    /// <summary>房主：现在收不收新的直连加入方（没有待用邀请时不收），见 <see cref="DirectBridge.AcceptingNewPeers"/>。</summary>
    public Func<bool>? AcceptNewPeers { get; init; }

    /// <summary>加入方：证明自己是批准过的成员最多等这么久。</summary>
    internal static TimeSpan AdmitTimeout { get; set; } = TimeSpan.FromSeconds(20);
    private TaskCompletionSource<byte[]>? _challenge;
    private TaskCompletionSource<bool>? _admitReply;
    public bool IsHost { get; }
    public DirectOptions Options { get; private set; }
    /// <summary>This side's addresses. Only ever sent inside the encrypted knock (host: to approved guests; guest: to the host).</summary>
    public DirectAddresses? LocalAddresses { get; private set; }
    public DirectBridge? Bridge { get; private set; }
    public IEasyTierSession? EasyTier { get; private set; }
    public int LocalGamePort { get; } = EasyTierArgs.FreeTcpPort();
    public int LocalChatPort { get; } = EasyTierArgs.FreeTcpPort();
    /// <summary>加入方：远程控制连房主用的本机端口（不建网卡时）。</summary>
    public int LocalRemotePort { get; } = EasyTierArgs.FreeTcpPort();
    public int LocalSpeedPort { get; } = EasyTierArgs.FreeTcpPort();
    public bool UsingFallbackNode { get; private set; }
    public NatKind LocalNat => _gathered?.Nat ?? NatKind.Unknown;
    public string LocalNatDetail => _gathered?.NatDetail ?? "";
    public IPAddress? PublicIPv4 => _gathered?.Candidates.FirstOrDefault(c => c.Kind is PathKind.Punch or PathKind.Mapped)?.EndPoint.Address;

    /// <summary>加入方：测试矩阵的一步交给界面执行（聊天探测要用界面那边的连接）。为空时用内置的探测。</summary>
    public event Action? StateChanged;

    /// <param name="room">房主的房间；加入方传 null（批准后才拿到）。</param>
    public DirectSession(EasyTierInstaller installer, string workDir, RoomCode? room, bool isHost, DirectOptions options, DiagnosticLog log)
    {
        if (isHost && room is null) throw new ArgumentNullException(nameof(room));
        _installer = installer;
        _workDir = workDir;
        _room = room;
        IsHost = isHost;
        if (isHost && room!.FromInvite) LobbyUdpPort = FreeUdpPort();
        Options = options;
        _log = log;
    }

    private void Log(string text) => _log.Add(IsHost ? L.T("core.mp.session.host") : L.T("core.mp.session.guest"), text);

    /// <summary>收集地址，建好直连桥。房主同时启动 EasyTier。地址只经加密的敲门交给对方，不进任何码。</summary>
    public async Task<DirectAddresses> PrepareAsync(CancellationToken ct = default)
    {
        var key = _bridgeKey ?? throw new InvalidOperationException("no bridge key");
        var socket = DirectBridge.CreateSocket();
        Log(L.F("core.mp.session.socket", ((IPEndPoint)socket.LocalEndPoint!).Port));
        if (Options.Simulated)
        {
            // 本机模拟：只用回环地址，不查 STUN、不改路由器。结果会标“模拟”。
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            _gathered = new DirectGatherer.Gathered([new Candidate(PathKind.Lan, new IPEndPoint(IPAddress.Loopback, port))], NatKind.Cone, L.T("core.mp.session.simulated"), null);
            Log(L.T("core.mp.session.simulation"));
        }
        else
        {
            _gathered = await DirectGatherer.GatherAsync(socket, Options.StunServers, Options.TryPortMapping, Log, ct);
        }
        Log(L.F("core.mp.session.nat", DirectText.Nat(_gathered.Nat)));
        if (IsHost)
        {
            _easyTierUdpPort = FreeUdpPort();
            // New bridge peers: while an invite is pending, or for a short while after an approved guest sent its addresses
            // (its one-use invite is already spent by then).
            Bridge = new DirectBridge(socket, key, true, _easyTierUdpPort, Log, LobbyUdpPort) { AcceptingNewPeers = () => AcceptNewPeers?.Invoke() != false || ExpectingGuest };
            Bridge.Connected += OnConnected;
            Bridge.ControlReceived += OnControl;
            _speed = new SpeedTestServer(SpeedTestPortForTest);
            await StartEasyTierAsync(HostOptions(Options.FullLan, Options.AllowExit), ct);
        }
        else
        {
            Bridge = new DirectBridge(socket, key, false, null, Log);
            Bridge.Connected += OnConnected;
            Bridge.ControlReceived += OnControl;
        }
        if (!Options.Simulated) await StartNatKeepAliveAsync(ct);
        var session = IsHost ? Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) : _remote?.Session ?? "";
        LocalAddresses = new DirectAddresses(session.Length >= 8 ? session : "0000000000000000", _gathered.Nat, _gathered.Candidates);
        return LocalAddresses;
    }

    /// <summary>
    /// 码里有 STUN 查到的外部地址时，让桥一直往 STUN 服务器发保活包，免得等对方贴码的时候 NAT 映射被回收、码里的端口失效。
    /// 解析不了服务器就不保活（不影响建房间/加入）。
    /// </summary>
    private async Task StartNatKeepAliveAsync(CancellationToken ct)
    {
        if (Bridge is null || _gathered?.Candidates.FirstOrDefault(c => c.Kind == PathKind.Punch) is not { } punch) return;
        foreach (var server in Options.StunServers.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            IPEndPoint target;
            try
            {
                target = await Stun.ResolveAsync(server.Trim(), ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch (Exception e) when (e is SocketException or FormatException or ArgumentException or TimeoutException)
            {
                continue;
            }
            // 对称 NAT 每个目的地址一个端口，保活服务器看到的端口本来就和码里的不同，不比较。
            Bridge.StartNatKeepAlive(target, _gathered.Nat == NatKind.Cone ? punch.EndPoint : null);
            return;
        }
    }

    /// <summary>Host: how long the bridge keeps taking new peers after an approved guest sent its addresses.</summary>
    internal static TimeSpan ExpectGuestWindow { get; set; } = TimeSpan.FromMinutes(3);

    private bool ExpectingGuest => DateTime.UtcNow < _expectGuestUntil;

    /// <summary>Host: the addresses offered to approved guests (the knock server puts them in the encrypted reply).</summary>
    public DirectAddresses? Offer() => IsHost ? LocalAddresses : null;

    /// <summary>
    /// 换到新的房间网络（房主踢人之后）：按现在的参数换上新房间重启 EasyTier。直连桥不动（它的钥匙和房间无关）。
    /// </summary>
    public async Task SwitchRoomAsync(RoomCode next, CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _room = next;
            if ((EasyTier?.Options ?? _currentOptions) is { } current) await StartEasyTierLockedAsync(current with { Room = next }, ct, force: true);
        }
        finally
        {
            _startLock.Release();
            StateChanged?.Invoke();
        }
    }

    private static int FreeUdpPort()
    {
        using var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.Client.LocalEndPoint!).Port;
    }

    private EasyTierOptions HostOptions(bool tun, bool allowExit, IReadOnlyList<string>? extra = null) =>
        new(Room, true, Options.FallbackNodes, EasyTierArgs.FreeTcpPort(), null, [$"udp://127.0.0.1:{_easyTierUdpPort}"], _machineId, tun, null, allowExit, null,
            Verbose(extra)) { ServesSpeedTest = true, DirectUnderlayAddresses = UnderlayAddresses() };

    private EasyTierOptions GuestOptions(bool viaBridge, bool tun, string? exitNode, IReadOnlyList<string>? extra = null, bool nodes = true)
    {
        var peers = new List<string>();
        if (viaBridge && Bridge is not null) peers.Add($"udp://127.0.0.1:{Bridge.LocalPort}");
        if (nodes) peers.AddRange(Options.FallbackNodes);
        return new EasyTierOptions(Room, false, peers, EasyTierArgs.FreeTcpPort(), tun ? null : LocalGamePort, null, _machineId, tun,
            tun ? null : LocalChatPort, false, exitNode, Verbose(extra), [(LocalSpeedPort, SpeedTestServer.Port)])
        {
            LocalRemotePort = tun ? null : LocalRemotePort,
            DirectUnderlayAddresses = UnderlayAddresses(),
        };
    }

    private IReadOnlyList<string> UnderlayAddresses() => CollectDirectUnderlayAddresses(
        (_remote?.Candidates ?? _guestCandidates).Select(c => c.EndPoint.Address),
        Bridge?.Peers.Select(p => p.Remote.Address) ?? []);

    /// <summary>
    /// 出口接管系统流量之前，直连桥的物理对端仍须走原网络；包含打洞后变化的端口对应地址。IPv4 and IPv6 both: a candidate
    /// IPv6 address the exit can't protect (link-local, say) is left out, but one the bridge is actually connected over is
    /// kept, so the plan refuses it with a reason instead of the gate silently cutting the connection.
    /// </summary>
    internal static IReadOnlyList<string> CollectDirectUnderlayAddresses(IEnumerable<IPAddress> candidates, IEnumerable<IPAddress> connected)
    {
        static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        return candidates.Select(Normal).Where(a => a.AddressFamily == AddressFamily.InterNetwork
                || a.AddressFamily == AddressFamily.InterNetworkV6 && ExitNetworkPlan.IsSafeUnderlay(a))
            .Concat(connected.Select(Normal).Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            .Select(a => a.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>手动切换出口和矩阵切换共用启动锁，直连会话一直拥有其 EasyTier 子会话。</summary>
    public async Task SetExitNodeAsync(string? exitNode, CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = EasyTier?.Options ?? _currentOptions;
            if (current is null || !current.Tun) throw new InvalidOperationException(L.T("core.mp.component.exitrequireslan"));
            var options = current with { ExitNode = exitNode, DirectUnderlayAddresses = UnderlayAddresses() };
            await StartEasyTierLockedAsync(options, ct);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        finally
        {
            _startLock.Release();
            StateChanged?.Invoke();
        }
    }

    private IReadOnlyList<string> Verbose(IReadOnlyList<string>? extra) =>
        [.. extra ?? [], .. Options.Verbose ? new[] { "--console-log-level", "debug" } : []];

    /// <summary>
    /// 启动或换参数重启 EasyTier。完整局域网第一次启动会弹权限确认；已经提权的会话换参数不再弹。
    /// 一次只做一个；拿到锁之后 ct 已经取消（比如测试已经结束）就不再启动。
    /// 和“正在跑的参数”比较（EasyTier.Options），不是上一次要求的参数；force 时总是重启。
    /// </summary>
    internal async Task StartEasyTierAsync(EasyTierOptions options, CancellationToken ct, bool force = false)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            await StartEasyTierLockedAsync(options, ct, force);
        }
        finally
        {
            _startLock.Release();
            StateChanged?.Invoke();
        }
    }

    // 调用方持有 _startLock；成功时才提交参数和启动计数，失败时关闭可能已部分启用出口的会话。
    private async Task StartEasyTierLockedAsync(EasyTierOptions options, CancellationToken ct, bool force = false)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!force && EasyTier is { HasExited: false } running && SameConfig(running.Options, options)) return;
        Log(L.T("core.mp.session.start") + string.Join(' ', EasyTierArgs.Build(options).Select(a => options.Room.Secrets.Contains(a) ? L.T("core.mp.diagnostics.secret") : a)));
        try
        {
            if (StartForTest is { } start)
            {
                var next = await start(options, ct);
                EasyTier?.Dispose();
                EasyTier = next;
            }
            else if (EasyTier is EasyTierElevatedSession elevated && !elevated.HasExited)
            {
                await elevated.RestartAsync(options, ct);
            }
            else
            {
                EasyTier?.Dispose();
                // 只有还活着的提权守护才能复用（上面的分支）；新建时只按本次参数决定是否提权。
                var elevatedStart = options.Tun;
                EasyTier = CreateSessionForTest is { } create ? create(elevatedStart, options)
                    : elevatedStart
                        ? EasyTierElevatedSession.Start(_installer, options, Path.Combine(_workDir, "run-lan"))
                        : EasyTierSession.Start(_installer, options, Path.Combine(_workDir, IsHost ? "run-direct-host" : "run-direct-guest"));
                if (EasyTier is EasyTierSession plain && Options.Verbose) plain.Output += line => _log.Add("EasyTier", NetDiagnostics.Redact(line, _room is { } r ? [.. r.Secrets] : [], includePublicIps: true));
                if (EasyTier is EasyTierElevatedSession fresh) await fresh.WaitForReadyAsync(ct);
            }
            if (!_disposed && (EasyTier is null || EasyTier.HasExited)) throw new IOException(L.T("core.mp.component.startfailed"));
            _currentOptions = options;
            EasyTierStarts++;
            // 启动途中（比如在等权限确认）用户已经离开：刚起来的 EasyTier 马上关掉，不留在后台。
            if (_disposed) EasyTier?.Dispose();
        }
        catch
        {
            var failed = EasyTier;
            EasyTier = null;
            _currentOptions = null;
            try { failed?.Dispose(); }
            catch (Exception stopError) { Log(L.T("core.mp.matrix.restorefailedsettings") + stopError.Message); }
            throw;
        }
    }

    /// <summary>加入方：同意了一次测试，给它一个取消源（最长 <see cref="GuestTestConsent.MaxDuration"/>）。</summary>
    internal void BeginRun(string run)
    {
        lock (_runGate)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource(GuestTestConsent.MaxDuration);
            _runCtsRun = run;
        }
    }

    /// <summary>测试用：按这次测试的取消令牌切换（和测试步骤一样）。</summary>
    internal Task StartForRunAsync(string run, EasyTierOptions options) => StartEasyTierAsync(options, RunToken(run));

    /// <summary>这次测试的取消令牌；不是正在进行的测试时返回已取消的令牌（什么都不执行）。</summary>
    private CancellationToken RunToken(string? run)
    {
        lock (_runGate) return _runCts is { } cts && run is not null && _runCtsRun == run ? cts.Token : new CancellationToken(true);
    }

    private void CancelRun(string? run)
    {
        lock (_runGate)
        {
            if (_runCts is null || run is not null && _runCtsRun != run) return;
            _runCts.Cancel();
        }
    }

    /// <summary>EasyTier 启动或重启过几次（包括已提权会话的原地重启）：界面按它判断要不要重新计时。</summary>
    public int EasyTierStarts { get; private set; }


    private static bool SameConfig(EasyTierOptions a, EasyTierOptions b) =>
        a.Tun == b.Tun && a.AllowExit == b.AllowExit && a.ExitNode == b.ExitNode && (a.ConfiguredPeers ?? a.Peers).SequenceEqual(b.ConfiguredPeers ?? b.Peers)
        && (a.ExtraArgs ?? []).SequenceEqual(b.ExtraArgs ?? [])
        && (a.ExitNode is null || (a.DirectUnderlayAddresses.SequenceEqual(b.DirectUnderlayAddresses)));

    /// <summary>
    /// Guest, during the knock: the host approved and sent its addresses inside the encrypted reply. Gathers this side's
    /// addresses (STUN, port mapping), remembers the host's, and returns this side's for the encrypted ack.
    /// </summary>
    public async Task<DirectAddresses?> AnswerAsync(DirectAddresses host, CancellationToken ct = default)
    {
        if (IsHost) throw new InvalidOperationException("host");
        _remote = host;
        var local = LocalAddresses ?? await PrepareAsync(ct);
        LocalAddresses = local with { Session = host.Session };
        Bridge!.SetRemoteCandidates(host.Candidates);
        Log(L.F("core.mp.session.hostnat", DirectText.Nat(host.Nat), host.Candidates.Count));
        return LocalAddresses;
    }

    /// <summary>
    /// Guest, after the knock: punches towards the host's addresses (the host punches towards ours at the same time),
    /// proves on the bridge that this is an approved member, and runs the room's EasyTier over the bridge. When punching
    /// fails and the room has relays, the room runs over the relays instead.
    /// </summary>
    /// <returns>True when the room runs over the direct bridge; false when it fell back to the relays.</returns>
    public async Task<bool> CompleteJoinAsync(RoomCode room, CancellationToken ct = default)
    {
        if (IsHost) throw new InvalidOperationException("host");
        _room = room;
        DirectPeer? peer = null;
        if (_remote is { } host && Bridge is { } bridge)
            peer = await bridge.PunchAsync(host.Candidates, host.Nat, TimeSpan.FromSeconds(host.DirectlyReachable ? 20 : 45), ct);
        if (peer is null)
        {
            var reason = _remote is { } h ? FailureReason(h.Nat, LocalNat, h.Candidates, _gathered?.Candidates ?? []) : L.T("core.mp.session.nooffer");
            Log(L.T("core.mp.session.failed") + reason);
            if (Options.FallbackNodes.Count == 0)
            {
                Log(L.T("core.mp.session.nofallback"));
                throw new DirectFailedException(reason);
            }
            Log(L.T("core.mp.session.fallback") + string.Join(L.T("core.mp.format.listseparator"), Options.FallbackNodes));
            UsingFallbackNode = true;
        }
        if (peer is not null && room.FromInvite) await AdmitAsync(room.Member ?? throw new InvalidOperationException("no member key"), ct);
        await StartEasyTierAsync(GuestOptions(peer is not null, Options.FullLan, null), ct);
        return peer is not null;
    }

    /// <summary>
    /// Guest of a sealed direct code (no server): after <see cref="AnswerAsync"/>, punches towards the host while the friend
    /// carries the sealed reply back, then knocks over the bridge (it reaches only the host's lobby), proves membership
    /// and runs the room over the bridge. When punching fails and relays are set, the knock and the room use the relays.
    /// </summary>
    /// <returns>False when the host turned the knock down (the reason is already shown by <paramref name="knock"/>).</returns>
    public async Task<bool> JoinThroughBridgeAsync(Func<IReadOnlyList<string>, CancellationToken, Task<RoomCode?>> knock, CancellationToken ct = default)
    {
        if (IsHost) throw new InvalidOperationException("host");
        if (_remote is not { } host || Bridge is not { } bridge) throw new InvalidOperationException("no offer");
        // The host can punch only once the friend has carried the reply back by hand: wait longer than in the knock flow.
        var peer = await bridge.PunchAsync(host.Candidates, host.Nat, TimeSpan.FromSeconds(host.DirectlyReachable ? 60 : 120), ct);
        IReadOnlyList<string> lobbyPeers;
        if (peer is not null) lobbyPeers = [$"udp://127.0.0.1:{bridge.LocalPort}"];
        else
        {
            var reason = FailureReason(host.Nat, LocalNat, host.Candidates, _gathered?.Candidates ?? []);
            Log(L.T("core.mp.session.failed") + reason);
            if (Options.FallbackNodes.Count == 0)
            {
                Log(L.T("core.mp.session.nofallback"));
                throw new DirectFailedException(reason);
            }
            Log(L.T("core.mp.session.fallback") + string.Join(L.T("core.mp.format.listseparator"), Options.FallbackNodes));
            UsingFallbackNode = true;
            lobbyPeers = Options.FallbackNodes;
        }
        if (await knock(lobbyPeers, ct) is not { } room) return false;
        _room = room;
        if (peer is not null && room.FromInvite) await AdmitAsync(room.Member ?? throw new InvalidOperationException("no member key"), ct);
        await StartEasyTierAsync(GuestOptions(peer is not null, Options.FullLan, null), ct);
        return true;
    }

    /// <summary>
    /// 加入方：向房主证明自己是批准过的成员（要一个挑战，再用成员钥匙对它做 HMAC）。UDP 会丢包，每秒重发，最多 <see cref="AdmitTimeout"/>。
    /// 控制消息里只有成员编号和 HMAC，没有密钥。
    /// </summary>
    internal async Task AdmitAsync(MemberKey member, CancellationToken ct)
    {
        if (Bridge?.Peers.FirstOrDefault() is not { } peer || LocalAddresses is not { } local) throw new DirectFailedException(L.T("core.mp.direct.admit_timeout"));
        var challenge = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _challenge = challenge;
        _admitReply = reply;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AdmitTimeout);
        try
        {
            byte[] nonce;
            while (true)
            {
                await Bridge.SendControlAsync(peer, new JsonObject { ["op"] = "admit-start" });
                try { nonce = await challenge.Task.WaitAsync(TimeSpan.FromSeconds(1), limit.Token); break; }
                catch (TimeoutException) { }
            }
            var proof = DirectKeys.AdmitProof(member, local.Session, nonce);
            var message = new JsonObject { ["op"] = "admit", ["id"] = member.IdHex, ["mac"] = Convert.ToBase64String(proof) };
            bool ok;
            while (true)
            {
                await Bridge.SendControlAsync(peer, message);
                try { ok = await reply.Task.WaitAsync(TimeSpan.FromSeconds(1), limit.Token); break; }
                catch (TimeoutException) { }
            }
            if (!ok) throw new DirectFailedException(L.T("core.mp.direct.admit_refused"));
            Log(L.T("core.mp.direct.admit_ok"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DirectFailedException(L.T("core.mp.direct.admit_timeout"));
        }
        finally
        {
            _challenge = null;
            _admitReply = null;
        }
    }

    /// <summary>房主：加入方报上成员编号和对挑战的 HMAC。对得上（批准过、没作废）就把这条桥接到房间。</summary>
    internal bool TryAdmit(DirectPeer peer, JsonObject message)
    {
        if (Bridge is not { } bridge || !HasRoom || Room.Members is not { } members || LocalAddresses is not { } local || peer.Challenge is not { } challenge) return false;
        if (Str(message, "id") is not { Length: MemberKey.IdLength * 2 } idHex || Str(message, "mac") is not { } macText) return false;
        byte[] id, mac;
        try
        {
            id = Convert.FromHexString(idHex);
            mac = Convert.FromBase64String(macText);
        }
        catch (FormatException)
        {
            return false;
        }
        if (mac.Length != 32 || !members.TryGet(id, out var entry) || entry.IsHost || entry.Revoked) return false;
        byte[] expected;
        try
        {
            expected = DirectKeys.AdmitProof(entry.Key, local.Session, challenge);
        }
        catch (CryptographicException)
        {
            // 查到之后、算证明之前他刚好被踢（钥匙已清零）：按拒绝处理。
            return false;
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, mac)) return false;
        return bridge.Admit(peer, entry);
    }

    /// <summary>
    /// Host: an approved guest sent its addresses inside the encrypted knock ack. Opens the bridge to it for a short while
    /// and punches towards it (the guest punches towards us at the same time).
    /// </summary>
    public async Task<bool> AddGuestAsync(DirectAddresses reply, CancellationToken ct = default)
    {
        if (!IsHost || LocalAddresses is not { } local || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(reply.Session), Encoding.UTF8.GetBytes(local.Session)))
            throw new DirectFailedException(L.T("core.mp.code.wrongroom"));
        _expectGuestUntil = DateTime.UtcNow + ExpectGuestWindow;
        _guestCandidates = reply.Candidates;
        _guestNat = reply.Nat;
        Bridge!.SetRemoteCandidates(reply.Candidates);
        var peer = await Bridge.PunchAsync(reply.Candidates, reply.Nat, TimeSpan.FromSeconds(60), ct);
        if (peer is null) Log(L.T("core.mp.session.punchfailed") + FailureReason(LocalNat, reply.Nat, _gathered?.Candidates ?? [], reply.Candidates));
        return peer is not null;
    }

    /// <summary>说清楚为什么连不上。</summary>
    public static string FailureReason(NatKind a, NatKind b, IReadOnlyList<Candidate> aCands, IReadOnlyList<Candidate> bCands)
    {
        var noV6 = !aCands.Any(c => c.Kind == PathKind.IPv6) || !bCands.Any(c => c.Kind == PathKind.IPv6);
        if (a == NatKind.Symmetric && b == NatKind.Symmetric)
            return L.T(noV6 ? "core.mp.failure.symmetric_noipv6" : "core.mp.failure.symmetric");
        if (a == NatKind.Unknown || b == NatKind.Unknown)
            return L.T(noV6 ? "core.mp.failure.unknown_noipv6" : "core.mp.failure.unknown");
        return L.T(noV6 ? "core.mp.failure.timeout_noipv6" : "core.mp.failure.timeout");
    }

    private void OnConnected(DirectPeer peer)
    {
        if (!IsHost && Bridge is not null && _gathered is not null)
        {
            // Say which session this is. Addresses already went to the host inside the encrypted knock ack, so the hello
            // (authenticated, not encrypted) carries none.
            if (LocalAddresses is { } local) _ = Bridge.SendControlAsync(peer, Hello(local.Session));
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 加入方连上后发给房主的 hello：只有会话 id。
    /// 直连通道的控制消息只认证不加密，所以不带任何密钥，也不带地址（地址已经在加密的敲门里交换过）；
    /// 能通过 HMAC 校验只说明对方拿到过邀请（知道大厅密钥），还不能进房间。
    /// </summary>
    internal static JsonObject Hello(string session) => new() { ["op"] = "hello", ["s"] = session };

    /// <summary>房主：解析 hello，会话 id 要和自己的一致（别的房间或旧会话的 hello 不认）。</summary>
    internal static bool TryParseHello(JsonObject message, string expectedSession) =>
        (message["s"] as JsonValue)?.TryGetValue<string>(out var session) == true && DirectAddresses.IsValidSession(session)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(session), Encoding.UTF8.GetBytes(expectedSession));

    private void OnControl(DirectPeer peer, JsonObject message)
    {
        switch (Str(message, "op"))
        {
            case "hello" when IsHost:
                if (LocalAddresses is { } local && !TryParseHello(message, local.Session)) Log(L.F("core.mp.direct.admit_rejected", peer.Remote));
                break;
            case "admit-start" when IsHost && Bridge is { } challengeBridge:
                _ = challengeBridge.SendControlAsync(peer, new JsonObject { ["op"] = "challenge", ["n"] = Convert.ToBase64String(challengeBridge.ChallengeFor(peer)) });
                break;
            case "admit" when IsHost:
                var admitted = TryAdmit(peer, message);
                if (!admitted) Log(L.F("core.mp.direct.admit_rejected", peer.Remote));
                _ = Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "admit-result", ["ok"] = admitted });
                if (admitted) StateChanged?.Invoke();
                break;
            case "challenge" when !IsHost:
                if (Str(message, "n") is { } n && _challenge is { } waiting)
                {
                    try
                    {
                        if (Convert.FromBase64String(n) is { Length: 16 } nonce) waiting.TrySetResult(nonce);
                    }
                    catch (FormatException) { }
                }
                break;
            case "admit-result" when !IsHost:
                if (Bool(message, "ok") is { } ok) _admitReply?.TrySetResult(ok);
                break;
            case "test-request" when !IsHost:
                if (Str(message, "run") is not { Length: >= 8 and <= 64 } requestRun) break;
                if (TestConsent.Request(new TestRequest(requestRun, Bool(message, "fullLan") == true), out var restoring))
                {
                    Log(L.T("core.mp.matrix.requested"));
                    WatchPending(requestRun);
                    TestRequested?.Invoke(new TestRequest(requestRun, Bool(message, "fullLan") == true));
                }
                else if (restoring)
                {
                    // 旧测试还在恢复：明确拒绝，房主不用等确认超时；也不能把临时配置记成新测试的原设置。
                    _ = Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "test-answer", ["run"] = requestRun, ["ok"] = false });
                }
                break;
            case "test-end" when !IsHost:
                var withdrew = TestConsent.Withdraw(Str(message, "run") ?? "");
                _ = EndGuestTestAsync(Str(message, "run"), notify: withdrew);
                break;
            case "step" when !IsHost:
                _ = GuestStepAsync(peer, message);
                break;
            case "elevate" when !IsHost:
                _ = GuestElevateAsync(peer, Str(message, "run"));
                break;
            case "test-abort" when IsHost:
                // 加入方的用户点了“停止测试”。
                if (Str(message, "run") is { } abortRun && abortRun == _hostRun) _hostAbort?.Cancel();
                break;
            case "test-answer" when IsHost:
                if (Str(message, "run") is { } answerRun && answerRun == _hostRun) _testAnswer?.TrySetResult(message);
                break;
            case "elevated" when IsHost:
                if (Str(message, "run") == _hostRun) _elevateReply?.TrySetResult(Bool(message, "ok") == true);
                break;
            case "result" when IsHost:
                if (Str(message, "run") != _hostRun) break;
                var index = Int(message, "i") ?? -1;
                lock (_pending)
                    if (_pending.Remove(index, out var tcs)) tcs.TrySetResult(message);
                break;
        }
    }


    // 对方发来的 JSON 字段类型不对时当作没有，不抛异常。
    private static string? Str(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<string>(out var v) == true ? v : null;
    private static bool? Bool(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<bool>(out var v) == true ? v : null;
    private static int? Int(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<int>(out var v) == true ? v : null;
    private static long? Long(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<long>(out var v) == true ? v : null;
    private static double? Double(JsonObject m, string key) => (m[key] as JsonValue)?.TryGetValue<double>(out var v) == true ? v : null;

    /// <summary>
    /// 加入方：用户回答房主的测试请求。同意时只同意勾上的部分（完整局域网、把房主当出口），记下现在的联机设置，测完恢复。
    /// </summary>
    /// <returns>真的同意并且告诉了房主时返回 true（请求已经作废、断开、拒绝都是 false）。</returns>
    public async Task<bool> AnswerTestRequestAsync(bool accept, bool allowFullLan, bool allowExit)
    {
        var run = TestConsent.Pending?.Run;
        _pendingWatch?.Dispose();
        _pendingWatch = null;
        if (run is null)
        {
            // 请求已经作废（超时、房主撤回）：界面收起确认。
            TestEnded?.Invoke(run);
            return false;
        }
        if (Bridge?.Peers.FirstOrDefault() is not { } peer)
        {
            // 和房主断开了：当作拒绝（不留下一个没人知道的“已同意”）。
            TestConsent.Answer(false, false, false, null);
            Log(L.T("core.mp.matrix.nopeerdeclined"));
            TestEnded?.Invoke(run);
            return false;
        }
        // 记下现在真正在跑的参数（不是上次要求的），测完恢复成它。
        var answer = TestConsent.Answer(accept, allowFullLan, allowExit, EasyTier?.Options ?? _currentOptions);
        if (answer is { } a)
        {
            BeginRun(run);
            Log(L.T("core.mp.matrix.accepted"));
            _consentWatch?.Dispose();
            _consentWatch = new Timer(_ =>
            {
                if (TestConsent.IsIdle(out var idleRun))
                {
                    Log(L.T("core.mp.matrix.consentexpired"));
                    _ = EndGuestTestAsync(idleRun);
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }
        await Bridge.SendControlAsync(peer, new JsonObject
        {
            ["op"] = "test-answer",
            ["run"] = run,
            ["ok"] = answer is not null,
            ["fullLan"] = answer?.FullLan == true,
            ["exit"] = answer?.Exit == true,
        });
        return answer is not null;
    }

    /// <summary>等确认的请求放太久（<see cref="GuestTestConsent.PendingLimit"/>）就作废，界面收起确认。</summary>
    private void WatchPending(string run)
    {
        _pendingWatch?.Dispose();
        _pendingWatch = new Timer(_ =>
        {
            if (TestConsent.ExpirePending(run))
            {
                Log(L.T("core.mp.matrix.requestexpired"));
                TestEnded?.Invoke(run);
            }
        }, null, GuestTestConsent.PendingLimit + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    /// <summary>加入方：用户点了“停止测试”。告诉房主（尽力而为），然后自己结束并恢复。</summary>
    /// <remarks>
    /// 还没回答的请求当作拒绝。已经同意的：先清掉同意、放开所有路径、断了就重新打洞，再通知房主，最后恢复 EasyTier
    /// （停在“直连只开一条路径”的那一步时，不先恢复路径就发不出通知）。
    /// </remarks>
    public async Task StopGuestTestAsync()
    {
        if (TestConsent.ActiveRun is not { } run)
        {
            if (TestConsent.Pending is not null) await AnswerTestRequestAsync(false, false, false);
            return;
        }
        Log(L.T("core.mp.matrix.gueststopped"));
        await EndGuestTestAsync(run, beforeRestore: async () =>
        {
            if (Bridge?.Peers.FirstOrDefault() is { } peer)
            {
                try { await Bridge.SendControlAsync(peer, new JsonObject { ["op"] = "test-abort", ["run"] = run }); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
        });
    }

    /// <summary>
    /// 加入方：测试结束（房主说结束、太久没动静、超过最长时间、用户点停止）：清掉同意，取消还在路上的步骤，
    /// 在启动锁里恢复所有路径和原来的联机设置；参数未变且进程仍在时保留游戏连接。
    /// </summary>
    /// <param name="notify">不是这次测试（比如只是撤回了还没回答的请求）时也通知界面。</param>
    /// <param name="beforeRestore">放开路径之后、恢复 EasyTier 之前做的事（停止时通知房主）。</param>
    internal async Task EndGuestTestAsync(string? run, bool notify = false, Func<Task>? beforeRestore = null)
    {
        var ended = TestConsent.End(run, out var original);
        try
        {
            if (!ended) return;
            CancelRun(run);
            _consentWatch?.Dispose();
            _consentWatch = null;
            try
            {
                if (Bridge is { } bridge)
                {
                    RestoreBridgePaths();
                    // 停在直连某一步时连接可能已经断了：重新打洞，好让通知和之后的联机走得通。
                    if (bridge.Peers.Count == 0 && _remote is { } remote && !_disposed)
                        await bridge.PunchAsync(remote.Candidates, remote.Nat, TimeSpan.FromSeconds(30));
                }
                if (beforeRestore is not null) await beforeRestore();
            }
            catch (Exception e)
            {
                Log(L.T("core.mp.matrix.restorefailedsettings") + e.Message);
            }
            if (original is not null && !_disposed)
            {
                try
                {
                    await StartEasyTierAsync(original with { RpcPort = EasyTierArgs.FreeTcpPort() }, CancellationToken.None);
                    Log(L.T("core.mp.matrix.restored"));
                }
                catch (Exception e)
                {
                    // 恢复失败也不能继续保留测试用的完整局域网/出口，下一次只能从原配置重新开始。
                    var failed = EasyTier;
                    EasyTier = null;
                    _currentOptions = original;
                    try { failed?.Dispose(); }
                    catch (Exception stopError) { Log(L.T("core.mp.matrix.restorefailedsettings") + stopError.Message); }
                    Log(L.T("core.mp.matrix.restorefailedsettings") + e.Message);
                    StateChanged?.Invoke();
                }
            }
        }
        finally
        {
            try
            {
                if (ended || notify) TestEnded?.Invoke(run);
            }
            finally
            {
                // 通知处理器抛错也必须结束恢复；迟到的通知携带 run，不会收起下一次测试的界面。
                if (ended) TestConsent.CompleteEnd(run);
            }
        }
    }

    /// <summary>当前连接走的路径，界面显示用。</summary>
    public string DescribePath(IReadOnlyList<PeerInfo> peers)
    {
        var bridgePeer = Bridge?.Peers.FirstOrDefault();
        if (!IsHost)
        {
            var host = peers.FirstOrDefault(p => p.Ip == EasyTierArgs.HostIp && p.Cost != "Local");
            if (host is null) return UsingFallbackNode ? L.T("core.mp.session.nodeconnecting") : L.T("core.mp.session.connecting");
            if (UsingFallbackNode || bridgePeer is null) return L.T(host.Cost.StartsWith("relay", StringComparison.Ordinal) ? "core.mp.path.vianode_relay" : "core.mp.path.vianode_introduced");
            return L.F("core.mp.session.path", DirectText.Path(bridgePeer.Path), bridgePeer.RttMs is { } r ? $"{r:F0} ms" : "-", bridgePeer.LossPercent);
        }
        var guests = Bridge?.Peers ?? [];
        return guests.Count == 0 ? L.T("core.mp.session.waiting") : string.Join(L.T("core.mp.format.peerseparator"), guests.Select(g => $"{DirectText.Path(g.Path)} {(g.RttMs is { } r ? $"{r:F0} ms" : "")}"));
    }

    // ---------------- 全条件测试 ----------------

    /// <summary>房主：等加入方确认多久。</summary>
    public static readonly TimeSpan TestAnswerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 房主：先请加入方确认（加入方用户点了同意才开始，完整局域网和出口要它分别勾选），
    /// 再问双方能不能提权，然后排出步骤依次执行。结束、出错、取消都通知加入方结束并恢复原来的设置。
    /// </summary>
    public async Task<List<MatrixResult>> RunMatrixAsync(bool wantFullLan, IProgress<MatrixResult>? progress, Func<MatrixStep, bool, CancellationToken, Task<bool?>>? chatProbe, CancellationToken ct)
    {
        if (!IsHost || Bridge?.Peers.FirstOrDefault() is not { } peer) throw new InvalidOperationException(L.T("core.mp.matrix.peerrequired"));
        var run = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _hostAbort = abort;
        var callerCt = ct;
        ct = abort.Token;
        _hostRun = run;
        _testAnswer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Bridge.SendControlAsync(peer, new JsonObject { ["op"] = "test-request", ["run"] = run, ["fullLan"] = wantFullLan });
            JsonObject answer;
            try
            {
                answer = await _testAnswer.Task.WaitAsync(TestAnswerTimeout, ct);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(L.T("core.mp.matrix.requesttimeout"));
            }
            if (Bool(answer, "ok") != true) throw new InvalidOperationException(L.T("core.mp.matrix.declined"));
            var guestFullLan = Bool(answer, "fullLan") == true;
            var guestExit = Bool(answer, "exit") == true;
            var fullLan = false;
            if (wantFullLan && guestFullLan)
            {
                fullLan = await ElevateBothAsync(peer, run, ct);
                Log(fullLan ? L.T("core.mp.matrix.elevated") : L.T("core.mp.matrix.elevationdeclined"));
            }
            var members = (await (EasyTier?.PeersAsync(ct) ?? Task.FromResult(new List<PeerInfo>()))).Count(p => p.Ip.StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal));
            var plan = TestMatrix.Plan(new MatrixInputs(_gathered!.Candidates, _guestCandidates, members, Options.FallbackNodes.Count > 0, fullLan, Options.Simulated));
            // 加入方没同意把房主当出口：出口那几步不测。
            if (!guestExit) plan = [.. plan.Where(s => !s.Exit).Select((s, i) => s with { Index = i + 1 })];
            Log(L.P("core.mp.matrix.plan", plan.Count, plan.Count));
            _chatProbe = chatProbe;
            return await TestMatrix.RunAsync(plan, new HostRunner(this, run), MatrixStepTimeout, progress, Options.Simulated, ct);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested && !callerCt.IsCancellationRequested)
        {
            throw new InvalidOperationException(L.T("core.mp.matrix.guestaborted"));
        }
        finally
        {
            _hostRun = null;
            _hostAbort = null;
            _testAnswer = null;
            _elevateReply = null;
            _chatProbe = null;
            // 恢复：所有路径、原来的模式；通知加入方结束（它会恢复自己的设置）。
            RestoreBridgePaths();
            try
            {
                if (!_disposed && Bridge.Peers.Count == 0 && _guestCandidates.Count > 0)
                    await Bridge.PunchAsync(_guestCandidates, _guestNat, TimeSpan.FromSeconds(30));
                if (Bridge.Peers.FirstOrDefault() is { } back)
                    await Bridge.SendControlAsync(back, new JsonObject { ["op"] = "test-end", ["run"] = run });
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                Log(L.T("core.mp.matrix.restorefailedsettings") + e.Message);
            }
            if (!_disposed) await StartEasyTierAsync(HostOptions(Options.FullLan, Options.AllowExit), CancellationToken.None);
        }
    }

    private Func<MatrixStep, bool, CancellationToken, Task<bool?>>? _chatProbe;

    /// <summary>加入方的聊天探测（界面设置）：发一条消息、等回显。</summary>
    public Func<MatrixStep, CancellationToken, Task<bool?>>? GuestChatProbe { get; set; }

    /// <summary>两侧都检查当前会话；守护已退出时重新启动，不能把过去提过权当作现在仍然可用。</summary>
    internal Task EnsureElevatedAsync(CancellationToken ct) => StartEasyTierAsync(
        IsHost ? HostOptions(true, Options.AllowExit) : GuestOptions(!UsingFallbackNode, true, null), ct);

    private async Task<bool> ElevateBothAsync(DirectPeer peer, string run, CancellationToken ct)
    {
        try
        {
            await EnsureElevatedAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        _elevateReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevate", ["run"] = run });
        try
        {
            return await _elevateReply.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>加入方：房主要提权。只在用户同意了这次测试、并且勾了完整局域网时执行。</summary>
    private async Task GuestElevateAsync(DirectPeer peer, string? run)
    {
        var ok = TestConsent.AllowsElevate(run);
        if (!ok) Log(L.T("core.mp.matrix.notconsented"));
        else
        {
            try
            {
                await EnsureElevatedAsync(RunToken(run));
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                ok = false;
                Log(L.T("core.mp.matrix.elevationfailed") + e.Message);
            }
        }
        await Bridge!.SendControlAsync(peer, new JsonObject { ["op"] = "elevated", ["run"] = run, ["ok"] = ok });
    }

    private sealed class HostRunner(DirectSession s, string run) : IMatrixStepRunner
    {
        public async Task<MatrixResult> RunAsync(MatrixStep step, CancellationToken ct)
        {
            var peer = s.Bridge!.Peers.FirstOrDefault() ?? throw new InvalidOperationException(L.T("core.mp.matrix.disconnected"));
            var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (s._pending) s._pending[step.Index] = reply;
            using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task punch = Task.CompletedTask, restore = Task.CompletedTask, chat = Task.CompletedTask;
            try
            {
                s.Log(L.F("core.mp.matrix.step", step.Index, step.Label));
                await s.Bridge.SendControlAsync(peer, new JsonObject
                {
                    ["op"] = "step",
                    ["run"] = run,
                    ["i"] = step.Index,
                    ["route"] = step.Route.ToString(),
                    ["path"] = step.Path?.ToString(),
                    ["fullLan"] = step.FullLan,
                    ["exit"] = step.Exit,
                    ["hostIp"] = s.PublicIPv4?.ToString(),
                });
                // 同样只开这条路径；保留本地传输端点，让同配置的 EasyTier 隧道能继续收发。
                await s.ApplyStepAsync(step, work.Token);
                if (step.Route == RouteKind.Direct)
                    punch = s.Bridge.PunchAsync(s._guestCandidates, s._guestNat, TimeSpan.FromSeconds(30), work.Token);
                if (s._chatProbe is { } probe) chat = probe(step, true, work.Token);
                restore = s.RestoreStepConnectionAsync(work.Token);
                var message = await reply.Task.WaitAsync(work.Token);
                return new MatrixResult(step,
                    Bool(message, "passed"),
                    Long(message, "connectMs"),
                    Double(message, "latency"),
                    Double(message, "mbps"),
                    Bool(message, "chat"),
                    Bool(message, "exit"),
                    Str(message, "reason") ?? "");
            }
            finally
            {
                // 成功、超时、取消都先收束本步任务，迟到的恢复不能改动下一步的路径限制。
                work.Cancel();
                try { await Task.WhenAll(punch, restore, chat); }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { }
                catch (Exception e) { s.Log(L.T("core.mp.matrix.restorefailedsettings") + e.Message); }
                lock (s._pending) s._pending.Remove(step.Index);
                s.RestoreBridgePaths();
            }
        }
    }

    private void RestoreBridgePaths(CancellationToken ct = default)
    {
        lock (_pathGate)
        {
            ct.ThrowIfCancellationRequested();
            if (Bridge is not { } bridge) return;
            bridge.AllowedPaths.UnionWith(Enum.GetValues<PathKind>());
            bridge.SprayPorts = 256;
        }
    }

    private async Task RestoreStepConnectionAsync(CancellationToken ct)
    {
        await Task.Delay(MatrixRecoveryDelay, ct);
        ct.ThrowIfCancellationRequested();
        if (Bridge!.Peers.Count == 0)
        {
            RestoreBridgePaths(ct);
            await Bridge.PunchAsync(_guestCandidates, _guestNat, TimeSpan.FromSeconds(60), ct);
        }
    }

    /// <summary>两边都要做的切换：只允许这一步的路径、重新认证直连，按需切换 EasyTier 配置。</summary>
    private async Task ApplyStepAsync(MatrixStep step, CancellationToken ct)
    {
        if (step.Exit && step.Path == PathKind.IPv6)
            throw new InvalidOperationException(L.T("core.mp.exit.ipv6path"));
        var bridge = Bridge!;
        lock (_pathGate)
        {
            ct.ThrowIfCancellationRequested();
            if (step.Route == RouteKind.Direct)
            {
                bridge.AllowedPaths.Clear();
                bridge.AllowedPaths.Add(step.Path!.Value);
                if (step.Path == PathKind.PunchSpray) bridge.AllowedPaths.Add(PathKind.Punch);
                bridge.SprayPorts = step.Path == PathKind.PunchSpray ? 256 : 0;
                bridge.DropPeers(preserveLocalTransport: true);
            }
        }
        var relay = step.Route == RouteKind.Relay ? new[] { "--disable-p2p" } : null;
        if (IsHost)
        {
            await StartEasyTierAsync(HostOptions(step.FullLan, step.Exit), ct);
            // 完整局域网模式下加入方直接连房主的虚拟 IP 测吞吐，等虚拟网卡起来后再监听上去。
            if (step.FullLan && _speed is not null && !_speedOnVirtualIp)
                for (var i = 0; i < 20 && !(_speedOnVirtualIp = _speed.TryAddListener(IPAddress.Parse(EasyTierArgs.HostIp))); i++) await Task.Delay(500, ct);
        }
        else await StartEasyTierAsync(GuestOptions(step.Route != RouteKind.Node, step.FullLan, step.Exit ? EasyTierArgs.HostIp : null, relay, step.Route != RouteKind.Direct), ct);
    }

    /// <summary>解析房主发来的一步；字段缺失或类型不对时返回 null。</summary>
    internal static MatrixStep? ParseStep(JsonObject message)
    {
        if (Int(message, "i") is not { } index || Str(message, "route") is not { } routeText
            || !Enum.TryParse<RouteKind>(routeText, out var route) || !Enum.IsDefined(route)) return null;
        PathKind? path = null;
        if (Str(message, "path") is { } pathText)
        {
            if (!Enum.TryParse<PathKind>(pathText, out var parsed) || !Enum.IsDefined(parsed)) return null;
            path = parsed;
        }
        if (route == RouteKind.Direct && path is null) return null;
        return new MatrixStep(index, route, path, Bool(message, "fullLan") == true, Bool(message, "exit") == true);
    }

    internal async Task GuestStepAsync(DirectPeer controlPeer, JsonObject message)
    {
        var run = Str(message, "run");
        if (ParseStep(message) is not { } step) return;
        // 没同意、旧的、重复的、乱序的、超出同意范围（完整局域网、出口）的一步都不执行，只回一个失败。
        if (TestConsent.BeginStep(run, step.Index, step.FullLan, step.Exit) is { } refused)
        {
            Log(L.F("core.mp.matrix.steprefused", step.Index, refused));
            await Bridge!.SendControlAsync(controlPeer, new JsonObject { ["op"] = "result", ["run"] = run, ["i"] = step.Index, ["passed"] = false, ["reason"] = L.T("core.mp.matrix.notconsented") });
            return;
        }
        JsonObject? result = null;
        try
        {
            GuestStepStarted?.Invoke(run!, step);
            result = await GuestRunStepAsync(step, Str(message, "hostIp"), run);
        }
        catch (OperationCanceledException) when (RunToken(run).IsCancellationRequested) { }
        finally
        {
            // 房主收到结果就可能发下一步，因此必须先释放本步状态，再发结果。
            TestConsent.EndStep(run);
        }
        if (result is not null && !RunToken(run).IsCancellationRequested && Bridge!.Peers.FirstOrDefault() is { } back)
            await Bridge.SendControlAsync(back, result);
    }

    private async Task<JsonObject?> GuestRunStepAsync(MatrixStep step, string? hostIp, string? run)
    {
        Log(L.F("core.mp.matrix.step", step.Index, step.Label));
        var result = new JsonObject { ["op"] = "result", ["run"] = run, ["i"] = step.Index };
        // 测试结束（取消这次测试）时这一步跟着停，不会在恢复之后再切换。
        var runToken = RunToken(run);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        cts.CancelAfter(TimeSpan.FromSeconds(100));
        var watch = Stopwatch.StartNew();
        try
        {
            await ApplyStepAsync(step, cts.Token);
            if (step.Route == RouteKind.Direct)
            {
                var punched = await Bridge!.PunchAsync(_remote!.Candidates, _remote.Nat, TimeSpan.FromSeconds(30), cts.Token);
                if (punched is null) throw new DirectFailedException(L.F("core.mp.matrix.pathfailed", DirectText.Path(step.Path!.Value)));
            }
            // 等 EasyTier 看到房主。
            PeerInfo? host = null;
            for (var i = 0; i < 60 && host is null; i++)
            {
                await Task.Delay(500, cts.Token);
                host = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Ip == EasyTierArgs.HostIp && x.Cost != "Local");
            }
            if (host is null) throw new DirectFailedException(L.T("core.mp.matrix.hosttimeout"));
            result["connectMs"] = watch.ElapsedMilliseconds;
            if (step.Route == RouteKind.Relay)
            {
                var other = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Hostname == "amber-guest" && x.Cost.StartsWith("relay", StringComparison.Ordinal));
                if (other is null) throw new DirectFailedException(L.T("core.mp.matrix.nootherpeer"));
                result["latency"] = other.LatencyMs;
            }
            else
            {
                await Task.Delay(1500, cts.Token);
                result["latency"] = (await EasyTier!.PeersAsync(cts.Token)).FirstOrDefault(x => x.Ip == EasyTierArgs.HostIp && x.Cost != "Local")?.LatencyMs;
                var (speedHost, speedPort) = step.FullLan ? (EasyTierArgs.HostIp, SpeedTestServer.Port) : ("127.0.0.1", LocalSpeedPort);
                using var speedLimit = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                speedLimit.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    result["mbps"] = Math.Round(await SpeedTestServer.MeasureAsync(speedHost, speedPort, 4 * 1024 * 1024, speedLimit.Token), 1);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException && !cts.IsCancellationRequested)
                {
                    throw new DirectFailedException(L.T("core.mp.matrix.speedfailed") + e.Message);
                }
            }
            if (GuestChatProbe is { } chat)
            {
                result["chat"] = await chat(step, cts.Token);
                if (Bool(result, "chat") == false)
                    throw new DirectFailedException(L.F("core.mp.matrix.pathfailed", L.T("multiplayer.chat.title")));
            }
            if (step.Exit)
            {
                if (Options.Simulated || hostIp is null)
                {
                    result["exit"] = null;
                    result["reason"] = L.T("core.mp.matrix.simulatedexit");
                }
                else
                {
                    using var probe = DirectBridge.CreateSocket();
                    var mapped = await Stun.QueryAsync(probe, Options.StunServers.FirstOrDefault() ?? Stun.DefaultServers[0], TimeSpan.FromSeconds(4), cts.Token);
                    result["exit"] = mapped?.Address.ToString() == hostIp;
                    if (mapped is null) result["reason"] = L.T("core.mp.matrix.exitnostun");
                    else if (mapped.Address.ToString() != hostIp) result["reason"] = L.T("core.mp.matrix.exitwrongip");
                }
            }
            result["passed"] = Bool(result, "exit") != false;
        }
        catch (Exception e) when (e is DirectFailedException or OperationCanceledException or IOException or SocketException or InvalidOperationException)
        {
            result["passed"] = false;
            result["reason"] = e is OperationCanceledException ? L.T("core.mp.matrix.steptimeout") : e.Message;
            Log(L.F("core.mp.matrix.stepfailed", step.Index, result["reason"]));
        }
        // 测试已经结束：恢复由 EndGuestTestAsync 做，这里不再打洞、不再回结果。
        if (runToken.IsCancellationRequested) return null;
        // 恢复全部路径，确保能把结果发回去。
        var bridge = Bridge!;
        RestoreBridgePaths(runToken);
        // 留出房主 120 秒期限内的回报时间；结束测试时连恢复打洞也一起取消。
        if (bridge.Peers.Count == 0) await bridge.PunchAsync(_remote!.Candidates, _remote.Nat, TimeSpan.FromSeconds(15), runToken);
        if (bridge.Peers.Count > 0) return result;
        Log(L.F("core.mp.matrix.restorefailed", step.Index));
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        // 每一步单独兜住：前面哪一步出错，后面的（关 EasyTier、关桥、撤端口映射）照样做。
        void Try(Action step)
        {
            try { step(); }
            catch (Exception e) { Log(L.F("core.mp.punch.localerror", e.GetType().Name)); }
        }
        async Task TryAsync(Func<Task> step)
        {
            try { await step(); }
            catch (Exception e) { Log(L.F("core.mp.punch.localerror", e.GetType().Name)); }
        }
        _disposed = true;
        Try(() => _hostAbort?.Cancel());
        Try(() => _consentWatch?.Dispose());
        Try(() => _pendingWatch?.Dispose());
        Try(() =>
        {
            lock (_runGate)
            {
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = null;
            }
        });
        Try(() => EasyTier?.Dispose());
        if (_speed is not null) await TryAsync(() => _speed.DisposeAsync().AsTask());
        if (Bridge is not null) await TryAsync(() => Bridge.DisposeAsync().AsTask());
        if (_bridgeKey is { } bridgeKey) CryptographicOperations.ZeroMemory(bridgeKey);
        if (_gathered?.Mapping?.Remove is { } remove)
        {
            try
            {
                await remove().WaitAsync(TimeSpan.FromSeconds(3));
                Log(L.T("core.mp.mapping.removed"));
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or TimeoutException or OperationCanceledException) { }
        }
    }
}

public sealed class DirectFailedException(string message) : Exception(message);
