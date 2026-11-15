using System.Diagnostics;

namespace Amber.Core.Multiplayer;

/// <summary>
/// macOS/Linux 上的 easytier-core 残留进程记录。只在 PID、启动时间和可执行路径都对得上时结束，避免 PID 复用后伤及别的程序。
/// 记录里还写了启动它的 Jay 进程（PID + 启动时间）：那个 Jay 仍在运行时不算残留，不结束，
/// 这样同时开两个 Jay 时不会互相停掉对方的联机。
/// </summary>
internal static class EasyTierProcessTracker
{
    private const string Prefix = ".jay-easytier-";

    /// <param name="executables">认得的程序：easytier-core 本身，以及套了看门脚本时的 sh。</param>
    public static void Cleanup(string workDir, params string[] executables)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(workDir)) return;
        var expected = executables.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        foreach (var record in Directory.EnumerateFiles(workDir, Prefix + "*.pid"))
        {
            try
            {
                var lines = File.ReadAllLines(record);
                if ((lines.Length != 3 && lines.Length != 5) || !int.TryParse(lines[0], out var pid) || !long.TryParse(lines[1], out var ticks))
                {
                    File.Delete(record);
                    continue;
                }
                // A record whose Jay is still running (another Jay, or this one) belongs to a live session, not a leftover.
                // This Jay counts too: on Mac/Linux a second EasyTier started in the same work dir (the self-check's
                // guest, or any session that overlaps another) must not kill the room this Jay is still hosting.
                if (lines.Length == 5 && int.TryParse(lines[3], out var ownerPid) && long.TryParse(lines[4], out var ownerTicks)
                    && IsAlive(ownerPid, ownerTicks))
                {
                    continue;
                }
                var recordedPath = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]));
                using var process = Process.GetProcessById(pid);
                var sameStart = process.StartTime.ToUniversalTime().Ticks == ticks;
                string? runningPath;
                try { runningPath = process.MainModule?.FileName; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { runningPath = null; }
                if (!sameStart)
                {
                    File.Delete(record); // PID 已被复用。
                    continue;
                }
                if (runningPath is null) continue;
                if (expected.Contains(Path.GetFullPath(runningPath)))
                {
                    var watcher = Path.GetFullPath(runningPath) == Path.GetFullPath(EasyTierProcessGuard.WatcherExecutable);
                    EasyTierProcessGuard.Stop(process, watcher);
                    process.WaitForExit(5000);
                }
                File.Delete(record);
            }
            catch (ArgumentException)
            {
                TryDelete(record); // 进程已经不在。
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                // 记录损坏或当前无权查询时不猜测、不结束进程；下次再试。
            }
        }
    }

    public static string? Track(Process process, string workDir, string corePath) =>
        Track(process, workDir, corePath, Environment.ProcessId, OwnStartTicks());

    internal static string? Track(Process process, string workDir, string corePath, int ownerPid, long ownerStartTicks)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            Directory.CreateDirectory(workDir);
            var record = Path.Combine(workDir, $"{Prefix}{process.Id}-{Guid.NewGuid():N}.pid");
            var text = string.Join('\n', process.Id.ToString(), process.StartTime.ToUniversalTime().Ticks.ToString(),
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(corePath))),
                ownerPid.ToString(), ownerStartTicks.ToString()) + "\n";
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var stream = new FileStream(record, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => TryDelete(record);
            if (process.HasExited) TryDelete(record);
            return record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static long OwnStartTicks()
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime().Ticks;
    }

    private static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using var owner = Process.GetProcessById(pid);
            return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
