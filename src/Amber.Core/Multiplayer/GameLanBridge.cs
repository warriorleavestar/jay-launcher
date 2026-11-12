using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// How Jay reaches a game's LAN world on this computer. Normally a plain connection to 127.0.0.1. A game in the
/// Windows strong sandbox (an AppContainer) cannot be reached that way: Windows drops loopback connections into the
/// container. Such a game is launched with Jay's LAN bridge agent (<c>tools/jay-lan</c>), which keeps a control
/// connection out to this listener. When the port belongs to a game with an agent (Windows' table of listening ports
/// names the owning process), Jay asks that agent to connect to its own port and to open a second connection out to Jay
/// for this one player. Nothing is opened into the container, and other games and local services are reached as before.
/// <para>
/// The listener is on 127.0.0.1 only. Each launch gets its own token on the agent's command line, and the token is bound
/// to the process Jay started. The token is not a secret from the game or its mods (they can read the JVM arguments),
/// so what counts is the process: every connection's process is read from Windows' connection table, never from what it
/// says, and must be the launch's process. A request is answered only by a data connection from the agent's process,
/// and only that agent can turn its own requests down.
/// </para>
/// </summary>
public static class GameLanBridge
{
    internal const string Magic = "JAYLAN1";
    private const int MaxLine = 160;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a control connection waits for Jay to learn which process its launch started.</summary>
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Connections still sending their first line; more are closed at once.</summary>
    private const int MaxHandshakes = 32;

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Handshakes = new(MaxHandshakes, MaxHandshakes);
    private static TcpListener? _listener;
    private static readonly Dictionary<string, Launch> Launches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<long, Request> Pending = new();

    /// <summary>One game launch: its token, the process it started (once known) and that process's agent.</summary>
    private sealed class Launch
    {
        public TaskCompletionSource<int> Pid { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Agent? Agent { get; set; }
    }

    private sealed class Agent(Launch launch, Socket control, int pid)
    {
        public Launch Launch { get; } = launch;
        public Socket Control { get; } = control;
        public int Pid { get; } = pid;
        public SemaphoreSlim Write { get; } = new(1, 1);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Request(Agent Agent, TaskCompletionSource<Socket?> Reply);

    /// <summary>
    /// The JVM argument that loads the agent from <paramref name="jar"/> with a new launch token (the listener starts on
    /// first use), or null when the path can't be passed: the JVM splits <c>-javaagent:</c> at the first "=". Call
    /// <see cref="Started"/> with the process once it runs.
    /// </summary>
    public static string? AgentArgument(string jar)
    {
        if (jar.Contains('=')) return null;
        var port = Start();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (Gate) Launches[token] = new Launch();
        return $"-javaagent:{jar}={port}:{token}";
    }

    /// <summary>
    /// The launch with these arguments started <paramref name="process"/>: its agent is accepted from that process only,
    /// and the launch is forgotten when the process exits. Arguments without an agent are ignored.
    /// </summary>
    public static void Started(IEnumerable<string> arguments, Process process)
    {
        if (TokenIn(arguments) is not { } token) return;
        Launch? launch;
        lock (Gate) Launches.TryGetValue(token, out launch);
        if (launch is null) return;
        launch.Pid.TrySetResult(process.Id);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Ended(token);
        if (process.HasExited) Ended(token);
    }

    /// <summary>The launch failed to start, or its process exited: forget its token and close its agent.</summary>
    public static void Ended(IEnumerable<string> arguments)
    {
        if (TokenIn(arguments) is { } token) Ended(token);
    }

    private static void Ended(string token)
    {
        Launch? launch;
        lock (Gate)
        {
            if (!Launches.Remove(token, out launch)) return;
        }
        launch.Pid.TrySetResult(0);
        launch.Agent?.Control.Dispose();
    }

    private static string? TokenIn(IEnumerable<string> arguments)
    {
        foreach (var a in arguments)
        {
            if (!a.StartsWith("-javaagent:", StringComparison.Ordinal)) continue;
            var colon = a.LastIndexOf(':');
            if (colon > 0 && a[(colon + 1)..] is { Length: 32 } token)
                lock (Gate)
                    if (Launches.ContainsKey(token)) return token;
        }
        return null;
    }

    /// <summary>Games with a connected agent (tests, diagnostics).</summary>
    public static int AgentCount
    {
        get
        {
            lock (Gate) return Launches.Values.Count(l => l.Agent is not null);
        }
    }

    /// <summary>Whether a game with a bridge agent is running: rooms then pass the game port through Jay's relay.</summary>
    public static bool HasAgents => AgentCount > 0;

    /// <summary>
    /// Something listens on <paramref name="port"/> but can't be reached: most likely a game in the strong sandbox
    /// started without a working agent (or before Jay was restarted, which gives the agent a dead address).
    /// </summary>
    public static async Task<bool> ListenedButUnreachableAsync(int port)
    {
        if (!OperatingSystem.IsWindows() || LocalPortOwners.ListenerPids(port).Count == 0 || AgentFor(port) is not null) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var socket = await DirectAsync(port, timeout.Token).ConfigureAwait(false);
            return socket is null;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Connects to <paramref name="port"/> on this computer: through the agent of the game that listens there, else
    /// directly to <paramref name="direct"/> (127.0.0.1 unless given). Null when nothing answered within
    /// <paramref name="timeout"/>; throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<Socket?> ConnectAsync(int port, TimeSpan timeout, CancellationToken ct = default, IPAddress? direct = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
        try
        {
            return AgentFor(port) is { } agent ? await ViaAgentAsync(agent, port, linked.Token).ConfigureAwait(false) : await DirectAsync(port, linked.Token, direct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<Socket?> DirectAsync(int port, CancellationToken ct, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Agent? AgentFor(int port)
    {
        List<Agent> agents;
        lock (Gate) agents = [.. Launches.Values.Select(l => l.Agent).OfType<Agent>()];
        if (agents.Count == 0 || !OperatingSystem.IsWindows()) return null;
        var owners = LocalPortOwners.ListenerPids(port);
        return agents.FirstOrDefault(a => owners.Contains(a.Pid));
    }

    private static async Task<Socket?> ViaAgentAsync(Agent agent, int port, CancellationToken ct)
    {
        long id;
        var reply = new TaskCompletionSource<Socket?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Random ids: knowing one request's id tells nothing about the next.
        do id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
        while (!Pending.TryAdd(id, new Request(agent, reply)));
        Socket? result = null;
        try
        {
            if (!await SendAsync(agent, $"open {id} {port}\n", ct).ConfigureAwait(false)) return null;
            var done = await Task.WhenAny(reply.Task, agent.Closed.Task).WaitAsync(ct).ConfigureAwait(false);
            result = done == reply.Task ? await reply.Task.ConfigureAwait(false) : null;
            if (result is not null) Interlocked.Increment(ref _viaAgent);
            return result;
        }
        finally
        {
            // Closing the request: a data connection that arrives later can no longer be handed over (and is closed
            // by its handshake); one that arrived after this gave up is closed here.
            Pending.TryRemove(id, out _);
            if (!reply.TrySetResult(null) && reply.Task.Result is { } late && late != result) late.Dispose();
        }
    }

    private static async Task<bool> SendAsync(Agent agent, string line, CancellationToken ct)
    {
        await agent.Write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await agent.Control.SendAsync(Encoding.ASCII.GetBytes(line), SocketFlags.None, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            agent.Write.Release();
        }
    }

    private static int Start()
    {
        lock (Gate)
        {
            if (_listener is null)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listener = listener;
                _ = Task.Run(() => AcceptLoopAsync(listener));
            }
            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            if (!Handshakes.Wait(0))
            {
                socket.Dispose();
                continue;
            }
            _ = Task.Run(() => HandshakeAsync(socket));
        }
    }

    private static async Task HandshakeAsync(Socket socket)
    {
        var keep = false;
        var released = false;
        void Release()
        {
            if (!released) Handshakes.Release();
            released = true;
        }
        try
        {
            string? line;
            using (var timeout = new CancellationTokenSource(HandshakeTimeout))
                line = await ReadLineAsync(socket, timeout.Token);
            if (line is null || Parse(line) is not { } hello || !OperatingSystem.IsWindows()) return;
            Launch? launch;
            lock (Gate) Launches.TryGetValue(hello.Token, out launch);
            if (launch is null) return;
            var pid = LocalPortOwners.ConnectionPid((IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!);
            if (pid is null) return;
            socket.NoDelay = true;
            if (hello.Data is { } id)
            {
                // Only the agent that was asked may answer, from its own process.
                if (Pending.TryGetValue(id, out var request) && request.Agent.Launch == launch && request.Agent.Pid == pid
                    && request.Reply.TrySetResult(socket))
                    keep = true;
                return;
            }
            // The agent runs in the process this launch started (the JVM loads it before the game).
            using (var wait = new CancellationTokenSource(BindTimeout))
                if (await launch.Pid.Task.WaitAsync(wait.Token) != pid) return;
            Release();
            keep = true;
            await ServeControlAsync(new Agent(launch, socket, pid.Value));
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException or IOException or TimeoutException)
        {
        }
        finally
        {
            Release();
            if (!keep) socket.Dispose();
        }
    }

    private static async Task ServeControlAsync(Agent agent)
    {
        Agent? replaced;
        lock (Gate)
        {
            // One control connection per launch: a new one replaces the old.
            if (!Launches.ContainsValue(agent.Launch))
            {
                agent.Control.Dispose();
                return;
            }
            replaced = agent.Launch.Agent;
            agent.Launch.Agent = agent;
        }
        replaced?.Control.Dispose();
        try
        {
            // Tells the agent it was accepted (it waits for this before it stops backing off).
            if (!await SendAsync(agent, "ok\n", CancellationToken.None)) return;
            while (await ReadLineAsync(agent.Control, CancellationToken.None) is { } line)
            {
                // An agent can only turn down its own requests.
                if (line.Split(' ') is ["fail", var text] && long.TryParse(text, out var id)
                    && Pending.TryGetValue(id, out var request) && request.Agent == agent)
                    request.Reply.TrySetResult(null);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            lock (Gate)
                if (agent.Launch.Agent == agent) agent.Launch.Agent = null;
            agent.Closed.TrySetResult();
            agent.Control.Dispose();
        }
    }

    /// <summary>The first line of a connection from an agent: its launch token, and for a data connection the request id.</summary>
    internal static (string Token, long? Data)? Parse(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 3 || parts[0] != Magic || parts[1].Length != 32 || !parts[1].All(char.IsAsciiHexDigitLower)) return null;
        return parts switch
        {
            [_, var token, "control"] => (token, null),
            [_, var token, "data", var id] when long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) => (token, n),
            _ => null,
        };
    }

    /// <summary>Reads one ASCII line byte by byte (so nothing after it is consumed); null at the end of the stream.</summary>
    private static async Task<string?> ReadLineAsync(Socket socket, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await socket.ReceiveAsync(one, SocketFlags.None, ct) == 0) return null;
            if (one[0] == '\n') return line.ToString();
            if (line.Length >= MaxLine || one[0] is < 0x20 or > 0x7E) throw new IOException("bad line");
            line.Append((char)one[0]);
        }
    }

    private static int _viaAgent;

    /// <summary>Tests: connections made through an agent so far.</summary>
    internal static int ViaAgentCount => Volatile.Read(ref _viaAgent);

    /// <summary>Tests: whether a connection to <paramref name="port"/> would go through an agent.</summary>
    internal static bool HasAgentFor(int port) => AgentFor(port) is not null;

    /// <summary>Tests: the listener's port.</summary>
    internal static int PortForTest => Start();
}

/// <summary>Which process owns a TCP listener or connection on this computer (Windows' TCP tables; no admin needed).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class LocalPortOwners
{
    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3, TcpTableOwnerPidConnections = 4;

    /// <summary>Processes listening on <paramref name="port"/> (any local address, IPv4 or IPv6).</summary>
    public static HashSet<int> ListenerPids(int port)
    {
        var pids = new HashSet<int>();
        foreach (var row in Rows(AfInet, TcpTableOwnerPidListener).Concat(Rows(AfInet6, TcpTableOwnerPidListener)))
            if (row.Local.Port == port) pids.Add(row.Pid);
        return pids;
    }

    /// <summary>The process at the <paramref name="local"/> end of a TCP connection to <paramref name="remote"/>.</summary>
    public static int? ConnectionPid(IPEndPoint local, IPEndPoint remote)
    {
        // Dual-mode sockets report IPv4 peers as mapped IPv6 addresses; Windows lists those in the IPv4 table.
        local = Normalize(local);
        remote = Normalize(remote);
        var family = local.AddressFamily == AddressFamily.InterNetworkV6 ? AfInet6 : AfInet;
        foreach (var row in Rows(family, TcpTableOwnerPidConnections))
            if (row.Local.Equals(local) && row.Remote.Equals(remote)) return row.Pid;
        return null;
    }

    private static IPEndPoint Normalize(IPEndPoint e) =>
        e.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(e.Address.MapToIPv4(), e.Port) : e;

    internal readonly record struct Row(IPEndPoint Local, IPEndPoint Remote, int Pid);

    internal static List<Row> Rows(int family, int table)
    {
        var rows = new List<Row>();
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? 0 : Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, table, 0);
                if (result == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                if (result != 0) return rows;
                var count = Marshal.ReadInt32(buffer);
                var row = buffer + 4;
                for (var i = 0; i < count; i++)
                {
                    if (family == AfInet)
                    {
                        // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid
                        rows.Add(new Row(
                            new IPEndPoint(new IPAddress((uint)Marshal.ReadInt32(row, 4)), Port(Marshal.ReadInt32(row, 8))),
                            new IPEndPoint(new IPAddress((uint)Marshal.ReadInt32(row, 12)), Port(Marshal.ReadInt32(row, 16))),
                            Marshal.ReadInt32(row, 20)));
                        row += 24;
                    }
                    else
                    {
                        // MIB_TCP6ROW_OWNER_PID: local addr[16], scope, port, remote addr[16], scope, port, state, pid
                        rows.Add(new Row(
                            new IPEndPoint(new IPAddress(Bytes(row, 0), Marshal.ReadInt32(row, 16)), Port(Marshal.ReadInt32(row, 20))),
                            new IPEndPoint(new IPAddress(Bytes(row, 24), Marshal.ReadInt32(row, 40)), Port(Marshal.ReadInt32(row, 44))),
                            Marshal.ReadInt32(row, 52)));
                        row += 56;
                    }
                }
                return rows;
            }
            finally
            {
                if (buffer != 0) Marshal.FreeHGlobal(buffer);
            }
        }
        return rows;
    }

    /// <summary>The tables keep ports in network byte order in the low 16 bits.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static byte[] Bytes(nint at, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(at + offset, bytes, 0, 16);
        return bytes;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sort, int family, int tableClass, uint reserved);
}
