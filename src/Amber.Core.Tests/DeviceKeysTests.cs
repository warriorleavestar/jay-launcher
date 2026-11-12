using Amber.Core.Identity;

namespace Amber.Core.Tests;

/// <summary>Privacy plan item 7: one random seed per device, three keys from it, both signatures must verify.</summary>
[Collection(MlDsaImplementation.Name)]
public sealed class DeviceKeysTests
{
    private static readonly byte[] Seed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private const string PinnedMlDsaKeyHash = "50f97e608623c2c5601f359c4806cf4eb5d9a5bd1b487aed9feaea25037c002b";

    [Fact]
    public void FixedSeedGivesFixedKeysInBothImplementations()
    {
        using var a = DeviceKeys.FromSeed(Seed);
        MlDsa65.ForceBouncyCastle = true;
        try
        {
            using var b = DeviceKeys.FromSeed(Seed);
            Assert.Equal(a.Dh.PublicKey, b.Dh.PublicKey);
            Assert.Equal(a.EdPublicKey, b.EdPublicKey);
            Assert.Equal(a.MlDsaPublicKey, b.MlDsaPublicKey);
            // A signature from one implementation verifies in the other.
            var sig = b.Sign("test\n"u8, "hello"u8);
            MlDsa65.ForceBouncyCastle = false;
            Assert.True(DeviceKeys.Verify(a.EdPublicKey, a.MlDsaPublicKey, "test\n"u8, "hello"u8, sig));
        }
        finally { MlDsa65.ForceBouncyCastle = false; }
        Assert.Equal(MlDsa65.PublicKeySize, a.MlDsaPublicKey.Length);
        // Pinned (SHA-256 of the ML-DSA-65 public key for this seed): machines without a system ML-DSA compare BouncyCastle
        // with itself above, so this keeps them honest, and machines with one (Windows, OpenSSL 3.5+) check it against
        // the system implementation too.
        Assert.Equal(PinnedMlDsaKeyHash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(a.MlDsaPublicKey)));
        // The three keys are different keys, not one key reused.
        Assert.NotEqual(a.Dh.PublicKey, a.EdPublicKey);
    }

    [Fact]
    public void BothSignaturesMustVerify()
    {
        using var keys = DeviceKeys.Generate();
        using var other = DeviceKeys.Generate();
        var sig = keys.Sign("d\n"u8, "m"u8);
        Assert.True(DeviceKeys.Verify(keys.EdPublicKey, keys.MlDsaPublicKey, "d\n"u8, "m"u8, sig));
        Assert.False(DeviceKeys.Verify(keys.EdPublicKey, keys.MlDsaPublicKey, "e\n"u8, "m"u8, sig));
        // Only the Ed25519 half is right: refused.
        var mixed = sig with { MlDsa = other.Sign("d\n"u8, "m"u8).MlDsa };
        Assert.False(DeviceKeys.Verify(keys.EdPublicKey, keys.MlDsaPublicKey, "d\n"u8, "m"u8, mixed));
        var decoded = HybridSignature.Decode(sig.Encode());
        Assert.Equal(sig.Ed, decoded!.Ed);
        Assert.Equal(sig.MlDsa, decoded.MlDsa);
        // Two devices never share keys.
        Assert.NotEqual(keys.Dh.PublicKey, other.Dh.PublicKey);
    }
}

/// <summary>Tests that switch the ML-DSA implementation run alone, so no other test signs while it is switched.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MlDsaImplementation
{
    public const string Name = "MlDsaImplementation";
}
