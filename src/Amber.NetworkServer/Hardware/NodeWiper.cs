using System.Security.Cryptography;

namespace Amber.NetworkServer.Hardware;

/// <summary>
/// Destroys a hardware-bound node's own files after its hardware check failed: keys, certificates, accounts, bans,
/// admin token and EasyTier state. Only names on <see cref="Files"/> / <see cref="Folders"/> inside a data dir that
/// carries the <see cref="Marker"/> file are touched; symbolic links are removed, never followed.
/// </summary>
public static class NodeWiper
{
    /// <summary>Written into every node data dir. Without it nothing is wiped.</summary>
    public const string Marker = ".jay-node";

    /// <summary>What happened, written next to the marker (no secrets in it).</summary>
    public const string LogFile = "wiped.log";

    public static readonly string[] Files =
    [
        "directory.key", "directory.key.hw", "login.key", "login.key.hw", "directory.cert", "hardware.cert", "hardware.wrap",
        "hardware.request.json", "accounts.json", "bans.json", "admin.token", "directory-keys.json",
    ];

    public static readonly string[] Folders = ["public-node"];

    /// <summary>Marks a folder as a node data dir (so a later wipe is allowed there).</summary>
    public static void Mark(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var marker = Path.Combine(dataDir, Marker);
        if (!File.Exists(marker)) File.WriteAllText(marker, "Jay network node data. A failed hardware check wipes the Jay files in this folder.\n");
    }

    /// <summary>
    /// Overwrites and deletes the node's files. Returns what was (or, with <paramref name="dryRun"/>, would be) removed.
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="dataDir"/> isn't a safe, marked node folder.
    /// </summary>
    public static IReadOnlyList<string> Wipe(string dataDir, string reason, bool dryRun)
    {
        var dir = SafeDataDir(dataDir);
        var removed = new List<string>();
        foreach (var name in Files)
        {
            var path = Path.Combine(dir, name);
            if (Exists(path)) { removed.Add(path); if (!dryRun) DestroyFile(path); }
        }
        foreach (var name in Folders)
        {
            var path = Path.Combine(dir, name);
            if (Exists(path)) DestroyTree(path, removed, dryRun);
        }
        if (!dryRun)
            File.AppendAllText(Path.Combine(dir, LogFile), $"{DateTimeOffset.UtcNow:u} wiped {removed.Count} item(s): {reason}\n");
        return removed;
    }

    /// <summary>The full data dir path, after refusing anything that isn't clearly a node's own folder.</summary>
    internal static string SafeDataDir(string dataDir)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        var info = new DirectoryInfo(dir);
        if (!info.Exists) throw new InvalidOperationException($"{dir} doesn't exist");
        if (info.LinkTarget is not null) throw new InvalidOperationException($"{dir} is a link; refusing to wipe through it");
        if (Path.GetPathRoot(dir) is { } root && Path.TrimEndingDirectorySeparator(root) == dir) throw new InvalidOperationException($"{dir} is a drive or filesystem root");
        foreach (var special in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.System })
        {
            var s = Environment.GetFolderPath(special);
            if (s.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(s), dir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException($"{dir} is a system or home folder");
        }
        if (dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new InvalidOperationException($"{dir} is too close to the root");
        if (!File.Exists(Path.Combine(dir, Marker))) throw new InvalidOperationException($"{dir} has no {Marker} file, so it isn't a Jay node folder");
        return dir;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static void DestroyTree(string path, List<string> removed, bool dryRun)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || !info.Exists)
        {
            removed.Add(path);
            if (!dryRun) DestroyFile(path);
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo d && d.LinkTarget is null) DestroyTree(d.FullName, removed, dryRun);
            else { removed.Add(entry.FullName); if (!dryRun) DestroyFile(entry.FullName); }
        }
        removed.Add(path);
        if (!dryRun) info.Delete();
    }

    /// <summary>Overwrites a regular file with random bytes before deleting it; a link is just removed.</summary>
    private static void DestroyFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            if (Directory.Exists(path) && !File.Exists(path)) Directory.Delete(path); else info.Delete();
            return;
        }
        try
        {
            if (info.Exists && info.Length > 0)
            {
                if (info.IsReadOnly) info.IsReadOnly = false;
                using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var buffer = new byte[Math.Min(info.Length, 1 << 16)];
                for (long left = info.Length; left > 0; left -= buffer.Length)
                {
                    RandomNumberGenerator.Fill(buffer);
                    f.Write(buffer, 0, (int)Math.Min(buffer.Length, left));
                }
                f.Flush(true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still delete below */ }
        info.Delete();
    }
}
