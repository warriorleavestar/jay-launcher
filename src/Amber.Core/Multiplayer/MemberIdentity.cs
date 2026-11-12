using System.Security.Cryptography;
using System.Text;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer;

/// <summary>
/// This device's multiplayer identity. The file holds the device seed (<see cref="DeviceKeys"/>, privacy plan item 7):
/// the X25519 handshake key hosts know this device by, plus the Ed25519 and ML-DSA-65 device signing keys, all derived
/// from it. An identity file from before device seeds (a bare X25519 key) is replaced by a new seed with a warning.
/// 这台电脑上 Amber 的联机身份：一把长期的 X25519 密钥（每次安装一把），房主靠它认出“还是那个人”。
/// 保存在数据目录的 identity.key，和账号一样加密：Windows 用 DPAPI（当前用户），macOS/Linux 用仅当前用户可读（0600）的本机密钥文件 + AES-GCM。
/// DPAPI 拒绝时（比如 SSH 密钥登录、没有密码），Windows 也退回本机密钥文件。
/// 私钥绝不写进日志；ToString 只给指纹。文件坏了不会崩：换一把新的并给出提示（之前认识你的房主会把你当新成员）。
/// </summary>
public sealed class MemberIdentity : IDisposable
{
    public const string FileName = "identity.key";
    private const string WrapKeyFileName = "identity.wrap";
    private static readonly byte[] Entropy = "Amber.Launcher.Identity"u8.ToArray();
    private static readonly byte[] MagicDpapi = "AMBID1"u8.ToArray();
    private static readonly byte[] MagicAesGcm = "AMBID2"u8.ToArray();
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly string _directory;
    private readonly Lock _gate = new();
    private DeviceKeys _device;
    // 重置后换下来的旧密钥：可能还有握手在用，等这个对象 Dispose 时再清零。
    private readonly List<DeviceKeys> _retired = [];

    /// <summary>Stored plaintext: a format byte, then the 32-byte device seed.</summary>
    private const byte SeedFormat = 0x02;
    private const int StoredLength = 1 + DeviceKeys.SeedLength;

    private MemberIdentity(string directory, DeviceKeys device, string? warning, bool persisted = true)
    {
        _directory = directory;
        _device = device;
        Warning = warning;
        IsPersisted = persisted;
    }

    /// <summary>false：这次读身份文件时遇到读写错误（比如被杀毒软件暂时锁住），用的是只在内存里的临时身份，没有覆盖原文件。</summary>
    public bool IsPersisted { get; private set; }

    /// <summary>当前的密钥对。<see cref="Reset"/> 之后换成新的，旧的被清零，别长期持有。</summary>
    public X25519KeyPair KeyPair => Device.Dh;

    /// <summary>All of this device's keys. <see cref="Reset"/> replaces them; don't hold on to them for long.</summary>
    public DeviceKeys Device { get { lock (_gate) return _device; } }

    public byte[] PublicKey => (byte[])KeyPair.PublicKey.Clone();

    /// <summary>给人看的指纹，比如 K7Q2-M9XD-4HZP-8RW2。</summary>
    public string Fingerprint => FingerprintOf(KeyPair.PublicKey);

    /// <summary>读取时出了问题（文件坏了、换了系统用户）而换了新身份时的说明；正常时为 null。</summary>
    public string? Warning { get; private set; }

    private string KeyPath => Path.Combine(_directory, FileName);

    /// <summary>
    /// 读取身份；没有就新建并保存。文件内容坏了或解不开（密码学或格式错误）：原文件留一份 .broken，换一把新的，Warning 说明原因。
    /// 读写出错（文件被占用、权限一时不对）：不动原文件，这次用内存里的临时身份，Warning 说明，下次启动再读。
    /// </summary>
    public static MemberIdentity LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            try
            {
                var raw = Unprotect(directory, File.ReadAllBytes(path), l => l is StoredLength or X25519KeyPair.Length);
                try
                {
                    if (raw.Length == StoredLength)
                    {
                        if (raw[0] != SeedFormat) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
                        return new MemberIdentity(directory, DeviceKeys.FromSeed(raw.AsSpan(1)), null);
                    }
                    // A bare X25519 key from before device seeds: it can't give the signing keys, so this device starts
                    // over with a seed (no backward compatibility, privacy plan item 7). Saving overwrites the old key.
                    var upgraded = new MemberIdentity(directory, DeviceKeys.Generate(), L.T("core.mp.identity.upgraded"));
                    try { upgraded.Save(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
                    {
                        upgraded.IsPersisted = false;
                        upgraded.Warning = L.T("core.mp.identity.unsaved");
                    }
                    return upgraded;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(raw);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MemberIdentity(directory, DeviceKeys.Generate(), L.T("core.mp.identity.unreadable"), persisted: false);
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                try { File.Copy(path, path + ".broken", true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
                var fresh = new MemberIdentity(directory, DeviceKeys.Generate(), L.T("core.mp.identity.corrupt"));
                fresh.Save();
                return fresh;
            }
        }
        var created = new MemberIdentity(directory, DeviceKeys.Generate(), null);
        try
        {
            created.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Can't write the identity (read-only data folder, no key protection available): keep a temporary
            // in-memory identity so the multiplayer page still opens, and say so.
            created.IsPersisted = false;
            created.Warning = L.T("core.mp.identity.unsaved");
        }
        return created;
    }

    /// <summary>换一个新身份（用户点“重置身份”）。之前认识你的房主会把你当成新成员。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            var fresh = DeviceKeys.Generate();
            var old = _device;
            _device = fresh;
            try
            {
                Save();
            }
            catch
            {
                _device = old;
                fresh.Dispose();
                throw;
            }
            Warning = null;
            IsPersisted = true;
            _retired.Add(old);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_directory);
        var seed = _device.ExportSeed();
        byte[] raw = [SeedFormat, .. seed];
        CryptographicOperations.ZeroMemory(seed);
        try
        {
            var data = Protect(_directory, raw);
            var temp = KeyPath + ".tmp";
            WritePrivate(temp, data);
            File.Move(temp, KeyPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>公钥的指纹：SHA-256 的前 10 字节，Crockford Base32（16 个字符），每 4 个一组。</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> publicKey)
    {
        var digest = SHA256.HashData(publicKey);
        var text = new StringBuilder(19);
        // 80 位正好 16 个 5 位。
        var bits = 0;
        var buffer = 0;
        var written = 0;
        for (var i = 0; i < 10; i++)
        {
            buffer = (buffer << 8) | digest[i];
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written > 0 && written % 4 == 0) text.Append('-');
                text.Append(Crockford[(buffer >> bits) & 31]);
                written++;
            }
        }
        return text.ToString();
    }

    public override string ToString() => Fingerprint;

    public void Dispose()
    {
        lock (_gate)
        {
            _device.Dispose();
            foreach (var old in _retired) old.Dispose();
            _retired.Clear();
        }
    }

    /// <summary>Set once DPAPI has refused on this machine (or by tests); later saves go straight to the wrap-key file.</summary>
    internal static bool DpapiUnavailable { get; set; }

    internal static byte[] Protect(string directory, byte[] raw)
    {
        if (OperatingSystem.IsWindows() && !DpapiUnavailable)
        {
            try
            {
                return [.. MagicDpapi, .. ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser)];
            }
            catch (CryptographicException)
            {
                // DPAPI refuses when the user profile's master key can't be opened, e.g. an SSH key logon without a password.
                // Fall back to the same wrap-key file that macOS and Linux use, kept next to the identity in the user's data folder.
                DpapiUnavailable = true;
            }
        }
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[raw.Length];
        var tag = new byte[16];
        var key = WrapKey(directory, create: true);
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, raw, cipher, tag, Entropy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return [.. MagicAesGcm, .. nonce, .. tag, .. cipher];
    }

    /// <summary>解开 <see cref="Protect"/> 包起来的密钥；<paramref name="length"/> 是原文的长度。</summary>
    internal static byte[] Unprotect(string directory, byte[] stored, int length) => Unprotect(directory, stored, l => l == length);

    /// <summary>Like <see cref="Unprotect(string, byte[], int)"/>, accepting any plaintext length <paramref name="lengthOk"/> allows.</summary>
    internal static byte[] Unprotect(string directory, byte[] stored, Func<int, bool> lengthOk)
    {
        if (stored.AsSpan().StartsWith(MagicDpapi))
        {
            if (!OperatingSystem.IsWindows()) throw new CryptographicException(L.T("core.mp.identity.otherplatform"));
            var raw = ProtectedData.Unprotect(stored[MagicDpapi.Length..], Entropy, DataProtectionScope.CurrentUser);
            if (!lengthOk(raw.Length))
            {
                CryptographicOperations.ZeroMemory(raw);
                throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            }
            return raw;
        }
        if (stored.AsSpan().StartsWith(MagicAesGcm))
        {
            var body = stored.AsSpan(MagicAesGcm.Length);
            var length = body.Length - 12 - 16;
            if (length <= 0 || !lengthOk(length)) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            var raw = new byte[length];
            var key = WrapKey(directory, create: false);
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(body[..12], body[28..], body[12..28], raw, Entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            return raw;
        }
        throw new CryptographicException(L.T("core.mp.identity.corrupt"));
    }

    /// <summary>macOS/Linux：包身份用的本机密钥，0600，和身份文件放一起。</summary>
    private static byte[] WrapKey(string directory, bool create)
    {
        var path = Path.Combine(directory, WrapKeyFileName);
        // 长度不对的包裹密钥（比如第一次写入时断电留下的空文件）不可能加密过任何身份：
        // 需要新建时挪成 .broken 再重建，否则损坏分支里重新保存还会读到它，每次启动都失败。
        if (create && File.Exists(path) && new FileInfo(path).Length != 32)
            File.Move(path, path + ".broken", true);
        if (!File.Exists(path))
        {
            if (!create) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
            var created = RandomNumberGenerator.GetBytes(32);
            try
            {
                // CreateNew：两个 Jay 同时第一次启动时，只有一个能写成，另一个读已经写好的那把，不会互相覆盖。
                WritePrivate(path, created, FileMode.CreateNew);
                return created;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(created);
            }
        }
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new CryptographicException(L.T("core.mp.identity.corrupt"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    /// <summary>写文件；非 Windows 上一创建就是 0600（不先以默认权限创建再改）。</summary>
    internal static void WritePrivate(string path, byte[] data, FileMode mode = FileMode.Create)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        {
            stream.Write(data);
            stream.Flush(true);
        }
        // 已经存在的文件不会按 UnixCreateMode 改权限，写完再收紧一次。
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
