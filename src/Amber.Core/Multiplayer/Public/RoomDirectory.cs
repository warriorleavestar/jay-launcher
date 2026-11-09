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
/// </summary>
public static class RoomDirectoryProtocol
{
    public const int MaxLine = 16 * 1024;
    public const int MaxLookupKeys = 4;
    public const int MaxMailLength = 4096;
    public const int MaxMailPerRoom = 16;
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

    internal static async Task<JsonObject> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxLine];
        var length = 0;
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
    }

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
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public int Port { get; }

    /// <summary>账号密码登录（jay-server directory --accounts 时才有）。</summary>
    public LoginAuthority? Login { get; set; }

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
        get { lock (_gate) { Prune(); return _rooms.Count; } }
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
            var request = await RoomDirectoryProtocol.ReadLineAsync(stream, deadline.Token);
            var reply = Handle(request);
            await RoomDirectoryProtocol.WriteLineAsync(stream, reply, deadline.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or EndOfStreamException) { }
    }

    /// <summary>处理一个请求（测试直接调）。</summary>
    internal JsonObject Handle(JsonObject request)
    {
        try
        {
            return (request["op"]?.GetValue<string>()) switch
            {
                "announce" => Announce(request),
                "withdraw" => Withdraw(request),
                "list" => List(),
                "lookup" => Lookup(request),
                "mail" => Mail(request),
                "find" => Find(request),
                "login" when Login is { } login => login.Handle(request),
                _ => Error("op"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or JsonException)
        {
            return Error("bad");
        }
    }

    private static JsonObject Error(string code) => new() { ["ok"] = false, ["error"] = code };

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
            foreach (var old in entry.Lookup) _lookup.Remove(old);
            foreach (var key in keys) _lookup[key] = listing.Id;
            entry.Lookup = keys;
            if (entry.Search is { } oldSearch) _search.Remove(Convert.ToHexStringLower(oldSearch));
            entry.Search = fp is null ? null : Convert.FromHexString(fp);
            if (fp is not null) _search[fp] = listing.Id;
            entry.Listing = listing with { Updated = _now() };
            entry.Listed = listed;
            entry.Expires = _now() + RoomDirectoryProtocol.Ttl;
            _rooms[listing.Id] = entry;
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
            RemoveLocked(id, entry);
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

    private void Prune()
    {
        var now = _now();
        foreach (var (id, entry) in _rooms.Where(p => p.Value.Expires <= now).ToList()) RemoveLocked(id, entry);
    }

    private void RemoveLocked(string id, Entry entry)
    {
        _rooms.Remove(id);
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

/// <summary>目录服务的客户端：经公共网络连 10.235.0.1:47668。</summary>
public sealed class RoomDirectoryClient(IPublicNetwork network, string ip = PublicNetworkProfile.DirectoryIp, int port = PublicNetworkProfile.DirectoryPort)
{
    private async Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        await using var stream = await network.ConnectAsync(ip, port, deadline.Token);
        await RoomDirectoryProtocol.WriteLineAsync(stream, request, deadline.Token);
        return await RoomDirectoryProtocol.ReadLineAsync(stream, deadline.Token);
    }

    private static void Check(JsonObject reply)
    {
        if (reply["ok"]?.GetValue<bool>() == true) return;
        var code = reply["error"]?.GetValue<string>() ?? "";
        throw new DirectoryRejectedException(code, code switch
        {
            "owner" or "lookup" or "stale" => L.T("core.mp.public.err_owner"),
            "full" => L.T("core.mp.public.err_full"),
            "gone" => L.T("core.mp.public.err_gone"),
            _ => L.F("core.mp.public.err_other", code),
        });
    }

    public async Task<IReadOnlyList<RoomListing>> ListAsync(CancellationToken ct)
    {
        var reply = await CallAsync(new JsonObject { ["op"] = "list" }, ct);
        Check(reply);
        return [.. (reply["rooms"] as JsonArray ?? []).Select(RoomListing.FromJson).OfType<RoomListing>()];
    }

    /// <summary>发布或续期。返回目录里给房主捎的话（直连回复码）。</summary>
    /// <param name="listing">已经用 <see cref="RoomSigner.Sign"/> 签好的房间信息。</param>
    /// <param name="searchKey">本机的指纹密钥：给了就能用这台电脑的指纹码找到这个房间。</param>
    public async Task<IReadOnlyList<string>> AnnounceAsync(RoomListing listing, bool listed, IEnumerable<string> lookupKeys, CancellationToken ct,
        byte[]? searchKey = null)
    {
        var request = new JsonObject
        {
            ["op"] = "announce",
            ["room"] = listing.ToJson(),
            ["listed"] = listed,
            ["lookup"] = new JsonArray(lookupKeys.Select(k => (JsonNode?)k).ToArray()),
        };
        if (searchKey is not null) request["fp"] = Convert.ToHexStringLower(searchKey);
        var reply = await CallAsync(request, ct);
        Check(reply);
        return [.. (reply["mail"] as JsonArray ?? []).Select(m => m?.GetValue<string>()).OfType<string>()];
    }

    public async Task WithdrawAsync(RoomSigner signer, CancellationToken ct)
    {
        var (seq, sig) = signer.SignWithdraw(DateTimeOffset.UtcNow);
        Check(await CallAsync(new JsonObject { ["op"] = "withdraw", ["id"] = signer.RoomId, ["seq"] = seq, ["sig"] = Convert.ToHexStringLower(sig) }, ct));
    }

    /// <summary>按邀请码里的大厅密钥找房主；目录里没有时返回 null。</summary>
    public async Task<DirectoryHit?> LookupAsync(ReadOnlyMemory<byte> lobbyKey, CancellationToken ct)
    {
        var reply = await CallAsync(new JsonObject { ["op"] = "lookup", ["key"] = RoomDirectoryProtocol.LookupKey(lobbyKey.Span) }, ct);
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
        var reply = await CallAsync(new JsonObject { ["op"] = "find", ["code"] = code }, ct);
        Check(reply);
        return RoomListing.FromJson(reply["room"]);
    }

    /// <summary>登录公共网络的账号：账号密码封给节点的登录公钥，回来的凭证绑定自己的联机身份。</summary>
    public async Task<(string Token, AccountToken Parsed)> LoginAsync(X25519KeyPair me, NetworkLogin login, string network, string user, string password,
        CancellationToken ct, IReadOnlyDictionary<string, string>? factors = null)
    {
        var reply = await CallAsync(NetworkLoginClient.Request(me, login, user, password, DateTimeOffset.UtcNow, factors), ct);
        return NetworkLoginClient.Read(reply, me, login, network, DateTimeOffset.UtcNow);
    }

    public async Task MailAsync(string id, string text, CancellationToken ct) =>
        Check(await CallAsync(new JsonObject { ["op"] = "mail", ["id"] = id, ["text"] = text }, ct));
}

/// <summary>目录服务拒绝了请求（口令不对、满了、房间不在了）。</summary>
public sealed class DirectoryRejectedException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
