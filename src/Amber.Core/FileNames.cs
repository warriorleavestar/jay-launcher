using System.Globalization;

namespace Amber.Core;

/// <summary>
/// Turns a file name that came from someone else (a member, a mod list, a download API) into a bare name that is
/// safe on every platform: only the last path segment, no characters Windows refuses or treats specially
/// (':' would open an alternate data stream), no control or invisible format characters, no trailing dots or
/// spaces, and no Windows device names (CON, NUL, COM1, …), which get a leading underscore.
/// </summary>
public static class FileNames
{
    public const int MaxLength = 120;

    private static readonly HashSet<char> Refused =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    /// <summary>The safe bare name, or <paramref name="fallback"/> when nothing usable is left.</summary>
    public static string Bare(string? name, string fallback) => TryBare(name) ?? fallback;

    /// <summary>The safe bare name, or null when nothing usable is left.</summary>
    public static string? TryBare(string? name)
    {
        var last = (name ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var clean = new string([.. last.Where(c => !Refused.Contains(c) && !char.IsControl(c)
            && char.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim().TrimEnd('.', ' ');
        if (clean.Length > MaxLength) clean = clean[..MaxLength].TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") return null;
        return IsDeviceName(clean) ? "_" + clean : clean;
    }

    /// <summary>True for names Windows maps to a device whatever the extension ("con", "NUL.txt", "lpt9.jar").</summary>
    public static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
               && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
