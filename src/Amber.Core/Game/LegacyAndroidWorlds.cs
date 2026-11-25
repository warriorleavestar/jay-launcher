using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amber.Core.Game;

// Build 87 and earlier ran every Android instance in the shared game directory.
// Freeze the old list before the first new launch. Keep the originals usable in shared mode.
public static class LegacyAndroidWorlds
{
    public static void Preserve(GameDirectory game, string stateDir, IEnumerable<string> played,
        Func<string, bool> isolated, CancellationToken ct, bool planOnly = false)
    {
        var state = Path.Combine(stateDir, "android-world-upgrade-v1");
        Directory.CreateDirectory(state);
        var complete = Path.Combine(state, "complete");
        if (File.Exists(complete)) return;
        using var migrationLock = new FileStream(Path.Combine(state, "lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(complete)) return;
        var planFile = Path.Combine(state, "plan.json");
        var shared = Path.Combine(game.Root, "saves");
        if (!File.Exists(planFile))
        {
            var installed = game.ListInstalled().ToArray();
            var previous = played.Intersect(installed, StringComparer.Ordinal).ToArray();
            // Older profiles can predate launch history. Only instances already installed at
            // upgrade are candidates; a subsequently created instance starts with empty saves.
            var targets = (previous.Length > 0 ? previous : installed).Where(isolated).ToArray();
            var plan = new List<WorldImport>();
            if (Directory.Exists(shared))
            {
                foreach (var world in new DirectoryInfo(shared).EnumerateDirectories())
                {
                    if (!File.Exists(Path.Combine(world.FullName, "level.dat")) &&
                        !File.Exists(Path.Combine(world.FullName, "level.dat_old"))) continue;
                    foreach (var id in targets)
                    {
                        var saves = Path.Combine(game.VersionDir(id), "saves");
                        var name = world.Name;
                        for (int i = 1; Exists(Path.Combine(saves, name)) ||
                            plan.Any(p => p.Instance == id && p.Target == name); i++)
                            name = $"{world.Name} (legacy {i})";
                        plan.Add(new(id, world.Name, name, Guid.NewGuid().ToString("N")));
                    }
                }
            }
            File.WriteAllBytes(planFile + ".tmp", JsonSerializer.SerializeToUtf8Bytes(plan, WorldImportJson.Default.ListWorldImport));
            File.Move(planFile + ".tmp", planFile);
        }

        if (planOnly) return;
        var imports = JsonSerializer.Deserialize(File.ReadAllBytes(planFile), WorldImportJson.Default.ListWorldImport)
            ?? throw new InvalidDataException(planFile);
        var staging = Path.Combine(state, "staging");
        foreach (var item in imports)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(game.VersionJsonPath(item.Instance))) continue;
            var saves = Path.Combine(game.VersionDir(item.Instance), "saves");
            var target = Path.Combine(saves, item.Target);
            var marker = ".jay-import-" + item.Id;
            for (int i = 1; Exists(target) && !File.Exists(Path.Combine(target, marker)); i++)
                target = Path.Combine(saves, $"{item.Target} (legacy {i})");
            if (File.Exists(Path.Combine(target, marker))) continue;
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            try
            {
                if (new DirectoryInfo(saves).LinkTarget is not null ||
                    new DirectoryInfo(game.VersionDir(item.Instance)).LinkTarget is not null)
                    throw new IOException(saves);
                CopyWorld(new DirectoryInfo(Path.Combine(shared, item.Source)), staging, ct);
                File.WriteAllText(Path.Combine(staging, marker), "");
                Directory.CreateDirectory(saves);
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        File.WriteAllText(complete, "1");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CopyWorld(DirectoryInfo from, string to, CancellationToken ct)
    {
        if (from.LinkTarget is not null) throw new IOException(from.Name);
        Directory.CreateDirectory(to);
        foreach (var entry in from.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null) throw new IOException(entry.Name);
            var dest = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo child) CopyWorld(child, dest, ct);
            else File.Copy(entry.FullName, dest);
        }
    }
}

internal sealed record WorldImport(string Instance, string Source, string Target, string Id);
[JsonSerializable(typeof(List<WorldImport>))]
internal partial class WorldImportJson : JsonSerializerContext { }
