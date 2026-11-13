using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Amber.Core.Identity;

/// <summary>
/// Ed25519 (64 bytes) and ML-DSA-65 (3309 bytes) over the same bytes. It only counts when both verify, so it holds as
/// long as either scheme does. Used by device keys (<see cref="DeviceKeys"/>) and Jay accounts (<see cref="JayAccount"/>).
/// </summary>
public sealed record HybridSignature(byte[] Ed, byte[] MlDsa)
{
    public const int Size = 64 + MlDsa65.SignatureSize;

    public byte[] Encode() => [.. Ed, .. MlDsa];

    public static HybridSignature? Decode(ReadOnlySpan<byte> data) =>
        data.Length == Size ? new HybridSignature(data[..64].ToArray(), data[64..].ToArray()) : null;

    /// <summary>Signs <paramref name="domain"/> ‖ <paramref name="message"/> with both keys.</summary>
    internal static HybridSignature Sign(byte[] edPrivate, byte[] mlDsaSeed, ReadOnlySpan<byte> domain, ReadOnlySpan<byte> message)
    {
        byte[] signed = [.. domain, .. message];
        var ed = new Ed25519Signer();
        ed.Init(true, new Ed25519PrivateKeyParameters(edPrivate));
        ed.BlockUpdate(signed, 0, signed.Length);
        return new HybridSignature(ed.GenerateSignature(), MlDsa65.Sign(mlDsaSeed, signed));
    }

    /// <summary>True only when both halves verify against the two public keys.</summary>
    public static bool Verify(ReadOnlySpan<byte> edPublicKey, ReadOnlySpan<byte> mlDsaPublicKey, ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> message, HybridSignature? signature)
    {
        if (signature is null || edPublicKey.Length != 32 || signature.Ed.Length != 64) return false;
        byte[] signed = [.. domain, .. message];
        try
        {
            var ed = new Ed25519Signer();
            ed.Init(false, new Ed25519PublicKeyParameters(edPublicKey.ToArray()));
            ed.BlockUpdate(signed, 0, signed.Length);
            if (!ed.VerifySignature(signature.Ed)) return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        return MlDsa65.Verify(mlDsaPublicKey, signed, signature.MlDsa);
    }
}
