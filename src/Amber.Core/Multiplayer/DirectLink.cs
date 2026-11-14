using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>直连的几种路径，按优先顺序排列。</summary>
public enum PathKind
{
    /// <summary>IPv6 公网地址直连。</summary>
    IPv6,
    /// <summary>路由器映射的端口（UPnP / NAT-PMP）。</summary>
    Mapped,
    /// <summary>同一个局域网里的地址。</summary>
    Lan,
    /// <summary>IPv4 打洞（STUN 查到的外部地址，含端口预测）。</summary>
    Punch,
    /// <summary>IPv4 打洞加“生日”喷洒：对方是对称 NAT 时，往一批端口同时发。</summary>
    PunchSpray,
}

public sealed record Candidate(PathKind Kind, IPEndPoint EndPoint)
{
    public override string ToString() => $"{DirectText.Path(Kind)} {EndPoint}";
}

public static class DirectText
{
    public static string Path(PathKind kind) => kind switch
    {
        PathKind.IPv6 => L.T("core.mp.path.ipv6"),
        PathKind.Mapped => L.T("core.mp.path.mapped"),
        PathKind.Lan => L.T("core.mp.path.lan"),
        PathKind.Punch => L.T("core.mp.path.punch"),
        PathKind.PunchSpray => L.T("core.mp.path.spray"),
        _ => kind.ToString(),
    };

    public static string Nat(NatKind kind) => kind switch
    {
        NatKind.Open => L.T("core.mp.nat.open"),
        NatKind.Cone => L.T("core.mp.nat.cone"),
        NatKind.Symmetric => L.T("core.mp.nat.symmetric"),
        _ => L.T("core.mp.nat.unknown"),
    };
}

/// <summary>
/// One side's addresses for a direct link: a session id, the NAT type and the candidate addresses (public IPv4 from STUN,
/// IPv6, router-mapped ports, LAN addresses).
/// <para>
/// These are never written into any code a person copies or pastes. The host sends its addresses only to a guest it has
/// approved, inside the encrypted knock reply; the guest answers with its own inside the encrypted knock ack
/// (<see cref="KnockServer.DirectOffer"/>, <see cref="KnockClient.KnockAsync"/>). Someone who only holds an invite code
/// learns no address of either side.
/// </para>
/// </summary>
public sealed record DirectAddresses(string Session, NatKind Nat, IReadOnlyList<Candidate> Candidates)
{
    /// <summary>At most this many candidates are read from the other side (a gatherer finds a handful).</summary>
    public const int MaxCandidates = 16;

    /// <summary>The other side has IPv6, a mapped port or an open NAT: it can be reached without punching from both ends.</summary>
    public bool DirectlyReachable => Nat == NatKind.Open || Candidates.Any(c => c.Kind is PathKind.IPv6 or PathKind.Mapped);

    /// <summary>The JSON sent inside the encrypted knock.</summary>
    public JsonObject ToJson() => new()
    {
        ["s"] = Session,
        ["n"] = Nat.ToString(),
        ["c"] = new JsonArray(Candidates.Take(MaxCandidates).Select(c => (JsonNode?)new JsonArray(c.Kind.ToString(), c.EndPoint.ToString())).ToArray()),
    };

    /// <summary>Reads <see cref="ToJson"/>. A session id of the wrong shape is refused; unknown or broken candidates are skipped.</summary>
    public static bool TryParse(JsonObject? json, out DirectAddresses addresses)
    {
        addresses = null!;
        if (json is null) return false;
        try
        {
            var session = (json["s"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "";
            if (!IsValidSession(session)) return false;
            var nat = (json["n"] as JsonValue)?.TryGetValue<string>(out var n) == true && Enum.TryParse<NatKind>(n, out var kind) && Enum.IsDefined(kind) ? kind : NatKind.Unknown;
            var candidates = new List<Candidate>();
            foreach (var item in json["c"] as JsonArray ?? [])
            {
                if (candidates.Count >= MaxCandidates) break;
                if (item is not JsonArray pair || pair.Count != 2) continue;
                if ((pair[0] as JsonValue)?.TryGetValue<string>(out var k) == true && Enum.TryParse<PathKind>(k, out var path) && Enum.IsDefined(path)
                    && (pair[1] as JsonValue)?.TryGetValue<string>(out var e) == true && IPEndPoint.TryParse(e, out var ep) && ep.Port > 0)
                    candidates.Add(new Candidate(path, ep));
            }
            addresses = new DirectAddresses(session, nat, candidates);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Session ids are 8–64 ASCII letters or digits (the host makes 16 hex digits).</summary>
    public static bool IsValidSession(string? session) => session is { Length: >= 8 and <= 64 } && session.All(char.IsAsciiLetterOrDigit);

    /// <summary>Never prints an address (this ends up in logs and exception messages).</summary>
    public override string ToString() => $"DirectAddresses({Candidates.Count})";
}

/// <summary>
/// Codes of the removed direct-link format: JAY2D- / JAY2R- (and the even older AMBDIRECT / AMBREPLY). They carried both
/// sides' public IP addresses in plain text, so they are refused; the paste box tells the person to ask for a current invite.
/// </summary>
public static class OldDirectCodes
{
    private static readonly string[] Prefixes = ["JAY2D-", "JAY2R-", "AMBDIRECT", "AMBREPLY"];

    public static bool Contains(string? text) =>
        text is not null && Prefixes.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 直连用的两种钥匙，都用 HKDF-SHA256（salt = "jay-direct-v1"）派生，和房间密钥无关：
/// 桥的探测 / 控制包 HMAC 钥匙来自邀请里的大厅密钥 L；批准后的“这条桥是我的”证明来自这个成员自己的 psk。
/// </summary>
public static class DirectKeys
{
    private static readonly byte[] Salt = "jay-direct-v1"u8.ToArray();

    private static byte[] Derive(ReadOnlySpan<byte> key, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, output, Salt, Encoding.ASCII.GetBytes(info));
        return output;
    }

    /// <summary>直连桥探测、心跳、控制包的 HMAC 钥匙。</summary>
    public static byte[] Bridge(ReadOnlySpan<byte> lobbyKey) => Derive(lobbyKey, "bridge");

    /// <summary>
    /// 批准后的证明：加入方用自己成员钥匙的 psk 对房主这条桥上发来的随机挑战做 HMAC（带上会话 id 和成员编号），
    /// 房主据此把这条桥接到房间的 EasyTier。挑战每条桥一个，路上的人抄下来也用不到别的桥上。
    /// </summary>
    public static byte[] AdmitProof(MemberKey member, string session, ReadOnlySpan<byte> challenge)
    {
        var psk = member.CopyPsk();
        var key = Derive(psk, "admit");
        try
        {
            var data = new List<byte>();
            data.AddRange("jay-direct-admit-v1|"u8.ToArray());
            data.AddRange(Encoding.ASCII.GetBytes(session + "|" + member.IdHex + "|"));
            data.AddRange(challenge.ToArray());
            return HMACSHA256.HashData(key, data.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>收集能连到本机的地址：IPv6 公网地址、局域网地址、STUN 查到的外部地址、路由器映射。</summary>
public static class DirectGatherer
{
    public sealed record Gathered(List<Candidate> Candidates, NatKind Nat, string NatDetail, PortMappingResult? Mapping);

    public static async Task<Gathered> GatherAsync(Socket socket, IReadOnlyList<string> stunServers, bool tryPortMapping, Action<string> log, CancellationToken ct = default)
    {
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        // An IPv4-only socket (no IPv6 on this OS) can't receive on IPv6 addresses, so don't hand them out.
        var ipv6 = socket.AddressFamily == AddressFamily.InterNetworkV6;
        var list = new List<Candidate>();
        foreach (var address in Stun.LocalAddresses())
        {
            if (ipv6 && address.AddressFamily == AddressFamily.InterNetworkV6 && PortMapping.IsPublic(address) && !address.IsIPv6Teredo && !address.IsIPv4MappedToIPv6)
                list.Add(new Candidate(PathKind.IPv6, new IPEndPoint(address, port)));
            else if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                list.Add(new Candidate(PathKind.Lan, new IPEndPoint(address, port)));
        }
        log(list.Any(c => c.Kind == PathKind.IPv6)
            ? L.T("core.mp.gather.ipv6") + string.Join(L.T("core.mp.format.listseparator"), list.Where(c => c.Kind == PathKind.IPv6).Select(c => c.EndPoint.Address))
            : L.T("core.mp.gather.noipv6"));
        log(L.T("core.mp.gather.lan") + string.Join(L.T("core.mp.format.listseparator"), list.Where(c => c.Kind == PathKind.Lan).Select(c => c.EndPoint.Address)));

        var watch = Stopwatch.StartNew();
        var stun = await Stun.DetectAsync(socket, stunServers, ct);
        log(stun.Mapped is { } m
            ? L.F("core.mp.gather.stun_external", watch.ElapsedMilliseconds, stun.Detail, m)
            : L.F("core.mp.gather.stun", watch.ElapsedMilliseconds, stun.Detail));
        if (stun.Mapped is { } reported) Transport.ExitPolicy.ReportPublicAddress(reported.Address);
        if (stun.Mapped is { } mapped && mapped.AddressFamily == AddressFamily.InterNetwork)
            list.Add(new Candidate(PathKind.Punch, mapped));

        PortMappingResult? mapping = null;
        if (tryPortMapping)
        {
            mapping = await PortMapping.TryMapAsync(port, log, ct);
            if (mapping is not null)
            {
                list.Add(new Candidate(PathKind.Mapped, mapping.External));
                Transport.ExitPolicy.ReportPublicAddress(mapping.External.Address);
            }
        }
        return new Gathered(list, mapping is not null && stun.Kind != NatKind.Open ? NatKind.Open : stun.Kind, stun.Detail, mapping);
    }
}

/// <summary>一条打通的连接。</summary>
internal readonly record struct PendingPing(long SentAt, byte[] Nonce);

public sealed class DirectPeer
{
    public required IPEndPoint Remote { get; init; }
    public required PathKind Path { get; init; }
    public long ConnectedMs { get; internal set; }
    internal Socket? Local { get; set; }
    internal IPEndPoint? LocalClient { get; set; }
    /// <summary>Pings still waiting for their answer, by sequence number: when each was sent and its random part, kept
    /// and removed together so neither outlives the other.</summary>
    internal readonly ConcurrentDictionary<uint, PendingPing> PendingPings = new();
    /// <summary>
    /// This address answered a ping carrying a fresh random value sent to it on this bridge, so traffic really goes both
    /// ways with whoever holds the bridge key there. A replayed probe or probe answer from a forged source address never
    /// gets that far. Only such peers may get an exception from the member exit's traffic gate.
    /// </summary>
    public bool Verified => Volatile.Read(ref _verified);
    internal bool _verified;
    /// <summary>房主：这条桥的数据转发到本机哪个 EasyTier（批准前是大厅，批准后是房间）。</summary>
    internal int TargetPort;
    /// <summary>Host: cached loopback endpoint for <see cref="TargetPort"/>, so forwarding doesn't allocate one per packet.</summary>
    internal IPEndPoint? LocalTarget;
    /// <summary>房主：已经证明了自己是批准过的成员，数据转发到房间的 EasyTier。</summary>
    public bool Admitted { get; internal set; }
    /// <summary>房主：这条桥上是哪个成员（批准后才有）。踢出时断开这条桥。</summary>
    public MemberEntry? Member { get; internal set; }
    /// <summary>房主：发给这条桥的随机挑战（批准后的证明要对它做 HMAC）。</summary>
    internal byte[]? Challenge;
    internal CancellationTokenRegistration Revocation;
    internal int PingsSent;
    internal int PongsReceived;
    public double? RttMs { get; internal set; }
    public double LossPercent => PingsSent == 0 ? 0 : Math.Max(0, 100.0 * (PingsSent - PongsReceived - PendingPings.Count) / PingsSent);
    public long BytesIn;
    public long BytesOut;
    public DateTime LastSeen { get; internal set; } = DateTime.UtcNow;
}

/// <summary>
/// 直连桥：Amber 自己用一个 UDP 套接字打洞（所有路径共用这一个套接字），打通后在两边的 EasyTier 之间转发数据包。
/// EasyTier 只在本机回环上收发，数据本身仍是 EasyTier 加密过的；打洞探测包用邀请里大厅密钥派生的 HMAC 认证（见 <see cref="DirectKeys"/>），
/// 不认识的地址发来的数据一律丢掉。房主可以同时连多个加入方，加入方之间由 EasyTier 经房主中转或者再自己打洞。
/// 房主开了大厅端口时（邀请房间），新连上的桥只转发到大厅的 EasyTier（只能敲门）；加入方证明自己是批准过的成员后才改接房间的 EasyTier。
/// </summary>
public sealed class DirectBridge : IAsyncDisposable
{
    private const byte Probe = 1, ProbeAck = 2, Ping = 4, Pong = 5, Data = 0x10, Control = 0x20;
    private const int TagLength = 16;

    private readonly Socket _socket;
    private readonly byte[] _key;
    private readonly bool _isHost;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, DirectPeer> _peers = new();
    // 测试重新验证同一条路径时保留 EasyTier 绑定的本机端点；暂停期间不接受数据转发。
    private readonly Dictionary<IPEndPoint, DirectPeer> _pausedPeers = new();
    private readonly object _peerGate = new();
    private readonly ConcurrentBag<Task> _loops = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DirectPeer>> _punches = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>房主：踢出的人的地址，之后它的探测包不再接。</summary>
    private readonly HashSet<IPEndPoint> _blocked = [];
    /// <summary>房主：证明过成员身份的地址（桥断了从同一地址回来时不算新人）。</summary>
    private readonly ConcurrentDictionary<IPEndPoint, MemberEntry> _admittedRemotes = new();
    private Socket? _guestLocal;
    private int _probeRound;
    /// <summary>Placeholder remote endpoint for ReceiveFromAsync on the shared socket (matches its address family).</summary>
    private readonly IPEndPoint _anyRemote;
    private static readonly IPEndPoint AnyLoopbackRemote = new(IPAddress.Any, 0);

    /// <summary>房主：本机房间 EasyTier 的 UDP 监听端口；加入方：给本机 EasyTier 连的端口。</summary>
    public int LocalPort { get; private set; }

    /// <summary>房主：本机大厅 EasyTier 的 UDP 监听端口。设了它，新连上的桥先只能到大厅，批准后才接到房间。</summary>
    public int? LobbyPort { get; }
    public IPEndPoint SocketEndPoint => (IPEndPoint)_socket.LocalEndPoint!;
    public IReadOnlyCollection<DirectPeer> Peers => _peers.Values.ToList();
    public HashSet<PathKind> AllowedPaths { get; } = [.. Enum.GetValues<PathKind>()];

    /// <summary>
    /// 房主：现在收不收新的加入方。直连房间的大厅密钥 L（也就是桥钥匙）整个房间都不换，
    /// 所以没有待用邀请时不再接新的桥——拿着旧码的人连大厅都到不了；已经连着的、证明过身份的照常。
    /// </summary>
    public Func<bool>? AcceptingNewPeers { get; set; }

    /// <summary>喷洒时每轮往多少个端口发（对方对称 NAT 时用）。</summary>
    public int SprayPorts { get; set; } = 256;

    public event Action<DirectPeer>? Connected;
    public event Action<DirectPeer, JsonObject>? ControlReceived;

    /// <summary>
    /// One UDP socket for every path: dual-stack IPv6 when the OS has IPv6, otherwise plain IPv4
    /// (some Linux systems boot with IPv6 disabled, where an IPv6 socket cannot even be created).
    /// </summary>
    public static Socket CreateSocket(int port = 0) => CreateSocket(port, preferIPv6: true);

    /// <param name="preferIPv6">False forces the IPv4-only fallback (tests use it to cover systems without IPv6).</param>
    internal static Socket CreateSocket(int port, bool preferIPv6)
    {
        Socket? socket = null;
        if (preferIPv6)
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
            }
        }
        socket ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Windows 上对方端口不可达时 UDP 会报 10054，关掉这个行为，免得打洞时接收循环被打断。
            if (OperatingSystem.IsWindows()) socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            socket.Bind(new IPEndPoint(AnyAddress(socket), port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IPAddress AnyAddress(Socket socket) => socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;

    /// <param name="key">探测和控制包的 HMAC 钥匙（<see cref="DirectKeys.Bridge"/>）。</param>
    /// <param name="easyTierPort">房主：本机房间 EasyTier 监听的 UDP 端口。加入方传 null，桥会开一个本机端口给 EasyTier 连。</param>
    /// <param name="lobbyPort">房主：本机大厅 EasyTier 监听的 UDP 端口（邀请房间）。批准前的桥只转发到这里。</param>
    public DirectBridge(Socket socket, byte[] key, bool isHost, int? easyTierPort, Action<string> log, int? lobbyPort = null)
    {
        if (key.Length != 32) throw new ArgumentException(null, nameof(key));
        _socket = socket;
        _anyRemote = new IPEndPoint(AnyAddress(socket), 0);
        _isHost = isHost;
        _log = log;
        _key = (byte[])key.Clone();
        LobbyPort = isHost ? lobbyPort : null;
        if (isHost)
        {
            LocalPort = easyTierPort ?? throw new ArgumentNullException(nameof(easyTierPort));
        }
        else
        {
            _guestLocal = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows()) _guestLocal.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            _guestLocal.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            LocalPort = ((IPEndPoint)_guestLocal.LocalEndPoint!).Port;
            _loops.Add(GuestLocalLoopAsync());
        }
        _loops.Add(ReceiveLoopAsync());
        _loops.Add(PingLoopAsync());
    }

    private byte[] Tag(ReadOnlySpan<byte> data) => HMACSHA256.HashData(_key, data)[..TagLength];

    /// <summary>认证过的包：类型 + 载荷 + 16 字节 HMAC。</summary>
    internal byte[] Seal(byte type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[1 + payload.Length + TagLength];
        packet[0] = type;
        payload.CopyTo(packet.AsSpan(1));
        Tag(packet.AsSpan(0, 1 + payload.Length)).CopyTo(packet.AsSpan(1 + payload.Length));
        return packet;
    }

    internal bool Verify(ReadOnlySpan<byte> packet) =>
        packet.Length > TagLength && CryptographicOperations.FixedTimeEquals(Tag(packet[..^TagLength]), packet[^TagLength..]);

    /// <summary>往对方的所有允许的地址同时发探测包，持续到连上或超时。</summary>
    public async Task<DirectPeer?> PunchAsync(IReadOnlyList<Candidate> remote, NatKind remoteNat, TimeSpan timeout, CancellationToken ct = default)
    {
        var round = Interlocked.Increment(ref _probeRound);
        var acknowledged = new TaskCompletionSource<DirectPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = remote.Where(c => AllowedPaths.Contains(c.Kind)).ToList();
        var spray = AllowedPaths.Contains(PathKind.PunchSpray) && remoteNat == NatKind.Symmetric
                    && remote.FirstOrDefault(c => c.Kind == PathKind.Punch) is { } punch ? punch.EndPoint : null;
        var startMessage = L.P("core.mp.punch.start", targets.Count, targets.Count);
        _log(spray is null ? startMessage : L.F("core.mp.punch.start_spray", startMessage,
            L.P("core.mp.punch.spray", SprayPorts, spray.Address, SprayPorts)));
        foreach (var t in targets) _log(L.T("core.mp.punch.try") + t);
        var start = _clock.ElapsedMilliseconds;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        limit.CancelAfter(timeout);
        var sent = 0;
        var random = new Random();
        var targetAddresses = targets.Select(t => Stun.Normalize(t.EndPoint).Address)
            .Concat(spray is null ? [] : [Stun.Normalize(spray).Address]).ToHashSet();
        _punches[round] = acknowledged;
        try
        {
            for (var tick = 0; !limit.IsCancellationRequested; tick++)
            {
                var probe = Seal(Probe, BuildProbePayload(round));
                foreach (var t in targets)
                {
                    await SendRawAsync(probe, t.EndPoint, limit.Token);
                    sent++;
                }
                if (spray is not null && tick % 2 == 0)
                {
                    // 先往映射端口附近（端口预测），再往随机端口（生日悖论）。
                    for (var i = 0; i < SprayPorts; i++)
                    {
                        var port = i < 32 ? spray.Port + i - 16 : random.Next(1024, 65536);
                        if (port is < 1 or > 65535) continue;
                        await SendRawAsync(probe, new IPEndPoint(spray.Address, port), limit.Token);
                        sent++;
                    }
                }
                var confirmed = acknowledged.Task.IsCompletedSuccessfully ? acknowledged.Task.Result : null;
                if (confirmed is not null && (!_peers.TryGetValue(confirmed.Remote, out var active) || !ReferenceEquals(active, confirmed)))
                    confirmed = null;
                // A peer that (re)connected since this punch started counts. Comparing peer counts instead would miss a newcomer
                // whenever a stale peer was evicted at the same time. When two guests punch at once, prefer the one whose address
                // is among this punch's targets so each punch reports its own guest; a NAT may still rewrite the address, so any
                // newcomer is the fallback.
                var newest = confirmed ?? NewestSince(_peers.Values, start, targetAddresses);
                if (newest is not null)
                {
                    _log(L.P("core.mp.punch.success", sent, DirectText.Path(newest.Path), newest.Remote, _clock.ElapsedMilliseconds - start, sent));
                    return newest;
                }
                await Task.Delay(tick < 20 ? 100 : 300, limit.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            // 超时。
        }
        finally
        {
            _punches.TryRemove(round, out _);
        }
        _log(L.P("core.mp.punch.timeout", (long)Math.Round(timeout.TotalSeconds), timeout.TotalSeconds, sent));
        return null;
    }

    /// <summary>The earliest peer connected at or after <paramref name="start"/>, preferring one whose address is a punch target.</summary>
    internal static DirectPeer? NewestSince(IEnumerable<DirectPeer> peers, long start, IReadOnlySet<IPAddress> targetAddresses)
    {
        DirectPeer? any = null, targeted = null;
        foreach (var p in peers)
        {
            if (p.ConnectedMs < start) continue;
            if (any is null || p.ConnectedMs < any.ConnectedMs) any = p;
            if (targetAddresses.Contains(Stun.Normalize(p.Remote).Address) && (targeted is null || p.ConnectedMs < targeted.ConnectedMs))
                targeted = p;
        }
        return targeted ?? any;
    }

    /// <summary>NAT 保活的间隔。很多运营商（尤其是国内的 CGNAT）几十秒没有包就回收 UDP 映射。</summary>
    internal TimeSpan NatKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    private byte[]? _keepAliveId;
    private IPEndPoint? _keepAliveExpected;
    private int _keepAliveStarted;

    /// <summary>
    /// 打洞套接字的 NAT 保活：每隔 <see cref="NatKeepAliveInterval"/> 往 STUN 服务器发一个 Binding 请求。
    /// 码里写的外部地址（STUN 查到的端口）只有在映射一直活着时才有效：房主发出邀请码后要等对方贴回复码，
    /// 这段时间套接字上没有任何包，路由器/运营商把映射回收后，再发包会换一个新的外部端口，
    /// 对方往码里的旧端口发的探测全被丢掉，端口受限锥形 NAT 的对方又不收新端口发来的包，两边都打不通。
    /// <paramref name="expected"/> 不为空时（锥形 NAT），回复里的外部地址变了就记一笔，方便排查。
    /// </summary>
    public void StartNatKeepAlive(IPEndPoint stunServer, IPEndPoint? expected)
    {
        if (Interlocked.Exchange(ref _keepAliveStarted, 1) != 0) return;
        _keepAliveExpected = expected is null ? null : Stun.Normalize(expected);
        _loops.Add(KeepAliveLoopAsync(stunServer));
    }

    private async Task KeepAliveLoopAsync(IPEndPoint stunServer)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NatKeepAliveInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var id = RandomNumberGenerator.GetBytes(12);
            Volatile.Write(ref _keepAliveId, id);
            await SendRawAsync(Stun.BuildRequest(id), stunServer, _cts.Token);
        }
    }

    /// <summary>接收循环收到的包是不是保活请求的 STUN 回复（是的话吃掉，不再往下处理）。</summary>
    private bool HandleKeepAliveResponse(ReadOnlySpan<byte> packet)
    {
        if (Volatile.Read(ref _keepAliveId) is not { } id || Stun.ParseResponse(packet, id) is not { } mapped) return false;
        mapped = Stun.Normalize(mapped);
        if (_keepAliveExpected is { } expected && !expected.Equals(mapped))
        {
            _log(L.F("core.mp.punch.mapping_changed", expected.Port, mapped.Port));
            _keepAliveExpected = mapped;
        }
        return true;
    }

    /// <summary>房主没有对方地址时也能被连：只要有人发来认证通过的探测包就接受。</summary>
    public Task<DirectPeer?> WaitForPeerAsync(TimeSpan timeout, CancellationToken ct = default) => PunchAsync([], NatKind.Unknown, timeout, ct);

    private byte[] BuildProbePayload(int round)
    {
        var payload = new byte[13];
        payload[0] = _isHost ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), round);
        RandomNumberGenerator.Fill(payload.AsSpan(5));
        return payload;
    }

    private async Task SendRawAsync(ReadOnlyMemory<byte> packet, IPEndPoint target, CancellationToken ct)
    {
        // IPv4-only socket: an IPv6 candidate is unreachable from here (and SendTo would throw), skip it.
        if (_socket.AddressFamily == AddressFamily.InterNetwork && target.AddressFamily == AddressFamily.InterNetworkV6 && !target.Address.IsIPv4MappedToIPv6) return;
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, Stun.MapTo(_socket, target), ct);
        }
        catch (SocketException)
        {
            // 某个地址不可达（比如没有 IPv6 路由），换下一个。
        }
    }

    private PathKind Classify(IPEndPoint from, IReadOnlyList<Candidate>? hints = null)
    {
        if (from.AddressFamily == AddressFamily.InterNetworkV6) return PathKind.IPv6;
        if (!PortMapping.IsPublic(from.Address)) return PathKind.Lan;
        return _lastRemote?.FirstOrDefault(c => c.EndPoint.Equals(from))?.Kind ?? PathKind.Punch;
    }

    private IReadOnlyList<Candidate>? _lastRemote;

    /// <summary>记下对方的地址，用来判断连上的是哪条路径。</summary>
    public void SetRemoteCandidates(IReadOnlyList<Candidate> candidates) => _lastRemote = candidates;

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _anyRemote, _cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            var from = Stun.Normalize((IPEndPoint)r.RemoteEndPoint);
            var packet = buffer.AsSpan(0, r.ReceivedBytes);
            if (packet.Length == 0) continue;
            var type = packet[0];
            if (HandleKeepAliveResponse(packet)) continue;

            // 对方发来的内容不可信：处理一个包时出任何错（坏 JSON、字段类型不对、事件处理抛异常、转发出错）都只丢掉这个包、记一笔，
            // 不能让共用的接收循环停掉。数据包也在里面。
            try
            {
                if (type == Data)
                {
                    // Forward straight from the receive buffer: it is only reused after this await completes.
                    if (_peers.TryGetValue(from, out var peer)) await ForwardToLocalAsync(peer, buffer.AsMemory(1, r.ReceivedBytes - 1));
                    continue;
                }
                if (!Verify(packet)) continue;
                await HandleAuthenticatedAsync(from, type, packet[1..^TagLength].ToArray());
            }
            catch (Exception e) when (e is not OperationCanceledException || !_cts.IsCancellationRequested)
            {
                if (e is ObjectDisposedException && _cts.IsCancellationRequested) return;
                _log(L.F("core.mp.punch.controlerror", e.GetType().Name));
            }
        }
    }

    private async Task HandleAuthenticatedAsync(IPEndPoint from, byte type, byte[] payload)
    {
        switch (type)
        {
            case Probe:
                // 对方是同一侧（房主收到房主的探测）就不理，防止自己连自己。
                if (payload.Length >= 1 && payload[0] == (_isHost ? 1 : 0)) break;
                // 先登记再回确认：不接（人满、路径没开）就不回，对方不会以为连上了。
                if (!Accept(from)) break;
                await SendRawAsync(Seal(ProbeAck, payload), from, _cts.Token);
                break;
            case ProbeAck:
                if (Accept(from) && payload.Length >= 5 && payload[0] == (_isHost ? 1 : 0)
                    && _punches.TryGetValue(BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1)), out var round)
                    && _peers.TryGetValue(from, out var acknowledged)) round.TrySetResult(acknowledged);
                break;
            // A ping names the side that sent it. Our own pings sent back to us (a reflection by someone on the path who
            // doesn't hold the key) are never answered, so they can't be turned into an answer for ourselves.
            case Ping when _peers.TryGetValue(from, out var p1) && payload.Length >= PingLength && payload[PingLength - 1] != OwnRole:
                p1.LastSeen = DateTime.UtcNow;
                var pong = new byte[PingLength + 1];
                payload.AsSpan(0, PingLength).CopyTo(pong);
                pong[PingLength] = OwnRole;
                await SendRawAsync(Seal(Pong, pong), from, _cts.Token);
                break;
            case Pong when _peers.TryGetValue(from, out var p2) && payload.Length >= 4:
                // An answer must be to our ping (our side in the echo) and come from the other side; anything else,
                // such as one of our own answers sent back to us, is ignored whole.
                if (payload.Length >= PingLength && payload[PingLength - 1] != OwnRole) break;
                if (payload.Length > PingLength && payload[PingLength] == OwnRole) break;
                p2.LastSeen = DateTime.UtcNow;
                var seq = BinaryPrimitives.ReadUInt32BigEndian(payload);
                if (p2.PendingPings.TryRemove(seq, out var pending))
                {
                    // The answer to this address's own fresh ping, from this address and from the other side: the path
                    // works both ways.
                    if (payload.Length == PingLength + 1
                        && CryptographicOperations.FixedTimeEquals(pending.Nonce, payload.AsSpan(4, PingNonceLength)))
                        Volatile.Write(ref p2._verified, true);
                    Interlocked.Increment(ref p2.PongsReceived);
                    var rtt = _clock.Elapsed.TotalMilliseconds - pending.SentAt / 1000.0;
                    p2.RttMs = p2.RttMs is { } old ? old * 0.7 + rtt * 0.3 : rtt;
                }
                break;
            case Control when _peers.TryGetValue(from, out var p3):
                p3.LastSeen = DateTime.UtcNow;
                if (JsonNode.Parse(payload) is JsonObject json) ControlReceived?.Invoke(p3, json);
                break;
        }
    }

    internal const int PingNonceLength = 16;

    /// <summary>A ping: sequence number, random value, and the side that sent it (1 = host, 0 = guest). Its answer
    /// echoes the ping and adds the answering side.</summary>
    internal const int PingLength = 4 + PingNonceLength + 1;

    private byte OwnRole => _isHost ? (byte)1 : (byte)0;

    /// <summary>房主最多同时接这么多个加入方（每个一个本机套接字和一个转发循环）。</summary>
    public const int MaxHostPeers = 32;

    /// <summary>房主：加入方这么久没有任何回应就移除（腾出位子，也关掉它的本机套接字和转发循环）。</summary>
    internal TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>登记一个对方。已经登记过返回 true；不接（加入方已经有房主、人满、路径没开、建不了套接字）返回 false。</summary>
    private bool Accept(IPEndPoint from)
    {
        DirectPeer? connected;
        bool accepted;
        lock (_peerGate) accepted = AcceptLocked(from, out connected);
        if (connected is not null)
        {
            _log(L.F("core.mp.punch.connected", from, DirectText.Path(connected.Path)));
            Connected?.Invoke(connected);
        }
        return accepted;
    }

    private bool AcceptLocked(IPEndPoint from, out DirectPeer? connected)
    {
        connected = null;
        if (_cts.IsCancellationRequested) return false;
        if (_peers.TryGetValue(from, out var known))
        {
            // 已经连着的对方回了探测（心跳断了之后补发的探测）：算它还活着。
            known.LastSeen = DateTime.UtcNow;
            return true;
        }
        if (!_isHost && !_peers.IsEmpty) return false;
        if (_blocked.Contains(from)) return false;
        var path = Classify(from);
        if (!AllowedPaths.Contains(path) && !(path == PathKind.Punch && AllowedPaths.Contains(PathKind.PunchSpray)))
        {
            _log(L.F("core.mp.punch.disabled", from, DirectText.Path(path)));
            return false;
        }
        EvictStale();
        if (_pausedPeers.Remove(from, out var resumed))
        {
            resumed.ConnectedMs = _clock.ElapsedMilliseconds;
            resumed.LastSeen = DateTime.UtcNow;
            _peers[from] = resumed;
            connected = resumed;
            return true;
        }
        if (_isHost && AcceptingNewPeers?.Invoke() == false
            && !(_admittedRemotes.TryGetValue(from, out var member) && !member.Revoked))
        {
            _log(L.F("core.mp.direct.closed_to_new", from));
            return false;
        }
        if (_isHost && _peers.Count + _pausedPeers.Count >= MaxHostPeers)
        {
            _log(L.F("core.mp.punch.toomanypeers", from, MaxHostPeers));
            return false;
        }
        if (!_isHost)
        {
            // 加入方只保留一个房主；真正换地址后不再保留旧路径状态。
            _pausedPeers.Clear();
        }
        var peer = new DirectPeer { Remote = from, Path = path, ConnectedMs = _clock.ElapsedMilliseconds, TargetPort = LobbyPort ?? LocalPort, Admitted = LobbyPort is null };
        if (_isHost)
        {
            // 每个加入方一个本机套接字，去连本机 EasyTier 的监听端口。先建好再登记：建不了就不接这个加入方，
            // 不会留下一个没有本机套接字的对方（之后它的数据包会让转发出错）。
            Socket? local = null;
            try
            {
                local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) local.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                local?.Dispose();
                _log(L.F("core.mp.punch.localerror", e.GetType().Name));
                return false;
            }
            peer.Local = local;
            if (!_peers.TryAdd(from, peer))
            {
                local.Dispose();
                return true;
            }
            _loops.Add(HostLocalLoopAsync(peer, local));
        }
        else if (!_peers.TryAdd(from, peer))
        {
            return true;
        }
        connected = peer;
        return true;
    }

    /// <summary>房主：移除超过 PeerTimeout 没有任何回应的加入方（离开了、换了地址、掉线了）。</summary>
    private void EvictStale()
    {
        lock (_peerGate)
        {
            var now = DateTime.UtcNow;
            foreach (var (endpoint, peer) in _pausedPeers.ToArray())
            {
                if (now - peer.LastSeen <= PeerTimeout) continue;
                _pausedPeers.Remove(endpoint);
                peer.Local?.Dispose();
            }
            if (!_isHost) return;
            foreach (var (endpoint, peer) in _peers)
            {
                if (now - peer.LastSeen <= PeerTimeout || !_peers.TryRemove(endpoint, out var removed)) continue;
                removed.Local?.Dispose();
                _log(L.F("core.mp.punch.peerevicted", endpoint));
            }
        }
    }

    private async Task ForwardToLocalAsync(DirectPeer peer, ReadOnlyMemory<byte> data)
    {
        peer.LastSeen = DateTime.UtcNow;
        Interlocked.Add(ref peer.BytesIn, data.Length);
        try
        {
            if (_isHost)
            {
                if (peer.Local is not { } local) return;
                var target = peer.LocalTarget;
                if (target is null || target.Port != peer.TargetPort) peer.LocalTarget = target = new IPEndPoint(IPAddress.Loopback, peer.TargetPort);
                await local.SendToAsync(data, SocketFlags.None, target);
            }
            else if (peer.LocalClient is { } client) await _guestLocal!.SendToAsync(data, SocketFlags.None, client);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Sends one data packet. <paramref name="buffer"/> holds the payload from index 1 on; index 0 is overwritten with the
    /// packet type, so the local loops receive at offset 1 and send without copying.
    /// </summary>
    private async Task SendDataAsync(DirectPeer peer, byte[] buffer, int dataLength)
    {
        buffer[0] = Data;
        Interlocked.Add(ref peer.BytesOut, dataLength);
        await SendRawAsync(buffer.AsMemory(0, dataLength + 1), peer.Remote, _cts.Token);
    }

    private async Task HostLocalLoopAsync(DirectPeer peer, Socket local)
    {
        var buffer = new byte[65536 + 1];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await local.ReceiveFromAsync(buffer.AsMemory(1), SocketFlags.None, AnyLoopbackRemote, _cts.Token);
                // 批准后换了本机套接字：旧套接字（大厅那边）上收到的不再往外发。
                if (!ReferenceEquals(peer.Local, local)) return;
                if (!_peers.TryGetValue(peer.Remote, out var active) || !ReferenceEquals(active, peer)) continue;
                await SendDataAsync(peer, buffer, r.ReceivedBytes);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) { }
        }
    }

    private async Task GuestLocalLoopAsync()
    {
        var buffer = new byte[65536 + 1];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _guestLocal!.ReceiveFromAsync(buffer.AsMemory(1), SocketFlags.None, AnyLoopbackRemote, _cts.Token);
                // The guest keeps at most one peer (the host). Enumerating doesn't snapshot the dictionary the way .Values does.
                DirectPeer? peer = null;
                foreach (var entry in _peers)
                {
                    peer = entry.Value;
                    break;
                }
                if (peer is null) continue;
                if (!r.RemoteEndPoint.Equals(peer.LocalClient)) peer.LocalClient = (IPEndPoint)r.RemoteEndPoint;
                await SendDataAsync(peer, buffer, r.ReceivedBytes);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) { }
        }
    }

    /// <summary>对方这么久没回任何东西（心跳停了）就同时补发探测包。</summary>
    internal static readonly TimeSpan ReprobeAfter = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 每秒一次心跳：测延迟和丢包，也保持路由器上的映射不过期。对方的回应停了（对方换了版本、路由器映射被回收、
    /// 对方重启了桥）时，每次心跳再补一个探测包：探测包任何版本都会回，回了就重新算连着。
    /// </summary>
    private async Task PingLoopAsync()
    {
        uint seq = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            EvictStale();
            foreach (var peer in _peers.Values)
            {
                seq++;
                // 超过 5 秒没回的算丢包。
                foreach (var (s, ping) in peer.PendingPings)
                    if (_clock.Elapsed.TotalMilliseconds - ping.SentAt / 1000.0 > 5000) peer.PendingPings.TryRemove(s, out _);
                Interlocked.Increment(ref peer.PingsSent);
                // Sequence number, a random value the answer must echo, and our side.
                var payload = new byte[PingLength];
                BinaryPrimitives.WriteUInt32BigEndian(payload, seq);
                RandomNumberGenerator.Fill(payload.AsSpan(4, PingNonceLength));
                payload[PingLength - 1] = OwnRole;
                peer.PendingPings[seq] = new PendingPing((long)(_clock.Elapsed.TotalMilliseconds * 1000), payload[4..(4 + PingNonceLength)]);
                await SendRawAsync(Seal(Ping, payload), peer.Remote, _cts.Token);
                if (DateTime.UtcNow - peer.LastSeen > ReprobeAfter)
                    await SendRawAsync(Seal(Probe, BuildProbePayload(_probeRound)), peer.Remote, _cts.Token);
            }
        }
    }

    /// <summary>
    /// 房主：这条桥上的人证明了自己是批准过的成员：换一个新的本机套接字，改接房间的 EasyTier（大厅那边的连接随旧套接字断掉）。
    /// 成员钥匙作废（踢出、重新批准换了钥匙、房间结束）时这条桥马上断开，之后这个地址的探测不再接。
    /// </summary>
    public bool Admit(DirectPeer peer, MemberEntry member)
    {
        if (!_isHost) throw new InvalidOperationException();
        Socket? old;
        lock (_peerGate)
        {
            if (!_peers.TryGetValue(peer.Remote, out var active) || !ReferenceEquals(active, peer) || member.Revoked) return false;
            if (peer.Admitted && ReferenceEquals(peer.Member, member)) return true;
            Socket local;
            try
            {
                local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows()) local.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }
            catch (SocketException e)
            {
                _log(L.F("core.mp.punch.localerror", e.GetType().Name));
                return false;
            }
            old = peer.Local;
            peer.Revocation.Dispose();
            peer.Local = local;
            peer.TargetPort = LocalPort;
            peer.Member = member;
            peer.Admitted = true;
            _admittedRemotes[peer.Remote] = member;
            _loops.Add(HostLocalLoopAsync(peer, local));
        }
        old?.Dispose();
        // 已经作废的，Register 会立刻调用。
        peer.Revocation = member.Revocation.Register(() => Kick(peer));
        _log(L.F("core.mp.direct.admitted", peer.Remote));
        return true;
    }

    /// <summary>房主：断开这条桥（踢出的人），之后这个地址发来的探测不再接。</summary>
    public void Kick(DirectPeer peer)
    {
        lock (_peerGate)
        {
            _blocked.Add(peer.Remote);
            _admittedRemotes.TryRemove(peer.Remote, out _);
            if (_peers.TryGetValue(peer.Remote, out var active) && ReferenceEquals(active, peer)) _peers.TryRemove(peer.Remote, out _);
            if (_pausedPeers.TryGetValue(peer.Remote, out var paused) && ReferenceEquals(paused, peer)) _pausedPeers.Remove(peer.Remote);
            peer.Local?.Dispose();
        }
        _log(L.F("core.mp.direct.kicked", peer.Remote));
    }

    /// <summary>房主：这条桥的随机挑战（批准后的证明对它做 HMAC）。每条桥一个，第一次要时生成。</summary>
    internal byte[] ChallengeFor(DirectPeer peer)
    {
        lock (_peerGate) return peer.Challenge ??= RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>经直连通道发控制消息（测试矩阵双方同步用）。</summary>
    public async Task SendControlAsync(DirectPeer peer, JsonObject message) =>
        await SendRawAsync(Seal(Control, Encoding.UTF8.GetBytes(message.ToJsonString())), peer.Remote, _cts.Token);

    /// <summary>重新验证远端路径；矩阵可保留本地传输端点，避免破坏仍在运行的 EasyTier 会话。</summary>
    public void DropPeers(bool preserveLocalTransport = false)
    {
        lock (_peerGate)
        {
            if (!preserveLocalTransport)
            {
                foreach (var peer in _pausedPeers.Values)
                {
                    peer.Local?.Dispose();
                    peer.Revocation.Dispose();
                }
                _pausedPeers.Clear();
            }
            foreach (var endpoint in _peers.Keys)
            {
                if (!_peers.TryRemove(endpoint, out var peer)) continue;
                if (preserveLocalTransport)
                {
                    peer.LastSeen = DateTime.UtcNow;
                    _pausedPeers[endpoint] = peer;
                }
                else
                {
                    peer.Local?.Dispose();
                    peer.Revocation.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _socket.Dispose();
        _guestLocal?.Dispose();
        DropPeers();
        try
        {
            await Task.WhenAll(_loops.ToArray());
        }
        catch (Exception e)
        {
            // 哪个循环出过错都不影响收尾：调用方接着要撤掉端口映射、重置界面。
            if (e is not (OperationCanceledException or ObjectDisposedException or SocketException)) _log(L.F("core.mp.punch.localerror", e.GetType().Name));
        }
        _cts.Dispose();
        CryptographicOperations.ZeroMemory(_key);
    }
}
