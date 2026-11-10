using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Amber.NetworkServer.Hardware;

/// <summary>
/// Checks that a hardware-bound node isn't being watched or tampered with before (and while) it holds its keys.
/// A failed check makes the node refuse to run; it never wipes anything, because these signals have innocent causes too
/// (a debugging admin, a system update). None of this can detect a full kernel-level compromise on its own; the TPM's
/// PCR 7 binding (<see cref="Tpm2Tools"/>) is what catches a changed boot chain.
/// </summary>
public interface IRuntimeIntegrity
{
    /// <summary>Reasons the node must not run, empty when everything is fine.</summary>
    IReadOnlyList<string> Problems(string dataDir, bool requireSecureBoot);
}

public sealed class RuntimeIntegrity : IRuntimeIntegrity
{
    public static readonly RuntimeIntegrity Instance = new();

    /// <summary>Environment variables that load foreign code into the process.</summary>
    internal static readonly string[] InjectionVariables =
        ["LD_PRELOAD", "LD_AUDIT", "DYLD_INSERT_LIBRARIES", "DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "COR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH"];

    public IReadOnlyList<string> Problems(string dataDir, bool requireSecureBoot)
    {
        var problems = new List<string>();
        if (IsBeingTraced()) problems.Add("a debugger or tracer is attached to this process");
        foreach (var name in InjectionVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value) && value != "0") problems.Add($"{name} is set (code injection)");
        }
        if (!OperatingSystem.IsWindows())
        {
            if (Environment.ProcessPath is { } exe)
            {
                CheckNotShared(exe, problems);
                if (Path.GetDirectoryName(exe) is { } bin) CheckNotShared(bin, problems);
            }
            CheckNotShared(dataDir, problems);
        }
        if (requireSecureBoot && SecureBootEnabled() != true) problems.Add("Secure Boot is off (the network's hardware certificate requires it)");
        return problems;
    }

    /// <summary>Whether the firmware booted with Secure Boot on. Null when it can't be told (no UEFI, not readable).</summary>
    public static bool? SecureBootEnabled()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const string var = "/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c";
                if (!File.Exists(var)) return null;
                var bytes = File.ReadAllBytes(var); // 4 bytes of attributes, then the value
                return bytes.Length >= 5 && bytes[4] == 1;
            }
            if (OperatingSystem.IsWindows())
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null) is int v ? v == 1 : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    private static bool IsBeingTraced()
    {
        if (Debugger.IsAttached) return true;
        if (OperatingSystem.IsWindows()) return IsDebuggerPresent();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/self/status"))
                    if (line.StartsWith("TracerPid:", StringComparison.Ordinal)) return line[10..].Trim() != "0";
            }
            catch (IOException) { }
        }
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    /// <summary>The program and the node's data must not be writable by other users, or anyone on the box could swap them.</summary>
    private static void CheckNotShared(string path, List<string> problems)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) problems.Add($"{path} is writable by other users");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
