using System.Security.Cryptography;

namespace Amber.Core.Auth;

/// <summary>
/// Seals the local wrap keys (the 32-byte key files next to the device identity, accounts and saved
/// servers) with a key the operating system keeps outside the app's files, such as the Android Keystore
/// or the iOS Keychain. Implementations use AES-256-GCM: <see cref="Seal"/> returns a 12-byte nonce, the 32-byte
/// ciphertext and a 16-byte tag (<see cref="LocalKeyProtection.SealedLength"/> bytes in all).
/// </summary>
public interface ILocalKeyProtector
{
    byte[] Seal(byte[] key);

    /// <summary>Opens what <see cref="Seal"/> returned; throws <see cref="CryptographicException"/> when it can't.</summary>
    byte[] Open(byte[] sealedKey);
}

/// <summary>
/// How a wrap key is stored on disk. Without a protector (desktop) the file holds the 32 raw bytes, readable
/// only by the current user. With one (mobile) it holds a marker and the sealed key; an older raw file is
/// sealed the next time it is read.
/// </summary>
public static class LocalKeyProtection
{
    public const int KeyLength = 32;
    /// <summary>What <see cref="ILocalKeyProtector.Seal"/> returns for a key: nonce, ciphertext and tag.</summary>
    public const int SealedLength = 12 + KeyLength + 16;
    private static readonly byte[] Magic = "AMBWK1"u8.ToArray();
    private static readonly AsyncLocal<ILocalKeyProtector?> TestProtector = new();
    private static ILocalKeyProtector? s_protector;

    /// <summary>Set once at startup by a platform that has an OS-held key; null keeps raw key files.</summary>
    public static ILocalKeyProtector? Protector
    {
        get => TestProtector.Value ?? s_protector;
        set => s_protector = value;
    }

    /// <summary>Tests: a protector for the current async flow only, so parallel tests don't see it.</summary>
    internal static ILocalKeyProtector? ProtectorForTest
    {
        get => TestProtector.Value;
        set => TestProtector.Value = value;
    }

    /// <summary>
    /// True when the file content has a form this class can read (raw or sealed), at its full length: a file cut short
    /// while it was first written (power loss) is not, so the caller can set it aside and make a new key.
    /// </summary>
    internal static bool IsWellFormed(ReadOnlySpan<byte> stored) =>
        stored.Length == KeyLength || (stored.StartsWith(Magic) && stored.Length == Magic.Length + SealedLength);

    /// <summary>
    /// The bytes to write for a key. When the key store can't seal (a broken key store on some device), the key
    /// is written raw as on desktop so the identity and saved servers keep working; a later read seals it.
    /// </summary>
    internal static byte[] Encode(byte[] key)
    {
        if (Protector is { } protector)
        {
            try
            {
                var sealedKey = protector.Seal(key);
                // Only the full sealed form is written, so a later read can tell a complete file from a cut-off one.
                if (sealedKey.Length == SealedLength) return [.. Magic, .. sealedKey];
            }
            catch (CryptographicException)
            {
            }
        }
        return (byte[])key.Clone();
    }

    /// <summary>Reads a key file's content. <paramref name="reseal"/> is true for a raw file that should now be sealed.</summary>
    internal static byte[] Decode(byte[] stored, string description, out bool reseal)
    {
        reseal = false;
        if (stored.AsSpan().StartsWith(Magic))
        {
            if (Protector is not { } protector)
                throw new CryptographicException($"The {description} key is sealed by another device's key store");
            if (stored.Length != Magic.Length + SealedLength) throw new CryptographicException($"Invalid {description} key");
            var key = protector.Open(stored[Magic.Length..]);
            if (key.Length != KeyLength)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"Invalid {description} key");
            }
            return key;
        }
        if (stored.Length != KeyLength) throw new CryptographicException($"Invalid {description} key");
        reseal = Protector is not null;
        return (byte[])stored.Clone();
    }

    /// <summary>Replaces a raw key file with its sealed form. A failure leaves the raw file, which still works.</summary>
    internal static void TryReseal(string path, byte[] key, Action<string, byte[]> writePrivate)
    {
        var encoded = Encode(key);
        if (encoded.Length == KeyLength) return; // The key store could not seal it this time.
        var temp = path + ".sealing";
        try
        {
            writePrivate(temp, encoded);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
