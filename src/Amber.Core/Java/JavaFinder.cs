using Amber.Core.Localization;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Amber.Core.Java;

/// <summary>一个本机 Java 安装。</summary>
public sealed record JavaInstall(string JavaPath, string Version, int MajorVersion, bool Is64Bit)
{
    public override string ToString() => L.F("core.java.runtime.description", Version, (Is64Bit ? "64" : "32"), JavaPath);
}

/// <summary>扫描本机已安装的 Java。</summary>
public static partial class JavaFinder
{
    private static string JavaExe => OperatingSystem.IsWindows() ? "java.exe" : "java";

    public static List<JavaInstall> FindAll(string? gameRoot = null)
    {
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) homes.Add(javaHome);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, JavaExe)))
                homes.Add(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir);
        }

        foreach (var parent in CandidateParents(gameRoot))
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var sub in SafeEnumerate(parent))
            {
                homes.Add(sub);
                // Mojang 运行时多套一层：runtime/java-runtime-gamma/windows-x64/java-runtime-gamma
                foreach (var nested in SafeEnumerate(sub))
                foreach (var deeper in SafeEnumerate(nested))
                {
                    homes.Add(deeper);
                    if (OperatingSystem.IsMacOS())
                        homes.Add(Path.Combine(deeper, "jre.bundle", "Contents", "Home"));
                }
            }
        }

        var result = new List<JavaInstall>();
        var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes)
        {
            var exe = Path.Combine(home, "bin", JavaExe);
            if (!File.Exists(exe) || !seenExe.Add(Path.GetFullPath(exe))) continue;
            if (Probe(home, exe) is { } install) result.Add(install);
        }
        return result.OrderByDescending(j => j.MajorVersion).ThenBy(j => j.JavaPath).ToList();
    }

    /// <summary>为指定主版本挑一个最合适的 Java：优先完全匹配，其次比要求高的最低版本。</summary>
    public static JavaInstall? Pick(IEnumerable<JavaInstall> installs, int requiredMajor)
    {
        var list = installs.Where(j => j.Is64Bit || !Environment.Is64BitOperatingSystem).ToList();
        if (list.Count == 0) list = installs.ToList();
        return list.FirstOrDefault(j => j.MajorVersion == requiredMajor)
               ?? list.Where(j => j.MajorVersion > requiredMajor).MinBy(j => j.MajorVersion);
    }

    /// <summary>只要主版本完全一致的 64 位 Java（32 位系统除外）。</summary>
    public static JavaInstall? PickExact(IEnumerable<JavaInstall> installs, int major)
    {
        var list = installs.Where(j => j.MajorVersion == major).ToList();
        return list.FirstOrDefault(j => j.Is64Bit || !Environment.Is64BitOperatingSystem) ?? list.FirstOrDefault();
    }

    private static IEnumerable<string> CandidateParents(string? gameRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (string.IsNullOrEmpty(pf)) continue;
                foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "Android\\openjdk" })
                    yield return Path.Combine(pf, vendor);
            }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/Library/Java/JavaVirtualMachines";
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            yield return Path.Combine(home, "Library", "Java", "JavaVirtualMachines");
        yield return Path.Combine(home, ".jdks");
        if (gameRoot is not null) yield return Path.Combine(gameRoot, "runtime");
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return []; }
    }

    private static JavaInstall? Probe(string home, string exe) =>
        JavaVersionProbe.Detect(home, exe) is { } found ? new JavaInstall(exe, found.Version, ParseMajor(found.Version), found.Is64) : null;

    // Remembered per program file (path, size, time): a probe that had to run Java does not run again for the same file.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), int> s_majors = new();

    /// <summary>
    /// The major version of one java executable: from its files (see <see cref="JavaVersionProbe"/>), else from the
    /// restricted probe. Never runs it any other way. 0 when it cannot be told.
    /// </summary>
    public static int MajorOf(string javaPath, CancellationToken ct = default)
    {
        if (!File.Exists(javaPath)) return 0;
        var full = Path.GetFullPath(javaPath);
        var bin = Path.GetDirectoryName(full) ?? full;
        var home = Path.GetFileName(bin) == "bin" ? Path.GetDirectoryName(bin) ?? bin : bin;
        try
        {
            var info = new FileInfo(full);
            var key = (full, info.Length, info.LastWriteTimeUtc);
            if (s_majors.TryGetValue(key, out var known)) return known;
            var major = JavaVersionProbe.Detect(home, full, ct) is { } found ? ParseMajor(found.Version) : 0;
            if (major > 0) s_majors[key] = major;
            return major;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary><see cref="MajorOf"/> off the calling thread (it may wait for the probe).</summary>
    public static Task<int> MajorOfAsync(string javaPath, CancellationToken ct = default) => Task.Run(() => MajorOf(javaPath, ct), ct);

    /// <summary>Forgets what <see cref="MajorOf"/> remembered (tests).</summary>
    internal static void ForgetMajors() => s_majors.Clear();

    /// <summary>"1.8.0_401" → 8，"17.0.10" → 17，"21" → 21。</summary>
    public static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_', '-', '+');
        if (!int.TryParse(parts[0], out var first)) return 0;
        return first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var second) ? second : first;
    }
}
