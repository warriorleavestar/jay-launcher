using System.Text.Json;
using System.Text.Json.Nodes;
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
            : RoomVerdict.Reject("mode", "这个网络不允许这种房间方式");
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
        return _words.Any(text.Contains) ? RoomVerdict.Reject("name", "房间名或房主名里有这个网络不允许的词") : RoomVerdict.Allow;
    }
}

/// <summary>
/// 封禁名单：按房主的联机身份指纹（换房间也跑不掉，除非他换一个联机身份）或房间编号封。
/// 存在节点数据目录的一个 JSON 文件里，改了马上生效。
/// </summary>
public sealed class BanList : IRoomGate
{
    public sealed record Ban(string Kind, string Value, string? Reason, DateTimeOffset At);

    private readonly string? _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Ban> _bans = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="path">存哪里；null 只放在内存里（测试或者自己管存储）。</param>
    public BanList(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException(path);
        foreach (var item in o["bans"] as JsonArray ?? [])
            if (item is JsonObject b && b["kind"]?.GetValue<string>() is "host" or "room" && b["value"]?.GetValue<string>() is { Length: > 0 } value)
            {
                var kind = b["kind"]!.GetValue<string>();
                _bans[kind + ":" + value] = new Ban(kind, value, b["reason"]?.GetValue<string>(),
                    DateTimeOffset.FromUnixTimeSeconds(b["at"]?.GetValue<long>() ?? 0));
            }
    }

    public IReadOnlyList<Ban> List()
    {
        lock (_gate) return [.. _bans.Values.OrderBy(b => b.At)];
    }

    /// <summary>封一个房主（<paramref name="kind"/> = host，值是联机身份指纹）或房间（room，值是房间编号）。</summary>
    public void Add(string kind, string value, string? reason, DateTimeOffset now)
    {
        Validate(kind, value);
        lock (_gate)
        {
            _bans[kind + ":" + value.Trim()] = new Ban(kind, value.Trim(), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), now);
            Save();
        }
    }

    public bool Remove(string kind, string value)
    {
        Validate(kind, value);
        lock (_gate)
        {
            if (!_bans.Remove(kind + ":" + value.Trim())) return false;
            Save();
            return true;
        }
    }

    private static void Validate(string kind, string value)
    {
        if (kind is not ("host" or "room")) throw new ArgumentException("只能封 host（房主指纹）或 room（房间编号）");
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128) throw new ArgumentException("要给指纹或房间编号");
    }

    private void Save()
    {
        if (_path is null) return;
        var array = new JsonArray([.. _bans.Values.Select(b => (JsonNode)new JsonObject
        {
            ["kind"] = b.Kind, ["value"] = b.Value, ["reason"] = b.Reason, ["at"] = b.At.ToUnixTimeSeconds(),
        })]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["v"] = 1, ["bans"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    public bool IsBanned(RoomListing listing)
    {
        lock (_gate) return _bans.ContainsKey("room:" + listing.Id) || _bans.ContainsKey("host:" + listing.HostFingerprint);
    }

    public RoomVerdict Check(RoomAnnouncement a) =>
        IsBanned(a.Listing) ? RoomVerdict.Reject("banned", "你被这个网络的管理员封禁了") : RoomVerdict.Allow;
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
                return RoomVerdict.Reject("rate", $"每人最多同时开 {maxRoomsPerHost} 个房间");
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
