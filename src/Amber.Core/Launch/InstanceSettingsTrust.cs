using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;
using Amber.Core.Localization;

namespace Amber.Core.Launch;

/// <summary>
/// Which instance settings Jay itself wrote. <see cref="InstanceSettings.FileName"/> sits in the instance folder, and
/// the sandboxed game may write there; but five of its fields leave the sandbox: the commands Jay runs before launch
/// and after exit, the Java path, and the JVM and game arguments. Jay records a hash of those five values each time it
/// saves them, in its own data folder (which the game never sees), and ignores values it did not record.
/// <para>
/// The record is by value, not by path, so copying or renaming an instance keeps its settings. The first time Jay
/// sees a game folder (the first run after this check was added, or a newly added folder) it accepts the settings
/// already there, since they were written before any game ran under this check. Jay adopts a folder before it starts
/// any game from it (see <see cref="EnsureAdopted"/>), so nothing a game wrote can be adopted this way.
/// </para>
/// <para>
/// Several Jay processes (the app, the CLI) can share the record, so every read-change-write holds a lock file next to
/// it. A failure to record is reported to the caller: a save then fails, and a launch does not start.
/// </para>
/// </summary>
internal static class InstanceSettingsTrust
{
    private static readonly AsyncLocal<string?> StoreOverride = new();
    private static readonly AsyncLocal<int?> MaxOverride = new();
    private static readonly Lock Gate = new();

    /// <summary>The record file; tests point it elsewhere (for their own async flow only).</summary>
    internal static string? StorePathOverride { get => StoreOverride.Value; set => StoreOverride.Value = value; }

    /// <summary>How many records are kept before old ones that no instance uses any more are dropped.</summary>
    internal static int MaxTrusted { get => MaxOverride.Value ?? 4096; set => MaxOverride.Value = value; }

    internal static string StorePath => StorePathOverride ?? Path.Combine(
        Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(SpecialFolders.ApplicationData(), "Amber"),
        "instance-settings-trust.json");

    /// <summary>The hash of the fields that reach outside the sandbox, or null when all of them are empty.</summary>
    internal static string? Fingerprint(InstanceSettings s)
    {
        string?[] fields = [s.PreLaunchCommand, s.PostExitCommand, s.JavaPath, s.JvmArgs, s.GameArgs];
        if (fields.All(string.IsNullOrWhiteSpace)) return null;
        var text = string.Join('\0', fields.Select(f => string.IsNullOrWhiteSpace(f) ? "" : f));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("jay-instance-settings-v1\0" + text)));
    }

    /// <summary>
    /// Records the game folder as seen, accepting the settings already in it, unless that was done before. Called
    /// before any game starts from the folder; throws <see cref="IOException"/> when the record cannot be kept.
    /// </summary>
    internal static void EnsureAdopted(GameDirectory dir) => Change(store => AdoptIfNew(dir, store));

    /// <summary>
    /// True when Jay wrote these values (or they have no value that leaves the sandbox). Fails closed: when the record
    /// cannot be read, values that leave the sandbox are not trusted.
    /// </summary>
    internal static bool IsTrusted(GameDirectory dir, InstanceSettings settings)
    {
        var print = Fingerprint(settings);
        try
        {
            var trusted = false;
            Change(store =>
            {
                var changed = AdoptIfNew(dir, store);
                trusted = print is null || store.Trusted.Contains(print);
                return changed;
            }, keepGoingIfUnwritable: true);
            return trusted;
        }
        catch (IOException)
        {
            return print is null;
        }
    }

    /// <summary>Records the values Jay is about to save. Throws <see cref="IOException"/> when they cannot be recorded.</summary>
    internal static void Trust(GameDirectory dir, InstanceSettings settings) => Change(store =>
    {
        var changed = AdoptIfNew(dir, store);
        if (Fingerprint(settings) is { } print) changed |= Add(store, print);
        return changed;
    });

    /// <summary>Reads the record, applies <paramref name="change"/> and writes it back if it changed, all under the lock.</summary>
    private static void Change(Func<Store, bool> change, bool keepGoingIfUnwritable = false)
    {
        lock (Gate)
        {
            using var held = TakeLock();
            var store = Read();
            if (!change(store)) return;
            try
            {
                Write(store);
            }
            catch (IOException) when (keepGoingIfUnwritable)
            {
                // Only a check: its answer stands for this read. Launching still needs the record (EnsureAdopted).
            }
        }
    }

    /// <summary>The first time a game folder is seen: accept every instance's settings already in it.</summary>
    private static bool AdoptIfNew(GameDirectory dir, Store store)
    {
        var root = Key(dir.Root);
        if (store.Roots.Contains(root)) return false;
        store.Roots.Add(root);
        foreach (var print in PrintsIn(dir.Root)) Add(store, print);
        return true;
    }

    /// <summary>The fingerprints of the instance settings in a game folder now.</summary>
    private static IEnumerable<string> PrintsIn(string root)
    {
        var versions = Path.Combine(root, "versions");
        IEnumerable<string> folders;
        try
        {
            folders = Directory.Exists(versions) ? Directory.EnumerateDirectories(versions).ToList() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var folder in folders)
        {
            var file = Path.Combine(folder, InstanceSettings.FileName);
            if (File.Exists(file) && InstanceSettings.ReadFile(file) is { } existing && Fingerprint(existing) is { } print)
                yield return print;
        }
    }

    private static bool Add(Store store, string print)
    {
        var index = store.Trusted.IndexOf(print);
        if (index == store.Trusted.Count - 1 && index >= 0) return false;
        if (index >= 0) store.Trusted.RemoveAt(index);
        store.Trusted.Add(print);
        if (store.Trusted.Count > MaxTrusted) Trim(store);
        return true;
    }

    /// <summary>
    /// Over the limit: drop the oldest records that no instance in a known game folder uses now. A record still in use
    /// is kept even past the limit, so trimming never turns a player's own settings into unknown ones. Only records
    /// already in the list are kept this way; nothing new is trusted.
    /// </summary>
    private static void Trim(Store store)
    {
        var inUse = new HashSet<string>(store.Roots.SelectMany(PrintsIn), StringComparer.Ordinal);
        // The newest record is the one being added; it always stays.
        for (var i = 0; i < store.Trusted.Count - 1 && store.Trusted.Count > MaxTrusted;)
        {
            if (inUse.Contains(store.Trusted[i])) i++;
            else store.Trusted.RemoveAt(i);
        }
    }

    private static string Key(string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return OperatingSystem.IsLinux() ? full : full.ToLowerInvariant();
    }

    private sealed class Store
    {
        public List<string> Roots { get; } = [];
        public List<string> Trusted { get; } = [];
    }

    /// <summary>Holds the lock file next to the record, waiting a few seconds for another Jay process to let go.</summary>
    private static FileStream TakeLock()
    {
        var path = StorePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 200)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException e)
            {
                throw Unavailable(e);
            }
            catch (IOException e)
            {
                throw Unavailable(e);
            }
        }
    }

    private static Store Read()
    {
        var store = new Store();
        string text;
        try
        {
            text = File.ReadAllText(StorePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Not there yet: folders are adopted as they are now.
            return store;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // There but unreadable: starting again would accept whatever the folders hold now, so stop instead.
            throw Unavailable(e);
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                foreach (var r in json["roots"] as JsonArray ?? []) if (r?.GetValueKind() == JsonValueKind.String) store.Roots.Add(r.GetValue<string>());
                foreach (var t in json["trusted"] as JsonArray ?? []) if (t?.GetValueKind() == JsonValueKind.String) store.Trusted.Add(t.GetValue<string>());
            }
        }
        catch (JsonException)
        {
            // Damaged (only Jay writes this file, and only whole): start again, which is no worse than the first run.
        }
        return store;
    }

    private static void Write(Store store)
    {
        try
        {
            var json = new JsonObject
            {
                ["version"] = 1,
                ["roots"] = new JsonArray([.. store.Roots.Select(r => (JsonNode?)JsonValue.Create(r))]),
                ["trusted"] = new JsonArray([.. store.Trusted.Select(t => (JsonNode?)JsonValue.Create(t))]),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, json.ToJsonString());
            File.Move(temp, StorePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(e);
        }
    }

    private static IOException Unavailable(Exception inner) =>
        new(L.F("core.launch.settings_record_unwritable", inner.Message), inner);
}
