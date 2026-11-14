using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Amber.Core.Diagnostics;

/// <summary>
/// A local-only record of how this run of Jay is going, so an exit that leaves no crash report and no console output
/// (a clean shutdown nobody asked for, the breaker restart, or a SIGKILL) can still be told apart afterwards.
/// It holds short breadcrumbs: page status keys (never their arguments, so no codes, addresses or names), helper
/// process starts and exits, POSIX signals, the exit reason and unhandled exception stacks. Nothing is sent anywhere.
/// Files live in <c>&lt;data dir&gt;/diagnostics/</c>: <c>exit-trail.txt</c> for this run and
/// <c>previous-run.txt</c> for the last run that did not exit cleanly.
/// </summary>
public static class ExitTrail
{
    internal const string FolderName = "diagnostics";
    internal const string TrailName = "exit-trail.txt";
    internal const string PreviousName = "previous-run.txt";
    internal const string MarkerName = "running.marker";

    /// <summary>The trail is trimmed to its newest half once it grows past this many bytes.</summary>
    internal const int MaxBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static readonly List<PosixSignalRegistration> Signals = [];
    private static string? _dir;
    private static bool _closed;

    /// <summary>True when the previous run ended without <see cref="CleanExit"/>; its trail is in <see cref="PreviousRunPath"/>.</summary>
    public static bool PreviousRunEndedUnexpectedly { get; private set; }

    public static string? PreviousRunPath => _dir is null ? null : Path.Combine(_dir, PreviousName);

    public static string? TrailPath => _dir is null ? null : Path.Combine(_dir, TrailName);

    /// <summary>Starts the trail for this run. Keeps the previous trail when that run never reached a clean exit.</summary>
    public static void Start(string dataDir, bool hookProcess = true)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.Combine(dataDir, FolderName);
                Directory.CreateDirectory(dir);
                _dir = dir;
                _closed = false;
                var marker = Path.Combine(dir, MarkerName);
                var trail = Path.Combine(dir, TrailName);
                var others = File.Exists(marker) ? MarkerPids(marker).Where(pid => pid != Environment.ProcessId).ToList() : [];
                // Another Jay on the same data folder is still running: its marker is not a sign of an unclean exit, and
                // its trail is still being written, so join it instead of copying and clearing it.
                var sibling = others.Any(pid => IsRunningSince(pid, File.GetLastWriteTimeUtc(marker)));
                PreviousRunEndedUnexpectedly = !sibling && File.Exists(marker);
                if (PreviousRunEndedUnexpectedly && File.Exists(trail)) File.Copy(trail, Path.Combine(dir, PreviousName), overwrite: true);
                if (!sibling) File.WriteAllText(trail, "");
                var pids = (sibling ? others : []).Append(Environment.ProcessId);
                File.WriteAllLines(marker, pids.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _dir = null;
                return;
            }
        }
        var version = typeof(ExitTrail).Assembly.GetName().Version?.ToString() ?? "?";
        Note("start", $"pid {Environment.ProcessId}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, core {version}"
            + (PreviousRunEndedUnexpectedly ? ", previous run did not exit cleanly" : ""));
        if (hookProcess) HookProcess();
    }

    private static void HookProcess()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Exception("AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""), e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Exception("TaskScheduler.UnobservedTaskException", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Note("exit", $"process exit, code {Environment.ExitCode}");
        if (OperatingSystem.IsWindows()) return;
        // Only observe: the default handling (ending the process) still runs.
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try { Signals.Add(PosixSignalRegistration.Create(signal, c => Note("signal", c.Signal.ToString()))); }
            catch (Exception e) when (e is PlatformNotSupportedException or IOException) { }
        }
    }

    /// <summary>Adds one breadcrumb. Callers pass status keys and counts, never codes, addresses or names.</summary>
    public static void Note(string category, string text)
    {
        if (_dir is null) return;
        Append($"{DateTimeOffset.UtcNow:O} [{category}] {Scrub(text)}\n");
    }

    /// <summary>Records an exception with its stack. Messages go through <see cref="Scrub"/> too.</summary>
    public static void Exception(string source, Exception? e)
    {
        if (_dir is null) return;
        Append($"{DateTimeOffset.UtcNow:O} [exception] {source}\n{Scrub(e?.ToString() ?? "(no exception object)")}\n");
    }

    /// <summary>Records a helper process (EasyTier and the like) starting, and its exit code once it ends.</summary>
    public static void TrackProcess(string label, Process process)
    {
        if (_dir is null) return;
        int pid;
        try { pid = process.Id; }
        catch (InvalidOperationException) { return; }
        Note("process", $"{label} started, pid {pid}");
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                string code;
                try { code = process.ExitCode.ToString(CultureInfo.InvariantCulture); }
                catch (InvalidOperationException) { code = "?"; }
                Note("process", $"{label} exited, pid {pid}, code {code}");
            };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The app is ending on purpose: writes the reason and removes the marker, so the next start sees a clean exit.</summary>
    public static void CleanExit(string reason)
    {
        Note("exit", reason);
        lock (Gate)
        {
            if (_dir is null || _closed) return;
            _closed = true;
            try
            {
                // Only this run's line goes: a second Jay that is still running keeps its own.
                var marker = Path.Combine(_dir, MarkerName);
                var rest = File.Exists(marker) ? MarkerPids(marker).Where(pid => pid != Environment.ProcessId).ToList() : [];
                if (rest.Count == 0) File.Delete(marker);
                else File.WriteAllLines(marker, rest.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The process ids written in the marker, one per line; anything unreadable is skipped.</summary>
    private static List<int> MarkerPids(string marker)
    {
        var pids = new List<int>();
        foreach (var line in File.ReadAllLines(marker))
            if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0) pids.Add(pid);
        return pids;
    }

    /// <summary>
    /// Whether a process with this id is running and was already running when the marker was last written. A process
    /// that started later only reuses the id of a run that has since ended.
    /// </summary>
    internal static bool IsRunningSince(int pid, DateTime markerWrittenUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return process.StartTime.ToUniversalTime() <= markerWrittenUtc.AddSeconds(2);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Replaces the user's home folder with ~ so paths in stacks and messages don't carry the account name.</summary>
    internal static string Scrub(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void Append(string line)
    {
        lock (Gate)
        {
            if (_dir is null) return;
            var path = Path.Combine(_dir, TrailName);
            try
            {
                // Open, write and close every time: a SIGKILL right after still leaves this line on disk.
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (new FileInfo(path).Length > MaxBytes) Trim(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Trim(string path)
    {
        var text = File.ReadAllText(path);
        var cut = text.IndexOf('\n', text.Length / 2);
        File.WriteAllText(path, "(older lines trimmed)\n" + (cut < 0 ? "" : text[(cut + 1)..]));
    }

    /// <summary>Tests: forget the current trail without touching files.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _dir = null;
            _closed = false;
            PreviousRunEndedUnexpectedly = false;
        }
    }
}
