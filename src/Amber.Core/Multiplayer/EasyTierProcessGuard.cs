using System.Diagnostics;
using System.Globalization;

namespace Amber.Core.Multiplayer;

/// <summary>
/// Mac/Linux 上保证 Jay 没了 easytier-core 也跟着结束（Windows 用作业对象 <see cref="ProcessJob"/>，这里不管）：
/// 启动时套一层 /bin/sh 看门，每秒看一次 Jay 还在不在，不在了（包括 kill -9）就结束 easytier-core。
/// 看门进程自己也被单独结束时，由 <see cref="EasyTierProcessTracker"/> 在下次开始联机前清理。
/// </summary>
internal static class EasyTierProcessGuard
{
    /// <summary>
    /// The watcher script: $1 is Jay's PID, then easytier-core and its arguments. TERM/INT are passed on to the core.
    /// SIGHUP is ignored before the core starts, so the core inherits that too: the watcher and core share Jay's process
    /// group, and macOS sends SIGHUP to a whole orphaned group that has a stopped member (see <see cref="Stop"/>).
    /// A SIGHUP meant for the group must not take down a room that is still running.
    /// </summary>
    internal const string WatchScript =
        "trap '' HUP; p=$1; shift; \"$@\" & c=$!; " +
        "trap 'kill -TERM $c 2>/dev/null' TERM INT; " +
        "while kill -0 \"$p\" 2>/dev/null && kill -0 \"$c\" 2>/dev/null; do sleep 1; done; " +
        "kill -TERM \"$c\" 2>/dev/null; wait \"$c\"";

    /// <summary>非 Windows：把要启动的 easytier-core 改成经看门脚本启动。参数原样传，不经过 shell 拼接。</summary>
    internal static void Wrap(ProcessStartInfo info)
    {
        if (OperatingSystem.IsWindows()) return;
        var core = info.FileName;
        var args = info.ArgumentList.ToList();
        info.FileName = "/bin/sh";
        info.ArgumentList.Clear();
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(WatchScript);
        info.ArgumentList.Add("jay-easytier");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(core);
        foreach (var a in args) info.ArgumentList.Add(a);
    }

    /// <summary>看门脚本用的 sh 的真实路径（Linux 上 /bin/sh 多半指向 dash 或 bash，进程信息里看到的是真实路径）。</summary>
    internal static string WatcherExecutable { get; } = ResolveShell();

    private static string ResolveShell()
    {
        if (OperatingSystem.IsWindows()) return "/bin/sh";
        // 要和系统报告的进程路径完全一致：/bin 本身也可能是指向 /usr/bin 的链接，所以用 realpath 解析整条路径。
        try
        {
            // macOS /bin/sh starts the shell selected by /private/var/select/sh.
            // Process.MainModule reports that selected executable after startup.
            var shell = OperatingSystem.IsMacOS() ? "/private/var/select/sh" : "/bin/sh";
            var resolved = realpath(shell, IntPtr.Zero);
            if (resolved == IntPtr.Zero) return "/bin/sh";
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(resolved) ?? "/bin/sh"; }
            finally { free(resolved); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return "/bin/sh"; }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern IntPtr realpath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern void free(IntPtr p);

    /// <summary>How long <see cref="Stop"/> waits for the watcher to end the core before forcing it.</summary>
    internal static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Ends an EasyTier process started through <see cref="Wrap"/> (or the core itself on Windows).
    /// On Mac/Linux it asks the watcher with SIGTERM first; its trap ends the core and waits for it.
    /// <c>Process.Kill(entireProcessTree: true)</c> is only the fallback: on Unix it SIGSTOPs the watcher before killing
    /// the tree. A stopped process in Jay's process group, which is orphaned when Jay was started from the Dock or Finder,
    /// makes macOS send SIGHUP to the whole group. That SIGHUP closed Jay itself right after the knock lobby ended (#229).
    /// </summary>
    /// <param name="wrapped">Whether <paramref name="process"/> is the watcher. Left out, it is read from the start info,
    /// which only processes this Jay started have.</param>
    internal static void Stop(Process process, bool? wrapped = null, TimeSpan? timeout = null)
    {
        try
        {
            if (process.HasExited) return;
            if (!OperatingSystem.IsWindows() && (wrapped ?? StartedThroughWatcher(process))
                && SendTerm(process.Id) && process.WaitForExit(timeout ?? StopTimeout))
            {
                return;
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool StartedThroughWatcher(Process process)
    {
        try { return process.StartInfo.FileName == "/bin/sh"; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool SendTerm(int pid)
    {
        try { return kill(pid, 15) == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    /// <summary>给 <see cref="EasyTierProcessTracker"/> 记录的可执行文件：套了看门脚本时是 sh，否则是 easytier-core 本身。</summary>
    internal static string TrackedExecutable(ProcessStartInfo info) =>
        info.FileName == "/bin/sh" ? WatcherExecutable : info.FileName;
}
