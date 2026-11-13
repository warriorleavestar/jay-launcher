using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Multiplayer;

/// <summary>出口使用端交给提权守护的网络计划。只传地址（IPv4 和 IPv6），接口和网关由守护在改变路由前现场核对。</summary>
public sealed record ExitNetworkPlan(string VirtualIp, IReadOnlyList<string> UnderlayAddresses, string AdapterName = "Amber LAN")
{
    public const int MaxUnderlayAddresses = 128;

    public static ExitNetworkPlan? Create(EasyTierOptions options)
    {
        if (options.ExitNode is null) return null;
        if (!options.Tun) throw new ArgumentException(L.T("core.mp.component.exitrequireslan"));
        var ownIp = EasyTierArgs.VirtualIp(options);
        if (!IPAddress.TryParse(options.ExitNode, out var exit) || exit.AddressFamily != AddressFamily.InterNetwork
            || !exit.ToString().StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal)
            || exit.GetAddressBytes()[3] is 0 or 255 || exit.ToString() == ownIp)
            throw new ArgumentException(L.T("core.mp.exit.notmember"));

        var loopbackBridge = options.Peers.Concat(options.Listeners ?? []).Any(IsLoopback);
        if (loopbackBridge && options.Peers.Any(p => !IsLoopback(p)))
            throw new InvalidOperationException(L.T("core.mp.exit.mixedpeers"));

        if (options.DirectUnderlayAddresses.Count > MaxUnderlayAddresses)
            throw new ArgumentException(L.T("core.mp.exit.toomany"));
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var nodeAddresses = new List<string>();
        if (options.Peers.Count > MaxUnderlayAddresses) throw new ArgumentException(L.T("core.mp.exit.toomany"));
        // 全局门禁不能只信任进程的网卡绑定；普通节点也须有精确的物理承载地址。
        foreach (var peer in options.Peers.Where(p => !IsLoopback(p)))
        {
            if (!Uri.TryCreate(peer, UriKind.Absolute, out var uri)) throw new InvalidOperationException(L.T("core.mp.exit.relay_unreadable"));
            // A relay named by domain: the addresses ExitRelays resolved when the exit was chosen.
            if (uri.HostNameType == UriHostNameType.Dns)
            {
                if (!options.RelayAddresses.TryGetValue(uri.Host, out var resolved) || resolved.Count == 0)
                    throw new InvalidOperationException(L.T("core.mp.exit.relay_unreadable"));
                nodeAddresses.AddRange(resolved);
                continue;
            }
            var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
            if (!IPAddress.TryParse(host, out var address)) throw new InvalidOperationException(L.T("core.mp.exit.relay_unreadable"));
            nodeAddresses.Add(Normalize(address).ToString());
        }
        foreach (var value in options.DirectUnderlayAddresses.Concat(nodeAddresses))
        {
            if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                throw new ArgumentException(L.T("core.mp.exit.underlay_address"));
            var ip = Normalize(parsed);
            if (IPAddress.IsLoopback(ip)) continue;
            if (!IsSafeUnderlay(ip)) throw new ArgumentException(L.T("core.mp.exit.underlayunsafe"));
            addresses.Add(ip.ToString());
        }
        if (addresses.Count > MaxUnderlayAddresses) throw new ArgumentException(L.T("core.mp.exit.toomany"));
        // 只返回已明确识别的承载；未列入的物理出口由会话门禁阻止，不能猜测网关或放行整个进程。
        return new ExitNetworkPlan(ownIp, addresses.Order(StringComparer.Ordinal).ToArray(), EasyTierArgs.DeviceName(options));
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>
    /// An address the connection itself may use while everything else goes through the member: a routable IPv4 or IPv6
    /// address of the physical network. Unspecified, broadcast, multicast and room addresses are refused, and so are IPv6
    /// link-local addresses (they need an interface scope the route and traffic gate can't carry). Loopback is handled by
    /// the caller. Windows (ExitRoutingScript, ExitTrafficPolicyScript) and Unix (UnixExitNetwork) check the same rules.
    /// </summary>
    internal static bool IsSafeUnderlay(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] is not 0 and < 224 && !ip.Equals(IPAddress.Broadcast)
                && !ip.ToString().StartsWith(EasyTierArgs.Subnet, StringComparison.Ordinal);
        }
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 && !ip.IsIPv4MappedToIPv6
            && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip)
            && !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast;
    }

    private static bool IsLoopback(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback;
}
