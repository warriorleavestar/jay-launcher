using Amber.Core.Localization;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Amber.Core.Multiplayer;

/// <summary>
/// 路由器上开的一个端口映射。Dispose 时删掉映射。
/// <paramref name="Renew"/> asks the router again for the same mapping (true when it still maps the same external port);
/// <paramref name="Lease"/> is how long the router keeps it without a renewal.
/// </summary>
public sealed record PortMappingResult(string Method, IPEndPoint External, Func<Task>? Remove,
    Func<CancellationToken, Task<bool>>? Renew = null, TimeSpan? Lease = null)
{
    /// <summary>The lease Jay asks for.</summary>
    public static readonly TimeSpan RequestedLease = TimeSpan.FromSeconds(LeaseSeconds);

    internal const int LeaseSeconds = 7200;
}

/// <summary>
/// 请路由器把一个外部 UDP 端口转到本机（UPnP IGD 和 NAT-PMP）。只改路由器上的临时映射（带租期），
/// 不改本机系统设置；结束联机时删掉。路由器关了这个功能、或者路由器自己也在运营商 NAT 后面时没用。
/// </summary>
public static partial class PortMapping
{
    /// <summary>
    /// What the router said before any mapping was asked for: where its UPnP description is, and the external address
    /// NAT-PMP reports. Finding these only waits for answers and changes nothing on the router, so it can run while STUN
    /// is still classifying the NAT; the mapping itself is asked for afterwards (<see cref="MapAsync"/>).
    /// </summary>
    public sealed record GatewayInfo(IPAddress? Gateway, IPAddress? UpnpLocalIp, Uri? UpnpLocation, IPAddress? PmpExternal)
    {
        public static readonly GatewayInfo None = new(null, null, null, null);
    }

    public static async Task<PortMappingResult?> TryMapAsync(int localPort, Action<string> log, CancellationToken ct = default) =>
        await MapAsync(await FindGatewayAsync(log, ct), localPort, log, ct);

    /// <summary>Looks for UPnP and NAT-PMP on the default gateway at the same time. Never asks for a mapping.</summary>
    public static async Task<GatewayInfo> FindGatewayAsync(Action<string> log, CancellationToken ct = default)
    {
        var gateway = DefaultGateway();
        if (gateway is null)
        {
            log(L.T("core.mp.natpmp.nogateway"));
            return GatewayInfo.None;
        }
        var upnp = FindUpnpAsync(gateway, log, ct);
        var pmp = FindNatPmpAsync(gateway, log, ct);
        try
        {
            await Task.WhenAll(upnp, pmp);
        }
        catch when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        var (localIp, location) = upnp.Result;
        return new GatewayInfo(gateway, localIp, location, pmp.Result);
    }

    /// <summary>Asks the router found by <see cref="FindGatewayAsync"/> for a mapping: UPnP first, then NAT-PMP.</summary>
    public static async Task<PortMappingResult?> MapAsync(GatewayInfo found, int localPort, Action<string> log, CancellationToken ct = default)
    {
        if (found is { Gateway: { } gateway, UpnpLocalIp: { } localIp, UpnpLocation: { } location })
        {
            try
            {
                if (await MapUpnpAsync(localPort, gateway, localIp, location, log, ct) is { } upnp) return upnp;
            }
            catch (Exception e) when (e is SocketException or HttpRequestException or IOException or InvalidDataException or System.Xml.XmlException or OperationCanceledException or FormatException)
            {
                if (ct.IsCancellationRequested) throw;
                log(L.T("core.mp.upnp.failed") + e.Message);
            }
        }
        if (found is { Gateway: { } pmpGateway, PmpExternal: { } external } && IsPublic(external))
        {
            try
            {
                if (await MapNatPmpAsync(localPort, pmpGateway, external, log, ct) is { } pmp) return pmp;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                log(L.T("core.mp.natpmp.failed") + e.Message);
            }
        }
        log(L.T("core.mp.pcp.skipped"));
        return null;
    }

    /// <summary>运营商级 NAT（100.64.0.0/10）和内网地址拿到映射也没用。</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6UniqueLocal && !IPAddress.IsLoopback(address);
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || b[0] >= 224);
    }

    // ---- UPnP IGD ----

    private static async Task<(IPAddress? LocalIp, Uri? Location)> FindUpnpAsync(IPAddress gateway, Action<string> log, CancellationToken ct)
    {
        if (!IsGatewayAddress(gateway)) return (null, null);
        try
        {
            var localIp = LocalAddressTowards(gateway);
            var location = await DiscoverAsync(gateway, localIp, ct);
            if (location is null)
            {
                log(L.T("core.mp.upnp.notfound"));
                return (null, null);
            }
            log(L.T("core.mp.upnp.gateway") + location.Host);
            return (localIp, location);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log(L.T("core.mp.upnp.failed") + e.Message);
            return (null, null);
        }
    }

    internal static async Task<PortMappingResult?> MapUpnpAsync(int localPort, IPAddress gateway, IPAddress localIp,
        Uri location, Action<string> log, CancellationToken ct, Func<HttpMessageHandler>? handlerFactory = null)
    {
        using var http = new GatewayHttp(gateway, localIp, location, handlerFactory?.Invoke());
        using var description = new HttpRequestMessage(HttpMethod.Get, location);
        var text = await http.SendAsync(description, ct);
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxUpnpResponseBytes,
        });
        var xml = XDocument.Load(reader);
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var service = xml.Descendants(ns + "service").FirstOrDefault(s =>
            (string?)s.Element(ns + "serviceType") is { } t && ServiceTypePattern().IsMatch(t));
        if (service is null)
        {
            log(L.T("core.mp.upnp.noservice"));
            return null;
        }
        var type = (string)service.Element(ns + "serviceType")!;
        if (!TryResolveControl(gateway, location, (string?)service.Element(ns + "controlURL"), out var control))
            throw new InvalidDataException(L.T("core.mp.upnp.unsafeaddress"));

        var external = ExtractTag(await SoapAsync(http, control!, type, "GetExternalIPAddress", "", ct), "NewExternalIPAddress");
        if (external is null || !IPAddress.TryParse(external, out var externalIp))
        {
            log(L.T("core.mp.upnp.noexternal"));
            return null;
        }
        log(L.F("core.mp.upnp.external", externalIp, IsPublic(externalIp) ? "" : L.T("core.mp.upnp.privateaddress")));
        if (!IsPublic(externalIp)) return null;

        var args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{localPort}</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                   $"<NewInternalPort>{localPort}</NewInternalPort><NewInternalClient>{localIp}</NewInternalClient><NewEnabled>1</NewEnabled>" +
                   $"<NewPortMappingDescription>Jay</NewPortMappingDescription><NewLeaseDuration>{PortMappingResult.LeaseSeconds}</NewLeaseDuration>";
        await SoapAsync(http, control!, type, "AddPortMapping", args, ct);
        log(L.F("core.mp.upnp.mapped", localPort, localIp, localPort));
        return new PortMappingResult("UPnP", new IPEndPoint(externalIp, localPort), async () =>
        {
            // 删除映射也走相同的目的地、代理和重定向限制。
            using var h = new GatewayHttp(gateway, localIp, location, handlerFactory?.Invoke(), TimeSpan.FromSeconds(3));
            await SoapAsync(h, control!, type, "DeletePortMapping",
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{localPort}</NewExternalPort><NewProtocol>UDP</NewProtocol>", CancellationToken.None);
        }, async renewCt =>
        {
            // Renewing is the same AddPortMapping again, under the same destination, proxy and redirect limits.
            try
            {
                using var h = new GatewayHttp(gateway, localIp, location, handlerFactory?.Invoke());
                await SoapAsync(h, control!, type, "AddPortMapping", args, renewCt);
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or IOException or InvalidDataException
                                          or OperationCanceledException && !renewCt.IsCancellationRequested)
            {
                return false;
            }
        }, PortMappingResult.RequestedLease);
    }

    private static async Task<Uri?> DiscoverAsync(IPAddress gateway, IPAddress localIp, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(localIp, 0));
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (TryDiscoveryLocation(gateway, r.RemoteEndPoint, Encoding.ASCII.GetString(r.Buffer), out var uri)) return uri;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?im)^location:[\t ]*([^\r\n]+)")]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"\Aurn:schemas-upnp-org:service:(WANIPConnection|WANPPPConnection):[1-9][0-9]*\z")]
    private static partial Regex ServiceTypePattern();

    internal const int MaxUpnpResponseBytes = 1024 * 1024;

    internal static bool TryDiscoveryLocation(IPAddress gateway, IPEndPoint responder, string response, out Uri? location)
    {
        location = null;
        if (!responder.Address.Equals(gateway) || !IsGatewayAddress(gateway)) return false;
        var matches = LocationPattern().Matches(response);
        if (matches.Count != 1) return false;
        var value = matches[0].Groups[1].Value.Trim(' ', '\t');
        if (!SafeUriText(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsGatewayUri(gateway, uri)) return false;
        location = uri;
        return true;
    }

    internal static bool TryResolveControl(IPAddress gateway, Uri location, string? value, out Uri? control)
    {
        control = null;
        if (!IsGatewayUri(gateway, location) || string.IsNullOrWhiteSpace(value) || !SafeUriText(value)
            || !Uri.TryCreate(location, value, out var uri) || !IsGatewayUri(gateway, uri)) return false;
        control = uri;
        return true;
    }

    private static bool IsGatewayAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is > 0 and < 224 && b[0] != 127 && !(b[0] == 169 && b[1] == 254);
    }

    private static bool SafeUriText(string value) =>
        !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains('#')
        && !Uri.UnescapeDataString(value).Any(char.IsControl);

    private static bool IsGatewayUri(IPAddress gateway, Uri uri) =>
        IsGatewayAddress(gateway) && uri.IsAbsoluteUri && (uri.Scheme is "http" or "https")
        && SafeUriText(uri.OriginalString) && uri.Port > 0 && uri.UserInfo.Length == 0
        && !uri.GetLeftPart(UriPartial.Authority).Contains('@') && uri.Fragment.Length == 0
        && uri.HostNameType == UriHostNameType.IPv4 && IPAddress.TryParse(uri.Host, out var address) && address.Equals(gateway);

    internal static SocketsHttpHandler CreateGatewayHandler(IPAddress gateway, IPAddress localIp) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            // 不做 DNS 查询；连接前再次核对目的地址，并绑定发现网关时使用的本机地址。
            if (!IsGatewayAddress(gateway) || !IPAddress.TryParse(context.DnsEndPoint.Host, out var target) || !target.Equals(gateway))
                throw new HttpRequestException(L.T("core.mp.upnp.unsafeaddress"));
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(localIp, 0));
                await socket.ConnectAsync(new IPEndPoint(gateway, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    internal sealed class GatewayHttp : IDisposable
    {
        private readonly IPAddress _gateway;
        private readonly HttpClient _http;
        private readonly TimeSpan _timeout;

        internal GatewayHttp(IPAddress gateway, IPAddress localIp, Uri location, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
        {
            if (!IsGatewayUri(gateway, location)) throw new InvalidDataException(L.T("core.mp.upnp.unsafeaddress"));
            _gateway = gateway;
            _timeout = timeout ?? TimeSpan.FromSeconds(5);
            _http = new HttpClient(new Amber.Core.Net.NetworkPolicyHandler(handler ?? CreateGatewayHandler(gateway, localIp), Amber.Core.Net.NetworkCategory.Multiplayer)) { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { } uri || !IsGatewayUri(_gateway, uri))
                throw new InvalidDataException(L.T("core.mp.upnp.unsafeaddress"));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_timeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException(L.T("core.mp.upnp.invalidresponse"));
            if (response.Content.Headers.ContentLength > MaxUpnpResponseBytes)
                throw new InvalidDataException(L.T("core.mp.upnp.invalidresponse"));
            await using var input = await response.Content.ReadAsStreamAsync(limit.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var n = await input.ReadAsync(buffer, limit.Token);
                if (n == 0) break;
                if (output.Length + n > MaxUpnpResponseBytes) throw new InvalidDataException(L.T("core.mp.upnp.invalidresponse"));
                output.Write(buffer, 0, n);
            }
            using var boundedContent = new ByteArrayContent(output.ToArray());
            boundedContent.Headers.ContentType = response.Content.Headers.ContentType;
            var text = await boundedContent.ReadAsStringAsync(limit.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(L.F("core.mp.upnp.rejected", request.Method, ExtractTag(text, "errorDescription") ?? response.StatusCode.ToString()));
            return text;
        }

        public void Dispose() => _http.Dispose();
    }

    private static async Task<string> SoapAsync(GatewayHttp http, Uri control, string serviceType, string action, string args, CancellationToken ct)
    {
        var body = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        return await http.SendAsync(request, ct);
    }

    internal static string? ExtractTag(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{tag}>([^<]*)</{tag}>");
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---- NAT-PMP（RFC 6886）----

    /// <summary>The external address NAT-PMP reports (RFC 6886, 3.2); asks for no mapping.</summary>
    private static async Task<IPAddress?> FindNatPmpAsync(IPAddress gateway, Action<string> log, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var address = await PmpRequestAsync(udp, new IPEndPoint(gateway, 5351), [0, 0], 12, ct);
            if (address is null)
            {
                log(L.T("core.mp.natpmp.noresponse"));
                return null;
            }
            var externalIp = new IPAddress(address.AsSpan(8, 4).ToArray());
            log(L.F("core.mp.natpmp.external", externalIp));
            return externalIp;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log(L.T("core.mp.natpmp.failed") + e.Message);
            return null;
        }
    }

    private static async Task<PortMappingResult?> MapNatPmpAsync(int localPort, IPAddress gateway, IPAddress externalIp, Action<string> log, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(gateway, 5351);
        var mapRequest = new byte[12];
        mapRequest[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(mapRequest.AsSpan(4), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(mapRequest.AsSpan(6), (ushort)localPort);
        BinaryPrimitives.WriteUInt32BigEndian(mapRequest.AsSpan(8), PortMappingResult.LeaseSeconds);
        var mapped = await PmpRequestAsync(udp, target, mapRequest, 16, ct);
        if (mapped is null || BinaryPrimitives.ReadUInt16BigEndian(mapped.AsSpan(2)) != 0) return null;
        var externalPort = BinaryPrimitives.ReadUInt16BigEndian(mapped.AsSpan(10));
        var lease = PmpLease(mapped);
        log(L.F("core.mp.natpmp.mapped", externalPort, localPort));
        return new PortMappingResult("NAT-PMP", new IPEndPoint(externalIp, externalPort), async () =>
        {
            using var u = new UdpClient(AddressFamily.InterNetwork);
            var delete = (byte[])mapRequest.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(delete.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt32BigEndian(delete.AsSpan(8), 0);
            await u.SendAsync(delete, target);
        }, async renewCt =>
        {
            // The same request again (RFC 6886, 3.3); it must still map the same external port.
            try
            {
                using var u = new UdpClient(AddressFamily.InterNetwork);
                var again = await PmpRequestAsync(u, target, mapRequest, 16, renewCt);
                return again is not null && BinaryPrimitives.ReadUInt16BigEndian(again.AsSpan(2)) == 0
                    && BinaryPrimitives.ReadUInt16BigEndian(again.AsSpan(10)) == externalPort;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException && !renewCt.IsCancellationRequested)
            {
                return false;
            }
        }, lease);
    }

    /// <summary>The lifetime a NAT-PMP mapping reply grants (bytes 12–15), kept between 20 s and the lease that was asked for.</summary>
    internal static TimeSpan PmpLease(ReadOnlySpan<byte> reply)
    {
        var seconds = reply.Length >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(reply[12..]) : 0;
        return TimeSpan.FromSeconds(seconds == 0 ? PortMappingResult.LeaseSeconds : Math.Clamp(seconds, 20, PortMappingResult.LeaseSeconds));
    }

    private static async Task<byte[]?> PmpRequestAsync(UdpClient udp, IPEndPoint target, byte[] request, int expected, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(request, target, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
            try
            {
                var r = await udp.ReceiveAsync(timeout.Token);
                if (r.Buffer.Length >= expected) return r.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    internal static IPAddress? DefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

    private static IPAddress LocalAddressTowards(IPAddress remote)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(remote, 9);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}
