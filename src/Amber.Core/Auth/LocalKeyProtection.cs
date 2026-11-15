using System.Security.Cryptography;

namespace Amber.Core.Auth;

/// <summary>
/// Seals the local wrap keys (the 32-byte key files next to the device identity, accounts and saved
/// servers) with a key the operating system keeps outside the app's files, such as the Android Keystore
/// or the iOS Keychain. Implementations use the platform's own authenticated encryption.
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

    /// <summary>True when the file content has a form this class can read (raw or sealed).</summary>
    internal static bool IsWellFormed(ReadOnlySpan<byte> stored) =>
        stored.Length == KeyLength || (stored.StartsWith(Magic) && stored.Length > Magic.Length);

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
                return [.. Magic, .. protector.Seal(key)];
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
