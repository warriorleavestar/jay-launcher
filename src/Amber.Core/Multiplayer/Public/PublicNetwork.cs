using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 一个公共网络：大家先进的“大厅层”EasyTier 网络。只用来看房间列表、找房主、敲门；游戏和聊天在房主的子网（房间自己的 EasyTier）里走。
/// 网络名和密钥是公开的（谁都能进），所以公共网络上的流量当作没有保密：Jay 在这里只开敲门端口，安全靠敲门握手和房间里每人一把的钥匙。
/// </summary>
/// <param name="Id">本机保存用的编号（内置的 Jay 公共网络是 "jay"）。</param>
/// <param name="Nodes">这个公共网络的节点（目录服务也跑在节点那台机器上）。</param>
public sealed record PublicNetworkProfile(string Id, string Name, string NetworkName, string Secret, IReadOnlyList<string> Nodes)
{
    /// <summary>公共网络用的网段；目录服务固定在 .0.1，其他人由 EasyTier 自动分配。</summary>
    public const string Subnet = "10.235.";
    public const string DirectoryIp = "10.235.0.1";
    public const int DirectoryPort = 47668;
    /// <summary>最多几个目录节点（互为备用）。</summary>
    public const int MaxDirectories = 4;
    public static readonly string[] DirectoryIps = [.. Enumerable.Range(1, MaxDirectories).Select(DirectoryIpOf)];

    /// <summary>
    /// 第 <paramref name="index"/> 个目录节点的地址：1 是主目录 10.235.0.1，2–4 是备用 10.235.255.254 / .253 / .252。
    /// 备用的放在网段最后：EasyTier 的自动分配从 .0.2 往上数，不会撞上。
    /// </summary>
    public static string DirectoryIpOf(int index) => index switch
    {
        1 => DirectoryIp,
        >= 2 and <= MaxDirectories => Subnet + "255." + (256 - index).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>内置的 Jay 公共网络的 EasyTier 网络名。</summary>
    public const string JayNetworkName = "jay-public-v1";
    /// <summary>内置的 Jay 公共网络的 EasyTier 密钥：公开的，写在程序里，只用来和别的网络分开。</summary>
    public const string JaySecret = "jay-public-network-v1";

    /// <summary>Jay 自己的公共节点（作者部署的服务器）。还没部署时为空，这时公共网络要在设置里填节点才能用。</summary>
    public static readonly string[] JayNodes = [];

    /// <summary>内置的 Jay 公共网络：节点 = 程序里的 + 设置里填的（测试或自己搭的节点）。</summary>
    public static PublicNetworkProfile Jay(IEnumerable<string>? extraNodes = null)
    {
        var nodes = new List<string>(JayNodes);
        foreach (var n in extraNodes ?? [])
            if (RelayNodes.IsValid(n) && !nodes.Contains(n.Trim())) nodes.Add(n.Trim());
        return new PublicNetworkProfile("jay", L.T("core.mp.public.jay_name"), JayNetworkName, JaySecret, nodes);
    }

    public bool IsUsable => Nodes.Count > 0;

    /// <summary>是不是 Jay 审核过的网络（内置的 Jay 网络和注册表签名的连接串）。</summary>
    public bool Reviewed { get; init; } = true;

    /// <summary>网络的服务器配置文件（第 5 期用；内置网络没有）。</summary>
    public NetworkConfigRef? Config { get; init; }

    /// <summary>网络主页（可选）。</summary>
    public string? Home { get; init; }

    public bool IsBuiltIn => Id == "jay";

    public override string ToString() => Name;
}

/// <summary>公共网络 EasyTier 的启动参数。</summary>
/// <param name="Directory">目录服务那台机器：固定地址 10.235.0.N（N = <paramref name="DirectoryIndex"/>），只放行目录端口。</param>
/// <param name="Listeners">目录服务那台机器同时当节点时监听的地址（tcp://0.0.0.0:11010 等）。</param>
/// <param name="DirectoryIndex">第几个目录节点（1 是主目录，2–4 是备用）。</param>
public sealed record PublicNetworkOptions(PublicNetworkProfile Profile, int RpcPort, int SocksPort, bool Directory = false, IReadOnlyList<string>? Listeners = null,
    int DirectoryIndex = 1)
{
    public override string ToString() => "PublicNetworkOptions";
}

public static class PublicNetworkArgs
{
    public static Dictionary<string, string> Environment(PublicNetworkOptions o) => new() { [EasyTierArgs.SecretVariable] = o.Profile.Secret };

    public static List<string> Build(PublicNetworkOptions o)
    {
        var args = new List<string>
        {
            "--network-name", o.Profile.NetworkName,
            "--rpc-portal", $"127.0.0.1:{o.RpcPort.ToString(CultureInfo.InvariantCulture)}",
            "--rpc-portal-whitelist", "127.0.0.1/32",
            // 不用电脑名：公共网络里谁都能看到别人的主机名。
            "--hostname", o.Directory ? "jay-directory" : "jay",
            "--instance-name", $"jay-public-{o.RpcPort.ToString(CultureInfo.InvariantCulture)}",
            "--machine-id", Guid.NewGuid().ToString(),
            "--no-tun",
            // 不和陌生人直接打洞：P2P 会把自己的公网地址告诉公共网络里的每个人。敲门的流量很小，都经节点转发。
            "--disable-p2p",
            "--latency-first",
            // Jay 自己经本机 SOCKS5 连公共网络里的地址（目录服务、房主的敲门端口）。
            "--socks5", o.SocksPort.ToString(CultureInfo.InvariantCulture),
            "--udp-whitelist", "1",
        };
        if (o.Directory) args.AddRange(["--ipv4", PublicNetworkProfile.DirectoryIpOf(o.DirectoryIndex) + "/16", "--tcp-whitelist", PublicNetworkProfile.DirectoryPort.ToString(CultureInfo.InvariantCulture)]);
        // 普通成员：自动分配地址；只放行敲门端口（没开房间时本机没人监听，连过来直接被拒）。
        else args.AddRange(["--dhcp", "true", "--tcp-whitelist", KnockProtocol.Port.ToString(CultureInfo.InvariantCulture)]);
        if (o.Listeners is { Count: > 0 } listeners) foreach (var l in listeners) args.AddRange(["--listeners", l]);
        else args.Add("--no-listener");
        foreach (var peer in o.Profile.Nodes.Where(p => !string.IsNullOrWhiteSpace(p))) args.AddRange(["--peers", peer.Trim()]);
        if (o.Profile.Nodes.Concat(o.Listeners ?? []).Any(IsLoopback)) args.AddRange(["--bind-device", "false"]);
        return args;
    }

    public const string DirectoryIpWithMask = PublicNetworkProfile.DirectoryIp + "/16";

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost" || u.Host == "0.0.0.0");
}

/// <summary>Jay 怎么用公共网络：连到里面的某个地址、查自己的地址。测试里换成直接连本机。</summary>
public interface IPublicNetwork
{
    /// <summary>连到公共网络里的 ip:port（目录服务或房主的敲门端口）。</summary>
    Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct);
    /// <summary>自己在公共网络里的地址（EasyTier 自动分配，可能中途变）；还没分到时是 null。</summary>
    Task<string?> MyIpAsync(CancellationToken ct);
    /// <summary>现在在线的目录节点地址（按编号排）。默认只有主目录。</summary>
    Task<IReadOnlyList<string>> DirectoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([PublicNetworkProfile.DirectoryIp]);
}

/// <summary>运行中的公共网络 EasyTier。Dispose 时结束进程。</summary>
public sealed class PublicNetworkSession : IPublicNetwork, IDisposable
{
    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly string _cliPath;
    private readonly Queue<string> _output = new();

    public PublicNetworkOptions Options { get; }
    public bool HasExited => _process.HasExited;
    public event Action<string>? Output;

    private PublicNetworkSession(Process process, IDisposable? job, string cliPath, PublicNetworkOptions options)
    {
        _process = process;
        _job = job;
        _cliPath = cliPath;
        Options = options;
    }

    public static PublicNetworkSession Start(EasyTierInstaller installer, PublicNetworkOptions options, string workDir) =>
        Start(installer.CorePath, installer.CliPath, options, workDir);

    public static PublicNetworkSession Start(string corePath, string cliPath, PublicNetworkOptions options, string workDir)
    {
        var (process, job) = EasyTierSession.Launch(corePath, PublicNetworkArgs.Build(options), PublicNetworkArgs.Environment(options), workDir);
        var session = new PublicNetworkSession(process, job, cliPath, options);
        process.OutputDataReceived += (_, e) => session.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => session.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return session;
    }

    public IReadOnlyList<string> RecentOutput { get { lock (_output) return _output.ToList(); } }

    private void OnLine(string? line)
    {
        if (line is null) return;
        lock (_output)
        {
            _output.Enqueue(line);
            if (_output.Count > 200) _output.Dequeue();
        }
        Output?.Invoke(line);
    }

    public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => EasyTierSession.QueryPeersAsync(_cliPath, Options.RpcPort, ct);

    public async Task<string?> MyIpAsync(CancellationToken ct)
    {
        // 自己那一行的 cost 是 Local。
        var peers = await PeersAsync(ct);
        return peers.FirstOrDefault(p => p.Cost == "Local") is { } me ? StripMask(me.Ip) : null;
    }

    private (DateTimeOffset At, IReadOnlyList<string> Ips) _directories = (DateTimeOffset.MinValue, []);

    /// <summary>现在在线的目录节点（公共网络里看得到的 10.235.0.1–4，按编号排；查一次缓存 5 秒）。</summary>
    public async Task<IReadOnlyList<string>> DirectoriesAsync(CancellationToken ct)
    {
        var cached = _directories;
        if (DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Ips;
        var peers = await PeersAsync(ct);
        var online = peers.Where(p => p.Cost != "Local").Select(p => StripMask(p.Ip)).ToHashSet();
        IReadOnlyList<string> ips = [.. PublicNetworkProfile.DirectoryIps.Where(online.Contains)];
        _directories = (DateTimeOffset.UtcNow, ips);
        return ips;
    }

    /// <summary>等到能看到目录服务（连上了节点、至少一个目录节点在线）。</summary>
    public async Task WaitForDirectoryAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (HasExited) throw new IOException(L.F("core.mp.public.exited", RecentOutput.LastOrDefault() ?? ""));
                var peers = await PeersAsync(cts.Token);
                if (peers.Any(p => PublicNetworkProfile.DirectoryIps.Contains(StripMask(p.Ip)) && p.Cost != "Local")
                    // 自动分配的地址要已经在公共网络的网段里：刚连上、还没看到别人的路由时 EasyTier 会先给一个 10.126.126.x，过几秒才改过来。
                    && peers.FirstOrDefault(p => p.Cost == "Local") is { Ip.Length: > 0 } me && IsMemberAddress(StripMask(me.Ip))) return;
                await Task.Delay(500, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("core.mp.public.no_directory"));
        }
    }

    public Task<Stream> ConnectAsync(string ip, int port, CancellationToken ct) => Socks5Client.ConnectAsync(IPAddress.Loopback, Options.SocksPort, ip, port, ct);

    internal static string StripMask(string ip) => ip.Split('/')[0];

    /// <summary>
    /// An address a member may use: inside the public subnet and not one of the directory addresses. EasyTier's DHCP hands
    /// out the lowest free address, so while the main directory is offline a member can be given 10.235.0.1. Such a member
    /// isn't treated as connected; EasyTier moves it once the directory is back and claims its fixed address.
    /// Even while it holds the address it can't receive directory traffic: members only let the knock port in.
    /// </summary>
    internal static bool IsMemberAddress(string ip) =>
        ip.StartsWith(PublicNetworkProfile.Subnet, StringComparison.Ordinal) && !PublicNetworkProfile.DirectoryIps.Contains(ip);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            _process.WaitForExit(3000);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        _job?.Dispose();
    }
}

/// <summary>最小的 SOCKS5 客户端（RFC 1928，只做无认证 CONNECT 到 IPv4 地址），用来经 EasyTier 的本机 SOCKS5 连公共网络里的地址。</summary>
public static class Socks5Client
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<Stream> ConnectAsync(IPAddress proxy, int proxyPort, string ip, int port, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var target) || target.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException(null, nameof(ip));
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);
            await client.ConnectAsync(proxy, proxyPort, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, deadline.Token);
            var hello = new byte[2];
            await stream.ReadExactlyAsync(hello, deadline.Token);
            if (hello[0] != 5 || hello[1] != 0) throw new IOException(L.T("core.mp.public.socks_failed"));
            var request = new byte[10];
            request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 1;
            target.GetAddressBytes().CopyTo(request, 4);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)port);
            await stream.WriteAsync(request, deadline.Token);
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, deadline.Token);
            if (reply[0] != 5 || reply[1] != 0) throw new IOException(L.T("core.mp.public.unreachable"));
            var rest = reply[3] switch { 1 => 4 + 2, 4 => 16 + 2, 3 => -1, _ => throw new IOException(L.T("core.mp.public.socks_failed")) };
            if (rest < 0)
            {
                var len = new byte[1];
                await stream.ReadExactlyAsync(len, deadline.Token);
                rest = len[0] + 2;
            }
            await stream.ReadExactlyAsync(new byte[rest], deadline.Token);
            return new OwnedStream(stream, client);
        }
        catch (EndOfStreamException)
        {
            client.Dispose();
            // EasyTier 的 SOCKS5 在目标端口不放行或没人监听时直接断开。
            throw new IOException(L.T("core.mp.public.unreachable"));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>关流时一起关掉 TcpClient。</summary>
    private sealed class OwnedStream(NetworkStream inner, TcpClient owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
