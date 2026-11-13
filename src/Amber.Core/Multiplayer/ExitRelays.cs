using System.Net;
using System.Net.Sockets;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Going online through a member sends everything except the relay underlay into the room's adapter, so the route plan
/// must know each relay's address. Relays are usually named by domain (codes never carry an IP), so they are resolved
/// here, once, when the exit is chosen:
/// <list type="bullet">
/// <item>TCP, UDP, QUIC and WireGuard relays are pinned: EasyTier is handed the resolved address itself, so it dials
/// exactly the address the plan protects.</item>
/// <item>WebSocket relays keep their name (TLS and the HTTP host need it); every address it resolved to is protected.
/// If the name later resolves elsewhere, the exit's traffic gate blocks that address rather than letting it out.</item>
/// </list>
/// Leaving the exit restores the configured peers.
/// </summary>
public static class ExitRelays
{
    public static async Task<EasyTierOptions> PinAsync(EasyTierOptions options,
        Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null, CancellationToken ct = default)
    {
        var configured = options.ConfiguredPeers ?? options.Peers;
        if (options.ExitNode is null)
            return options with { Peers = configured, ConfiguredPeers = null, RelayAddresses = new Dictionary<string, IReadOnlyList<string>>() };
        lookup ??= (host, token) => Dns.GetHostAddressesAsync(host, token);
        var peers = new List<string>(configured.Count);
        var resolved = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in configured)
        {
            if (!Uri.TryCreate(peer.Trim(), UriKind.Absolute, out var uri) || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns)
            {
                peers.Add(peer);
                continue;
            }
            IPAddress[] found;
            try { found = await lookup(uri.Host, ct); }
            catch (SocketException e) { throw new InvalidOperationException(L.F("core.mp.exit.relay_unresolved", uri.Host, e.Message), e); }
            var v4 = found.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (v4.Length == 0) throw new InvalidOperationException(L.F("core.mp.exit.relay_unresolved", uri.Host, L.T("core.mp.exit.relay_no_ipv4")));
            if (uri.Scheme is "ws" or "wss")
            {
                resolved[uri.Host] = v4;
                peers.Add(peer);
            }
            else peers.Add(Pin(uri, v4[0]));
        }
        return options with { Peers = peers, ConfiguredPeers = configured, RelayAddresses = resolved };
    }

    /// <summary>The same relay at <paramref name="address"/> instead of its name.</summary>
    internal static string Pin(Uri uri, string address)
    {
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rest = uri.PathAndQuery == "/" ? "" : uri.PathAndQuery;
        return $"{uri.Scheme}://{address}{port}{rest}";
    }
}
