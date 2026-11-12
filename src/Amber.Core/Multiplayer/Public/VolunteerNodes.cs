using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// Volunteer nodes: players who choose to let other Jay users connect to the public network through their computer.
/// A volunteer's Jay listens on a public TCP/UDP port and registers <c>tcp://address:port</c> with the directory
/// ("volunteer" op, renewed every few minutes). The directory only lists an address after it has connected to it itself,
/// and only addresses on the public internet (no private or loopback ranges), so the list can't be used to point Jay at
/// someone's LAN. Members ask for a handful ("nodes" op), add them as extra EasyTier peers and keep them for next time,
/// so the network keeps working when the official nodes are down.
/// <para>
/// Volunteers see the same public-network traffic any node sees: knocks and directory requests, never room traffic
/// (rooms have their own encrypted subnets). Their public address is shown to whoever asks the directory, which is why
/// it is strictly opt-in.
/// </para>
/// </summary>
public static class VolunteerNodes
{
    public const int DefaultPort = 11010;
    /// <summary>A volunteer re-registers this often.</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(3);
    /// <summary>Not re-registered for this long = gone.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    /// <summary>How many a member gets per "nodes" request, and how many it remembers per network.</summary>
    public const int PerRequest = 8;
    public const int Remember = 16;
    /// <summary>How many extra peers a member actually connects to.</summary>
    public const int ConnectTo = 3;

    /// <summary>
    /// Normalizes a volunteer address to <c>tcp://host:port</c>. Returns null for anything else, including literal
    /// private, loopback, link-local and other non-public IPs.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 300) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "tcp://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "tcp" || uri.Port is <= 0 or > 65535
            || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Host.Length == 0) return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.Host.ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (!IsPublic(ip)) return null;
        }
        else if (uri.HostNameType != UriHostNameType.Dns || !host.Contains('.') || host.EndsWith(".local", StringComparison.Ordinal)
                 || host.EndsWith(".lan", StringComparison.Ordinal) || host == "localhost") return null;
        return $"tcp://{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Is this an address on the public internet (not private, loopback, link-local, CGNAT, multicast, …)?</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || (b[0] == 100 && b[1] >= 64 && b[1] < 128)   // CGNAT
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                     || (b[0] == 198 && b[1] is 18 or 19)
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal
                     || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8));   // documentation
        }
        return false;
    }

    /// <summary>
    /// This computer's public IPv4 address as EasyTier's STUN check saw it, from <c>easytier-cli node -o json</c>
    /// (<c>stun_info.public_ip</c>, else <c>ip_list.public_ipv4</c>). Null when there is none (STUN blocked, or not
    /// checked yet). Behind carrier-grade NAT this is the carrier's address, which port forwarding can't reach.
    /// </summary>
    public static string? PublicAddressFromNodeInfo(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node) return null;
            var candidates = new List<string>();
            foreach (var ip in node["stun_info"]?["public_ip"] as JsonArray ?? [])
                if (ip?.GetValueKind() == JsonValueKind.String) candidates.Add(ip.GetValue<string>());
            if (node["ip_list"]?["public_ipv4"] is JsonObject v4 && v4["addr"]?.GetValueKind() == JsonValueKind.Number)
            {
                var n = v4["addr"]!.GetValue<uint>();
                candidates.Add(new IPAddress([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]).ToString());
            }
            else if (node["ip_list"]?["public_ipv4"]?.GetValueKind() == JsonValueKind.String) candidates.Add(node["ip_list"]!["public_ipv4"]!.GetValue<string>());
            return candidates.Select(c => IPAddress.TryParse(c.Trim(), out var ip) ? ip : null)
                .FirstOrDefault(ip => ip is { AddressFamily: AddressFamily.InterNetwork } && IsPublic(ip))?.ToString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException) { return null; }
    }

    /// <summary>
    /// Default reachability check the directory runs before listing a volunteer: resolve the host, refuse it if any
    /// address is not public, then open (and close) a TCP connection within 5 seconds.
    /// </summary>
    public static async Task<bool> ProbeAsync(string url, CancellationToken ct) =>
        await ResolveAsync(url, ct) is { } address && await ConnectAsync(address, new Uri(Normalize(url)!).Port, ct);

    /// <summary>
    /// The one address the directory will dial for <paramref name="url"/>: the host resolved once (within 5 seconds), or
    /// null when it doesn't resolve or any of its addresses is not public.
    /// </summary>
    public static async Task<IPAddress?> ResolveAsync(string url, CancellationToken ct)
    {
        if (Normalize(url) is not { } normalized) return null;
        var uri = new Uri(normalized);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, deadline.Token);
            return addresses.Length > 0 && addresses.All(IsPublic) ? addresses[0] : null;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ArgumentException) { return null; }
    }

    /// <summary>Opens (and closes) a TCP connection to an address already resolved, within 5 seconds.</summary>
    public static async Task<bool> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        if (!IsPublic(address)) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, deadline.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ArgumentException) { return false; }
    }

    /// <summary>The machine an address belongs to, for per-machine caps: the IPv4 address, or the /64 of an IPv6 one.</summary>
    public static string MachineOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}

/// <summary>
/// The directory's list of volunteer nodes. Entries come in through <see cref="Register"/>, are probed in the background
/// and only listed once the probe succeeded (re-probed every hour). Unrenewed entries drop out after
/// <see cref="VolunteerNodes.Ttl"/>.
/// </summary>
/// <param name="resolve">
/// Resolves an address to the one IP the directory dials, for the per-machine cap. Defaults to
/// <see cref="VolunteerNodes.ResolveAsync"/> when <paramref name="probe"/> is the default too; with a custom probe and no
/// resolver, only the host name counts.
/// </param>
public sealed class VolunteerRegistry(Func<string, CancellationToken, Task<bool>>? probe = null, Func<DateTimeOffset>? clock = null,
    Func<string, CancellationToken, Task<IPAddress?>>? resolve = null)
{
    public const int MaxEntries = 256;
    public static readonly TimeSpan ReprobeEvery = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public DateTimeOffset Seen;
        public DateTimeOffset? VerifiedAt;
        public bool Probing;
        /// <summary>The machine it resolved to (<see cref="VolunteerNodes.MachineOf"/>), once known.</summary>
        public string? Machine;
    }

    private readonly Func<string, CancellationToken, Task<bool>>? _customProbe = probe;
    private readonly Func<string, CancellationToken, Task<IPAddress?>>? _resolve = resolve ?? (probe is null ? VolunteerNodes.ResolveAsync : null);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Addresses the operator banned (checked on every register and list).</summary>
    public Func<string, bool>? Blocked { get; set; }

    /// <summary>Raised when an address got listed or failed its probe (for logs and counters).</summary>
    public event Action<string, bool>? Probed;

    /// <summary>The last probe started by <see cref="Register"/> (tests wait on it).</summary>
    public Task LastProbe { get; private set; } = Task.CompletedTask;

    /// <summary>Most reachability checks ("probe" op) per minute for the whole directory, and per address.</summary>
    public const int ChecksPerMinute = 30;
    public static readonly TimeSpan CheckSameAddressEvery = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTimeOffset> _checks = new();

    /// <summary>New addresses the whole directory takes per minute ("volunteer" op; renewals of known ones don't count).</summary>
    public const int RegistrationsPerMinute = 60;

    /// <summary>Entries one host may have, whatever the port: counted by name, and by the machine it resolves to.</summary>
    public const int MaxPerHost = 2;

    private readonly Queue<DateTimeOffset> _registrations = new();

    private static string HostOf(string normalized) => new Uri(normalized).Host;
    private readonly Dictionary<string, DateTimeOffset> _lastCheck = new(StringComparer.Ordinal);

    /// <summary>
    /// Handles a "probe" request: can the directory reach this address right now? Lets a would-be volunteer test its
    /// port forwarding before volunteering. Same rules as listing (public addresses only) and rate limited, so it can't
    /// be used as a port scanner. Returns <c>{ok, reachable}</c>, or error "bad" / "banned" / "rate".
    /// </summary>
    public async Task<JsonObject> CheckAsync(string? url, CancellationToken ct = default)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return Error("bad");
        if (Blocked?.Invoke(address) == true) return Error("banned");
        var now = _now();
        lock (_gate)
        {
            while (_checks.Count > 0 && now - _checks.Peek() > TimeSpan.FromMinutes(1)) _checks.Dequeue();
            foreach (var key in _lastCheck.Where(p => now - p.Value > CheckSameAddressEvery).Select(p => p.Key).ToList()) _lastCheck.Remove(key);
            if (_checks.Count >= ChecksPerMinute || _lastCheck.ContainsKey(address)) return Error("rate");
            _checks.Enqueue(now);
            _lastCheck[address] = now;
        }
        IPAddress? ip = null;
        if (_resolve is not null)
        {
            try { ip = await _resolve(address, ct); }
            catch (Exception e) when (e is not OutOfMemoryException) { ip = null; }
            if (ip is null) return new JsonObject { ["ok"] = true, ["reachable"] = false };
            // Also one check per machine at a time, so many names or ports of one machine can't be swept either.
            var machine = "machine:" + VolunteerNodes.MachineOf(ip);
            lock (_gate)
            {
                if (_lastCheck.ContainsKey(machine)) return Error("rate");
                _lastCheck[machine] = now;
            }
        }
        bool reachable;
        try { reachable = await Dial(address, ip, ct); }
        catch (Exception e) when (e is not OutOfMemoryException) { reachable = false; }
        return new JsonObject { ["ok"] = true, ["reachable"] = reachable };
    }

    /// <summary>Handles a "volunteer" request. Returns the reply: <c>{ok, listed}</c> or an error code.</summary>
    public JsonObject Register(string? url)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return Error("bad");
        if (Blocked?.Invoke(address) == true) return Error("banned");
        var now = _now();
        bool startProbe;
        bool listed;
        lock (_gate)
        {
            Prune(now);
            if (!_entries.TryGetValue(address, out var entry))
            {
                if (_entries.Count >= MaxEntries) return Error("full");
                // Each new address costs the directory an outgoing connection, so new addresses are rate limited for
                // the whole directory and capped per host: registering can't be used to make the directory dial
                // many ports of one machine, or many machines in a burst.
                while (_registrations.Count > 0 && now - _registrations.Peek() > TimeSpan.FromMinutes(1)) _registrations.Dequeue();
                if (_registrations.Count >= RegistrationsPerMinute) return Error("rate");
                var host = HostOf(address);
                if (_entries.Keys.Count(k => HostOf(k) == host) >= MaxPerHost)
                    return new JsonObject { ["ok"] = false, ["error"] = "host", ["reason"] = $"one host may register at most {MaxPerHost} addresses" };
                _registrations.Enqueue(now);
                _entries[address] = entry = new Entry();
            }
            entry.Seen = now;
            listed = entry.VerifiedAt is { } at && now - at < ReprobeEvery;
            startProbe = !listed && !entry.Probing;
            if (startProbe) entry.Probing = true;
        }
        if (startProbe) LastProbe = ProbeAsync(address);
        return new JsonObject { ["ok"] = true, ["listed"] = listed };
    }

    /// <summary>
    /// Dials the address: at the IP it already resolved to when there is one (it isn't looked up again, so a name can't
    /// point somewhere else between the check and the connection), or through the custom probe.
    /// </summary>
    private Task<bool> Dial(string address, IPAddress? ip, CancellationToken ct) =>
        _customProbe is not null ? _customProbe(address, ct)
        : ip is not null ? VolunteerNodes.ConnectAsync(ip, new Uri(address).Port, ct)
        : VolunteerNodes.ProbeAsync(address, ct);

    private async Task ProbeAsync(string address)
    {
        bool ok;
        IPAddress? ip = null;
        var crowded = false;
        if (_resolve is not null)
        {
            try { ip = await _resolve(address, CancellationToken.None); }
            catch (Exception e) when (e is not OutOfMemoryException) { ip = null; }
            if (ip is not null)
            {
                var machine = VolunteerNodes.MachineOf(ip);
                lock (_gate)
                {
                    // Many names for one machine count as that machine: over the cap, the entry goes without being dialled.
                    crowded = _entries.Count(p => p.Key != address && p.Value.Machine == machine) >= MaxPerHost;
                    if (!crowded && _entries.TryGetValue(address, out var mine)) mine.Machine = machine;
                }
            }
        }
        if (crowded || (_resolve is not null && ip is null)) ok = false;
        else
        {
            try { ok = await Dial(address, ip, CancellationToken.None); }
            catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
        }
        lock (_gate)
        {
            if (_entries.TryGetValue(address, out var entry))
            {
                entry.Probing = false;
                if (ok) entry.VerifiedAt = _now();
                else _entries.Remove(address);
            }
        }
        Probed?.Invoke(address, ok);
    }

    /// <summary>Up to <paramref name="count"/> verified, live volunteers in random order (spreads the load).</summary>
    public IReadOnlyList<string> Pick(int count = VolunteerNodes.PerRequest)
    {
        var listed = Listed();
        return [.. listed.OrderBy(_ => Random.Shared.Next()).Take(count)];
    }

    /// <summary>Every verified, live volunteer (admin API).</summary>
    public IReadOnlyList<string> Listed()
    {
        var now = _now();
        lock (_gate)
        {
            Prune(now);
            return [.. _entries.Where(p => p.Value.VerifiedAt is not null && Blocked?.Invoke(p.Key) != true).Select(p => p.Key).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>Drops an address now (it can register again unless it is also banned).</summary>
    public bool Remove(string url)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return false;
        lock (_gate) return _entries.Remove(address);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(p => now - p.Value.Seen > VolunteerNodes.Ttl && !p.Value.Probing).Select(p => p.Key).ToList())
            _entries.Remove(key);
    }

    private static JsonObject Error(string code) => new() { ["ok"] = false, ["error"] = code };
}

/// <summary>Volunteer nodes this Jay has seen, per network, so it can still get in next time when the official nodes are down.</summary>
public sealed class VolunteerCache(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public IReadOnlyList<string> For(string networkId)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return [.. (o?[networkId] as JsonArray ?? []).Select(n => n?.GetValue<string>()).Select(VolunteerNodes.Normalize).OfType<string>()
                .Distinct().Take(VolunteerNodes.Remember)];
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Puts <paramref name="fresh"/> first and keeps at most <see cref="VolunteerNodes.Remember"/>.</summary>
    public void Remember(string networkId, IEnumerable<string> fresh)
    {
        var list = fresh.Select(VolunteerNodes.Normalize).OfType<string>().Concat(For(networkId)).Distinct().Take(VolunteerNodes.Remember).ToList();
        try
        {
            JsonObject o;
            try { o = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : []; }
            catch (JsonException) { o = []; }
            o[networkId] = new JsonArray([.. list.Select(u => (JsonNode)u)]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, o.ToJsonString(Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Forget(string networkId, string url)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return;
        var rest = For(networkId).Where(u => u != address).ToList();
        try
        {
            var o = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
            o[networkId] = new JsonArray([.. rest.Select(u => (JsonNode)u)]);
            File.WriteAllText(path, o.ToJsonString(Options));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
}
