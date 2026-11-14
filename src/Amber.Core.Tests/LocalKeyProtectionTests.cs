using System.Security.Cryptography;
using Amber.Core.Auth;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Mobile wrap keys: with an OS key store the key files next to the identity, accounts and saved servers hold the
/// key sealed, older raw files are sealed on the next read, and nothing changes without a key store (desktop).
/// </summary>
public sealed class LocalKeyProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-wrapkey-" + Guid.NewGuid().ToString("N"));

    public LocalKeyProtectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        LocalKeyProtection.ProtectorForTest = null;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    [Fact]
    public void IdentityWrapKeyIsSealedAndReadBack()
    {
        if (OperatingSystem.IsWindows()) return; // Windows protects the identity with DPAPI instead.
        LocalKeyProtection.ProtectorForTest = new TestKeyStore();
        byte[] first;
        using (var identity = MemberIdentity.LoadOrCreate(_dir))
        {
            Assert.Null(identity.Warning);
            first = identity.PublicKey;
        }
        var wrap = File.ReadAllBytes(Path.Combine(_dir, "identity.wrap"));
        Assert.StartsWith("AMBWK1", System.Text.Encoding.ASCII.GetString(wrap));
        Assert.NotEqual(LocalKeyProtection.KeyLength, wrap.Length);

        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Null(again.Warning);
        Assert.Equal(first, again.PublicKey);
    }

    [Fact]
    public void RawWrapKeyFromAnOlderVersionIsSealedOnTheNextRead()
    {
        if (OperatingSystem.IsWindows()) return;
        byte[] first;
        using (var identity = MemberIdentity.LoadOrCreate(_dir)) first = identity.PublicKey;
        var wrapPath = Path.Combine(_dir, "identity.wrap");
        Assert.Equal(LocalKeyProtection.KeyLength, new FileInfo(wrapPath).Length);

        LocalKeyProtection.ProtectorForTest = new TestKeyStore();
        using (var identity = MemberIdentity.LoadOrCreate(_dir))
        {
            Assert.Null(identity.Warning);
            Assert.Equal(first, identity.PublicKey);
        }
        Assert.NotEqual(LocalKeyProtection.KeyLength, new FileInfo(wrapPath).Length);
        Assert.False(File.Exists(wrapPath + ".sealing"));
        using var again = MemberIdentity.LoadOrCreate(_dir);
        Assert.Equal(first, again.PublicKey);
    }

    [Fact]
    public void SecretKeyFilesAreSealedAndOldOnesResealed()
    {
        var store = new TestKeyStore();
        LocalKeyProtection.ProtectorForTest = store;
        var path = Path.Combine(_dir, "servers.json.key");
        var key = LocalSecretKey.LoadOrCreate(path, "Jay server");
        Assert.Equal(LocalKeyProtection.KeyLength, key.Length);
        Assert.False(File.ReadAllBytes(path).AsSpan().IndexOf(key) >= 0);
        Assert.Equal(key, LocalSecretKey.LoadOrCreate(path, "Jay server"));

        var oldPath = Path.Combine(_dir, "accounts.json.key");
        var oldKey = RandomNumberGenerator.GetBytes(LocalKeyProtection.KeyLength);
        File.WriteAllBytes(oldPath, oldKey);
        Assert.Equal(oldKey, LocalSecretKey.LoadOrCreate(oldPath, "account"));
        Assert.False(File.ReadAllBytes(oldPath).AsSpan().IndexOf(oldKey) >= 0);
        Assert.Equal(oldKey, LocalSecretKey.LoadOrCreate(oldPath, "account"));
    }

    [Fact]
    public void WithoutAKeyStoreFilesStayRawAndSealedOnesAreRefused()
    {
        var path = Path.Combine(_dir, "plain.key");
        var key = LocalSecretKey.LoadOrCreate(path, "account");
        Assert.Equal(key, File.ReadAllBytes(path));

        var store = new TestKeyStore();
        LocalKeyProtection.ProtectorForTest = store;
        var sealedPath = Path.Combine(_dir, "sealed.key");
        LocalSecretKey.LoadOrCreate(sealedPath, "account");
        LocalKeyProtection.ProtectorForTest = null;
        Assert.ThrowsAny<CryptographicException>(() => LocalSecretKey.LoadOrCreate(sealedPath, "account"));

        // A sealed key that was changed on disk does not open.
        LocalKeyProtection.ProtectorForTest = store;
        var stored = File.ReadAllBytes(sealedPath);
        stored[^1] ^= 1;
        File.WriteAllBytes(sealedPath, stored);
        Assert.ThrowsAny<CryptographicException>(() => LocalSecretKey.LoadOrCreate(sealedPath, "account"));
    }

    [Fact]
    public void ABrokenKeyStoreFallsBackToTheRawKeyFile()
    {
        LocalKeyProtection.ProtectorForTest = new BrokenKeyStore();
        var path = Path.Combine(_dir, "servers.json.key");
        var key = LocalSecretKey.LoadOrCreate(path, "Jay server");
        Assert.Equal(key, File.ReadAllBytes(path));
        Assert.Equal(key, LocalSecretKey.LoadOrCreate(path, "Jay server"));

        // Once the key store works again, the raw file is sealed.
        LocalKeyProtection.ProtectorForTest = new TestKeyStore();
        Assert.Equal(key, LocalSecretKey.LoadOrCreate(path, "Jay server"));
        Assert.NotEqual(LocalKeyProtection.KeyLength, new FileInfo(path).Length);
    }

    private sealed class BrokenKeyStore : ILocalKeyProtector
    {
        public byte[] Seal(byte[] key) => throw new CryptographicException("key store unavailable");
        public byte[] Open(byte[] sealedKey) => throw new CryptographicException("key store unavailable");
    }

    /// <summary>Stands in for the Android Keystore or iOS Keychain: AES-GCM with a key held in memory.</summary>
    private sealed class TestKeyStore : ILocalKeyProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public byte[] Seal(byte[] key)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[key.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, key, cipher, tag);
            return [.. nonce, .. tag, .. cipher];
        }

        public byte[] Open(byte[] sealedKey)
        {
            if (sealedKey.Length < 28) throw new CryptographicException("short");
            var plain = new byte[sealedKey.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(sealedKey.AsSpan(0, 12), sealedKey.AsSpan(28), sealedKey.AsSpan(12, 16), plain);
            return plain;
        }
    }
}
