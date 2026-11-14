using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Amber.Core.Identity;

/// <summary>
/// ML-DSA-65 (FIPS 204), pure mode with an empty context. Keys come from a 32-byte seed (FIPS 204 ξ), so the same seed
/// gives the same key pair in both implementations. Uses the system implementation when there is one (Windows 11 recent
/// builds, Linux with OpenSSL 3.5+) and BouncyCastle otherwise; both produce standard encodings and verify each other.
/// </summary>
internal static class MlDsa65
{
    public const int SeedSize = 32;
    public const int PublicKeySize = 1952;
    public const int SignatureSize = 3309;

    /// <summary>Tests: always use BouncyCastle (to check both implementations agree).</summary>
    internal static bool ForceBouncyCastle { get; set; }

#pragma warning disable SYSLIB5006
    private static bool UseSystem => !ForceBouncyCastle && MLDsa.IsSupported;
#pragma warning restore SYSLIB5006

    private static readonly MLDsaParameters Bc = MLDsaParameters.ml_dsa_65;

    /// <summary>The public key for <paramref name="seed"/>.</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize) throw new CryptographicException("ML-DSA seed size");
        if (UseSystem)
        {
#pragma warning disable SYSLIB5006
            using var key = MLDsa.ImportMLDsaPrivateSeed(MLDsaAlgorithm.MLDsa65, seed);
            return key.ExportMLDsaPublicKey();
#pragma warning restore SYSLIB5006
        }
        var copy = seed.ToArray();
        try { return MLDsaPrivateKeyParameters.FromSeed(Bc, copy).GetPublicKeyEncoded(); }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>Signs <paramref name="message"/> with the key from <paramref name="seed"/> (hedged, so two signatures differ).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        if (seed.Length != SeedSize) throw new CryptographicException("ML-DSA seed size");
        if (UseSystem)
        {
#pragma warning disable SYSLIB5006
            using var key = MLDsa.ImportMLDsaPrivateSeed(MLDsaAlgorithm.MLDsa65, seed);
            return key.SignData(message.ToArray());
#pragma warning restore SYSLIB5006
        }
        // The seed copy is zeroed after use. The key BouncyCastle expands from it is managed memory it doesn't let us clear.
        var copy = seed.ToArray();
        try
        {
            var signer = new MLDsaSigner(Bc, deterministic: false);
            signer.Init(true, new Org.BouncyCastle.Crypto.Parameters.ParametersWithRandom(MLDsaPrivateKeyParameters.FromSeed(Bc, copy), new SecureRandom()));
            var bytes = message.ToArray();
            signer.BlockUpdate(bytes, 0, bytes.Length);
            return signer.GenerateSignature();
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="message"/> by <paramref name="publicKey"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize) return false;
        try
        {
            if (UseSystem)
            {
#pragma warning disable SYSLIB5006
                using var key = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, publicKey);
                return key.VerifyData(message.ToArray(), signature.ToArray());
#pragma warning restore SYSLIB5006
            }
            var verifier = new MLDsaSigner(Bc, deterministic: false);
            verifier.Init(false, MLDsaPublicKeyParameters.FromEncoding(Bc, publicKey.ToArray()));
            var bytes = message.ToArray();
            verifier.BlockUpdate(bytes, 0, bytes.Length);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
