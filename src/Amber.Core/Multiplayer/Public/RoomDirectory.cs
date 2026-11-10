using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 目录服务的协议：一条连接一个请求，各是一行 JSON（UTF-8，最长 16 KB）。
/// announce 发布 / 续期房间（房主用房间的签名钥匙签过，房间编号由签名公钥派生，序号只能变大）；
/// withdraw 撤下；list 看公开列表；lookup 按大厅名找房主（拿着邀请码的人用它找到隐藏房间）；mail 给房主捎一句话（直连房间的回复码）；
/// find 按设备指纹码找房间（房主打开了“可以用指纹码找到这个房间”时，发布时带上指纹密钥，目录拿它认码）。
/// 目录服务只存这些，不经手任何钥匙；房主 90 秒没续期就从列表里消失。
/// <para>
/// post / fetch / drop are mailboxes for sealed mail between identities (<see cref="Contacts.SealedMail"/>): contact
/// requests and room invites that never touch a chat platform. The directory keeps opaque blobs under a daily mailbox id
/// for <see cref="MailTtl"/>; it can't read them, can't tell who sent them, and only the recipient can delete one early.
/// </para>
/// </summary>
public static class RoomDirectoryProtocol
{
    public const int MaxLine = 16 * 1024;
    public const int MaxLookupKeys = 4;
    public const int MaxMailLength = 4096;
    public const int MaxMailPerRoom = 16;
    /// <summary>Sealed mail kept per mailbox; more is refused with "full" until the recipient picks some up or it expires.</summary>
    public const int MaxMailPerBox = 32;
    /// <summary>Mailboxes asked for in one fetch (yesterday's, today's and tomorrow's).</summary>
    public const int MaxFetchBoxes = 3;
    public static readonly TimeSpan MailTtl = TimeSpan.FromHours(48);
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    internal static async Task WriteLineAsync(Stream stream, JsonNode node, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        if (bytes.Length > MaxLine) throw new InvalidDataException(L.T("core.mp.public.too_large"));
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<JsonObject> ReadLineAsync(Stream stream, CancellationToken ct, byte? firstByte = null)
    {
        var buffer = new byte[MaxLine];
        var length = 0;
        if (firstByte is { } b) buffer[length++] = b;
        while (true)
        {
            if (length == buffer.Length) throw new InvalidDataException(L.T("core.mp.public.too_large"));
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) throw new EndOfStreamException();
            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                try
                {
                    return JsonNode.Parse(buffer.AsSpan(0, newline)) as JsonObject ?? throw new InvalidDataException(L.T("core.mp.public.bad_reply"));
                }
                catch (JsonException) { throw new InvalidDataException(L.T("core.mp.public.bad_reply")); }
            }
        }
    }

    /// <summary>邀请码的大厅名 → 目录里的查找键（不直接存大厅名，免得目录拿它去进大厅）。</summary>
    public static string LookupKey(ReadOnlySpan<byte> lobbyKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("jay-lookup-v1:" + LobbyKeys.LobbyName(lobbyKey))));
}

/// <summary>目录服务（跑在公共网络节点那台机器上，监听 127.0.0.1:47668，由同一台机器上的公共网络 EasyTier 把 10.235.0.1:47668 交过来）。</summary>
public sealed class RoomDirectoryServer : IAsyncDisposable
{
    public const int MaxRooms = 5000;
    public const int MaxConcurrent = 64;
    public const int MaxListed = 300;

    private sealed class Entry
    {
        public required RoomListing Listing;
        public bool Listed;
        public string[] Lookup = [];
        /// <summary>房主的指纹密钥（没打开指纹查找时为 null）。</summary>
        public byte[]? Search;
        public DateTimeOffset Expires;
        public readonly List<string> Mail = [];
        /// <summary>The host's verified network account (null when the host did not log in).</summary>
        public string? Account;
    }

    private sealed record SealedItem(string Blob, string DropHash, DateTimeOffset Expires);

    /// <summary>All mailboxes together hold at most this many items (about 32 MB of blobs at the largest size).</summary>
    public const int MaxSealedMail = 8000;

    private readonly TcpListener _listener;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lookup = new(StringComparer.Ordinal);
    /// <summary>指纹密钥（十六进制）→ 房间编号，先到先得。</summary>
    private readonly Dictionary<string, string> _search = new(StringComparer.Ordinal);
    /// <summary>撤下过的房间和撤下时的序号（之后拿旧签名重新发布的不认）。只留最近的一些。</summary>
    private readonly Dictionary<string, long> _withdrawn = new(StringComparer.Ordinal);
    /// <summary>Mailbox id → sealed mail waiting there.</summary>
    private readonly Dictionary<string, List<SealedItem>> _mailboxes = new(StringComparer.Ordinal);
    private int _sealedCount;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public int Port { get; }

    /// <summary>账号密码登录（jay-server directory --accounts 时才有）。</summary>
    public LoginAuthority? Login { get; set; }

    /// <summary>
    /// Oldest Jay allowed to use this directory (from the network config's policy.minVersion). Every request carries the
    /// client's version in "jay"; older or unversioned clients get error "version" plus "min", and Jay tells the user to update.
    /// </summary>
    public Version? MinClientVersion { get; set; }

    /// <summary>Only hosts logged in to this network's accounts may publish rooms (set when the network config requires login).</summary>
    public bool RequireAccountToPublish { get; set; }

    /// <summary>
    /// This directory's X25519 identity. With one, clients of a network that pins it ("dirKeys") reach the directory
    /// through <see cref="DirectoryChannel"/>. Without one, only the legacy protocol is spoken.
    /// </summary>
    public X25519KeyPair? Identity { get; set; }

    /// <summary>The network's EasyTier secret, for <see cref="DirectoryChannel"/>'s psk.</summary>
    public string NetworkSecret { get; set; } = "";

    /// <summary>
    /// This directory's certificate from the network's root key (<see cref="DirectoryCertificate"/>), sent in the
    /// handshake so Jay can accept a key that isn't pinned in the connection string. Null: none.
    /// </summary>
    public byte[]? Certificate { get; set; }

    /// <summary>
    /// Whether unauthenticated (legacy JSON line) requests are still answered. True by default so Jay versions without
    /// directory pinning keep working; a network that requires a new enough Jay can turn it off.
    /// </summary>
    public bool AcceptLegacyClients { get; set; } = true;

    /// <summary>Volunteer nodes ("volunteer" / "nodes" ops). Null = this network doesn't take volunteers (error "off").</summary>
    public VolunteerRegistry? Volunteers { get; set; }

    /// <summary>房间审核规则（按顺序，任何一条拒绝就不收）。见 <see cref="IRoomGate"/>。</summary>
    public IReadOnlyList<IRoomGate> Gates { get; set; } = [];

    /// <summary>目录事件的订阅者。见 <see cref="IDirectoryObserver"/>。</summary>
    public IReadOnlyList<IDirectoryObserver> Observers { get; set; } = [];

    /// <summary>自定义请求（op 以 x- 开头）。见 <see cref="IDirectoryExtension"/>。</summary>
    public IReadOnlyList<IDirectoryExtension> Extensions { get; set; } = [];

    /// <summary>锁里攒下的事件，出锁后再通知（订阅者不在锁里跑）。</summary>
    private readonly List<Action<IDirectoryObserver>> _events = [];

    public RoomDirectoryServer(IPAddress address, int port, Action<string>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    public int Count
    {
        get
        {
            try { lock (_gate) { Prune(); return _rooms.Count; } }
            finally { Flush(); }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client); }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var first = new byte[1];
            if (!await SecureStream.ReadExactAsync(stream, first, deadline.Token)) return;
            if (first[0] == DirectoryChannel.LegacyFirstByte)
            {
                if (!AcceptLegacyClients) return;
                var request = await RoomDirectoryProtocol.ReadLineAsync(stream, deadline.Token, first[0]);
                await RoomDirectoryProtocol.WriteLineAsync(stream, Handle(request), deadline.Token);
                return;
            }
            if (Identity is null) return;
            if (await DirectoryChannel.AcceptAsync(stream, first[0], Identity, NetworkSecret, deadline.Token, Certificate) is not { } secure) return;
            await secure.Reply(Handle(secure.Request), deadline.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or EndOfStreamException) { }
    }

    /// <summary>处理一个请求（测试和框架的宿主直接调）。</summary>
    public JsonObject Handle(JsonObject request)
    {
        try
        {
            var op = request["op"]?.GetValue<string>();
            if (MinClientVersion is { } min && !ClientVersion.Meets(request["jay"]?.GetValue<string>(), min))
                return new JsonObject { ["ok"] = false, ["error"] = "version", ["min"] = min.ToString() };
            return op switch
            {
                "announce" => Announce(request),
                "withdraw" => Withdraw(request),
                "list" => List(),
                "lookup" => Lookup(request),
                "mail" => Mail(request),
                "find" => Find(request),
                "post" => Post(request),
                "fetch" => Fetch(request),
                "drop" => Drop(request),
                "volunteer" => Volunteers is { } v ? v.Register(request["url"]?.GetValue<string>()) : Error("off"),
                // Blocks this connection's task for at most a few seconds while the directory dials the address.
                "probe" => Volunteers is { } p ? p.CheckAsync(request["url"]?.GetValue<string>()).GetAwaiter().GetResult() : Error("off"),
                "nodes" => Volunteers is { } v ? new JsonObject { ["ok"] = true, ["nodes"] = new JsonArray([.. v.Pick().Select(u => (JsonNode)u)]) } : Error("off"),
                "login" when Login is { } login => LoginRequest(login, request),
                _ when DirectoryExtensions.IsValidOp(op) && Extensions.FirstOrDefault(x => x.Op == op) is { } extension => Extend(extension, request),
                _ => Error("op"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or JsonException)
        {
            return Error("bad");
        }
        finally
        {
            Flush();
        }
    }

    private JsonObject LoginRequest(LoginAuthority login, JsonObject request)
    {
        var reply = login.Handle(request);
        var ok = reply["ok"]?.GetValue<bool>() == true;
        var error = reply["error"]?.GetValue<string>();
        lock (_gate) _events.Add(o => o.OnLogin(ok, error));
        return reply;
    }

    private JsonObject Extend(IDirectoryExtension extension, JsonObject request)
    {
        JsonObject reply;
        try { reply = extension.Handle(request, View()); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Error("ext"); }
        reply["ok"] ??= true;
        return reply;
    }

    /// <summary>把锁里攒下的事件发给订阅者。订阅者出错不影响目录。</summary>
    private void Flush()
    {
        Action<IDirectoryObserver>[] events;
        lock (_gate)
        {
            if (_events.Count == 0 || Observers.Count == 0) { _events.Clear(); return; }
            events = [.. _events];
            _events.Clear();
        }
        foreach (var observer in Observers)
            foreach (var e in events)
            {
                try { e(observer); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.Invoke(L.F("core.mp.public.observer_error", ex.Message)); }
            }
    }

    /// <summary>目录快照：房间总数、公开数、公开列表。</summary>
    public DirectoryView View()
    {
        lock (_gate)
        {
            Prune();
            var listed = _rooms.Values.Where(e => e.Listed && e.Listing.Mode != RoomVisibility.Hidden).Select(e => e.Listing).ToList();
            return new DirectoryView(_rooms.Count, listed.Count, listed);
        }
    }

    /// <summary>运营者看的公开房间（隐藏房间不给内容，只在 <see cref="View"/> 里计数）。</summary>
    public IReadOnlyList<DirectoryRoomInfo> ListedRooms()
    {
        try
        {
            lock (_gate)
            {
                Prune();
                return [.. _rooms.Values.Where(e => e.Listed && e.Listing.Mode != RoomVisibility.Hidden).OrderByDescending(e => e.Listing.Updated)
                    .Select(e => new DirectoryRoomInfo(e.Listing.Id, e.Listing.Name, e.Listing.Host, e.Listing.HostFingerprint, e.Listing.Mode,
                        e.Listing.Players, e.Listing.Game, e.Listing.Updated, e.Account))];
            }
        }
        finally { Flush(); }
    }

    /// <summary>运营者删掉一个房间（房主下次续期时会再过一遍审核规则；要彻底挡住就同时封禁）。</summary>
    public bool Remove(string id)
    {
        try
        {
            lock (_gate)
            {
                if (!_rooms.TryGetValue(id, out var entry)) return false;
                RemoveLocked(id, entry, RoomRemoval.Removed);
                return true;
            }
        }
        finally { Flush(); }
    }

    /// <summary>The verified account of a room's host, if any (for "ban this room's host").</summary>
    public (string HostFingerprint, string? Account)? HostOf(string id)
    {
        lock (_gate) return _rooms.TryGetValue(id, out var e) ? (e.Listing.HostFingerprint, e.Account) : null;
    }

    /// <summary>Removes every room published under a network account. Returns how many.</summary>
    public int RemoveAccount(string account)
    {
        try
        {
            lock (_gate)
            {
                var ids = _rooms.Where(p => p.Value.Account == account).Select(p => p.Key).ToList();
                foreach (var id in ids) RemoveLocked(id, _rooms[id], RoomRemoval.Removed);
                return ids.Count;
            }
        }
        finally { Flush(); }
    }

    /// <summary>删掉某个房主（联机身份指纹）开的所有房间。返回删了几个。</summary>
    public int RemoveHost(string hostFingerprint)
    {
        try
        {
            lock (_gate)
            {
                var ids = _rooms.Where(p => string.Equals(p.Value.Listing.HostFingerprint, hostFingerprint, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
                foreach (var id in ids) RemoveLocked(id, _rooms[id], RoomRemoval.Removed);
                return ids.Count;
            }
        }
        finally { Flush(); }
    }

    private static JsonObject Error(string code) => new() { ["ok"] = false, ["error"] = code };

    private static JsonObject Error(RoomVerdict verdict)
    {
        var reply = Error(verdict.Code!);
        if (verdict.Reason is { Length: > 0 } reason) reply["reason"] = reason;
        return reply;
    }

    private JsonObject Announce(JsonObject request)
    {
        // FromJson 已经核对过签名和编号。
        if (RoomListing.FromJson(request["room"]) is not { } listing || listing.Ip.Length == 0) return Error("room");
        var listed = request["listed"]?.GetValue<bool>() ?? false;
        var keys = (request["lookup"] as JsonArray ?? []).Select(k => k?.GetValue<string>()).OfType<string>()
            .Where(k => k.Length == 64 && k.All(char.IsAsciiHexDigit)).Distinct().Take(RoomDirectoryProtocol.MaxLookupKeys).ToArray();
        var fp = request["fp"]?.GetValue<string>();
        if (fp is not null && (fp.Length != 64 || !fp.All(char.IsAsciiHexDigit))) return Error("bad");
        fp = fp?.ToLowerInvariant();
        if (!listed && keys.Length == 0 && fp is null) return Error("empty");
        // The host's account: a login token this node's login service signed for exactly this host identity.
        string? account = null;
        if (Login is { } login && request["account"]?.GetValue<string>() is { Length: > 0 and <= 2048 } token)
            account = AccountToken.Verify(token, login.SignKey, login.Network, listing.HostKey, _now())?.User;
        if (RequireAccountToPublish && account is null) return Error("login");
        lock (_gate)
        {
            Prune();
            if (_rooms.TryGetValue(listing.Id, out var entry))
            {
                // 旧的签名版本（重放）不认。
                if (listing.Seq <= entry.Listing.Seq) return Error("stale");
            }
            else
            {
                if (_rooms.Count >= MaxRooms) return Error("full");
                if (_withdrawn.TryGetValue(listing.Id, out var gone) && listing.Seq <= gone) return Error("stale");
                entry = new Entry { Listing = listing };
            }
            // 查找键先到先得：别人的房间已经占着的键不给（拿着同一张公开邀请的人不能把找房主的人引到自己那里）。
            foreach (var key in keys)
                if (_lookup.TryGetValue(key, out var holder) && holder != listing.Id) return Error("lookup");
            if (fp is not null && _search.TryGetValue(fp, out var owner) && owner != listing.Id) return Error("lookup");
            var isNew = !_rooms.ContainsKey(listing.Id);
            var announcement = new RoomAnnouncement(listing, listed, isNew, fp is not null, account);
            foreach (var gate in Gates)
            {
                var verdict = gate.Check(announcement);
                if (verdict.Allowed) continue;
                _events.Add(o => o.OnRejected(listing, verdict));
                // 续期被拒（比如刚被封禁）：已经在目录里的也拿掉。
                if (!isNew) RemoveLocked(listing.Id, entry, RoomRemoval.Removed);
                return Error(verdict);
            }
            foreach (var old in entry.Lookup) _lookup.Remove(old);
            foreach (var key in keys) _lookup[key] = listing.Id;
            entry.Lookup = keys;
            if (entry.Search is { } oldSearch) _search.Remove(Convert.ToHexStringLower(oldSearch));
            entry.Search = fp is null ? null : Convert.FromHexString(fp);
            if (fp is not null) _search[fp] = listing.Id;
            entry.Listing = listing with { Updated = _now() };
            entry.Listed = listed;
            entry.Account = account;
            entry.Expires = _now() + RoomDirectoryProtocol.Ttl;
            _rooms[listing.Id] = entry;
            var published = entry.Listing;
            _events.Add(o => o.OnAnnounced(published, listed, isNew));
            var mail = new JsonArray(entry.Mail.Select(m => (JsonNode?)m).ToArray());
            entry.Mail.Clear();
            return new JsonObject { ["ok"] = true, ["mail"] = mail };
        }
    }

    private JsonObject Withdraw(JsonObject request)
    {
        var id = request["id"]?.GetValue<string>();
        var seq = request["seq"]?.GetValue<long>();
        var sig = request["sig"]?.GetValue<string>();
        if (id is null || seq is not { } s || sig is not { Length: 128 } || !sig.All(char.IsAsciiHexDigit)) return Error("bad");
        lock (_gate)
        {
            if (!_rooms.TryGetValue(id, out var entry)) return new JsonObject { ["ok"] = true };
            if (s <= entry.Listing.Seq || !RoomSigner.Verify(entry.Listing.SignKey, RoomSigner.WithdrawBytes(id, s), Convert.FromHexString(sig))) return Error("owner");
            RemoveLocked(id, entry, RoomRemoval.Withdrawn);
            if (_withdrawn.Count >= MaxRooms) _withdrawn.Clear();
            _withdrawn[id] = s;
            return new JsonObject { ["ok"] = true };
        }
    }

    private JsonObject List()
    {
        lock (_gate)
        {
            Prune();
            var rooms = _rooms.Values.Where(e => e.Listed && e.Listing.Mode != RoomVisibility.Hidden)
                .OrderByDescending(e => e.Listing.Players).ThenByDescending(e => e.Listing.Updated).Take(MaxListed)
                .Select(e => (JsonNode?)e.Listing.ToJson()).ToArray();
            return new JsonObject { ["ok"] = true, ["rooms"] = new JsonArray(rooms) };
        }
    }

    private JsonObject Lookup(JsonObject request)
    {
        var key = request["key"]?.GetValue<string>();
        if (key is null) return Error("bad");
        lock (_gate)
        {
            Prune();
            if (!_lookup.TryGetValue(key, out var id) || !_rooms.TryGetValue(id, out var entry)) return new JsonObject { ["ok"] = true };
            // 只给地址和房主公钥（隐藏房间的其他信息不外露）。
            return new JsonObject { ["ok"] = true, ["ip"] = entry.Listing.Ip, ["key"] = Convert.ToHexStringLower(entry.Listing.HostKey), ["id"] = id };
        }
    }

    /// <summary>按指纹码找房间：拿每个登记了指纹密钥的房间算一遍标签（几千个 HMAC，很快）。找到了给整份签过名的房间信息。</summary>
    private JsonObject Find(JsonObject request)
    {
        if (!FingerprintCode.TryParse(request["code"]?.GetValue<string>(), out var code)) return Error("bad");
        lock (_gate)
        {
            Prune();
            foreach (var entry in _rooms.Values)
                if (entry.Search is { } key && FingerprintCode.Matches(key, code))
                    return new JsonObject { ["ok"] = true, ["room"] = entry.Listing.ToJson() };
            return new JsonObject { ["ok"] = true };
        }
    }

    private JsonObject Mail(JsonObject request)
    {
        var id = request["id"]?.GetValue<string>();
        var text = request["text"]?.GetValue<string>();
        if (id is null || text is not { Length: > 0 and <= RoomDirectoryProtocol.MaxMailLength }) return Error("bad");
        lock (_gate)
        {
            Prune();
            if (!_rooms.TryGetValue(id, out var entry) || entry.Listing.Mode != RoomVisibility.Direct) return Error("gone");
            if (entry.Mail.Count >= RoomDirectoryProtocol.MaxMailPerRoom) return Error("full");
            entry.Mail.Add(text);
            return new JsonObject { ["ok"] = true };
        }
    }

    private static bool IsHex(string? text, int length) => text is not null && text.Length == length && text.All(char.IsAsciiHexDigit);

    private JsonObject Post(JsonObject request)
    {
        var box = request["box"]?.GetValue<string>()?.ToLowerInvariant();
        var blob = request["blob"]?.GetValue<string>();
        var drop = request["drop"]?.GetValue<string>()?.ToLowerInvariant();
        if (!IsHex(box, 32) || !IsHex(drop, 64) || blob is null || blob.Length > (Contacts.SealedMail.MaxBlob + 2) / 3 * 4) return Error("bad");
        try { if (Convert.FromBase64String(blob).Length is 0 or > Contacts.SealedMail.MaxBlob) return Error("bad"); }
        catch (FormatException) { return Error("bad"); }
        lock (_gate)
        {
            PruneMail();
            if (!_mailboxes.TryGetValue(box!, out var items))
            {
                if (_sealedCount >= MaxSealedMail) return Error("full");
                _mailboxes[box!] = items = [];
            }
            if (items.Any(i => i.Blob == blob)) return new JsonObject { ["ok"] = true };
            if (items.Count >= RoomDirectoryProtocol.MaxMailPerBox || _sealedCount >= MaxSealedMail) return Error("full");
            items.Add(new SealedItem(blob, drop!, _now() + RoomDirectoryProtocol.MailTtl));
            _sealedCount++;
            return new JsonObject { ["ok"] = true };
        }
    }

    /// <summary>Room left for blobs in one fetch reply (the reply is one line of at most <see cref="RoomDirectoryProtocol.MaxLine"/>).</summary>
    private const int FetchBudget = 12 * 1024;

    private JsonObject Fetch(JsonObject request)
    {
        var boxes = (request["boxes"] as JsonArray ?? []).Select(b => b?.GetValue<string>()?.ToLowerInvariant()).Where(b => IsHex(b, 32))
            .Distinct().Take(RoomDirectoryProtocol.MaxFetchBoxes).ToList();
        var from = Math.Max(0, request["from"]?.GetValue<int>() ?? 0);
        lock (_gate)
        {
            PruneMail();
            var all = boxes.SelectMany(box => _mailboxes.TryGetValue(box!, out var items) ? items.Select(i => (Box: box!, i.Blob)) : []).ToList();
            var found = new JsonArray();
            var used = 0;
            var next = from;
            for (; next < all.Count; next++)
            {
                var (box, blob) = all[next];
                if (used > 0 && used + blob.Length + 64 > FetchBudget) break;
                used += blob.Length + 64;
                found.Add((JsonNode)new JsonObject { ["box"] = box, ["blob"] = blob });
            }
            var reply = new JsonObject { ["ok"] = true, ["items"] = found };
            if (next < all.Count) reply["next"] = next;
            return reply;
        }
    }

    private JsonObject Drop(JsonObject request)
    {
        var box = request["box"]?.GetValue<string>()?.ToLowerInvariant();
        var token = request["token"]?.GetValue<string>();
        if (!IsHex(box, 32) || !IsHex(token, Contacts.MailMessage.TokenLength * 2)) return Error("bad");
        var hash = Contacts.SealedMail.DropHash(Convert.FromHexString(token!));
        lock (_gate)
        {
            if (_mailboxes.TryGetValue(box!, out var items))
            {
                _sealedCount -= items.RemoveAll(i => i.DropHash == hash);
                if (items.Count == 0) _mailboxes.Remove(box!);
            }
            return new JsonObject { ["ok"] = true };
        }
    }

    private void PruneMail()
    {
        var now = _now();
        foreach (var (box, items) in _mailboxes.ToList())
        {
            _sealedCount -= items.RemoveAll(i => i.Expires <= now);
            if (items.Count == 0) _mailboxes.Remove(box);
        }
    }

    private void Prune()
    {
        var now = _now();
        foreach (var (id, entry) in _rooms.Where(p => p.Value.Expires <= now).ToList()) RemoveLocked(id, entry, RoomRemoval.Expired);
    }

    private void RemoveLocked(string id, Entry entry, RoomRemoval reason)
    {
        if (!_rooms.Remove(id)) return;
        _events.Add(o => o.OnRemoved(id, reason));
        foreach (var key in entry.Lookup)
            if (_lookup.TryGetValue(key, out var holder) && holder == id) _lookup.Remove(key);
        if (entry.Search is { } search && _search.TryGetValue(Convert.ToHexStringLower(search), out var finder) && finder == id)
            _search.Remove(Convert.ToHexStringLower(search));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _accept; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        _cts.Dispose();
    }
}

/// <summary>查到的房主（拿着邀请码的人在目录里找到的）。</summary>
public sealed record DirectoryHit(string Id, string Ip, byte[] HostKey);

/// <summary>
/// 目录服务的客户端：经公共网络连目录节点（10.235.0.1，网络有备用目录节点时还有 .2 .3 .4，见 <see cref="PublicNetworkProfile.DirectoryIps"/>）。
/// 写（发布、续期、撤下）发给每个在线的目录节点，所以每个目录都有完整的房间表，一台挂了别的照常；
/// 读（列表）合并所有在线目录的结果，查找按顺序问到有结果为止；捎话、登录交给第一个肯收的。
/// 备用目录节点之间不用互相信任或同步：房间信息本来就是房主签过名的。
/// </summary>
public sealed class RoomDirectoryClient(IPublicNetwork network, IReadOnlyList<string>? ips = null, int port = PublicNetworkProfile.DirectoryPort)
{
    /// <summary>This Jay's version, sent with every request so a network can turn away clients older than its policy.minVersion.</summary>
    public string Version { get; init; } = Launch.UpdateChecker.CurrentVersion;

    private async Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct)
    {
        if (ips is { Count: > 0 }) return ips;
        try
        {
            var online = await network.DirectoriesAsync(ct);
            return online.Count > 0 ? online : [PublicNetworkProfile.DirectoryIp];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return [PublicNetworkProfile.DirectoryIp]; }
    }

    /// <summary>
    /// The directory keys to insist on. Defaults to the network's pinned keys (<see cref="PublicNetworkProfile.DirectoryKeys"/>).
    /// When there are any, every request goes through <see cref="DirectoryChannel"/> and a directory that can't prove it
    /// holds one of them gets nothing; there is no fallback to the legacy protocol.
    /// </summary>
    public IReadOnlyList<byte[]> PinnedKeys { get; init; } = network.Profile?.DirectoryKeys ?? [];

    /// <summary>Root keys that certify directory keys (<see cref="PublicNetworkProfile.DirectoryRoots"/>). Same rule as pinned keys.</summary>
    public IReadOnlyList<byte[]> RootKeys { get; init; } = network.Profile?.DirectoryRoots ?? [];

    /// <summary>
    /// Where to remember directory keys of networks that pin nothing (trust on first use). Null: such networks use the
    /// legacy protocol as before.
    /// </summary>
    public DirectoryKeyMemory? KeyMemory { get; init; }

    /// <summary>The network's EasyTier secret, for the channel's psk. Defaults to the network's.</summary>
    public string NetworkSecret { get; init; } = network.Profile?.Secret ?? "";

    private DirectoryTrust Trust => new()
    {
        Network = network.Profile?.NetworkName ?? "",
        Pinned = PinnedKeys,
        Roots = RootKeys,
        Memory = KeyMemory,
    };

    private async Task<JsonObject> CallAsync(string ip, JsonObject request, CancellationToken ct)
    {
        var trust = Trust;
        request["jay"] = Version;
        // Pinned keys, roots, or a key already remembered for this address: authenticated only.
        if (trust.IsStrict || trust.Remembers(ip)) return await CallOnceAsync(ip, request, trust, ct);
        if (trust.Memory is not null)
        {
            // First contact with a network that pins nothing: try the authenticated channel and remember the key; a
            // directory too old for it closes the connection (on Windows often as a reset), and only then does Jay fall
            // back to the legacy protocol.
            try { return await CallOnceAsync(ip, request, trust, ct); }
            catch (Exception e) when (ClosedByOldDirectory(e) && !trust.Remembers(ip)) { }
        }
        return await CallOnceAsync(ip, request, null, ct);
    }

    private static bool ClosedByOldDirectory(Exception e) =>
        e is EndOfStreamException or InvalidDataException || e is IOException { InnerException: SocketException } and not DirectoryImpostorException and not DirectoryKeyChangedException;

    private async Task<JsonObject> CallOnceAsync(string ip, JsonObject request, DirectoryTrust? trust, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try
        {
            await using var stream = await network.ConnectAsync(ip, port, deadline.Token);
            if (trust is not null) return await DirectoryChannel.CallAsync(stream, trust, ip, NetworkSecret, request, deadline.Token);
            await RoomDirectoryProtocol.WriteLineAsync(stream, request, deadline.Token);
            return await RoomDirectoryProtocol.ReadLineAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(L.T("core.mp.public.no_directory"));
        }
        catch (SocketException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>发给每个目录，返回每个的回复（连不上的是异常）。</summary>
    private async Task<List<(JsonObject? Reply, Exception? Error)>> FanOutAsync(JsonObject request, CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        var tasks = targets.Select(async ip =>
        {
            try { return ((JsonObject?)await CallAsync(ip, (JsonObject)request.DeepClone(), ct), (Exception?)null); }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { return (null, e); }
        }).ToList();
        var results = (await Task.WhenAll(tasks)).ToList();
        ct.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>至少一个目录收下就算成功；都不收时，有明确拒绝就报拒绝，否则报连不上。</summary>
    private static List<JsonObject> NeedOne(List<(JsonObject? Reply, Exception? Error)> results)
    {
        var ok = results.Where(r => r.Reply?["ok"]?.GetValue<bool>() == true).Select(r => r.Reply!).ToList();
        if (ok.Count > 0) return ok;
        if (results.FirstOrDefault(r => r.Reply is not null).Reply is { } rejected) Check(rejected);
        throw results.Select(r => r.Error).OfType<Exception>().FirstOrDefault() ?? new IOException(L.T("core.mp.public.no_directory"));
    }

    /// <summary>按顺序问，第一个成功的回复（<paramref name="accept"/> 决定算不算数，不算就问下一个）。</summary>
    private async Task<JsonObject> FirstAsync(JsonObject request, Func<JsonObject, bool> accept, CancellationToken ct)
    {
        JsonObject? last = null;
        Exception? error = null;
        foreach (var ip in await TargetsAsync(ct))
        {
            try
            {
                var reply = await CallAsync(ip, (JsonObject)request.DeepClone(), ct);
                if (accept(reply)) return reply;
                // 都没有结果时，回一个“成功但没找到”比回别的目录的错误更准。
                if (last is null || (!IsOk(last) && IsOk(reply))) last = reply;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { error ??= e; }
        }
        return last ?? throw error ?? new IOException(L.T("core.mp.public.no_directory"));
    }

    private static bool IsOk(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;

    private static void Check(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == true) return;
        var code = reply["error"]?.GetValue<string>() ?? "";
        // 网络节点自己的审核规则拒绝时会带一句原因（节点写的，只当文字显示）。
        var reason = reply["reason"]?.GetValue<string>() is { } r ? RoomChat.Clean(r, DirectoryExtensions.MaxReason) : null;
        throw new DirectoryRejectedException(code, code switch
        {
            "owner" or "lookup" or "stale" => L.T("core.mp.public.err_owner"),
            "full" => L.T("core.mp.public.err_full"),
            "gone" => L.T("core.mp.public.err_gone"),
            "rate" => L.T("core.mp.public.err_rate"),
            "version" => L.F("core.mp.public.err_version", reply["min"]?.GetValue<string>() is { Length: <= 32 } min ? min : "?"),
            "login" => L.T("core.mp.public.err_login"),
            "off" => L.T("core.mp.public.volunteer_off"),
            _ when !string.IsNullOrWhiteSpace(reason) => L.F("core.mp.public.err_policy", reason),
            _ => L.F("core.mp.public.err_other", code),
        });
    }

    /// <summary>公开列表：合并所有在线目录的结果，同一个房间取序号最大的版本。</summary>
    public async Task<IReadOnlyList<RoomListing>> ListAsync(CancellationToken ct)
    {
        var replies = NeedOne(await FanOutAsync(new JsonObject { ["op"] = "list" }, ct));
        var rooms = new Dictionary<string, RoomListing>(StringComparer.Ordinal);
        foreach (var reply in replies)
            foreach (var room in (reply["rooms"] as JsonArray ?? []).Select(RoomListing.FromJson).OfType<RoomListing>())
                if (!rooms.TryGetValue(room.Id, out var have) || room.Seq > have.Seq) rooms[room.Id] = room;
        return [.. rooms.Values.OrderByDescending(r => r.Players).ThenByDescending(r => r.Updated).Take(RoomDirectoryServer.MaxListed)];
    }

    /// <summary>发布或续期（发给每个在线的目录）。返回目录里给房主捎的话（直连回复码）。</summary>
    /// <param name="listing">已经用 <see cref="RoomSigner.Sign"/> 签好的房间信息。</param>
    /// <param name="searchKey">本机的指纹密钥：给了就能用这台电脑的指纹码找到这个房间。</param>
    /// <param name="account">This network's login token, if logged in: lets networks that require login accept the room and tie it to the account.</param>
    public async Task<IReadOnlyList<string>> AnnounceAsync(RoomListing listing, bool listed, IEnumerable<string> lookupKeys, CancellationToken ct,
        byte[]? searchKey = null, string? account = null)
    {
        var request = new JsonObject
        {
            ["op"] = "announce",
            ["room"] = listing.ToJson(),
            ["listed"] = listed,
            ["lookup"] = new JsonArray(lookupKeys.Select(k => (JsonNode?)k).ToArray()),
        };
        if (searchKey is not null) request["fp"] = Convert.ToHexStringLower(searchKey);
        if (account is not null) request["account"] = account;
        var replies = NeedOne(await FanOutAsync(request, ct));
        return [.. replies.SelectMany(r => (r["mail"] as JsonArray ?? []).Select(m => m?.GetValue<string>()).OfType<string>()).Distinct()];
    }

    /// <summary>
    /// Registers (or renews) this computer as a volunteer node with every online directory. Returns whether at least
    /// one directory lists it already (the first registration is probed first, so it lists on the next renewal).
    /// </summary>
    public async Task<bool> VolunteerAsync(string url, CancellationToken ct)
    {
        var address = VolunteerNodes.Normalize(url) ?? throw new ArgumentException(L.T("core.mp.public.volunteer_bad_address"));
        List<JsonObject> replies;
        try { replies = NeedOne(await FanOutAsync(new JsonObject { ["op"] = "volunteer", ["url"] = address }, ct)); }
        catch (DirectoryRejectedException e) when (e.Code == "op")
        {
            // Directories from before volunteer nodes existed.
            throw new DirectoryRejectedException("off", L.T("core.mp.public.volunteer_off"));
        }
        return replies.Any(r => r["listed"]?.GetValue<bool>() == true);
    }

    /// <summary>Volunteer nodes the directories know (merged, public addresses only). Empty when the network takes none.</summary>
    /// <summary>
    /// Asks a directory to dial <paramref name="url"/> now: true when it got through. Use it to test port forwarding
    /// before volunteering (something must be listening on the port during the test).
    /// </summary>
    public async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        var address = VolunteerNodes.Normalize(url) ?? throw new ArgumentException(L.T("core.mp.public.volunteer_bad_address"));
        var reply = await FirstAsync(new JsonObject { ["op"] = "probe", ["url"] = address }, IsOk, ct);
        if (!IsOk(reply) && reply["error"]?.GetValue<string>() == "op") throw new DirectoryRejectedException("off", L.T("core.mp.public.volunteer_off"));
        Check(reply);
        return reply["reachable"]?.GetValue<bool>() == true;
    }

    public async Task<IReadOnlyList<string>> NodesAsync(CancellationToken ct)
    {
        var results = await FanOutAsync(new JsonObject { ["op"] = "nodes" }, ct);
        return [.. results.Select(r => r.Reply).OfType<JsonObject>().Where(IsOk)
            .SelectMany(r => (r["nodes"] as JsonArray ?? []).Select(n => n?.GetValue<string>()))
            .Select(VolunteerNodes.Normalize).OfType<string>().Distinct().Take(VolunteerNodes.PerRequest * 2)];
    }

    public async Task WithdrawAsync(RoomSigner signer, CancellationToken ct)
    {
        var (seq, sig) = signer.SignWithdraw(DateTimeOffset.UtcNow);
        NeedOne(await FanOutAsync(new JsonObject { ["op"] = "withdraw", ["id"] = signer.RoomId, ["seq"] = seq, ["sig"] = Convert.ToHexStringLower(sig) }, ct));
    }

    /// <summary>按邀请码里的大厅密钥找房主；目录里没有时返回 null。</summary>
    public async Task<DirectoryHit?> LookupAsync(ReadOnlyMemory<byte> lobbyKey, CancellationToken ct)
    {
        var reply = await FirstAsync(new JsonObject { ["op"] = "lookup", ["key"] = RoomDirectoryProtocol.LookupKey(lobbyKey.Span) },
            r => IsOk(r) && r["ip"] is not null, ct);
        Check(reply);
        var ip = reply["ip"]?.GetValue<string>();
        var key = reply["key"]?.GetValue<string>();
        var id = reply["id"]?.GetValue<string>();
        if (ip is null || key is not { Length: 64 } || id is null || !RoomListing.IsPublicNetworkIp(ip)) return null;
        try { return new DirectoryHit(id, ip, Convert.FromHexString(key)); }
        catch (FormatException) { return null; }
    }

    /// <summary>按设备指纹码找房间；没找到返回 null。</summary>
    public async Task<RoomListing?> FindAsync(string code, CancellationToken ct)
    {
        var reply = await FirstAsync(new JsonObject { ["op"] = "find", ["code"] = code }, r => IsOk(r) && r["room"] is not null, ct);
        Check(reply);
        return RoomListing.FromJson(reply["room"]);
    }

    /// <summary>登录公共网络的账号：账号密码封给节点的登录公钥，回来的凭证绑定自己的联机身份。交给第一个开了登录的目录节点。</summary>
    public async Task<(string Token, AccountToken Parsed)> LoginAsync(X25519KeyPair me, NetworkLogin login, string network, string user, string password,
        CancellationToken ct, IReadOnlyDictionary<string, string>? factors = null)
    {
        // 每个目录节点各算一次（请求里有时间戳，换个节点重发也一样）；没开登录的节点回 op，问下一个。
        var request = NetworkLoginClient.Request(me, login, user, password, DateTimeOffset.UtcNow, factors);
        var reply = await FirstAsync(request, r => r["error"]?.GetValue<string>() != "op", ct);
        return NetworkLoginClient.Read(reply, me, login, network, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Leaves sealed mail in a mailbox at every directory that takes it (so one directory that drops it can't make it
    /// disappear). Throws when none took it.
    /// </summary>
    public async Task PostMailAsync(string box, byte[] blob, string dropHash, CancellationToken ct) =>
        NeedOne(await FanOutAsync(new JsonObject { ["op"] = "post", ["box"] = box, ["blob"] = Convert.ToBase64String(blob), ["drop"] = dropHash }, ct));

    /// <summary>Most pages read from one directory per fetch (a flooded mailbox can't keep Jay busy forever).</summary>
    public const int MaxFetchPages = 16;

    /// <summary>Everything waiting in these mailboxes at any directory, each blob once. Throws when no directory answered.</summary>
    public async Task<IReadOnlyList<(string Box, byte[] Blob)>> FetchMailAsync(IReadOnlyList<string> boxes, CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        var pages = await Task.WhenAll(targets.Select(async ip =>
        {
            var replies = new List<JsonObject>();
            Exception? error = null;
            var from = 0;
            try
            {
                for (var page = 0; page < MaxFetchPages; page++)
                {
                    var request = new JsonObject
                    {
                        ["op"] = "fetch",
                        ["boxes"] = new JsonArray([.. boxes.Select(b => (JsonNode)JsonValue.Create(b))]),
                        ["from"] = from,
                    };
                    var reply = await CallAsync(ip, request, ct);
                    replies.Add(reply);
                    if (!IsOk(reply) || reply["next"]?.GetValue<int>() is not { } next || next <= from) break;
                    from = next;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or InvalidOperationException or FormatException) { error = e; }
            return (replies, error);
        }));
        ct.ThrowIfCancellationRequested();
        NeedOne([.. pages.Select(p => (p.replies.FirstOrDefault(), p.replies.Count > 0 ? null : p.error))]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string, byte[])>();
        foreach (var item in pages.SelectMany(p => p.replies).Where(IsOk).SelectMany(r => (r["items"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            var box = item["box"]?.GetValue<string>();
            var blob = item["blob"]?.GetValue<string>();
            if (box is null || blob is null || !boxes.Contains(box) || !seen.Add(blob)) continue;
            try
            {
                var bytes = Convert.FromBase64String(blob);
                if (bytes.Length <= Contacts.SealedMail.MaxBlob) result.Add((box, bytes));
            }
            catch (FormatException) { }
        }
        return result;
    }

    /// <summary>Deletes mail this Jay has read, at every directory (best effort: it expires anyway).</summary>
    public async Task DropMailAsync(string box, byte[] token, CancellationToken ct) =>
        await FanOutAsync(new JsonObject { ["op"] = "drop", ["box"] = box, ["token"] = Convert.ToHexStringLower(token) }, ct);

    /// <summary>给直连房间的房主捎话：交给第一个肯收的目录（房主续期时会从每个目录取一遍）。</summary>
    public async Task MailAsync(string id, string text, CancellationToken ct) =>
        Check(await FirstAsync(new JsonObject { ["op"] = "mail", ["id"] = id, ["text"] = text }, IsOk, ct));
}

/// <summary>目录服务拒绝了请求（口令不对、满了、房间不在了）。</summary>
public sealed class DirectoryRejectedException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
