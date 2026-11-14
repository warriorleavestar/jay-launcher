using Amber.Core.Auth;
using Foundation;
using Security;
using System.Security.Cryptography;

namespace Amber.iOS;

/// <summary>
/// Seals Jay's local wrap keys with AES-256-GCM under a key kept in the iOS Keychain, readable after the
/// first unlock and only on this device (it is not part of backups or moved to a new device).
/// </summary>
internal sealed class KeychainKeyProtector : ILocalKeyProtector
{
    private const string Service = "nz.work.minecraft.jay.local-wrap-key";
    private const string Account = "wrap-key";
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly Lock _gate = new();

    public byte[] Seal(byte[] key)
    {
        var master = GetOrCreateMaster();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var cipher = new byte[key.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(master, TagLength);
            aes.Encrypt(nonce, key, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    public byte[] Open(byte[] sealedKey)
    {
        if (sealedKey.Length <= NonceLength + TagLength) throw new CryptographicException("The sealed key is too short.");
        var master = ExistingMaster() ?? throw new CryptographicException("The iOS Keychain no longer has Jay's key.");
        try
        {
            var plain = new byte[sealedKey.Length - NonceLength - TagLength];
            using var aes = new AesGcm(master, TagLength);
            aes.Decrypt(sealedKey.AsSpan(0, NonceLength), sealedKey.AsSpan(NonceLength + TagLength),
                sealedKey.AsSpan(NonceLength, TagLength), plain);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    private static byte[]? ExistingMaster()
    {
        var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account };
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound) return null;
        if (status != SecStatusCode.Success || data is null)
            throw new CryptographicException($"The iOS Keychain could not be read ({status}).");
        var master = data.ToArray();
        if (master.Length == KeyLength) return master;
        CryptographicOperations.ZeroMemory(master);
        throw new CryptographicException("The iOS Keychain holds an unexpected key.");
    }

    private byte[] GetOrCreateMaster()
    {
        lock (_gate)
        {
            if (ExistingMaster() is { } existing) return existing;
            var created = RandomNumberGenerator.GetBytes(KeyLength);
            using var value = NSData.FromArray(created);
            var record = new SecRecord(SecKind.GenericPassword)
            {
                Service = Service,
                Account = Account,
                Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
                ValueData = value
            };
            var status = SecKeyChain.Add(record);
            if (status == SecStatusCode.Success) return created;
            CryptographicOperations.ZeroMemory(created);
            if (status == SecStatusCode.DuplicateItem && ExistingMaster() is { } raced) return raced;
            throw new CryptographicException($"The iOS Keychain could not store Jay's key ({status}).");
        }
    }
}
