using System.Net;
// 这些测试只在 Mac/Linux 上真正执行（各自先判断系统），Windows 上直接返回。
#pragma warning disable CA1416, xUnit1031

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux 完整局域网模式：root 守护的命令、核对和广播转发的解析。</summary>
public sealed class UnixFullLanTests
{
    private static byte[] UdpPacket(byte[] src, byte[] dst, ushort srcPort, ushort dstPort, byte[] payload, ushort flags = 0)
    {
        var packet = new byte[28 + payload.Length];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), flags);
        packet[8] = 64;
        packet[9] = 17;
        src.CopyTo(packet, 12);
        dst.CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + payload.Length));
        payload.CopyTo(packet, 28);
        return packet;
    }

    [Fact]
    public void RelayKeepsPortsAndPayloadOfALocalBroadcast()
    {
        if (OperatingSystem.IsWindows()) return;
        var packet = UdpPacket([192, 168, 1, 20], [255, 255, 255, 255], 40000, 4445, "hello"u8.ToArray());
        Assert.True(UnixBroadcastRelay.TryParse(packet, out var src, out var dst, out var payload));
        Assert.Equal(40000, src);
        Assert.Equal(4445, dst);
        Assert.Equal("hello"u8.ToArray(), payload.ToArray());

        var udp = UnixBroadcastRelay.BuildUdp(src, dst, payload);
        Assert.Equal(packet.AsSpan(20).ToArray(), udp);
    }

    [Theory]
    [InlineData("10.233.144.37", "255.255.255.0", true, "10.233.144.255")]
    [InlineData("10.233.144.37", null, true, "10.233.144.255")]
    [InlineData("10.233.144.37", "255.255.0.0", true, "10.233.255.255")]
    [InlineData("10.233.144.37", "0.0.0.0", true, "10.233.144.255")]
    [InlineData("10.233.144.37", "255.255.255.0", false, "255.255.255.255")]
    public void RelayTargetIsTheSubnetBroadcastOnMac(string ip, string? mask, bool mac, string expected) =>
        Assert.Equal(IPAddress.Parse(expected),
            UnixBroadcastRelay.RelayTarget(IPAddress.Parse(ip), mask is null ? null : IPAddress.Parse(mask), mac));

    [Fact]
    public void RelaySkipsFragmentsUnicastAndTheVirtualSubnet()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([192, 168, 1, 20], [255, 255, 255, 255], 1, 2, [1], flags: 0x2000), out _, out _, out _));
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([192, 168, 1, 20], [192, 168, 1, 5], 1, 2, [1]), out _, out _, out _));
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([10, 233, 144, 7], [255, 255, 255, 255], 1, 2, [1]), out _, out _, out _));
        var truncated = UdpPacket([192, 168, 1, 20], [255, 255, 255, 255], 1, 2, [1, 2, 3]);
        Assert.False(UnixBroadcastRelay.TryParse(truncated.AsSpan(0, 27), out _, out _, out _));
    }

    [Fact]
    public void DirectedBroadcastOfALocalSubnetCounts()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = UnixBroadcastRelay.LocalBroadcasts();
        Assert.Contains(0xffffffffu, set);
        Assert.True(UnixBroadcastRelay.IsBroadcastTarget([255, 255, 255, 255], set));
        Assert.False(UnixBroadcastRelay.IsBroadcastTarget([8, 8, 8, 255], [0xffffffff]));
    }

    [Fact]
    public void RelayIsRateLimited()
    {
        var limiter = new UnixBroadcastRelay.RateLimiter(3);
        Assert.Equal([true, true, true, false], Enumerable.Range(0, 4).Select(_ => limiter.Take()));
    }

    [Fact]
    public void LinuxFilterJumpsLandInsideTheProgram()
    {
        if (OperatingSystem.IsWindows()) return;
        var filter = UnixBroadcastRelay.LinuxFilter();
        for (var i = 0; i < filter.Length; i++)
        {
            if (filter[i].Code != 0x15) continue;
            Assert.InRange(i + 1 + filter[i].Jt, 0, filter.Length - 1);
            Assert.InRange(i + 1 + filter[i].Jf, 0, filter.Length - 1);
            Assert.Equal(0u, filter[i + 1 + filter[i].Jf].K);   // 不满足条件都跳到“拒绝”
        }
        Assert.Equal((ushort)0x06, filter[^1].Code);
    }

    [Fact]
    public void GuardCommandCarriesArgumentsAsAnArray()
    {
        var command = new UnixGuardCommand("start", 3, ["--network-name", "a b", "--ipv4", "10.233.144.1"], new() { ["ET_NETWORK_SECRET"] = "s" }, "10.233.144.1");
        var json = JsonSerializer.Serialize(command, AmberJsonContext.Default.UnixGuardCommand);
        Assert.DoesNotContain('\n', json);
        var back = JsonSerializer.Deserialize(json, AmberJsonContext.Default.UnixGuardCommand)!;
        Assert.Equal(command.Argv, back.Argv);
        Assert.Equal("s", back.Env!["ET_NETWORK_SECRET"]);
        Assert.Equal(3, back.N);
    }

    [Fact]
    public void GuardCommandLineNamesThisProcessAndTheCore()
    {
        if (OperatingSystem.IsWindows()) return;
        var line = UnixElevatedGuard.GuardCommandLine("/tmp/x/s", "/data/easytier-core");
        var flag = line.IndexOf(UnixElevatedGuard.GuardFlag);
        Assert.True(flag >= 1);
        Assert.Equal(["/tmp/x/s", UnixIds.EffectiveUserId().ToString(), Environment.ProcessId.ToString(), "/data/easytier-core"], line.Skip(flag + 1));
        Assert.True(UnixElevatedGuardHost.IsGuardInvocation([.. line.Skip(flag)]));
    }

    [Fact]
    public void GuardRefusesBadArgumentsAndOpenDirectories()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(2, UnixElevatedGuardHost.Run([UnixElevatedGuard.GuardFlag, "/tmp/s"]));
        if (UnixIds.EffectiveUserId() != 0)
        {
            // 不是 root：守护什么都不做。
            Assert.Equal(2, UnixElevatedGuardHost.Run([UnixElevatedGuard.GuardFlag, "/tmp/s", "0", "1", "/bin/true"]));
            return;
        }
        var dir = Directory.CreateTempSubdirectory("jay-et-test-");
        try
        {
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Assert.Equal(3, UnixElevatedGuardHost.Run([UnixElevatedGuard.GuardFlag, Path.Combine(dir.FullName, "s"), "0", "1", "/bin/true"]));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void GuardOnlyTalksToTheJayProcessThatCreatedTheSocket()
    {
        if (OperatingSystem.IsWindows() || UnixIds.EffectiveUserId() != 0) return;
        var dir = Directory.CreateTempSubdirectory("jay-et-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "s");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            // 命令行里写的是别的进程号：守护连上后核对失败，不读任何命令。
            Assert.Equal(3, UnixElevatedGuardHost.Run([UnixElevatedGuard.GuardFlag, path, "0", (Environment.ProcessId + 1).ToString(), "/bin/true"]));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void PeerCredentialsAreReadFromTheSocket()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("jay-et-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "s");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            using var server = listener.Accept();
            Assert.Equal(UnixIds.EffectiveUserId(), UnixIds.PeerUserId(server));
            Assert.Equal(Environment.ProcessId, UnixIds.PeerProcessId(client));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void ElevatedGuardStartsCoreRestartsAndStopsIt()
    {
        if (OperatingSystem.IsWindows()) return;
        // 用 /bin/sh 冒充 easytier-core，守护在进程内经测试钩子以当前用户身份启动（不弹密码框）。
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, "#!/bin/sh\necho \"started $1 secret=$ET_NETWORK_SECRET other=$OTHER\"\nexec sleep 30\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Thread? host = null;
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var args = command.Skip(flag).ToArray();
            // 守护本来以 root 运行；测试里核对对端的 uid 就是自己。
            host = new Thread(() => UnixElevatedGuardHost.RunForTest(args)) { IsBackground = true };
            host.Start();
            return Process.GetCurrentProcess();
        };
        UnixElevatedGuard.AcceptAnyUserForTest = true;
        try
        {
            Environment.SetEnvironmentVariable("OTHER", "leak");
            using var guard = UnixElevatedGuard.Start(core, ["one"], new Dictionary<string, string> { ["ET_NETWORK_SECRET"] = "k", ["OTHER"] = "x" }, null, "10.233.144.250");
            guard.WaitForReadyAsync().Wait(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("started one", StringComparison.Ordinal)), 5000));
            // 只放行房间密钥这一个环境变量（守护自己的环境里的也不传给核心）。
            Assert.Contains("started one secret=k other=", guard.RecentOutput);

            Assert.True(guard.StartAsync(["two"], new Dictionary<string, string>(), null, "10.233.144.250").Wait(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("started two", StringComparison.Ordinal)), 5000));
            Assert.False(guard.HasExited);

            guard.Dispose();
            Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.True(host!.Join(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTHER", null);
            UnixElevatedGuard.LaunchForTest = null;
            UnixElevatedGuard.AcceptAnyUserForTest = false;
            File.Delete(core);
        }
    }
}
