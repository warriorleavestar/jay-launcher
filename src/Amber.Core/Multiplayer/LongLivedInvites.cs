using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>A long-lived invite for a trusted friend: a name to recognise it by, its token and when it stops working.</summary>
public sealed record LongLivedInvite(string Id, string Label, byte[] Token, DateTimeOffset Created, DateTimeOffset Expires)
{
    public bool IsActive(DateTimeOffset now) => now < Expires;
}

/// <summary>
/// Long-lived invites for friends you play with regularly (official §6.15, open question 14): 30 days by default, revoke
/// any time, and the host still approves each knock and checks the safety code. They share one lobby key kept on this
/// computer (in a file only this user can read), so the same code works for every room opened while it is valid.
/// When the last one is revoked or expires, the key is thrown away: old codes then lead nowhere.
/// </summary>
public sealed class LongLivedInvites
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);
    public const int MaxInvites = 50;
    public const int MaxLabel = 40;

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<LongLivedInvite> _items = [];
    private byte[]? _lobbyKey;

    public LongLivedInvites(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    public event Action? Changed;

    /// <summary>The invites still valid, newest first.</summary>
    public IReadOnlyList<LongLivedInvite> Active
    {
        get
        {
            var now = _now();
            return [.. _items.Where(i => i.IsActive(now)).OrderByDescending(i => i.Created)];
        }
    }

    public LongLivedInvite Create(string label, TimeSpan? lifetime = null)
    {
        Prune();
        if (_items.Count >= MaxInvites) throw new InvalidOperationException("too many long-lived invites");
        var life = lifetime ?? DefaultLifetime;
        if (life <= TimeSpan.Zero || life > MaxLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
        _lobbyKey ??= RandomNumberGenerator.GetBytes(JayInvite.KeyLength);
        var clean = new string([.. (label ?? "").Where(c => !char.IsControl(c)).Take(MaxLabel)]).Trim();
        var now = _now();
        var invite = new LongLivedInvite(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), clean, RandomNumberGenerator.GetBytes(JayInvite.KeyLength), now, now + life);
        _items.Add(invite);
        Save();
        Changed?.Invoke();
        return invite;
    }

    /// <summary>The code to give the friend (the room's game is added when a room is open).</summary>
    public JayInvite CodeOf(LongLivedInvite invite, TunnelSettings tunnel, InviteGame? game = null) =>
        _lobbyKey is { } key ? new JayInvite((byte[])key.Clone(), (byte[])invite.Token.Clone(), tunnel, game) : throw new InvalidOperationException();

    public void Revoke(string id)
    {
        if (_items.RemoveAll(i => i.Id == id) == 0) return;
        Prune();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Put the valid invites into a new room's invite book (before any other invite). Returns how many went in.</summary>
    public int SeedInto(InviteBook book, TunnelSettings tunnel)
    {
        Prune();
        if (_lobbyKey is not { } key) return 0;
        var added = 0;
        foreach (var invite in Active)
            if (book.AddLongLived(key, invite.Token, invite.Expires, tunnel) is not null) added++;
        return added;
    }

    /// <summary>Expired ones go; with none left, the lobby key goes too.</summary>
    private void Prune()
    {
        var now = _now();
        var removed = _items.RemoveAll(i => !i.IsActive(now));
        if (_items.Count == 0 && _lobbyKey is { } key)
        {
            CryptographicOperations.ZeroMemory(key);
            _lobbyKey = null;
            removed++;
        }
        if (removed > 0) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root) return;
            var key = Convert.FromBase64String(root["lobbyKey"]?.GetValue<string>() ?? "");
            if (key.Length != JayInvite.KeyLength) return;
            foreach (var node in root["invites"]?.AsArray() ?? [])
            {
                if (node is not JsonObject o) continue;
                var token = Convert.FromBase64String(o["token"]?.GetValue<string>() ?? "");
                if (token.Length != JayInvite.KeyLength) continue;
                _items.Add(new LongLivedInvite(o["id"]?.GetValue<string>() ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                    o["label"]?.GetValue<string>() ?? "", token,
                    DateTimeOffset.FromUnixTimeSeconds(o["created"]?.GetValue<long>() ?? 0),
                    DateTimeOffset.FromUnixTimeSeconds(o["expires"]?.GetValue<long>() ?? 0)));
            }
            _lobbyKey = key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            _items.Clear();
            _lobbyKey = null;
        }
        Prune();
    }

    private void Save()
    {
        try
        {
            if (_lobbyKey is null || _items.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            var root = new JsonObject
            {
                ["lobbyKey"] = Convert.ToBase64String(_lobbyKey),
                ["invites"] = new JsonArray([.. _items.Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id,
                    ["label"] = i.Label,
                    ["token"] = Convert.ToBase64String(i.Token),
                    ["created"] = i.Created.ToUnixTimeSeconds(),
                    ["expires"] = i.Expires.ToUnixTimeSeconds(),
                })]),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
