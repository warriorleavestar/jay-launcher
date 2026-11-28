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
// Shares UnixExitNetwork.RunForTest with UnixExitNetworkTests, so the two classes never run at the same time.
[Collection("UnixExitNetwork")]
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

    [UnixFact]
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

    [UnixFact]
    public void RelaySkipsFragmentsUnicastAndTheVirtualSubnet()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([192, 168, 1, 20], [255, 255, 255, 255], 1, 2, [1], flags: 0x2000), out _, out _, out _));
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([192, 168, 1, 20], [192, 168, 1, 5], 1, 2, [1]), out _, out _, out _));
        Assert.False(UnixBroadcastRelay.TryParse(UdpPacket([10, 233, 144, 7], [255, 255, 255, 255], 1, 2, [1]), out _, out _, out _));
        var truncated = UdpPacket([192, 168, 1, 20], [255, 255, 255, 255], 1, 2, [1, 2, 3]);
        Assert.False(UnixBroadcastRelay.TryParse(truncated.AsSpan(0, 27), out _, out _, out _));
    }

    [UnixFact]
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

    [UnixFact]
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

    [UnixFact]
    public void GuardCommandLineNamesThisProcessAndTheCore()
    {
        if (OperatingSystem.IsWindows()) return;
        var line = UnixElevatedGuard.GuardCommandLine("/tmp/x/s", "/data/easytier-core");
        var flag = line.IndexOf(UnixElevatedGuard.GuardFlag);
        Assert.True(flag >= 1);
        Assert.Equal(["/tmp/x/s", UnixIds.EffectiveUserId().ToString(), Environment.ProcessId.ToString(), "/data/easytier-core"], line.Skip(flag + 1));
        Assert.True(UnixElevatedGuardHost.IsGuardInvocation([.. line.Skip(flag)]));
    }

    [UnixFact]
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

    [UnixFact]
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

    [UnixFact]
    public void ElevatedGuardStartsCoreRestartsAndStopsIt()
    {
        if (OperatingSystem.IsWindows()) return;
        // 用 /bin/sh 冒充 easytier-core，守护在进程内经测试钩子以当前用户身份启动（不弹密码框）。
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, "#!/bin/sh\necho \"started $1 secret=$ET_NETWORK_SECRET other=$OTHER\"\nexec sleep 30\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Thread? host = null;
        var leftoverChecks = 0;
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var args = command.Skip(flag).ToArray();
            // 守护本来以 root 运行；测试里核对对端的 uid 就是自己。The leftover check goes to the test, never to the
            // system's resolv.conf, even when the tests run as root.
            host = new Thread(() => UnixElevatedGuardHost.RunForTest(args, () => Interlocked.Increment(ref leftoverChecks))) { IsBackground = true };
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
            // Each start first looks for what a killed guard may have left.
            Assert.Equal(2, Volatile.Read(ref leftoverChecks));

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

    /// <summary>A package like the pinned one, holding a shell script as easytier-core.</summary>
    private static (byte[] Zip, EasyTierRelease.Asset Asset) FakePackage(string script)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("easytier-test/easytier-core").Open())) w.Write(script);
            using (var w = new StreamWriter(zip.CreateEntry("easytier-test/easytier-cli").Open())) w.Write("#!/bin/sh\n");
            using (var w = new StreamWriter(zip.CreateEntry("easytier-test/readme.txt").Open())) w.Write("not extracted");
        }
        var bytes = ms.ToArray();
        var asset = new EasyTierRelease.Asset("test-unix", "easytier-test.zip", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            bytes.Length, "easytier-core", "easytier-cli");
        return (bytes, asset);
    }

    private static void WithCoreStore(Action<string, string, EasyTierRelease.Asset, byte[]> test, string script = "#!/bin/sh\nexit 0\n")
    {
        var dir = Directory.CreateTempSubdirectory("jay-core-store-");
        var (zip, asset) = FakePackage(script);
        var package = Path.Combine(dir.FullName, "user", asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.WriteAllBytes(package, zip);
        ElevatedCoreStore.RootForTest = Path.Combine(dir.FullName, "protected", "Jay", "easytier");
        ElevatedCoreStore.AssetForTest = asset;
        try { test(dir.FullName, package, asset, zip); }
        finally
        {
            ElevatedCoreStore.RootForTest = null;
            ElevatedCoreStore.AssetForTest = null;
            ElevatedCoreStore.OwnerForTest = null;
            ElevatedCoreStore.ReleaseForTest();
            dir.Delete(true);
        }
    }

    [Fact]
    public void GuardCopyComesFromTheCheckedPackageAndIsReused()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((_, package, asset, _) =>
        {
            var core = ElevatedCoreStore.Prepare(package);
            Assert.Equal(Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag, asset.Platform, "easytier-core"), core);
            Assert.StartsWith("#!/bin/sh", File.ReadAllText(core));
            var dir = Path.GetDirectoryName(core)!;
            Assert.False(File.Exists(Path.Combine(dir, "readme.txt")));
            // Only the owner may write anything in it.
            foreach (var path in Directory.EnumerateFileSystemEntries(dir).Append(dir))
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
            Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(core) & UnixFileMode.UserExecute);
            // A change to the user's package afterwards doesn't matter: the guard's own copy is used.
            File.Delete(package);
            Assert.Equal(core, ElevatedCoreStore.Prepare(package));
        });
    }

    [Fact]
    public void GuardRefusesAPackageThatDoesNotMatch()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((_, package, _, zip) =>
        {
            var tampered = (byte[])zip.Clone();
            tampered[^30] ^= 0xFF;
            File.WriteAllBytes(package, tampered);
            Assert.Throws<InvalidDataException>(() => ElevatedCoreStore.Prepare(package));
            File.WriteAllBytes(package, [.. zip, 0]);
            Assert.Throws<InvalidDataException>(() => ElevatedCoreStore.Prepare(package));
            File.Delete(package);
            Assert.Throws<InvalidDataException>(() => ElevatedCoreStore.Prepare(package));
            Assert.False(Directory.Exists(Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag)) &&
                Directory.EnumerateFileSystemEntries(Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag))
                    .Any(e => !e.Contains(".tmp-") && Path.GetFileName(e) != ElevatedCoreStore.InUseFile));
        });
    }

    [Fact]
    public void GuardRefusesACopyOthersCouldChange()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((root, package, asset, _) =>
        {
            var core = ElevatedCoreStore.Prepare(package);
            File.SetUnixFileMode(core, File.GetUnixFileMode(core) | UnixFileMode.OtherWrite);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            File.SetUnixFileMode(core, File.GetUnixFileMode(core) & ~UnixFileMode.OtherWrite);
            Assert.Equal(core, ElevatedCoreStore.Prepare(package));
            // A link in place of the folder is refused too.
            var dir = Path.GetDirectoryName(core)!;
            var elsewhere = Path.Combine(root, "elsewhere");
            Directory.Move(dir, elsewhere);
            Directory.CreateSymbolicLink(dir, elsewhere);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
        });
    }

    [Fact]
    public void OnlyStaleExtractionFoldersAreRemoved()
    {
        var parent = Directory.CreateTempSubdirectory("jay-core-temps-");
        try
        {
            var now = DateTime.UtcNow;
            // Another guard is extracting into this one right now.
            var busy = Directory.CreateDirectory(Path.Combine(parent.FullName, "test.tmp-busy")).FullName;
            File.WriteAllText(Path.Combine(busy, "easytier-core"), "partial");
            var stale = Directory.CreateDirectory(Path.Combine(parent.FullName, "test.tmp-stale")).FullName;
            Directory.SetLastWriteTimeUtc(stale, now - ElevatedCoreStore.StaleTempAge - TimeSpan.FromMinutes(1));
            var other = Directory.CreateDirectory(Path.Combine(parent.FullName, "other.tmp-stale")).FullName;
            Directory.SetLastWriteTimeUtc(other, now - ElevatedCoreStore.StaleTempAge - TimeSpan.FromMinutes(1));
            ElevatedCoreStore.RemoveStaleTemps(parent.FullName, "test", now);
            Assert.True(File.Exists(Path.Combine(busy, "easytier-core")));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(other)); // another platform's folder is not this guard's to remove
        }
        finally { parent.Delete(true); }
    }

    [Fact]
    public void TwoGuardsPreparingAtOnceBothGetTheCopy()
    {
        if (OperatingSystem.IsWindows() && !IsElevatedOnWindows()) return;
        WithCoreStore((_, package, asset, _) =>
        {
            // The protected folders come from a first guard (a root made here by hand would be refused on Windows); the
            // copy itself is removed again so the two guards below both have to extract it.
            var first = ElevatedCoreStore.Prepare(package);
            var parent = Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag);
            Directory.Delete(Path.GetDirectoryName(first)!, recursive: true);
            // A third guard is half-way through its own extraction; it must not lose its folder.
            var busy = Directory.CreateDirectory(Path.Combine(parent, asset.Platform + ".tmp-0123456789abcdef")).FullName;
            using var start = new Barrier(2);
            var cores = new string?[2];
            var errors = new Exception?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                try { cores[i] = ElevatedCoreStore.Prepare(package); }
                catch (Exception e) { errors[i] = e; }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.All(errors, Assert.Null);
            Assert.Equal(cores[0], cores[1]);
            Assert.True(File.Exists(cores[0]));
            Assert.True(Directory.Exists(busy));
        });
    }

    [Fact]
    public void APreparedBuildIsHeldUntilItsGuardLetsGo()
    {
        if (OperatingSystem.IsWindows() && !IsElevatedOnWindows()) return;
        WithCoreStore((_, package, _, _) =>
        {
            ElevatedCoreStore.Prepare(package);
            var inUse = Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag, ElevatedCoreStore.InUseFile);
            // What a guard of another build does before removing this folder: it finds it held.
            Assert.ThrowsAny<IOException>(() => new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.None).Dispose());
            // Guards of the same build share the hold.
            new FileStream(inUse, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Write).Dispose();
            ElevatedCoreStore.ReleaseForTest();
            new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
        });
    }

    [Fact]
    public void AnotherBuildsFolderIsLeftWhileItsGuardUsesIt()
    {
        var root = Directory.CreateTempSubdirectory("jay-core-builds-");
        try
        {
            var other = Directory.CreateDirectory(Path.Combine(root.FullName, "2.4.5-jay-0123456789ab")).FullName;
            File.WriteAllText(Path.Combine(other, "easytier-core"), "x");
            // The other build's guard has prepared its copy and holds the folder until its core is done.
            using (new FileStream(Path.Combine(other, ElevatedCoreStore.InUseFile), FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Write))
            {
                ElevatedCoreStore.RemoveOtherBuilds(root.FullName);
                Assert.True(File.Exists(Path.Combine(other, "easytier-core")));
            }
            // Once it lets go, a later start removes the folder.
            ElevatedCoreStore.RemoveOtherBuilds(root.FullName);
            Assert.False(Directory.Exists(other));
            Assert.Empty(Directory.EnumerateDirectories(root.FullName));
        }
        finally { root.Delete(true); }
    }

    /// <summary>
    /// A guard of another build that has created its folder but not yet taken it up holds the builds lock meanwhile: a
    /// removal waits for it, then sees the folder held and leaves it.
    /// </summary>
    [Fact]
    public async Task AFolderBeingTakenUpIsNotRemovedInBetween()
    {
        var root = Directory.CreateTempSubdirectory("jay-core-builds-");
        try
        {
            var other = Path.Combine(root.FullName, "2.4.5-jay-0123456789ab");
            Task removal;
            FileStream hold;
            // The other guard: lock, create its folder, and (a moment later) take it up.
            using (var builds = ElevatedCoreStore.LockBuilds(root.FullName))
            {
                Assert.NotNull(builds);
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "easytier-core"), "x");
                removal = Task.Run(() => ElevatedCoreStore.RemoveOtherBuilds(root.FullName));
                await Task.Delay(300);
                Assert.True(Directory.Exists(other));
                Assert.False(removal.IsCompleted);
                hold = new FileStream(Path.Combine(other, ElevatedCoreStore.InUseFile), FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Write);
            }
            using (hold)
            {
                await removal.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(File.Exists(Path.Combine(other, "easytier-core")));
            }
            ElevatedCoreStore.RemoveOtherBuilds(root.FullName);
            Assert.False(Directory.Exists(other));
        }
        finally { root.Delete(true); }
    }

    /// <summary>The Windows guard runs elevated; creating an administrators-owned folder needs that too.</summary>
    private static bool IsElevatedOnWindows() =>
        OperatingSystem.IsWindows() && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    [Fact]
    public void WindowsGuardCopyIsAdminOnlyCheckedAndReused()
    {
        // CI runs Windows tests elevated, like the guard; a normal developer shell can't create such a folder.
        if (!OperatingSystem.IsWindows() || !IsElevatedOnWindows()) return;
        WithCoreStore((_, package, asset, zip) =>
        {
            var tampered = (byte[])zip.Clone();
            tampered[^30] ^= 0xFF;
            File.WriteAllBytes(package, tampered);
            Assert.Throws<InvalidDataException>(() => ElevatedCoreStore.Prepare(package));
            File.WriteAllBytes(package, zip);
            var core = ElevatedCoreStore.Prepare(package);
            Assert.Equal(Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag, asset.Platform, "easytier-core"), core);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(core)!, "readme.txt")));
            var security = new DirectoryInfo(ElevatedCoreStore.RootForTest!).GetAccessControl(
                System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
            Assert.Null(ElevatedCoreStore.AdminOnlyProblem(security));
            // A change to the user's package afterwards doesn't matter: the guard's own copy is used.
            File.Delete(package);
            Assert.Equal(core, ElevatedCoreStore.Prepare(package));
        });
    }

    [Fact]
    public void WindowsGuardRefusesARootUsersCanChange()
    {
        if (!OperatingSystem.IsWindows() || !IsElevatedOnWindows()) return;
        WithCoreStore((_, package, _, _) =>
        {
            // Made by an ordinary create under the user's temp folder: it inherits the user's own full control.
            Directory.CreateDirectory(ElevatedCoreStore.RootForTest!);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            Assert.False(Directory.Exists(Path.Combine(ElevatedCoreStore.RootForTest!, EasyTierRelease.InstallTag)));
        });
    }

    [Fact]
    public void WindowsAdminOnlyCheckReadsFolderSecurity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        var creatorOwner = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.CreatorOwnerSid, null);
        System.Security.AccessControl.DirectorySecurity Folder(System.Security.Principal.SecurityIdentifier owner, System.Security.AccessControl.FileSystemRights usersMay, bool creatorOwnerFull = false)
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetOwner(owner);
            security.AddAccessRule(new(admins, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new(users, usersMay, System.Security.AccessControl.AccessControlType.Allow));
            if (creatorOwnerFull)
                security.AddAccessRule(new(creatorOwner, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            return security;
        }
        Assert.Null(ElevatedCoreStore.AdminOnlyProblem(Folder(admins, System.Security.AccessControl.FileSystemRights.ReadAndExecute)));
        Assert.Null(ElevatedCoreStore.AdminOnlyProblem(Folder(admins, System.Security.AccessControl.FileSystemRights.ReadAndExecute, creatorOwnerFull: true)));
        Assert.NotNull(ElevatedCoreStore.AdminOnlyProblem(Folder(users, System.Security.AccessControl.FileSystemRights.ReadAndExecute)));
        Assert.NotNull(ElevatedCoreStore.AdminOnlyProblem(Folder(admins, System.Security.AccessControl.FileSystemRights.Modify)));
        Assert.NotNull(ElevatedCoreStore.AdminOnlyProblem(Folder(admins, System.Security.AccessControl.FileSystemRights.ReadAndExecute | System.Security.AccessControl.FileSystemRights.WriteData)));
    }

    [Fact]
    public void FileOwnerReadsTheOwnerOfAPath()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("jay-owner-");
        try
        {
            Assert.Equal(UnixIds.EffectiveUserId(), UnixIds.FileOwner(dir.FullName));
            Assert.Equal(0u, UnixIds.FileOwner("/"));
            // A link is reported itself, not followed.
            var link = Path.Combine(dir.FullName, "link");
            File.CreateSymbolicLink(link, "/");
            Assert.Equal(UnixIds.EffectiveUserId(), UnixIds.FileOwner(link));
            Assert.Throws<IOException>(() => UnixIds.FileOwner(Path.Combine(dir.FullName, "missing")));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void GuardRefusesARootThatSomeoneElseOwns()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((root, package, _, _) =>
        {
            var store = ElevatedCoreStore.RootForTest!;
            var someone = UnixIds.EffectiveUserId() == 1234u ? 4321u : 1234u;
            // The root already exists and belongs to another account: nothing is extracted into it.
            Directory.CreateDirectory(store);
            ElevatedCoreStore.OwnerForTest = p => p == store ? someone : UnixIds.FileOwner(p);
            var e = Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            Assert.Contains("not owned by root", e.Message);
            Assert.False(Directory.Exists(Path.Combine(store, EasyTierRelease.InstallTag)));
            // The same for a folder above it.
            var above = Path.Combine(root, "protected");
            ElevatedCoreStore.OwnerForTest = p => p == above ? someone : UnixIds.FileOwner(p);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            Assert.False(Directory.Exists(Path.Combine(store, EasyTierRelease.InstallTag)));
            // A copy that is already in place, but whose program or folder belongs to another account, is refused too.
            ElevatedCoreStore.OwnerForTest = null;
            var core = ElevatedCoreStore.Prepare(package);
            ElevatedCoreStore.OwnerForTest = p => p == core ? someone : UnixIds.FileOwner(p);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            var folder = Path.GetDirectoryName(core)!;
            ElevatedCoreStore.OwnerForTest = p => p == folder ? someone : UnixIds.FileOwner(p);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
            ElevatedCoreStore.OwnerForTest = null;
            Assert.Equal(core, ElevatedCoreStore.Prepare(package));
            // A group-writable root is refused as well.
            File.SetUnixFileMode(store, File.GetUnixFileMode(store) | UnixFileMode.GroupWrite);
            Assert.Throws<IOException>(() => ElevatedCoreStore.Prepare(package));
        });
    }

    [Fact]
    public void GuardRemovesCopiesOfEarlierBuildsOnceTheCurrentOneChecksOut()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((_, package, asset, zip) =>
        {
            var store = ElevatedCoreStore.RootForTest!;
            var older = Path.Combine(store, "2.6.3-jay-0123456789ab");
            var retired = Path.Combine(store, "2.6.2-jay-0123456789ab.old-0011223344556677");
            var other = Path.Combine(store, "notes");
            foreach (var d in new[] { Path.Combine(older, asset.Platform), retired, other }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(older, asset.Platform, "easytier-core"), "old");
            // A package that doesn't check out leaves the earlier copies alone.
            File.WriteAllBytes(package, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => ElevatedCoreStore.Prepare(package));
            Assert.True(Directory.Exists(older));
            File.WriteAllBytes(package, zip);
            var core = ElevatedCoreStore.Prepare(package);
            Assert.True(File.Exists(core));
            Assert.False(Directory.Exists(older));
            Assert.False(Directory.Exists(retired));
            Assert.True(Directory.Exists(other)); // not named like a build: never touched
            Assert.Equal([EasyTierRelease.InstallTag, "notes"], Directory.EnumerateDirectories(store).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void ElevatedGuardRunsCoreFromItsOwnCopyOfThePackage()
    {
        if (OperatingSystem.IsWindows()) return;
        WithCoreStore((_, package, _, _) =>
        {
            Thread? host = null;
            UnixElevatedGuard.LaunchForTest = command =>
            {
                var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
                var args = command.Skip(flag).ToArray();
                host = new Thread(() => UnixElevatedGuardHost.RunForTest(args)) { IsBackground = true };
                host.Start();
                return Process.GetCurrentProcess();
            };
            UnixElevatedGuard.AcceptAnyUserForTest = true;
            try
            {
                using var guard = UnixElevatedGuard.Start(package, ["one"], new Dictionary<string, string>(), null, "10.233.144.250");
                guard.WaitForReadyAsync().Wait(TimeSpan.FromSeconds(10));
                Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("from ", StringComparison.Ordinal)), 5000));
                var from = guard.RecentOutput.First(l => l.StartsWith("from ", StringComparison.Ordinal))["from ".Length..];
                Assert.StartsWith(ElevatedCoreStore.RootForTest!, from, StringComparison.Ordinal);
                guard.Dispose();
                Assert.True(host!.Join(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                UnixElevatedGuard.LaunchForTest = null;
                UnixElevatedGuard.AcceptAnyUserForTest = false;
            }
        }, "#!/bin/sh\necho \"from $0\"\nexec sleep 30\n");
    }

    [Fact]
    public async Task WaitingForThePasswordDialogDoesNotBlockAndCanBeCancelled()
    {
        if (OperatingSystem.IsWindows()) return;
        // A launcher that stays open like a password dialog nobody answers.
        var dialogPid = 0;
        UnixElevatedGuard.LaunchForTest = _ =>
        {
            var dialog = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "exec sleep 30"]) { UseShellExecute = false })!;
            dialogPid = dialog.Id;
            return dialog;
        };
        static bool Running(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
        }
        try
        {
            using var cts = new CancellationTokenSource();
            var caller = Environment.CurrentManagedThreadId;
            var start = UnixElevatedGuard.StartAsync("/nonexistent/package.zip", ["one"], new Dictionary<string, string>(), null, "10.233.144.250", cts.Token);
            // Returned to the caller while the dialog is still open.
            Assert.False(start.IsCompleted);
            cts.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
            // The dialog is closed too.
            Assert.True(SpinWait.SpinUntil(() => !Running(dialogPid), 5000));
        }
        finally
        {
            UnixElevatedGuard.LaunchForTest = null;
            if (dialogPid != 0 && Running(dialogPid)) Process.GetProcessById(dialogPid).Kill();
        }
    }

    [Fact]
    public async Task ClosingThePasswordDialogEndsTheWaitAsDeclined()
    {
        if (!OperatingSystem.IsLinux()) return;
        // pkexec exits with 126 when the user closes the dialog.
        UnixElevatedGuard.LaunchForTest = _ => Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "exit 126"]) { UseShellExecute = false })!;
        try
        {
            var start = UnixElevatedGuard.StartAsync("/nonexistent/package.zip", ["one"], new Dictionary<string, string>(), null, "10.233.144.250");
            var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains(Amber.Core.Localization.L.T("core.mp.component.elevationdeclined.linux"), e.Message);
        }
        finally { UnixElevatedGuard.LaunchForTest = null; }
    }

    /// <summary>A stop signal to the root guard (what SIGTERM triggers) ends the core and returns normally, so cleanup runs.</summary>
    [Fact]
    public void ElevatedGuardStopsTheCoreWhenAskedToStop()
    {
        if (OperatingSystem.IsWindows()) return;
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, "#!/bin/sh\necho \"pid=$$\"\nexec sleep 30\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Thread? host = null;
        var result = -1;
        Action? stop = null;
        UnixElevatedGuardHost.StopHookForTest = s => stop = s;
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var args = command.Skip(flag).ToArray();
            host = new Thread(() => result = UnixElevatedGuardHost.RunForTest(args)) { IsBackground = true };
            host.Start();
            return Process.GetCurrentProcess();
        };
        UnixElevatedGuard.AcceptAnyUserForTest = true;
        try
        {
            using var guard = UnixElevatedGuard.Start(core, ["one"], new Dictionary<string, string>(), null, "10.233.144.250");
            guard.WaitForReadyAsync().Wait(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("pid=", StringComparison.Ordinal)), 5000));
            var pid = int.Parse(guard.RecentOutput.First(l => l.StartsWith("pid=", StringComparison.Ordinal))[4..]);
            Assert.True(SpinWait.SpinUntil(() => stop is not null, 5000));
            stop!();
            stop();
            Assert.True(host!.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, result);
            Assert.True(SpinWait.SpinUntil(() => !IsRunning(pid), 5000));
            // Another stop request after the session has ended (a late signal) is harmless.
            stop();
        }
        finally
        {
            UnixElevatedGuardHost.StopHookForTest = null;
            UnixElevatedGuard.LaunchForTest = null;
            UnixElevatedGuard.AcceptAnyUserForTest = false;
            File.Delete(core);
        }
    }

    /// <summary>A stop that is already pending when a start command arrives wins: no core is started.</summary>
    [Fact]
    public void ElevatedGuardStartsNoCoreOnceAskedToStop()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(Path.GetTempPath(), "jay-fake-core-ran-" + Guid.NewGuid().ToString("N"));
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, $"#!/bin/sh\ntouch '{marker}'\nexec sleep 30\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Thread? host = null;
        var result = -1;
        // The stop arrives as the session begins, after the start command is already waiting on the socket, so both are
        // ready at the guard's first wait.
        UnixElevatedGuardHost.StopHookForTest = s => { Thread.Sleep(500); s(); };
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var args = command.Skip(flag).ToArray();
            host = new Thread(() => result = UnixElevatedGuardHost.RunForTest(args)) { IsBackground = true };
            host.Start();
            return Process.GetCurrentProcess();
        };
        UnixElevatedGuard.AcceptAnyUserForTest = true;
        try
        {
            using var guard = UnixElevatedGuard.Start(core, ["one"], new Dictionary<string, string>(), null, "10.233.144.250");
            Assert.True(SpinWait.SpinUntil(() => host is not null, 10000));
            Assert.True(host!.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, result);
            Thread.Sleep(300);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            UnixElevatedGuardHost.StopHookForTest = null;
            UnixElevatedGuard.LaunchForTest = null;
            UnixElevatedGuard.AcceptAnyUserForTest = false;
            File.Delete(core);
            File.Delete(marker);
        }
    }

    /// <summary>
    /// A stop while the guard waits for the virtual adapter (which can take up to 30 s) ends the wait at once and runs
    /// the normal clean-up: the core is stopped and the exit routes are taken down again.
    /// </summary>
    [Fact]
    public void ElevatedGuardStopsWhileWaitingForTheAdapter()
    {
        if (!OperatingSystem.IsLinux()) return;
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, "#!/bin/sh\necho \"pid=$$\"\nexec sleep 30\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var calls = new List<string>();
        UnixExitNetwork.RunForTest = (tool, args, _) =>
        {
            lock (calls) calls.Add(tool + " " + string.Join(' ', args));
            return args.Contains("get") ? (0, "203.0.113.10 via 192.168.1.1 dev wlan0 src 192.168.1.20 uid 0") : (0, "");
        };
        Thread? host = null;
        var result = -1;
        Action? stop = null;
        UnixElevatedGuardHost.StopHookForTest = s => stop = s;
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var args = command.Skip(flag).ToArray();
            host = new Thread(() => result = UnixElevatedGuardHost.RunForTest(args)) { IsBackground = true };
            host.Start();
            return Process.GetCurrentProcess();
        };
        UnixElevatedGuard.AcceptAnyUserForTest = true;
        try
        {
            // No adapter ever carries this address, so without the stop the guard would wait the full 30 s.
            using var guard = UnixElevatedGuard.Start(core, ["one"], new Dictionary<string, string>(),
                new ExitNetworkPlan("10.233.144.250", ["203.0.113.10"], "amberlan-10000"), "10.233.144.250");
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("pid=", StringComparison.Ordinal)), 10000));
            var pid = int.Parse(guard.RecentOutput.First(l => l.StartsWith("pid=", StringComparison.Ordinal))[4..]);
            var asked = Stopwatch.StartNew();
            stop!();
            Assert.True(host!.Join(TimeSpan.FromSeconds(10)));
            Assert.True(asked.Elapsed < TimeSpan.FromSeconds(10), $"the guard took {asked.Elapsed} to stop");
            Assert.Equal(0, result);
            Assert.True(SpinWait.SpinUntil(() => !IsRunning(pid), 5000));
            lock (calls) Assert.Contains("ip -4 route del 203.0.113.10/32 proto 201", calls);
        }
        finally
        {
            UnixExitNetwork.RunForTest = null;
            UnixElevatedGuardHost.StopHookForTest = null;
            UnixElevatedGuard.LaunchForTest = null;
            UnixElevatedGuard.AcceptAnyUserForTest = false;
            File.Delete(core);
        }
    }

    private sealed class Step(List<string> log, string name, Action? onDispose = null) : IDisposable
    {
        public void Dispose()
        {
            onDispose?.Invoke();
            log.Add(name);
        }
    }

    /// <summary>
    /// The guard's signal handlers outlive the session's clean-up (stopping the core, restoring routes and firewall), so a
    /// second signal during the clean-up cannot end the process half way; also when the session fails.
    /// </summary>
    [Fact]
    public void SignalHandlersStayUntilTheCleanUpIsOver()
    {
        if (OperatingSystem.IsWindows()) return;
        var log = new List<string>();
        var signalsHeld = 2;
        var session = new Step(log, "session", () => log.Add(signalsHeld == 2 ? "cleanup with handlers" : "cleanup without handlers"));
        IDisposable[] signals = [new Step(log, "signal", () => signalsHeld--), new Step(log, "signal", () => signalsHeld--)];
        Assert.Equal(0, UnixElevatedGuardHost.RunThenCleanUp(() => 0, session, signals));
        Assert.Equal(["cleanup with handlers", "session", "signal", "signal"], log);

        log.Clear();
        signalsHeld = 2;
        session = new Step(log, "session", () => log.Add(signalsHeld == 2 ? "cleanup with handlers" : "cleanup without handlers"));
        signals = [new Step(log, "signal", () => signalsHeld--), new Step(log, "signal", () => signalsHeld--)];
        Assert.Throws<InvalidOperationException>(() => UnixElevatedGuardHost.RunThenCleanUp(() => throw new InvalidOperationException(), session, signals));
        Assert.Equal(["cleanup with handlers", "session", "signal", "signal"], log);
    }

    /// <summary>
    /// The real guard entry point in its own process (Jay's own Main, the production signal handlers, only the root check
    /// switched off) gets a real SIGTERM: it stops the core and exits normally. A second SIGTERM that arrives while it is
    /// still stopping the core is absorbed too, so the clean-up is not cut short.
    /// </summary>
    [Fact]
    public void RootGuardProcessCleansUpOnRealSigterm()
    {
        if (OperatingSystem.IsWindows()) return;
        var app = typeof(Amber.App.Program).Assembly.Location; // Jay itself: its Main hands the guard flag to the guard
        Assert.True(File.Exists(app), app);
        // The core ignores SIGTERM but says when it gets one, so the guard's clean-up (TERM, then 5 s, then KILL) is
        // known to be under way when the second signal is sent.
        var core = Path.Combine(Path.GetTempPath(), "jay-fake-core-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(core, "#!/bin/sh\ntrap 'echo got-term' TERM\necho \"pid=$$\"\nwhile :; do sleep 1 & wait $!; done\n");
        File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? child = null;
        var corePid = 0;
        UnixElevatedGuard.LaunchForTest = command =>
        {
            var flag = command.ToList().IndexOf(UnixElevatedGuard.GuardFlag);
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(app);
            foreach (var a in command.Skip(flag)) info.ArgumentList.Add(a);
            info.Environment[UnixElevatedGuardHost.NoRootForTestVariable] = "1";
            child = Process.Start(info)!;
            child.StandardInput.Close();
            _ = child.StandardOutput.ReadToEndAsync();
            _ = child.StandardError.ReadToEndAsync();
            return Process.GetCurrentProcess();
        };
        UnixElevatedGuard.AcceptAnyUserForTest = true;
        try
        {
            using var guard = UnixElevatedGuard.Start(core, ["one"], new Dictionary<string, string>(), null, "10.233.144.250");
            // Ready: the guard has registered its handlers (before it reads any command) and started the core.
            Assert.True(guard.WaitForReadyAsync().Wait(TimeSpan.FromSeconds(30)), "guard ready");
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Any(l => l.StartsWith("pid=", StringComparison.Ordinal)), 10000), "core started");
            corePid = int.Parse(guard.RecentOutput.First(l => l.StartsWith("pid=", StringComparison.Ordinal))[4..]);

            Signal(child!.Id);
            Assert.True(SpinWait.SpinUntil(() => guard.RecentOutput.Contains("got-term"), 10000), "the guard is stopping the core");
            Assert.False(child.HasExited, "the guard is still cleaning up");
            Signal(child.Id);

            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(20)), "guard exited");
            Assert.Equal(0, child.ExitCode);
            Assert.True(SpinWait.SpinUntil(() => !IsRunning(corePid), 5000), "core stopped");
            Assert.True(guard.WaitForExit(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            UnixElevatedGuard.LaunchForTest = null;
            UnixElevatedGuard.AcceptAnyUserForTest = false;
            try { if (child is { HasExited: false }) child.Kill(); } catch (InvalidOperationException) { }
            if (corePid > 0 && IsRunning(corePid)) Process.Start("kill", ["-KILL", corePid.ToString()])!.WaitForExit();
            child?.Dispose();
            File.Delete(core);
        }

        static void Signal(int pid) => Process.Start("kill", ["-TERM", pid.ToString()])!.WaitForExit();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
