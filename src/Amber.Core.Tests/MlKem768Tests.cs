using System.Security.Cryptography;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>ML-KEM-768：系统实现和 BouncyCastle 实现都能用，而且互通（Mac 上只有后者，Windows 11 上多半是前者）。</summary>
[Collection(nameof(MlKemCollection))]
public class MlKem768Tests
{
    [Fact]
    public void BouncyCastle_RoundTrip()
    {
        MlKem768.ForceBouncyCastle = true;
        try
        {
            using var key = MlKem768.Generate();
            var ek = key.ExportEncapsulationKey();
            Assert.Equal(MlKem768.EncapsulationKeySize, ek.Length);
            var (ct, shared) = MlKem768.Encapsulate(ek);
            Assert.Equal(MlKem768.CiphertextSize, ct.Length);
            Assert.Equal(MlKem768.SharedSecretSize, shared.Length);
            Assert.Equal(shared, key.Decapsulate(ct));
            Assert.Throws<CryptographicException>(() => MlKem768.Encapsulate(ek[..100]));
        }
        finally { MlKem768.ForceBouncyCastle = false; }
    }

    /// <summary>一边系统实现、一边 BouncyCastle，两个方向都能算出同一个共享密钥。系统没有 ML-KEM 时跳过。</summary>
    [Fact]
    public void SystemAndBouncyCastle_Interoperate()
    {
#pragma warning disable SYSLIB5006
        if (!MLKem.IsSupported) return;
#pragma warning restore SYSLIB5006
        try
        {
            using var systemKey = MlKem768.Generate();
            MlKem768.ForceBouncyCastle = true;
            var (ct1, s1) = MlKem768.Encapsulate(systemKey.ExportEncapsulationKey());
            using var bcKey = MlKem768.Generate();
            MlKem768.ForceBouncyCastle = false;
            Assert.Equal(s1, systemKey.Decapsulate(ct1));
            var (ct2, s2) = MlKem768.Encapsulate(bcKey.ExportEncapsulationKey());
            Assert.Equal(s2, bcKey.Decapsulate(ct2));
        }
        finally { MlKem768.ForceBouncyCastle = false; }
    }

    /// <summary>整套极端加密握手：发起方用 BouncyCastle，应答方用当前系统默认的实现。</summary>
    [Fact]
    public void ExtremeHandshake_WorksWithBouncyCastle()
    {
        var psk = RandomNumberGenerator.GetBytes(32);
        using var initiator = new NoiseHandshake(NoisePattern.NNhfspsk0, true, [], psk);
        using var responder = new NoiseHandshake(NoisePattern.NNhfspsk0, false, [], psk);
        byte[] m1;
        MlKem768.ForceBouncyCastle = true;
        try { m1 = initiator.WriteMessage("hi"u8); }
        finally { MlKem768.ForceBouncyCastle = false; }
        Assert.Equal("hi"u8.ToArray(), responder.ReadMessage(m1));
        var m2 = responder.WriteMessage("ok"u8);
        MlKem768.ForceBouncyCastle = true;
        try { Assert.Equal("ok"u8.ToArray(), initiator.ReadMessage(m2)); }
        finally { MlKem768.ForceBouncyCastle = false; }
        Assert.Equal(initiator.HandshakeHash, responder.HandshakeHash);
    }
}

/// <summary>ForceBouncyCastle 是全局开关：这组测试不和别的并行。</summary>
[CollectionDefinition(nameof(MlKemCollection), DisableParallelization = true)]
public class MlKemCollection;
