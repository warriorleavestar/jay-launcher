using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>
/// What the game process may see when it runs in the OS sandbox (privacy plan item 15): its own instance folder, the
/// Java runtime, the shared game files (versions, libraries, assets), the universal loader's runtime folder and the
/// few files Jay names on its command line. Everything else of the player's (Jay's data folder with its keys,
/// browser profiles, other files) stays out of reach. Network: outbound, plus loopback to Jay for multiplayer.
/// <para>
/// The grants are platform-neutral; <see cref="LinuxSandbox"/> turns them into a bubblewrap command line and
/// <see cref="MacSandbox"/> into a sandbox-exec profile. A grant that is a file is granted alone, never its folder.
/// </para>
/// </summary>
public sealed record SandboxGrants
{
    /// <summary>Folders (or files) the game reads and writes: the instance folder, the bake cache, a chosen saves folder.</summary>
    public IReadOnlyList<string> ReadWrite { get; init; } = [];

    /// <summary>Folders (or files) the game only reads: Java, game files, natives, the loader install, log config, agents.</summary>
    public IReadOnlyList<string> ReadOnly { get; init; } = [];

    /// <summary>
    /// Folders that stay hidden even when a granted folder contains them (Jay's data folder inside a granted game
    /// folder, for example). A grant inside a hidden folder still wins, so Jay can hand over one file from it.
    /// </summary>
    public IReadOnlyList<string> Hidden { get; init; } = [];

    /// <summary>Full paths, without trailing separators, each listed once (read-write wins over read-only).</summary>
    public SandboxGrants Normalized()
    {
        var rw = Distinct(ReadWrite);
        var ro = Distinct(ReadOnly).Where(p => !rw.Contains(p, PathComparer)).ToList();
        return this with { ReadWrite = rw, ReadOnly = ro, Hidden = Distinct(Hidden) };
    }

    /// <summary>
    /// Why these grants cannot be used, or null: a grant must not be a hidden folder itself or contain one that the
    /// sandbox cannot cover (the caller's <paramref name="canHideInside"/>), and every grant must be a full path.
    /// </summary>
    public string? Problem(bool canHideInside = true)
    {
        foreach (var path in ReadWrite.Concat(ReadOnly))
        {
            if (!Path.IsPathFullyQualified(path)) return L.F("core.launch.sandbox.relative", path);
            foreach (var hidden in Hidden)
            {
                if (GameSandbox.SamePath(path, hidden)) return L.F("core.launch.sandbox.grants_data", path);
                if (!canHideInside && GameSandbox.IsInside(hidden, path)) return L.F("core.launch.sandbox.grants_data", path);
            }
        }
        return null;
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static List<string> Distinct(IEnumerable<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(GameSandbox.Full).Distinct(PathComparer).ToList();
}

/// <summary>What a launch asks of the sandbox. Off means the game runs as before, with nothing hidden.</summary>
public sealed record SandboxOptions
{
    public bool Enabled { get; init; }

    /// <summary>Windows only: the level to use (the caller decides; see <see cref="WindowsSandboxLevel"/>).</summary>
    public WindowsSandboxLevel WindowsLevel { get; init; } = WindowsSandboxLevel.Standard;

    /// <summary>Folders the caller adds (the universal loader install and bake data read-only, the bake cache writable).</summary>
    public IReadOnlyList<string> ExtraReadOnly { get; init; } = [];
    public IReadOnlyList<string> ExtraReadWrite { get; init; } = [];

    /// <summary>Jay's data folder (keys, accounts, settings) and anything else that must stay hidden.</summary>
    public IReadOnlyList<string> Hidden { get; init; } = [];

    public static SandboxOptions Off { get; } = new();
}

/// <summary>The sandbox could not start: the game is not launched, and the message says why.</summary>
public sealed class SandboxUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public static class GameSandbox
{
    /// <summary>
    /// True on the systems where Jay has a sandbox for the game (Windows: low integrity or AppContainer, Linux: bubblewrap,
    /// macOS: sandbox-exec). Android and iOS run the game their own way.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// LWJGL and JNA unpack native libraries into <c>java.io.tmpdir</c> unless told otherwise, and the sandboxed game
    /// can write only its instance folder: they unpack into <c>&lt;instance&gt;/natives-extract</c> and
    /// <c>&lt;instance&gt;/jna-tmp</c> instead (the layout the universal loader's sandbox docs name). Empty for a
    /// folder Java could not read from its command line (not ASCII: Java reads it in the ANSI code page on Windows);
    /// the game then keeps its old behaviour. Added first, so an instance's own JVM arguments still win.
    /// </summary>
    public static IReadOnlyList<string> NativeExtractArguments(string workDir)
    {
        var instance = Path.GetFullPath(workDir);
        if (!instance.All(char.IsAscii)) return [];
        var lwjgl = Path.Combine(instance, "natives-extract");
        var jna = Path.Combine(instance, "jna-tmp");
        Directory.CreateDirectory(lwjgl);
        Directory.CreateDirectory(jna);
        return ["-Dorg.lwjgl.system.SharedLibraryExtractPath=" + lwjgl, "-Djna.tmpdir=" + jna];
    }

    /// <summary>
    /// The grants for one launch: the instance folder writable; Java, versions, libraries, assets, natives and the
    /// files named in the JVM arguments read-only; plus what the caller adds.
    /// </summary>
    public static SandboxGrants For(string instanceDir, string gameRoot, string javaPath, IEnumerable<string> jvmArgs,
        SandboxOptions options, IEnumerable<string>? extraReadOnly = null, IEnumerable<string>? extraReadWrite = null)
    {
        var readOnly = new List<string>
        {
            JavaHome(javaPath),
            Path.Combine(gameRoot, "versions"),
            Path.Combine(gameRoot, "libraries"),
            Path.Combine(gameRoot, "assets"),
        };
        readOnly.AddRange(FilesIn(jvmArgs));
        readOnly.AddRange(options.ExtraReadOnly);
        if (extraReadOnly is not null) readOnly.AddRange(extraReadOnly);
        var readWrite = new List<string> { instanceDir };
        readWrite.AddRange(options.ExtraReadWrite);
        if (extraReadWrite is not null) readWrite.AddRange(extraReadWrite);
        return new SandboxGrants { ReadWrite = readWrite, ReadOnly = readOnly, Hidden = options.Hidden }.Normalized();
    }

    /// <summary>The Java installation folder of a java executable (…/bin/java → …), or the executable's folder.</summary>
    public static string JavaHome(string javaPath)
    {
        var bin = Path.GetDirectoryName(Full(javaPath)) ?? javaPath;
        var home = Path.GetFileName(bin) is "bin" ? Path.GetDirectoryName(bin) : null;
        // macOS JDKs keep java in Contents/Home/bin; granting Contents/Home is enough.
        return home ?? bin;
    }

    /// <summary>Existing files named in arguments (-javaagent:…, -Dx=/path, a bare path). Folders are not granted this way.</summary>
    public static IEnumerable<string> FilesIn(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            var (_, path, _) = AsciiPaths.SplitPathArgument(arg);
            if (path is not null && File.Exists(path)) yield return path;
        }
    }

    /// <summary>
    /// The environment a sandboxed game gets: only what Java, the display, sound and graphics drivers need. Everything
    /// else Jay's own process has (tokens, keys, proxy credentials in variables) is dropped.
    /// </summary>
    public static IDictionary<string, string?> FilterEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToList())
            if (!KeepVariable(key)) environment.Remove(key);
        return environment;
    }

    internal static bool KeepVariable(string name)
    {
        var n = name.ToUpperInvariant();
        if (KeptVariables.Contains(n)) return true;
        return KeptPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> KeptVariables = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LANGUAGE", "TZ", "TERM",
        "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP",
        "PULSE_SERVER", "PIPEWIRE_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP",
        "SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "COMSPEC", "PATHEXT", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER", "OS", "USERNAME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)",
        "__CF_USER_TEXT_ENCODING",
    };

    private static readonly string[] KeptPrefixes = ["LC_", "MESA_", "LIBGL_", "__GL_", "__NV_", "DRI_", "VK_", "GALLIUM_", "ALSOFT_", "JAVA_HOME"];

    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool SamePath(string a, string b) =>
        string.Equals(Full(a), Full(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    internal static bool IsInside(string path, string folder)
    {
        var p = Full(path);
        var f = Full(folder);
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(p, f, cmp) || p.StartsWith(f + Path.DirectorySeparatorChar, cmp) || p.StartsWith(f + '/', cmp);
    }
}

/// <summary>What the admin prompt for the loopback exemption would be like for this Windows user.</summary>
public enum AdminPrompt
{
    /// <summary>An administrator with UAC: one yes in the prompt.</summary>
    Prompt,
    /// <summary>Jay already runs elevated: no prompt.</summary>
    Elevated,
    /// <summary>A standard user: the prompt would need someone else's admin password, so Jay does not ask.</summary>
    NoAdmin,
}

/// <summary>The level for one Windows launch, and whether the player must be asked first.</summary>
/// <param name="AskFirst">Strong mode needs the loopback exemption: explain, then one admin prompt.</param>
/// <param name="Reason">Why this launch uses the standard level although strong mode is on (localized), or null.</param>
/// <param name="Fallback">A reason to remember (a localization key): strong mode is not tried again until the player retries.</param>
public sealed record WindowsSandboxDecision(WindowsSandboxLevel Level, bool AskFirst = false, string? Reason = null, string? Fallback = null);

/// <summary>
/// Which Windows sandbox level a launch gets (privacy plan item 15). Strong mode is on by default; when it cannot be
/// used Jay falls back to the standard level without asking again on every launch.
/// </summary>
public static class WindowsSandboxPolicy
{
    public const string Refused = "core.launch.sandbox.win.reason.refused";
    public const string NoAdmin = "core.launch.sandbox.win.reason.no_admin";
    public const string Failed = "core.launch.sandbox.win.reason.failed";

    public static WindowsSandboxDecision Decide(bool strongWanted, string? fallback, string? fallbackDetail, bool javaCanUseStrong, int javaMajor,
        bool exempt, AdminPrompt admin)
    {
        if (!strongWanted) return new(WindowsSandboxLevel.Standard);
        if (fallback is not null) return new(WindowsSandboxLevel.Standard, Reason: Describe(fallback, fallbackDetail));
        if (!javaCanUseStrong) return new(WindowsSandboxLevel.Standard, Reason: L.F("core.launch.sandbox.win.no_dns", javaMajor));
        if (exempt) return new(WindowsSandboxLevel.Strong);
        if (admin == AdminPrompt.NoAdmin) return new(WindowsSandboxLevel.Standard, Reason: Describe(NoAdmin, null), Fallback: NoAdmin);
        return new(WindowsSandboxLevel.Strong, AskFirst: true);
    }

    /// <summary>
    /// Whether a Java can look up names in the strong sandbox: Java 8 with its JNDI DNS provider, Java 18+ when this
    /// build carries Jay's resolver. Java 9–17 have no hook (see <see cref="WindowsSandbox.DnsArguments"/>).
    /// </summary>
    public static bool JavaCanUseStrong(int javaMajor, string javaHome, bool hasResolver) => javaMajor switch
    {
        > 0 and <= 8 => File.Exists(Path.Combine(javaHome, "lib", "ext", "dnsns.jar")),
        >= 18 => hasResolver,
        _ => false,
    };

    /// <summary>The remembered reason as a sentence part ("you declined the admin confirmation.").</summary>
    public static string Describe(string fallback, string? detail) =>
        fallback == Failed ? L.F(Failed, detail ?? "?") : L.T(fallback);
}
