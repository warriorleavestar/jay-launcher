using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Multiplayer;

/// <summary>房主信任的人：公钥和当时的名字。</summary>
public sealed record TrustedMember(byte[] PublicKey, string Name)
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);

    /// <summary>
    /// The person's Jay account (jay1…), when they showed a valid certificate at the time. Any device that account certifies
    /// is trusted too, so a trusted friend on a new computer is let in without asking.
    /// </summary>
    public string? JayAccount { get; init; }
}

/// <summary>
/// 房主的信任列表（trusted.json）：只存公钥和名字。列表里的人用任何一张有效邀请敲门都直接批准。
/// 文件坏了当作空列表（不崩）；下次保存前先把坏文件留一份 .broken。读不出来（被占用）时不缓存、不覆盖，下次再读。
/// </summary>
public sealed class TrustedMembers(string path)
{
    private readonly Lock _gate = new();
    private List<TrustedMember>? _members;

    public event Action? Changed;

    public IReadOnlyList<TrustedMember> All
    {
        get { lock (_gate) return [.. Load()]; }
    }

    public bool IsTrusted(ReadOnlySpan<byte> publicKey)
    {
        lock (_gate)
        {
            foreach (var m in Load())
                if (CryptographicOperations.FixedTimeEquals(m.PublicKey, publicKey)) return true;
            return false;
        }
    }

    /// <summary>True when the Jay account was trusted along with one of its devices. Pass only a verified account.</summary>
    public bool IsTrustedJayAccount(string? account)
    {
        if (!Identity.JayAddress.IsValid(account)) return false;
        lock (_gate) return Load().Any(m => m.JayAccount is not null && string.Equals(m.JayAccount, account, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="jayAccount">The person's verified Jay account, if they showed one: their other devices are trusted too.</param>
    public void Add(byte[] publicKey, string name, string? jayAccount = null)
    {
        if (publicKey.Length != 32) throw new ArgumentException(null, nameof(publicKey));
        var account = Identity.JayAddress.TryDecode(jayAccount, out var key) ? Identity.JayAddress.Encode(key) : null;
        lock (_gate)
        {
            var list = Load();
            var old = list.FirstOrDefault(m => m.PublicKey.AsSpan().SequenceEqual(publicKey));
            list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey));
            list.Add(new TrustedMember((byte[])publicKey.Clone(), KnockProtocol.SanitizeName(name)) { JayAccount = account ?? old?.JayAccount });
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Trusts exactly this device and no others: no Jay account is stored with it, so a new device of the same person
    /// still has to knock and be confirmed. An account stored earlier for this key is dropped.
    /// </summary>
    public void AddDevice(byte[] publicKey, string name)
    {
        if (publicKey.Length != 32) throw new ArgumentException(null, nameof(publicKey));
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey));
            list.Add(new TrustedMember((byte[])publicKey.Clone(), KnockProtocol.SanitizeName(name)));
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Stops trusting this one device only, leaving the person's other devices as they are.</summary>
    public void RemoveDevice(byte[] publicKey)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey)) == 0) return;
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Stops trusting the person: this device, and every other device of theirs trusted under the same Jay account.</summary>
    public void Remove(byte[] publicKey)
    {
        lock (_gate)
        {
            var list = Load();
            var account = list.FirstOrDefault(m => m.PublicKey.AsSpan().SequenceEqual(publicKey))?.JayAccount;
            if (list.RemoveAll(m => m.PublicKey.AsSpan().SequenceEqual(publicKey) || (account is not null && m.JayAccount == account)) == 0) return;
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>The last read failed (locked or not readable): nothing is cached, and nothing is written over the file.</summary>
    private bool _readFailed;
    /// <summary>The file could not be understood: a copy is kept as .broken before it is first replaced.</summary>
    private bool _broken;

    private List<TrustedMember> Load()
    {
        if (_members is not null) return _members;
        var members = new List<TrustedMember>();
        _readFailed = false;
        string? text;
        try { text = ListFile.Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not readable right now: empty for this call only; the next call reads again and Save leaves the file alone.
            _readFailed = true;
            return members;
        }
        JsonArray? array = null;
        if (text is not null)
        {
            try { array = JsonNode.Parse(text) as JsonArray ?? throw new JsonException(); }
            catch (JsonException) { _broken = true; }
        }
        foreach (var item in array ?? new JsonArray())
        {
            // One damaged entry is skipped; the others stay.
            try
            {
                if (item?["pub"]?.GetValue<string>() is not { Length: 64 } hex || !hex.All(char.IsAsciiHexDigit)) continue;
                var jay = item["jay"]?.GetValue<string>();
                members.Add(new TrustedMember(Convert.FromHexString(hex), KnockProtocol.SanitizeName(item["name"]?.GetValue<string>()))
                {
                    JayAccount = Identity.JayAddress.TryDecode(jay, out var account) ? Identity.JayAddress.Encode(account) : null,
                });
            }
            // Keep the file as .broken before the entry is dropped on the next save.
            catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException) { _broken = true; }
        }
        _members = members;
        return members;
    }

    private void Save(List<TrustedMember> list)
    {
        // Never write over a file that couldn't be read: it may hold everyone the host trusts.
        if (_readFailed) return;
        if (_broken)
        {
            ListFile.KeepBroken(path);
            _broken = false;
        }
        _members = list;
        var array = new JsonArray();
        foreach (var m in list)
        {
            var item = new JsonObject { ["pub"] = Convert.ToHexStringLower(m.PublicKey), ["name"] = m.Name };
            if (m.JayAccount is not null) item["jay"] = m.JayAccount;
            array.Add((JsonNode)item);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
