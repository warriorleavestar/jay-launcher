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
    private readonly HashSet<Task> _running = [];

    public SplitRegistry Registry { get; }
    public IReadOnlyList<int> Ports => [.. _listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)];
    private int _accepted;
    private int _rejected;
    private int _tampered;

    /// <summary>Joined split connections handed to a handler.</summary>
    public int Accepted => Volatile.Read(ref _accepted);
    /// <summary>Legs refused before pairing (bad handshake, a service that isn't split, an unexpected failure).</summary>
    public int Rejected => Volatile.Read(ref _rejected);
    /// <summary>Joined connections dropped because the legs went out of step.</summary>
    public int Tampered => Volatile.Read(ref _tampered);

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
            Track(HandleLegAsync(client, index, ct));
        }
    }

    /// <summary>Keeps each leg's task until it ends, so <see cref="DisposeAsync"/> can wait for the open connections.</summary>
    private void Track(Task task)
    {
        lock (_running) _running.Add(task);
        _ = task.ContinueWith(t => { lock (_running) _running.Remove(t); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
                Interlocked.Increment(ref _rejected);
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
                Interlocked.Increment(ref _tampered);
                _log?.Invoke(L.T("core.mp.split.mismatch"));
            };
            // The member's key revoked (kick, re-approval, room ended): drop every leg at once.
            var target = joined;
            revocation = member!.Revocation.Register(target.Abort);
            Interlocked.Increment(ref _accepted);
            try
            {
                await handler(joined, ct);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or CryptographicException)
            {
                // The forwarding ended (the other side closed, a kick, the room ending): not a refused leg.
                joined.Abort();
            }
        }
        catch (Exception e) when (e is CryptographicException or IOException or SocketException or OperationCanceledException or ObjectDisposedException
                                   or PlatformNotSupportedException or InvalidDataException or ArgumentException)
        {
            // Failed: no close records, the other side sees a cut connection.
            secure?.Abort();
            joined?.Abort();
            Interlocked.Increment(ref _rejected);
            if (e is CryptographicException or PlatformNotSupportedException) _log?.Invoke(L.T("core.mp.transport.endpoint_handshake_failed") + e.Message);
        }
        catch (Exception e)
        {
            // Anything else still ends this leg quietly, is counted, and is logged by type only (no member details).
            secure?.Abort();
            joined?.Abort();
            Interlocked.Increment(ref _rejected);
            _log?.Invoke(L.T("core.mp.transport.endpoint_handshake_failed") + e.GetType().Name);
        }
        finally
        {
            await revocation.DisposeAsync();
            if (secure is not null) secure.Abort();
            // Closing a joined connection writes a close record, which fails when the other side is already gone.
            if (joined is not null) await DisposeQuietlyAsync(joined);
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
        // Open connections see the cancellation and end; wait a little for them so nothing is still forwarding afterwards.
        Task[] running;
        lock (_running) running = [.. _running];
        // Only waiting here: a leg's own failure was already counted and logged where it happened, and must not stop the
        // room from closing the rest.
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(5)));
        _cts.Dispose();
    }
}

/// <summary>
/// Member side: opens one game or chat connection, split over the room's paths when its policy asks for it.
/// <list type="bullet">
/// <item>Off: always one path, as before.</item>
/// <item>Auto: split when every leg can be opened; otherwise one path, and <see cref="LastReason"/> says why. After
/// <see cref="MaxFailures"/> split attempts in a row fail, it uses one path for <see cref="RetryAfter"/> before trying again,
/// instead of retrying on every connection.</item>
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
    private readonly string? _noLegsReason;
    private int _failures;
    private long _pausedUntil;

    /// <summary>How long Auto waits after <see cref="MaxFailures"/> failures in a row before it tries to split again.</summary>
    internal TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    public SplitPolicy Policy { get; }
    private int _splitConnections;
    private int _singlePathConnections;
    public int SplitConnections => Volatile.Read(ref _splitConnections);
    public int SinglePathConnections => Volatile.Read(ref _singlePathConnections);
    /// <summary>
    /// Why the last connection used one path (null when it was split, or splitting is off). Only for showing: connections
    /// opened at the same time may overwrite each other's reason.
    /// </summary>
    public string? LastReason { get; private set; }

    /// <param name="single">Opens a single-path tunnel connection (the existing way).</param>
    /// <param name="legs">Opens the raw connection for each leg, in leg order; null when the paths aren't set up (for example in TUN mode).</param>
    /// <param name="noLegsReason">Why there are no legs, when <paramref name="legs"/> is null for a known reason (full-LAN mode).</param>
    public SplitConnector(RoomCode room, TunnelService service, Func<CancellationToken, Task<Stream>> single,
        IReadOnlyList<Func<CancellationToken, Task<Stream>>>? legs, Action<string>? log = null, string? noLegsReason = null)
    {
        _noLegsReason = noLegsReason;
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
        if (_legs is null) return await NotSplitAsync(_noLegsReason ?? L.T("core.mp.split.not_ready"), ct);
        if (Policy == SplitPolicy.Auto && Volatile.Read(ref _failures) >= MaxFailures)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref _pausedUntil)) return await NotSplitAsync(L.T("core.mp.split.gave_up"), ct);
            // The pause is over: one more try. Another failure pauses again.
            Interlocked.Exchange(ref _failures, MaxFailures - 1);
        }
        try
        {
            var stream = await SplitLegs.ConnectAsync((i, c) => _legs[i](c), _room.Split.Paths, _room.Epoch,
                (raw, leg, c) => TunnelHandshake.ConnectAsync(raw, _room, _room.Tunnel.Mode, _room.Tunnel.Disguise, _service, c, leg), ct);
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Increment(ref _splitConnections);
            LastReason = null;
            return stream;
        }
        catch (DeviceAttestationException)
        {
            throw;
        }
        // The same failures the host side treats as a failed handshake (the Minecraft disguise can raise the last three).
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or CryptographicException or SocketException or OperationCanceledException
                                   or ObjectDisposedException or InvalidDataException or ArgumentException or PlatformNotSupportedException)
        {
            if (Interlocked.Increment(ref _failures) >= MaxFailures)
                Interlocked.Exchange(ref _pausedUntil, System.Diagnostics.Stopwatch.GetTimestamp() + (long)(RetryAfter.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
            _log?.Invoke(L.T("core.mp.split.failed") + e.Message);
            return await NotSplitAsync(L.T("core.mp.split.failed") + e.Message, ct, e);
        }
    }

    private async Task<Stream> NotSplitAsync(string reason, CancellationToken ct, Exception? inner = null)
    {
        LastReason = reason;
        if (Policy == SplitPolicy.Strict) throw new IOException(L.F("core.mp.split.strict_unavailable", reason), inner);
        var stream = await _single(ct);
        Interlocked.Increment(ref _singlePathConnections);
        return stream;
    }
}
