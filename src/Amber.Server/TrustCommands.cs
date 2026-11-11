using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Launch;
using Amber.Core.Multiplayer.Public;

namespace Amber.Server;

/// <summary>
/// Offline key handling and signing review for trust keys (network owners, Jay trustees, directory roots).
/// Before anything is signed the whole content is decoded and printed, and the signer has to type a short code from it,
/// so a second trustee can't sign something they never looked at (privacy plan, item 1).
/// </summary>
internal static class TrustCommands
{
    private static readonly HashSet<string> NetworkFields =
        ["v", "id", "name", "network", "secret", "nodes", "seq", "exp", "home", "config", "dirKeys", "dirRoots", "dirRootsNeed"];
    private static readonly HashSet<string> CertificateFields = ["key", "net", "exp"];

    /// <summary>Reads a secret from the terminal without echo. Refuses when input isn't a terminal.</summary>
    internal static string AskSecret(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("要在终端里输入口令（输入被重定向了）");
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    /// <summary>A new passphrase, typed twice; null with --no-passphrase.</summary>
    internal static string? NewPassphrase(Options options)
    {
        if (options.Has("no-passphrase")) return null;
        while (true)
        {
            var first = AskSecret("给钥匙文件设一个口令（建议 5 个以上随机词，另外记在安全的地方）：");
            if (first.Length < 12) { Console.Error.WriteLine("口令至少 12 个字符。"); continue; }
            if (AskSecret("再输一遍：") == first) return first;
            Console.Error.WriteLine("两次不一样，重来。");
        }
    }

    /// <summary>Loads a trust key file, asking for its passphrase when it is sealed.</summary>
    internal static TrustSigner LoadKey(string path) => TrustKeyFile.Read(File.ReadAllText(path), () => AskSecret($"{Path.GetFileName(path)} 的口令："));

    /// <summary>Writes a new key file; refuses to overwrite. Private to the user on Unix.</summary>
    internal static void WriteKey(string path, ReadOnlySpan<byte> seed, string? passphrase)
    {
        var write = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) write.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new StreamWriter(path, Encoding.ASCII, write);
        file.Write(TrustKeyFile.Format(seed, passphrase));
    }

    /// <summary>The public key lines everyone may see: full key (for JayTrust.Keys), id (for dirRoots) and a short fingerprint.</summary>
    internal static IEnumerable<string> PublicLines(TrustSigner signer)
    {
        yield return $"公钥（写进 JayTrust.Keys 或 JayTrust.ReleaseKeys，不是秘密）：{Convert.ToBase64String(signer.PublicKey)}";
        yield return $"钥匙编号（写进 dirRoots）：{Convert.ToBase64String(signer.Id)}";
        yield return $"指纹（核对用）：{Fingerprint(signer.Id)}";
    }

    /// <summary>Refuses a release key for network, directory or hardware signing, and a network key for releases.</summary>
    internal static TrustSigner ForUse(TrustSigner signer, bool release) =>
        ForUse(signer, release, JayTrust.IsNetworkKey, JayTrust.IsReleaseKey);

    internal static TrustSigner ForUse(TrustSigner signer, bool release, Func<byte[], bool> isNetworkKey, Func<byte[], bool> isReleaseKey)
    {
        if (release && isNetworkKey(signer.Id))
            throw new InvalidOperationException("这是审核网络用的钥匙，发版要用另一把发版钥匙（jay-release.key）");
        if (!release && isReleaseKey(signer.Id))
            throw new InvalidOperationException("这是发版钥匙，审核网络、目录证书要用另一把信任钥匙（jay-trustee.key）");
        return signer;
    }

    internal static string Fingerprint(byte[] id) => Group(Convert.ToHexString(id.AsSpan(0, 8)));

    private static string Group(string hex) => string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));

    /// <summary>What a network string says (unknown fields throw), one field per line (only fields that differ from <paramref name="previous"/>, when given).</summary>
    internal static List<string> DescribeNetwork(string text, string? previous)
    {
        var (payload, signatures) = NetworkString.Split(text);
        var content = JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException("连接串内容不是 JSON 对象");
        var unknown = content.Select(p => p.Key).Where(k => !NetworkFields.Contains(k)).ToList();
        var lines = new List<string>();
        foreach (var k in unknown) lines.Add($"不认识的字段 {k}：{content[k]?.ToJsonString()}");
        if (unknown.Count > 0) throw new InvalidDataException("连接串里有不认识的字段，拒绝签名（先升级 jay-server）：\n" + string.Join("\n", lines));
        var info = NetworkInfo.FromJson(content);
        var now = NetworkFieldsOf(info);
        if (previous is not null)
        {
            var (prevPayload, _) = NetworkString.Split(previous);
            var before = NetworkFieldsOf(NetworkInfo.FromJson(JsonNode.Parse(prevPayload) as JsonObject ?? throw new InvalidDataException("--prev 不是连接串")));
            lines.Add("和上一版相比，变了的字段：");
            foreach (var (name, value) in now.Where(f => !before.Contains(f))) lines.Add($"  + {name}：{value}");
            foreach (var (name, value) in before.Where(f => !now.Contains(f))) lines.Add($"  - {name}：{value}");
            if (lines.Count == 1) lines.Add("  （没有变化）");
        }
        else foreach (var (name, value) in now) lines.Add($"{name}：{value}");
        lines.AddRange(SignerLines(signatures.Select(s => s.Key)));
        lines.Add($"被签内容的 SHA-256（和另一位信任人对一下）：{Digest(payload)}");
        return lines;
    }

    private static List<(string, string)> NetworkFieldsOf(NetworkInfo i)
    {
        var f = new List<(string, string)>
        {
            ("网络编号", i.Id), ("名字", i.Name), ("EasyTier 网络名", i.NetworkName), ("EasyTier 密钥", i.Secret), ("版本号", i.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("过期", i.Expires?.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "不过期"),
            ("主页", i.Home ?? "无"), ("配置文件", i.Config is { } c ? $"{c.Url}  SHA-256 {c.Sha256}" : "无"),
            ("目录根钥匙要几把签", i.DirectoryRootsNeed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        foreach (var n in i.Nodes) f.Add(("节点", n));
        foreach (var k in i.DirectoryKeys ?? []) f.Add(("目录公钥", Convert.ToBase64String(k)));
        foreach (var k in i.DirectoryRoots ?? []) f.Add(("目录根钥匙", Convert.ToBase64String(k)));
        return f;
    }

    /// <summary>What a directory certificate says.</summary>
    internal static List<string> DescribeCertificate(ReadOnlySpan<byte> certificate)
    {
        var cert = DirectoryCertificate.Parse(certificate) ?? throw new InvalidDataException("不是有效的目录证书（格式或签名不对）");
        var content = JsonNode.Parse(cert.Content) as JsonObject ?? throw new InvalidDataException("证书内容不是 JSON 对象");
        var unknown = content.Select(p => p.Key).Where(k => !CertificateFields.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException("证书里有不认识的字段，拒绝签名：\n" + string.Join("\n", unknown.Select(k => $"不认识的字段 {k}：{content[k]?.ToJsonString()}")));
        var lines = DescribeCertificateContent(cert.DirectoryKey, cert.Network, cert.Expires);
        lines.AddRange(SignerLines(cert.Signers));
        lines.Add($"被签内容的 SHA-256（和另一位信任人对一下）：{Digest(cert.Content)}");
        return lines;
    }

    internal static List<string> DescribeCertificateContent(byte[] key, string network, DateTimeOffset expires) =>
    [
        $"目录节点公钥：{Convert.ToBase64String(key)}",
        $"网络：{network}",
        $"到期：{expires.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture)}",
    ];

    /// <summary>
    /// What a release signature file says, checked against the SHA256SUMS.txt next to it: every file and hash, the commit,
    /// the trustee list, and who already signed (and whether each of those signatures is valid). Throws when the file is
    /// malformed or names a different SHA256SUMS.txt.
    /// </summary>
    internal static List<string> DescribeRelease(string manifestJson, byte[] sums, TrustList own)
    {
        var manifest = ReleaseManifest.Parse(manifestJson) ?? throw new InvalidDataException("不是有效的发布签名文件（格式不对或有不认识的字段），拒绝签名");
        var s = manifest.Statement;
        if (Convert.ToHexStringLower(SHA256.HashData(sums)) != s.SumsHash)
            throw new InvalidDataException("签名文件写的 SHA256SUMS.txt 和手上这份不一样，拒绝签名");
        var files = ReleaseSignature.ParseSums(Encoding.UTF8.GetString(sums));
        var lines = new List<string>
        {
            $"版本号：{s.Tag}",
            $"发布序号：{s.Seq}",
            $"提交号：{s.Commit}（到仓库里对一下，是不是要发的那个提交）",
            $"信任人名单：{Group(s.TrustHash[..16].ToUpperInvariant())}{(s.TrustHash == own.Hash ? "（就是现在这份）" : "（不是程序自带的名单，要有 trust-chain.json 才认）")}",
            $"发布日志：{(s.LogHead == ReleaseStatement.NoLog ? "还没有" : s.LogHead)}",
            $"文件（{files.Count} 个）：",
        };
        foreach (var (name, hash) in files.OrderBy(p => p.Key, StringComparer.Ordinal)) lines.Add($"  {hash}  {name}");
        var message = s.Bytes();
        if (manifest.Signatures.Count == 0) lines.Add("已经签过的：还没有人");
        for (var i = 0; i < manifest.Signatures.Count; i++)
        {
            var sig = manifest.Signatures[i];
            var valid = own.Votes(message, [sig]) == 1;
            lines.Add($"已签 {i + 1}：{Fingerprint(sig.KeyId)}{(valid ? "（Jay 信任人，签名对）" : "（不是这份名单里的人，或签名不对）")}");
        }
        lines.Add($"被签内容的 SHA-256（和另一位信任人对一下）：{Digest(message)}");
        return lines;
    }

    /// <summary>What a trustee list change says: who leaves, who joins, how many must sign, seq.</summary>
    internal static List<string> DescribeTrustChange(TrustList from, TrustList to, long seq)
    {
        var lines = new List<string> { $"旧名单：{Group(from.Hash[..16].ToUpperInvariant())}，{from.Ids.Count} 人里要 {from.Need} 人签" };
        foreach (var id in from.Ids) lines.Add($"  {(to.Ids.Any(x => x.AsSpan().SequenceEqual(id)) ? "留下" : "移除")}：{Fingerprint(id)}");
        lines.Add($"新名单：{Group(to.Hash[..16].ToUpperInvariant())}，{to.Ids.Count} 人里要 {to.Need} 人签");
        foreach (var id in to.Ids.Where(id => !from.Ids.Any(x => x.AsSpan().SequenceEqual(id)))) lines.Add($"  新加：{Fingerprint(id)}");
        lines.Add($"序号：{seq}");
        lines.Add($"新名单编号（完整）：{to.Hash}");
        return lines;
    }

    private static IEnumerable<string> SignerLines(IEnumerable<byte[]> signers)
    {
        var trustees = JayTrust.Quorum;
        var list = signers.ToList();
        if (list.Count == 0) { yield return "已经签过的：还没有人"; yield break; }
        for (var i = 0; i < list.Count; i++)
            yield return $"已签 {i + 1}：{Fingerprint(list[i])}{(trustees.Count([list[i]]) == 1 ? "（Jay 信任人）" : "")}";
    }

    private static string Digest(byte[] bytes) => Group(Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 8)));

    /// <summary>The code to type: the last 6 characters of <paramref name="value"/> (all of it when shorter).</summary>
    internal static string CodeOf(string value) => value.Length <= 6 ? value : value[^6..];

    /// <summary>
    /// Prints <paramref name="lines"/> and asks for <paramref name="expected"/> to be typed. Refuses without an interactive
    /// terminal; there is deliberately no switch to skip it.
    /// </summary>
    internal static bool Confirm(IReadOnlyList<string> lines, string what, string expected, TextWriter output, TextReader input, bool interactive)
    {
        output.WriteLine("──── 要签的内容 ────");
        foreach (var l in lines) output.WriteLine(l);
        output.WriteLine("────────────────────");
        if (!interactive) { output.WriteLine("要在终端里确认后才签（输入被重定向了）。没有签。"); return false; }
        output.Write($"看清楚了再签：手打{what}的最后 {expected.Length} 位「{expected}」并回车（别的任何输入都不签）：");
        output.Flush();
        if ((input.ReadLine() ?? "").Trim() == expected) return true;
        output.WriteLine("没有对上，没有签。");
        return false;
    }

    internal static bool ConfirmOnConsole(IReadOnlyList<string> lines, string what, string expected) =>
        Confirm(lines, what, expected, Console.Error, Console.In, !Console.IsInputRedirected);
}
