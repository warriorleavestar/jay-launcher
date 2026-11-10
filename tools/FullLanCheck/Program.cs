// Mac/Linux 完整局域网 + 经别人上网的无界面端到端检查，给 CI（.github/workflows/fulllan-e2e.yml）和开发者用。
// 要 root 运行（sudo dotnet FullLanCheck.dll <数据目录>）：已经是 root 时 Jay 不弹密码框，直接在本进程下起守护（--amber-et-guard）。
// 同一台机器上开两个成员：房主不建网卡（--no-tun，允许出口，监听 127.0.0.1），加入方走完整局域网模式（守护 + 虚拟网卡）。
// 依次检查：虚拟网卡和地址、经虚拟网卡连房主、广播转发、换参数重启、开出口后的防漏规则和 DNS、房主掉线不漏、结束后全部恢复、
// 加入方进程被 kill -9 后守护和 easytier-core 自己退出。密码框（pkexec / osascript）这一步只能在真机上测。
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Amber.Core;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

if (!OperatingSystem.IsWindows() && UnixElevatedGuardHost.IsGuardInvocation(args)) return UnixElevatedGuardHost.Run(args);
return await FullLanCheck.MainAsync(args);

static class FullLanCheck
{
    private const int HostRpc = 15888, GuestRpc = 15889, ChildRpc = 15890, GamePort = 25565, BroadcastPort = 4445;
    private const string Listener = "tcp://127.0.0.1:11010";
    private static int _failures;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string _phase = "启动";
    private static TimeSpan _phaseStarted;
    private static TimeSpan _phaseLimit = TimeSpan.FromMinutes(3);
    private static EasyTierElevatedSession? _watchedGuest;
    private static bool _noExit;
    private static bool _netWatch;
    /// <summary>--part lan|stop|kill (macOS CI): run only part of the check so each workflow step keeps its own log when the runner goes silent.</summary>
    private static string _part = "all";

    /// <summary>进入下一步：打印带时间的进度；看门狗发现某一步超时就打印现场后直接退出（CI 卡住时能看出停在哪）。</summary>
    private static void Phase(string name, int limitSeconds = 180)
    {
        lock (Clock)
        {
            _phase = name;
            _phaseStarted = Clock.Elapsed;
            _phaseLimit = TimeSpan.FromSeconds(limitSeconds);
        }
        Console.WriteLine($"==> [{Clock.Elapsed.TotalSeconds,6:0.0}s] {name}");
    }

    private static void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                string phase; TimeSpan over;
                lock (Clock) { phase = _phase; over = Clock.Elapsed - _phaseStarted - _phaseLimit; }
                // --net-watch: if this machine loses its own network (the CI runner then goes silent until the job times out and
                // the log is lost), tear everything down at once so the runner reconnects and the log shows which phase did it.
                var netLost = _netWatch && NetworkLost();
                if (!netLost && over <= TimeSpan.Zero && Clock.Elapsed < TimeSpan.FromMinutes(15)) continue;
                if (netLost)
                {
                    foreach (var pid in CorePids().Concat(GuardPids())) Run("kill", "-9", pid.ToString());
                    if (OperatingSystem.IsMacOS()) Run("/sbin/pfctl", "-a", UnixExitAnchor, "-F", "all");
                }
                Console.WriteLine(netLost
                    ? $"NETLOST  lost this machine's own network during: {phase} ({Clock.Elapsed.TotalSeconds:0}s)"
                    : $"TIMEOUT  卡在：{phase}（总共 {Clock.Elapsed.TotalSeconds:0}s）");
                try
                {
                    Console.WriteLine("---- 加入方 easytier / 守护输出 ----");
                    foreach (var l in _watchedGuest?.RecentOutput.TakeLast(40) ?? []) Console.WriteLine("  " + l);
                    Console.WriteLine("---- 进程 ----");
                    Console.WriteLine(Run("ps", "axo", "pid,ppid,stat,etime,command").Output);
                    Console.WriteLine("---- 网卡 ----");
                    Console.WriteLine(Run("ifconfig", "-a").Output);
                    Console.WriteLine("---- routes ----");
                    Console.WriteLine(Run("netstat", "-rn", "-f", "inet").Output);
                    if (OperatingSystem.IsMacOS())
                    {
                        Console.WriteLine("---- pf ----");
                        Console.WriteLine(Run("/sbin/pfctl", "-s", "info").Output);
                        Console.WriteLine(Run("/sbin/pfctl", "-s", "rules").Output);
                        Console.WriteLine(Run("/sbin/pfctl", "-a", UnixExitAnchor, "-s", "rules").Output);
                    }
                    Console.WriteLine("---- 托管线程栈 ----");
                    Console.WriteLine(Run("dotnet-stack", "report", "-p", Environment.ProcessId.ToString()).Output);
                }
                catch (Exception e) { Console.WriteLine(e); }
                Console.Out.Flush();
                foreach (var pid in CorePids().Concat(GuardPids())) Run("kill", "-9", pid.ToString());
                Environment.Exit(3);
            }
        }) { IsBackground = true, Name = "watchdog" };
        thread.Start();
    }

    public static async Task<int> MainAsync(string[] args)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("只用于 Mac/Linux"); return 2; }
        if (args.Length >= 1 && args[0] == "--child") return await ChildAsync(args);
        // --no-exit：跳过经房主上网两步（GitHub 的 macOS 机器上用；环境变量经 sudo 不一定传得过去，所以用参数）。
        _noExit = args.Contains("--no-exit") || Environment.GetEnvironmentVariable("JAY_FULLLAN_NO_EXIT") == "1";
        args = [.. args.Where(a => a != "--no-exit")];
        if (Environment.UserName != "root") { Console.WriteLine("要用 root 运行（sudo）"); return 2; }
        _netWatch = args.Contains("--net-watch");
        args = [.. args.Where(a => a != "--net-watch")];
        var partAt = Array.IndexOf(args, "--part");
        if (partAt >= 0 && partAt + 1 < args.Length)
        {
            _part = args[partAt + 1];
            args = [.. args.Take(partAt), .. args.Skip(partAt + 2)];
        }
        if (_part is not ("all" or "lan" or "stop" or "kill")) { Console.WriteLine("--part: lan | stop | kill"); return 2; }
        if (args.Length != 1) { Console.WriteLine("用法：sudo dotnet FullLanCheck.dll <测试数据目录> [--no-exit] [--net-watch]"); return 2; }
        if (_netWatch && !Reachable()) { Log("--net-watch: no baseline network, watch disabled"); _netWatch = false; }
        StartWatchdog();
        Phase("安装 easytier");

        var dataDir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(dataDir);
        var installer = Installer(dataDir);
        await installer.EnsureAsync();
        Log($"easytier-core: {installer.CorePath}");
        Check("完整局域网可用", PlatformFeatures.FullLan, "");
        Check("经别人上网可用", PlatformFeatures.FullTrafficExit, "");
        if (_part == "kill")
        {
            Phase("kill -9 测试", 240);
            await KillCheckAsync(dataDir);
            return Finish(null);
        }

        var room = RoomCode.Create(GamePort);
        var dnsBefore = DnsSnapshot();
        var hostOptions = new EasyTierOptions(room, IsHost: true, Peers: [], RpcPort: HostRpc, Listeners: [Listener], AllowExit: true);
        var guestOptions = new EasyTierOptions(room, IsHost: false, Peers: [Listener], RpcPort: GuestRpc, MachineId: Guid.NewGuid().ToString(), Tun: true);
        var guestIp = IPAddress.Parse(EasyTierArgs.VirtualIp(guestOptions));

        Phase("启动房主（不建网卡）", 60);
        using var game = new TcpListener(IPAddress.Loopback, GamePort);
        game.Start();
        var host = EasyTierSession.Start(installer, hostOptions, Path.Combine(dataDir, "host"));
        EasyTierElevatedSession? guest = null;
        try
        {
            Phase("启动加入方守护并等就绪", 150);
            guest = EasyTierElevatedSession.Start(installer, guestOptions, Path.Combine(dataDir, "guest"));
            await guest.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
            _watchedGuest = guest;
            Log("加入方守护已就绪");
            Phase("等虚拟网卡", 60);

            // 1. 虚拟网卡带上本机虚拟 IP。
            var tun = await WaitAsync(() => FindInterface(guestIp), TimeSpan.FromSeconds(30));
            Check("虚拟网卡带上虚拟 IP", tun is not null, tun is null ? Run("ifconfig", "-a").Output : $"{tun} {guestIp}");
            if (tun is null) return Finish(guest);
            if (OperatingSystem.IsLinux()) Check("Linux 网卡名是 amberlan", tun == "amberlan", tun);
            else Check("macOS 用 utun", tun.StartsWith("utun", StringComparison.Ordinal), tun);

            Phase("经虚拟网卡连房主", 120);
            // 2. 经虚拟网卡连房主的游戏端口（房主那边 EasyTier 交给本机 25565）。
            Check("经虚拟网卡连上房主游戏端口", await ConnectThroughTunAsync(game), string.Join('\n', guest.RecentOutput.TakeLast(15)));

            Phase("广播转发", 60);
            // 3. 广播转发：本机从物理网卡发 255.255.255.255，守护应该从虚拟网卡用虚拟 IP 再发一份。
            var (seen, capture) = await BroadcastAsync(tun, guestIp);
            Check("广播从虚拟网卡转发出去（来源是虚拟 IP）", seen,
                capture + string.Join('\n', guest.RecentOutput.Where(l => l.Contains("broadcast relay", StringComparison.Ordinal))));

            Phase("换参数重启", 180);
            // 4. 换参数重启（不应再提权，守护还是同一个）。
            var guards = GuardPids();
            await guest.RestartAsync(guestOptions with { ExtraArgs = ["--default-protocol", "tcp"] }, new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
            Check("重启后守护没换", guards.Count == 1 && GuardPids().SequenceEqual(guards), $"{string.Join(',', guards)} -> {string.Join(',', GuardPids())}");
            tun = await WaitAsync(() => FindInterface(guestIp), TimeSpan.FromSeconds(30));
            Check("重启后网卡回来", tun is not null, "");
            Check("重启后还能连房主", await ConnectThroughTunAsync(game), "");

            // GitHub 的 macOS 机器上不能测经别人上网：pf 防漏规则一装，同一台机器上跑的 CI 代理也连不上 GitHub，
            // 整个任务失联直到超时（看门狗的输出也回不来）。Linux 上 nft 规则不影响已有连接，可以测。真 Mac 上照常测。
            if (_part == "lan")
            {
                // Hard stop instead of the graceful teardown, which the "stop" part checks separately.
                Phase("hard stop (--part lan)", 60);
                foreach (var pid in CorePids().Concat(GuardPids())) Run("kill", "-9", pid.ToString());
                host.Dispose();
                return Finish(null);
            }
            var skipExit = _noExit;
            if (skipExit) Log("跳过经房主上网和房主掉线两步（--no-exit）");
            if (!skipExit)
            {
                Phase("切到经房主上网", 180);
                // 5. 经房主上网：防漏规则、DNS 都要装上。
                var exitOptions = guestOptions with { ExitNode = EasyTierArgs.HostIp };
                await guest.RestartAsync(exitOptions, new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
                Log("已切到经房主上网");
                Check("防漏规则已装", FirewallPresent(), FirewallDump());
                var dnsExit = DnsSnapshot();
                Check("DNS 换成 1.1.1.1 / 8.8.8.8", dnsExit.Contains("1.1.1.1") && dnsExit.Contains("8.8.8.8"), dnsExit);
                Check("经虚拟网卡还能连房主", await ConnectThroughTunAsync(game), "");
                // 同一台机器上房主自己的出站也被本机的出口路由和防漏规则管着，这里不判断能不能上网，只记下来；真正经出口上网在两台机器上测。
                Log($"（仅记录）开出口后访问外网：{(await InternetAsync() ? "通" : "不通")}");

                Phase("房主掉线", 90);
                // 6. 房主掉线：不能退回直接上网。
                host.Dispose();
                await Task.Delay(3000);
                Check("房主掉线后不漏到本机网络", !await InternetAsync(), "");
                Check("房主掉线后防漏规则还在", FirewallPresent(), "");
            }
        }
        catch (Exception e)
        {
            Check("没有异常", false, e.ToString() + "\n" + string.Join('\n', guest?.RecentOutput.TakeLast(30) ?? []));
        }
        finally
        {
            host.Dispose();
        }

        Phase("结束加入方并检查恢复", 120);
        // 7. 结束：网卡、规则、DNS、进程全部恢复。
        guest?.Dispose();
        await WaitAsync(() => FindInterface(guestIp) is null && CorePids().Count == 0 && GuardPids().Count == 0 ? "" : null, TimeSpan.FromSeconds(20));
        Check("结束后虚拟网卡消失", FindInterface(guestIp) is null, "");
        Check("结束后没有残留进程", CorePids().Count == 0 && GuardPids().Count == 0, Run("ps", "aux").Output);
        Check("结束后防漏规则撤掉", !FirewallPresent(), FirewallDump());
        var dnsAfter = DnsSnapshot();
        Check("结束后 DNS 恢复", dnsAfter == dnsBefore, $"之前：\n{dnsBefore}\n之后：\n{dnsAfter}");
        Check("结束后能上网", await InternetAsync(), "");

        if (_part == "stop") return Finish(null);
        Phase("kill -9 测试", 240);
        // 8. 加入方进程被 kill -9：守护和 easytier-core 几秒内自己退出，网卡消失。
        await KillCheckAsync(dataDir);
        return Finish(null);
    }

    private static int Finish(EasyTierElevatedSession? guest)
    {
        guest?.Dispose();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"FAILED: {_failures}");
        Console.Out.Flush();
        // 有后台线程没收干净时也不要挂着不退。
        Environment.Exit(_failures == 0 ? 0 : 1);
        return _failures == 0 ? 0 : 1;
    }

    private static EasyTierInstaller Installer(string dataDir) => new(new Downloader(new HttpClient(), new SourceChain([])), dataDir);

    /// <summary>子进程：只起一个加入方（要有房主时房主也在子进程里，免得父进程的房主掉线影响判断），就绪后打印 READY 一直等着被杀。</summary>
    private static async Task<int> ChildAsync(string[] args)
    {
        var dataDir = args[1];
        var installer = Installer(dataDir);
        var room = RoomCode.Create(GamePort);
        using var host = EasyTierSession.Start(installer, new EasyTierOptions(room, true, [], HostRpc, Listeners: [Listener]), Path.Combine(dataDir, "child-host"));
        var guest = EasyTierElevatedSession.Start(installer, new EasyTierOptions(room, false, [Listener], ChildRpc, MachineId: Guid.NewGuid().ToString(), Tun: true), Path.Combine(dataDir, "child-guest"));
        await guest.WaitForReadyAsync(new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        Console.WriteLine("READY " + EasyTierArgs.VirtualIp(guest.Options));
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task KillCheckAsync(string dataDir)
    {
        var self = Environment.ProcessPath!;
        var info = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") info.ArgumentList.Add(typeof(FullLanCheck).Assembly.Location);
        info.ArgumentList.Add("--child");
        info.ArgumentList.Add(dataDir);
        using var child = Process.Start(info)!;
        var readyLine = await ReadLineAsync(child.StandardOutput, TimeSpan.FromSeconds(120));
        if (readyLine is null || !readyLine.StartsWith("READY ", StringComparison.Ordinal))
        {
            Check("kill -9 测试：子进程就绪", false, readyLine ?? "超时");
            try { child.Kill(true); } catch (InvalidOperationException) { }
            return;
        }
        var ip = IPAddress.Parse(readyLine[6..]);
        Check("kill -9 测试：子进程建好网卡", await WaitAsync(() => FindInterface(ip), TimeSpan.FromSeconds(30)) is not null, "");
        Run("kill", "-9", child.Id.ToString());
        // 子进程里的房主（不建网卡的 easytier-core）没有守护看着，不算在里面；只看守护和加入方的网卡。
        var gone = await WaitAsync(() => FindInterface(ip) is null && GuardPids().Count == 0 ? "" : null, TimeSpan.FromSeconds(20));
        Check("kill -9 后守护退出、网卡消失", gone is not null, Run("ps", "aux").Output);
        foreach (var pid in CorePids()) Run("kill", "-9", pid.ToString());
    }

    private static async Task<bool> ConnectThroughTunAsync(TcpListener game)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(EasyTierArgs.HostIp, GamePort).WaitAsync(TimeSpan.FromSeconds(3));
                // 连上以后再 Accept：先挂着的 Accept 在连接失败时不会取消，会把后面成功的连接抢走，之后每次都等不到。
                using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var server = await game.AcceptTcpClientAsync(acceptTimeout.Token);
                var payload = Encoding.ASCII.GetBytes("jay-fulllan");
                await client.GetStream().WriteAsync(payload);
                var buffer = new byte[payload.Length];
                await server.GetStream().ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (buffer.AsSpan().SequenceEqual(payload)) return true;
            }
            catch (Exception e) when (e is SocketException or TimeoutException or IOException or OperationCanceledException) { await Task.Delay(1000); }
        }
        return false;
    }

    private static async Task<(bool Seen, string Capture)> BroadcastAsync(string tun, IPAddress guestIp)
    {
        var dump = new ProcessStartInfo("tcpdump", ["-l", "-n", "-i", tun, "-c", "1", "udp", "and", "dst", "port", BroadcastPort.ToString()])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var tcpdump = Process.Start(dump)!;
        var output = new StringBuilder();
        tcpdump.OutputDataReceived += (_, e) => { if (e.Data is { } l) lock (output) output.AppendLine(l); };
        tcpdump.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (output) output.AppendLine(l); };
        tcpdump.BeginOutputReadLine();
        tcpdump.BeginErrorReadLine();
        await Task.Delay(2000);
        using (var udp = new UdpClient())
        {
            udp.EnableBroadcast = true;
            for (var i = 0; i < 10 && !tcpdump.HasExited; i++)
            {
                try { await udp.SendAsync(Encoding.ASCII.GetBytes("jay-broadcast"), new IPEndPoint(IPAddress.Broadcast, BroadcastPort)); }
                catch (SocketException e) { lock (output) output.AppendLine("send: " + e.Message); }
                await Task.Delay(500);
            }
        }
        if (!tcpdump.WaitForExit(3000)) tcpdump.Kill();
        tcpdump.WaitForExit();
        string text;
        lock (output) text = output.ToString();
        return (text.Contains(guestIp + "."), text);
    }

    private const string UnixExitAnchor = "com.apple/250.JayExit";
    private const string WatchHost = "github.com";
    private static int _netMisses;
    private static DateTime _nextProbe;

    /// <summary>One TCP connect to github.com:443 (the runner's own control traffic goes to GitHub as well).</summary>
    private static bool Reachable()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(WatchHost, 443).Wait(TimeSpan.FromSeconds(4)) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException or IOException) { return false; }
    }

    /// <summary>Probes about every 6 s; three misses in a row (about 20 s) count as lost. The exit phases cut the network on purpose, so they are not watched.</summary>
    private static bool NetworkLost()
    {
        string phase;
        lock (Clock) phase = _phase;
        if (phase is "切到经房主上网" or "房主掉线" || DateTime.UtcNow < _nextProbe) return false;
        _nextProbe = DateTime.UtcNow.AddSeconds(6);
        _netMisses = Reachable() ? 0 : _netMisses + 1;
        if (_netMisses > 0) Console.WriteLine($"net-watch: github.com:443 unreachable ({_netMisses}) during {phase}");
        return _netMisses >= 3;
    }

    private static async Task<bool> InternetAsync()
    {
        foreach (var url in new[] { "https://www.cloudflare.com/cdn-cgi/trace", "https://www.microsoft.com/" })
        {
            var r = await Task.Run(() => Run("curl", "-sS", "-o", "/dev/null", "--max-time", "8", url));
            if (r.ExitCode == 0) return true;
        }
        return false;
    }

    private static string? FindInterface(IPAddress ip) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip)))?.Name;

    private static List<int> CorePids() => Pids("easytier-core");
    private static List<int> GuardPids() => Pids("--amber-et-guard");

    private static List<int> Pids(string pattern)
    {
        var r = Run("pgrep", "-f", "--", pattern);
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).Where(p => p != Environment.ProcessId).Order().ToList();
    }

    private static bool FirewallPresent() => OperatingSystem.IsLinux()
        ? Run("nft", "list", "table", "inet", "jay_exit").ExitCode == 0
        : Run("pfctl", "-a", "com.apple/250.JayExit", "-s", "rules").Output.Contains("block", StringComparison.Ordinal);

    private static string FirewallDump() => OperatingSystem.IsLinux()
        ? Run("nft", "list", "ruleset").Output
        : Run("pfctl", "-a", "com.apple/250.JayExit", "-s", "rules").Output;

    /// <summary>当前 DNS 设置的快照：Linux 看 resolv.conf 和 resolvectl，macOS 看 scutil --dns 里的 nameserver 行。</summary>
    private static string DnsSnapshot()
    {
        if (OperatingSystem.IsLinux())
        {
            var conf = File.Exists("/etc/resolv.conf") ? File.ReadAllText("/etc/resolv.conf") : "";
            var resolved = Run("resolvectl", "dns").Output;
            return (conf + "\n" + resolved).Trim();
        }
        return string.Join('\n', Run("scutil", "--dns").Output.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("resolver #", StringComparison.Ordinal) || l.StartsWith("nameserver[", StringComparison.Ordinal) || l.StartsWith("if_index", StringComparison.Ordinal)));
    }

    private static (int ExitCode, string Output) Run(string file, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { Console.WriteLine($"      （命令超时，已结束：{file} {string.Join(' ', arguments)}）"); p.Kill(true); }
            p.WaitForExit(5000);
            // 子进程留下的后台进程可能还占着输出管道，读不完也不等。
            var output = (stdout.Wait(5000) ? stdout.Result : "") + (stderr.Wait(1000) ? stderr.Result : "");
            return (p.HasExited ? p.ExitCode : -1, output);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, e.Message); }
    }

    private static async Task<string?> WaitAsync(Func<string?> probe, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (probe() is { } r) return r;
            if (sw.Elapsed > timeout) return null;
            await Task.Delay(500);
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, TimeSpan timeout)
    {
        try { return await reader.ReadLineAsync().WaitAsync(timeout); }
        catch (TimeoutException) { return null; }
    }

    private static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok)
        {
            _failures++;
            if (!string.IsNullOrWhiteSpace(detail)) Console.WriteLine("      " + detail.Replace("\n", "\n      "));
        }
    }

    private static void Log(string line) => Console.WriteLine("      " + line);
}
