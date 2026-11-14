using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Game;

namespace Amber.Core.Launch;

/// <summary>
/// Which instance settings Jay itself wrote. <see cref="InstanceSettings.FileName"/> sits in the instance folder, and
/// the sandboxed game may write there; but five of its fields leave the sandbox: the commands Jay runs before launch
/// and after exit, the Java path, and the JVM and game arguments. Jay records a hash of those five values each time it
/// saves them, in its own data folder (which the game never sees), and ignores values it did not record.
/// <para>
/// The record is by value, not by path, so copying or renaming an instance keeps its settings. The first time Jay
/// sees a game folder (the first run after this check was added, or a newly added folder) it accepts the settings
/// already there, since they were written before any game ran under this check.
/// </para>
/// </summary>
internal static class InstanceSettingsTrust
{
    private const int MaxTrusted = 4096;
    private static readonly Lock Gate = new();

    /// <summary>The record file; tests point it elsewhere.</summary>
    internal static string? StorePathOverride { get; set; }

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

    /// <summary>True when Jay wrote these values (or they have no value that leaves the sandbox).</summary>
    internal static bool IsTrusted(GameDirectory dir, InstanceSettings settings)
    {
        if (Fingerprint(settings) is not { } print) return true;
        lock (Gate)
        {
            var store = Read();
            if (AdoptIfNew(dir, store)) Write(store);
            return store.Trusted.Contains(print);
        }
    }

    /// <summary>Records the values Jay is about to save.</summary>
    internal static void Trust(GameDirectory dir, InstanceSettings settings)
    {
        lock (Gate)
        {
            var store = Read();
            var changed = AdoptIfNew(dir, store);
            if (Fingerprint(settings) is { } print) changed |= Add(store, print);
            if (changed) Write(store);
        }
    }

    /// <summary>The first time a game folder is seen: accept every instance's settings already in it.</summary>
    private static bool AdoptIfNew(GameDirectory dir, Store store)
    {
        var root = Key(dir.Root);
        if (store.Roots.Contains(root)) return false;
        store.Roots.Add(root);
        if (Directory.Exists(dir.VersionsDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(dir.VersionsDir))
            {
                var file = Path.Combine(folder, InstanceSettings.FileName);
                if (!File.Exists(file)) continue;
                if (InstanceSettings.ReadFile(file) is { } existing && Fingerprint(existing) is { } print) Add(store, print);
            }
        }
        return true;
    }

    private static bool Add(Store store, string print)
    {
        var index = store.Trusted.IndexOf(print);
        if (index == store.Trusted.Count - 1 && index >= 0) return false;
        if (index >= 0) store.Trusted.RemoveAt(index);
        store.Trusted.Add(print);
        if (store.Trusted.Count > MaxTrusted) store.Trusted.RemoveRange(0, store.Trusted.Count - MaxTrusted);
        return true;
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

    private static Store Read()
    {
        var store = new Store();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(StorePath)) is JsonObject json)
            {
                foreach (var r in json["roots"] as JsonArray ?? []) if (r?.GetValueKind() == JsonValueKind.String) store.Roots.Add(r.GetValue<string>());
                foreach (var t in json["trusted"] as JsonArray ?? []) if (t?.GetValueKind() == JsonValueKind.String) store.Trusted.Add(t.GetValue<string>());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable: start again. Folders are then adopted again as they are now, which is no worse
            // than the first run.
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
            // Not recorded: the values are ignored at the next launch and the player sets them again.
        }
    }
}
