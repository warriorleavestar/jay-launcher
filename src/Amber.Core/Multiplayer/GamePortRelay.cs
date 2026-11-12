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
/// It listens on 127.0.0.1 only, the same place EasyTier and the direct bridge hand room traffic to, so it is not
/// reachable from the network. The caller decides which ports are trusted as targets (see the view model's port check).
/// </para>
/// </summary>
public sealed class GamePortRelay : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _gate = new();
    private readonly HashSet<IDisposable> _open = [];
    private readonly TimeSpan _connectTimeout;
    private int _target;

    public GamePortRelay(TimeSpan? connectTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
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

    /// <summary>Connections being passed on right now (tests).</summary>
    public int OpenConnections
    {
        get { lock (_gate) return _open.Count / 2; }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var inbound = client;
        if (Target is not { } port)
        {
            // No game yet: close at once so the player's game shows a connection error instead of hanging.
            return;
        }
        Socket? outbound = null;
        try
        {
            // Through the game's LAN bridge when the game is in the strong sandbox (see GameLanBridge), else directly.
            outbound = await GameLanBridge.ConnectAsync(port, _connectTimeout, _cts.Token);
            if (outbound is null) return;
            inbound.NoDelay = true;
            outbound.NoDelay = true;
            lock (_gate)
            {
                if (_cts.IsCancellationRequested) return;
                _open.Add(inbound);
                _open.Add(outbound);
            }
            var a = inbound.GetStream();
            var b = new NetworkStream(outbound, ownsSocket: false);
            var up = PumpAsync(a, b);
            var down = PumpAsync(b, a);
            await Task.WhenAny(up, down);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _open.Remove(inbound);
                if (outbound is not null) _open.Remove(outbound);
            }
            outbound?.Dispose();
        }
    }

    private async Task PumpAsync(Stream from, Stream to)
    {
        try
        {
            await from.CopyToAsync(to, 16 * 1024, _cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _listener.Stop();
        IDisposable[] open;
        lock (_gate) open = [.. _open];
        foreach (var c in open) c.Dispose();
        try { await _acceptLoop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        // _cts is not disposed: connections still finishing read its token.
    }
}
