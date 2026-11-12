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
/// The listener is on 127.0.0.1 only. An agent proves it was started by this Jay with the secret on its command line,
/// and its process is read from Windows' connection table, never from what it says.
/// </para>
/// </summary>
public static class GameLanBridge
{
    internal const string Magic = "JAYLAN1";
    private const int MaxLine = 128;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly object Gate = new();
    private static TcpListener? _listener;
    private static string _secret = "";
    private static readonly List<Agent> Agents = [];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<Socket?>> Pending = new();
    private static long _nextId;

    private sealed class Agent(Socket control, int pid)
    {
        public Socket Control { get; } = control;
        public int Pid { get; } = pid;
        public SemaphoreSlim Write { get; } = new(1, 1);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The JVM argument that loads the agent from <paramref name="jar"/> and points it at this Jay (the listener starts
    /// on first use). The secret is random per Jay run.
    /// </summary>
    public static string AgentArgument(string jar)
    {
        var (port, secret) = Start();
        return $"-javaagent:{jar}={port}:{secret}";
    }

    /// <summary>Games with a connected agent (tests, diagnostics).</summary>
    public static int AgentCount
    {
        get { lock (Gate) return Agents.Count; }
    }

    /// <summary>Whether a game with a bridge agent is running: rooms then pass the game port through Jay's relay.</summary>
    public static bool HasAgents => AgentCount > 0;

    /// <summary>
    /// Connects to <paramref name="port"/> on 127.0.0.1: through the agent of the game that listens there, else directly.
    /// Null when nothing answered within <paramref name="timeout"/>; throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task<Socket?> ConnectAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
        try
        {
            return AgentFor(port) is { } agent ? await ViaAgentAsync(agent, port, linked.Token).ConfigureAwait(false) : await DirectAsync(port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<Socket?> DirectAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct).ConfigureAwait(false);
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
        lock (Gate)
            if (Agents.Count == 0) return null;
        if (!OperatingSystem.IsWindows()) return null;
        var owners = LocalPortOwners.ListenerPids(port);
        lock (Gate)
            for (var i = Agents.Count - 1; i >= 0; i--)
                if (owners.Contains(Agents[i].Pid)) return Agents[i];
        return null;
    }

    private static async Task<Socket?> ViaAgentAsync(Agent agent, int port, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<Socket?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = reply;
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

    private static (int Port, string Secret) Start()
    {
        lock (Gate)
        {
            if (_listener is null)
            {
                _secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listener = listener;
                _ = Task.Run(() => AcceptLoopAsync(listener));
            }
            return (((IPEndPoint)_listener.LocalEndpoint).Port, _secret);
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
            _ = Task.Run(() => HandshakeAsync(socket));
        }
    }

    private static async Task HandshakeAsync(Socket socket)
    {
        var keep = false;
        try
        {
            string? line;
            using (var timeout = new CancellationTokenSource(HandshakeTimeout))
                line = await ReadLineAsync(socket, timeout.Token);
            if (line is null || Parse(line, _secret) is not { } hello) return;
            socket.NoDelay = true;
            if (hello.Data is { } id)
            {
                if (Pending.TryGetValue(id, out var reply) && reply.TrySetResult(socket)) keep = true;
                return;
            }
            if (!OperatingSystem.IsWindows()
                || LocalPortOwners.ConnectionPid((IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!) is not { } pid)
                return;
            keep = true;
            await ServeControlAsync(new Agent(socket, pid));
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            if (!keep) socket.Dispose();
        }
    }

    /// <summary>More control connections than any number of running games needs; more are turned away.</summary>
    private const int MaxAgents = 16;

    private static async Task ServeControlAsync(Agent agent)
    {
        lock (Gate)
        {
            if (Agents.Count >= MaxAgents)
            {
                agent.Control.Dispose();
                return;
            }
            Agents.Add(agent);
        }
        try
        {
            while (await ReadLineAsync(agent.Control, CancellationToken.None) is { } line)
            {
                var parts = line.Split(' ');
                if (parts is ["fail", var text] && long.TryParse(text, out var id) && Pending.TryGetValue(id, out var reply))
                    reply.TrySetResult(null);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            lock (Gate) Agents.Remove(agent);
            agent.Closed.TrySetResult();
            agent.Control.Dispose();
        }
    }

    /// <summary>The first line of a connection from an agent: control, or data for request <c>id</c>. Null when it is not one.</summary>
    internal static (bool Control, long? Data)? Parse(string line, string secret)
    {
        var parts = line.Split(' ');
        if (parts.Length < 3 || parts[0] != Magic || secret.Length == 0) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(secret))) return null;
        return parts switch
        {
            [_, _, "control"] => (true, null),
            [_, _, "data", var id] when long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) => (false, n),
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
