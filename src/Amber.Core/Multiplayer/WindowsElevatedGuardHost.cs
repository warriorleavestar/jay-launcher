using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Multiplayer;

/// <summary>
/// The Windows full-LAN guard: Jay itself, started through the admin prompt with
/// <c>Jay.exe --jay-lan-guard &lt;pipe id&gt; &lt;Jay pid&gt; &lt;user SID&gt; &lt;package path&gt;</c>.
/// The guard checks the package and runs easytier-core only from its own administrators-only copy
/// (<see cref="ElevatedCoreStore"/>), never from the user's data folder.
/// It connects to the four pipes the non-admin Jay created, checks they belong to that Jay process (server process id,
/// owner is the user or Administrators) before reading anything, then starts, restarts and stops easytier-core on
/// command and sends its output back through the pipes. The room key only travels through the pipe; nothing is written
/// to disk. When the command pipe breaks (Jay quit or crashed), a stop arrives, or easytier-core exits on its own, the
/// guard stops everything and exits. The guard and every child are in a kill-on-close job, so killing the guard also
/// ends easytier-core.
/// <para>
/// The remote exit and hotspot sharing still run the fixed PowerShell setup (<see cref="ExitNetworkHelper"/>) in a child
/// process fed through standard input, and only once a session actually uses a remote exit. Plain full LAN never starts
/// PowerShell. Messages here are for developers (the guard has no window), so they are not translated.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsElevatedGuardHost
{
    public const string GuardFlag = "--jay-lan-guard";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsGuardInvocation(string[] args) => args.Length > 0 && args[0] == GuardFlag;

    /// <summary>The guard's command line after the executable.</summary>
    internal static IReadOnlyList<string> Arguments(string pipeId, int jayPid, string userSid, string packagePath) =>
        [GuardFlag, pipeId, jayPid.ToString(System.Globalization.CultureInfo.InvariantCulture), userSid, packagePath];

    /// <summary>Guard entry point. Exit code: 0 normal end, 2 bad command line, 3 the pipes did not check out, 4 a failure while running.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 5 || args[0] != GuardFlag) return 2;
        var id = args[1];
        if (id.Length != 32 || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return 2;
        if (!uint.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var jayPid)) return 2;
        SecurityIdentifier user;
        try { user = new SecurityIdentifier(args[3]); }
        catch (ArgumentException) { return 2; }
        var packagePath = args[4];
        if (!Path.IsPathFullyQualified(packagePath)) return 2;

        // Join a kill-on-close job before starting anything: every child (easytier-core, the exit helper) inherits it.
        if (!GuardJob.Protect()) return 4;

        var pipes = new List<NamedPipeClientStream>();
        try
        {
            var clock = Stopwatch.StartNew();
            var limit = TimeSpan.FromSeconds(ElevatedGuard.GuardConnectSeconds);
            NamedPipeClientStream Connect(string kind, PipeDirection direction)
            {
                var pipe = new NamedPipeClientStream(".", $"amber-et-{id}-{kind}", direction, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                pipes.Add(pipe);
                var left = limit - clock.Elapsed;
                if (left <= TimeSpan.Zero) throw new TimeoutException();
                pipe.Connect(left);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != jayPid) throw new UnauthorizedAccessException("pipe server mismatch");
                var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (owner != user && owner != new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) throw new UnauthorizedAccessException("pipe owner mismatch");
                return pipe;
            }
            var cmd = Connect("cmd", PipeDirection.In);
            var output = Connect("out", PipeDirection.Out);
            var error = Connect("err", PipeDirection.Out);
            var ack = Connect("ack", PipeDirection.Out);
            var core = new Lazy<string>(() => ResolveCore(packagePath, jayPid), LazyThreadSafetyMode.ExecutionAndPublication);
            using var session = new GuardSession(cmd, output, error, ack, () => core.Value, () => new ExitNetworkHelper());
            return session.Run();
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException) { return 3; }
        finally
        {
            foreach (var pipe in pipes) pipe.Dispose();
        }
    }

    /// <summary>
    /// The program to run. A package (what Jay passes) is checked and run from the administrators-only copy. A program path
    /// is accepted only when this guard has no more rights than the Jay that asked for it (tests and a Jay that already runs
    /// as administrator), so the guard never runs a program the user's account could have swapped with rights it lacks.
    /// </summary>
    internal static string ResolveCore(string path, uint jayPid)
    {
        if (ElevatedCoreStore.IsPackage(path)) return ElevatedCoreStore.Prepare(path);
        if (!IsElevated(GetCurrentProcess()) || ProcessIsElevated(jayPid)) return path;
        throw new UnauthorizedAccessException("The guard only runs easytier-core from the checked package");
    }

    private static bool ProcessIsElevated(uint pid)
    {
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try { return IsElevated(process); }
        finally { CloseHandle(process); }
    }

    private static bool IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token)) return false; // TOKEN_QUERY
        try { return GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated != 0; } // TokenElevation
        finally { CloseHandle(token); }
    }

    /// <summary>One guard run over connected pipes. Internal so tests can drive it with a fake exit helper.</summary>
    internal sealed class GuardSession(Stream cmd, Stream output, Stream error, Stream ack, Func<string> resolveCore, Func<IExitNetworkHelper> helperFactory) : IDisposable
    {
        public GuardSession(Stream cmd, Stream output, Stream error, Stream ack, string corePath, Func<IExitNetworkHelper> helperFactory)
            : this(cmd, output, error, ack, () => corePath, helperFactory) { }

        private readonly StreamWriter _ack = new(ack, Utf8) { AutoFlush = true, NewLine = "\n" };
        private readonly object _ackLock = new();
        private Process? _core;
        private Task[] _copies = [];
        private IExitNetworkHelper? _helper;
        private bool _exitActive;

        /// <summary>Where the session is: tests check the teardown order through this.</summary>
        internal Action<string>? Trace { get; init; }

        public int Run()
        {
            using var reader = new StreamReader(cmd, Utf8);
            var code = 0;
            try
            {
                var pending = reader.ReadLineAsync();
                while (true)
                {
                    if (pending.Wait(500))
                    {
                        var line = pending.Result;
                        if (line is null) break;
                        var msg = JsonSerializer.Deserialize(line, AmberJsonContext.Default.GuardCommand);
                        if (msg is null || msg.Cmd == "stop") break;
                        if (msg.Cmd == "start" && !Start(msg)) { code = 4; break; }
                        if (msg.Cmd == "hotspot") Hotspot(msg);
                        pending = reader.ReadLineAsync();
                    }
                    else if (_core is { HasExited: true }) break;
                    _helper?.Tick(Forward);
                }
            }
            catch (Exception e)
            {
                code = 4;
                SendError(Message(e));
            }
            finally
            {
                try { StopSession(); }
                catch (Exception e) { SendError(Message(e)); }
            }
            return code;
        }

        private bool Start(GuardCommand msg)
        {
            try
            {
                if (msg.Network is not null) _helper ??= helperFactory();
                StopSession();
                if (msg.Network is { } plan) _helper!.EnterRoutes(plan, Forward);
                StartCore(msg.Args, msg.Env);
                if (msg.Network is { } ready)
                {
                    _helper!.Ready(ready, _core!.Id, Forward);
                    _exitActive = true;
                }
                if (_core!.HasExited) throw new IOException("Core exited during startup");
                Send($"started {msg.N}");
                return true;
            }
            catch (Exception e)
            {
                var failure = Message(e);
                try { StopSession(); }
                catch (Exception cleanup) { SendError(Message(cleanup)); }
                Send($"failed {msg.N} {B64(failure)}");
                SendError(failure);
                return false;
            }
        }

        private void Hotspot(GuardCommand msg)
        {
            try
            {
                if (_core is null || _core.HasExited) throw new InvalidOperationException("The multiplayer session has ended");
                var on = msg.Hotspot == true;
                if (on && (!_exitActive || _helper is null)) throw new InvalidOperationException("Select a remote exit in full LAN mode before sharing a hotspot");
                var active = _helper?.SetHotspot(on, Forward) ?? false;
                Send(active ? "hotspot on" : "hotspot off");
                Send($"started {msg.N}");
            }
            catch (Exception e)
            {
                // Sharing must end before the failure is reported; if even that fails, the guard stops the whole session.
                if (_helper is { } helper)
                {
                    try { helper.SetHotspot(false, Forward); }
                    catch (Exception cleanup) { SendError(Message(cleanup)); throw; }
                }
                Send("hotspot off");
                Send($"failed {msg.N} {B64(Message(e))}");
            }
        }

        private void StartCore(string arguments, IReadOnlyDictionary<string, string>? env)
        {
            var corePath = resolveCore();
            var info = new ProcessStartInfo(corePath)
            {
                Arguments = arguments,
                // Not Jay's working folder: easytier-core and the libraries it loads come from its own folder only.
                WorkingDirectory = Path.GetDirectoryName(corePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Only the allow-listed key reaches easytier-core.
            if (env is not null && env.TryGetValue(EasyTierArgs.SecretVariable, out var secret)) info.Environment[EasyTierArgs.SecretVariable] = secret;
            var process = Process.Start(info) ?? throw new IOException("easytier-core did not start");
            _core = process;
            _copies = [process.StandardOutput.BaseStream.CopyToAsync(output), process.StandardError.BaseStream.CopyToAsync(error)];
            Trace?.Invoke("core-start");
        }

        private void StopCore()
        {
            Trace?.Invoke("core");
            if (_core is { } core)
            {
                if (!core.HasExited)
                {
                    core.Kill();
                    if (!core.WaitForExit(5000)) throw new IOException("Core did not stop; bypass leases retained");
                }
                core.Dispose();
            }
            foreach (var copy in _copies)
            {
                try { copy.Wait(3000); }
                catch (AggregateException) { }
            }
            _copies = [];
            _core = null;
        }

        /// <summary>
        /// Teardown order matters: hotspot sharing and DNS are restored while the virtual adapter still exists, the core
        /// stops, the underlay bypass routes go only after the capture routes are gone, and the traffic policy is released
        /// last even when an earlier step failed.
        /// </summary>
        internal void StopSession()
        {
            string? dnsFailure = null;
            try
            {
                if (_helper is { } helper)
                {
                    try { helper.PreStop(Forward); }
                    catch (Exception e) { dnsFailure = Message(e); }
                    finally { Send("hotspot off"); }
                }
                _exitActive = false;
                StopCore();
                _helper?.ReleaseRoutes(Forward);
                if (dnsFailure is not null) throw new IOException(dnsFailure);
            }
            finally
            {
                _helper?.ExitPolicy(Forward);
            }
        }

        /// <summary>Lines from the exit helper: errors and warnings go to Jay's log, hotspot state to the acknowledgement pipe.</summary>
        private void Forward(string kind, string text)
        {
            if (kind == "h") Send(text == "on" ? "hotspot on" : "hotspot off");
            else SendError(text);
        }

        private void SendError(string message) => Send("error " + B64(message));

        private void Send(string line)
        {
            lock (_ackLock)
            {
                try { _ack.WriteLine(line); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
        }

        private static string Message(Exception e) => e is AggregateException { InnerException: { } inner } ? inner.Message : e.Message;

        private static string B64(string s) => Convert.ToBase64String(Utf8.GetBytes(s));

        public void Dispose()
        {
            _helper?.Dispose();
            try { _ack.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>Puts the guard in a job that ends every process in it when the guard's last handle closes (the guard exits or is killed).</summary>
    internal static class GuardJob
    {
        private static IntPtr _job;

        public static bool Protect()
        {
            if (_job != IntPtr.Zero) return true;
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return false;
            var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, 9, buffer, (uint)size) || !AssignProcessToJobObject(job, GetCurrentProcess())) return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
            // Keep the only handle open for the guard's lifetime: Windows closes it at exit and ends the children with it.
            _job = job;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
