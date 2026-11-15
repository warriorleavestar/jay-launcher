using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// ChaCha20-Poly1305 (RFC 8439) with the same calls as <see cref="ChaCha20Poly1305"/>. The system class is used where
/// the OS provides it; elsewhere (retail Windows 10 before build 20142 has no ChaCha20-Poly1305 in CNG) BouncyCastle's
/// implementation of the same construction does the work, so both produce and accept the same bytes.
/// </summary>
public sealed class ChaChaPoly : IDisposable
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Tests: use BouncyCastle even where the system has ChaCha20-Poly1305.</summary>
    internal static bool ForceBouncyCastle { get; set; }

    /// <summary>True when the system class is used.</summary>
    public static bool UsesSystem => !ForceBouncyCastle && ChaCha20Poly1305.IsSupported;

    private readonly ChaCha20Poly1305? _system;
    private byte[]? _key;

    public ChaChaPoly(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize) throw new ArgumentException("ChaCha20-Poly1305 needs a 32-byte key.", nameof(key));
        if (UsesSystem) _system = new ChaCha20Poly1305(key);
        else _key = key.ToArray();
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (_system is not null)
        {
            _system.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return;
        }
        Check(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        var output = Run(true, nonce, plaintext, associatedData);
        try
        {
            output.AsSpan(0, plaintext.Length).CopyTo(ciphertext);
            output.AsSpan(plaintext.Length, TagSize).CopyTo(tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>Throws <see cref="AuthenticationTagMismatchException"/> (a <see cref="CryptographicException"/>) when the data was altered.</summary>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (_system is not null)
        {
            _system.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return;
        }
        Check(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        var input = new byte[ciphertext.Length + TagSize];
        ciphertext.CopyTo(input);
        tag.CopyTo(input.AsSpan(ciphertext.Length));
        var output = Run(false, nonce, input, associatedData);
        try
        {
            output.AsSpan(0, ciphertext.Length).CopyTo(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    private static void Check(ReadOnlySpan<byte> nonce, int plaintextLength, int ciphertextLength, int tagLength)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException("ChaCha20-Poly1305 needs a 12-byte nonce.", nameof(nonce));
        if (tagLength != TagSize) throw new ArgumentException("ChaCha20-Poly1305 needs a 16-byte tag.", nameof(tagLength));
        if (plaintextLength != ciphertextLength) throw new ArgumentException("Plaintext and ciphertext must be the same length.");
    }

    private byte[] Run(bool encrypt, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, ReadOnlySpan<byte> associatedData)
    {
        var key = _key;
        ObjectDisposedException.ThrowIf(key is null, this);
        var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce.ToArray(), associatedData.ToArray()));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        try
        {
            var n = cipher.ProcessBytes(input, output);
            cipher.DoFinal(output.AsSpan(n));
            return output;
        }
        catch (InvalidCipherTextException)
        {
            CryptographicOperations.ZeroMemory(output);
            throw new AuthenticationTagMismatchException();
        }
    }

    public void Dispose()
    {
        _system?.Dispose();
        if (_key is { } key) CryptographicOperations.ZeroMemory(key);
        _key = null;
    }
}
