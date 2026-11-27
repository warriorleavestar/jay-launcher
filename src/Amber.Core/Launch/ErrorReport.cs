using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Amber.Core.Launch;

/// <summary>What goes into an exported error report.</summary>
public sealed record ErrorReportInput(
    string InstanceName,
    string McVersion,
    string? Loader,
    IReadOnlyList<string> Mods,
    IReadOnlyList<StoredLogLine> Log,
    string? JavaVersion,
    string? PlayerName,
    string? CrashReport = null);

/// <summary>
/// The error report someone can send to a friend or a mod author (official design §6.9): game and mod list, the last
/// <see cref="MaxLines"/> log lines, the system and Java version. Before anything is shown or saved it is cleaned:
/// tokens and session ids, e-mail addresses, the computer's user name (also inside paths) and account ids are replaced.
/// The player name stays, it is in every Minecraft log anyway.
/// </summary>
public static partial class ErrorReport
{
    public const int MaxLines = 2000;

    public static string Build(ErrorReportInput input, string? userName = null, string? homeDir = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Jay error report");
        sb.AppendLine($"Instance: {input.InstanceName}");
        sb.AppendLine($"Minecraft: {input.McVersion}{(input.Loader is null ? "" : " · " + input.Loader)}");
        sb.AppendLine($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        if (input.JavaVersion is not null) sb.AppendLine($"Java: {input.JavaVersion}");
        sb.AppendLine();
        sb.AppendLine($"## Mods ({input.Mods.Count})");
        foreach (var mod in input.Mods) sb.AppendLine("- " + mod);
        sb.AppendLine();
        var log = input.Log.Skip(Math.Max(0, input.Log.Count - MaxLines)).ToList();
        sb.AppendLine($"## Log (last {log.Count} lines)");
        foreach (var line in log)
            sb.AppendLine($"{line.Time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} {(line.FromJay ? "JAY" : line.Level.ToString().ToUpperInvariant())} {line.Message}");
        if (input.CrashReport is { Length: > 0 } crash)
        {
            sb.AppendLine();
            sb.AppendLine("## Crash report");
            sb.AppendLine(crash);
        }
        return Redact(sb.ToString(), userName ?? Environment.UserName, homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Removes secrets and the computer user name. Safe to run more than once.</summary>
    public static string Redact(string text, string? userName, string? homeDir)
    {
        // Launch arguments and session lines: --accessToken xxx, --uuid, --xuid, --clientId, "token:…:…".
        text = ArgSecret().Replace(text, m => m.Groups[1].Value + " <redacted>");
        text = SessionToken().Replace(text, "token:<redacted>");
        text = Jwt().Replace(text, "<redacted-token>");
        text = Bearer().Replace(text, "Bearer <redacted>");
        text = Email().Replace(text, "<email>");
        if (!string.IsNullOrEmpty(homeDir) && homeDir.Length > 3)
        {
            text = text.Replace(homeDir, "~", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(homeDir.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
        {
            // Also paths that don't start at the home folder (C:\Users\name, /home/name, /Users/name).
            text = Regex.Replace(text, @"(?i)([\\/](?:users|home)[\\/])" + Regex.Escape(userName) + @"(?=[\\/]|\b)", "$1<user>");
            // The 8.3 short form Windows uses for a long or spaced profile folder (TMP=C:\Users\ALICES~1\…).
            if (ShortNamePattern(userName) is { } shortName)
                text = Regex.Replace(text, @"(?i)([\\/](?:users|home)[\\/])" + shortName + @"~\d{1,6}(?=[\\/]|\b)", "$1<user>");
            // A folder named after the account right under a drive or a mount point (D:\name\…, /media/name/…,
            // /run/media/name/…, /mnt/name/…, /Volumes/name/…). Only whole path segments: ordinary words stay readable.
            text = Regex.Replace(text, @"(?i)((?:\b[a-z]:[\\/]|/run/media/|/media/|/mnt/|/Volumes/))" + Regex.Escape(userName) + @"(?=[\\/])", "$1<user>");
        }
        return text;
    }

    /// <summary>
    /// A pattern for the part before "~N" of the 8.3 short name Windows makes for a folder called <paramref name="name"/>:
    /// spaces and dots dropped, at most six characters. A character 8.3 can't hold becomes "_" or a code page
    /// character, so it matches any one character there. Null when nothing is left.
    /// </summary>
    internal static string? ShortNamePattern(string name)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var c in name)
        {
            if (count == 6) break;
            if (c is ' ' or '.') continue;
            sb.Append(c is > (char)0x20 and < (char)0x7F && "\"*+,/:;<=>?[\\]|".IndexOf(c) < 0 ? Regex.Escape(c.ToString()) : @"[^\\/~\s]");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    [GeneratedRegex(@"(--(?:accessToken|session|uuid|xuid|clientId|userProperties))\s+(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ArgSecret();

    [GeneratedRegex(@"token:[A-Za-z0-9._\-]{8,}(?::[0-9a-fA-F\-]{8,})?")]
    private static partial Regex SessionToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{5,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._\-~+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
