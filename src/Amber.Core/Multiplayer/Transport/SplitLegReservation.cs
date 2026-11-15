using System.Net;
using System.Net.Sockets;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// The host's leg ports (<see cref="SplitSettings.LegPort"/>), bound before the room's EasyTier instances start. The
/// instances only let room members reach the leg ports when this reservation exists, so a port that another program
/// already uses is never opened to the room.
/// <para>The session holds the ports for as long as EasyTier serves them. A <see cref="SplitTunnelServer"/> borrows the
/// listeners and gives them back when it stops, so a transport restarting in the same session never lets the ports go.
/// A connection still waiting in a listener's queue when one transport stops is accepted by the next; if the room was
/// rekeyed in between, its leg carries the old epoch and fails the leg check there.</para>
/// </summary>
public sealed class SplitLegReservation : IDisposable
{
    private readonly TcpListener[] _all;
    private TcpListener[]? _listeners;
    private int _borrowed;

    private SplitLegReservation(TcpListener[] listeners) => _all = _listeners = listeners;

    /// <summary>Binds every leg port of a room with this many paths, or none: null when any of them is taken.</summary>
    public static SplitLegReservation? TryReserve(int paths, IPAddress? address = null, IReadOnlyList<int>? ports = null)
    {
        var listeners = new TcpListener[paths];
        try
        {
            for (var i = 0; i < paths; i++) listeners[i] = Bind(address ?? IPAddress.Loopback, ports?[i] ?? SplitSettings.LegPort(i));
            return new SplitLegReservation(listeners);
        }
        catch (SocketException)
        {
            foreach (var l in listeners) l?.Stop();
            return null;
        }
    }

    /// <summary>
    /// A listening socket that no other program shares. On Windows a plain bind can succeed next to another program's
    /// wildcard bind on the same port, so the bind asks for the port to itself.
    /// </summary>
    internal static TcpListener Bind(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
        }
        catch
        {
            listener.Stop();
            throw;
        }
        return listener;
    }

    /// <summary>
    /// The bound listeners, in leg order, for good: whoever takes them stops them. Null when they were taken, borrowed or
    /// released. For a reservation that isn't the session's (the transport bound the ports itself).
    /// </summary>
    internal TcpListener[]? TryTake() => Volatile.Read(ref _borrowed) != 0 ? null : Interlocked.Exchange(ref _listeners, null);

    /// <summary>
    /// The bound listeners, in leg order, lent until <see cref="Return"/>; they stay this reservation's. Null when they are
    /// already lent, taken or released.
    /// </summary>
    internal TcpListener[]? TryBorrow()
    {
        if (Interlocked.Exchange(ref _borrowed, 1) != 0) return null;
        if (Volatile.Read(ref _listeners) is { } listeners) return listeners;
        Volatile.Write(ref _borrowed, 0);
        return null;
    }

    /// <summary>Gives lent listeners back, still bound, for the next borrower.</summary>
    internal void Return() => Volatile.Write(ref _borrowed, 0);

    /// <summary>A room with a Strict service needs its leg ports; without them it doesn't open.</summary>
    internal static bool NeedsLegs(SplitSettings split) =>
        split.For(TunnelService.Game) == SplitPolicy.Strict || split.For(TunnelService.Chat) == SplitPolicy.Strict;

    internal static IOException Busy(SplitSettings split, Exception? inner = null) =>
        new(L.F("core.mp.split.ports_busy_strict", SplitSettings.LegPort(0), SplitSettings.LegPort(split.Paths - 1)), inner);

    /// <summary>Releases the ports unless they were taken; lent listeners are released too (the session is ending).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _listeners, null) is { } listeners)
            foreach (var l in listeners) l.Stop();
    }

    internal IReadOnlyList<TcpListener> AllForTest => _all;
}
