namespace Amber.Core.Content;

/// <summary>What a recently deleted thing was, from where it used to live.</summary>
public enum RecycledKind { Instance, World, Mod, ResourcePack, Shader, Screenshot, Datapack, Other }

/// <summary>One row of Settings › Storage › Recently deleted.</summary>
public sealed record RecycledItem(TrashEntry Entry, RecycledKind Kind, string? InstanceId)
{
    public string Name => Entry.Name;

    /// <summary>Whole days left before it is cleared for good (0 = goes at the next start).</summary>
    public int DaysLeft(int keepDays, DateTimeOffset now) =>
        Math.Max(0, (int)Math.Ceiling((Entry.DeletedAt + TimeSpan.FromDays(keepDays) - now).TotalDays));
}

/// <summary>
/// "Recently deleted" across every trash folder Jay uses (official design §6.20): the game directory's own and, when an
/// instance is on another volume, the one at that instance's root. Restores go through <see cref="TrashService"/>;
/// deleting for good only removes entry folders that <see cref="TrashService"/> made.
/// </summary>
public static class RecycleIndex
{
    /// <summary>Everything in the trash folders of the game directory and these instance folders, newest first.</summary>
    public static IReadOnlyList<RecycledItem> Collect(string gameRoot, IEnumerable<string> instanceDirs)
    {
        var roots = new List<string> { Path.Combine(gameRoot, TrashService.TrashFolder) };
        roots.AddRange(instanceDirs.Select(d => Path.Combine(d, TrashService.TrashFolder)));
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var items = new List<RecycledItem>();
        foreach (var root in roots)
        {
            if (!seen.Add(Path.GetFullPath(root))) continue;
            foreach (var entry in TrashService.List(root))
                items.Add(new RecycledItem(entry, KindOf(entry), InstanceOf(entry.OriginalPath, gameRoot)));
        }
        return [.. items.OrderByDescending(i => i.Entry.DeletedAt)];
    }

    /// <summary>The kind from the folder it was deleted from (mods → mod, saves → world, versions → instance…).</summary>
    public static RecycledKind KindOf(TrashEntry entry)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(entry.OriginalPath)) ?? "");
        return parent.ToLowerInvariant() switch
        {
            "versions" when entry.IsDirectory => RecycledKind.Instance,
            "saves" when entry.IsDirectory => RecycledKind.World,
            "mods" => RecycledKind.Mod,
            "resourcepacks" => RecycledKind.ResourcePack,
            "shaderpacks" => RecycledKind.Shader,
            "screenshots" => RecycledKind.Screenshot,
            "datapacks" => RecycledKind.Datapack,
            _ => RecycledKind.Other,
        };
    }

    /// <summary>The instance a deleted thing belonged to (versions/&lt;id&gt;/…), or the instance itself.</summary>
    public static string? InstanceOf(string originalPath, string gameRoot)
    {
        var versions = Path.Combine(Path.GetFullPath(gameRoot), "versions") + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(originalPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(versions, comparison)) return null;
        var rest = full[versions.Length..];
        var cut = rest.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return cut < 0 ? rest : rest[..cut];
    }

    /// <summary>
    /// Deletes one entry for good. Only an entry folder directly inside its trash folder, with its record, is removed;
    /// anything else is refused.
    /// </summary>
    public static void DeleteForever(TrashEntry entry)
    {
        var root = Path.GetFullPath(entry.TrashRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.GetFullPath(entry.EntryDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(dir), root, comparison) || !File.Exists(Path.Combine(dir, "entry.json"))
            || !string.Equals(Path.GetFileName(root), TrashService.TrashFolder, comparison))
            throw new TrashException(TrashFailure.EntryMissing, dir);
        // Directory.Delete only removes a junction or symbolic link itself, never what it points to.
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Empties everything listed; returns how many went. Locked files are skipped (tried again next time).</summary>
    public static int Empty(IEnumerable<RecycledItem> items)
    {
        var removed = 0;
        foreach (var item in items)
        {
            try
            {
                DeleteForever(item.Entry);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
