using Amber.Core.Diagnostics;
using System.IO.Compression;

namespace Amber.Core.Multiplayer.Files;

/// <summary>
/// Gets things ready to send (design: "Where to send from"): worlds and folders become one zip, a mod list is names and
/// versions only, and a log is masked like everything shared. Everything is written to an outgoing folder that Jay
/// cleans up; the originals are never changed.
/// </summary>
public static class FilePacker
{
    /// <summary>A received mod list bigger than this is not read.</summary>
    public const long MaxModListBytes = 2 * 1024 * 1024;
    /// <summary>A log bigger than this is cut to its last part before it goes out; only that part is read.</summary>
    public const int MaxLogBytes = 8 * 1024 * 1024;
    /// <summary>A received zip with more entries than this is not looked into when telling whether it holds a world.</summary>
    public const int MaxWorldEntries = 200_000;
    /// <summary>Nor one whose list of entries (the zip's central directory) is bigger than this.</summary>
    public const long MaxWorldDirectoryBytes = 32L * 1024 * 1024;

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
    /// Copies a log masked the way everything shared is (<see cref="Redaction.ForSharing"/>: tokens, the user name and home
    /// folder, every Jay code, room secrets and public addresses). A very long log keeps its last part, where the error usually is.
    /// </summary>
    public static string RedactLog(string path, string outDir, string? userName = null, string? homeDir = null, IEnumerable<string>? secrets = null)
    {
        Directory.CreateDirectory(outDir);
        var text = ReadTail(path, MaxLogBytes);
        var clean = Redaction.ForSharing(text, secrets, userName, homeDir);
        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        var target = UniqueFile(outDir, PrivateMessenger.SafeName(name));
        File.WriteAllText(target, clean);
        return target;
    }

    /// <summary>
    /// The text of a file, or of its last <paramref name="maxBytes"/> bytes when it is longer (only those are read, so a
    /// huge log never sits in memory whole). A character cut in half at the start is dropped.
    /// </summary>
    internal static string ReadTail(string path, int maxBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        if (length <= maxBytes)
        {
            using var whole = new StreamReader(file, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return whole.ReadToEnd();
        }
        file.Seek(length - maxBytes, SeekOrigin.Begin);
        var bytes = new byte[maxBytes];
        file.ReadExactly(bytes);
        // Skip UTF-8 continuation bytes (10xxxxxx): the first whole character starts after them.
        var start = 0;
        while (start < bytes.Length && start < 4 && (bytes[start] & 0xC0) == 0x80) start++;
        return System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>
    /// True when some entry of the zip is a level.dat (the same rule <see cref="Game.WorldManager.Import"/> imports by).
    /// Reads the zip's list of entries one at a time and stops at the first level.dat; a zip over
    /// <see cref="MaxWorldEntries"/> or <see cref="MaxWorldDirectoryBytes"/> throws <see cref="InvalidDataException"/>
    /// before any entry is read.
    /// </summary>
    internal static bool HasWorld(string zipPath, CancellationToken ct = default)
    {
        var found = false;
        ZipDirectory.Scan(zipPath, MaxWorldEntries, MaxWorldDirectoryBytes, e => !(found = IsLevelDat(e.Name)), ct);
        return found;
    }

    internal static bool IsLevelDat(string entryName) => entryName.Replace('\\', '/').Split('/')[^1] == "level.dat";

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
