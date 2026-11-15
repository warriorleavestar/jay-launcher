using System.IO.Compression;

namespace Amber.Core.Content;

/// <summary>What a file dragged into the window is (official design §6.25).</summary>
public enum DropKind { Modpack, Mod, ResourcePack, Shader, Datapack, World, Theme, Picture, Unknown,
    /// <summary>Dropped on a friend's conversation: the files are sent to them, whatever they are.</summary>
    Send }

/// <summary>Files of one kind dragged in together: they are handled together.</summary>
public sealed record DropGroup(DropKind Kind, IReadOnlyList<string> Paths);

/// <summary>
/// Recognises dragged files. A <c>.zip</c> is judged by what is inside it, not its name: <c>modrinth.index.json</c> or a
/// CurseForge <c>manifest.json</c> is a modpack, <c>level.dat</c> a world, <c>shaders/</c> a shader pack, and
/// <c>pack.mcmeta</c> a datapack (with <c>data/</c>) or a resource pack.
/// </summary>
public static class DropClassifier
{
    /// <summary>Zips with more entries than this are judged by their first entries only (a huge world is still a world).</summary>
    private const int MaxEntries = 20000;

    public static DropKind Classify(string path)
    {
        try
        {
            if (!File.Exists(path)) return DropKind.Unknown;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".mrpack": return DropKind.Modpack;
                case ".jar": return DropKind.Mod;
                case ".jaytheme": return DropKind.Theme;
                // A picture goes into a slot of a theme made from pictures (design doc "Image themes").
                case ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif": return DropKind.Picture;
                case ".zip": break;
                default: return DropKind.Unknown;
            }
            using var zip = ZipFile.OpenRead(path);
            return ClassifyZip(zip);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return DropKind.Unknown;
        }
    }

    internal static DropKind ClassifyZip(ZipArchive zip)
    {
        var names = zip.Entries.Take(MaxEntries).Select(e => e.FullName.Replace('\\', '/').TrimStart('/')).ToList();
        if (names.Contains("modrinth.index.json", StringComparer.OrdinalIgnoreCase)) return DropKind.Modpack;
        if (zip.GetEntry("manifest.json") is { } manifest && IsCurseForgeManifest(manifest)) return DropKind.Modpack;
        // A world is level.dat at the top or inside one folder (most world zips hold the world folder).
        if (names.Any(n => IsAtDepth(n, "level.dat", 1))) return DropKind.World;
        if (names.Any(n => StartsWithAtDepth(n, "shaders/", 1))) return DropKind.Shader;
        if (names.Contains("pack.mcmeta", StringComparer.OrdinalIgnoreCase))
            return names.Any(n => n.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) ? DropKind.Datapack : DropKind.ResourcePack;
        return DropKind.Unknown;
    }

    private static bool IsCurseForgeManifest(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 * 1024 * 1024) return false;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd();
        return text.Contains("\"manifestType\"", StringComparison.Ordinal) && text.Contains("\"minecraft\"", StringComparison.Ordinal);
    }

    /// <summary><paramref name="name"/> is <paramref name="file"/> at the top or under at most <paramref name="depth"/> folders.</summary>
    private static bool IsAtDepth(string name, string file, int depth)
    {
        var parts = name.Split('/');
        return parts.Length <= depth + 1 && string.Equals(parts[^1], file, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithAtDepth(string name, string folder, int depth)
    {
        if (name.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        var parts = name.Split('/');
        for (var i = 1; i <= depth && i < parts.Length; i++)
            if (string.Join('/', parts.Skip(i)).StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Same kinds together, in the order they were first seen.</summary>
    public static IReadOnlyList<DropGroup> Group(IEnumerable<string> paths)
    {
        var groups = new List<(DropKind Kind, List<string> Paths)>();
        foreach (var path in paths)
        {
            var kind = Classify(path);
            var index = groups.FindIndex(g => g.Kind == kind);
            if (index < 0) groups.Add((kind, [path]));
            else groups[index].Paths.Add(path);
        }
        return [.. groups.Select(g => new DropGroup(g.Kind, g.Paths))];
    }

    /// <summary>
    /// Copies a dropped file into a content folder without replacing anything there: "x.jar" becomes "x (2).jar" when
    /// taken. Returns the new path.
    /// </summary>
    public static string CopyInto(string file, string directory)
    {
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var target = Path.Combine(directory, name + ext);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(directory, $"{name} ({n}){ext}");
        File.Copy(file, target, overwrite: false);
        return target;
    }
}
