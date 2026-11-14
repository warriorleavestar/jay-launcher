using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Amber.Core.Launch;

namespace Amber.Core.Java;

/// <summary>
/// Which version a Java is. A Java can be any folder the player picked, so it is never simply run: first its files are
/// read (the <c>release</c> file, the version resource of <c>java.exe</c> or <c>jvm.dll</c>, the manifest of
/// <c>rt.jar</c>); only when none of them tells, it runs once in a probe that can reach nothing but the runtime and a
/// private temp folder:
/// <list type="bullet">
/// <item>Windows: an AppContainer of its own, without network capabilities, granted read access to the Java folder for
/// the run only.</item>
/// <item>Linux: bubblewrap, in new namespaces without network, with the system folders and the Java folder read-only and
/// an empty temp folder.</item>
/// </list>
/// Both with a cleared environment, a time limit, a limit on output and the whole process tree killed at the end.
/// Where neither is available (macOS, Linux without bubblewrap) the version stays unknown.
/// </summary>
public static partial class JavaVersionProbe
{
    public sealed record Found(string Version, bool Is64);

    internal const int MaxOutputChars = 64 * 1024;
    internal const int MaxReleaseBytes = 64 * 1024;
    internal const int MaxManifestBytes = 64 * 1024;
    internal const string ContainerName = "Jay.JavaProbe";

    /// <summary>How long the probe may run.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Probes that ran a Java (tests).</summary>
    internal static int RestrictedRuns;

    /// <summary>Why the last probe could not run or answer (diagnostics and tests).</summary>
    internal static string? LastProblem { get; private set; }

    // One probe at a time: the Java folder's temporary grant is taken away again when it ends.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The version from the files, else from the restricted probe; null when neither tells.</summary>
    public static Found? Detect(string home, string exe, CancellationToken ct = default) => FromFiles(home, exe) ?? Restricted(home, exe, ct);

    // ---------------------------------------------------------------- from files only

    /// <summary>The version read from files of the runtime, without running anything; null when they do not tell.</summary>
    public static Found? FromFiles(string home, string exe)
    {
        string? version = null;
        bool? is64 = null;
        if (ContainedFiles.ReadSmall(Path.Combine(home, "release"), MaxReleaseBytes) is { } release)
        {
            foreach (var line in Encoding.UTF8.GetString(release).Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) version = trimmed["JAVA_VERSION=".Length..].Trim('"');
                else if (trimmed.StartsWith("OS_ARCH=", StringComparison.Ordinal)) is64 = trimmed.Contains("64");
            }
        }
        if (version is { Length: > 0 } && JavaFinder.ParseMajor(version) <= 0) version = null;
        version ??= FromVersionResource(home, exe) ?? FromRuntimeJar(home);
        if (version is null) return null;
        return new Found(version, is64 ?? Is64Executable(exe) ?? true);
    }

    /// <summary>Windows: the file version of java.exe or jvm.dll (8.0.4010.10 for 8u401, 17.0.10.0 for 17.0.10).</summary>
    internal static string? FromVersionResource(string home, string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string[] files =
        [
            exe,
            Path.Combine(home, "bin", "server", "jvm.dll"),
            Path.Combine(home, "bin", "client", "jvm.dll"),
            Path.Combine(home, "jre", "bin", "server", "jvm.dll"),
        ];
        foreach (var file in files)
        {
            // Only real program files: GetVersionInfo reads the resource as data and runs nothing.
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (Is64Executable(file) is null) continue;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(file);
                var major = info.FileMajorPart == 1 && info.FileMinorPart > 1 ? info.FileMinorPart : info.FileMajorPart;
                if (major is < 5 or > 99) continue;
                return major <= 8 ? $"1.{major}.0" : $"{major}.{info.FileMinorPart}.{info.FileBuildPart}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>Java 8 and older: the version in rt.jar's manifest; Java 9+: in jrt-fs.jar's.</summary>
    internal static string? FromRuntimeJar(string home)
    {
        foreach (var jar in new[] { Path.Combine(home, "lib", "rt.jar"), Path.Combine(home, "jre", "lib", "rt.jar"), Path.Combine(home, "lib", "jrt-fs.jar") })
        {
            try
            {
                if (!File.Exists(jar)) continue;
                using var zip = ZipFile.OpenRead(jar);
                if (zip.GetEntry("META-INF/MANIFEST.MF") is not { Length: > 0 and <= MaxManifestBytes } entry) continue;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string? implementation = null, specification = null;
                while (reader.ReadLine() is { } line)
                {
                    if (line.StartsWith("Implementation-Version:", StringComparison.Ordinal)) implementation = line["Implementation-Version:".Length..].Trim();
                    else if (line.StartsWith("Specification-Version:", StringComparison.Ordinal)) specification = line["Specification-Version:".Length..].Trim();
                }
                foreach (var v in new[] { implementation, specification })
                    if (v is { Length: > 0 } && JavaFinder.ParseMajor(v) is >= 5 and <= 99) return v;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { }
        }
        return null;
    }

    /// <summary>Whether a program file is 64-bit, from its PE or ELF header; null when it is neither.</summary>
    internal static bool? Is64Executable(string file)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return ContainedFiles.ReadSmall(file, 64 * 1024 * 1024) is { Length: > 4 } elf && elf[0] == 0x7F && elf[1] == (byte)'E' && elf[2] == (byte)'L' && elf[3] == (byte)'F'
                    ? elf[4] == 2 : null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[4096];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (read < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z') return null;
            var pe = BitConverter.ToInt32(head, 0x3C);
            if (pe < 0 || pe + 6 > read || head[pe] != (byte)'P' || head[pe + 1] != (byte)'E' || head[pe + 2] != 0 || head[pe + 3] != 0) return null;
            var machine = BitConverter.ToUInt16(head, pe + 4);
            return machine is 0x8664 or 0xAA64;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // ---------------------------------------------------------------- the restricted probe

    /// <summary>Runs <c>java -XshowSettings:properties -version</c> in the restricted probe; null when it cannot tell.</summary>
    public static Found? Restricted(string home, string exe, CancellationToken ct = default)
    {
        Gate.Wait(ct);
        try
        {
            string? output = null;
            if (OperatingSystem.IsWindows()) output = RunWindows(home, exe, ct);
            else if (OperatingSystem.IsLinux()) output = RunLinux(home, exe, ct);
            ct.ThrowIfCancellationRequested();
            if (output is null) return null;
            if (Parse(output) is { } found) return found;
            LastProblem = "no version in: " + output[..Math.Min(output.Length, 400)];
            return null;
        }
        finally { Gate.Release(); }
    }

    internal static Found? Parse(string output)
    {
        var version = VersionProperty().Match(output) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        if (version is null || JavaFinder.ParseMajor(version) is < 1 or > 99) return null;
        var arch = ArchProperty().Match(output) is { Success: true } a ? a.Groups[1].Value.Trim() : "";
        return new Found(version, arch.Length == 0 || arch.Contains("64"));
    }

    private static readonly string[] Arguments = ["-XshowSettings:properties", "-version"];

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? RunWindows(string home, string exe, CancellationToken ct)
    {
        System.Security.Principal.SecurityIdentifier sid;
        string temp;
        try
        {
            sid = WindowsSandbox.ContainerSid(ContainerName);
            temp = Path.Combine(WindowsSandbox.ContainerFolder(sid), "Temp", "probe-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(temp);
        }
        catch (Exception e) when (e is SandboxUnavailableException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            LastProblem = e.Message;
            return null;
        }

        // Read access to the runtime for this run only (a Java under Program Files is readable to every container already).
        var granted = new List<string>();
        foreach (var folder in Homes(home, exe))
        {
            if (WindowsSandbox.NamesExplicitly(folder, sid)) continue;
            try
            {
                WindowsSandbox.Grant(folder, sid, write: false);
                granted.Add(folder);
            }
            catch (SandboxUnavailableException) { }
        }
        try
        {
            var psi = new ProcessStartInfo(Path.GetFullPath(exe))
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = temp,
            };
            foreach (var arg in Arguments) psi.ArgumentList.Add(arg);
            var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            psi.Environment.Clear();
            psi.Environment["SystemRoot"] = system;
            psi.Environment["windir"] = system;
            psi.Environment["SystemDrive"] = Path.GetPathRoot(system)?.TrimEnd('\\') ?? "C:";
            psi.Environment["ComSpec"] = Path.Combine(system, "System32", "cmd.exe");
            psi.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            psi.Environment["PATH"] = Path.Combine(system, "System32") + ";" + system;
            // Windows needs it to start a container process and points it at the container's own folder.
            psi.Environment["LOCALAPPDATA"] = WindowsSandbox.ContainerFolder(sid);
            Interlocked.Increment(ref RestrictedRuns);
            using var process = WindowsSandbox.StartProbe(psi, sid, temp);
            return Collect(process, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or SandboxUnavailableException or IOException or InvalidOperationException)
        {
            LastProblem = e.Message;
            return null;
        }
        finally
        {
            foreach (var folder in granted)
            {
                try { WindowsSandbox.Ungrant(folder, sid); }
                catch (Exception e) when (e is SandboxUnavailableException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            try { Directory.Delete(temp, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? RunLinux(string home, string exe, CancellationToken ct)
    {
        if (LinuxSandbox.Problem() is not null || LinuxSandbox.Bubblewrap is not { } bwrap) return null;
        var args = new List<string> { "--die-with-parent", "--new-session", "--unshare-all", "--hostname", "jay-probe" };
        foreach (var folder in LinuxSandbox.SystemFolders)
        {
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is { } target) args.AddRange(["--symlink", target, folder]);
            else if (info.Exists) args.AddRange(["--ro-bind", folder, folder]);
        }
        args.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", "/var/tmp", "--tmpfs", "/run"]);
        foreach (var folder in Homes(home, exe).OrderBy(f => f.Length)) args.AddRange(["--ro-bind", folder, folder]);
        args.AddRange(["--chdir", "/tmp", "--", Path.GetFullPath(exe), .. Arguments]);
        var psi = new ProcessStartInfo(bwrap)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment.Clear();
        psi.Environment["PATH"] = "/usr/bin:/bin";
        psi.Environment["HOME"] = "/tmp";
        psi.Environment["TMPDIR"] = "/tmp";
        psi.Environment["LANG"] = "C";
        try
        {
            Interlocked.Increment(ref RestrictedRuns);
            using var process = Process.Start(psi);
            return process is null ? null : Collect(process, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LastProblem = e.Message;
            return null;
        }
    }

    /// <summary>The runtime folders the probe may read: the Java home, and where the program really is when it is a link.</summary>
    private static IEnumerable<string> Homes(string home, string exe)
    {
        var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            GameSandbox.Full(home),
            GameSandbox.JavaHome(exe),
        };
        try
        {
            if (File.ResolveLinkTarget(exe, returnFinalTarget: true) is { } real) folders.Add(GameSandbox.JavaHome(real.FullName));
        }
        catch (IOException) { }
        return folders.Where(Directory.Exists);
    }

    /// <summary>Both outputs, each up to the limit, within the time limit; null otherwise. The process tree is always killed.</summary>
    private static string? Collect(Process process, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(Timeout);
        try
        {
            var error = ReadCapped(process.StandardError, stop);
            var output = ReadCapped(process.StandardOutput, stop);
            Task.WaitAll([error, output, process.WaitForExitAsync(stop.Token)], stop.Token);
            LastProblem = null;
            return error.Result + "\n" + output.Result;
        }
        catch (Exception e) when (e is OperationCanceledException or AggregateException)
        {
            LastProblem = ct.IsCancellationRequested ? "cancelled" : "no answer in time, or too much output";
            return null;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    private static async Task<string> ReadCapped(StreamReader reader, CancellationTokenSource stop)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer, stop.Token)) > 0)
        {
            if (text.Length + n > MaxOutputChars)
            {
                // Too much: not a version line. Stops the whole probe.
                stop.Cancel();
                throw new OperationCanceledException(stop.Token);
            }
            text.Append(buffer, 0, n);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"java\.version = (.+)")]
    private static partial Regex VersionProperty();

    [GeneratedRegex(@"os\.arch = (.+)")]
    private static partial Regex ArchProperty();
}

/// <summary>A Java whose version Jay could not tell, neither from its files nor in the restricted probe.</summary>
public sealed class JavaVersionUnknownException(string javaPath)
    : InvalidOperationException(Localization.L.F("core.java.version_unknown", javaPath))
{
    public string JavaPath { get; } = javaPath;
}
