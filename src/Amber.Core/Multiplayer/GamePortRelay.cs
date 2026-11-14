using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

/// <summary>
/// A room opened before the game: the room's game port is this loopback listener, and connections are passed on to the
/// game's real LAN port once it is known (the world was opened to LAN, or the player typed a port). Until then a
/// connection is closed straight away. Changing the target later (the world was reopened on a new port) only affects
/// new connections. A game in the Windows strong sandbox is reached through its LAN bridge (<see cref="GameLanBridge"/>),
/// which is why rooms always put this relay in front of such a game.
/// <para>
/// It listens on 127.0.0.1, the same place EasyTier and the direct bridge hand room traffic to, so it is not reachable
/// from the network. A whole-LAN room without wrapping, where guests reach the host's virtual address directly, also
/// listens on that address (<see cref="ListenOnAdapterAsync"/>). Packets for that address can also arrive on another
/// adapter, so connections there are passed on only from the sources the caller allows (the room's virtual subnet)
/// and closed otherwise. The caller decides which ports are trusted as targets (see the view model's port check).
/// </para>
/// </summary>
public sealed class GamePortRelay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private TcpListener? _adapterListener;
    private Task? _adapterLoop;
    private readonly object _gate = new();
    // Each connection being passed on: the caller's side, its side to the game and the game port it reached.
    private readonly HashSet<(TcpClient In, Socket Out, int Port)> _open = [];
    // Callers taken on for a game port whose connection to the game isn't up yet, by port.
    private readonly Dictionary<int, int> _connecting = [];
    /// <summary>Connections passed on (or being connected) at once; a LAN world takes far fewer players.</summary>
    internal const int MaxConnections = 64;

    /// <summary>Connections one guest whose address is known may have passed on (or being connected) at once.</summary>
    internal const int MaxPerSource = 16;

    // Connections passed on (or being connected) by guest address, for those whose address is known.
    private readonly Dictionary<IPAddress, int> _perSource = [];

    /// <summary>
    /// A connection over which nothing has passed either way for this long is closed and its place freed. A game in
    /// play never goes this quiet (Minecraft sends a keep-alive every 15 seconds), so a caller can't park places it
    /// doesn't use. This matters most where callers can't be told apart (see <see cref="ServeAsync"/>).
    /// </summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// New connections from callers that can't be told apart (loopback) allowed at once: a connection counts as new for
    /// <see cref="NewFor"/>. Minecraft drops a connection that hasn't logged in by then and a world takes a limited number
    /// of players, so a flood of fresh connections only delays new players; players already in are never pushed out, and
    /// a room can still pass on <see cref="MaxConnections"/>.
    /// </summary>
    internal const int MaxNewUnidentified = 16;

    internal TimeSpan NewFor { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tests: the cap on new unidentified connections (raised to test the total limit on its own).</summary>
    internal int MaxNewForTest { get; init; } = MaxNewUnidentified;

    // Unidentified connections still counted as new (under _gate).
    private int _newUnidentified;

    private readonly TimeSpan _connectTimeout;
    private int _target;

    public GamePortRelay(TimeSpan? connectTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener));
    }

    /// <summary>The port the room uses as its game port.</summary>
    public int Port { get; }

    /// <summary>The game's real port, or null while waiting for the game.</summary>
    public int? Target
    {
        get
        {
            var t = Volatile.Read(ref _target);
            return t == 0 ? null : t;
        }
    }

    /// <summary>Point new connections at the game's port on this computer.</summary>
    public void SetTarget(int port)
    {
        if (port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (port == Port) throw new ArgumentException("The relay can't point at itself.", nameof(port));
        Volatile.Write(ref _target, port);
    }

    /// <summary>Back to waiting: new connections are closed until a port is set again.</summary>
    public void ClearTarget() => Volatile.Write(ref _target, 0);

    /// <summary>
    /// Back to waiting, but only if the relay still points at <paramref name="port"/> and nobody is connected to it right
    /// now: a game that answered a player since it was last checked is in use, whatever the check said. Checked and
    /// cleared together, so a connection being passed on can't slip in between.
    /// </summary>
    public bool ClearTargetIfIdle(int port)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _target) != port || _open.Any(c => c.Port == port) || _connecting.ContainsKey(port)) return false;
            Volatile.Write(ref _target, 0);
            return true;
        }
    }

    /// <summary>Connections being passed on right now (tests).</summary>
    public int OpenConnections
    {
        get { lock (_gate) return _open.Count; }
    }

    /// <summary>
    /// Connections passed on to one game port. Connections to a world linked before stay open after a switch, so they
    /// say nothing about whether the world linked now still answers.
    /// </summary>
    public int OpenConnectionsTo(int port)
    {
        lock (_gate) return _open.Count(c => c.Port == port) + _connecting.GetValueOrDefault(port);
    }

    /// <summary>Tests: runs once a caller is taken on for a port, before the connection to the game is made.</summary>
    internal Func<Task>? BeforeConnectForTest { get; set; }

    /// <summary>Tests: makes the connection to the game in place of <see cref="GameLanBridge.ConnectAsync"/> (to make that fail).</summary>
    internal Func<Socket>? CreateOutboundForTest { get; set; }

    /// <summary>
    /// Also listen on <paramref name="address"/> (a whole-LAN room's virtual address) at the same port, waiting for the
    /// adapter to get that address. Completes once listening; throws the <see cref="SocketException"/> when it gives up,
    /// or <see cref="OperationCanceledException"/> when the relay is disposed first.
    /// </summary>
    /// <param name="allowSource">Which callers' addresses are passed on there (the room's virtual subnet); others are
    /// closed at once, before anything is forwarded.</param>
    /// <param name="lost">Called (on a pool thread) with the reason if that listener stops while the relay is still
    /// running, for example because the adapter went away; the caller can listen again or end the room.</param>
    public async Task ListenOnAdapterAsync(IPAddress address, Func<IPAddress, bool> allowSource, TimeSpan? wait = null,
        Func<IPAddress, int, TcpListener>? listen = null, Action<Exception>? lost = null)
    {
        var listener = await AdapterListener.BindAsync(address, Port, wait ?? AdapterListener.DefaultWait, _cts.Token, listen);
        lock (_gate)
        {
            if (_cts.IsCancellationRequested || _adapterListener is not null)
            {
                listener.Stop();
                _cts.Token.ThrowIfCancellationRequested();
                return;
            }
            _adapterListener = listener;
            _adapterLoop = Task.Run(() => AdapterLoopAsync(listener, allowSource, lost));
        }
    }

    /// <summary>Listening on an adapter address too (see <see cref="ListenOnAdapterAsync"/>).</summary>
    public bool ListensOnAdapter
    {
        get { lock (_gate) return _adapterListener is not null; }
    }

    /// <summary>Accepts the next connection (tests replace it to throw).</summary>
    internal Func<TcpListener, CancellationToken, ValueTask<TcpClient>> AcceptForTest { get; set; } = (l, ct) => l.AcceptTcpClientAsync(ct);

    /// <summary>A caller that gave up before its connection was accepted: only that connection is gone.</summary>
    internal static bool IsTransientAccept(SocketException e) =>
        e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;

    private async Task AdapterLoopAsync(TcpListener listener, Func<IPAddress, bool> allowSource, Action<Exception>? lost)
    {
        Exception? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await AcceptForTest(listener, _cts.Token); }
                catch (SocketException e) when (IsTransientAccept(e)) { continue; }
                // The adapter went away (or the relay stopped): this listener is done. A listener
                // stopped before the accept starts throws InvalidOperationException ("not listening").
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
                {
                    reason = e;
                    return;
                }
                IPAddress? source;
                try { source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { source = null; }
                if (source is not null && source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
                if (source is null || !allowSource(source))
                {
                    client.Dispose();
                    continue;
                }
                var guest = source;
                _ = Task.Run(() => ServeAsync(client, guest));
            }
        }
        finally
        {
            bool stillRunning;
            lock (_gate)
            {
                if (_adapterListener == listener) _adapterListener = null;
                stillRunning = !_cts.IsCancellationRequested;
            }
            listener.Stop();
            // Nobody would notice otherwise: the room would look open but take no new guests.
            if (stillRunning && lost is not null)
                _ = Task.Run(() => lost(reason ?? new SocketException((int)SocketError.NotSocket)));
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    /// <param name="source">The guest's address when it is known (a whole-LAN guest on the virtual address); its
    /// connections count against <see cref="MaxPerSource"/>. Null on loopback, where callers can't be told apart (the
    /// wrapped transport limits each member before it hands a connection over).</param>
    private async Task ServeAsync(TcpClient client, IPAddress? source = null)
    {
        using var inbound = client;
        int port;
        // 1 while this unidentified connection still counts as new; whoever sets it to 0 first gives the place back.
        var isNew = 0;
        void NoLongerNew()
        {
            if (Interlocked.Exchange(ref isNew, 0) == 1)
                lock (_gate) _newUnidentified--;
        }
        // The target is read and the caller counted for it under the same lock ClearTargetIfIdle takes, so a game that
        // is taking a player right now is never dropped between the two.
        lock (_gate)
        {
            // No game yet: close at once so the player's game shows a connection error instead of hanging.
            if (Target is not { } target || _cts.IsCancellationRequested) return;
            // Players passed on at once, counted from the moment they are taken on: more are closed straight away.
            if (_open.Count + _connecting.Values.Sum() >= MaxConnections) return;
            // One guest can't take every place: past its own share only that guest is turned away.
            if (source is not null)
            {
                if (_perSource.GetValueOrDefault(source) >= MaxPerSource) return;
                _perSource[source] = _perSource.GetValueOrDefault(source) + 1;
            }
            else
            {
                if (_newUnidentified >= MaxNewForTest) return;
                _newUnidentified++;
                isNew = 1;
            }
            port = target;
            _connecting[port] = _connecting.GetValueOrDefault(port) + 1;
        }
        if (isNew == 1) _ = Task.Delay(NewFor, _cts.Token).ContinueWith(_ => NoLongerNew(), TaskScheduler.Default);
        var connecting = true;
        Socket? outbound = null;
        (TcpClient, Socket, int)? entry = null;
        try
        {
            if (BeforeConnectForTest is { } before) await before();
            // Through the game's LAN bridge when the game is in the strong sandbox (see GameLanBridge), else directly.
            // Inside the try so a failure creating the connection still takes the caller off the connecting count.
            outbound = CreateOutboundForTest is { } create ? create() : await GameLanBridge.ConnectAsync(port, _connectTimeout, _cts.Token);
            if (outbound is null) return;
            inbound.NoDelay = true;
            outbound.NoDelay = true;
            entry = (inbound, outbound, port);
            lock (_gate)
            {
                Done();
                if (_cts.IsCancellationRequested) return;
                _open.Add(entry.Value);
            }
            var a = inbound.GetStream();
            var b = new NetworkStream(outbound, ownsSocket: false);
            var lastSeen = Environment.TickCount64;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var up = PumpAsync(a, b, () => Volatile.Write(ref lastSeen, Environment.TickCount64), idle.Token);
            var down = PumpAsync(b, a, () => Volatile.Write(ref lastSeen, Environment.TickCount64), idle.Token);
            var watch = WatchIdleAsync(() => Volatile.Read(ref lastSeen), idle.Token);
            await Task.WhenAny(up, down, watch);
            idle.Cancel();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
            {
                Done();
                if (entry is { } done) _open.Remove(done);
                if (source is not null)
                {
                    if (_perSource.GetValueOrDefault(source) <= 1) _perSource.Remove(source);
                    else _perSource[source]--;
                }
            }
            outbound?.Dispose();
            NoLongerNew();
        }

        // No longer connecting (once): the caller is now open, or gone.
        void Done()
        {
            if (!connecting) return;
            connecting = false;
            if (_connecting.GetValueOrDefault(port) <= 1) _connecting.Remove(port);
            else _connecting[port]--;
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, Action seen, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                seen();
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends when nothing has passed for <see cref="IdleTimeout"/> (or when <paramref name="ct"/> is cancelled).</summary>
    private async Task WatchIdleAsync(Func<long> lastSeen, CancellationToken ct)
    {
        var limit = (long)IdleTimeout.TotalMilliseconds;
        var step = TimeSpan.FromMilliseconds(Math.Clamp(limit / 4, 10, 5000));
        try
        {
            while (Environment.TickCount64 - lastSeen() < limit) await Task.Delay(step, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? adapterLoop;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            _adapterListener?.Stop();
            adapterLoop = _adapterLoop;
        }
        _listener.Stop();
        (TcpClient In, Socket Out, int Port)[] open;
        lock (_gate) open = [.. _open];
        foreach (var c in open)
        {
            c.In.Dispose();
            c.Out.Dispose();
        }
        try { await _acceptLoop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        if (adapterLoop is not null)
            try { await adapterLoop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        // _cts is not disposed: connections still finishing read its token.
    }
}
