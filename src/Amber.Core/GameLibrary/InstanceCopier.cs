using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;

namespace Amber.Core.GameLibrary;

/// <summary>
/// "Copy an existing instance" in the new-instance panel (official design §6.11): copies the version folder under a new
/// name, renames the version files and the id inside the JSON. Instances with their own folder bring their mods,
/// settings and, when asked, their worlds; shared-folder instances only get a second version entry.
/// </summary>
public static class InstanceCopier
{
    /// <summary>Folders that belong to one run or are rebuilt on start, never copied.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "natives", "logs", "crash-reports", TrashService.TrashFolder,
    };

    public static void Copy(GameDirectory dir, string sourceId, string newId, bool includeSaves, CancellationToken ct = default)
    {
        var source = dir.VersionDir(sourceId);
        var target = dir.VersionDir(newId);
        if (!File.Exists(dir.VersionJsonPath(sourceId))) throw new FileNotFoundException(null, dir.VersionJsonPath(sourceId));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException(newId);

        // The marker goes in first: a copy cut short (crash, power loss) shows as "install not finished", not as a working instance.
        dir.MarkInstallStarted(newId);
        try
        {
            CopyTree(source, target, top: true, includeSaves, ct);
            var oldJson = Path.Combine(target, sourceId + ".json");
            var json = JsonNode.Parse(File.ReadAllText(oldJson)) as JsonObject ?? throw new InvalidDataException(oldJson);
            json["id"] = newId;
            File.WriteAllText(dir.VersionJsonPath(newId), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Delete(oldJson);
            var oldJar = Path.Combine(target, sourceId + ".jar");
            if (File.Exists(oldJar)) File.Move(oldJar, dir.VersionJarPath(newId));
            dir.MarkInstallFinished(newId);
        }
        catch
        {
            try { Directory.Delete(target, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void CopyTree(string from, string to, bool top, bool includeSaves, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            // Links are left out: following one could copy a whole drive, keeping it would share files between the two instances.
            if (entry.LinkTarget is not null) continue;
            if (top && (Skipped.Contains(entry.Name) || entry.Name == GameDirectory.IncompleteMarker || !includeSaves && entry.Name.Equals("saves", StringComparison.OrdinalIgnoreCase)))
                continue;
            var dest = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo sub) CopyTree(sub.FullName, dest, top: false, includeSaves, ct);
            else File.Copy(entry.FullName, dest);
        }
    }
}
