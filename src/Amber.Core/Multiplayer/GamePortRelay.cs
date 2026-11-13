using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

/// <summary>
/// A room opened before the game: the room's game port is this loopback listener, and connections are passed on to the
/// game's real LAN port once it is known (the world was opened to LAN, or the player typed a port). Until then a
/// connection is closed straight away. Changing the target later (the world was reopened on a new port) only affects
/// new connections.
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
    private readonly HashSet<TcpClient> _open = [];
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

    /// <summary>Connections being passed on right now (tests).</summary>
    public int OpenConnections
    {
        get { lock (_gate) return _open.Count / 2; }
    }

    /// <summary>
    /// Also listen on <paramref name="address"/> (a whole-LAN room's virtual address) at the same port, waiting for the
    /// adapter to get that address. Completes once listening; throws the <see cref="SocketException"/> when it gives up,
    /// or <see cref="OperationCanceledException"/> when the relay is disposed first.
    /// </summary>
    /// <param name="allowSource">Which callers' addresses are passed on there (the room's virtual subnet); others are
    /// closed at once, before anything is forwarded.</param>
    public async Task ListenOnAdapterAsync(IPAddress address, Func<IPAddress, bool> allowSource, TimeSpan? wait = null, Func<IPAddress, int, TcpListener>? listen = null)
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
            _adapterLoop = Task.Run(() => AdapterLoopAsync(listener, allowSource));
        }
    }

    /// <summary>Listening on an adapter address too (see <see cref="ListenOnAdapterAsync"/>).</summary>
    public bool ListensOnAdapter
    {
        get { lock (_gate) return _adapterListener is not null; }
    }

    private async Task AdapterLoopAsync(TcpListener listener, Func<IPAddress, bool> allowSource)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                // The adapter went away (or the relay stopped): this listener is done. A listener
                // stopped before the accept starts throws InvalidOperationException ("not listening").
                try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException) { return; }
                IPAddress? source;
                try { source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address; }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { source = null; }
                if (source is null || !allowSource(source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_adapterListener == listener) _adapterListener = null;
            }
            listener.Stop();
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

    private async Task ServeAsync(TcpClient client)
    {
        using var inbound = client;
        if (Target is not { } port)
        {
            // No game yet: close at once so the player's game shows a connection error instead of hanging.
            return;
        }
        using var outbound = new TcpClient();
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(_connectTimeout);
                await outbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            }
            inbound.NoDelay = true;
            outbound.NoDelay = true;
            lock (_gate)
            {
                if (_cts.IsCancellationRequested) return;
                _open.Add(inbound);
                _open.Add(outbound);
            }
            var a = inbound.GetStream();
            var b = outbound.GetStream();
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
                _open.Remove(outbound);
            }
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
        Task? adapterLoop;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested) return;
            _cts.Cancel();
            _adapterListener?.Stop();
            adapterLoop = _adapterLoop;
        }
        _listener.Stop();
        TcpClient[] open;
        lock (_gate) open = [.. _open];
        foreach (var c in open) c.Dispose();
        try { await _acceptLoop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        if (adapterLoop is not null)
            try { await adapterLoop; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        // _cts is not disposed: connections still finishing read its token.
    }
}
