using System.Globalization;
using System.IO.Compression;

namespace Amber.Core.Game;

/// <summary>One backup zip of a world in the instance's <c>backups</c> folder.</summary>
public sealed record WorldBackup(string Path, string WorldFolder, DateTimeOffset TakenAt, long SizeBytes);

/// <summary>
/// World backups (official design §6.6): make one (cancellable, no half-written zip left behind), list a world's
/// backups newest first, and restore one. Restoring first backs up the world as it is now, then swaps the folder, so
/// a restore can always be undone by restoring that automatic backup.
/// </summary>
public static class WorldBackups
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public static string Dir(string workDir) => Path.Combine(workDir, "backups");

    /// <summary>Backups of one world (by folder name), newest first. Zips that don't follow the naming are skipped.</summary>
    public static IReadOnlyList<WorldBackup> List(string workDir, string worldFolder)
    {
        var dir = Dir(workDir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<WorldBackup>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length <= StampFormat.Length + 1 || name[^(StampFormat.Length + 1)] != '-') continue;
            var folder = name[..^(StampFormat.Length + 1)];
            if (!string.Equals(folder, worldFolder, StringComparison.Ordinal)) continue;
            if (!DateTime.TryParseExact(name[^StampFormat.Length..], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var taken)) continue;
            result.Add(new WorldBackup(file, folder, new DateTimeOffset(taken), new FileInfo(file).Length));
        }
        return [.. result.OrderByDescending(b => b.TakenAt)];
    }

    /// <summary>
    /// Zips the world into <c>backups/{folder}-{time}.zip</c>. Written to a temporary name first, so a cancel or a
    /// full disk never leaves a broken backup in the list. <c>session.lock</c> (held by a running game) is skipped.
    /// </summary>
    public static string Create(WorldInfo world, string workDir, IProgress<double>? progress = null, CancellationToken ct = default, DateTime? now = null)
    {
        var dir = Dir(workDir);
        Directory.CreateDirectory(dir);
        var stamp = (now ?? DateTime.Now).ToString(StampFormat, CultureInfo.InvariantCulture);
        var target = Path.Combine(dir, $"{world.FolderName}-{stamp}.zip");
        for (var i = 2; File.Exists(target); i++)
        {
            // Two backups in the same second: bump the time instead of overwriting (keeps the name parseable).
            stamp = (now ?? DateTime.Now).AddSeconds(i - 1).ToString(StampFormat, CultureInfo.InvariantCulture);
            target = Path.Combine(dir, $"{world.FolderName}-{stamp}.zip");
        }
        var partial = target + ".partial";
        try
        {
            var files = Directory.EnumerateFiles(world.Path, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "session.lock").ToList();
            var total = Math.Max(1L, files.Sum(f => new FileInfo(f).Length));
            long done = 0;
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(world.FolderName + "/" + Path.GetRelativePath(world.Path, file).Replace('\\', '/'), CompressionLevel.Fastest);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var output = entry.Open();
                    input.CopyTo(output);
                    done += input.Length;
                    progress?.Report((double)done / total);
                }
            }
            File.Move(partial, target);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Replaces the world with this backup. The current world is backed up first (returned as <c>Safety</c>), the backup
    /// is unpacked next to it and checked, and only then is the old folder swapped out. Paths inside the zip are checked
    /// like an imported world, so a crafted zip can't write outside the world folder.
    /// </summary>
    public static (string? Safety, string World) Restore(WorldBackup backup, string savesDir, string workDir, CancellationToken ct = default) =>
        Restore(backup, savesDir, workDir, Directory.Move, ct);

    /// <param name="move">Moves a folder (tests make it fail).</param>
    internal static (string? Safety, string World) Restore(WorldBackup backup, string savesDir, string workDir, Action<string, string> move, CancellationToken ct = default)
    {
        WorldManager.CheckWorldComponent(backup.WorldFolder);
        var target = GameDirectory.Contained(savesDir, backup.WorldFolder);
        string? safety = null;
        if (Directory.Exists(target))
        {
            var current = new WorldInfo(backup.WorldFolder, target, backup.WorldFolder, "", null, null);
            safety = Create(current, workDir, ct: ct);
        }
        Directory.CreateDirectory(savesDir);
        var staging = Path.Combine(savesDir, ".jay-restore-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var zip = ZipFile.OpenRead(backup.Path))
            {
                var entries = zip.Entries.Select(e => (Entry: e, Name: WorldManager.ImportPath(e.FullName))).ToList();
                var level = entries.Where(e => e.Name.Split('/').Last() == "level.dat").OrderBy(e => e.Name.Length).FirstOrDefault();
                if (level.Entry is null) throw new InvalidDataException("level.dat");
                var prefix = level.Name[..^"level.dat".Length];
                foreach (var (entry, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
                    var path = GameDirectory.Contained(staging, name[prefix.Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, true);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                var old = Path.Combine(savesDir, ".jay-old-" + Guid.NewGuid().ToString("N")[..8]);
                move(target, old);
                try { move(staging, target); }
                catch
                {
                    // Put the current world back rather than leave the save without it.
                    try { move(old, target); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                TryDeleteDir(old);
            }
            else move(staging, target);
            return (safety, target);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
