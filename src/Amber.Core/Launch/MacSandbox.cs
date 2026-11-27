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
        {
            // sandbox-exec deny rules always win: a later allow cannot punch a hole through a denied parent.
            // Java normally lives below Amber's hidden data folder, so exclude its path and the parent directories
            // Java must traverse from the deny itself.
            AppendDeny(sb, "file-read*", path, grants.ReadOnly.Concat(grants.ReadWrite));
            AppendDeny(sb, "file-write*", path, grants.ReadWrite);
        }
        // The keychain is reached through these services, not only through files.
        sb.AppendLine("(deny mach-lookup (global-name \"com.apple.SecurityServer\") (global-name \"com.apple.securityd.xpc\") (global-name \"com.apple.security.agent\"))");
        return sb.ToString();
    }

    private static void AppendDeny(StringBuilder sb, string operation, string hidden, IEnumerable<string> grants)
    {
        var inside = grants.Where(p => GameSandbox.IsInside(p, hidden) && !GameSandbox.SamePath(p, hidden)).ToList();
        if (inside.Count == 0)
        {
            sb.AppendLine($"(deny {operation} (subpath {Quote(hidden)}))");
            return;
        }

        var filters = new List<string>
        {
            $"(subpath {Quote(hidden)})",
            $"(require-not (literal {Quote(hidden)}))",
        };
        foreach (var grant in inside)
        {
            for (var parent = Path.GetDirectoryName(grant); parent is not null && !GameSandbox.SamePath(parent, hidden); parent = Path.GetDirectoryName(parent))
            {
                if (!GameSandbox.IsInside(parent, hidden)) break;
                filters.Add($"(require-not (literal {Quote(parent)}))");
            }
            filters.Add($"(require-not ({(File.Exists(grant) ? "literal" : "subpath")} {Quote(grant)}))");
        }
        sb.AppendLine($"(deny {operation} (require-all {string.Join(' ', filters.Distinct(StringComparer.Ordinal))}))");
    }

    /// <summary>sandbox-exec's arguments (without the executable) to run the command under the profile.</summary>
    public static List<string> Arguments(string profile, IReadOnlyList<string> command) => ["-p", profile, .. command];

    /// <summary>Why the sandbox cannot be used here, or null.</summary>
    public static string? Problem() =>
        File.Exists(Executable) ? null : Localization.L.T("core.launch.sandbox.mac.missing");

    /// <summary>An SBPL string literal.</summary>
    internal static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
