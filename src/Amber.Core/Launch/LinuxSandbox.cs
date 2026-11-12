using System.Collections.Concurrent;
using System.Diagnostics;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>
/// The Linux game sandbox: bubblewrap (bwrap) runs Java in new namespaces (mount, PID, IPC, UTS, user) on a root
/// that holds only the system folders (/usr, /etc and friends, read-only), the GPU devices, the display and sound
/// sockets, and the grants. The player's home folder is an empty tmpfs with only the granted folders in it, so Jay's
/// data folder, keys and browser profiles are not there at all. The network namespace is shared: the game reaches
/// the internet and Jay's loopback port as before. Other processes, Jay's included, are invisible (own PID namespace).
/// </summary>
public static class LinuxSandbox
{
    /// <summary>What the builder needs from the running system; tests pass their own.</summary>
    public sealed record Host
    {
        public required string Home { get; init; }
        public string? RuntimeDir { get; init; }
        public string? Display { get; init; }
        public string? XAuthority { get; init; }
        public string? WaylandDisplay { get; init; }
        public string? PulseServer { get; init; }
        public Func<string, bool> Exists { get; init; } = p => File.Exists(p) || Directory.Exists(p);
        /// <summary>The target of a symbolic link (one level, made absolute), or null when the path is not a link.</summary>
        public Func<string, string?> LinkTarget { get; init; } = DefaultLinkTarget;
        public Func<IEnumerable<string>> GpuDevices { get; init; } = DefaultGpuDevices;

        public static Host Current() => new()
        {
            Home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Display = Environment.GetEnvironmentVariable("DISPLAY"),
            XAuthority = Environment.GetEnvironmentVariable("XAUTHORITY"),
            WaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            PulseServer = Environment.GetEnvironmentVariable("PULSE_SERVER"),
        };

        private static string? DefaultLinkTarget(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not { } target) return null;
                return Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? "/", target));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        private static IEnumerable<string> DefaultGpuDevices()
        {
            try { return Directory.EnumerateFiles("/dev", "nvidia*").ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>System folders bound read-only (each only when it exists; links are recreated as links).</summary>
    internal static readonly string[] SystemFolders = ["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/etc", "/nix/store", "/gnu/store"];

    /// <summary>The parts of /sys that Mesa and the GPU drivers read.</summary>
    internal static readonly string[] SysFolders = ["/sys/dev", "/sys/devices", "/sys/class", "/sys/bus"];

    /// <summary>
    /// bwrap's arguments (without "bwrap" itself) to run <paramref name="command"/> in <paramref name="workDir"/> with
    /// these grants. Pure: everything it needs from the system comes from <paramref name="host"/>.
    /// </summary>
    public static List<string> Arguments(SandboxGrants grants, Host host, string workDir, IReadOnlyList<string> command)
    {
        grants = grants.Normalized();
        var args = new List<string> { "--die-with-parent", "--new-session", "--unshare-all", "--share-net", "--hostname", "jay-game" };

        foreach (var folder in SystemFolders)
        {
            if (host.LinkTarget(folder) is { } target)
            {
                // Merged /usr: /bin → usr/bin. Keep the link so paths resolve the same inside.
                args.AddRange(["--symlink", RelativeLink(folder, target), folder]);
            }
            else if (host.Exists(folder)) args.AddRange(["--ro-bind", folder, folder]);
        }
        args.AddRange(["--proc", "/proc", "--dev", "/dev"]);
        if (host.Exists("/dev/dri")) args.AddRange(["--dev-bind", "/dev/dri", "/dev/dri"]);
        foreach (var device in host.GpuDevices()) args.AddRange(["--dev-bind", device, device]);
        foreach (var sys in SysFolders)
            if (host.Exists(sys)) args.AddRange(["--ro-bind", sys, sys]);

        args.AddRange(["--tmpfs", "/tmp", "--tmpfs", "/var/tmp", "--tmpfs", "/run"]);
        // /etc/resolv.conf often points into /run (systemd-resolved); bring that one file so DNS keeps working.
        if (host.LinkTarget("/etc/resolv.conf") is { } resolv && !resolv.StartsWith("/etc/", StringComparison.Ordinal) && host.Exists(resolv))
            args.AddRange(["--ro-bind", resolv, resolv]);
        // The home folder exists but is empty: granted folders inside it are mounted on top below.
        args.AddRange(["--tmpfs", host.Home]);

        if (host.RuntimeDir is { Length: > 0 } runtime)
            args.AddRange(["--perms", "0700", "--dir", runtime]);
        AddDisplayAndSound(args, host);

        // Grants and hidden folders, outermost first, so a folder inside another is mounted after it.
        var mounts = grants.ReadOnly.Select(p => (Path: p, Op: "--ro-bind"))
            .Concat(grants.ReadWrite.Select(p => (Path: p, Op: "--bind")))
            .Concat(grants.Hidden.Where(h => grants.ReadOnly.Concat(grants.ReadWrite).Any(g => GameSandbox.IsInside(h, g) && !GameSandbox.SamePath(h, g)))
                .Select(h => (Path: h, Op: "--tmpfs")))
            .OrderBy(m => Depth(m.Path)).ThenBy(m => m.Op == "--tmpfs" ? 0 : 1);
        foreach (var (path, op) in mounts)
        {
            if (op == "--tmpfs") args.AddRange(["--tmpfs", path]);
            else if (host.Exists(path)) args.AddRange([op, path, path]);
        }

        args.AddRange(["--chdir", workDir, "--"]);
        args.AddRange(command);
        return args;
    }

    private static void AddDisplayAndSound(List<string> args, Host host)
    {
        // X11: DISPLAY=:N (or unix:N) uses /tmp/.X11-unix/XN. A TCP display needs nothing.
        if (host.Display is { Length: > 0 } display && X11Number(display) is { } number)
        {
            var socket = "/tmp/.X11-unix/X" + number;
            var real = host.LinkTarget("/tmp/.X11-unix") is { } x11Dir ? x11Dir.TrimEnd('/') + "/X" + number : socket;
            if (host.Exists(real)) args.AddRange(["--ro-bind", real, socket]);
            if (host.XAuthority is { Length: > 0 } xauth && host.Exists(xauth)) args.AddRange(["--ro-bind", xauth, xauth]);
        }
        if (host.RuntimeDir is { Length: > 0 } runtime)
        {
            if (host.WaylandDisplay is { Length: > 0 } wayland)
            {
                var socket = Path.IsPathFullyQualified(wayland) ? wayland : runtime.TrimEnd('/') + "/" + wayland;
                BindSocket(args, host, socket);
            }
            BindSocket(args, host, runtime.TrimEnd('/') + "/pulse/native");
            BindSocket(args, host, runtime.TrimEnd('/') + "/pipewire-0");
        }
        if (host.PulseServer is { Length: > 0 } pulse && pulse.StartsWith("unix:", StringComparison.Ordinal))
            BindSocket(args, host, pulse["unix:".Length..]);
    }

    private static void BindSocket(List<string> args, Host host, string path)
    {
        var real = host.LinkTarget(path) ?? path;
        if (host.Exists(real)) args.AddRange(["--ro-bind", real, path]);
    }

    internal static string? X11Number(string display)
    {
        var colon = display.LastIndexOf(':');
        if (colon < 0) return null;
        var hostPart = display[..colon];
        if (hostPart.Length > 0 && hostPart != "unix") return null;
        var number = display[(colon + 1)..].Split('.')[0];
        return number.Length > 0 && number.All(char.IsAsciiDigit) ? number : null;
    }

    private static int Depth(string path) => path.Count(c => c == '/');

    private static string RelativeLink(string link, string target)
    {
        var parent = Path.GetDirectoryName(link) ?? "/";
        return target.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal) ? target[(parent.TrimEnd('/').Length + 1)..] : target;
    }

    // ---- running bwrap ----

    private static readonly Lazy<string?> s_bwrap = new(() => FindOnPath("bwrap"));

    /// <summary>The bwrap executable, or null when it is not installed.</summary>
    public static string? Bubblewrap => s_bwrap.Value;

    private static string? s_probeProblem;
    private static bool s_probed;
    private static readonly Lock s_probeLock = new();

    /// <summary>
    /// Why bwrap cannot sandbox here (not installed, user namespaces turned off, blocked by AppArmor), or null when a
    /// test run worked. Checked once per Jay session.
    /// </summary>
    public static string? Problem()
    {
        lock (s_probeLock)
        {
            if (s_probed) return s_probeProblem;
            s_probed = true;
            if (Bubblewrap is not { } bwrap) return s_probeProblem = L.T("core.launch.sandbox.linux.no_bwrap");
            try
            {
                var psi = new ProcessStartInfo(bwrap) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var a in new[] { "--unshare-all", "--share-net", "--die-with-parent", "--ro-bind", "/", "/", "--proc", "/proc", "--dev", "/dev", "--", "true" })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var error = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10_000)) { p.Kill(); return s_probeProblem = L.F("core.launch.sandbox.linux.failed", "timeout"); }
                return s_probeProblem = p.ExitCode == 0 ? null : L.F("core.launch.sandbox.linux.failed", error.Trim().Split('\n')[0]);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            {
                return s_probeProblem = L.F("core.launch.sandbox.linux.failed", e.Message);
            }
        }
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- the game inside: bwrap's PID is not the game's ----

    private static readonly ConcurrentDictionary<int, bool> s_sandboxed = new();

    /// <summary>Jay started bwrap with this PID for a game; <see cref="LinuxGameProcess.RequestExit"/> then looks inside.</summary>
    internal static void Track(Process bwrap)
    {
        s_sandboxed[bwrap.Id] = true;
        bwrap.EnableRaisingEvents = true;
        bwrap.Exited += (_, _) => s_sandboxed.TryRemove(bwrap.Id, out _);
    }

    internal static bool IsSandboxed(int processId) => s_sandboxed.ContainsKey(processId);

    /// <summary>How many sandboxed games are running (window matching by the inner PID is only safe with one).</summary>
    internal static int RunningCount => s_sandboxed.Count;

    /// <summary>The game (the newest descendant of bwrap): its PID outside and inside the sandbox; null when not found.</summary>
    internal static (int Outer, int Inner)? GameProcess(int bwrapPid)
    {
        var found = Descendants(bwrapPid).LastOrDefault();
        if (found == 0) return null;
        try
        {
            foreach (var line in File.ReadLines($"/proc/{found}/status"))
            {
                if (!line.StartsWith("NSpid:", StringComparison.Ordinal)) continue;
                var ids = line["NSpid:".Length..].Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                return (found, int.Parse(ids[^1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
        return (found, found);
    }

    private static IEnumerable<int> Descendants(int pid)
    {
        var queue = new Queue<int>([pid]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            string children;
            try { children = File.ReadAllText($"/proc/{current}/task/{current}/children"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(child, out var id)) continue;
                yield return id;
                queue.Enqueue(id);
            }
        }
    }
}
