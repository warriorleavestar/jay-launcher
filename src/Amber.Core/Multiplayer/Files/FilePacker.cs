using System.IO.Compression;
using Amber.Core.Launch;

namespace Amber.Core.Multiplayer.Files;

/// <summary>
/// Gets things ready to send (design: "Where to send from"): worlds and folders become one zip, a mod list is names and
/// versions only, and a log has the computer's user name masked. Everything is written to an outgoing folder that Jay
/// cleans up; the originals are never changed.
/// </summary>
public static class FilePacker
{
    /// <summary>A received mod list bigger than this is not read.</summary>
    public const long MaxModListBytes = 2 * 1024 * 1024;
    /// <summary>A log bigger than this is cut to its last part before it goes out.</summary>
    public const int MaxLogChars = 8 * 1024 * 1024;
    /// <summary>How many entries of a received zip are looked at when telling whether it holds a world.</summary>
    public const int MaxWorldEntries = 200_000;

    /// <summary>
    /// Zips <paramref name="folder"/> (with the folder itself as the top entry) into <paramref name="outDir"/>.
    /// A world's session.lock is left out: the game holds it open while it runs.
    /// </summary>
    public static string PackFolder(string folder, string outDir, CancellationToken ct = default)
    {
        var source = new DirectoryInfo(folder);
        if (!source.Exists) throw new DirectoryNotFoundException(folder);
        Directory.CreateDirectory(outDir);
        var target = UniqueFile(outDir, PrivateMessenger.SafeName(source.Name) + ".zip");
        try
        {
            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var file in source.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                if (file.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(source.FullName, file.FullName).Replace('\\', '/');
                zip.CreateEntryFromFile(file.FullName, source.Name + "/" + relative, CompressionLevel.Fastest);
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        return target;
    }

    /// <summary>Writes an instance's mod list (no mod files) as "&lt;title&gt; mods.jaymods".</summary>
    public static string WriteModList(ModManifest manifest, string title, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var target = UniqueFile(outDir, PrivateMessenger.SafeName(title + " mods") + FileKinds.ModListExtension);
        File.WriteAllText(target, manifest.ToJson());
        return target;
    }

    /// <summary>Reads a mod list someone sent; null when it is too big or not a mod list.</summary>
    public static ModManifest? ReadModList(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxModListBytes) return null;
            return ModManifest.FromJson(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Copies a log with the computer's user name and home folder masked (the same masking as an exported error report).
    /// A very long log keeps its last part, where the error usually is.
    /// </summary>
    public static string RedactLog(string path, string outDir, string? userName = null, string? homeDir = null)
    {
        Directory.CreateDirectory(outDir);
        var text = File.ReadAllText(path);
        if (text.Length > MaxLogChars) text = text[^MaxLogChars..];
        var clean = ErrorReport.Redact(text, userName ?? Environment.UserName, homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        var target = UniqueFile(outDir, PrivateMessenger.SafeName(name));
        File.WriteAllText(target, clean);
        return target;
    }

    /// <summary>True when some entry of the zip is a level.dat (the same rule <see cref="Game.WorldManager.Import"/> imports by).</summary>
    internal static bool HasWorld(ZipArchive zip) =>
        zip.Entries.Take(MaxWorldEntries).Any(e => e.FullName.Replace('\\', '/').Split('/').Last() == "level.dat");

    /// <summary>Removes packed files older than <paramref name="age"/> (left behind when Jay closed during a send).</summary>
    public static void CleanOld(string outDir, TimeSpan age, DateTime? now = null)
    {
        if (!Directory.Exists(outDir)) return;
        var cut = (now ?? DateTime.UtcNow) - age;
        foreach (var file in new DirectoryInfo(outDir).EnumerateFiles())
            if (file.LastWriteTimeUtc < cut) TryDelete(file.FullName);
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string UniqueFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
        return path;
    }

}
