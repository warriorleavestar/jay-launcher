using Amber.Core.Multiplayer;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

public class MultiplayerTests
{
    [Fact]
    public void RoomCode_RoundTrip()
    {
        var room = RoomCode.Create(51234);
        Assert.Matches(@"^AMB-[0-9A-HJKMNP-TV-Z]{8}-[0-9A-HJKMNP-TV-Z]{26}-51234$", room.ToString());
        Assert.True(RoomCode.TryParse(room.ToString(), out var parsed));
        Assert.Equal(room, parsed);
        Assert.StartsWith("amber-", parsed.NetworkName);
        Assert.NotEqual(room, RoomCode.Create(51234));
    }

    [Theory]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565")]
    [InlineData("  amb-k7q2m9xd-4hzp8rw2n6tc-25565  ")]
    [InlineData("房间码：AMB-K7Q2M9XD-4HZP8RW2N6TC-25565，快来")]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-25565\r\n")]
    public void RoomCode_ParsesPastedText(string text)
    {
        Assert.True(RoomCode.TryParse(text, out var room));
        Assert.Equal(("K7Q2M9XD", "4HZP8RW2N6TC", 25565), (room.Network, room.Secret, room.Port));
    }

    [Fact]
    public void RoomCode_ConfusableLettersMapToDigits()
    {
        Assert.True(RoomCode.TryParse("AMB-K7Q2M9XD-4HZP8RW2NOTL-25565", out var room));
        Assert.Equal("4HZP8RW2N0T1", room.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K7Q2M9XD-4HZP8RW2N6TC-25565")]
    [InlineData("AMB-K7Q2M9X-4HZP8RW2N6TC-25565")]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-0")]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6TC-70000")]
    [InlineData("AMB-K7Q2M9XD-4HZP8RW2N6T!-25565")]
    [InlineData("AMB-K7Q2M9XU-4HZP8RW2N6TC-25565")]
    public void RoomCode_RejectsBadInput(string text) => Assert.False(RoomCode.TryParse(text, out _));

    [Theory]
    [InlineData("[12:00:01] [Server thread/INFO]: Started serving on 51234", 51234)]
    [InlineData("[Server thread/INFO]: Started serving on 51235", 51235)]
    [InlineData("[CHAT] Local game hosted on port 25566", 25566)]
    [InlineData("[CHAT] 本地游戏已在端口 61000 上开启", 61000)]
    [InlineData("[Render thread/INFO]: Sound engine started", null)]
    [InlineData("Started serving on 99999", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [System] [CHAT] Local game hosted on port 25566", 25566)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] <Steve> Started serving on 22", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] <Steve> Local game hosted on port 3389", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] [Server] Started serving on 8080", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] Steve: 在端口 445", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] Steve:在端口 445", null)]
    [InlineData("[12:00:01] [Render thread/INFO]: [CHAT] <史蒂夫> 本地游戏已在端口 445 上开启", null)]
    public void PortFromLog(string line, int? port) => Assert.Equal(port, LanGames.PortFromLog(line));

    [Fact]
    public void LanAnnouncement_FormatAndParse()
    {
        var text = LanGames.Format("Tester - 新的世界", 51234);
        Assert.Equal(new LanGame("Tester - 新的世界", 51234), LanGames.Parse(text));
        Assert.Equal(new LanGame("x", 25565), LanGames.Parse("[MOTD]x[/MOTD][AD]192.168.1.2:25565[/AD]"));
        Assert.Null(LanGames.Parse("[MOTD]x[/MOTD]"));
    }

    [Fact]
    public void Args_Host()
    {
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 51234);
        var args = EasyTierArgs.Build(new EasyTierOptions(room, true, ["tcp://public.easytier.top:11010", " "], 15999));
        var text = string.Join(' ', args);
        Assert.Contains("--network-name " + room.EasyTierNetworkName, text);
        // The code's own network name never goes on the wire.
        Assert.DoesNotContain("amber-k7q2m9xd", text);
        // 房间密钥不放命令行，经环境变量传。
        Assert.DoesNotContain("--network-secret", args);
        Assert.DoesNotContain(args, a => a.Contains("4HZP8RW2N6TC"));
        Assert.Equal(new Dictionary<string, string> { ["ET_NETWORK_SECRET"] = room.EasyTierSecret },
            EasyTierArgs.Environment(new EasyTierOptions(room, true, [], 15999)));
        Assert.Contains("--ipv4 10.233.144.1", text);
        Assert.Contains("--no-tun", args);
        Assert.Contains("--rpc-portal 127.0.0.1:15999", text);
        Assert.Contains("--no-listener", text);
        Assert.Single(args, "--peers");
        Assert.DoesNotContain("--port-forward", args);
    }

    [Fact]
    public void Args_GuestForwardsLocalPortToHost()
    {
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 51234);
        var args = EasyTierArgs.Build(new EasyTierOptions(room, false, [], 16000, 40000, ["tcp://127.0.0.1:41000"], "guest-1"));
        var forward = args[args.IndexOf("--port-forward") + 1];
        Assert.Equal("tcp://127.0.0.1:40000/10.233.144.1:51234", forward);
        Assert.Equal("tcp://127.0.0.1:41000", args[args.IndexOf("--listeners") + 1]);
        Assert.DoesNotContain("--no-listener", args);
        var ip = args[args.IndexOf("--ipv4") + 1];
        Assert.StartsWith(EasyTierArgs.Subnet, ip);
        Assert.NotEqual(EasyTierArgs.HostIp, ip);
        var again = EasyTierArgs.Build(new EasyTierOptions(room, false, [], 1, 2, null, "guest-1"));
        Assert.Equal(ip, again[again.IndexOf("--ipv4") + 1]);
        Assert.Throws<ArgumentException>(() => EasyTierArgs.Build(new EasyTierOptions(room, false, [], 1)));
    }

    /// <summary>easytier-cli 2.6.4 -o json peer 的真实输出（本机两个实例直连时）。</summary>
    [Fact]
    public void ParsePeers_RealOutput()
    {
        const string json = """
            [{"cidr":"10.233.144.231/24","ipv4":"10.233.144.231","hostname":"amber-guest","cost":"Local","lat_ms":"-","loss_rate":"-","rx_bytes":"-","tx_bytes":"-","tunnel_proto":"-","nat_type":"Unknown","id":"747661651","version":"2.6.4-8428a89d"},
             {"cidr":"10.233.144.1/24","ipv4":"10.233.144.1","hostname":"amber-host","cost":"p2p","lat_ms":"0.27","loss_rate":"0.0%","rx_bytes":"940 B","tx_bytes":"1.17 kB","tunnel_proto":"tcp","nat_type":"Unknown","id":"1016038928","version":"2.6.4-8428a89d"}]
            """;
        var peers = EasyTierSession.ParsePeers(json);
        Assert.Equal(2, peers.Count);
        Assert.Equal(new PeerInfo("amber-guest", "10.233.144.231", "Local", null, "-"), peers[0]);
        Assert.Equal(new PeerInfo("amber-host", "10.233.144.1", "p2p", 0.27, "tcp"), peers[1]);
        Assert.Empty(EasyTierSession.ParsePeers("not json"));
    }

    [Fact]
    public void Args_LoopbackPeerDisablesDeviceBinding()
    {
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 51234);
        var local = EasyTierArgs.Build(new EasyTierOptions(room, false, ["tcp://127.0.0.1:5000"], 1, 2));
        Assert.Equal("false", local[local.IndexOf("--bind-device") + 1]);
        Assert.DoesNotContain("--bind-device", EasyTierArgs.Build(new EasyTierOptions(room, false, ["tcp://node.example.com:11010"], 1, 2)));
    }

    [Fact]
    public void Args_TunModeHasNoForwardAndNamedAdapter()
    {
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 51234);
        var guest = EasyTierArgs.Build(new EasyTierOptions(room, false, [], 1, null, null, "g", Tun: true));
        Assert.DoesNotContain("--no-tun", guest);
        Assert.DoesNotContain("--port-forward", guest);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Amber LAN", guest[guest.IndexOf("--dev-name") + 1]);
            Assert.Contains("--enable-udp-broadcast-relay", guest);
        }
        else
        {
            // Linux 网卡名不能有空格；macOS 的 utun 不能自己起名。广播由 Jay 的守护转发，EasyTier 的开关只在 Windows 上有。
            if (OperatingSystem.IsLinux()) Assert.Equal("amberlan", guest[guest.IndexOf("--dev-name") + 1]);
            else Assert.DoesNotContain("--dev-name", guest);
            Assert.DoesNotContain("--enable-udp-broadcast-relay", guest);
        }
        Assert.DoesNotContain("--enable-udp-broadcast-relay", EasyTierArgs.Build(new EasyTierOptions(room, false, [], 1, 2)));
    }
}

public class EasyTierEnvironmentTests
{
    [Fact]
    public void LaunchEnvironmentDropsInheritedEasyTierVariables()
    {
        var info = new System.Diagnostics.ProcessStartInfo("easytier-core");
        info.Environment["PATH"] = "/usr/bin";
        info.Environment["ET_DISABLE_ENCRYPTION"] = "true";
        info.Environment["ET_NETWORK_SECRET"] = "inherited";

        EasyTierSession.ApplyEnvironment(info, new Dictionary<string, string> { [EasyTierArgs.SecretVariable] = "room" });

        Assert.Equal("/usr/bin", info.Environment["PATH"]);
        Assert.False(info.Environment.ContainsKey("ET_DISABLE_ENCRYPTION"));
        Assert.Equal("room", info.Environment[EasyTierArgs.SecretVariable]);
    }
}

public class RelayNodeTests
{
    [Theory]
    [InlineData("tcp://public.easytier.top:11010", true)]
    [InlineData("udp://1.2.3.4:11010", true)]
    [InlineData("quic://node.example.com:11012", true)]
    [InlineData("wg://node.example.com:11011", true)]
    // Jay's EasyTier build has no websocket support, so ws/wss relays could never connect.
    [InlineData("wss://node.example.com:443", false)]
    [InlineData("ws://node.example.com:80", false)]
    [InlineData("tcp://public.easytier.top", false)]
    [InlineData("http://example.com:80", false)]
    [InlineData("public.easytier.top:11010", false)]
    public void Validate(string url, bool ok) => Assert.Equal(ok, RelayNodes.IsValid(url));

    [Fact]
    public void WebSocketRelaysAreDroppedFromSavedListsAndShareCodes()
    {
        Assert.Equal(["tcp://my.node.example:11010"], RelayNodes.Effective(["wss://ws.node.example:443/et", "tcp://my.node.example:11010", "ws://ws.node.example:80"], false));
        Assert.True(RelayNodes.TryDecode("wss://ws.node.example:443\ntcp://my.node.example:11010", out _, out var urls));
        Assert.Equal(["tcp://my.node.example:11010"], urls);
    }

    [Fact]
    public async Task LatencyIsOnlyMeasuredForTcpRelays()
    {
        var (ms, error) = await RelayNodes.ProbeAsync("wss://ws.node.example:443");
        Assert.Null(ms);
        Assert.Equal(L.F("core.mp.nodes.unsupportedlatency", "WSS"), error);
    }

    [Fact]
    public void ShareCode_RoundTrip()
    {
        var code = RelayNodes.Encode("小明的节点", ["tcp://a.example.com:11010", "udp://a.example.com:11010", "bad", "tcp://a.example.com:11010"]);
        Assert.StartsWith("AMBNODE-", code);
        Assert.DoesNotContain('=', code);
        Assert.True(RelayNodes.TryDecode("节点在这：" + code + "  拿去用", out var name, out var urls));
        Assert.Equal("小明的节点", name);
        Assert.Equal(["tcp://a.example.com:11010", "udp://a.example.com:11010"], urls);
    }

    /// <summary>node/install.sh 和 node/install-windows.ps1 打印的分享码（有域名时才打印，两边输出逐字节相同）。</summary>
    [Theory]
    [InlineData("AMBNODE-eyJ2IjoxLCJwZWVycyI6WyJ0Y3A6Ly9ub2RlLmV4YW1wbGUuY29tOjExMDEwIiwidWRwOi8vbm9kZS5leGFtcGxlLmNvbToxMTAxMCJdfQ", null)]
    public void ShareCode_FromNodeScripts(string code, string? expectedName)
    {
        Assert.True(RelayNodes.TryDecode(code, out var name, out var urls));
        Assert.Equal(expectedName, name);
        Assert.Equal(["tcp://node.example.com:11010", "udp://node.example.com:11010"], urls);
    }

    /// <summary>Old scripts printed codes with the node's IP: those nodes are never imported from a plain code.</summary>
    [Theory]
    [InlineData("AMBNODE-eyJ2IjoxLCJuYW1lIjoi5bCP5piOIFwi5rWL6K-VXCIgXFwg6IqC54K5IiwicGVlcnMiOlsidGNwOi8vMjAzLjAuMTEzLjU6MTEwMTAiLCJ1ZHA6Ly8yMDMuMC4xMTMuNToxMTAxMCJdfQ")]
    [InlineData("AMBNODE-eyJ2IjoxLCJwZWVycyI6WyJ0Y3A6Ly8yMDMuMC4xMTMuNToxMTAxMCIsInVkcDovLzIwMy4wLjExMy41OjExMDEwIl19")]
    public void ShareCode_WithAnIpIsRefused(string code)
    {
        Assert.False(RelayNodes.TryDecode(code, out _, out _));
        Assert.False(RelayNodes.TryOpen(code, null, out _, out _, out var error));
        Assert.Equal(L.T("multiplayer.nodes.invalid_share_code"), error);
    }

    /// <summary>A node with only an IP (a home relay): the share code is sealed, shows no IP, and opens with the passphrase.</summary>
    [Fact]
    public void ShareCode_WithAnIpNodeIsSealed()
    {
        string[] nodes = ["tcp://203.0.113.5:11010", "udp://relay.example.com:11010"];
        Assert.Throws<InvalidOperationException>(() => RelayNodes.Encode("家里", nodes));
        var (code, passphrase) = RelayNodes.Share("家里", nodes);
        Assert.NotNull(passphrase);
        Assert.StartsWith(RelayNodes.SealedPrefix, code);
        Assert.True(RelayNodes.IsSealed(code));
        Assert.DoesNotContain("203.0.113.5", code);
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", code);
        Assert.Equal(PastedCodeKind.NodeShare, PastedCode.Detect(code));
        Assert.False(RelayNodes.TryDecode(code, out _, out _));
        Assert.False(RelayNodes.TryOpen(code, "0000 0000 0000 0000", out _, out _, out var error));
        Assert.Equal(L.T("core.mp.sealed.wrong_passphrase"), error);
        Assert.True(RelayNodes.TryOpen("拿去：" + code, passphrase, out var name, out var urls, out error));
        Assert.Null(error);
        Assert.Equal("家里", name);
        Assert.Equal(nodes, urls);
        // Domains only: still the plain code.
        var (plain, none) = RelayNodes.Share(null, ["tcp://relay.example.com:11010"]);
        Assert.Null(none);
        Assert.StartsWith("AMBNODE-", plain);
    }

    [Fact]
    public void ShareCode_AcceptsPlainAddresses()
    {
        Assert.True(RelayNodes.TryDecode("tcp://x.example:11010\nudp://x.example:11010, nonsense", out var name, out var urls));
        Assert.Null(name);
        Assert.Equal(2, urls.Count);
        Assert.False(RelayNodes.TryDecode("AMBNODE-!!!", out _, out _));
        Assert.Throws<ArgumentException>(() => RelayNodes.Encode(null, ["nope"]));
    }

    [Fact]
    public async Task Probe_LocalListenerAndUdp()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var (ms, error) = await RelayNodes.ProbeAsync($"tcp://127.0.0.1:{port}");
        Assert.NotNull(ms);
        Assert.Null(error);
        listener.Stop();
        var (_, closed) = await RelayNodes.ProbeAsync($"tcp://127.0.0.1:{port}");
        Assert.NotNull(closed);
        var (udpMs, udp) = await RelayNodes.ProbeAsync("udp://127.0.0.1:11010");
        Assert.Null(udpMs);
        Assert.Contains("UDP", udp);
    }
}

public class ExitNodeArgsTests
{
    private static readonly RoomCode Room = new("K7Q2M9XD", "4HZP8RW2N6TC", 51234);

    [Fact]
    public void OffByDefault()
    {
        var args = EasyTierArgs.Build(new EasyTierOptions(Room, true, [], 1, Tun: true));
        Assert.DoesNotContain("--enable-exit-node", args);
        Assert.DoesNotContain("--exit-nodes", args);
    }

    [Fact]
    public void AllowAndUse()
    {
        Assert.Contains("--enable-exit-node", EasyTierArgs.Build(new EasyTierOptions(Room, true, [], 1, Tun: true, AllowExit: true)));
        var guest = EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 1, Tun: true, ExitNode: "10.233.144.1"));
        Assert.Equal("10.233.144.1", guest[guest.IndexOf("--exit-nodes") + 1]);
    }

    [Theory]
    [InlineData("10.233.144.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("not-an-ip", true)]
    public void UsingExitNeedsTunAndRoomMember(string exit, bool tun)
    {
        Assert.Throws<ArgumentException>(() => EasyTierArgs.Build(new EasyTierOptions(Room, false, [], 1, 2, Tun: tun, ExitNode: exit)));
    }
}

public class EasyTierExtractTests
{
    [Theory]
    [InlineData("windows", System.Runtime.InteropServices.Architecture.X64, "easytier-windows-x86_64-v2.6.4-jay-9407746b1ce1.zip", "easytier-core.exe")]
    [InlineData("macos", System.Runtime.InteropServices.Architecture.Arm64, "easytier-macos-aarch64-v2.6.4-jay-9407746b1ce1.zip", "easytier-core")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.X64, "easytier-linux-x86_64-v2.6.4-jay-9407746b1ce1.zip", "easytier-core")]
    [InlineData("linux", System.Runtime.InteropServices.Architecture.Arm64, "easytier-linux-aarch64-v2.6.4-jay-9407746b1ce1.zip", "easytier-core")]
    public void ChoosesJayHardenedPackage(string os, System.Runtime.InteropServices.Architecture arch, string name, string core)
    {
        var asset = EasyTierRelease.For(os, arch);
        Assert.Equal(name, asset.Name);
        Assert.Equal(core, asset.CoreName);
        Assert.Equal(64, asset.Sha256.Length);
        Assert.True(asset.Size > 1_000_000);
    }

    [Fact]
    public void RejectsUnsupportedPackage()
    {
        Assert.Throws<PlatformNotSupportedException>(() => EasyTierRelease.For("linux", System.Runtime.InteropServices.Architecture.Wasm));
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, "WinDivert32.sys")]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "WinDivert64.sys")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "WinDivert64.sys")]
    public void WindowsPackagesKeepTheirOwnWinDivertDriver(System.Runtime.InteropServices.Architecture arch, string driver)
    {
        // The 32-bit package ships WinDivert32.sys; keeping only WinDivert64.sys used to drop it, so full LAN on
        // 32-bit Windows never counted as installed.
        var asset = EasyTierRelease.For("windows", arch);
        Assert.Equal(driver, asset.DriverName);
        var root = Path.Combine(Path.GetTempPath(), "amber-et-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "et.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "easytier-core.exe", "easytier-cli.exe", "wintun.dll", driver })
                    using (var w = new StreamWriter(archive.CreateEntry($"easytier-{asset.Platform}/" + name).Open())) w.Write(name);
            var target = Path.Combine(root, "out");
            EasyTierInstaller.Extract(zip, target, asset);
            Assert.True(File.Exists(Path.Combine(target, driver)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void OnlyWindowsPackagesNeedADriver()
    {
        Assert.Null(EasyTierRelease.For("linux", System.Runtime.InteropServices.Architecture.X64).DriverName);
        Assert.Null(EasyTierRelease.For("macos", System.Runtime.InteropServices.Architecture.Arm64).DriverName);
    }

    [Fact]
    public void KeepsCoreCliDllsAndWinDivert()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-et-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "et.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "easytier-core.exe", "easytier-cli.exe", "wintun.dll", "Packet.dll", "WinDivert64.sys", "easytier-web.exe", "easytier-web-embed.exe" })
                    using (var w = new StreamWriter(archive.CreateEntry("easytier-windows-x86_64/" + name).Open())) w.Write(name);
            var target = Path.Combine(root, "out");
            EasyTierInstaller.Extract(zip, target, EasyTierRelease.For("windows", System.Runtime.InteropServices.Architecture.X64));
            Assert.Equal(["easytier-cli.exe", "easytier-core.exe", "Packet.dll", "WinDivert64.sys", "wintun.dll"],
                Directory.EnumerateFiles(target).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public void KeepsUnixExecutablesAndLibraries()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-et-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "et.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "easytier-core", "easytier-cli", "libeasytier.so", "easytier-web" })
                    using (var w = new StreamWriter(archive.CreateEntry("easytier-linux-x86_64/" + name).Open())) w.Write(name);
            var target = Path.Combine(root, "out");
            EasyTierInstaller.Extract(zip, target, EasyTierRelease.For("linux", System.Runtime.InteropServices.Architecture.X64));
            Assert.Equal(["easytier-cli", "easytier-core", "libeasytier.so"],
                Directory.EnumerateFiles(target).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class AsciiPathTests
{
    [Theory]
    [InlineData(@"D:\Games\.minecraft", 1252, true)]
    [InlineData(@"D:\游戏\我的启动器\测试游戏目录", 936, true)]
    [InlineData(@"D:\游戏\我的启动器\测试游戏目录", 1252, false)]
    [InlineData(@"C:\Users\Zoë\.minecraft", 1252, true)]
    [InlineData(@"C:\Users\Zoë\.minecraft", 936, false)]
    [InlineData(@"D:\任何\路径", 65001, true)]
    public void Representable(string path, int codePage, bool expected) =>
        Assert.Equal(expected, Amber.Core.Launch.AsciiPaths.Representable(path, codePage));

    [Theory]
    [InlineData(@"D:\中文\mc\libraries\a.jar", @"D:\中文\mc", @"C:\L\a1", @"C:\L\a1\libraries\a.jar")]
    [InlineData(@"D:\中文\mc", @"D:\中文\mc", @"C:\L\a1", @"C:\L\a1")]
    [InlineData(@"D:\中文\mc2\x", @"D:\中文\mc", @"C:\L\a1", @"D:\中文\mc2\x")]
    [InlineData(@"-Djava.library.path=D:\中文\mc\natives", @"D:\中文\mc", @"C:\L\a1", @"-Djava.library.path=D:\中文\mc\natives")]
    public void Remap(string path, string from, string to, string expected) =>
        Assert.Equal(expected, Amber.Core.Launch.AsciiPaths.Remap(path, from, to));
}
