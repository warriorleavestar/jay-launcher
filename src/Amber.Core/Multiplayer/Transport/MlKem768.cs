using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// ML-KEM-768（FIPS 203）。系统有就用 .NET 自带的（Windows 11 新版、Linux 上 OpenSSL 3.5+），
/// 没有时（macOS、较旧的 Linux / Windows）用 BouncyCastle 的实现。两种实现的密钥和密文编码都是 FIPS 203 的标准编码，可以互通。
/// </summary>
internal static class MlKem768
{
    public const int EncapsulationKeySize = 1184;
    public const int CiphertextSize = 1088;
    public const int SharedSecretSize = 32;

    /// <summary>测试用：强制用 BouncyCastle（检验两种实现互通）。</summary>
    internal static bool ForceBouncyCastle { get; set; }

#pragma warning disable SYSLIB5006
    private static bool UseSystem => !ForceBouncyCastle && MLKem.IsSupported;
#pragma warning restore SYSLIB5006

    /// <summary>生成一把临时密钥对。</summary>
    public static IDecapsulationKey Generate() => UseSystem ? new SystemKey() : new BcKey();

    /// <summary>用对方的封装公钥封装：返回 (密文, 共享密钥)。公钥不合法时抛 CryptographicException。</summary>
    public static (byte[] Ciphertext, byte[] Shared) Encapsulate(byte[] encapsulationKey)
    {
        if (encapsulationKey.Length != EncapsulationKeySize) throw new CryptographicException("ML-KEM encapsulation key size");
        if (UseSystem)
        {
#pragma warning disable SYSLIB5006
            using var remote = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, encapsulationKey);
            remote.Encapsulate(out var ciphertext, out var shared);
#pragma warning restore SYSLIB5006
            return (ciphertext, shared);
        }
        try
        {
            var key = MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, encapsulationKey);
            var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
            encapsulator.Init(new ParametersWithRandom(key, new SecureRandom()));
            var ct = new byte[encapsulator.EncapsulationLength];
            var secret = new byte[encapsulator.SecretLength];
            encapsulator.Encapsulate(ct, 0, ct.Length, secret, 0, secret.Length);
            return (ct, secret);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new CryptographicException("ML-KEM encapsulation key", e);
        }
    }

    public interface IDecapsulationKey : IDisposable
    {
        byte[] ExportEncapsulationKey();
        byte[] Decapsulate(byte[] ciphertext);
    }

    private sealed class SystemKey : IDecapsulationKey
    {
#pragma warning disable SYSLIB5006
        private readonly MLKem _key = MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
#pragma warning restore SYSLIB5006
        public byte[] ExportEncapsulationKey() => _key.ExportEncapsulationKey();
        public byte[] Decapsulate(byte[] ciphertext) => _key.Decapsulate(ciphertext);
        public void Dispose() => _key.Dispose();
    }

    private sealed class BcKey : IDecapsulationKey
    {
        private readonly MLKemPrivateKeyParameters _private;
        private readonly MLKemPublicKeyParameters _public;

        public BcKey()
        {
            var generator = new MLKemKeyPairGenerator();
            generator.Init(new MLKemKeyGenerationParameters(new SecureRandom(), MLKemParameters.ml_kem_768));
            var pair = generator.GenerateKeyPair();
            _private = (MLKemPrivateKeyParameters)pair.Private;
            _public = (MLKemPublicKeyParameters)pair.Public;
        }

        public byte[] ExportEncapsulationKey() => _public.GetEncoded();

        public byte[] Decapsulate(byte[] ciphertext)
        {
            if (ciphertext.Length != CiphertextSize) throw new CryptographicException("ML-KEM ciphertext size");
            var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
            decapsulator.Init(_private);
            var secret = new byte[decapsulator.SecretLength];
            decapsulator.Decapsulate(ciphertext, 0, ciphertext.Length, secret, 0, secret.Length);
            return secret;
        }

        public void Dispose() { }
    }
}
