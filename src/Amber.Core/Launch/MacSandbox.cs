using System.Text;

namespace Amber.Core.Launch;

/// <summary>
/// The macOS game sandbox: Java runs under sandbox-exec with a Jay-written profile. The profile starts from
/// <c>(allow default)</c> (a deny-by-default profile would need every framework path the JVM, LWJGL and the GPU
/// driver touch, which differ per macOS version) and then denies what the game must never reach: Jay's data folder,
/// the keychains and the keychain service, and browser profiles. Grants inside a denied folder are allowed again
/// after it (sandbox rules: the last match wins). Memory of other processes is already closed to an unsigned JVM
/// (task_for_pid needs an entitlement), and sandbox-exec execs Java in place, so the PID stays the game's.
/// </summary>
public static class MacSandbox
{
    public const string Executable = "/usr/bin/sandbox-exec";

    /// <summary>Folders under the home folder that hold browser profiles and other apps' secrets.</summary>
    internal static readonly string[] PrivateHomeFolders =
    [
        "Library/Keychains",
        "Library/Cookies",
        "Library/Safari",
        "Library/Application Support/Google/Chrome",
        "Library/Application Support/Microsoft Edge",
        "Library/Application Support/BraveSoftware",
        "Library/Application Support/Firefox",
        "Library/Application Support/com.apple.TCC",
        "Library/Messages",
        "Library/Mail",
        ".ssh",
        ".gnupg",
    ];

    /// <summary>The profile text (SBPL). Pure, so it is unit-tested.</summary>
    public static string Profile(SandboxGrants grants, string home)
    {
        grants = grants.Normalized();
        var sb = new StringBuilder();
        sb.AppendLine("(version 1)");
        sb.AppendLine("(allow default)");
        var denied = grants.Hidden.Concat(PrivateHomeFolders.Select(f => Path.Combine(home, f))).ToList();
        foreach (var path in denied)
            sb.AppendLine($"(deny file-read* file-write* (subpath {Quote(path)}))");
        // The keychain is reached through these services, not only through files.
        sb.AppendLine("(deny mach-lookup (global-name \"com.apple.SecurityServer\") (global-name \"com.apple.securityd.xpc\") (global-name \"com.apple.security.agent\"))");
        // Grants inside a denied folder (one file Jay hands over, a cache next to the data folder) are allowed again.
        foreach (var path in grants.ReadOnly)
            if (denied.Any(d => GameSandbox.IsInside(path, d)))
                sb.AppendLine($"(allow file-read* ({(File.Exists(path) ? "literal" : "subpath")} {Quote(path)}))");
        foreach (var path in grants.ReadWrite)
            if (denied.Any(d => GameSandbox.IsInside(path, d)))
                sb.AppendLine($"(allow file-read* file-write* ({(File.Exists(path) ? "literal" : "subpath")} {Quote(path)}))");
        return sb.ToString();
    }

    /// <summary>sandbox-exec's arguments (without the executable) to run the command under the profile.</summary>
    public static List<string> Arguments(string profile, IReadOnlyList<string> command) => ["-p", profile, .. command];

    /// <summary>Why the sandbox cannot be used here, or null.</summary>
    public static string? Problem() =>
        File.Exists(Executable) ? null : Localization.L.T("core.launch.sandbox.mac.missing");

    /// <summary>An SBPL string literal.</summary>
    internal static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
