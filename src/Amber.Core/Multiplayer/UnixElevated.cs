using Amber.Core.Localization;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 的完整局域网模式：建虚拟网卡（Linux 的 TUN、macOS 的 utun）要 root。和 Windows 的 UAC 守护一样，
/// 只在用户打开这个模式时请求一次权限：Linux 用 pkexec（系统的 polkit 密码框），macOS 用系统的管理员密码框（osascript）。
/// 拿到权限后运行的是 Jay 自己的守护模式（<see cref="UnixElevatedGuardHost"/>），由它启动、重启、结束 easytier-core。
///
/// 守护和 Jay 之间只有一条 Unix 域套接字，放在 mkdtemp 建的 0700 临时目录里；参数和房间密钥只经它传，不落盘。
/// Jay 只接受 root 连上来（核对对端 uid），守护只连属于当前用户、权限是 0700 的目录里的套接字。
/// Jay 退出或崩溃时套接字断开，守护结束 easytier-core 后自己退出。已经是 root 时不弹密码框，直接运行守护（测试、WSL 用）。
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixElevatedGuard : IElevatedGuard
{
    internal const string GuardFlag = "--amber-et-guard";
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>等守护连上来的总时间（含用户在密码框里输密码）。</summary>
    internal static TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    /// <summary>测试用：替换“怎么以 root 身份启动守护”。参数是守护命令行（程序 + 参数），返回启动器进程。</summary>
    internal static Func<IReadOnlyList<string>, Process>? LaunchForTest;

    /// <summary>测试用：守护以当前用户身份运行时也接受（正常只接受 root）。</summary>
    internal static bool AcceptAnyUserForTest;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly StreamWriter _writer;
    private readonly Queue<string> _output = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource> _starts = new();
    private readonly Task _reader;
    private Task _ready = Task.CompletedTask;
    private int _sent;
    private int _acked;
    private volatile bool _disposed;

    private UnixElevatedGuard(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _writer = new StreamWriter(_stream, Utf8) { AutoFlush = true, NewLine = "\n" };
        _reader = ReadAsync();
    }

    public bool HasExited => _reader.IsCompleted;
    public bool ConnectFailed => false;
    public bool StartPending => !HasExited && Volatile.Read(ref _acked) < Volatile.Read(ref _sent);
    public IReadOnlyList<string> RecentOutput { get { lock (_output) return _output.ToList(); } }

    /// <summary>
    /// 请求 root 权限并等守护连上来（这一步会等用户在密码框里确认，和 Windows 的 UAC 一样）。用户取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static UnixElevatedGuard Start(string corePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, ExitNetworkPlan? network, string virtualIp) =>
        StartAsync(corePath, args, env, network, virtualIp).GetAwaiter().GetResult();

    /// <summary>
    /// Like <see cref="Start"/>, without blocking a thread while the password dialog is open. Cancelling closes the dialog
    /// (the launcher is ended) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<UnixElevatedGuard> StartAsync(string corePath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env,
        ExitNetworkPlan? network, string virtualIp, CancellationToken ct = default)
    {
        // 经别人上网要 ip + nft（Linux）：缺的话在请求权限之前就拒绝。
        if (network is not null && !UnixExitNetwork.Supported) throw new PlatformNotSupportedException(L.T("core.mp.exit.unixtools"));
        var dir = Directory.CreateTempSubdirectory("jay-et-");
        var socketPath = Path.Combine(dir.FullName, "s");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Process? launcher = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            ct.ThrowIfCancellationRequested();
            launcher = Launch(GuardCommandLine(socketPath, corePath));
            var accepted = await AcceptRootAsync(listener, launcher, ct).ConfigureAwait(false);
            var guard = new UnixElevatedGuard(accepted);
            guard.Enqueue(args, env, virtualIp, network);
            return guard;
        }
        finally
        {
            listener.Dispose();
            // 连上之后套接字文件和目录就没用了：删掉，别的程序没法再连。
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            launcher?.Dispose();
        }
    }

    /// <summary>守护的命令行：Jay 自己（开发时经 dotnet 运行的话带上程序集）加守护参数。</summary>
    internal static List<string> GuardCommandLine(string socketPath, string corePath)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException(L.T("core.mp.component.startfailed"));
        var line = new List<string> { self };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet" && System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entry) line.Add(entry);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        line.AddRange([GuardFlag, socketPath, UnixIds.EffectiveUserId().ToString(inv), Environment.ProcessId.ToString(inv), corePath]);
        return line;
    }

    private static Process Launch(IReadOnlyList<string> command)
    {
        if (LaunchForTest is { } test) return test(command);
        ProcessStartInfo info;
        if (UnixIds.EffectiveUserId() == 0)
        {
            info = new ProcessStartInfo(command[0]);
            foreach (var a in command.Skip(1)) info.ArgumentList.Add(a);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // 系统管理员密码框：do shell script 会等命令结束，所以守护放到后台，osascript 在用户确认后马上返回。
            var shell = string.Join(' ', command.Select(ShellQuote)) + " </dev/null >/dev/null 2>&1 &";
            info = new ProcessStartInfo("/usr/bin/osascript");
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add($"do shell script \"{AppleScriptEscape(shell)}\" with prompt \"{AppleScriptEscape(L.T("core.mp.component.unixprompt"))}\" with administrator privileges");
        }
        else
        {
            var pkexec = UnixElevation.FindPkexec() ?? throw new PlatformNotSupportedException(L.T("core.mp.component.nopkexec"));
            info = new ProcessStartInfo(pkexec);
            foreach (var a in command) info.ArgumentList.Add(a);
        }
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("core.mp.component.startfailed"));
        process.StandardInput.Close();
        // 输出读掉（不读的话管道满了守护会卡住）；守护的正式输出经套接字回来。
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    /// <summary>
    /// 等 root 连上来：别的进程（不是 root）连上就断开接着等。启动器先退出且不是正常返回（用户取消、没授权）时不再等。
    /// macOS 的 osascript 在用户确认后正常退出（守护在后台），这种情况接着等守护连上来。
    /// </summary>
    private static async Task<Socket> AcceptRootAsync(Socket listener, Process launcher, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        Task<Socket>? accept = null;
        try
        {
            var exited = launcher.WaitForExitAsync(timeout.Token);
            while (true)
            {
                accept ??= listener.AcceptAsync(timeout.Token).AsTask();
                await Task.WhenAny(accept, exited).ConfigureAwait(false);
                if (accept.IsCompletedSuccessfully)
                {
                    var client = accept.Result;
                    accept = null;
                    var peer = UnixIds.PeerUserId(client);
                    if (peer is 0u || AcceptAnyUserForTest && peer == UnixIds.EffectiveUserId()) return client;
                    client.Dispose();
                    continue;
                }
                if (accept.IsCompleted || exited.IsCanceled || exited.IsFaulted)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new IOException(L.T("core.mp.guard.pipefailed"));
                }
                // The launcher ended. macOS's osascript ends normally once the guard runs in the background: keep waiting.
                if (launcher.ExitCode == 0)
                {
                    exited = Task.Delay(Timeout.Infinite, timeout.Token);
                    continue;
                }
                ThrowLauncherFailure(launcher);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Close the password dialog too: nobody is waiting for it any more.
            if (launcher.Id != Environment.ProcessId)
            {
                try { if (!launcher.HasExited) launcher.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            throw;
        }
        finally
        {
            if (accept is { IsCompletedSuccessfully: false }) _ = accept.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
        }
    }

    private static void ThrowLauncherFailure(Process launcher)
    {
        // pkexec：126 = 用户关掉了密码框，127 = 没通过认证；osascript 用户取消时也是非零退出（守护在后台，不会让 osascript 失败）。
        if (OperatingSystem.IsMacOS() || launcher.ExitCode is 126 or 127)
            throw new OperationCanceledException(L.T(OperatingSystem.IsMacOS() ? "core.mp.component.elevationdeclined.mac" : "core.mp.component.elevationdeclined.linux"));
        throw new IOException(L.F("core.mp.guard.exitcode", launcher.ExitCode));
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private void Keep(string line)
    {
        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > 200) _output.Dequeue();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_stream, Utf8, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) OnLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        finally { FailStarts(new IOException(L.T("core.mp.guard.exited"))); }
    }

    private void OnLine(string line)
    {
        var parts = line.Split(' ', 3);
        if (parts.Length == 2 && parts[0] == "out") { Keep(Decode(parts[1])); return; }
        if (parts.Length == 2 && parts[0] == "error") { Keep(Decode(parts[1])); return; }
        if (parts.Length >= 2 && parts[0] is "started" or "failed" && int.TryParse(parts[1], out var n))
        {
            lock (_gate)
            {
                if (!_starts.Remove(n, out var completion)) return;
                Volatile.Write(ref _acked, n);
                if (parts[0] == "started") completion.TrySetResult();
                else
                {
                    var reason = parts.Length == 3 ? Decode(parts[2]) : "EasyTier guard failed";
                    Keep(reason);
                    completion.TrySetException(new IOException(reason));
                }
            }
        }
    }

    private static string Decode(string value)
    {
        try { return Utf8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return "EasyTier guard error"; }
    }

    private void FailStarts(Exception exception)
    {
        lock (_gate)
        {
            foreach (var completion in _starts.Values) completion.TrySetException(exception);
            _starts.Clear();
        }
    }

    public Task StartAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, ExitNetworkPlan? network, string virtualIp)
    {
        if (network is not null && !UnixExitNetwork.Supported) return Task.FromException(new PlatformNotSupportedException(L.T("core.mp.exit.unixtools")));
        return Enqueue(args, env, virtualIp, network);
    }

    private Task Enqueue(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, string virtualIp, ExitNetworkPlan? network)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var n = Interlocked.Increment(ref _sent);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _starts.Add(n, completion);
            _ready = completion.Task;
            _ = _ready.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            var message = JsonSerializer.Serialize(new UnixGuardCommand("start", n, [.. args], new Dictionary<string, string>(env), virtualIp, network), AmberJsonContext.Default.UnixGuardCommand);
            try { _writer.WriteLine(message); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { completion.TrySetException(new IOException(L.T("core.mp.guard.sendfailed"), e)); }
            if (HasExited) completion.TrySetException(new IOException(L.T("core.mp.guard.unavailable")));
            return _ready;
        }
    }

    public Task WaitForReadyAsync(CancellationToken ct = default)
    {
        lock (_gate) return _ready.WaitAsync(ct);
    }

    /// <summary>让守护结束 easytier-core 后退出：发停止命令再关套接字（命令没送到的话，守护看到断开也会结束）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
        {
            _disposed = true;
            try { _writer.WriteLine(JsonSerializer.Serialize(new UnixGuardCommand("stop", 0, [], null, null), AmberJsonContext.Default.UnixGuardCommand)); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        }
        FailStarts(new ObjectDisposedException(nameof(UnixElevatedGuard)));
        try { _socket.Shutdown(SocketShutdown.Send); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        _stream.Dispose();
    }

    /// <summary>等守护断开（测试用）。</summary>
    internal bool WaitForExit(TimeSpan timeout) => _reader.Wait(timeout);
}

/// <summary>Mac/Linux 守护的一条命令（JSON 一行）。参数按数组传，不经过 shell。</summary>
public sealed record UnixGuardCommand(
    [property: JsonPropertyName("cmd")] string Cmd,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("argv")] string[] Argv,
    [property: JsonPropertyName("env")] Dictionary<string, string>? Env,
    [property: JsonPropertyName("vip")] string? VirtualIp,
    [property: JsonPropertyName("network")] ExitNetworkPlan? Network = null);

/// <summary>Mac/Linux 上能不能请求 root 权限。</summary>
public static class UnixElevation
{
    public static string? FindPkexec()
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin", "/run/wrappers/bin", "/usr/sbin" })
        {
            var path = Path.Combine(dir, "pkexec");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>不能用完整局域网模式的原因；能用时返回 null。</summary>
    public static string? UnavailableReason()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return null;
        if (!OperatingSystem.IsLinux()) return L.T("core.mp.component.windowsonly");
        if (UnixIds.EffectiveUserId() == 0) return null;
        return FindPkexec() is null ? L.T("core.mp.component.nopkexec") : null;
    }
}
