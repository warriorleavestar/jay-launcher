// 这些测试只在 Mac/Linux 上真正执行（各自先判断系统），Windows 上直接返回。
#pragma warning disable CA1416

using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux 的“经别人上网”：路由解析、防漏规则和恢复顺序（外部命令用测试钩子代替，不碰真实网络）。</summary>
[Collection("UnixExitNetwork")]
public sealed class UnixExitNetworkTests
{
    [Fact]
    public void ParsesLinuxRouteGet()
    {
        Assert.Equal(("192.168.1.1", "wlan0"), UnixExitNetwork.ParseLinuxRoute("203.0.113.10 via 192.168.1.1 dev wlan0 src 192.168.1.20 uid 0 \\    cache"));
        Assert.Equal((null, "eth0"), UnixExitNetwork.ParseLinuxRoute("192.168.1.5 dev eth0 src 192.168.1.20 uid 0 \\    cache"));
        Assert.Null(UnixExitNetwork.ParseLinuxRoute("local 127.0.0.1 dev lo src 127.0.0.1 uid 0"));
        Assert.Null(UnixExitNetwork.ParseLinuxRoute(""));
    }

    [Fact]
    public void ParsesMacRouteGet()
    {
        const string viaGateway = "   route to: 203.0.113.10\ndestination: default\n       mask: default\n    gateway: 192.168.1.1\n  interface: en0\n      flags: <UP,GATEWAY,DONE,STATIC,PRCLONING>\n";
        Assert.Equal(("192.168.1.1", "en0"), UnixExitNetwork.ParseMacRoute(viaGateway));
        const string onLink = "   route to: 192.168.1.5\ndestination: 192.168.1.0\n    gateway: link#6\n  interface: en0\n";
        Assert.Equal((null, "en0"), UnixExitNetwork.ParseMacRoute(onLink));
        Assert.Null(UnixExitNetwork.ParseMacRoute("   route to: 127.0.0.1\n  interface: lo0\n"));
    }

    [Fact]
    public void LinuxRulesetOnlyLetsTunnelUnderlayLoopbackAndDhcpOut()
    {
        var rules = UnixExitNetwork.LinuxRuleset("amberlan-10000", [("203.0.113.10", "wlan0")]);
        Assert.Contains("table inet jay_exit", rules);
        Assert.Contains("hook output", rules);
        Assert.Contains("oif \"lo\" accept", rules);
        Assert.Contains("oifname \"amberlan-10000\" accept", rules);
        Assert.Contains("ip daddr 203.0.113.10 oifname \"wlan0\" accept", rules);
        Assert.Contains("udp sport 68 udp dport 67 accept", rules);
        // 最后一条规则是丢弃：其他一切出站（包括 IPv6 和局域网）都不放行。
        Assert.EndsWith("drop\n  }\n}\n", rules);
    }

    [Fact]
    public void MacRulesetEndsWithABlock()
    {
        var rules = UnixExitNetwork.MacRuleset("utun7", [("203.0.113.10", "en0")]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("pass out quick on lo0 all", rules[0]);
        Assert.Contains("pass out quick on utun7 all", rules);
        Assert.Contains("pass out quick on en0 inet from any to 203.0.113.10", rules);
        Assert.Equal("block drop out quick all", rules[^1]);
    }

    [Fact]
    public void ParsesPfTokenAndScutilOutput()
    {
        Assert.Equal("18446744073709551615", UnixExitNetwork.ParsePfToken("No ALTQ support in kernel\npf enabled\nToken : 18446744073709551615\n"));
        Assert.Null(UnixExitNetwork.ParsePfToken("pfctl: pf already enabled"));
        const string ipv4 = "<dictionary> {\n  PrimaryInterface : en0\n  PrimaryService : 8F2C-11AA\n  Router : 192.168.1.1\n}\n";
        Assert.Equal("8F2C-11AA", UnixExitNetwork.ParseScutilValue(ipv4, "PrimaryService"));
        const string dns = "<dictionary> {\n  DomainName : lan\n  ServerAddresses : <array> {\n    0 : 192.168.1.1\n    1 : 192.168.1.2\n  }\n}\n";
        Assert.Equal(["192.168.1.1", "192.168.1.2"], UnixExitNetwork.ParseScutilArray(dns, "ServerAddresses"));
        Assert.Equal("lan", UnixExitNetwork.ParseScutilValue(dns, "DomainName"));
        Assert.Empty(UnixExitNetwork.ParseScutilArray(dns, "SearchDomains"));
    }

    [Fact]
    public void LinuxEnterAndExitRunInTheSafeOrder()
    {
        if (!OperatingSystem.IsLinux()) return;
        var calls = new List<string>();
        UnixExitNetwork.RunForTest = (tool, args, input) =>
        {
            calls.Add(tool + " " + string.Join(' ', args));
            if (args.Contains("get")) return (0, "203.0.113.10 via 192.168.1.1 dev wlan0 src 192.168.1.20 uid 0");
            return (0, "");
        };
        // Never touch the real resolver file, whether or not this machine has systemd-resolved.
        var dir = Directory.CreateTempSubdirectory("jay-resolv-");
        UnixExitNetwork.ResolvConf = Path.Combine(dir.FullName, "resolv.conf");
        File.WriteAllText(UnixExitNetwork.ResolvConf, "nameserver 127.0.0.53\n");
        try
        {
            var exit = new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["203.0.113.10"], "amberlan-10000"));
            exit.EnterRoutes();
            // 先清掉上次留下的，再查路由、加主机路由（带固定 proto 号）。
            Assert.Equal("ip -4 route flush proto 201", calls[0]);
            Assert.Equal("nft delete table inet jay_exit", calls[1]);
            Assert.Equal("ip -4 route replace 203.0.113.10/32 via 192.168.1.1 dev wlan0 proto 201", calls[3]);
            calls.Clear();
            exit.EnterTunnel("amberlan-10000");
            Assert.Equal("nft -f -", calls[0]);
            calls.Clear();
            exit.ExitDns();
            exit.ExitRoutes();
            Assert.Contains("ip -4 route del 203.0.113.10/32 proto 201", calls);
            Assert.Equal("nft delete table inet jay_exit", calls[^1]);
        }
        finally
        {
            UnixExitNetwork.RunForTest = null;
            UnixExitNetwork.ResolvConf = "/etc/resolv.conf";
            dir.Delete(true);
        }
    }

    /// <summary>Runs a resolv.conf test against a temporary folder instead of /etc.</summary>
    private static void WithResolvConf(Action<string> test)
    {
        var dir = Directory.CreateTempSubdirectory("jay-resolv-");
        UnixExitNetwork.ResolvConf = Path.Combine(dir.FullName, "resolv.conf");
        try { test(dir.FullName); }
        finally
        {
            UnixExitNetwork.ResolvConf = "/etc/resolv.conf";
            dir.Delete(true);
        }
    }

    [Fact]
    public void ResolvConfOriginalIsKeptOnDiskAndRestored()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(_ =>
        {
            var path = UnixExitNetwork.ResolvConf;
            File.WriteAllText(path, "nameserver 192.168.1.1\nsearch lan\n");
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Contains("nameserver " + ExitDnsServers.Primary, File.ReadAllText(path));
            // The original sits next to it while the session runs, so it outlives a killed guard.
            Assert.Equal("nameserver 192.168.1.1\nsearch lan\n", File.ReadAllText(UnixExitNetwork.ResolvConfBackup));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal("nameserver 192.168.1.1\nsearch lan\n", File.ReadAllText(path));
            Assert.False(File.Exists(UnixExitNetwork.ResolvConfBackup));
            UnixExitNetwork.RestoreResolvConf(); // nothing left to do
            Assert.Equal("nameserver 192.168.1.1\nsearch lan\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public void ResolvConfLeftByAKilledGuardIsRestoredOnTheNextStart()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(_ =>
        {
            var path = UnixExitNetwork.ResolvConf;
            File.WriteAllText(path, "nameserver 192.168.1.1\n");
            UnixExitNetwork.ReplaceResolvConf();
            // A new session replaces it again: the backup must still be the user's file, not Jay's.
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(UnixExitNetwork.ResolvConfBackup));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(path));
        });
    }

    [Fact]
    public void ResolvConfLinkIsMovedAsALinkAndItsTargetIsNeverWritten()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var path = UnixExitNetwork.ResolvConf;
            var managed = Path.Combine(dir, "managed-resolv.conf");
            File.WriteAllText(managed, "nameserver 192.168.1.1\n");
            File.CreateSymbolicLink(path, managed);
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal(managed, new FileInfo(UnixExitNetwork.ResolvConfBackup).LinkTarget);
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal(managed, new FileInfo(path).LinkTarget);
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
        });
    }

    [Fact]
    public void MissingResolvConfIsMissingAgainAfterRestore()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(_ =>
        {
            var path = UnixExitNetwork.ResolvConf;
            UnixExitNetwork.ReplaceResolvConf();
            Assert.True(File.Exists(path));
            UnixExitNetwork.RestoreResolvConf();
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(UnixExitNetwork.ResolvConfAbsent));
        });
    }

    [Fact]
    public void RouteFailureStopsBeforeTheCoreStarts()
    {
        if (OperatingSystem.IsWindows()) return;
        UnixExitNetwork.RunForTest = (_, args, _) => args.Contains("get") ? (2, "RTNETLINK answers: Network is unreachable") : (0, "");
        try
        {
            var exit = new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["203.0.113.10"], "amberlan-10000"));
            Assert.Throws<IOException>(exit.EnterRoutes);
        }
        finally { UnixExitNetwork.RunForTest = null; }
    }
}
