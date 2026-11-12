using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer;

namespace Amber.Core.Identity;

/// <summary>One saved Jay account on this computer (the seed itself stays encrypted on disk until <see cref="JayAccountStore.Open"/>).</summary>
public sealed record JayAccountEntry(string Address, string Label, bool BackedUp, int Epoch, DateTimeOffset Created, DeviceCertificate? Device)
{
    public string ShortAddress => JayAddress.Short(Address);
}

/// <summary>
/// The Jay accounts saved on this computer, in jay-accounts.json in the data directory. Each seed is encrypted like the
/// multiplayer identity (Windows: DPAPI for the current user; macOS/Linux: a 0600 local key + AES-GCM). The recovery phrase
/// itself is never stored: only the seed derived from it (and the optional extra password), so the words are only ever on paper.
/// A damaged file is kept as .broken and treated as empty, never crashing the app.
/// </summary>
public sealed class JayAccountStore(string directory)
{
    public const string FileName = "jay-accounts.json";

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

    public void Load()
    {
        lock (_gate)
        {
            _items.Clear();
            if (!File.Exists(PathOnDisk)) return;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(PathOnDisk)) as JsonObject;
                foreach (var node in root?["accounts"] as JsonArray ?? [])
                {
                    if (node is not JsonObject o) continue;
                    var address = (string?)o["address"];
                    var seed = (string?)o["seed"];
                    if (!JayAddress.IsValid(address) || string.IsNullOrEmpty(seed) || _items.Any(i => i.Entry.Address == address)) continue;
                    var entry = new JayAccountEntry(address!, DeviceCertificate.CleanName((string?)o["label"]), (bool?)o["backedUp"] ?? false,
                        Math.Max(0, (int?)o["epoch"] ?? 0), DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, (long?)o["created"] ?? 0)),
                        DeviceCertificate.FromJson(o["device"]));
                    _items.Add((entry, seed));
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                try { File.Copy(PathOnDisk, PathOnDisk + ".broken", true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
                _items.Clear();
            }
        }
    }

    /// <summary>Saves an account (replacing one with the same address) and certifies this device's keys for it.</summary>
    public JayAccountEntry Add(JayAccount account, string label, bool backedUp, DeviceKeys device, string deviceName, DateTimeOffset now)
    {
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
                epoch, created, account.CertifyDevice(device, deviceName, epoch, now));
            if (existing >= 0) _items[existing] = (entry, protectedSeed);
            else _items.Add((entry, protectedSeed));
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

    /// <summary>Forgets the account on this computer. It still exists: the recovery phrase restores it.</summary>
    public void Remove(string address)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(i => i.Entry.Address == address) > 0) SaveLocked();
        }
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
            });
        }
        Directory.CreateDirectory(directory);
        var temp = PathOnDisk + ".tmp";
        MemberIdentity.WritePrivate(temp, System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["version"] = 1, ["accounts"] = accounts }.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        File.Move(temp, PathOnDisk, true);
    }
}
