namespace Amber.Core.Multiplayer;

/// <summary>Files friends send me: which ones are programs or scripts and get an extra warning before I accept them.</summary>
public static class ReceivedFiles
{
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".msi", ".msp", ".scr", ".pif", ".cpl",
        ".lnk", ".reg", ".dll", ".jar", ".sh", ".bash", ".command", ".app", ".pkg", ".dmg", ".run", ".bin", ".appimage", ".deb", ".rpm", ".py",
    };

    /// <summary>True for programs, installers and scripts (by extension; "a.txt.exe" counts, "exe.txt" does not).</summary>
    public static bool IsProgram(string fileName) =>
        ProgramExtensions.Contains(Path.GetExtension(fileName.TrimEnd(' ', '.')));
}
