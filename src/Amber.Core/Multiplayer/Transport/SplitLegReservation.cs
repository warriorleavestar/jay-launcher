using System.Net;
using System.Net.Sockets;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// The host's leg ports (<see cref="SplitSettings.LegPort"/>), bound before the room's EasyTier instances start. The
/// instances only let room members reach the leg ports when this reservation exists, so a port that another program
/// already uses is never opened to the room. <see cref="SplitTunnelServer"/> takes the bound listeners over.
/// </summary>
public sealed class SplitLegReservation : IDisposable
{
    private TcpListener[]? _listeners;

    private SplitLegReservation(TcpListener[] listeners) => _listeners = listeners;

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

    /// <summary>The bound listeners, in leg order, once; null when they were already taken or released.</summary>
    internal TcpListener[]? TryTake() => Interlocked.Exchange(ref _listeners, null);

    /// <summary>A room with a Strict service needs its leg ports; without them it doesn't open.</summary>
    internal static bool NeedsLegs(SplitSettings split) =>
        split.For(TunnelService.Game) == SplitPolicy.Strict || split.For(TunnelService.Chat) == SplitPolicy.Strict;

    internal static IOException Busy(SplitSettings split, Exception? inner = null) =>
        new(L.F("core.mp.split.ports_busy_strict", SplitSettings.LegPort(0), SplitSettings.LegPort(split.Paths - 1)), inner);

    public void Dispose()
    {
        if (TryTake() is { } listeners)
            foreach (var l in listeners) l.Stop();
    }
}
