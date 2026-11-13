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
        try
        {
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
            calls.Clear();
            exit.ExitDns();
            exit.ExitRoutes();
            Assert.Contains("ip -4 route del 203.0.113.10/32 proto 201", calls);
            Assert.Equal("nft delete table inet jay_exit", calls[^1]);
        }
        finally { UnixExitNetwork.RunForTest = null; }
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
        // DHCPv6 renewals keep the device's address.
        Assert.Contains("oifname \"wlan0\" ip6 daddr ff02::1:2 udp sport 546 udp dport 547 accept", linux);
        Assert.Single(linux.Split('\n'), l => l.Contains("ff02::1:ff00:0/104", StringComparison.Ordinal));
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
        }
        finally { UnixExitNetwork.RunForTest = null; }
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
