using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Host side of split room connections (design mp-split-design v3 §5–6): one listener per leg on
/// <see cref="SplitSettings.LegPort"/>, each leg a full member tunnel handshake bound to its index, the room's leg count and
/// key epoch, then <see cref="SplitRegistry"/> joins the legs of one connection. A joined connection goes to the same
/// game or chat handler as a single-path one. Only game and chat are split; a leg asking for anything else is refused.
/// <para>
/// Each leg is reached over its own EasyTier network (leg 0 over the room's network, leg i over <c>Split.Nets[i - 1]</c>);
/// which network serves which port is set up with the EasyTier instances, not here.
/// </para>
/// </summary>
public sealed class SplitTunnelServer : IAsyncDisposable
{
    private readonly RoomCode _room;
    private readonly Func<TunnelService, Func<Stream, CancellationToken, Task>?> _route;
    private readonly Action<string>? _log;
    private readonly TcpListener[] _listeners;
    private readonly Task[] _loops;
    private readonly CancellationTokenSource _cts = new();

    public SplitRegistry Registry { get; }
    public IReadOnlyList<int> Ports => [.. _listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
    /// <summary>Joined split connections handed to a handler.</summary>
    public int Accepted;
    /// <summary>Legs refused before pairing (bad handshake, a service that isn't split).</summary>
    public int Rejected;
    /// <summary>Joined connections dropped because the legs went out of step.</summary>
    public int Tampered;

    /// <param name="ports">Listening port per leg; defaults to <see cref="SplitSettings.LegPort"/> (tests pass 0).</param>
    public SplitTunnelServer(RoomCode room, Func<TunnelService, Func<Stream, CancellationToken, Task>?> route, Action<string>? log = null,
        IPAddress? address = null, IReadOnlyList<int>? ports = null, SplitRegistry? registry = null)
    {
        if (!room.FromInvite || !room.Tunnel.Wraps || !room.Split.Enabled || room.Members is null) throw new ArgumentException("split needs a wrapped invite room with split enabled", nameof(room));
        if (ports is not null && ports.Count != room.Split.Paths) throw new ArgumentException("one port per leg", nameof(ports));
        _room = room;
        _route = route;
        _log = log;
        Registry = registry ?? new SplitRegistry();
        _listeners = new TcpListener[room.Split.Paths];
        try
        {
            for (var i = 0; i < _listeners.Length; i++)
            {
                _listeners[i] = new TcpListener(address ?? IPAddress.Loopback, ports?[i] ?? SplitSettings.LegPort(i));
                _listeners[i].Start();
            }
        }
        catch
        {
            foreach (var l in _listeners) l?.Stop();
            throw;
        }
        // On the thread pool, away from any UI synchronization context.
        _loops = [.. _listeners.Select((l, i) => Task.Run(() => AcceptLoopAsync(l, i, _cts.Token)))];
    }

    private async Task AcceptLoopAsync(TcpListener listener, int index, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleLegAsync(client, index, ct);
        }
    }

    private async Task HandleLegAsync(TcpClient client, int index, CancellationToken ct)
    {
        Pump.Tune(client);
        // The leg's stream owns the socket: once the leg is handed to the registry, the joined stream closes it.
        Stream raw = new OwnedStream(client);
        var leg = new SplitLeg(index, _room.Split.Paths, _room.Epoch);
        // Who has to close what: the raw socket until the handshake wraps it, then the secure leg until the registry takes it,
        // then the joined stream (only on the call that completes the set).
        var rawOwned = true;
        SecureStream? secure = null;
        SplitStream? joined = null;
        CancellationTokenRegistration revocation = default;
        try
        {
            var accepted = await TunnelHandshake.AcceptWithMemberAsync(raw, _room, _room.Tunnel.Mode, _room.Tunnel.Disguise, ct, leg);
            if (accepted is null) return;
            rawOwned = false;
            (secure, var service, var member) = accepted.Value;
            var handler = member is null || _room.Split.For(service) == SplitPolicy.Off ? null : _route(service);
            if (handler is null)
            {
                Interlocked.Increment(ref Rejected);
                _log?.Invoke(L.F("core.mp.transport.endpoint_service_unavailable", service));
                return;
            }
            // From here the registry owns the leg (it aborts it on any refusal).
            var handed = secure;
            secure = null;
            joined = await Registry.AcceptAsync(handed, member!, leg, (byte)service, ct);
            // Not the last leg: it now lives inside the joined stream, which the last leg's call serves.
            if (joined is null) return;
            joined.Tampered += () =>
            {
                Interlocked.Increment(ref Tampered);
                _log?.Invoke(L.T("core.mp.split.mismatch"));
            };
            // The member's key revoked (kick, re-approval, room ended): drop every leg at once.
            var target = joined;
            revocation = member!.Revocation.Register(target.Abort);
            Interlocked.Increment(ref Accepted);
            await handler(joined, ct);
        }
        catch (Exception e) when (e is CryptographicException or IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                   or PlatformNotSupportedException or InvalidDataException or ArgumentException)
        {
            // Failed: no close records, the other side sees a cut connection.
            secure?.Abort();
            joined?.Abort();
            Interlocked.Increment(ref Rejected);
            if (e is CryptographicException or PlatformNotSupportedException) _log?.Invoke(L.T("core.mp.transport.endpoint_handshake_failed") + e.Message);
        }
        finally
        {
            await revocation.DisposeAsync();
            if (secure is not null) secure.Abort();
            if (joined is not null) await joined.DisposeAsync();
            if (rawOwned) await DisposeQuietlyAsync(raw);
        }
    }

    private static async Task DisposeQuietlyAsync(Stream stream)
    {
        try { await stream.DisposeAsync(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var l in _listeners) l.Stop();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

/// <summary>
/// Member side: opens one game or chat connection, split over the room's paths when its policy asks for it.
/// <list type="bullet">
/// <item>Off: always one path, as before.</item>
/// <item>Auto: split when every leg can be opened; otherwise one path, and <see cref="LastReason"/> says why. After
/// <see cref="MaxFailures"/> split attempts in a row fail, it stops trying for this session instead of retrying forever.</item>
/// <item>Strict: split or nothing. A connection that can't be split fails with the reason.</item>
/// </list>
/// A device attestation failure is never a reason to fall back: it is reported like on one path.
/// </summary>
public sealed class SplitConnector
{
    public const int MaxFailures = 3;

    private readonly RoomCode _room;
    private readonly TunnelService _service;
    private readonly Func<CancellationToken, Task<Stream>> _single;
    private readonly IReadOnlyList<Func<CancellationToken, Task<Stream>>>? _legs;
    private readonly Action<string>? _log;
    private readonly Func<string?>? _pathProblem;
    private int _failures;

    public SplitPolicy Policy { get; }
    public int SplitConnections;
    public int SinglePathConnections;
    /// <summary>Why the last connection used one path (null when it was split, or splitting is off).</summary>
    public string? LastReason { get; private set; }

    /// <param name="single">Opens a single-path tunnel connection (the existing way).</param>
    /// <param name="legs">Opens the raw connection for each leg, in leg order; null when the paths aren't set up (for example in TUN mode).</param>
    /// <param name="pathProblem">Why the paths can't be used right now (null: they can). Asked before every connection.</param>
    public SplitConnector(RoomCode room, TunnelService service, Func<CancellationToken, Task<Stream>> single,
        IReadOnlyList<Func<CancellationToken, Task<Stream>>>? legs, Action<string>? log = null, Func<string?>? pathProblem = null)
    {
        _pathProblem = pathProblem;
        _room = room;
        _service = service;
        _single = single;
        _log = log;
        Policy = room.FromInvite && room.Tunnel.Wraps ? room.Split.For(service) : SplitPolicy.Off;
        _legs = legs is not null && legs.Count == room.Split.Paths ? legs : null;
    }

    public async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (Policy == SplitPolicy.Off) return await _single(ct);
        if (_legs is null) return await NotSplitAsync(L.T("core.mp.split.not_ready"), ct);
        if (_pathProblem?.Invoke() is { } problem) return await NotSplitAsync(problem, ct);
        if (Policy == SplitPolicy.Auto && Volatile.Read(ref _failures) >= MaxFailures) return await NotSplitAsync(L.T("core.mp.split.gave_up"), ct);
        try
        {
            var stream = await SplitLegs.ConnectAsync((i, c) => _legs[i](c), _room.Split.Paths, _room.Epoch,
                (raw, leg, c) => TunnelHandshake.ConnectAsync(raw, _room, _room.Tunnel.Mode, _room.Tunnel.Disguise, _service, c, leg), ct);
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Increment(ref SplitConnections);
            _log?.Invoke(L.F(_service == TunnelService.Game ? "core.mp.split.connected_game" : "core.mp.split.connected_chat", stream.LegCount));
            LastReason = null;
            return stream;
        }
        catch (DeviceAttestationException)
        {
            throw;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or CryptographicException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _failures);
            _log?.Invoke(L.T("core.mp.split.failed") + e.Message);
            return await NotSplitAsync(L.T("core.mp.split.failed") + e.Message, ct, e);
        }
    }

    private async Task<Stream> NotSplitAsync(string reason, CancellationToken ct, Exception? inner = null)
    {
        LastReason = reason;
        if (Policy == SplitPolicy.Strict) throw new IOException(L.T("core.mp.split.strict_unavailable") + " " + reason, inner);
        var stream = await _single(ct);
        Interlocked.Increment(ref SinglePathConnections);
        return stream;
    }
}
