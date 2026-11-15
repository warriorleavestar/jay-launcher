using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChaChaPolyCollection
{
    public const string Name = "ChaChaPoly fallback";
}

/// <summary>
/// The BouncyCastle fallback for systems without ChaCha20-Poly1305 (retail Windows 10) must speak the same bytes as
/// the system class, since one side of a connection may use either.
/// </summary>
[Collection(ChaChaPolyCollection.Name)]
public sealed class ChaChaPolyTests
{
    private static readonly byte[] Key = Convert.FromHexString("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
    private static readonly byte[] Nonce = Convert.FromHexString("070000004041424344454647");
    private static readonly byte[] Ad = Convert.FromHexString("50515253c0c1c2c3c4c5c6c7");
    private static readonly byte[] Plain = Encoding.ASCII.GetBytes(
        "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");

    private static (byte[] Cipher, byte[] Tag) Seal(bool bouncyCastle, byte[] plain)
    {
        ChaChaPoly.ForceBouncyCastle = bouncyCastle;
        try
        {
            using var aead = new ChaChaPoly(Key);
            var cipher = new byte[plain.Length];
            var tag = new byte[ChaChaPoly.TagSize];
            aead.Encrypt(Nonce, plain, cipher, tag, Ad);
            return (cipher, tag);
        }
        finally { ChaChaPoly.ForceBouncyCastle = false; }
    }

    private static byte[] Open(bool bouncyCastle, byte[] cipher, byte[] tag)
    {
        ChaChaPoly.ForceBouncyCastle = bouncyCastle;
        try
        {
            using var aead = new ChaChaPoly(Key);
            var plain = new byte[cipher.Length];
            aead.Decrypt(Nonce, cipher, tag, plain, Ad);
            return plain;
        }
        finally { ChaChaPoly.ForceBouncyCastle = false; }
    }

    [Fact]
    public void TheFallbackMatchesTheRfcTestVector()
    {
        var (cipher, tag) = Seal(bouncyCastle: true, Plain);
        Assert.Equal("1ae10b594f09e26a7e902ecbd0600691", Convert.ToHexStringLower(tag));
        Assert.StartsWith("d31a8d34648e60db7b86afbc53ef7ec2", Convert.ToHexStringLower(cipher));
        Assert.Equal(Plain, Open(bouncyCastle: true, cipher, tag));
    }

    [Fact]
    public void TheFallbackAndTheSystemClassReadEachOther()
    {
        if (!ChaCha20Poly1305.IsSupported) return; // nothing to compare against on this system
        var fromFallback = Seal(bouncyCastle: true, Plain);
        var fromSystem = Seal(bouncyCastle: false, Plain);
        Assert.Equal(fromSystem.Cipher, fromFallback.Cipher);
        Assert.Equal(fromSystem.Tag, fromFallback.Tag);
        Assert.Equal(Plain, Open(bouncyCastle: false, fromFallback.Cipher, fromFallback.Tag));
        Assert.Equal(Plain, Open(bouncyCastle: true, fromSystem.Cipher, fromSystem.Tag));
    }

    [Fact]
    public void TheFallbackRejectsAlteredData()
    {
        var (cipher, tag) = Seal(bouncyCastle: true, Plain);
        cipher[3] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => Open(bouncyCastle: true, cipher, tag));
        cipher[3] ^= 1;
        tag[0] ^= 1;
        var e = Assert.ThrowsAny<CryptographicException>(() => Open(bouncyCastle: true, cipher, tag));
        Assert.IsType<AuthenticationTagMismatchException>(e);
    }

    [Fact]
    public void ANoiseCipherOnTheFallbackTalksToOneOnTheSystem()
    {
        var key = RandomNumberGenerator.GetBytes(CipherState.KeyLength);
        ChaChaPoly.ForceBouncyCastle = true;
        using var fallback = new CipherState();
        try { fallback.InitializeKey((byte[])key.Clone()); }
        finally { ChaChaPoly.ForceBouncyCastle = false; }
        using var system = new CipherState();
        system.InitializeKey((byte[])key.Clone());

        var record = fallback.EncryptWithAd("ad"u8, "hello"u8);
        Assert.Equal("hello"u8.ToArray(), system.DecryptWithAd("ad"u8, record));
        var back = system.EncryptWithAd([], "world"u8);
        Assert.Equal("world"u8.ToArray(), fallback.DecryptWithAd([], back));

        fallback.Rekey();
        system.Rekey();
        Assert.Equal("again"u8.ToArray(), system.DecryptWithAd([], fallback.EncryptWithAd([], "again"u8)));
        Assert.Throws<AuthenticationTagMismatchException>(() => fallback.DecryptWithAd("x"u8, system.EncryptWithAd("y"u8, "z"u8)));
    }
}
