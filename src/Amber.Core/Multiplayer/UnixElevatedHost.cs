using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 守护模式（以 root 运行）：<c>Jay --amber-et-guard 套接字 用户uid Jay进程号 easytier-core路径</c>。
/// 先核对套接字是那个 Jay 进程（对端进程号和 uid）建的才读命令；按命令启动、重启、结束 easytier-core，输出经套接字送回；
/// 完整局域网模式下顺带转发本机游戏发出的 UDP 广播（<see cref="UnixBroadcastRelay"/>）。
/// 套接字断开（Jay 退出或崩溃）、收到停止命令或 easytier-core 自己退出时，结束一切后退出。不写任何文件。
/// 这里的提示只给开发者看（守护没有界面），不翻译。
/// </summary>
[UnsupportedOSPlatform("windows")]
public static class UnixElevatedGuardHost
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsGuardInvocation(string[] args) => args.Length > 0 && args[0] == UnixElevatedGuard.GuardFlag;

    /// <summary>守护入口，返回进程退出码：0 正常结束，2 参数或环境不对，3 核对失败，4 运行中出错。</summary>
    public static int Run(string[] args) =>
        Run(args, requireRoot: Environment.GetEnvironmentVariable(NoRootForTestVariable) != "1", handleSignals: true);

    /// <summary>
    /// Tests only: set to "1", a guard started as its own process runs as the current user. Everything else, the signal
    /// handlers included, is exactly what the root guard does; without root it can only do what the user can anyway.
    /// </summary>
    internal const string NoRootForTestVariable = "AMBER_ET_GUARD_TEST_NO_ROOT";

    /// <summary>测试用：在测试进程里以当前用户身份跑守护（不接管测试进程的信号）。</summary>
    internal static int RunForTest(string[] args) => Run(args, requireRoot: false, handleSignals: false);

    /// <summary>Tests: receives the running session's stop action (what a SIGTERM does in the real guard).</summary>
    internal static Action<Action>? StopHookForTest;

    private static int Run(string[] args, bool requireRoot, bool handleSignals)
    {
        if (args.Length != 5 || !uint.TryParse(args[2], out var uid) || !int.TryParse(args[3], out var jayPid)) return 2;
        if (requireRoot && UnixIds.EffectiveUserId() != 0) return 2;
        var socketPath = args[1];
        var corePath = args[4];
        Socket socket;
        try
        {
            // 套接字所在目录必须是 0700（mkdtemp 建的），也不能是符号链接。
            var dir = new DirectoryInfo(Path.GetDirectoryName(socketPath)!);
            if (dir.LinkTarget is not null || (File.GetUnixFileMode(dir.FullName) & (UnixFileMode)0b000_111_111) != 0) return 3;
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            // 对端（建套接字的进程）必须就是命令行里的那个 Jay，而且属于请求权限的用户。
            if (UnixIds.PeerUserId(socket) != uid || UnixIds.PeerProcessId(socket) != jayPid)
            {
                socket.Dispose();
                return 3;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or ArgumentException) { return 3; }
        var session = new GuardSession(socket, corePath);
        // A signal (logout, kill, Ctrl+C in a terminal) must still take down the core, routes and firewall rules:
        // stop the session and clean up instead of letting the runtime end the process. The handlers stay in place
        // until the clean-up is over, so a second signal during it is absorbed too.
        var signals = new List<IDisposable>();
        if (handleSignals)
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
                signals.Add(PosixSignalRegistration.Create(signal, ctx => { ctx.Cancel = true; session.RequestStop(); }));
        StopHookForTest?.Invoke(session.RequestStop);
        return RunThenCleanUp(session.Run, session, signals);
    }

    /// <summary>
    /// Runs <paramref name="run"/>, then disposes <paramref name="session"/> (stopping the core, restoring routes, DNS and
    /// firewall), and only then <paramref name="signals"/>: a signal that arrives while the session cleans up still finds
    /// a handler and cannot cut the clean-up short.
    /// </summary>
    internal static int RunThenCleanUp(Func<int> run, IDisposable session, IReadOnlyList<IDisposable> signals)
    {
        try
        {
            try { return run(); }
            finally { session.Dispose(); }
        }
        finally
        {
            foreach (var r in signals) r.Dispose();
        }
    }

    private sealed class GuardSession(Socket socket, string corePath) : IDisposable
    {
        private readonly NetworkStream _stream = new(socket, ownsSocket: true);
        private readonly object _writeLock = new();
        private Process? _core;
        private IDisposable? _relay;
        private UnixExitNetwork? _exit;
        private readonly ManualResetEventSlim _coreExited = new(false);
        private readonly ManualResetEventSlim _stop = new(false);

        /// <summary>Safe to call any number of times, from a signal handler too, also once the session is disposed.</summary>
        public void RequestStop()
        {
            try { _stop.Set(); }
            catch (ObjectDisposedException) { }
        }

        public int Run()
        {
            using var reader = new StreamReader(_stream, Utf8, leaveOpen: true);
            var pending = reader.ReadLineAsync();
            try
            {
                while (true)
                {
                    var index = WaitHandle.WaitAny([((IAsyncResult)pending).AsyncWaitHandle, _coreExited.WaitHandle, _stop.WaitHandle]);
                    if (index == 2) return 0;
                    if (index == 1)
                    {
                        Send("error " + B64("easytier-core exited"));
                        return 0;
                    }
                    var line = pending.Result;
                    if (line is null) return 0;
                    var cmd = JsonSerializer.Deserialize(line, AmberJsonContext.Default.UnixGuardCommand);
                    if (cmd is null || cmd.Cmd == "stop") return 0;
                    if (cmd.Cmd == "start")
                    {
                        try
                        {
                            StartCore(cmd);
                            Send($"started {cmd.N}");
                        }
                        catch (StopRequestedException)
                        {
                            // Asked to stop while starting: end like any other stop; Dispose cleans up what was set up.
                            return 0;
                        }
                        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                        {
                            StopCore();
                            Send($"failed {cmd.N} {B64(e.Message)}");
                            return 4;
                        }
                    }
                    pending = reader.ReadLineAsync();
                }
            }
            catch (Exception)
            {
                // Any error ends the session the same way, so the caller's using block always removes what it set up.
                return 4;
            }
        }

        /// <summary>Thrown inside <see cref="StartCore"/> when a stop (a signal) arrives part way through a start.</summary>
        private sealed class StopRequestedException : Exception;

        private void ThrowIfStopRequested()
        {
            if (_stop.IsSet) throw new StopRequestedException();
        }

        private void StartCore(UnixGuardCommand cmd)
        {
            StopCore();
            // A stop can arrive together with (or before) a start command; it wins, and no new core is started.
            ThrowIfStopRequested();
            CheckCore(corePath);
            // 经别人上网：承载地址的主机路由要在 EasyTier 加 /1 路由之前查好、加好。
            if (cmd.Network is { } plan)
            {
                _exit = new UnixExitNetwork(plan);
                _exit.EnterRoutes();
            }
            var info = new ProcessStartInfo(corePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = "/",
            };
            foreach (var a in cmd.Argv) info.ArgumentList.Add(a);
            // 核心只拿到最小的环境：固定的 PATH 加上房间密钥，守护自己的环境变量一个都不传。
            info.Environment.Clear();
            info.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
            if (cmd.Env is { } env && env.TryGetValue(EasyTierArgs.SecretVariable, out var secret)) info.Environment[EasyTierArgs.SecretVariable] = secret;
            _coreExited.Reset();
            ThrowIfStopRequested();
            var process = Process.Start(info) ?? throw new InvalidOperationException("easytier-core did not start");
            _core = process;
            process.StandardInput.Close();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { if (ReferenceEquals(_core, process)) _coreExited.Set(); };
            process.OutputDataReceived += (_, e) => { if (e.Data is { } l) Send("out " + B64(l)); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } l) Send("out " + B64(l)); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited) throw new InvalidOperationException("easytier-core exited during startup");
            if (cmd.VirtualIp is { } vip && System.Net.IPAddress.TryParse(vip, out var ip))
            {
                if (_exit is { } exit)
                {
                    // 虚拟网卡就绪（带上本机虚拟 IP）之后才上防漏规则和 DNS；就绪之前不回“已启动”。
                    var tun = WaitForTun(ip, process) ?? throw new IOException("virtual adapter did not come up");
                    exit.EnterTunnel(tun);
                }
                _relay = UnixBroadcastRelay.Start(ip, message => Send("error " + B64(message)));
            }
        }

        /// <summary>以 root 运行的程序：必须是普通文件（不是符号链接），别的用户和组不能改它。</summary>
        internal static void CheckCore(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) throw new IOException("easytier-core missing or a symlink");
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) throw new IOException("easytier-core is writable by others");
        }

        /// <summary>Waits up to 30 s for the virtual adapter; a stop request ends the wait at once.</summary>
        private string? WaitForTun(System.Net.IPAddress ip, Process core)
        {
            for (var i = 0; i < 60 && !core.HasExited; i++)
            {
                if (UnixBroadcastRelay.FindByAddress(ip) is { } tun) return tun.Name;
                if (_stop.Wait(500)) throw new StopRequestedException();
            }
            ThrowIfStopRequested();
            return null;
        }

        private void StopCore()
        {
            _relay?.Dispose();
            _relay = null;
            // DNS 在虚拟网卡还在的时候换回来；路由和防漏规则等核心结束后再撤，中间不漏流量。
            var exit = _exit;
            _exit = null;
            try { exit?.ExitDns(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Send("error " + B64(e.Message)); }
            try { StopCoreProcess(); }
            finally
            {
                try { exit?.ExitRoutes(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Send("error " + B64(e.Message)); }
            }
        }

        private void StopCoreProcess()
        {
            if (_core is not { } core) return;
            _core = null;
            try
            {
                if (!core.HasExited)
                {
                    // 先 SIGTERM 让 EasyTier 自己拆网卡和路由，5 秒没退再强制结束。
                    UnixIds.Terminate(core.Id);
                    if (!core.WaitForExit(5000)) core.Kill(entireProcessTree: true);
                    core.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException) { }
            core.Dispose();
        }

        private void Send(string line)
        {
            lock (_writeLock)
            {
                try
                {
                    var bytes = Utf8.GetBytes(line + "\n");
                    _stream.Write(bytes);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
            }
        }

        private static string B64(string s) => Convert.ToBase64String(Utf8.GetBytes(s));

        public void Dispose()
        {
            StopCore();
            _stream.Dispose();
            _coreExited.Dispose();
            _stop.Dispose();
        }
    }
}

/// <summary>Mac/Linux 上取用户 uid 和 Unix 域套接字对端身份。</summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixIds
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    public static uint EffectiveUserId() => GetEuid();

    public static void Terminate(int pid) => _ = Kill(pid, 15);

    /// <summary>对端进程的 uid；取不到时返回 null。Linux 用 SO_PEERCRED，macOS 用 LOCAL_PEERCRED。</summary>
    public static uint? PeerUserId(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> cred = stackalloc byte[12]; // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                return socket.GetRawSocketOption(1, 17, cred) >= 8 ? BitConverter.ToUInt32(cred[4..8]) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                Span<byte> cred = stackalloc byte[76]; // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
                return socket.GetRawSocketOption(0, 1, cred) >= 8 ? BitConverter.ToUInt32(cred[4..8]) : null;
            }
        }
        catch (SocketException) { }
        return null;
    }

    /// <summary>对端进程号；取不到时返回 null。Linux 用 SO_PEERCRED，macOS 用 LOCAL_PEERPID。</summary>
    public static int? PeerProcessId(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> cred = stackalloc byte[12];
                return socket.GetRawSocketOption(1, 17, cred) >= 4 ? BitConverter.ToInt32(cred[..4]) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                Span<byte> pid = stackalloc byte[4];
                return socket.GetRawSocketOption(0, 2, pid) >= 4 ? BitConverter.ToInt32(pid) : null;
            }
        }
        catch (SocketException) { }
        return null;
    }
}
