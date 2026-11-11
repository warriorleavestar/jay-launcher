using System.Globalization;
using System.Text;

namespace Amber.Core.Diagnostics;

/// <summary>
/// Text from the network (a server's MOTD and version name, a peer's host name) made safe to print to a terminal:
/// control characters (ESC, BEL, CR, LF, …) and the bidirectional overrides become visible <c>\uXXXX</c> escapes, so a
/// server can't move the cursor, rewrite earlier lines, set the clipboard (OSC 52) or fake the CLI's own output lines.
/// </summary>
public static class TerminalText
{
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!text.Any(NeedsEscape)) return text;
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (NeedsEscape(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool NeedsEscape(char c) =>
        char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '‎' or '‏' or '؜';
}
