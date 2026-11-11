using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Amber.NetworkServer.Hardware;

/// <summary>
/// Seals a short secret to this machine's TPM so a copy of the node's files on another machine can't open it.
/// </summary>
public interface ITpmSealer
{
    /// <summary>Whether this machine has a usable TPM (and the tools to talk to it).</summary>
    bool Available { get; }

    /// <summary>Whether <see cref="Seal"/> also binds to the Secure Boot state (TPM PCR 7), so a changed boot chain can't unseal.</summary>
    bool BindsBootState { get; }

    byte[] Seal(byte[] secret);

    /// <summary>Opens a sealed blob. Throws <see cref="TpmUnsealException"/> when the TPM refuses (different TPM or changed boot state).</summary>
    byte[] Unseal(byte[] blob);
}

public sealed class TpmUnsealException(string message) : CryptographicException(message);

/// <summary>No TPM: nothing is sealed (the keys are then protected by the hardware-derived key alone).</summary>
public sealed class NoTpm : ITpmSealer
{
    public static readonly NoTpm Instance = new();
    public bool Available => false;
    public bool BindsBootState => false;
    public byte[] Seal(byte[] secret) => throw new NotSupportedException();
    public byte[] Unseal(byte[] blob) => throw new TpmUnsealException("no TPM");
}

public static class TpmSealers
{
    /// <summary>The TPM of this machine: tpm2-tools on Linux, the Platform Crypto Provider on Windows, otherwise none.</summary>
    public static ITpmSealer ForThisMachine()
    {
        if (OperatingSystem.IsLinux() && Tpm2Tools.IsAvailable()) return new Tpm2Tools();
        if (OperatingSystem.IsWindows() && WindowsPlatformTpm.IsAvailable()) return new WindowsPlatformTpm();
        return NoTpm.Instance;
    }
}

/// <summary>
/// Linux: tpm2-tools against /dev/tpmrm0 (the service user needs the tss group, no root). The secret is sealed under a
/// primary key of the owner hierarchy with a policy on PCR 7 (Secure Boot state), so it opens only on this TPM and only
/// while the machine boots the same way. Secrets go through stdin/stdout, never to disk.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class Tpm2Tools : ITpmSealer
{
    private const string Pcr = "sha256:7";

    public bool Available => true;
    public bool BindsBootState => true;

    public static bool IsAvailable() =>
        (File.Exists("/dev/tpmrm0") || File.Exists("/dev/tpm0")) && SystemHardwareSource.Run("tpm2_getcap", ["properties-fixed"]) is not null;

    /// <summary>A stable id of this TPM: its endorsement key's public part (persistent handle, or re-derived from the endorsement seed).</summary>
    public static string? EndorsementKeyId()
    {
        if (!IsAvailable()) return null;
        using var temp = new TempDir();
        var pub = Path.Combine(temp.Path, "ek.pub");
        if (SystemHardwareSource.Run("tpm2_readpublic", ["-c", "0x81010001", "-o", pub]) is null)
        {
            var ctx = Path.Combine(temp.Path, "ek.ctx");
            if (SystemHardwareSource.Run("tpm2_createek", ["-G", "rsa", "-c", ctx, "-u", pub]) is null) return null;
            SystemHardwareSource.Run("tpm2_flushcontext", [ctx]);
        }
        return File.Exists(pub) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pub))) : null;
    }

    public byte[] Seal(byte[] secret)
    {
        using var temp = new TempDir();
        string P(string name) => Path.Combine(temp.Path, name);
        Require(SystemHardwareSource.Run("tpm2_createprimary", ["-C", "o", "-g", "sha256", "-G", "ecc", "-c", P("primary.ctx")]), "createprimary");
        Require(SystemHardwareSource.Run("tpm2_pcrread", ["-o", P("pcr.bin"), Pcr]), "pcrread");
        Require(SystemHardwareSource.Run("tpm2_createpolicy", ["--policy-pcr", "-l", Pcr, "-f", P("pcr.bin"), "-L", P("policy.digest")]), "createpolicy");
        Require(SystemHardwareSource.Run("tpm2_create",
            ["-C", P("primary.ctx"), "-L", P("policy.digest"), "-a", "fixedtpm|fixedparent|noda", "-i", "-", "-u", P("seal.pub"), "-r", P("seal.priv")], secret), "create");
        var pub = File.ReadAllBytes(P("seal.pub"));
        var priv = File.ReadAllBytes(P("seal.priv"));
        var blob = new byte[4 + pub.Length + priv.Length];
        BinaryPrimitives.WriteInt32BigEndian(blob, pub.Length);
        pub.CopyTo(blob, 4);
        priv.CopyTo(blob, 4 + pub.Length);
        return blob;
    }

    public byte[] Unseal(byte[] blob)
    {
        if (blob.Length < 4) throw new TpmUnsealException("bad blob");
        var pubLength = BinaryPrimitives.ReadInt32BigEndian(blob);
        if (pubLength <= 0 || pubLength > blob.Length - 4) throw new TpmUnsealException("bad blob");
        using var temp = new TempDir();
        string P(string name) => Path.Combine(temp.Path, name);
        File.WriteAllBytes(P("seal.pub"), blob[4..(4 + pubLength)]);
        File.WriteAllBytes(P("seal.priv"), blob[(4 + pubLength)..]);
        if (SystemHardwareSource.Run("tpm2_createprimary", ["-C", "o", "-g", "sha256", "-G", "ecc", "-c", P("primary.ctx")]) is null
            || SystemHardwareSource.Run("tpm2_load", ["-C", P("primary.ctx"), "-u", P("seal.pub"), "-r", P("seal.priv"), "-c", P("seal.ctx")]) is null)
            throw new TpmUnsealException("this TPM can't load the sealed key (different TPM?)");
        return SystemHardwareSource.RunBytes("tpm2_unseal", ["-c", P("seal.ctx"), "-p", "pcr:" + Pcr])
            ?? throw new TpmUnsealException("the TPM refused to unseal: the Secure Boot state (PCR 7) changed");
    }

    private static void Require(string? result, string step)
    {
        if (result is null) throw new CryptographicException("tpm2_" + step + " failed");
    }

    /// <summary>A private scratch folder (0700) for TPM context files. Holds no secrets.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("jay-tpm-").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}

/// <summary>
/// Windows: a non-exportable RSA key in the Microsoft Platform Crypto Provider (the TPM) wraps the secret. Only this TPM
/// can unwrap it. Windows doesn't bind it to PCR 7 here; <see cref="RuntimeIntegrity"/> checks Secure Boot instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformTpm : ITpmSealer
{
    private const string Provider = "Microsoft Platform Crypto Provider";
    private const string KeyName = "Jay network node key";

    public bool Available => true;
    public bool BindsBootState => false;

    public static bool IsAvailable()
    {
        try { return CngKey.Exists(KeyName, new CngProvider(Provider)) || OpenOrCreate() is { } k && Dispose(k); }
        catch (CryptographicException) { return false; }
    }

    private static bool Dispose(CngKey key) { key.Dispose(); return true; }

    private static CngKey OpenOrCreate()
    {
        var provider = new CngProvider(Provider);
        if (CngKey.Exists(KeyName, provider)) return CngKey.Open(KeyName, provider);
        return CngKey.Create(CngAlgorithm.Rsa, KeyName, new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.None,
            Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
        });
    }

    // OAEP with SHA-1 is what every TPM 2.0 behind the Platform Crypto Provider supports; it is still a sound key wrap.
    public byte[] Seal(byte[] secret)
    {
        using var key = OpenOrCreate();
        using var rsa = new RSACng(key);
        return rsa.Encrypt(secret, RSAEncryptionPadding.OaepSHA1);
    }

    public byte[] Unseal(byte[] blob)
    {
        try
        {
            using var key = CngKey.Open(KeyName, new CngProvider(Provider));
            using var rsa = new RSACng(key);
            return rsa.Decrypt(blob, RSAEncryptionPadding.OaepSHA1);
        }
        catch (CryptographicException e) { throw new TpmUnsealException("this TPM can't unwrap the node key: " + e.Message); }
    }
}
