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

    [LinuxFact]
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
        // Never touch the real resolver file or the real runtime folder (the DNS lease), whether or not this machine has
        // systemd-resolved, and whether or not the tests run as root.
        try
        {
            WithResolvConf(_ =>
            {
                File.WriteAllText(UnixExitNetwork.ResolvConf, "nameserver 127.0.0.53\n");
                var exit = new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["203.0.113.10"], "amberlan-10000"));
                exit.EnterRoutes();
                // 先清掉上次留下的，再查路由、加主机路由（带固定 proto 号）。
                Assert.Equal("ip -4 route flush proto 201", calls[0]);
                Assert.Equal("ip -6 route flush proto 201", calls[1]);
                Assert.Equal("nft delete table inet jay_exit", calls[2]);
                Assert.Equal("ip -4 route replace 203.0.113.10/32 via 192.168.1.1 dev wlan0 proto 201", calls[4]);
                calls.Clear();
                exit.EnterTunnel("amberlan-10000");
                Assert.Equal("nft -f -", calls[0]);
                Assert.True(File.Exists(UnixExitNetwork.DnsLease));
                calls.Clear();
                exit.ExitDns();
                exit.ExitRoutes();
                Assert.Contains("ip -4 route del 203.0.113.10/32 proto 201", calls);
                Assert.Equal("nft delete table inet jay_exit", calls[^1]);
                Assert.Equal("nameserver 127.0.0.53\n", File.ReadAllText(UnixExitNetwork.ResolvConf));
            });
        }
        finally
        {
            UnixExitNetwork.RunForTest = null;
        }
    }

    /// <summary>Runs a resolv.conf test against a temporary folder instead of /etc.</summary>
    private static void WithResolvConf(Action<string> test)
    {
        var dir = Directory.CreateTempSubdirectory("jay-resolv-");
        UnixExitNetwork.ResolvConf = Path.Combine(dir.FullName, "resolv.conf");
        UnixExitNetwork.RuntimeDir = Path.Combine(dir.FullName, "run");
        Directory.CreateDirectory(UnixExitNetwork.RuntimeDir);
        try { test(dir.FullName); }
        finally
        {
            UnixExitNetwork.ResolvConf = "/etc/resolv.conf";
            UnixExitNetwork.RuntimeDir = "/run";
            dir.Delete(true);
        }
    }

    /// <summary>
    /// A second guard starting (say, a plain full-LAN session) while another Jay routes through a room member treats only
    /// an unheld swap as left over: the active session keeps its servers until it ends or its guard dies.
    /// </summary>
    [Fact]
    public void ALeftoverCheckLeavesAnActiveSessionsResolverAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(_ =>
        {
            var path = UnixExitNetwork.ResolvConf;
            File.WriteAllText(path, "nameserver 192.168.1.1\n");
            using (var active = UnixExitNetwork.TakeDnsLease())
            {
                Assert.NotNull(active);
                UnixExitNetwork.ReplaceResolvConf();
                // The other guard's start: it finds the swap held, so it is not a leftover.
                Assert.Null(UnixExitNetwork.TakeDnsLease());
                UnixExitNetwork.RestoreLeftoverResolvConf();
                Assert.Contains("nameserver " + ExitDnsServers.Primary, File.ReadAllText(path));
                Assert.True(File.Exists(UnixExitNetwork.ResolvConfBackup));
            }
            // The session's guard is gone without restoring (its hold went with it): now it is a leftover.
            UnixExitNetwork.RestoreLeftoverResolvConf();
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(path));
            Assert.False(File.Exists(UnixExitNetwork.ResolvConfBackup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(UnixExitNetwork.DnsLease));
        });
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

    /// <summary>resolv.conf as NetworkManager keeps it in symlink mode: a link into the runtime folder.</summary>
    private static string ManagedLink(string dir, string contents)
    {
        var managed = Path.Combine(UnixExitNetwork.RuntimeDir, "NetworkManager", "resolv.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        File.WriteAllText(managed, contents);
        File.CreateSymbolicLink(UnixExitNetwork.ResolvConf, managed);
        return managed;
    }

    [Fact]
    public void ResolvConfLinkIntoTheRuntimeFolderStaysALink()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var path = UnixExitNetwork.ResolvConf;
            var managed = ManagedLink(dir, "nameserver 192.168.1.1\n");
            UnixExitNetwork.ReplaceResolvConf();
            // The link is never replaced by a plain file; Jay's servers go into the file it points to.
            Assert.Equal(managed, new FileInfo(path).LinkTarget);
            Assert.Contains("nameserver " + ExitDnsServers.Primary, File.ReadAllText(path));
            Assert.False(File.Exists(UnixExitNetwork.ResolvConfBackup) || new FileInfo(UnixExitNetwork.ResolvConfBackup).LinkTarget is not null);
            // The saved contents sit in a root-only folder in the runtime folder.
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(UnixExitNetwork.LinkTargetBackup));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(UnixExitNetwork.LinkTargetBackup)!));
            // A second start keeps the user's contents as the backup, not Jay's.
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(UnixExitNetwork.LinkTargetBackup));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal(managed, new FileInfo(path).LinkTarget);
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
            Assert.False(File.Exists(UnixExitNetwork.LinkTargetBackup));
        });
    }

    [Fact]
    public void ResolvConfLinkThroughALinkedParentFolderStaysALink()
    {
        // Many systems link resolv.conf to /var/run/NetworkManager/resolv.conf, where /var/run is itself a link to /run.
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var path = UnixExitNetwork.ResolvConf;
            var managed = Path.Combine(UnixExitNetwork.RuntimeDir, "NetworkManager", "resolv.conf");
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            File.WriteAllText(managed, "nameserver 192.168.1.1\n");
            var varRun = Path.Combine(dir, "var-run");
            Directory.CreateSymbolicLink(varRun, UnixExitNetwork.RuntimeDir);
            var viaLink = Path.Combine(varRun, "NetworkManager", "resolv.conf");
            File.CreateSymbolicLink(path, viaLink);
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Equal(viaLink, new FileInfo(path).LinkTarget);
            Assert.Contains("nameserver " + ExitDnsServers.Primary, File.ReadAllText(managed));
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(UnixExitNetwork.LinkTargetBackup));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal(viaLink, new FileInfo(path).LinkTarget);
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
        });
    }

    [Fact]
    public void ResolvConfLinkHealsAfterAKilledGuardAndAReboot()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var path = UnixExitNetwork.ResolvConf;
            var managed = ManagedLink(dir, "nameserver 192.168.1.1\n");
            UnixExitNetwork.ReplaceResolvConf();
            // The guard is killed and the machine restarts: the runtime folder is empty again and the manager writes a new file.
            Directory.Delete(UnixExitNetwork.RuntimeDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            File.WriteAllText(managed, "nameserver 10.0.0.1\n");
            // Nothing of Jay's is left in /etc: the link still points at the manager's file.
            Assert.Equal(managed, new FileInfo(path).LinkTarget);
            Assert.Equal("nameserver 10.0.0.1\n", File.ReadAllText(path));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal("nameserver 10.0.0.1\n", File.ReadAllText(managed));
        });
    }

    [Fact]
    public void ResolvConfLinkTargetIsRestoredOnlyOverJaysOwnServers()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var managed = ManagedLink(dir, "nameserver 192.168.1.1\n");
            // A killed guard without a reboot: the next guard start puts the original back.
            UnixExitNetwork.ReplaceResolvConf();
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
            // The manager rewrote the file during the session: its new contents stay.
            UnixExitNetwork.ReplaceResolvConf();
            File.WriteAllText(managed, "nameserver 10.0.0.1\n");
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal("nameserver 10.0.0.1\n", File.ReadAllText(managed));
            Assert.False(File.Exists(UnixExitNetwork.LinkTargetBackup));
        });
    }

    [Fact]
    public void ResolvConfLinkIntoAFolderOthersCanChangeIsMovedAside()
    {
        if (OperatingSystem.IsWindows()) return;
        WithResolvConf(dir =>
        {
            var path = UnixExitNetwork.ResolvConf;
            var managed = ManagedLink(dir, "nameserver 192.168.1.1\n");
            var folder = Path.GetDirectoryName(managed)!;
            File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.OtherWrite);
            UnixExitNetwork.ReplaceResolvConf();
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal("nameserver 192.168.1.1\n", File.ReadAllText(managed));
            UnixExitNetwork.RestoreResolvConf();
            Assert.Equal(managed, new FileInfo(path).LinkTarget);
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
    public void Ipv6UnderlayGetsAHostRouteARuleAndNeighborDiscovery()
    {
        Assert.Equal(("fe80::1", "wlan0"), UnixExitNetwork.ParseLinuxRoute("2001:db8::10 from :: via fe80::1 dev wlan0 proto ra src 2001:db8::20 metric 1024 pref medium"));
        Assert.Equal(("fe80::1%en0", "en0"), UnixExitNetwork.ParseMacRoute("   route to: 2001:db8::10\ndestination: default\n    gateway: fe80::1%en0\n  interface: en0\n"));
        var linux = UnixExitNetwork.LinuxRuleset("amberlan-10000", [("203.0.113.10", "wlan0"), ("2001:db8::10", "wlan0")], [("wlan0", "fe80::1"), ("wlan0", "fe80::1")]);
        Assert.Contains("ip6 daddr 2001:db8::10 oifname \"wlan0\" accept", linux);
        // Neighbor discovery only: ICMPv6 with hop limit 255, to the solicited-node group and link-local neighbors
        // (the gateway, or an on-link peer asking from its link-local address), on that device.
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::1:ff00:0/104 ip6 hoplimit 255 icmpv6 type nd-neighbor-solicit accept", linux);
        Assert.Contains("oifname \"wlan0\" ip6 daddr fe80::/10 ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept", linux);
        // DHCPv6 renewals keep the device's address; defending it in duplicate address detection answers all nodes.
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::1:2 udp sport 546 udp dport 547 accept", linux);
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::1 ip6 hoplimit 255 icmpv6 type nd-neighbor-advert accept", linux);
        // Listener reports keep the solicited-node group forwarded where switches snoop MLD (RFC 8504 5.11): MLDv2 to
        // ff02::16, MLDv1 to the group itself (the whole /104, so a new or rotated address's group is covered too) and its
        // done message to all routers, with the hop limit of 1 those messages carry.
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::16 ip6 hoplimit 1 icmpv6 type mld2-listener-report accept", linux);
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::1:ff00:0/104 ip6 hoplimit 1 icmpv6 type mld-listener-report accept", linux);
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::2 ip6 hoplimit 1 icmpv6 type mld-listener-done accept", linux);
        Assert.Single(linux.Split('\n'), l => l.Contains("ff02::16", StringComparison.Ordinal));
        Assert.Equal(2, linux.Split('\n').Count(l => l.Contains("ff02::1:ff00:0/104", StringComparison.Ordinal)));
        Assert.Single(linux.Split('\n'), l => l.Contains("fe80::/10", StringComparison.Ordinal));
        // Every link-local or multicast permit names its device and the ND or DHCPv6 message: no plain fe80::/10 or
        // ff02::/16 permit (mDNS ff02::fb, LLMNR ff02::1:3, DNS or TCP to the gateway stay blocked).
        Assert.All(linux.Split('\n').Where(l => l.Contains("fe80", StringComparison.Ordinal) || l.Contains("ff02", StringComparison.Ordinal)),
            l => Assert.True(l.Contains("oifname \"wlan0\"", StringComparison.Ordinal) && (l.Contains("icmpv6 type", StringComparison.Ordinal) || l.Contains("udp sport 546 udp dport 547", StringComparison.Ordinal)), l));
        Assert.DoesNotContain("ff02::/16", linux);
        // A gateway outside fe80::/10 gets its own rule, also ND only.
        Assert.Contains("oifname \"wlan0\" ip6 daddr 2001:db8:1::1 ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept",
            UnixExitNetwork.LinuxRuleset("amberlan-10000", [("2001:db8::10", "wlan0")], [("wlan0", "2001:db8:1::1")]));
        Assert.EndsWith("drop\n  }\n}\n", linux);
        // IPv4 only: no IPv6 exception at all.
        Assert.DoesNotContain("ip6", UnixExitNetwork.LinuxRuleset("amberlan-10000", [("203.0.113.10", "wlan0")]));
        var mac = UnixExitNetwork.MacRuleset("utun7", [("2001:db8::10", "en0")], [("en0", UnixExitNetwork.NeighborAddress("fe80::1%en0"))]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("pass out quick on en0 inet6 from any to 2001:db8::10", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to ff02::1:ff00:0/104 icmp6-type neighbrsol", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to fe80::/10 icmp6-type { neighbrsol, neighbradv }", mac);
        Assert.Contains("pass out quick on en0 inet6 proto udp from any port 546 to ff02::1:2 port 547", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to ff02::1 icmp6-type neighbradv", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to ff02::16 icmp6-type 143", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to ff02::1:ff00:0/104 icmp6-type 131", mac);
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to ff02::2 icmp6-type 132", mac);
        Assert.All(mac.Where(l => l.Contains("fe80", StringComparison.Ordinal) || l.Contains("ff02", StringComparison.Ordinal)),
            l => Assert.True(l.StartsWith("pass out quick on en0 inet6 proto ", StringComparison.Ordinal) && (l.Contains("icmp6-type", StringComparison.Ordinal) || l.Contains("port 546 to ff02::1:2 port 547", StringComparison.Ordinal)), l));
        Assert.DoesNotContain(mac, l => l.Contains("ff02::/16", StringComparison.Ordinal));
        Assert.Contains("pass out quick on en0 inet6 proto ipv6-icmp from any to 2001:db8:1::1 icmp6-type { neighbrsol, neighbradv }",
            UnixExitNetwork.MacRuleset("utun7", [("2001:db8::10", "en0")], [("en0", "2001:db8:1::1")]));
        Assert.Equal("block drop out quick all", mac[^1]);
        Assert.DoesNotContain("inet6", UnixExitNetwork.MacRuleset("utun7", [("203.0.113.10", "en0")]));
        // An on-link underlay (no gateway) still answers its neighbors, which may ask from link-local addresses.
        Assert.Null(UnixExitNetwork.NeighborAddress("link#6"));
        var onLink = UnixExitNetwork.LinuxRuleset("amberlan-10000", [("2001:db8::10", "wlan0")], [("wlan0", null)]);
        Assert.Contains("oifname \"wlan0\" ip6 daddr fe80::/10 ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept", onLink);
        Assert.Contains("ip6 daddr ff02::1:ff00:0/104", onLink);

        if (!OperatingSystem.IsLinux()) return;
        var calls = new List<string>();
        UnixExitNetwork.RunForTest = (tool, args, _) =>
        {
            calls.Add(tool + " " + string.Join(' ', args));
            return args.Contains("get") ? (0, "2001:db8::10 from :: via fe80::1 dev wlan0 proto ra metric 1024") : (0, "");
        };
        try
        {
            // Entering routes first checks for leftovers, and the tunnel swaps the resolver: keep both off /etc and /run.
            WithResolvConf(_ =>
            {
                File.WriteAllText(UnixExitNetwork.ResolvConf, "nameserver 192.168.1.1\n");
                var exit = new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["2001:db8::10"], "amberlan-10000"));
                exit.EnterRoutes();
                Assert.Contains("ip -6 -o route get 2001:db8::10", calls);
                Assert.Contains("ip -6 route replace 2001:db8::10/128 via fe80::1 dev wlan0 proto 201", calls);
                calls.Clear();
                var rules = "";
                UnixExitNetwork.RunForTest = (tool, args, input) => { if (tool == "nft" && input is not null) rules = input; calls.Add(tool + " " + string.Join(' ', args)); return (0, ""); };
                exit.EnterTunnel("amberlan-10000");
                Assert.Contains("oifname \"wlan0\" ip6 daddr fe80::/10 ip6 hoplimit 255", rules);
                exit.ExitDns();
                calls.Clear();
                exit.ExitRoutes();
                Assert.Contains("ip -6 route del 2001:db8::10/128 proto 201", calls);
                // Addresses the exit can't protect never reach the route commands.
                Assert.Throws<IOException>(new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["fe80::5"], "amberlan-10000")).EnterRoutes);
            });
        }
        finally { UnixExitNetwork.RunForTest = null; }
    }

    [UnixFact]
    public void RouteFailureStopsBeforeTheCoreStarts()
    {
        if (OperatingSystem.IsWindows()) return;
        UnixExitNetwork.RunForTest = (_, args, _) => args.Contains("get") ? (2, "RTNETLINK answers: Network is unreachable") : (0, "");
        try
        {
            // Entering routes first checks for leftovers, which looks at the resolver file and the runtime folder.
            WithResolvConf(_ =>
            {
                var exit = new UnixExitNetwork(new ExitNetworkPlan("10.233.144.20", ["203.0.113.10"], "amberlan-10000"));
                Assert.Throws<IOException>(exit.EnterRoutes);
            });
        }
        finally { UnixExitNetwork.RunForTest = null; }
    }
}
