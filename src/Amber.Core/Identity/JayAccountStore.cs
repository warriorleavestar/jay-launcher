using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;

namespace Amber.Core.Identity;

/// <summary>One saved Jay account on this computer (the seed itself stays encrypted on disk until <see cref="JayAccountStore.Open"/>).</summary>
public sealed record JayAccountEntry(string Address, string Label, bool BackedUp, int Epoch, DateTimeOffset Created, DeviceCertificate? Device)
{
    /// <summary>The version 0 address this account had before addresses covered both account keys, when it was moved.</summary>
    public string? FormerAddress { get; init; }

    public string ShortAddress => JayAddress.Short(Address);
}

/// <summary>
/// The Jay accounts saved on this computer, in jay-accounts-2.json in the data directory. Each seed is encrypted like the
/// multiplayer identity (Windows: DPAPI for the current user; macOS/Linux: a 0600 local key + AES-GCM). The recovery phrase
/// itself is never stored: only the seed derived from it (and the optional extra password), so the words are only ever on paper.
/// A damaged file is kept as .broken and treated as empty, never crashing the app. A file that can't be read right now
/// (locked by another program, no permission) is left alone: the list is empty for now, and nothing is saved over it until
/// a later read succeeds, so the accounts in it are never lost.
/// Accounts saved by versions before version 1 addresses (in jay-accounts.json) are read once, when the new file doesn't
/// exist yet, and that file is never written: an older version sharing or going back to this data folder keeps its accounts,
/// and can't erase the new ones it doesn't understand.
/// </summary>
public sealed class JayAccountStore(string directory)
{
    public const string FileName = "jay-accounts-2.json";

    /// <summary>The file older versions keep their accounts in (version 0 addresses). Only read, never written.</summary>
    public const string LegacyFileName = "jay-accounts.json";

    /// <summary>What other people see as this device's name: only the operating system, never the computer or user name.</summary>
    public static string ThisDeviceName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsAndroid() ? "Android" : OperatingSystem.IsIOS() ? "iOS" : "Jay";
    private readonly Lock _gate = new();
    private readonly List<(JayAccountEntry Entry, string ProtectedSeed)> _items = [];

    private string PathOnDisk => Path.Combine(directory, FileName);

    public IReadOnlyList<JayAccountEntry> Accounts
    {
        get { lock (_gate) return [.. _items.Select(i => i.Entry)]; }
    }

    public JayAccountEntry? Find(string? address)
    {
        lock (_gate) return _items.Select(i => i.Entry).FirstOrDefault(e => e.Address == address);
    }

    // The file exists but couldn't be read (an I/O or permission error): saving would replace accounts that weren't read.
    private bool _unreadable;

    /// <summary>The accounts file is there but couldn't be read this time; it is tried again before the next save.</summary>
    public bool Unreadable
    {
        get { lock (_gate) return _unreadable; }
    }

    // Version 0 entries that couldn't be moved this time (the seed didn't open: a locked file, DPAPI unavailable, ...).
    // They are written back unchanged, so a later start can still move them and the seed is never lost.
    private readonly List<JsonNode> _unmoved = [];

    /// <summary>
    /// Old address → new address for every account moved from a version 0 address (kept in the file as "was"), so the
    /// owner can point Minecraft account bindings at the new address on any start, not only the one that moved it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Readdressed
    {
        get
        {
            lock (_gate)
            {
                var map = new Dictionary<string, string>();
                foreach (var (entry, _) in _items)
                    if (entry.FormerAddress is { } former) map.TryAdd(former, entry.Address);
                return map;
            }
        }
    }

    /// <summary>The version 1 address of a saved version 0 account, when its seed opens here and matches the old address.</summary>
    private string? NewAddressFor(string? oldAddress, string protectedSeed)
    {
        if (oldAddress is null || !JayAddress.TryDecodeVersion0(oldAddress, out var oldKey)) return null;
        byte[]? seed = null;
        try
        {
            seed = MemberIdentity.Unprotect(directory, Convert.FromBase64String(protectedSeed), JayAccount.SeedLength);
            using var account = JayAccount.FromSeed(seed);
            return account.PublicKey.AsSpan().SequenceEqual(oldKey) ? account.Address : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _items.Clear();
            _unmoved.Clear();
            _unreadable = false;
            var legacy = !File.Exists(PathOnDisk);
            var moved = false;
            var path = legacy ? Path.Combine(directory, LegacyFileName) : PathOnDisk;
            if (!File.Exists(path)) return;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                foreach (var node in root?["accounts"] as JsonArray ?? [])
                {
                    if (node is not JsonObject o) continue;
                    var address = (string?)o["address"];
                    var seed = (string?)o["seed"];
                    if (string.IsNullOrEmpty(seed)) continue;
                    var former = JayAddress.TryDecodeVersion0((string?)o["was"], out _) ? (string?)o["was"] : null;
                    if (!JayAddress.IsValid(address))
                    {
                        // An account saved before addresses became a hash of both account keys (version 0, the bare
                        // Ed25519 key): work out its new address from the seed, and remember the old one so the bindings
                        // that point at it can follow. If that can't be done now, keep the entry exactly as it is, to try
                        // again next time. An address that is neither version is dropped.
                        if (!JayAddress.TryDecodeVersion0(address, out _)) continue;
                        if (NewAddressFor(address, seed) is not { } renamed)
                        {
                            _unmoved.Add(o.DeepClone());
                            continue;
                        }
                        former = address;
                        address = renamed;
                        moved = true;
                    }
                    if (_items.Any(i => i.Entry.Address == address)) continue;
                    var entry = new JayAccountEntry(address!, DeviceCertificate.CleanName((string?)o["label"]), (bool?)o["backedUp"] ?? false,
                        Math.Max(0, (int?)o["epoch"] ?? 0), DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, (long?)o["created"] ?? 0)),
                        DeviceCertificate.FromJson(o["device"])) { FormerAddress = former };
                    _items.Add((entry, seed));
                }
                if (moved || (legacy && (_items.Count > 0 || _unmoved.Count > 0)))
                {
                    // The first start of this version, or an entry that couldn't be moved before and could now: the accounts
                    // go to the new file, so this happens once. The old file stays as it is.
                    try { SaveLocked(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                _unmoved.Clear();
                _items.Clear();
                if (legacy) return;
                try { File.Copy(PathOnDisk, PathOnDisk + ".broken", true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not damaged, just not readable now: start empty and keep the file exactly as it is.
                _unmoved.Clear();
                _items.Clear();
                _unreadable = true;
            }
        }
    }

    /// <summary>Before a change is saved: when the file couldn't be read at start, read it again first, and refuse while it still can't be.</summary>
    private void ReadAgainIfUnreadable()
    {
        bool unreadable;
        lock (_gate) unreadable = _unreadable;
        if (!unreadable) return;
        Load();
        lock (_gate)
            if (_unreadable) throw new IOException(L.T("core.identity.accounts_unreadable"));
    }

    /// <summary>Saves an account (replacing one with the same address) and certifies this device's keys for it.</summary>
    public JayAccountEntry Add(JayAccount account, string label, bool backedUp, DeviceKeys device, string deviceName, DateTimeOffset now)
    {
        ReadAgainIfUnreadable();
        Directory.CreateDirectory(directory);
        var seed = account.ExportSeed();
        string protectedSeed;
        try { protectedSeed = Convert.ToBase64String(MemberIdentity.Protect(directory, seed)); }
        finally { CryptographicOperations.ZeroMemory(seed); }

        lock (_gate)
        {
            var existing = _items.FindIndex(i => i.Entry.Address == account.Address);
            var epoch = existing >= 0 ? _items[existing].Entry.Epoch : 0;
            var created = existing >= 0 ? _items[existing].Entry.Created : DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
            var entry = new JayAccountEntry(account.Address, DeviceCertificate.CleanName(label), backedUp || (existing >= 0 && _items[existing].Entry.BackedUp),
                epoch, created, account.CertifyDevice(device, deviceName, epoch, now)) { FormerAddress = existing >= 0 ? _items[existing].Entry.FormerAddress : null };
            if (existing >= 0) _items[existing] = (entry, protectedSeed);
            else _items.Add((entry, protectedSeed));
            // Saved again from the recovery phrase: an old copy that couldn't be moved isn't needed any more.
            var v0 = JayAddress.EncodeVersion0(account.PublicKey);
            _unmoved.RemoveAll(n => (string?)n["address"] == v0);
            SaveLocked();
            return entry;
        }
    }

    /// <summary>Decrypts a saved account. Null when it isn't saved here or can't be decrypted (other user / other computer).</summary>
    public JayAccount? Open(string address)
    {
        string? stored;
        lock (_gate) stored = _items.FirstOrDefault(i => i.Entry.Address == address).ProtectedSeed;
        if (stored is null) return null;
        byte[]? seed = null;
        try
        {
            seed = MemberIdentity.Unprotect(directory, Convert.FromBase64String(stored), JayAccount.SeedLength);
            var account = JayAccount.FromSeed(seed);
            if (account.Address == address) return account;
            account.Dispose();
            return null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>
    /// Makes sure this device's certificate matches its current device keys (they change when the user resets the identity).
    /// Returns the entry, re-signed if needed; null when the account isn't saved here or can't be opened.
    /// </summary>
    public JayAccountEntry? EnsureDeviceCertificate(string address, DeviceKeys device, string deviceName, DateTimeOffset now)
    {
        var entry = Find(address);
        if (entry is null) return null;
        if (entry.Device is { } cert && cert.Names(device) && cert.Account == entry.Address && cert.Epoch == entry.Epoch
            && cert.DeviceName == DeviceCertificate.CleanName(deviceName) && cert.Verify(device.Dh.PublicKey)) return entry;
        using var account = Open(address);
        if (account is null) return null;
        return Add(account, entry.Label, entry.BackedUp, device, deviceName, now);
    }

    public void MarkBackedUp(string address) => Update(address, e => e with { BackedUp = true });

    public void Rename(string address, string label) => Update(address, e => e with { Label = DeviceCertificate.CleanName(label) });

    /// <summary>
    /// Forgets the account on this computer. It still exists: the recovery phrase restores it. Its encrypted seed goes from
    /// every file here, including the file older versions keep (where it is under its version 0 address).
    /// </summary>
    public void Remove(string address)
    {
        lock (_gate)
        {
            var gone = _items.Where(i => i.Entry.Address == address).Select(i => i.Entry).ToList();
            if (gone.Count == 0) return;
            _items.RemoveAll(i => i.Entry.Address == address);
            var old = gone.SelectMany(e => new[] { e.Address, e.FormerAddress, e.Device is { } d ? JayAddress.EncodeVersion0(d.AccountEd) : null })
                .OfType<string>().ToHashSet();
            _unmoved.RemoveAll(n => old.Contains((string?)n["address"] ?? ""));
            SaveLocked();
            PurgeLegacyLocked(old);
        }
    }

    /// <summary>Takes these addresses out of the older versions' file. The rest of that file stays as it is.</summary>
    private void PurgeLegacyLocked(IReadOnlySet<string> addresses)
    {
        var path = Path.Combine(directory, LegacyFileName);
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["accounts"] is not JsonArray accounts) return;
            var drop = accounts.Where(n => n is JsonObject o && addresses.Contains((string?)o["address"] ?? "")).ToList();
            if (drop.Count == 0) return;
            foreach (var n in drop) accounts.Remove(n);
            var temp = path + ".tmp";
            MemberIdentity.WritePrivate(temp, System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    private void Update(string address, Func<JayAccountEntry, JayAccountEntry> change)
    {
        lock (_gate)
        {
            var at = _items.FindIndex(i => i.Entry.Address == address);
            if (at < 0) return;
            _items[at] = (change(_items[at].Entry), _items[at].ProtectedSeed);
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        // Never replace a file whose accounts weren't read.
        if (_unreadable) throw new IOException(L.T("core.identity.accounts_unreadable"));
        var accounts = new JsonArray();
        foreach (var (entry, seed) in _items)
        {
            accounts.Add(new JsonObject
            {
                ["address"] = entry.Address,
                ["label"] = entry.Label,
                ["seed"] = seed,
                ["backedUp"] = entry.BackedUp,
                ["epoch"] = entry.Epoch,
                ["created"] = entry.Created.ToUnixTimeSeconds(),
                ["device"] = entry.Device?.ToJson(),
                ["was"] = entry.FormerAddress,
            });
        }
        foreach (var node in _unmoved) accounts.Add(node.DeepClone());
        Directory.CreateDirectory(directory);
        var temp = PathOnDisk + ".tmp";
        MemberIdentity.WritePrivate(temp, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 1, ["accounts"] = accounts }.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        File.Move(temp, PathOnDisk, true);
    }
}
