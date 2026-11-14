using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 的“经别人上网”（root 守护里运行），对应 Windows 的 ExitRoutingScript / ExitDnsScript / ExitTrafficPolicyScript：
/// 1. 启动 easytier-core 之前，给每个承载地址（节点、直连对端）加一条经物理网关的主机路由，免得 EasyTier 的两条 /1 路由把承载本身也吸进隧道；
/// 2. 虚拟网卡就绪后把 DNS 换成 1.1.1.1 / 8.8.8.8（经隧道）：Linux 用 systemd-resolved，没有的话临时覆盖 /etc/resolv.conf；macOS 改主服务的 DNS；
/// 3. 防漏：只放行回环、虚拟网卡、发往承载地址的包和 DHCP，其他出站一律丢弃（Linux 用单独的 nftables 表，macOS 用 pf 锚点），
///    隧道断了也不会退回直接上网。
/// 结束时按相反顺序恢复。守护被强制结束来不及恢复时，下次启动先清掉上次留下的表、锚点和路由（它们都有固定的标记）。
/// 这里的提示只给开发者看，不翻译。
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixExitNetwork
{
    internal static readonly string[] DnsServers = ExitDnsServers.All;

    /// <summary>Linux：本模块加的路由都带这个 proto 号，崩溃后能认出来清掉。</summary>
    internal const int RouteProto = 201;
    internal const string NftTable = "jay_exit";
    internal const string PfAnchor = "com.apple/250.JayExit";

    /// <summary>测试用：替换执行外部命令（程序、参数、标准输入）→（退出码、输出）。</summary>
    internal static Func<string, IReadOnlyList<string>, string?, (int Code, string Output)>? RunForTest;

    private readonly ExitNetworkPlan _plan;
    private readonly List<(string Address, string Device)> _routes = [];
    private string? _tun;
    private bool _firewall;
    private string? _pfToken;
    private bool _resolved;
    private bool _resolvConfReplaced;
    private MacDnsBackup? _macDns;

    public UnixExitNetwork(ExitNetworkPlan plan) => _plan = plan;

    /// <summary>上次守护被强制结束时留下的规则和路由：每次启动出口之前先清掉。</summary>
    public static void CleanupStale()
    {
        if (OperatingSystem.IsLinux())
        {
            Run("ip", ["-4", "route", "flush", "proto", RouteProto.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            Run("nft", ["delete", "table", "inet", NftTable]);
            RestoreResolvConf();
        }
        else if (OperatingSystem.IsMacOS())
        {
            Run("/sbin/pfctl", ["-a", PfAnchor, "-F", "all"]);
        }
    }

    /// <summary>启动 easytier-core 之前：查好每个承载地址现在走哪个网关和网卡，加主机路由。</summary>
    public void EnterRoutes()
    {
        CleanupStale();
        foreach (var address in _plan.UnderlayAddresses)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.ToString() != address)
                throw new IOException("Unsupported exit underlay address");
            var (gateway, device) = LookupRoute(address);
            if (OperatingSystem.IsLinux())
            {
                var args = new List<string> { "-4", "route", "replace", address + "/32" };
                if (gateway is not null) args.AddRange(["via", gateway]);
                args.AddRange(["dev", device, "proto", RouteProto.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                Check("ip", args);
            }
            else
            {
                var args = new List<string> { "-n", "add", "-host", address };
                if (gateway is not null) args.Add(gateway);
                else args.AddRange(["-interface", device]);
                Check("/sbin/route", args);
            }
            _routes.Add((address, device));
        }
    }

    /// <summary>当前路由表里去这个地址的网关（直连时为 null）和网卡。</summary>
    internal static (string? Gateway, string Device) LookupRoute(string address)
    {
        if (OperatingSystem.IsLinux())
        {
            var output = Check("ip", ["-4", "-o", "route", "get", address]);
            return ParseLinuxRoute(output) ?? throw new IOException("No route to exit underlay " + address);
        }
        var mac = Check("/sbin/route", ["-n", "get", address]);
        return ParseMacRoute(mac) ?? throw new IOException("No route to exit underlay " + address);
    }

    internal static (string? Gateway, string Device)? ParseLinuxRoute(string output)
    {
        var words = output.Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? gateway = null, device = null;
        for (var i = 0; i + 1 < words.Length; i++)
        {
            if (words[i] == "via") gateway = words[i + 1];
            else if (words[i] == "dev") device = words[i + 1];
        }
        if (device is null || words.Length > 0 && words[0] is "local" or "broadcast" or "unreachable" or "prohibit" or "blackhole") return null;
        return (gateway, device);
    }

    internal static (string? Gateway, string Device)? ParseMacRoute(string output)
    {
        string? gateway = null, device = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("gateway:", StringComparison.Ordinal)) gateway = line["gateway:".Length..].Trim();
            else if (line.StartsWith("interface:", StringComparison.Ordinal)) device = line["interface:".Length..].Trim();
        }
        if (device is null || device.StartsWith("lo", StringComparison.Ordinal)) return null;
        // 直连网段时 gateway 是网卡的链路地址（link#N）或者没有。
        if (gateway is not null && !IPAddress.TryParse(gateway, out _)) gateway = null;
        return (gateway, device);
    }

    /// <summary>虚拟网卡就绪后：先上防漏规则，再换 DNS。</summary>
    public void EnterTunnel(string tun)
    {
        _tun = tun;
        if (OperatingSystem.IsLinux())
        {
            Check("nft", ["-f", "-"], LinuxRuleset(tun, _routes));
            _firewall = true;
            EnterLinuxDns(tun);
        }
        else
        {
            Check("/sbin/pfctl", ["-a", PfAnchor, "-f", "-"], MacRuleset(tun, _routes));
            _firewall = true;
            // pf 默认可能没开：-E 打开并拿一个引用令牌，结束时只放掉自己的令牌，不影响别的程序开着的 pf。
            var enable = Run("/sbin/pfctl", ["-E"]);
            _pfToken = ParsePfToken(enable.Output);
            EnterMacDns();
        }
    }

    internal static string LinuxRuleset(string tun, IEnumerable<(string Address, string Device)> underlay)
    {
        var sb = new StringBuilder();
        sb.Append("table inet ").Append(NftTable).Append(" {\n  chain out {\n    type filter hook output priority -10; policy accept;\n");
        sb.Append("    oif \"lo\" accept\n");
        sb.Append("    oifname \"").Append(tun).Append("\" accept\n");
        sb.Append("    udp sport 68 udp dport 67 accept\n");
        foreach (var (address, device) in underlay) sb.Append("    ip daddr ").Append(address).Append(" oifname \"").Append(device).Append("\" accept\n");
        sb.Append("    drop\n  }\n}\n");
        return sb.ToString();
    }

    internal static string MacRuleset(string tun, IEnumerable<(string Address, string Device)> underlay)
    {
        var sb = new StringBuilder();
        sb.Append("pass out quick on lo0 all\n");
        sb.Append("pass out quick on ").Append(tun).Append(" all\n");
        sb.Append("pass out quick inet proto udp from any port 68 to any port 67\n");
        foreach (var (address, device) in underlay) sb.Append("pass out quick on ").Append(device).Append(" inet from any to ").Append(address).Append('\n');
        sb.Append("block drop out quick all\n");
        return sb.ToString();
    }

    internal static string? ParsePfToken(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Token", StringComparison.Ordinal) && t.IndexOf(':') is var colon and > 0) return t[(colon + 1)..].Trim();
        }
        return null;
    }

    private void EnterLinuxDns(string tun)
    {
        if (Exists("resolvectl") && UsesResolvedStub())
        {
            _resolved = true;
            Check("resolvectl", ["dns", tun, .. DnsServers]);
            Check("resolvectl", ["domain", tun, "~."]);
            Run("resolvectl", ["default-route", tun, "true"]);
            Run("resolvectl", ["flush-caches"]);
            return;
        }
        // 没有 systemd-resolved：临时换掉 /etc/resolv.conf，结束时原样换回。
        // Marked first: if the swap fails halfway (original already moved aside), ending the session still puts it back.
        _resolvConfReplaced = true;
        ReplaceResolvConf();
    }

    /// <summary>The resolver file; tests point it at a temporary folder.</summary>
    internal static string ResolvConf = "/etc/resolv.conf";

    /// <summary>The runtime folder (a tmpfs emptied at every boot); tests point it at a temporary folder.</summary>
    internal static string RuntimeDir = "/run";

    /// <summary>The original resolv.conf (a plain file or a link, moved as it is) while Jay's temporary file is in place.</summary>
    internal static string ResolvConfBackup => ResolvConf + ".jay-backup";

    /// <summary>Marks that there was no resolv.conf before, so restoring means removing Jay's file.</summary>
    internal static string ResolvConfAbsent => ResolvConf + ".jay-absent";

    /// <summary>
    /// The contents of the file a resolv.conf link points to, while Jay's servers are written into it. It lives in a
    /// root-only folder under <see cref="RuntimeDir"/>, so it disappears at the next boot together with what Jay wrote.
    /// </summary>
    internal static string LinkTargetBackup => Path.Combine(RuntimeDir, "jay", "resolv.conf.target-backup");

    private const string ResolvConfHeader = "# Temporary DNS while Jay routes traffic through a room member; restored afterwards\n";

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static byte[] JayResolvConf() => Encoding.UTF8.GetBytes(ResolvConfHeader + string.Concat(DnsServers.Select(s => $"nameserver {s}\n")));

    /// <summary>
    /// Puts Jay's resolver servers in place.
    /// <para>When resolv.conf is a link to a file under <see cref="RuntimeDir"/> (NetworkManager, resolvconf), the link is
    /// left alone: the target's contents are saved under <see cref="LinkTargetBackup"/> and Jay's servers are written into
    /// the target. Both are on the runtime tmpfs, so after a reboot the manager writes a fresh file even if the guard was
    /// killed before it could restore it.</para>
    /// <para>Otherwise the original (a plain file, or a link elsewhere) is renamed, not copied, so it survives a killed guard
    /// on disk next to the file (root-owned like the folder it is in). <see cref="RestoreResolvConf"/> puts either back.</para>
    /// </summary>
    internal static void ReplaceResolvConf()
    {
        RestoreResolvConf();
        if (RuntimeLinkTarget() is { } target)
        {
            WriteLinkTarget(target);
            return;
        }
        var temp = ResolvConf + ".jay-new";
        WriteNew(temp, JayResolvConf());
        if (PathExists(ResolvConf)) File.Move(ResolvConf, ResolvConfBackup);
        else File.WriteAllText(ResolvConfAbsent, "");
        File.Move(temp, ResolvConf, overwrite: true);
    }

    /// <summary>
    /// The file resolv.conf links to, when it is a link whose final target is a plain file in a folder under
    /// <see cref="RuntimeDir"/> that only root can change; null otherwise (not a link, a link elsewhere, or a folder others
    /// could change, where writing as root would not be safe).
    /// </summary>
    private static string? RuntimeLinkTarget()
    {
        try
        {
            if (new FileInfo(ResolvConf).LinkTarget is null) return null;
            if (new FileInfo(ResolvConf).ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true } target) return null;
            if (target.LinkTarget is not null) return null;
            var runtime = Path.GetFullPath(RuntimeDir).TrimEnd('/') + "/";
            var full = Path.GetFullPath(target.FullName);
            if (!full.StartsWith(runtime, StringComparison.Ordinal)) return null;
            for (var d = Path.GetDirectoryName(full); d is not null && d.Length >= runtime.Length - 1; d = Path.GetDirectoryName(d))
            {
                if (new FileInfo(d).LinkTarget is not null) return null;
                if ((File.GetUnixFileMode(d) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return null;
                if (UnixIds.FileOwner(d) is var owner && owner != 0 && owner != UnixIds.EffectiveUserId()) return null;
            }
            return full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteLinkTarget(string target)
    {
        var folder = Path.GetDirectoryName(LinkTargetBackup)!;
        if (new FileInfo(folder).LinkTarget is not null) File.Delete(folder);
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The backup is complete on disk before the target changes.
        var backupTemp = LinkTargetBackup + ".new";
        WriteNew(backupTemp, File.ReadAllBytes(target), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(backupTemp, LinkTargetBackup, overwrite: true);
        ReplaceInPlace(target, JayResolvConf());
    }

    /// <summary>Writes a file next to <paramref name="target"/> and renames it over it, keeping the target's mode.</summary>
    private static void ReplaceInPlace(string target, byte[] contents)
    {
        var temp = target + ".jay-new";
        WriteNew(temp, contents, File.GetUnixFileMode(target));
        File.Move(temp, target, overwrite: true);
    }

    private static void WriteNew(string path, byte[] contents,
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
    {
        if (PathExists(path)) File.Delete(path);
        using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode });
        stream.Write(contents);
        stream.Flush(true);
    }

    /// <summary>
    /// Puts the original resolv.conf back if Jay's file is in place: at the end of a session, and before every guard and exit
    /// start in case a guard was killed before it could. Failures are thrown so the guard reports them.
    /// </summary>
    internal static void RestoreResolvConf()
    {
        if (PathExists(ResolvConfBackup))
        {
            File.Move(ResolvConfBackup, ResolvConf, overwrite: true);
        }
        else if (File.Exists(ResolvConfAbsent))
        {
            // Remove only Jay's own file: something else may have created one since.
            if (File.Exists(ResolvConf) && new FileInfo(ResolvConf).LinkTarget is null
                && File.ReadAllText(ResolvConf).StartsWith(ResolvConfHeader, StringComparison.Ordinal))
                File.Delete(ResolvConf);
            File.Delete(ResolvConfAbsent);
        }
        if (File.Exists(ResolvConf + ".jay-new")) File.Delete(ResolvConf + ".jay-new");
        if (File.Exists(LinkTargetBackup))
        {
            // Write the saved contents back only over Jay's own servers: the manager may have written a new file since.
            if (RuntimeLinkTarget() is { } target)
            {
                if (File.ReadAllText(target).StartsWith(ResolvConfHeader, StringComparison.Ordinal))
                    ReplaceInPlace(target, File.ReadAllBytes(LinkTargetBackup));
                if (File.Exists(target + ".jay-new")) File.Delete(target + ".jay-new");
            }
            File.Delete(LinkTargetBackup);
        }
    }

    private static bool UsesResolvedStub()
    {
        try { return File.ReadAllText(ResolvConf).Contains("127.0.0.53", StringComparison.Ordinal); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record MacDnsBackup(string Service, string? Original);

    private void EnterMacDns()
    {
        var global = Check("/usr/sbin/scutil", [], "show State:/Network/Global/IPv4\n");
        var service = ParseScutilValue(global, "PrimaryService") ?? throw new IOException("No primary network service");
        var key = $"State:/Network/Service/{service}/DNS";
        var current = Run("/usr/sbin/scutil", [], $"show {key}\n").Output;
        _macDns = new MacDnsBackup(service, current.Contains("No such key", StringComparison.Ordinal) ? null : current);
        Check("/usr/sbin/scutil", [], $"d.init\nd.add ServerAddresses * {string.Join(' ', DnsServers)}\nset {key}\n");
        Run("/usr/bin/dscacheutil", ["-flushcache"]);
        Run("/usr/bin/killall", ["-HUP", "mDNSResponder"]);
    }

    internal static string? ParseScutilValue(string output, string name)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(name + " ", StringComparison.Ordinal) && t.IndexOf(':') is var colon and > 0) return t[(colon + 1)..].Trim();
        }
        return null;
    }

    /// <summary>scutil 显示的数组（ServerAddresses : &lt;array&gt; { 0 : 1.1.1.1 … }）还原成 d.add 用的值列表。</summary>
    internal static List<string> ParseScutilArray(string output, string name)
    {
        var values = new List<string>();
        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l.StartsWith(name + " ", StringComparison.Ordinal) && l.Contains("<array>", StringComparison.Ordinal));
        if (start < 0) return values;
        for (var i = start + 1; i < lines.Count && lines[i] != "}"; i++)
            if (lines[i].IndexOf(':') is var colon and > 0) values.Add(lines[i][(colon + 1)..].Trim());
        return values;
    }

    private void ExitMacDns()
    {
        if (_macDns is not { } backup) return;
        _macDns = null;
        var key = $"State:/Network/Service/{backup.Service}/DNS";
        if (backup.Original is null) Run("/usr/sbin/scutil", [], $"remove {key}\n");
        else
        {
            var script = new StringBuilder("d.init\n");
            foreach (var field in new[] { "ServerAddresses", "SearchDomains" })
            {
                var values = ParseScutilArray(backup.Original, field);
                if (values.Count > 0) script.Append("d.add ").Append(field).Append(" * ").Append(string.Join(' ', values)).Append('\n');
            }
            if (ParseScutilValue(backup.Original, "DomainName") is { } domain) script.Append("d.add DomainName ").Append(domain).Append('\n');
            script.Append("set ").Append(key).Append('\n');
            Run("/usr/sbin/scutil", [], script.ToString());
        }
        Run("/usr/bin/dscacheutil", ["-flushcache"]);
        Run("/usr/bin/killall", ["-HUP", "mDNSResponder"]);
    }

    /// <summary>
    /// 恢复：DNS 先换回来（虚拟网卡还在时），防漏规则在 easytier-core 结束之后再撤（<see cref="ExitRoutes"/>），
    /// 免得核心退出的一瞬间有流量从物理网卡漏出去。
    /// </summary>
    public void ExitDns()
    {
        if (OperatingSystem.IsLinux())
        {
            if (_resolved && _tun is { } tun) Run("resolvectl", ["revert", tun]);
            _resolved = false;
            if (_resolvConfReplaced)
            {
                _resolvConfReplaced = false;
                RestoreResolvConf();
            }
        }
        else ExitMacDns();
    }

    /// <summary>easytier-core 结束之后：撤掉主机路由和防漏规则。</summary>
    public void ExitRoutes()
    {
        foreach (var (address, _) in _routes)
        {
            if (OperatingSystem.IsLinux()) Run("ip", ["-4", "route", "del", address + "/32", "proto", RouteProto.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            else Run("/sbin/route", ["-n", "delete", "-host", address]);
        }
        _routes.Clear();
        if (_firewall)
        {
            if (OperatingSystem.IsLinux()) Run("nft", ["delete", "table", "inet", NftTable]);
            else Run("/sbin/pfctl", ["-a", PfAnchor, "-F", "all"]);
            _firewall = false;
        }
        if (_pfToken is { } token)
        {
            Run("/sbin/pfctl", ["-X", token]);
            _pfToken = null;
        }
    }

    /// <summary>Linux 上能不能做出口：要 ip 和 nft 两个命令（iproute2、nftables）。</summary>
    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() && Exists("ip") && Exists("nft");

    private static bool Exists(string tool) =>
        tool.StartsWith('/') ? File.Exists(tool) : new[] { "/usr/sbin", "/usr/bin", "/sbin", "/bin" }.Any(d => File.Exists(Path.Combine(d, tool)));

    private static string Check(string tool, IReadOnlyList<string> args, string? input = null)
    {
        var (code, output) = Run(tool, args, input);
        if (code != 0) throw new IOException($"{Path.GetFileName(tool)} {string.Join(' ', args)} failed ({code}): {output.Trim()}");
        return output;
    }

    private static (int Code, string Output) Run(string tool, IReadOnlyList<string> args, string? input = null)
    {
        if (RunForTest is { } test) return test(tool, args, input);
        var path = tool.StartsWith('/') ? tool : new[] { "/usr/sbin", "/usr/bin", "/sbin", "/bin" }.Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);
        if (path is null) return (127, tool + " not found");
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["LC_ALL"] = "C";
        using var process = Process.Start(info)!;
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (124, tool + " timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
