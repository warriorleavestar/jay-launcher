using Android.Security.Keystore;
using Amber.Core.Auth;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Security.Cryptography;

namespace Amber.Android;

/// <summary>
/// Seals Jay's local wrap keys with an AES-256-GCM key generated inside the Android Keystore. The Keystore
/// key never leaves the Keystore, so copying the app's files alone is not enough to open the identity or the
/// saved server keys.
/// </summary>
internal sealed class AndroidKeystoreKeyProtector : ILocalKeyProtector
{
    private const string Provider = "AndroidKeyStore";
    private const string Alias = "jay-local-wrap-key";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLength = 12;
    private const int TagBits = 128;
    private readonly Lock _gate = new();

    public byte[] Seal(byte[] key)
    {
        try
        {
            using var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, GetOrCreateKey());
            var iv = cipher.GetIV() ?? throw new CryptographicException("The Android Keystore returned no IV.");
            if (iv.Length != IvLength) throw new CryptographicException("The Android Keystore returned an unexpected IV.");
            var sealedKey = cipher.DoFinal(key) ?? throw new CryptographicException("The Android Keystore returned nothing.");
            return [.. iv, .. sealedKey];
        }
        catch (Java.Lang.Exception e)
        {
            throw new CryptographicException("The Android Keystore could not seal the key.", e);
        }
    }

    public byte[] Open(byte[] sealedKey)
    {
        if (sealedKey.Length <= IvLength) throw new CryptographicException("The sealed key is too short.");
        try
        {
            var key = ExistingKey() ?? throw new CryptographicException("The Android Keystore no longer has Jay's key.");
            using var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, sealedKey[..IvLength]));
            return cipher.DoFinal(sealedKey[IvLength..]) ?? throw new CryptographicException("The Android Keystore returned nothing.");
        }
        catch (Java.Lang.Exception e)
        {
            throw new CryptographicException("The Android Keystore could not open the key.", e);
        }
    }

    private static IKey? ExistingKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store.GetKey(Alias, null);
    }

    private IKey GetOrCreateKey()
    {
        lock (_gate)
        {
            if (ExistingKey() is { } existing) return existing;
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
            generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .Build());
            return generator.GenerateKey()!;
        }
    }
}
