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
    public static string? Normalize(string? url) => Normalize(url, IsPublic);

    /// <param name="isPublic">Which IP literals count as public (tests pass their own, so they need no real addresses).</param>
    internal static string? Normalize(string? url, Func<IPAddress, bool> isPublic)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 300) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "tcp://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "tcp" || uri.Port is <= 0 or > 65535
            || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Host.Length == 0) return null;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.Host.ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (!isPublic(ip)) return null;
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
    public static string? PublicAddressFromNodeInfo(string json) => PublicAddressFromNodeInfo(json, IsPublic);

    internal static string? PublicAddressFromNodeInfo(string json, Func<IPAddress, bool> isPublic)
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
                .FirstOrDefault(ip => ip is { AddressFamily: AddressFamily.InterNetwork } && isPublic(ip))?.ToString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException) { return null; }
    }

    /// <summary>
    /// Default reachability check the directory runs before listing a volunteer: resolve the host, refuse it if any
    /// address is not public, then open (and close) a TCP connection, all within <see cref="CheckTimeout"/>.
    /// </summary>
    public static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(CheckTimeout);
        return await ResolveAsync(url, total.Token) is { } address && await ConnectAsync(address, new Uri(Normalize(url)!).Port, total.Token);
    }

    /// <summary>
    /// Lookup and connection together, for one check: short enough that a "probe" reply still fits the request's
    /// deadline after a slow handshake (<see cref="RoomDirectoryProtocol.HandshakeTimeout"/> of the 10 s exchange).
    /// </summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The one address the directory will dial for <paramref name="url"/>: the host resolved once (within 5 seconds, less when <paramref name="ct"/> says so), or
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

    /// <summary>Opens (and closes) a TCP connection to an address already resolved, within 5 seconds (less when <paramref name="ct"/> says so).</summary>
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

    /// <summary>
    /// Is a node of network <paramref name="networkName"/> listening at <paramref name="address"/>:<paramref name="port"/>?
    /// Opens a TCP connection, sends the opening of an EasyTier peer handshake (network name only, no secret) and waits
    /// for the node's handshake back under that network's name, then closes; within 5 seconds (less when
    /// <paramref name="ct"/> says so). Any other service at that port (a web or game server, say) isn't one.
    /// </summary>
    public static Task<bool> IsNetworkNodeAsync(IPAddress address, int port, string networkName, CancellationToken ct) =>
        IsPublic(address) ? AnswersAsNodeAsync(address, port, networkName, ct) : Task.FromResult(false);

    /// <summary><see cref="IsNetworkNodeAsync"/> for any address (tests dial this machine).</summary>
    internal static async Task<bool> AnswersAsNodeAsync(IPAddress address, int port, string networkName, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(NodeHandshake.Request(networkName, RandomNumberGenerator.GetInt32(1, int.MaxValue)), deadline.Token);
            var length = new byte[4];
            await stream.ReadExactlyAsync(length, deadline.Token);
            var body = BinaryPrimitives.ReadUInt32LittleEndian(length);
            if (body is < NodeHandshake.HeaderSize or > NodeHandshake.MaxReply) return false;
            var reply = new byte[body];
            await stream.ReadExactlyAsync(reply, deadline.Token);
            return NodeHandshake.IsReply(reply, networkName);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ArgumentException or EndOfStreamException)
        {
            return false;
        }
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
/// Who runs a volunteer node. A volunteer whose Minecraft account has a Jay account bound signs each registration with
/// its device keys and sends the certificate its Jay account signed for the device. The directory checks both and
/// remembers the account as the node's operator, so it can hand out nodes from different people (privacy plan stage 9
/// wants entry and exit run by different operators) and the network's operator can ban a person rather than one address.
/// The account address never leaves the directory: members only get the addresses, already spread over operators.
/// </summary>
public static class VolunteerOperator
{
    /// <summary>A registration's time may be this far behind the directory's clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(3);
    /// <summary>...and this far ahead of it.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromSeconds(60);
    private static ReadOnlySpan<byte> Domain => "jay-volunteer-v3\n"u8;

    /// <summary>
    /// What a registration names its network by (<see cref="Sign"/>): the network's id plus a hash of its EasyTier network
    /// name and secret. Ids of self-made networks are picked by their makers and can repeat; two networks with the same
    /// name and secret are one network. Only members and the directory know the secret, so the tag says nothing new.
    /// </summary>
    public static string NetworkTag(PublicNetworkProfile? profile)
    {
        if (profile is null) return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("jay-volunteer-net-v1\n" + profile.NetworkName + "\n" + profile.Secret));
        return profile.Id + ":" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The bytes signed: the network tag (<see cref="NetworkTag"/>), a newline, the normalized address, a newline and the
    /// time in Unix seconds (big-endian). The tag keeps a registration for one network from being used on another, even
    /// one that took the same id.
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
    /// <paramref name="network"/> (<see cref="NetworkTag"/>).
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
            // The time first (cheap, and compared without overflow), then the certificate and signature.
            if (node is not JsonObject o || o["at"]?.GetValue<long>() is not { } at) return null;
            var n = now.ToUnixTimeSeconds();
            if (at < n - (long)MaxClockSkew.TotalSeconds || at > n + (long)MaxClockAhead.TotalSeconds) return null;
            if (DeviceCertificate.FromJson(o["cert"]) is not { } cert || !cert.Verify(cert.DeviceKey)) return null;
            // The account is derived from the certificate's account keys (DeviceCertificate.Account encodes their id), so
            // it is always the canonical address bans and limits compare against; there's no other spelling to send.
            var sig = HybridSignature.Decode(Convert.FromBase64String(o["sig"]?.GetValue<string>() ?? ""));
            return HybridSignature.Verify(cert.DeviceEd, cert.DeviceMlDsa, Domain, Message(network, address, at), sig) ? (cert.Account, at) : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or CryptographicException) { return null; }
    }
}

/// <summary>A volunteer node and its operator (a Jay account, or "" when unknown or disputed), as the directory orders them.</summary>
/// <remarks>Operators stay in the directory: members only get the addresses, already spread over operators.</remarks>
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
/// A listed volunteer as the network's operator sees it: the Jay account it was registered under (null: unsigned), the
/// other Jay accounts that claimed it with a signed registration since (the few seen most recently, in the order they
/// first claimed), and how many unsigned registrations of it were turned away while it was signed.
/// </summary>
/// <param name="Claims">Signed claims of it so far (renewals included), and <paramref name="Claimants"/> how many different
/// accounts made them (counted up to <see cref="VolunteerRegistry.MaxClaimantsCounted"/>): many accounts suggest a flood.</param>
public sealed record VolunteerEntry(string Url, string? Operator, IReadOnlyList<string>? ClaimedBy = null, int UnsignedAttempts = 0,
    DateTimeOffset? LastUnsigned = null, int Claims = 0, int Claimants = 0)
{
    /// <summary>Someone claimed the address under another operator.</summary>
    public bool Disputed => ClaimedBy is { Count: > 0 };
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
        /// <summary>The Jay account that runs it (from a signed registration), or null.</summary>
        public string? Operator;
        /// <summary>The signing time of the last accepted signed registration (Unix seconds).</summary>
        public long SignedAt;
        /// <summary>
        /// Other Jay accounts that registered it with a signature, and when each last did. At most <see cref="MaxClaims"/>:
        /// a new claimant replaces the one not seen for longest, so someone who keeps renewing (the node's real runner)
        /// can't be kept out by a few one-off claims. The operator stays; the claims are for the network's operator to
        /// look at, and a disputed node doesn't count as anyone's when spreading.
        /// </summary>
        public List<(string Account, DateTimeOffset First, DateTimeOffset Seen)>? ClaimedBy;
        /// <summary>Signed claims so far, and the different accounts behind them (the set stops growing at a cap).</summary>
        public int Claims;
        public HashSet<string>? Claimants;
        /// <summary>Unsigned registrations turned away while the address was signed (not claims; see <see cref="Register"/>).</summary>
        public int UnsignedAttempts;
        public DateTimeOffset? LastUnsigned;
        public DateTimeOffset Seen;
        public DateTimeOffset? VerifiedAt;
        public bool Probing;
        /// <summary>The machine it resolved to (<see cref="VolunteerNodes.MachineOf"/>), once known.</summary>
        public string? Machine;
        /// <summary>The address it resolved to, once known: with the port, the listener it names.</summary>
        public string? Ip;
    }

    private readonly Func<string, CancellationToken, Task<bool>>? _customProbe = probe;
    private readonly Func<string, CancellationToken, Task<IPAddress?>>? _resolve = resolve ?? (probe is null ? VolunteerNodes.ResolveAsync : null);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Addresses the operator banned (checked on every register and list).</summary>
    public Func<string, bool>? Blocked { get; set; }

    /// <summary>Jay accounts the network's operator banned from volunteering (checked on every register and list).</summary>
    public Func<string, bool>? BlockedOperator { get; set; }

    /// <summary>Only list volunteers that signed their registration with a Jay account (error "operator" otherwise).</summary>
    public bool RequireOperator { get; set; }

    /// <summary>The network this directory serves (<see cref="VolunteerOperator.NetworkTag"/>); signed registrations must name it.</summary>
    public string NetworkId { get; init; } = "";

    /// <summary>
    /// The EasyTier network name of the network. When set (and no custom probe is given), an address is listed only once a
    /// node of this network answers there (<see cref="VolunteerNodes.IsNetworkNodeAsync"/>), not just any open port.
    /// </summary>
    public string? NetworkName { get; init; }

    /// <summary>Conflicting claims kept per address.</summary>
    public const int MaxClaims = 4;
    /// <summary>Different claimant accounts counted per address.</summary>
    public const int MaxClaimantsCounted = 256;

    // A signed registration is good once and only newer ones count: the last accepted signing time lives on the entry.
    // When an entry goes away its time stays a little while (until it falls out of the clock window) so the old
    // registration can't bring it back; that list is bounded and drops its oldest when full, never refusing anyone.
    private readonly Dictionary<string, long> _tombstones = new(StringComparer.Ordinal);
    /// <summary>
    /// Tombstones of addresses that were listed (passed a probe) when they went away: only their volunteer comes back
    /// without counting as new. One that failed its probe keeps just its signing time (against replays).
    /// </summary>
    private readonly HashSet<string> _mayReturn = new(StringComparer.Ordinal);
    /// <summary>
    /// Addresses whose probe just failed, and when: such an address isn't taken (and dialed) again until
    /// <see cref="CheckSameAddressEvery"/> has passed, whoever registers it.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastDialed = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, long At)> _tombstoneOrder = new();
    /// <summary>Signing times kept for addresses that went away.</summary>
    public const int MaxTombstones = 2 * MaxEntries;
    internal int TombstoneCount { get { lock (_gate) return _tombstones.Count; } }

    /// <summary>
    /// New addresses taken per minute (each one is dialed to probe it), counted apart for unsigned and signed
    /// registrations so an unsigned flood can't keep signed volunteers out. Renewals of listed addresses don't count, so a
    /// flood of new registrations can't push out the volunteers already on the list, and neither does a volunteer coming
    /// back to an address it signed for a moment ago.
    /// </summary>
    public const int NewPerMinute = 60;
    /// <summary>New addresses per minute for one Jay account.</summary>
    public const int NewPerAccountPerMinute = 8;
    /// <summary>New addresses per minute from one caller address (callers seen as loopback, behind a forward, aren't told apart).</summary>
    public const int NewPerSourcePerMinute = 10;
    private readonly Queue<DateTimeOffset> _newUnsigned = new();
    private readonly Queue<DateTimeOffset> _newSigned = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _newBy = new(StringComparer.Ordinal);

    /// <summary>Raised when an address got listed or failed its probe (for logs and counters).</summary>
    public event Action<string, bool>? Probed;

    /// <summary>Raised the first time an address gets a conflicting claim (for logs).</summary>
    public event Action<string>? Disputed;

    /// <summary>The last probe started by <see cref="Register"/> (tests wait on it).</summary>
    public Task LastProbe { get; private set; } = Task.CompletedTask;

    /// <summary>Most reachability checks ("probe" op) per minute for the whole directory, and per address.</summary>
    public const int ChecksPerMinute = 30;
    public static readonly TimeSpan CheckSameAddressEvery = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTimeOffset> _checks = new();

    /// <summary>Entries one host may have, whatever the port: counted by name, and by the machine it resolves to.</summary>
    public const int MaxPerHost = 2;

    /// <summary>
    /// After a probe fails, the address and port it resolved to aren't dialled again for this long, under any name.
    /// The failed entry is dropped at once, so without this a caller could register the same target again and again
    /// under many names. Keyed by exact address and port, so a failing port, or a failing address in the same IPv6 /64,
    /// can't keep a real node off the list.
    /// </summary>
    public static readonly TimeSpan FailedCooldown = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);

    private bool CoolingDown(string key, DateTimeOffset now) => _failedAt.TryGetValue(key, out var at) && now - at < FailedCooldown;

    private static string HostOf(string normalized) => new Uri(normalized).Host;
    private static string Endpoint(string ip, int port) => $"{ip}|{port}";
    /// <summary>The listener a failed dial cools down: the exact address and port (not the machine's /64, so one address
    /// that fails can't hold back another one next to it).</summary>
    private static string DialKey(IPAddress ip, string address) => "dial:" + Endpoint(ip.ToString(), new Uri(address).Port);
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
        // One deadline for the lookup and the connection together, so the reply still fits the request's deadline.
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(VolunteerNodes.CheckTimeout);
        ct = total.Token;
        IPAddress? ip = null;
        if (_resolve is not null)
        {
            try { ip = await _resolve(address, ct); }
            catch (Exception e) when (e is not OutOfMemoryException) { ip = null; }
            if (ip is null) return new JsonObject { ["ok"] = true, ["reachable"] = false };
            // Also one check per machine at a time, so many names or ports of one machine can't be swept either.
            // A check refused here still used its place in the directory-wide budget on purpose: the name was already
            // looked up, and otherwise endless names for one machine would be free lookups.
            var machine = "machine:" + VolunteerNodes.MachineOf(ip);
            lock (_gate)
            {
                if (_lastCheck.ContainsKey(machine)) return Error("rate");
                _lastCheck[machine] = now;
            }
        }
        bool reachable;
        try { reachable = await Dial(address, ip, asNode: false, ct); }
        catch (Exception e) when (e is not OutOfMemoryException) { reachable = false; }
        return new JsonObject { ["ok"] = true, ["reachable"] = reachable };
    }

    /// <summary>
    /// Handles a "volunteer" request. <paramref name="signedBy"/> is the request's "operator" part
    /// (<see cref="VolunteerOperator.Sign"/>), if any. Returns the reply: <c>{ok, listed}</c> or an error code:
    /// "operator" when the signature doesn't check out (or is missing and required), "stale" when this account already
    /// registered this address with the same or a later time, "owner" when the address is registered under another
    /// operator (another account, or none) that hasn't let it lapse, "rate" when too many new addresses came this minute.
    /// </summary>
    /// <param name="source">The caller's address, for a per-caller limit on new addresses (null or loopback: none).</param>
    public JsonObject Register(string? url, JsonNode? signedBy = null, string? source = null)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return Error("bad");
        if (Blocked?.Invoke(address) == true) return Error("banned");
        var now = _now();
        if (source is not null && System.Net.IPAddress.TryParse(source, out var from) && System.Net.IPAddress.IsLoopback(from)) source = null;
        // The cheap limits come before the signature is checked: a new address from a caller (or, unsigned, from anyone)
        // over its limit costs no verification.
        lock (_gate)
        {
            if (!_entries.ContainsKey(address))
            {
                if (source is not null && Full(_newBy, "src:" + source, NewPerSourcePerMinute, now)) return Error("rate");
                if (signedBy is null && Full(_newUnsigned, NewPerMinute, now)) return Error("rate");
            }
            // Claims on a known address count against their caller too (only conflicting ones are charged, below).
            else if (signedBy is not null && source is not null && Full(_newBy, "claim-src:" + source, NewPerSourcePerMinute, now)) return Error("rate");
        }
        string? account = null;
        long signedAt = 0;
        if (signedBy is not null)
        {
            if (VolunteerOperator.Verify(signedBy, NetworkId, address, now) is not { } signed) return Error("operator");
            (account, signedAt) = signed;
        }
        if (account is null && RequireOperator) return Error("operator");
        if (account is not null && BlockedOperator?.Invoke(account) == true) return Error("banned");
        bool startProbe = false;
        bool listed = false;
        var conflict = false;
        var newlyDisputed = false;
        lock (_gate)
        {
            Prune(now);
            var known = _entries.TryGetValue(address, out var entry);
            if (!known)
            {
                if (_entries.Count >= MaxEntries) return Error("full");
                var returning = false;
                if (account is not null && _tombstones.TryGetValue(Key(account, address), out var gone))
                {
                    if (signedAt <= gone) return Error("stale");
                    returning = _mayReturn.Contains(Key(account, address));
                }
                // Each new entry is dialed: an address that just failed isn't dialed again sooner than a reachability check allows.
                foreach (var (dialed, _) in _lastDialed.Where(p => now - p.Value > CheckSameAddressEvery || p.Value > now).ToList()) _lastDialed.Remove(dialed);
                if (_lastDialed.ContainsKey(address)) return Error("rate");
                // Each new address costs the directory an outgoing connection, so new addresses are also capped per host:
                // registering can't be used to make the directory dial many ports of one machine. Listed entries hold a
                // host's places; entries still being probed are capped on their own and can't keep a host's real node off
                // the list (a failed probe drops its entry at once).
                var host = HostOf(address);
                if (_entries.Count(p => HostOf(p.Key) == host && p.Value.VerifiedAt is not null) >= MaxPerHost)
                    return new JsonObject { ["ok"] = false, ["error"] = "host", ["reason"] = $"one host may register at most {MaxPerHost} addresses" };
                if (_entries.Count(p => HostOf(p.Key) == host && p.Value.VerifiedAt is null) >= MaxPerHost) return Error("rate");
                // The caller's limit holds for everyone.
                if (source is not null && Full(_newBy, "src:" + source, NewPerSourcePerMinute, now)) return Error("rate");
                // A listed volunteer coming back to its own address isn't new.
                if (!returning)
                {
                    if (account is null ? Full(_newUnsigned, NewPerMinute, now)
                        : Full(_newSigned, NewPerMinute, now) || Full(_newBy, "acct:" + account, NewPerAccountPerMinute, now)) return Error("rate");
                    if (account is null) _newUnsigned.Enqueue(now);
                    else
                    {
                        _newSigned.Enqueue(now);
                        Take(_newBy, "acct:" + account, now);
                    }
                }
                if (source is not null) Take(_newBy, "src:" + source, now);
            }
            if (known && entry!.Operator != account)
            {
                conflict = true;
                if (account is null)
                {
                    // An unsigned registration of a signed address isn't a claim (it would let anyone, the operator
                    // included, mark the address as disputed), but it is counted: the address may be someone's who never
                    // signed and was registered first by somebody else.
                    entry.UnsignedAttempts++;
                    entry.LastUnsigned = now;
                }
                else
                {
                    // Claims have the same per-account and per-caller limits as new addresses.
                    if (Full(_newBy, "claim:" + account, NewPerAccountPerMinute, now)) return Error("rate");
                    Take(_newBy, "claim:" + account, now);
                    if (source is not null) Take(_newBy, "claim-src:" + source, now);
                    newlyDisputed = entry.ClaimedBy is null;
                    entry.ClaimedBy ??= [];
                    entry.Claims++;
                    entry.Claimants ??= new HashSet<string>(StringComparer.Ordinal);
                    if (entry.Claimants.Count < MaxClaimantsCounted) entry.Claimants.Add(account);
                    var mine = entry.ClaimedBy.FindIndex(c => c.Account == account);
                    if (mine >= 0) entry.ClaimedBy[mine] = (account, entry.ClaimedBy[mine].First, now);
                    else
                    {
                        // Full: the claim not renewed for longest makes room, except the longest-held claim still being
                        // renewed (a flood of new accounts can't push out whoever has been claiming it all along).
                        if (entry.ClaimedBy.Count >= MaxClaims)
                        {
                            var active = entry.ClaimedBy.Where(c => now - c.Seen <= 2 * VolunteerNodes.RenewEvery).ToList();
                            var kept = active.Count > 0 ? active.MinBy(c => c.First).Account : null;
                            var evict = entry.ClaimedBy.Select((c, i) => (c, i)).Where(x => x.c.Account != kept).MinBy(x => x.c.Seen).i;
                            entry.ClaimedBy.RemoveAt(evict);
                        }
                        entry.ClaimedBy.Add((account, now, now));
                    }
                }
            }
            else if (known && account is not null && signedAt <= entry!.SignedAt) return Error("stale");
            if (!known && !conflict)
            {
                _entries[address] = entry = new Entry { Operator = account };
            }
            if (!conflict && account is not null) entry!.SignedAt = signedAt;
            // An address keeps its operator (or having none) until it lapses: nobody can take it over, add a signature
            // to it or strip one. Who registered first needn't be who runs the node, so a conflicting claim (above) is
            // kept for the network's operator to look at; it changes nothing else.
            if (!conflict)
            {
                entry!.Seen = now;
                listed = entry.VerifiedAt is { } at && now - at < ReprobeEvery;
                startProbe = !listed && !entry.Probing;
                if (startProbe) entry.Probing = true;
            }
        }
        if (conflict)
        {
            if (newlyDisputed) Disputed?.Invoke(address);
            return Error("owner");
        }
        if (startProbe) LastProbe = ProbeAsync(address);
        return new JsonObject { ["ok"] = true, ["listed"] = listed };
    }

    /// <summary>
    /// Dials the address: at the IP it already resolved to when there is one (it isn't looked up again, so a name can't
    /// point somewhere else between the check and the connection), or through the custom probe.
    /// </summary>
    /// <remarks>
    /// <paramref name="asNode"/>: before listing, a node of the network must answer (when <see cref="NetworkName"/> is set);
    /// a would-be volunteer's port check only needs the port open.
    /// </remarks>
    private Task<bool> Dial(string address, IPAddress? ip, bool asNode, CancellationToken ct) =>
        _customProbe is not null ? _customProbe(address, ct)
        : ip is not null && asNode && NetworkName is { } network ? VolunteerNodes.IsNetworkNodeAsync(ip, new Uri(address).Port, network, ct)
        : ip is not null ? VolunteerNodes.ConnectAsync(ip, new Uri(address).Port, ct)
        : VolunteerNodes.ProbeAsync(address, ct);

    private async Task ProbeAsync(string address)
    {
        using var total = new CancellationTokenSource(VolunteerNodes.CheckTimeout);
        bool ok;
        IPAddress? ip = null;
        var crowded = false;
        DateTimeOffset? sameListener = null;
        if (_resolve is not null)
        {
            try { ip = await _resolve(address, total.Token); }
            catch (Exception e) when (e is not OutOfMemoryException) { ip = null; }
            if (ip is not null)
            {
                var machine = VolunteerNodes.MachineOf(ip);
                var port = new Uri(address).Port;
                var endpoint = Endpoint(ip.ToString(), port);
                lock (_gate)
                {
                    // Many names for one machine (an IPv4 address, or an IPv6 /64) count as that machine: over the cap, the
                    // entry goes without being dialled. As for names, listed entries and entries still being probed are
                    // counted apart. Names for one listener (the same address and port) take one place between them, so
                    // names somebody else points at a node can't crowd out the node's own registration (Pick lists one).
                    var others = _entries.Where(p => p.Key != address && p.Value.Machine == machine).ToList();
                    int Listeners(bool verified) => others.Where(p => (p.Value.VerifiedAt is not null) == verified && p.Value.Ip is not null)
                        .Select(p => Endpoint(p.Value.Ip!, new Uri(p.Key).Port)).Where(e => e != endpoint).Distinct().Count();
                    crowded = Listeners(true) >= MaxPerHost || Listeners(false) >= MaxPerHost || CoolingDown(DialKey(ip, address), _now());
                    // A listener checked under another name since this one was needn't be dialled again: the name takes
                    // that check's time, so the listener is still dialled once its newest check is too old.
                    _entries.TryGetValue(address, out var mine);
                    var now = _now();
                    sameListener = crowded ? null : others
                        .Where(p => p.Value.VerifiedAt is { } at && now - at < ReprobeEvery && at > (mine?.VerifiedAt ?? DateTimeOffset.MinValue)
                                    && p.Value.Ip is { } other && Endpoint(other, new Uri(p.Key).Port) == endpoint)
                        .Max(p => p.Value.VerifiedAt);
                    if (!crowded && mine is not null) (mine.Machine, mine.Ip) = (machine, ip.ToString());
                }
            }
        }
        if (crowded || (_resolve is not null && ip is null)) ok = false;
        else if (sameListener is not null) ok = true;
        else
        {
            try { ok = await Dial(address, ip, asNode: true, total.Token); }
            catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
        }
        lock (_gate)
        {
            if (_entries.TryGetValue(address, out var entry))
            {
                entry.Probing = false;
                if (ok) entry.VerifiedAt = sameListener ?? _now();
                else
                {
                    _lastDialed[address] = _now();
                    Drop(address, entry);
                }
            }
            if (!ok && !crowded)
            {
                // Dialled and failed: cool down that port of the machine, whatever happens to the entry (the address
                // itself waits as any failed address does).
                if (ip is not null) _failedAt[DialKey(ip, address)] = _now();
            }
        }
        Probed?.Invoke(address, ok);
    }

    /// <summary>
    /// Up to <paramref name="count"/> verified, live volunteers in random order (spreads the load), at most one per
    /// operator while there are others to give. Each comes with its operator tag ("" when unsigned).
    /// </summary>
    /// <remarks>Names for one listener (the same port of one machine) are given once.</remarks>
    public IReadOnlyList<VolunteerNode> Pick(int count = VolunteerNodes.PerRequest)
    {
        // A name that is the address itself can't be pointed elsewhere later, so it goes first among a listener's names.
        var shuffled = LiveEntries().OrderBy(p => IPAddress.TryParse(new Uri(p.Entry.Url).Host.Trim('[', ']'), out _) ? 0 : 1)
            .ThenBy(_ => Random.Shared.Next())
            .DistinctBy(p => p.Listener)
            .OrderBy(_ => Random.Shared.Next())
            .Select(p => new VolunteerNode(p.Entry.Url, p.Entry.Disputed ? "" : p.Entry.Operator ?? ""));
        return [.. VolunteerNode.SpreadOperators(shuffled).Take(count)];
    }

    /// <summary>Every verified, live volunteer (admin API).</summary>
    public IReadOnlyList<string> Listed() => [.. Live().Select(p => p.Url)];

    /// <summary>
    /// Every verified, live volunteer with the Jay account it was registered under (null: unsigned), and whether someone
    /// claimed it under another operator since (admin API).
    /// </summary>
    public IReadOnlyList<VolunteerEntry> Operators() => Live();

    private List<VolunteerEntry> Live() => [.. LiveEntries().Select(p => p.Entry)];

    /// <summary>Live entries with the listener each one names (machine and port; the name itself until it resolved).</summary>
    private List<(VolunteerEntry Entry, string Listener)> LiveEntries()
    {
        var now = _now();
        lock (_gate)
        {
            Prune(now);
            return [.. _entries.Where(p => p.Value.VerifiedAt is not null && Blocked?.Invoke(p.Key) != true
                    && (p.Value.Operator is not { } op || BlockedOperator?.Invoke(op) != true))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => (Public(p.Key, p.Value), p.Value.Ip is { } ip ? Endpoint(ip, new Uri(p.Key).Port) : p.Key))];
        }
    }

    private static VolunteerEntry Public(string url, Entry e) =>
        new(url, e.Operator, e.ClaimedBy?.Select(c => c.Account).ToList(), e.UnsignedAttempts, e.LastUnsigned, e.Claims, e.Claimants?.Count ?? 0);

    /// <summary>Drops an address now (it can register again unless it is also banned).</summary>
    public bool Remove(string url)
    {
        if (VolunteerNodes.Normalize(url) is not { } address) return false;
        lock (_gate) return _entries.TryGetValue(address, out var entry) && Drop(address, entry);
    }

    /// <summary>Drops every node this Jay account runs. Returns their addresses.</summary>
    /// <remarks>Each comes back as the operator saw it, with any conflicting claims (someone else may run that address).</remarks>
    public IReadOnlyList<VolunteerEntry> RemoveOperator(string account)
    {
        lock (_gate)
        {
            var gone = _entries.Where(p => p.Value.Operator == account).ToList();
            foreach (var (key, entry) in gone) Drop(key, entry);
            return [.. gone.Select(p => Public(p.Key, p.Value))];
        }
    }

    private static string Key(string account, string address) => account + "\n" + address;

    private static bool Full(Queue<DateTimeOffset> times, int limit, DateTimeOffset now)
    {
        // A time ahead of now (the clock went back) is dropped too, so the limit can't stick.
        while (times.TryPeek(out var at) && (now - at > TimeSpan.FromMinutes(1) || at > now)) times.Dequeue();
        return times.Count >= limit;
    }

    private static bool Full(Dictionary<string, Queue<DateTimeOffset>> counts, string key, int limit, DateTimeOffset now)
    {
        if (counts.Count > 4096)
            foreach (var (k, _) in counts.Where(p => !p.Value.TryPeek(out var at) || now - at > TimeSpan.FromMinutes(1)).ToList()) counts.Remove(k);
        return counts.TryGetValue(key, out var times) && Full(times, limit, now);
    }

    private static void Take(Dictionary<string, Queue<DateTimeOffset>> counts, string key, DateTimeOffset now)
    {
        if (!counts.TryGetValue(key, out var times)) counts[key] = times = new Queue<DateTimeOffset>();
        times.Enqueue(now);
    }

    /// <summary>Removes an entry, keeping its signing time for a while (see <see cref="_tombstones"/>). Under the lock.</summary>
    private bool Drop(string address, Entry entry)
    {
        if (!_entries.Remove(address)) return false;
        if (entry.Operator is not { } account || entry.SignedAt == 0) return true;
        // A signing time already outside the clock window can't be replayed: nothing to keep.
        if (entry.SignedAt < (_now() - VolunteerOperator.MaxClockSkew).ToUnixTimeSeconds()) return true;
        var key = Key(account, address);
        _tombstones[key] = entry.SignedAt;
        if (entry.VerifiedAt is not null) _mayReturn.Add(key);
        else _mayReturn.Remove(key);
        _tombstoneOrder.Enqueue((key, entry.SignedAt));
        while (_tombstoneOrder.Count > MaxTombstones)
        {
            var (oldKey, oldAt) = _tombstoneOrder.Dequeue();
            if (_tombstones.TryGetValue(oldKey, out var at) && at == oldAt)
            {
                _tombstones.Remove(oldKey);
                _mayReturn.Remove(oldKey);
            }
        }
        return true;
    }

    private void Prune(DateTimeOffset now)
    {
        // Times older than the clock window can't be replayed anyway. The queue is in the order entries went away, not
        // by signing time, so the list itself is swept (it is small); the queue only decides who goes first when full,
        // and skips keys already gone.
        var oldest = (now - VolunteerOperator.MaxClockSkew).ToUnixTimeSeconds();
        foreach (var (key, _) in _tombstones.Where(p => p.Value < oldest).ToList())
        {
            _tombstones.Remove(key);
            _mayReturn.Remove(key);
        }
        while (_tombstoneOrder.TryPeek(out var head) && !(_tombstones.TryGetValue(head.Key, out var at) && at == head.At)) _tombstoneOrder.Dequeue();
        foreach (var (key, entry) in _entries.Where(p => now - p.Value.Seen > VolunteerNodes.Ttl && !p.Value.Probing).ToList())
            Drop(key, entry);
        foreach (var key in _failedAt.Where(p => now - p.Value >= FailedCooldown).Select(p => p.Key).ToList()) _failedAt.Remove(key);
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

/// <summary>
/// The first packet of an EasyTier peer connection over TCP, enough to tell a node of a network from any other service:
/// a 4-byte length, the 16-byte peer header (from, to, type, flags, forward count, reserved, payload length; little
/// endian) and a protobuf HandshakeRequest (magic, peer id, version, network name, secret digest), as EasyTier's
/// <c>tunnel/packet_def.rs</c> and <c>proto/peer_rpc.proto</c> describe them.
/// </summary>
internal static class NodeHandshake
{
    public const int HeaderSize = 16;
    public const int MaxReply = 4096;
    private const uint Magic = 0xd1e1a5e1;
    private const byte HandshakeType = 2;

    /// <summary>The handshake request, with the secret digest left zero (the node replies all the same).</summary>
    public static byte[] Request(string networkName, int peerId)
    {
        var payload = new List<byte>();
        Varint(payload, 1 << 3); Varint(payload, Magic);
        Varint(payload, 2 << 3); Varint(payload, (uint)peerId);
        Varint(payload, 3 << 3); Varint(payload, 1);
        var name = Encoding.UTF8.GetBytes(networkName);
        Varint(payload, (5 << 3) | 2); Varint(payload, (uint)name.Length); payload.AddRange(name);
        Varint(payload, (6 << 3) | 2); Varint(payload, 32); payload.AddRange(new byte[32]);
        var packet = new byte[4 + HeaderSize + payload.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)(HeaderSize + payload.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)peerId);
        packet[12] = HandshakeType;
        packet[14] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), (uint)payload.Count);
        payload.CopyTo(packet, 4 + HeaderSize);
        return packet;
    }

    /// <summary>Is <paramref name="body"/> (after the length) a handshake from a node of <paramref name="networkName"/>?</summary>
    public static bool IsReply(ReadOnlySpan<byte> body, string networkName)
    {
        if (body.Length < HeaderSize || body[8] != HandshakeType) return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        if (length != body.Length - HeaderSize) return false;
        var payload = body[HeaderSize..];
        uint? magic = null;
        string? name = null;
        var at = 0;
        while (at < payload.Length)
        {
            if (!TryVarint(payload, ref at, out var key)) return false;
            switch (key & 7)
            {
                case 0:
                    if (!TryVarint(payload, ref at, out var value)) return false;
                    if (key >> 3 == 1) magic = (uint)value;
                    break;
                case 2:
                    if (!TryVarint(payload, ref at, out var size) || size > (ulong)(payload.Length - at)) return false;
                    if (key >> 3 == 5) name = Encoding.UTF8.GetString(payload.Slice(at, (int)size));
                    at += (int)size;
                    break;
                case 5: at += 4; break;
                case 1: at += 8; break;
                default: return false;
            }
        }
        return at == payload.Length && magic == Magic && name == networkName;
    }

    private static void Varint(List<byte> to, uint value)
    {
        while (value >= 0x80)
        {
            to.Add((byte)(value | 0x80));
            value >>= 7;
        }
        to.Add((byte)value);
    }

    private static bool TryVarint(ReadOnlySpan<byte> from, ref int at, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && at < from.Length; shift += 7)
        {
            var b = from[at++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}
