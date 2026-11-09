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
            var request = await RoomDirectoryProtocol.ReadLineAsync(stream, deadline.Token);
            var reply = Handle(request);
            await RoomDirectoryProtocol.WriteLineAsync(stream, reply, deadline.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or SocketException or EndOfStreamException) { }
    }

    /// <summary>处理一个请求（测试和框架的宿主直接调）。</summary>
    public JsonObject Handle(JsonObject request)
    {
        try
        {
            var op = request["op"]?.GetValue<string>();
            return op switch
            {
                "announce" => Announce(request),
                "withdraw" => Withdraw(request),
                "list" => List(),
                "lookup" => Lookup(request),
                "mail" => Mail(request),
                "find" => Find(request),
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
                        e.Listing.Players, e.Listing.Game, e.Listing.Updated))];
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
            var announcement = new RoomAnnouncement(listing, listed, isNew, fp is not null);
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

    private async Task<JsonObject> CallAsync(string ip, JsonObject request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RoomDirectoryProtocol.RequestTimeout);
        try
        {
            await using var stream = await network.ConnectAsync(ip, port, deadline.Token);
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
        var replies = NeedOne(await FanOutAsync(request, ct));
        return [.. replies.SelectMany(r => (r["mail"] as JsonArray ?? []).Select(m => m?.GetValue<string>()).OfType<string>()).Distinct()];
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

    /// <summary>给直连房间的房主捎话：交给第一个肯收的目录（房主续期时会从每个目录取一遍）。</summary>
    public async Task MailAsync(string id, string text, CancellationToken ct) =>
        Check(await FirstAsync(new JsonObject { ["op"] = "mail", ["id"] = id, ["text"] = text }, IsOk, ct));
}

/// <summary>目录服务拒绝了请求（口令不对、满了、房间不在了）。</summary>
public sealed class DirectoryRejectedException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
