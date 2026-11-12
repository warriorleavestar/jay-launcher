using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Identity;

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
    public static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        if (Normalize(url) is not { } normalized) return false;
        var uri = new Uri(normalized);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, deadline.Token);
            if (addresses.Length == 0 || !addresses.All(IsPublic)) return false;
            using var client = new TcpClient(addresses[0].AddressFamily);
            await client.ConnectAsync(addresses[0], uri.Port, deadline.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ArgumentException) { return false; }
    }
}

/// <summary>
/// Who runs a volunteer node. A volunteer whose Minecraft account has a Jay account bound signs each registration with
/// its device keys and sends the certificate its Jay account signed for the device. The directory checks both and
/// remembers the account as the node's operator, so it can hand out nodes from different people (privacy plan stage 9
/// wants entry and exit run by different operators) and the network's operator can ban a person rather than one address.
/// The account address never leaves the directory: members only get a tag that tells, within one reply, whether two
/// nodes have the same operator.
/// </summary>
public static class VolunteerOperator
{
    /// <summary>A registration's time may be this far from the directory's clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(3);
    private static ReadOnlySpan<byte> Domain => "jay-volunteer-v2\n"u8;

    /// <summary>
    /// The bytes signed: the network id, a newline, the normalized address, a newline and the time in Unix seconds
    /// (big-endian). The network id keeps a registration for one network from being used on another.
    /// </summary>
    private static byte[] Message(string network, string address, long at)
    {
        var text = Encoding.UTF8.GetBytes(network + "\n" + address + "\n");
        var message = new byte[text.Length + 8];
        text.CopyTo(message, 0);
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(text.Length), at);
        return message;
    }

    /// <summary>
    /// The "operator" part of a volunteer registration for <paramref name="address"/> (already normalized) on the network
    /// <paramref name="network"/> (<see cref="PublicNetworkProfile.Id"/>).
    /// </summary>
    public static JsonObject Sign(JayProof proof, string network, string address, DateTimeOffset now)
    {
        var at = now.ToUnixTimeSeconds();
        return new JsonObject
        {
            ["cert"] = proof.Certificate.ToJson(),
            ["at"] = at,
            ["sig"] = Convert.ToBase64String(proof.Keys.Sign(Domain, Message(network, address, at)).Encode()),
        };
    }

    /// <summary>
    /// The Jay account that signed for this registration, with the time it signed at, or null when the signature,
    /// certificate, network or time doesn't check out.
    /// </summary>
    public static (string Account, long At)? Verify(JsonNode? node, string network, string address, DateTimeOffset now)
    {
        try
        {
            if (node is not JsonObject o || DeviceCertificate.FromJson(o["cert"]) is not { } cert || !cert.Verify(cert.DeviceKey)) return null;
            if (o["at"]?.GetValue<long>() is not { } at || Math.Abs(at - now.ToUnixTimeSeconds()) > (long)MaxClockSkew.TotalSeconds) return null;
            var sig = HybridSignature.Decode(Convert.FromBase64String(o["sig"]?.GetValue<string>() ?? ""));
            return HybridSignature.Verify(cert.DeviceEd, cert.DeviceMlDsa, Domain, Message(network, address, at), sig) ? (cert.Account, at) : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { return null; }
    }
}

/// <summary>A volunteer node from the directory, with a tag for its operator ("" when it didn't say who runs it).</summary>
/// <remarks>Tags only compare between nodes from the same directory reply; <see cref="RoomDirectoryClient.VolunteerNodesAsync"/> scopes them.</remarks>
public sealed record VolunteerNode(string Url, string Operator)
{
    /// <summary>
    /// Nodes in order, taking one per operator first (unknown operators each count as their own), then the rest. This
    /// spreads load and avoids the obvious "both ends run by the same person"; it is not a security property, because
    /// one person can hold any number of Jay accounts.
    /// </summary>
    public static IReadOnlyList<VolunteerNode> SpreadOperators(IEnumerable<VolunteerNode> nodes)
    {
        var list = nodes.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = new List<VolunteerNode>();
        var rest = new List<VolunteerNode>();
        foreach (var node in list)
            (node.Operator.Length == 0 || seen.Add(node.Operator) ? first : rest).Add(node);
        return [.. first, .. rest];
    }
}

/// <summary>
/// The directory's list of volunteer nodes. Entries come in through <see cref="Register"/>, are probed in the background
/// and only listed once the probe succeeded (re-probed every hour). Unrenewed entries drop out after
/// <see cref="VolunteerNodes.Ttl"/>.
/// </summary>
public sealed class VolunteerRegistry(Func<string, CancellationToken, Task<bool>>? probe = null, Func<DateTimeOffset>? clock = null)
{
    public const int MaxEntries = 256;
    public static readonly TimeSpan ReprobeEvery = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        /// <summary>The Jay account that runs it (from a signed registration), or null.</summary>
        public string? Operator;
        /// <summary>Someone else (or nobody) tried to register it under another operator: shown as unsigned until it lapses.</summary>
        public bool Disputed;
        public DateTimeOffset Seen;
        public DateTimeOffset? VerifiedAt;
        public bool Probing;
    }

    private readonly Func<string, CancellationToken, Task<bool>> _probe = probe ?? VolunteerNodes.ProbeAsync;
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Addresses the operator banned (checked on every register and list).</summary>
    public Func<string, bool>? Blocked { get; set; }

    /// <summary>Jay accounts the network's operator banned from volunteering (checked on every register and list).</summary>
    public Func<string, bool>? BlockedOperator { get; set; }

    /// <summary>Only list volunteers that signed their registration with a Jay account (error "operator" otherwise).</summary>
    public bool RequireOperator { get; set; }

    /// <summary>The network this directory serves (<see cref="PublicNetworkProfile.Id"/>); signed registrations must name it.</summary>
    public string NetworkId { get; init; } = "";

    // The last signing time accepted per (account, address): a signed registration is good once, and only newer ones count.
    private readonly Dictionary<string, long> _lastSigned = new(StringComparer.Ordinal);

    // Operator tags are keyed per directory run, so they can't be matched across directories or restarts.
    private readonly byte[] _tagKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Raised when an address got listed or failed its probe (for logs and counters).</summary>
    public event Action<string, bool>? Probed;

    /// <summary>The last probe started by <see cref="Register"/> (tests wait on it).</summary>
    public Task LastProbe { get; private set; } = Task.CompletedTask;

    /// <summary>Most reachability checks ("probe" op) per minute for the whole directory, and per address.</summary>
    public const int ChecksPerMinute = 30;
    public static readonly TimeSpan CheckSameAddressEvery = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTimeOffset> _checks = new();
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
        bool reachable;
        try { reachable = await _probe(address, ct); }
        catch (Exception e) when (e is not OutOfMemoryException) { reachable = false; }
        return new JsonObject { ["ok"] = true, ["reachable"] = reachable };
    }

    /// <summary>
    /// Handles a "volunteer" request. <paramref name="signedBy"/> is the request's "operator" part
    /// (<see cref="VolunteerOperator.Sign"/>), if any. Returns the reply: <c>{ok, listed}</c> or an error code:
    /// "operator" when the signature doesn't check out, was used before (or is missing and required), "owner" when the
    /// address is registered under another operator (another account, or none) that hasn't let it lapse.
    /// </summary>
    public JsonObject Register(string? url, JsonNode? signedBy = null)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return Error("bad");
        if (Blocked?.Invoke(address) == true) return Error("banned");
        var now = _now();
        string? account = null;
        long signedAt = 0;
        if (signedBy is not null)
        {
            if (VolunteerOperator.Verify(signedBy, NetworkId, address, now) is not { } signed) return Error("operator");
            (account, signedAt) = signed;
        }
        if (account is null && RequireOperator) return Error("operator");
        if (account is not null && BlockedOperator?.Invoke(account) == true) return Error("banned");
        bool startProbe;
        bool listed;
        lock (_gate)
        {
            Prune(now);
            if (account is not null)
            {
                var key = account + "\n" + address;
                if (_lastSigned.TryGetValue(key, out var last) && signedAt <= last) return Error("operator");
                _lastSigned[key] = signedAt;
            }
            if (!_entries.TryGetValue(address, out var entry))
            {
                if (_entries.Count >= MaxEntries) return Error("full");
                _entries[address] = entry = new Entry { Operator = account };
            }
            // An address keeps its operator (or having none) until it lapses: nobody can take it over, add a signature
            // to it or strip one. A conflicting claim can't be settled here (whoever registered first needn't be who runs
            // the node), so the entry stops saying who runs it until it lapses.
            else if (entry.Operator != account)
            {
                entry.Disputed = true;
                return Error("owner");
            }
            entry.Seen = now;
            listed = entry.VerifiedAt is { } at && now - at < ReprobeEvery;
            startProbe = !listed && !entry.Probing;
            if (startProbe) entry.Probing = true;
        }
        if (startProbe) LastProbe = ProbeAsync(address);
        return new JsonObject { ["ok"] = true, ["listed"] = listed };
    }

    private async Task ProbeAsync(string address)
    {
        bool ok;
        try { ok = await _probe(address, CancellationToken.None); }
        catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
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

    /// <summary>
    /// Up to <paramref name="count"/> verified, live volunteers in random order (spreads the load), at most one per
    /// operator while there are others to give. Each comes with its operator tag ("" when unsigned).
    /// </summary>
    public IReadOnlyList<VolunteerNode> Pick(int count = VolunteerNodes.PerRequest)
    {
        var shuffled = Live().OrderBy(_ => Random.Shared.Next()).Select(p => new VolunteerNode(p.Url, Tag(p.Operator)));
        return [.. VolunteerNode.SpreadOperators(shuffled).Take(count)];
    }

    /// <summary>Every verified, live volunteer (admin API).</summary>
    public IReadOnlyList<string> Listed() => [.. Live().Select(p => p.Url)];

    /// <summary>Every verified, live volunteer with the Jay account that runs it, if it said (admin API).</summary>
    public IReadOnlyList<(string Url, string? Operator)> Operators() => Live();

    private List<(string Url, string? Operator)> Live()
    {
        var now = _now();
        lock (_gate)
        {
            Prune(now);
            return [.. _entries.Where(p => p.Value.VerifiedAt is not null && Blocked?.Invoke(p.Key) != true
                    && (p.Value.Operator is not { } op || BlockedOperator?.Invoke(op) != true))
                .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Disputed ? null : p.Value.Operator))];
        }
    }

    private string Tag(string? account) =>
        account is null ? "" : Convert.ToHexStringLower(HMACSHA256.HashData(_tagKey, Encoding.UTF8.GetBytes(account)))[..16];

    /// <summary>Drops an address now (it can register again unless it is also banned).</summary>
    public bool Remove(string url)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return false;
        lock (_gate) return _entries.Remove(address);
    }

    /// <summary>Drops every node this Jay account runs. Returns how many.</summary>
    public int RemoveOperator(string account)
    {
        lock (_gate)
        {
            var keys = _entries.Where(p => p.Value.Operator == account).Select(p => p.Key).ToList();
            foreach (var key in keys) _entries.Remove(key);
            return keys.Count;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = (now - VolunteerOperator.MaxClockSkew).ToUnixTimeSeconds();
        foreach (var key in _lastSigned.Where(p => p.Value < oldest).Select(p => p.Key).ToList()) _lastSigned.Remove(key);
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
