using System.Net;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Net;

namespace Amber.NetworkServer;

/// <summary>
/// 搭一个 Jay 公共网络节点。最少只要：
/// <code>
/// await using var server = new JayNetworkServerBuilder()
///     .UseDataDir("/var/lib/my-network")
///     .UseNetwork(NetworkString.Parse(connectionString))
///     .Build();
/// await server.RunAsync(ct);
/// </code>
/// 然后按需加：<see cref="AddGate"/>（自己的房间审核）、<see cref="UseAccounts"/>（账号库）、<see cref="AddLoginFactor"/>（自己的验证方式）、
/// <see cref="AddObserver"/>（事件）、<see cref="AddExtension"/>（自定义请求）。
/// </summary>
public sealed class JayNetworkServerBuilder
{
    internal string DataDir = Path.Combine(AppContext.BaseDirectory, "jay-network-data");
    internal SignedNetwork? Network;
    internal List<string> Seeds = [];
    internal int ListenPort = 11010;
    internal (string Core, string Cli)? EasyTier;
    internal bool DownloadEasyTier = true;
    internal IPEndPoint? DirectOnly;
    internal bool SeedOnlyMode;
    internal int DirectoryIndex = 1;
    internal NetworkConfig? Config;
    internal LoginAuthority? Login;
    internal (IAccountStore? Store, bool RequireSecondFactor)? Accounts;
    internal readonly List<ILoginFactor> Factors = [];
    internal readonly List<IRoomGate> Gates = [];
    internal readonly List<IDirectoryObserver> Observers = [];
    internal readonly List<IDirectoryExtension> Extensions = [];
    internal bool BanListOn = true;
    internal RateLimitGate? RateLimit = new();
    internal int? AdminPort = JayNetworkServer.DefaultAdminPort;
    internal Action<string> Log = _ => { };
    internal Func<DateTimeOffset>? Clock;

    /// <summary>数据目录：EasyTier、登录钥匙、账号表、封禁名单、管理口令都放这里。</summary>
    public JayNetworkServerBuilder UseDataDir(string path) { DataDir = Path.GetFullPath(path); return this; }

    /// <summary>用自己的公共网络（JAYNET1- 连接串）。不调就是内置的 Jay 公共网络。</summary>
    public JayNetworkServerBuilder UseNetwork(SignedNetwork network) { Network = network; return this; }

    /// <summary>别的种子节点（tcp://host:11010），互相连起来组成一个网络。</summary>
    public JayNetworkServerBuilder UseSeeds(IEnumerable<string> seeds)
    {
        foreach (var s in seeds)
            if (!RelayNodes.IsValid(s)) throw new ArgumentException($"种子节点地址不对：{s}（例：tcp://1.2.3.4:11010）");
        Seeds = [.. seeds.Select(s => s.Trim())];
        return this;
    }

    /// <summary>EasyTier 对公网监听的端口（TCP 和 UDP），默认 11010。</summary>
    public JayNetworkServerBuilder UseListenPort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("端口要是 1–65535");
        ListenPort = port;
        return this;
    }

    /// <summary>用已经有的 EasyTier（不用就第一次运行时下载固定版本并校验 SHA-256）。</summary>
    public JayNetworkServerBuilder UseEasyTier(string corePath, string cliPath) { EasyTier = (corePath, cliPath); return this; }

    /// <summary>
    /// 不启动 EasyTier，目录服务直接监听这个地址（测试用，或者你自己管 EasyTier、把 10.235.0.1:47668 转过来）。
    /// </summary>
    public JayNetworkServerBuilder WithoutEasyTier(IPEndPoint directory) { DirectOnly = directory; return this; }

    /// <summary>只当种子节点（帮忙转发、分担地区流量），不跑房间目录。一个网络里只能有一个目录节点（它占着 10.235.0.1）。</summary>
    public JayNetworkServerBuilder SeedOnly() { SeedOnlyMode = true; return this; }

    /// <summary>
    /// 第几个目录节点：1 是主目录（10.235.0.1），2–4 是备用（10.235.0.2–4）。Jay 把房间发给每个在线的目录、合并列表，
    /// 所以一台挂了别的照常。备用节点要和主节点用同样的网络配置、屏蔽词，开账号登录时用同一份 login.key 和账号表（或同一个 IAccountStore）。
    /// </summary>
    public JayNetworkServerBuilder UseDirectoryIndex(int index)
    {
        if (index is < 1 or > PublicNetworkProfile.MaxDirectories) throw new ArgumentException($"目录节点编号要是 1–{PublicNetworkProfile.MaxDirectories}");
        DirectoryIndex = index;
        return this;
    }

    /// <summary>网络配置文件：节点按里面的 policy.modes 再挡一道（防改过的客户端）。</summary>
    public JayNetworkServerBuilder UseConfig(NetworkConfig config) { Config = config; return this; }

    /// <summary>直接给一个配好的登录服务（自己管钥匙时用）。</summary>
    public JayNetworkServerBuilder UseLogin(LoginAuthority login) { Login = login; return this; }

    /// <summary>
    /// 开账号登录。<paramref name="store"/> 是 null 时用内置账号表（数据目录里的 accounts.json，用 jay-server account 管）。
    /// 登录钥匙在数据目录的 login.key，第一次自动生成；公钥要写进网络配置的 login 段（见 <see cref="JayNetworkServer.LoginSection"/>）。
    /// </summary>
    public JayNetworkServerBuilder UseAccounts(IAccountStore? store = null, bool requireSecondFactor = false)
    {
        Accounts = (store, requireSecondFactor);
        return this;
    }

    /// <summary>登录时密码之后的验证（短信、邮件码、会员系统……）。内置账号表的 TOTP 总是在第一个。</summary>
    public JayNetworkServerBuilder AddLoginFactor(ILoginFactor factor) { Factors.Add(factor); return this; }

    public JayNetworkServerBuilder AddGate(IRoomGate gate) { Gates.Add(gate); return this; }

    public JayNetworkServerBuilder AddObserver(IDirectoryObserver observer) { Observers.Add(observer); return this; }

    public JayNetworkServerBuilder AddExtension(IDirectoryExtension extension)
    {
        if (!DirectoryExtensions.IsValidOp(extension.Op)) throw new ArgumentException($"自定义请求的名字要以 x- 开头，只用小写字母、数字、横线：{extension.Op}");
        if (Extensions.Any(e => e.Op == extension.Op)) throw new ArgumentException($"自定义请求 {extension.Op} 重复了");
        Extensions.Add(extension);
        return this;
    }

    /// <summary>屏蔽词（房间名、房主名）。</summary>
    public JayNetworkServerBuilder UseBlockedWords(IEnumerable<string> words) => AddGate(new NameFilterGate(words));

    /// <summary>改限流（每人同时几个房间、每人每分钟新发布几次、整个目录每分钟几个新房间）。</summary>
    public JayNetworkServerBuilder UseRateLimit(int maxRoomsPerHost, int newPerHostPerMinute, int newPerMinute)
    {
        RateLimit = new RateLimitGate(maxRoomsPerHost, newPerHostPerMinute, newPerMinute, Clock);
        return this;
    }

    public JayNetworkServerBuilder WithoutRateLimit() { RateLimit = null; return this; }

    /// <summary>不要封禁名单（默认开着，存在数据目录的 bans.json）。</summary>
    public JayNetworkServerBuilder WithoutBanList() { BanListOn = false; return this; }

    /// <summary>本机管理接口的端口（只听 127.0.0.1，默认 47669；0 = 随便挑一个空端口）。</summary>
    public JayNetworkServerBuilder UseAdmin(int port) { AdminPort = port; return this; }

    public JayNetworkServerBuilder WithoutAdmin() { AdminPort = null; return this; }

    public JayNetworkServerBuilder UseLog(Action<string> log) { Log = log; return this; }

    /// <summary>测试用：换时钟。</summary>
    public JayNetworkServerBuilder UseClock(Func<DateTimeOffset> clock) { Clock = clock; return this; }

    public JayNetworkServer Build() => new(this);
}

/// <summary>运行中的网络节点：EasyTier（种子）+ 房间目录 + 账号登录 + 管理接口。</summary>
public sealed class JayNetworkServer : IAsyncDisposable
{
    public const int DefaultAdminPort = 47669;

    private readonly JayNetworkServerBuilder _b;
    private readonly DateTimeOffset _started;
    private PublicNetworkSession? _network;
    private AdminApi? _admin;

    internal JayNetworkServer(JayNetworkServerBuilder b)
    {
        _b = b;
        _started = Now;
        Profile = b.Network is { } n ? n.ToProfile() with { Nodes = b.Seeds } : PublicNetworkProfile.Jay(b.Seeds);
        Bans = b.BanListOn ? new BanList(Path.Combine(b.DataDir, "bans.json")) : null;
        Stats = new DirectoryStats();
        if (b.Login is { } login) Login = login;
        else if (b.Accounts is { } accounts)
        {
            var (key, signer) = LoginAuthority.LoadOrCreateKeys(Path.Combine(b.DataDir, "login.key"));
            var store = accounts.Store ?? (Builtin = new AccountStore(Path.Combine(b.DataDir, "accounts.json")));
            Login = new LoginAuthority(store, key, signer, Profile.Id, b.Clock)
            {
                Factors = [new TotpFactor(store), .. b.Factors],
                RequireSecondFactor = accounts.RequireSecondFactor,
            };
        }
    }

    private DateTimeOffset Now => _b.Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public PublicNetworkProfile Profile { get; }
    public RoomDirectoryServer? Directory { get; private set; }
    public BanList? Bans { get; }
    public DirectoryStats Stats { get; }
    public LoginAuthority? Login { get; }
    /// <summary>用内置账号表时是它（jay-server account 管的那个）。</summary>
    public AccountStore? Builtin { get; }
    public string DataDir => _b.DataDir;
    public bool SeedOnly => _b.SeedOnlyMode;
    /// <summary>这个节点的目录地址（只当种子时是 null）。</summary>
    public string? DirectoryIp => SeedOnly ? null : PublicNetworkProfile.DirectoryIpOf(_b.DirectoryIndex);
    public int? AdminPort => _admin?.Port;
    /// <summary>管理接口的口令文件（数据目录里的 admin.token，只有本机这个用户能读）。</summary>
    public string AdminTokenPath => Path.Combine(_b.DataDir, "admin.token");
    public bool EasyTierRunning => _network is { HasExited: false };

    /// <summary>开了账号登录时，要写进网络配置文件 "login" 的那一段。</summary>
    public JsonObject? LoginSection => Login is not { } login ? null : new JsonObject
    {
        ["required"] = true,
        ["key"] = Convert.ToBase64String(login.PublicKey),
        ["signKey"] = Convert.ToBase64String(login.SignKey),
        ["factors"] = new JsonArray([.. login.Factors.Select(f => (JsonNode)f.Name)]),
        ["requireFactor"] = login.RequireSecondFactor,
    };

    public async Task StartAsync(CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(_b.DataDir);
        if (!_b.SeedOnlyMode)
        {
            var gates = new List<IRoomGate>();
            if (Bans is not null) gates.Add(Bans);
            if (_b.Config is { } config) gates.Add(new PolicyGate(config.Policy));
            if (_b.RateLimit is { } rate) gates.Add(rate);
            gates.AddRange(_b.Gates);
            var observers = new List<IDirectoryObserver> { Stats };
            if (_b.RateLimit is { } r) observers.Add(r);
            observers.AddRange(_b.Observers);
            var endpoint = _b.DirectOnly ?? new IPEndPoint(IPAddress.Loopback, PublicNetworkProfile.DirectoryPort);
            Directory = new RoomDirectoryServer(endpoint.Address, endpoint.Port, _b.Log, _b.Clock)
            {
                Login = Login,
                Gates = gates,
                Observers = observers,
                Extensions = [.. _b.Extensions],
            };
        }
        if (_b.AdminPort is { } port) _admin = AdminApi.Start(this, port, AdminToken.LoadOrCreate(AdminTokenPath), _b.Log);
        if (_b.DirectOnly is not null) return;

        string core, cli;
        if (_b.EasyTier is { } et) (core, cli) = et;
        else
        {
            using var http = Downloader.CreateHttpClient();
            var installer = new EasyTierInstaller(new Downloader(http, SourceChain.Default()), _b.DataDir);
            if (!installer.IsInstalled) _b.Log("下载 EasyTier……");
            await installer.EnsureAsync(null, ct);
            (core, cli) = (installer.CorePath, installer.CliPath);
        }
        var options = new PublicNetworkOptions(Profile, EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort(), Directory: !_b.SeedOnlyMode,
            Listeners: [$"tcp://0.0.0.0:{_b.ListenPort}", $"udp://0.0.0.0:{_b.ListenPort}"], DirectoryIndex: _b.DirectoryIndex);
        _network = PublicNetworkSession.Start(core, cli, options, Path.Combine(_b.DataDir, "public-node"));
    }

    /// <summary>EasyTier 的最近几行输出（出问题时看）。</summary>
    public IEnumerable<string> RecentOutput => _network?.RecentOutput ?? [];

    public event Action<string>? EasyTierOutput
    {
        add { if (_network is not null) _network.Output += value; }
        remove { if (_network is not null) _network.Output -= value; }
    }

    /// <summary>启动并一直跑到 <paramref name="ct"/> 取消（返回 0）或 EasyTier 意外退出（返回 1）。</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (Directory is null && _network is null) await StartAsync(ct);
        var last = -1;
        while (!ct.IsCancellationRequested)
        {
            if (_network is { HasExited: true })
            {
                _b.Log("EasyTier 意外退出：" + string.Join(" / ", _network.RecentOutput.TakeLast(3)));
                return 1;
            }
            if (Directory is { } d && d.Count != last)
            {
                last = d.Count;
                _b.Log($"目录里现在有 {last} 个房间");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { }
        }
        return 0;
    }

    /// <summary>健康检查（管理接口的 /health，不用口令）。</summary>
    public JsonObject Health()
    {
        var view = Directory?.View();
        var healthy = (_b.DirectOnly is not null || EasyTierRunning) && (SeedOnly || Directory is not null);
        return new JsonObject
        {
            ["ok"] = healthy,
            ["network"] = Profile.Id,
            ["role"] = SeedOnly ? "seed" : "directory",
            ["directory"] = SeedOnly ? null : PublicNetworkProfile.DirectoryIpOf(_b.DirectoryIndex),
            ["easytier"] = _b.DirectOnly is not null ? "external" : EasyTierRunning ? "running" : "stopped",
            ["rooms"] = view?.Rooms ?? 0,
            ["listed"] = view?.Listed ?? 0,
            ["login"] = Login is not null,
            ["uptime"] = (long)(Now - _started).TotalSeconds,
        };
    }

    /// <summary>封禁并马上把他已经在目录里的房间拿掉。</summary>
    public int Ban(string kind, string value, string? reason)
    {
        if (Bans is null) throw new InvalidOperationException("这个节点没开封禁名单");
        Bans.Add(kind, value, reason, Now);
        if (Directory is not { } d) return 0;
        return kind == "host" ? d.RemoveHost(value.Trim()) : d.Remove(value.Trim()) ? 1 : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is not null) await _admin.DisposeAsync();
        _network?.Dispose();
        if (Directory is not null) await Directory.DisposeAsync();
    }
}

/// <summary>节点的计数（管理接口的 /stats）：只有数字，不记谁做了什么。</summary>
public sealed class DirectoryStats : IDirectoryObserver
{
    private long _published, _renewed, _removed, _expired, _withdrawn, _loginOk, _loginFail;
    private readonly Dictionary<string, long> _rejected = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    void IDirectoryObserver.OnAnnounced(RoomListing listing, bool listed, bool isNew)
    {
        if (isNew) Interlocked.Increment(ref _published);
        else Interlocked.Increment(ref _renewed);
    }

    void IDirectoryObserver.OnRemoved(string roomId, RoomRemoval reason)
    {
        if (reason == RoomRemoval.Expired) Interlocked.Increment(ref _expired);
        else if (reason == RoomRemoval.Withdrawn) Interlocked.Increment(ref _withdrawn);
        else Interlocked.Increment(ref _removed);
    }

    void IDirectoryObserver.OnRejected(RoomListing listing, RoomVerdict verdict)
    {
        lock (_gate) _rejected[verdict.Code!] = _rejected.GetValueOrDefault(verdict.Code!) + 1;
    }

    void IDirectoryObserver.OnLogin(bool ok, string? error)
    {
        if (ok) Interlocked.Increment(ref _loginOk);
        else Interlocked.Increment(ref _loginFail);
    }

    public JsonObject ToJson()
    {
        var rejected = new JsonObject();
        lock (_gate) foreach (var (code, n) in _rejected.OrderBy(p => p.Key)) rejected[code] = n;
        return new JsonObject
        {
            ["published"] = Interlocked.Read(ref _published),
            ["renewed"] = Interlocked.Read(ref _renewed),
            ["withdrawn"] = Interlocked.Read(ref _withdrawn),
            ["expired"] = Interlocked.Read(ref _expired),
            ["removed"] = Interlocked.Read(ref _removed),
            ["rejected"] = rejected,
            ["loginOk"] = Interlocked.Read(ref _loginOk),
            ["loginFail"] = Interlocked.Read(ref _loginFail),
        };
    }
}
