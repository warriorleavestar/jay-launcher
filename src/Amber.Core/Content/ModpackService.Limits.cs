using System.IO.Compression;
using Amber.Core.Localization;

namespace Amber.Core.Content;

/// <summary>
/// Bounds on unpacking a pack: the files a pack unpacks must fit on the drive, a pack can't hold an absurd number of
/// files, and no entry may write more than the size it declares. Legitimate packs and zipped game folders of many GB
/// still unpack; a damaged or crafted one is refused before it fills the disk.
/// </summary>
public sealed partial class ModpackService
{
    /// <summary>Far above any real pack or game folder (a large one has tens of thousands of files).</summary>
    internal const int MaxEntries = 500_000;

    /// <summary>Refuses a set of entries that has too many files or doesn't fit in the free space where it goes.</summary>
    internal static void CheckRoom(IReadOnlyCollection<ZipArchiveEntry> entries, string targetDir)
    {
        if (entries.Count > MaxEntries) throw new InvalidDataException(L.F("core.content.modpack.toomanyfiles", entries.Count, MaxEntries));
        long total = 0;
        foreach (var entry in entries) total = entry.Length > long.MaxValue - total ? long.MaxValue : total + entry.Length;
        if (FreeSpace(targetDir) is { } free && total > free)
            throw new InvalidDataException(L.F("core.content.modpack.nospace", Fmt.Size(total), Fmt.Size(free)));
    }

    /// <summary>Free bytes on the drive that holds <paramref name="dir"/> (or its nearest existing parent), or null when unknown.</summary>
    internal static long? FreeSpace(string dir)
    {
        try
        {
            var path = Path.GetFullPath(dir);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
            // Windows names a drive by its root; elsewhere any path names the file system it is on.
            var drive = OperatingSystem.IsWindows() ? Path.GetPathRoot(path) : path;
            return string.IsNullOrEmpty(drive) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Like <see cref="ZipFileExtensions.ExtractToFile(ZipArchiveEntry, string, bool)"/>, but an entry that holds more than its declared size is refused.</summary>
    internal static void ExtractLimited(ZipArchiveEntry entry, string path)
    {
        using (var source = entry.Open())
        using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var left = entry.Length;
            int read;
            while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left + 1))) > 0)
            {
                if (read > left) throw new InvalidDataException(L.F("core.content.modpack.badentry", entry.FullName));
                target.Write(buffer, 0, read);
                left -= read;
            }
        }
        try { File.SetLastWriteTime(path, entry.LastWriteTime.DateTime); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
    }
}
