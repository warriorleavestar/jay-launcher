using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// One extra EasyTier instance for split path <see cref="Index"/> (1 or 2; path 0 is the room's own instance).
/// It joins the extra network from the room's split settings, never builds a TUN device, never listens, and never tries
/// peer-to-peer: this path always goes through a relay node, so a node's operator sees one share and the direct path
/// carries another.
/// </summary>
/// <param name="Peers">Nodes this instance connects to. The host uses all its nodes; a member uses the one group the planner chose.</param>
/// <param name="LocalLegPort">Member: the local port forwarded to the host's leg port over this network.</param>
/// <param name="MemberHost">Member: the last part of its address on this network. The room's own instance uses the same one
/// (<see cref="EasyTierArgs.JoinerHost"/>), so the extra networks add no address clash the room's own network doesn't have.</param>
public sealed record SplitPathOptions(int Index, SplitNet Net, bool IsHost, IReadOnlyList<string> Peers, int RpcPort, int? LocalLegPort = null, bool QuicProxy = true,
    int? MemberHost = null)
{
    public override string ToString() => "SplitPathOptions";
}

/// <summary>easytier-core arguments for an extra split path. Path i uses 10.236.i.0/24, the host always .1.</summary>
public static class SplitPathArgs
{
    public static string HostIp(int index) => $"10.236.{index}.1";

    public static string MemberIp(SplitPathOptions o) =>
        $"10.236.{o.Index}.{(o.MemberHost ?? EasyTierArgs.JoinerHost(null)).ToString(CultureInfo.InvariantCulture)}";

    public static Dictionary<string, string> Environment(SplitPathOptions o) => new() { [EasyTierArgs.SecretVariable] = o.Net.Secret };

    public static List<string> Build(SplitPathOptions o)
    {
        if (o.Index is < 1 or >= SplitStream.MaxLegs) throw new ArgumentOutOfRangeException(nameof(o));
        var port = SplitSettings.LegPort(o.Index).ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "--network-name", o.Net.Network,
            "--ipv4", o.IsHost ? HostIp(o.Index) : MemberIp(o),
            "--rpc-portal", $"127.0.0.1:{o.RpcPort}",
            "--hostname", o.IsHost ? "jay-path-host" : "jay-path-guest",
            "--instance-name", $"jay-path{o.Index}-{(o.IsHost ? "host" : "guest")}-{o.RpcPort}",
            // A machine id of its own, so this instance and the room's main one don't replace each other on a node.
            "--machine-id", Guid.NewGuid().ToString(),
            "--no-tun",
            "--no-listener",
            // Relay only: this path must not turn into a second direct path.
            "--disable-p2p",
        };
        if (o.QuicProxy) args.Add("--enable-quic-proxy");
        foreach (var peer in o.Peers.Where(p => !string.IsNullOrWhiteSpace(p))) args.AddRange(["--peers", peer.Trim()]);
        if (o.Peers.Any(p => Uri.TryCreate(p.Trim(), UriKind.Absolute, out var u) && (u.IsLoopback || u.Host == "localhost"))) args.AddRange(["--bind-device", "false"]);
        // The host serves only this path's leg port; a member serves nothing.
        args.AddRange(["--tcp-whitelist", o.IsHost ? port : "1"]);
        args.AddRange(["--udp-whitelist", "1"]);
        args.AddRange(["--rpc-portal-whitelist", "127.0.0.1/32"]);
        if (!o.IsHost)
        {
            var local = o.LocalLegPort ?? throw new ArgumentException(L.T("core.mp.component.forwardrequired"));
            args.AddRange(["--port-forward", $"tcp://127.0.0.1:{local.ToString(CultureInfo.InvariantCulture)}/{HostIp(o.Index)}:{port}"]);
        }
        return args;
    }
}

/// <summary>
/// Which nodes count as run by the same people. Jay can't prove who runs a node, so this is a best guess: nodes that share
/// a registered domain, or an IPv4 /16 or IPv6 /32, are one group. The interface never claims the groups are different
/// operators. When a signed relay list with operator labels exists, its labels go in <see cref="Labels"/> and take precedence.
/// </summary>
public static class RelayOperators
{
    /// <summary>Operator labels by node host, from a signed list. Empty until one exists.</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; internal set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SecondLevel = ["co", "com", "net", "org", "ac", "gov", "edu", "or", "ne", "go"];

    public static string GroupOf(string node)
    {
        if (!Uri.TryCreate(node.Trim(), UriKind.Absolute, out var u) || u.Host.Length == 0) return "?" + node.Trim();
        var host = u.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (Labels.TryGetValue(host, out var label)) return "label:" + label;
        if (IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            return ip.AddressFamily == AddressFamily.InterNetwork
                ? $"v4:{b[0]}.{b[1]}"
                : $"v6:{Convert.ToHexStringLower(b, 0, 4)}";
        }
        var parts = host.Split('.');
        if (parts.Length <= 2) return "dns:" + host;
        // example.co.uk, example.com.cn: keep three labels.
        var take = parts[^1].Length == 2 && SecondLevel.Contains(parts[^2]) ? 3 : 2;
        return "dns:" + string.Join('.', parts[^take..]);
    }
}

/// <summary>The member's plan for the extra paths: which nodes each extra instance uses, or why there is none.</summary>
/// <param name="ExtraPeers">ExtraPeers[i] are the nodes for path i + 1.</param>
public sealed record SplitPathPlan(IReadOnlyList<IReadOnlyList<string>> ExtraPeers, string? Problem)
{
    public bool Ready => Problem is null;
}

/// <summary>
/// Chooses the extra paths (design mp-split-design v3 §3 and §9).
/// <list type="bullet">
/// <item>Path 0 is the room's own instance and must reach the host directly (peer to peer); the readiness check enforces that.</item>
/// <item>Each extra path goes through the nodes of one group, a different group per path, relay only.</item>
/// <item>The choice is fixed for a member: groups are ordered by a keyed hash of the member's id, so the same member
/// lands on the same groups every time (also after a rekey) instead of drawing new nodes on every connection.</item>
/// </list>
/// Two relay nodes of different groups for paths 0 and 1 (when path 0 can't go direct) would need path 0's own nodes
/// restricted, which can cut the member off from the host; that mode is left out of this version.
/// </summary>
public static class SplitPathPlanner
{
    public static SplitPathPlan Plan(int paths, IReadOnlyList<string> nodes, ReadOnlySpan<byte> stickyKey)
    {
        if (paths is < SplitStream.MinLegs or > SplitStream.MaxLegs) throw new ArgumentOutOfRangeException(nameof(paths));
        var groups = nodes.Select(n => n.Trim()).Where(RelayNodes.IsValid).Distinct()
            .GroupBy(RelayOperators.GroupOf)
            .Select(g => (Key: g.Key, Nodes: (IReadOnlyList<string>)[.. g]))
            .ToList();
        if (groups.Count < paths - 1) return new SplitPathPlan([], L.T("core.mp.split.no_independent_path"));
        var key = stickyKey.ToArray();
        var ordered = groups.OrderBy(g => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(g.Key))), StringComparer.Ordinal).ToList();
        return new SplitPathPlan([.. ordered.Take(paths - 1).Select(g => g.Nodes)], null);
    }

    /// <summary>
    /// Whether the paths are ready right now. <paramref name="main"/> is the host as seen on the room's own instance;
    /// <paramref name="extra"/> the host as seen on each extra instance (null: not seen). Returns the problem, or null.
    /// </summary>
    public static string? Check(PeerInfo? main, IReadOnlyList<PeerInfo?> extra)
    {
        if (main is null) return L.T("core.mp.split.host_unreachable");
        // Path 0 has to be direct: relayed, it may go through the same node as an extra path.
        if (!string.Equals(main.Cost, "p2p", StringComparison.OrdinalIgnoreCase)) return L.T("core.mp.split.main_not_direct");
        if (extra.Any(p => p is null)) return L.T("core.mp.split.relay_unreachable");
        return null;
    }
}

/// <summary>
/// The extra EasyTier instances of one room session, started with the room's own instance and ended with it.
/// A member's group also watches whether the paths are ready (<see cref="Problem"/>); the split connector asks it before
/// every connection. The watch fails closed: when a check can't be completed, or the watch has stopped, the paths count as
/// not ready. The host's instances join every node and serve only their leg port.
/// </summary>
public sealed class SplitPathGroup : IDisposable
{
    private readonly record struct PathInstance(SplitPathOptions Options, Process? Process, IDisposable? Job);

    private readonly List<PathInstance> _instances = [];
    private readonly bool _isHost;
    private readonly Func<CancellationToken, Task<List<PeerInfo>>>? _mainPeers;
    private readonly Func<SplitPathOptions, CancellationToken, Task<List<PeerInfo>>> _extraPeers;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _watch;
    private volatile string? _problem;
    private int _disposed;

    /// <summary>How often a member checks its paths.</summary>
    internal TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Member: why the paths aren't usable right now (null when they are). Always null on the host.</summary>
    public string? Problem => _isHost ? null : Volatile.Read(ref _disposed) != 0 ? L.T("core.mp.split.stopped") : _problem;
    public IReadOnlyList<SplitPathOptions> Instances => [.. _instances.Select(i => i.Options)];

    private SplitPathGroup(bool isHost, Func<CancellationToken, Task<List<PeerInfo>>>? mainPeers,
        Func<SplitPathOptions, CancellationToken, Task<List<PeerInfo>>> extraPeers, Action<string>? log, string? problem)
    {
        _isHost = isHost;
        _mainPeers = mainPeers;
        _extraPeers = extraPeers;
        _log = log;
        _problem = problem;
    }

    /// <summary>
    /// Starts the extra instances for a room whose host splits. Returns null when this session doesn't split
    /// (no split settings, full-LAN mode, no leg ports).
    /// </summary>
    /// <param name="mainPeers">The room's own instance's peer list (member only, for the readiness check).</param>
    /// <param name="log">Gets each extra instance's output (prefixed with its path) and a line when one stops.</param>
    public static SplitPathGroup? Start(EasyTierInstaller installer, EasyTierOptions o, string workDir,
        Func<CancellationToken, Task<List<PeerInfo>>> mainPeers, Action<string>? log = null) =>
        Start(installer.CorePath, installer.CliPath, o, workDir, mainPeers, log);

    internal static SplitPathGroup? Start(string corePath, string cliPath, EasyTierOptions o, string workDir,
        Func<CancellationToken, Task<List<PeerInfo>>> mainPeers, Action<string>? log = null)
    {
        var split = o.Room.Split;
        if (!split.Enabled || o.Tun || !o.Room.FromInvite || !o.Room.Tunnel.Wraps) return null;
        if (!o.IsHost && (o.LocalSplitPorts is not { } ports || ports.Count != split.Paths)) return null;
        SplitPathPlan plan;
        if (o.IsHost) plan = new SplitPathPlan([.. Enumerable.Repeat(o.Peers, split.Paths - 1)], null);
        else plan = SplitPathPlanner.Plan(split.Paths, o.Peers, o.Room.Member?.Id ?? []);
        var group = new SplitPathGroup(o.IsHost, o.IsHost ? null : mainPeers,
            (path, ct) => EasyTierSession.QueryPeersAsync(cliPath, path.RpcPort, ct), log,
            o.IsHost ? null : plan.Problem ?? L.T("core.mp.split.starting"));
        if (!plan.Ready) return group;
        try
        {
            for (var i = 1; i < split.Paths; i++)
            {
                var options = new SplitPathOptions(i, split.Nets[i - 1], o.IsHost, plan.ExtraPeers[i - 1], EasyTierArgs.FreeTcpPort(),
                    o.IsHost ? null : o.LocalSplitPorts![i], o.QuicProxy, o.IsHost ? null : EasyTierArgs.JoinerHost(o.MachineId));
                var (process, job) = EasyTierSession.Launch(corePath, SplitPathArgs.Build(options), SplitPathArgs.Environment(options),
                    Path.Combine(workDir, $"path{i}"));
                var prefix = $"path{i}: ";
                var index = i;
                process.OutputDataReceived += (_, e) => { if (e.Data is { } line) log?.Invoke(prefix + line); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) log?.Invoke(prefix + line); };
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => { if (Volatile.Read(ref group._disposed) == 0) log?.Invoke(L.F("core.mp.split.path_exited", index)); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                group._instances.Add(new PathInstance(options, process, job));
            }
        }
        catch
        {
            group.Dispose();
            throw;
        }
        if (!o.IsHost) group.StartWatch();
        return group;
    }

    /// <summary>Tests: a member group over the given paths with no processes behind them.</summary>
    internal static SplitPathGroup ForTest(IReadOnlyList<SplitPathOptions> paths, Func<CancellationToken, Task<List<PeerInfo>>> mainPeers,
        Func<SplitPathOptions, CancellationToken, Task<List<PeerInfo>>> extraPeers, TimeSpan interval, Action<string>? log = null)
    {
        var group = new SplitPathGroup(false, mainPeers, extraPeers, log, L.T("core.mp.split.starting")) { Interval = interval };
        group._instances.AddRange(paths.Select(p => new PathInstance(p, null, null)));
        group.StartWatch();
        return group;
    }

    private void StartWatch()
    {
        // The list doesn't change after this point; the watch still works on its own copy.
        var instances = _instances.ToArray();
        _watch = Task.Run(() => WatchAsync(instances, _cts.Token));
    }

    private async Task WatchAsync(PathInstance[] instances, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _problem = await CheckAsync(instances, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Fail closed: a check that didn't finish never leaves an earlier "ready" in place.
                _problem = L.T("core.mp.split.check_failed");
                _log?.Invoke(L.T("core.mp.split.check_failed") + " (" + e.GetType().Name + ")");
            }
            try
            {
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        _problem = L.T("core.mp.split.stopped");
    }

    private async Task<string?> CheckAsync(PathInstance[] instances, CancellationToken ct)
    {
        var main = (await _mainPeers!(ct)).FirstOrDefault(p => p.Ip == EasyTierArgs.HostIp && p.Cost != "Local");
        var extra = new List<PeerInfo?>();
        foreach (var path in instances)
        {
            if (path.Process is { HasExited: true })
            {
                extra.Add(null);
                continue;
            }
            var peers = await _extraPeers(path.Options, ct);
            extra.Add(peers.FirstOrDefault(p => p.Ip == SplitPathArgs.HostIp(path.Options.Index) && p.Cost != "Local"));
        }
        return SplitPathPlanner.Check(main, extra);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        var watchEnded = true;
        if (_watch is { } watch)
        {
            try { watchEnded = watch.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
        foreach (var (_, process, job) in _instances)
        {
            if (process is null) continue;
            try
            {
                if (!process.HasExited) process.Kill(true);
                process.WaitForExit(3000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
            job?.Dispose();
        }
        _instances.Clear();
        // A watch that didn't end in time may still touch the token; leave the source to the finalizer then.
        if (watchEnded) _cts.Dispose();
    }
}
