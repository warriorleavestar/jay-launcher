using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Public;

namespace Amber.NetworkServer;

/// <summary>
/// 按网络配置的 <c>policy.modes</c> 在节点这边也挡一道：Jay 本身就不让开不允许的方式，
/// 这条规则防的是改过的客户端绕过去。隐藏房间不进列表，总是放行。
/// </summary>
public sealed class PolicyGate(NetworkPolicy policy) : IRoomGate
{
    public RoomVerdict Check(RoomAnnouncement a) =>
        a.Listing.Mode == RoomVisibility.Hidden || !a.Listed || policy.Allows(a.Listing.Mode)
            ? RoomVerdict.Allow
            : RoomVerdict.Reject("mode", L.T("server.gate.mode"));
}

/// <summary>屏蔽词：房间名或房主名里有这些词的不收（不分大小写，忽略空格）。只管进公开列表的房间。</summary>
public sealed class NameFilterGate : IRoomGate
{
    private readonly string[] _words;

    public NameFilterGate(IEnumerable<string> words) =>
        _words = [.. words.Select(Normalize).Where(w => w.Length > 0).Distinct()];

    /// <summary>从文本文件读，一行一个词，# 开头是注释。</summary>
    public static NameFilterGate FromFile(string path) =>
        new(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));

    public int Count => _words.Length;

    private static string Normalize(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant)]);

    public RoomVerdict Check(RoomAnnouncement a)
    {
        if (!a.Listed || a.Listing.Mode == RoomVisibility.Hidden) return RoomVerdict.Allow;
        var text = Normalize(a.Listing.Name + "\n" + a.Listing.Host);
        return _words.Any(text.Contains) ? RoomVerdict.Reject("name", L.T("server.gate.name")) : RoomVerdict.Allow;
    }
}

/// <summary>
/// The node's ban list. A ban targets a host identity (host fingerprint, survives new rooms), a room id, or a network
/// account (survives a new identity when the network requires login to publish). Bans can expire. The list lives in a JSON
/// file that is re-read when it changes on disk, so backup directory nodes can share it (rsync, a shared volume, ...).
/// </summary>
public sealed class BanList : IRoomGate
{
    public static readonly string[] Kinds = ["host", "room", "account"];

    /// <param name="Until">When the ban ends; null = permanent.</param>
    public sealed record Ban(string Kind, string Value, string? Reason, DateTimeOffset At, DateTimeOffset? Until = null)
    {
        public bool ActiveAt(DateTimeOffset now) => Until is not { } until || until > now;
    }

    private readonly string? _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private Dictionary<string, Ban> _bans = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedStamp;
    private DateTimeOffset _checkedAt;

    /// <param name="path">Where to keep the list; null keeps it in memory only (tests, or you store it yourself).</param>
    public BanList(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        lock (_gate) Load();
    }

    private static string Key(string kind, string value) => kind + ":" + value;

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        var stamp = File.GetLastWriteTimeUtc(_path);
        var bans = new Dictionary<string, Ban>(StringComparer.OrdinalIgnoreCase);
        var o = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? throw new InvalidDataException(_path);
        foreach (var item in o["bans"] as JsonArray ?? [])
            if (item is JsonObject b && b["kind"]?.GetValue<string>() is { } kind && Kinds.Contains(kind) && b["value"]?.GetValue<string>() is { Length: > 0 } value)
                bans[Key(kind, value)] = new Ban(kind, value, b["reason"]?.GetValue<string>(),
                    DateTimeOffset.FromUnixTimeSeconds(b["at"]?.GetValue<long>() ?? 0),
                    b["until"]?.GetValue<long>() is { } until ? DateTimeOffset.FromUnixTimeSeconds(until) : null);
        _bans = bans;
        _loadedStamp = stamp;
    }

    /// <summary>Picks up edits made by someone else (another node syncing the file) at most every 5 seconds.</summary>
    private void Refresh()
    {
        if (_path is null) return;
        var now = _now();
        if (now - _checkedAt < TimeSpan.FromSeconds(5)) return;
        _checkedAt = now;
        try
        {
            if (File.Exists(_path) && File.GetLastWriteTimeUtc(_path) != _loadedStamp) Load();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { /* keep the last good list */ }
    }

    /// <summary>Bans still in force, oldest first.</summary>
    public IReadOnlyList<Ban> List()
    {
        lock (_gate)
        {
            Refresh();
            var now = _now();
            return [.. _bans.Values.Where(b => b.ActiveAt(now)).OrderBy(b => b.At)];
        }
    }

    /// <summary>Bans a host (kind host, value = identity fingerprint), a room (room, room id) or an account (account, account name).</summary>
    /// <param name="duration">How long; null = permanent.</param>
    public Ban Add(string kind, string value, string? reason, DateTimeOffset now, TimeSpan? duration = null)
    {
        Validate(kind, value);
        if (duration is { } d && d <= TimeSpan.Zero) throw new ArgumentException("ban duration must be positive");
        var ban = new Ban(kind, value.Trim(), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), now, now + duration);
        lock (_gate)
        {
            Refresh();
            _bans[Key(kind, ban.Value)] = ban;
            Save();
        }
        return ban;
    }

    public bool Remove(string kind, string value)
    {
        Validate(kind, value);
        lock (_gate)
        {
            Refresh();
            if (!_bans.Remove(Key(kind, value.Trim()))) return false;
            Save();
            return true;
        }
    }

    private static void Validate(string kind, string value)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("ban kind must be host, room or account");
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128) throw new ArgumentException("missing fingerprint, room id or account");
    }

    private void Save()
    {
        var now = _now();
        // Expired bans are dropped on every save.
        foreach (var key in _bans.Where(p => !p.Value.ActiveAt(now)).Select(p => p.Key).ToList()) _bans.Remove(key);
        if (_path is null) return;
        var array = new JsonArray([.. _bans.Values.Select(b => (JsonNode)new JsonObject
        {
            ["kind"] = b.Kind, ["value"] = b.Value, ["reason"] = b.Reason, ["at"] = b.At.ToUnixTimeSeconds(),
            ["until"] = b.Until?.ToUnixTimeSeconds(),
        })]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["v"] = 1, ["bans"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }

    /// <summary>The ban that applies to this host/room/account, if any.</summary>
    public Ban? Find(RoomListing listing, string? account = null)
    {
        lock (_gate)
        {
            Refresh();
            var now = _now();
            foreach (var key in new[] { Key("host", listing.HostFingerprint), Key("room", listing.Id), account is null ? null : Key("account", account) })
                if (key is not null && _bans.TryGetValue(key, out var ban) && ban.ActiveAt(now)) return ban;
            return null;
        }
    }

    public bool IsBanned(RoomListing listing, string? account = null) => Find(listing, account) is not null;

    /// <summary>Is this account banned (used to refuse its logins)?</summary>
    public bool IsAccountBanned(string account)
    {
        lock (_gate)
        {
            Refresh();
            return _bans.TryGetValue(Key("account", account), out var ban) && ban.ActiveAt(_now());
        }
    }

    public RoomVerdict Check(RoomAnnouncement a)
    {
        if (Find(a.Listing, a.Account) is not { } ban) return RoomVerdict.Allow;
        // The host sees why and until when.
        var text = L.T("server.gate.banned");
        if (ban.Reason is { } reason) text += L.F("server.gate.ban_reason", reason);
        if (ban.Until is { } until) text += L.F("server.gate.ban_until", until.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        return RoomVerdict.Reject("banned", text);
    }
}

/// <summary>
/// 限流：同一个房主（联机身份）最多同时开几个房间，每分钟最多新发布几次（续期不算）。
/// 挡的是一个人刷满列表。换联机身份能绕过去，所以还设了整个目录每分钟新房间的上限。
/// </summary>
public sealed class RateLimitGate(int maxRoomsPerHost = 3, int newPerHostPerMinute = 6, int newPerMinute = 600, Func<DateTimeOffset>? clock = null)
    : IRoomGate, IDirectoryObserver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HashSet<string>> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _recent = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _all = new();

    private static void Trim(Queue<DateTimeOffset> q, DateTimeOffset now)
    {
        while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
    }

    public RoomVerdict Check(RoomAnnouncement a)
    {
        if (!a.IsNew) return RoomVerdict.Allow;
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        var host = a.Listing.HostFingerprint;
        lock (_gate)
        {
            if (_rooms.TryGetValue(host, out var open) && open.Count >= maxRoomsPerHost)
                return RoomVerdict.Reject("rate", L.F("server.gate.max_rooms", maxRoomsPerHost));
            var recent = _recent.TryGetValue(host, out var q) ? q : _recent[host] = new Queue<DateTimeOffset>();
            Trim(recent, now);
            Trim(_all, now);
            if (recent.Count >= newPerHostPerMinute || _all.Count >= newPerMinute) return RoomVerdict.Reject("rate");
            recent.Enqueue(now);
            _all.Enqueue(now);
            // 闲置的计数别越攒越多。
            if (_recent.Count > 10_000)
                foreach (var key in _recent.Where(p => { Trim(p.Value, now); return p.Value.Count == 0; }).Select(p => p.Key).ToList()) _recent.Remove(key);
            return RoomVerdict.Allow;
        }
    }

    void IDirectoryObserver.OnAnnounced(RoomListing listing, bool listed, bool isNew)
    {
        lock (_gate)
        {
            var host = listing.HostFingerprint;
            (_rooms.TryGetValue(host, out var set) ? set : _rooms[host] = []).Add(listing.Id);
            _owner[listing.Id] = host;
        }
    }

    void IDirectoryObserver.OnRemoved(string roomId, RoomRemoval reason)
    {
        lock (_gate)
        {
            if (!_owner.Remove(roomId, out var host) || !_rooms.TryGetValue(host, out var set)) return;
            set.Remove(roomId);
            if (set.Count == 0) _rooms.Remove(host);
        }
    }
}
