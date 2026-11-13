using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Listening on a network adapter's address that may not be there yet. A whole-LAN room's virtual adapter gets its
/// address a moment after EasyTier starts (in the background, after the room is set up), so a listener on that address
/// waits for it instead of failing.
/// </summary>
public static class AdapterListener
{
    /// <summary>
    /// How long a listener waits for its adapter address by default. The view model gives the elevated helper the same
    /// time to report ready, so neither gives up before the other (a first adapter install can be slow).
    /// <c>multiplayer.room.full_lan_not_ready</c> says "two minutes" in every language: change it with this value.
    /// </summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(120);

    internal static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>Creates and starts a listener (the default way to bind).</summary>
    public static TcpListener Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        return listener;
    }

    /// <summary>The address isn't on any adapter (yet).</summary>
    public static bool IsAddressMissing(SocketException e, IPAddress address) =>
        e.SocketErrorCode == SocketError.AddressNotAvailable && !IPAddress.IsLoopback(address);

    /// <summary>
    /// Binds with <paramref name="listen"/>, trying again every <see cref="RetryEvery"/> while the address is missing,
    /// for up to <paramref name="wait"/>. Throws the last <see cref="SocketException"/> when it gives up or the bind
    /// fails for another reason, and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static async Task<TcpListener> BindAsync(IPAddress address, int port, TimeSpan wait, CancellationToken ct,
        Func<IPAddress, int, TcpListener>? listen = null)
    {
        listen ??= Listen;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return listen(address, port); }
            catch (SocketException e) when (IsAddressMissing(e, address) && clock.Elapsed < wait) { }
            await Task.Delay(RetryEvery, ct);
        }
    }
}
