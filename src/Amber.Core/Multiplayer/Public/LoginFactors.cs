using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Amber.Core.Multiplayer.Public;

/// <summary>
/// 登录的第二步验证（两步验证 / 其他验证方式）。网络节点上可以挂好几个：密码对了以后，
/// 每个 <see cref="AppliesTo"/> 这个账号的验证都要过。Jay 内置 TOTP（验证器 App 的 6 位码），
/// 第三方网络可以自己实现这个接口（短信、邮件码、硬件钥匙、自己的审核系统……），
/// 或者用 <see cref="CommandFactor"/> 接一个外部程序，不用改 Jay。
/// </summary>
public interface ILoginFactor
{
    /// <summary>验证方式的名字（小写字母、数字、横线，最多 24 个字），比如 "totp"。</summary>
    string Name { get; }

    /// <summary>Jay 里给用户看的提示，比如“验证器 App 里的 6 位数字”。</summary>
    string Prompt { get; }

    /// <summary>这个账号要不要过这一步。</summary>
    bool AppliesTo(string user);

    /// <summary>核对用户填的内容。<paramref name="response"/> 已经去掉首尾空格，最多 256 个字。</summary>
    bool Verify(string user, string response, DateTimeOffset now);
}

/// <summary>节点要求的一步验证：名字和提示（发给 Jay 显示）。</summary>
public sealed record LoginFactorPrompt(string Name, string Prompt)
{
    public const int MaxResponse = 256;
    private static readonly Regex NamePattern = new("^[a-z0-9-]{1,24}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>从节点回复里读，名字不合规的丢掉，提示清理控制字符、限长。</summary>
    public static LoginFactorPrompt? From(string? name, string? prompt) =>
        IsValidName(name) ? new LoginFactorPrompt(name!, KnockProtocol.SanitizeName(prompt ?? name).Trim() is { Length: > 0 } p ? p[..Math.Min(p.Length, 100)] : name!) : null;
}

/// <summary>需要两步验证：Jay 收到后让用户填 <see cref="Need"/> 里的每一项，再登录一次。</summary>
public sealed class SecondFactorRequiredException(IReadOnlyList<LoginFactorPrompt> need, bool wrong, string message) : IOException(message)
{
    public IReadOnlyList<LoginFactorPrompt> Need { get; } = need;

    /// <summary>上次填的不对（不是第一次要）。</summary>
    public bool Wrong { get; } = wrong;
}

/// <summary>
/// TOTP（RFC 6238：HMAC-SHA1、30 秒、6 位），就是 Google Authenticator、Microsoft Authenticator 这类 App 里的码。
/// 允许前后各差一个 30 秒；同一个码用过一次就不能再用。
/// </summary>
public sealed class TotpFactor(AccountStore accounts) : ILoginFactor
{
    public const int Digits = 6;
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, long> _lastUsed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Name => "totp";
    public string Prompt => Localization.L.T("core.mp.factor.totp");

    public bool AppliesTo(string user) => accounts.TotpSecret(user) is not null;

    public bool Verify(string user, string response, DateTimeOffset now)
    {
        if (accounts.TotpSecret(user) is not { } secret) return false;
        var digits = response.Replace(" ", "");
        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit)) return false;
        var counter = now.ToUnixTimeSeconds() / (long)Step.TotalSeconds;
        for (var d = -1; d <= 1; d++)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, counter + d)), Encoding.ASCII.GetBytes(digits))) continue;
            lock (_gate)
            {
                if (_lastUsed.TryGetValue(user, out var last) && counter + d <= last) return false;
                _lastUsed[user] = counter + d;
            }
            return true;
        }
        return false;
    }

    /// <summary>算某个时间段的码（RFC 4226 的动态截断）。</summary>
    public static string Code(byte[] secret, long counter)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = HMACSHA1.HashData(secret, message);
        var offset = hash[^1] & 0x0F;
        var value = (BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Code(byte[] secret, DateTimeOffset at) => Code(secret, at.ToUnixTimeSeconds() / (long)Step.TotalSeconds);

    /// <summary>新密钥（20 字节，RFC 4226 推荐长度）。</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>验证器 App 扫码 / 手动输入用的 otpauth:// 地址。</summary>
    public static string Uri(string network, string user, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(network + ":" + user)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(network)}&digits={Digits}&period={(int)Step.TotalSeconds}";

    /// <summary>RFC 4648 Base32（不带补位），验证器 App 手动输入的格式。</summary>
    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}

/// <summary>
/// 接外部程序的验证方式：网络维护者写一个程序（任何语言），Jay 节点把 JSON
/// <c>{"user": "...", "response": "...", "network": "..."}</c> 写到它的标准输入，退出码 0 = 通过。
/// 比如发邮件验证码、查自己的会员系统、接短信平台。程序 10 秒没结束算不通过。
/// <c>applies</c> 参数为 null 时对所有账号生效。
/// </summary>
public sealed class CommandFactor(string name, string prompt, string program, IReadOnlyList<string> arguments, string network,
    Func<string, bool>? applies = null) : ILoginFactor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public string Name { get; } = LoginFactorPrompt.IsValidName(name) ? name : throw new ArgumentException(name);
    public string Prompt => prompt;

    public bool AppliesTo(string user) => applies?.Invoke(user) ?? true;

    public bool Verify(string user, string response, DateTimeOffset now)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            process.StandardInput.Write(new System.Text.Json.Nodes.JsonObject { ["user"] = user, ["response"] = response, ["network"] = network }.ToJsonString());
            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }
}
