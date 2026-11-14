using System.Diagnostics;
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
/// it, waiting only briefly for another process. The record is always replaced whole (written aside, then moved over),
/// so a check that only reads it takes no lock and never waits. A failure to record is reported to the caller: a save
/// then fails, and a launch does not start.
/// </para>
/// </summary>
internal static class InstanceSettingsTrust
{
    private static readonly AsyncLocal<string?> StoreOverride = new();
    private static readonly AsyncLocal<int?> MaxOverride = new();
    private static readonly Lock Gate = new();

    /// <summary>How long a save, or a load that adopts a new folder, waits for another Jay process holding the record.</summary>
    internal static readonly TimeSpan QuickWait = TimeSpan.FromSeconds(1);

    /// <summary>How long a launch waits for the record: it does not start without it.</summary>
    internal static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(5);

    /// <summary>The record file; tests point it elsewhere (for their own async flow only).</summary>
    internal static string? StorePathOverride { get => StoreOverride.Value; set => StoreOverride.Value = value; }

    /// <summary>How many records are kept before old ones that no instance uses any more are dropped.</summary>
    internal static int MaxTrusted { get => MaxOverride.Value ?? 4096; set => MaxOverride.Value = value; }

    internal static string StorePath => StorePathOverride ?? Path.Combine(
        Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(SpecialFolders.ApplicationData(), "Amber"),
        "instance-settings-trust.json");

    /// <summary>Jay's data folder, which holds the record (and nothing the game can write).</summary>
    internal static string DataFolder => Path.GetDirectoryName(StorePath)!;

    /// <summary>A short name for a folder, the same however its path is written (see <see cref="Key"/>).</summary>
    internal static string FolderKey(string folder) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key(folder))))[..32];

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
    internal static void EnsureAdopted(GameDirectory dir) => EnsureAdopted(dir, LaunchWait);

    internal static void EnsureAdopted(GameDirectory dir, TimeSpan wait)
    {
        // Already recorded: nothing to write, so no lock (a folder is never taken off the record).
        if (Read().Roots.Contains(Key(dir.Root))) return;
        Change(store => AdoptIfNew(dir, store), wait);
    }

    /// <summary>What a check of instance settings found.</summary>
    internal enum Verdict
    {
        /// <summary>Jay wrote these values, or there is no value that leaves the sandbox.</summary>
        Trusted,

        /// <summary>The record was read, and these values are not in it.</summary>
        NotRecorded,

        /// <summary>The record could not be read (or another Jay process held it): the values could not be checked.</summary>
        RecordUnavailable,
    }

    /// <summary>
    /// True when Jay wrote these values (or they have no value that leaves the sandbox). Fails closed: when the record
    /// cannot be read, values that leave the sandbox are not trusted.
    /// </summary>
    internal static bool IsTrusted(GameDirectory dir, InstanceSettings settings) => Check(dir, settings) == Verdict.Trusted;

    /// <summary>
    /// Checks the values against the record. For a folder already recorded this only reads the record and takes no
    /// lock; only the first sight of a folder writes (adopts it), and that waits at most <see cref="QuickWait"/>.
    /// Fails closed: anything short of a record that lists the values is not <see cref="Verdict.Trusted"/>.
    /// </summary>
    internal static Verdict Check(GameDirectory dir, InstanceSettings settings)
    {
        var print = Fingerprint(settings);
        try
        {
            var store = Read();
            if (!store.Roots.Contains(Key(dir.Root)))
            {
                // First sight of this folder: adopting it is a write. When the lock is busy the check fails closed (the
                // file is left as it is; the next load adopts it), rather than adopt without the record.
                Change(s => AdoptIfNew(dir, s), QuickWait, keepGoingIfUnwritable: true, then: s => store = s);
            }
            return print is null || store.Trusted.Contains(print) ? Verdict.Trusted : Verdict.NotRecorded;
        }
        catch (IOException)
        {
            return print is null ? Verdict.Trusted : Verdict.RecordUnavailable;
        }
    }

    /// <summary>
    /// Records the values Jay is about to save, then runs <paramref name="save"/> (which writes the instance file) under
    /// the same lock, so no other Jay process can trim the new record away before the file that uses it is written.
    /// <paramref name="save"/> is told which values the record lists. Throws <see cref="IOException"/> when the values
    /// cannot be recorded (or the record stays held by another Jay process past <see cref="QuickWait"/>);
    /// <paramref name="save"/> then does not run.
    /// </summary>
    internal static void Trust(GameDirectory dir, InstanceSettings settings, Action<Func<string, bool>>? save = null) => Change(store =>
    {
        var changed = AdoptIfNew(dir, store);
        if (Fingerprint(settings) is { } print)
        {
            // Add first: it may trim, and a trim sets what this folder uses from the files as they are now, before
            // the save. Noting the new values after it keeps them on that list, so a later trim keeps them while the
            // folder is away.
            changed |= Add(store, print);
            changed |= NoteUse(store, Key(dir.Root), print);
        }
        return changed;
    }, QuickWait, then: store => save?.Invoke(store.Trusted.Contains));

    /// <summary>
    /// Reads the record, applies <paramref name="change"/>, writes it back if it changed and runs <paramref name="then"/>,
    /// all under the lock, waiting at most <paramref name="wait"/> for it.
    /// </summary>
    private static void Change(Func<Store, bool> change, TimeSpan wait, bool keepGoingIfUnwritable = false, Action<Store>? then = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        if (!Gate.TryEnter(wait)) throw Busy(null);
        try
        {
            using var held = TakeLock(deadline);
            var store = Read();
            if (change(store))
            {
                try
                {
                    Write(store);
                }
                catch (IOException) when (keepGoingIfUnwritable)
                {
                    // Only a check: its answer stands for this read. Launching still needs the record (EnsureAdopted).
                }
            }
            then?.Invoke(store);
        }
        finally
        {
            Gate.Exit();
        }
    }

    /// <summary>The first time a game folder is seen: accept every instance's settings already in it.</summary>
    private static bool AdoptIfNew(GameDirectory dir, Store store)
    {
        var root = Key(dir.Root);
        if (store.Roots.Contains(root)) return false;
        // Every settings file must be read now: one skipped (locked, unreadable) would never be adopted later. The folder
        // is recorded only when all of them were, otherwise the whole adoption is tried again next time.
        var prints = PrintsIn(dir.Root, out var state, out var unreadable);
        if (state == FolderState.Unreadable)
            throw new IOException(L.F("core.launch.settings_file_unreadable", unreadable!.Message), unreadable);
        store.Roots.Add(root);
        foreach (var print in prints) Add(store, print);
        store.Uses[root] = [.. prints];
        return true;
    }

    private enum FolderState
    {
        Readable,

        /// <summary>The game folder or its versions folder is not there (a drive that is not connected, say).</summary>
        Missing,

        Unreadable,
    }

    /// <summary>
    /// The fingerprints of the instance settings in a game folder now. <paramref name="unreadable"/> is the first error
    /// when a folder or settings file that exists could not be read (a damaged file is not an error: it holds nothing).
    /// </summary>
    private static List<string> PrintsIn(string root, out FolderState state, out Exception? unreadable)
    {
        unreadable = null;
        state = FolderState.Readable;
        List<string> prints = [];
        var versions = Path.Combine(root, "versions");
        List<string> folders;
        try
        {
            if (!Directory.Exists(versions))
            {
                state = FolderState.Missing;
                return prints;
            }
            folders = [.. Directory.EnumerateDirectories(versions)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone while being listed (unplugged) or not readable: either way, what it uses is not known now.
            state = Directory.Exists(versions) ? FolderState.Unreadable : FolderState.Missing;
            unreadable = e;
            return prints;
        }
        foreach (var folder in folders)
        {
            var file = Path.Combine(folder, InstanceSettings.FileName);
            if (!File.Exists(file)) continue;
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable ??= e;
                state = FolderState.Unreadable;
                continue;
            }
            if (InstanceSettings.Parse(text) is { } existing && Fingerprint(existing) is { } print) prints.Add(print);
        }
        return prints;
    }

    /// <summary>Remembers that an instance in <paramref name="root"/> uses <paramref name="print"/> (see <see cref="Trim"/>).</summary>
    private static bool NoteUse(Store store, string root, string print)
    {
        if (!store.Uses.TryGetValue(root, out var uses)) store.Uses[root] = uses = new(StringComparer.Ordinal);
        return uses.Add(print);
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
    /// <para>
    /// A folder that is not there right now (a drive that is not connected) cannot say what it uses, and it will not be
    /// adopted again when it comes back. So the record remembers what each folder used when Jay last read it (or saved
    /// into it), and keeps those records while the folder is away. Those lists only shrink when Jay reads the folder
    /// again, so the record stays as large as what the folders used, not larger.
    /// </para>
    /// </summary>
    private static void Trim(Store store)
    {
        HashSet<string> inUse = new(StringComparer.Ordinal);
        foreach (var root in store.Roots)
        {
            var prints = PrintsIn(root, out var state, out _);
            if (state == FolderState.Readable)
            {
                // Only what the record lists matters here; values a game wrote are not worth remembering.
                var uses = new HashSet<string>(prints.Where(store.Trusted.Contains), StringComparer.Ordinal);
                store.Uses[root] = uses;
                inUse.UnionWith(uses);
            }
            else if (state == FolderState.Missing && store.Uses.TryGetValue(root, out var known))
            {
                inUse.UnionWith(known);
            }
            else
            {
                // Can't tell what that folder uses right now (unreadable, or away and recorded before Jay remembered
                // what folders use): drop nothing this time.
                return;
            }
        }
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

        /// <summary>What each game folder's instances used when Jay last read the folder or saved into it.</summary>
        public Dictionary<string, HashSet<string>> Uses { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Holds the lock file next to the record, waiting until <paramref name="deadline"/> for another Jay process to let go.</summary>
    private static FileStream TakeLock(long deadline)
    {
        var path = StorePath + ".lock";
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException e)
            {
                throw Unavailable(e);
            }
            catch (IOException e)
            {
                throw Busy(e);
            }
        }
    }

    /// <summary>
    /// The record as it is now. Needs no lock: <see cref="Write"/> replaces the file in one step, so this sees the
    /// record before or after a change, never half of one.
    /// </summary>
    private static Store Read()
    {
        var store = new Store();
        string text;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        while (true)
        {
            try
            {
                // Let a writer replace the file while it is open here (on Windows that needs FileShare.Delete).
                using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
                break;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Not there yet: folders are adopted as they are now.
                return store;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Stopwatch.GetTimestamp() < deadline)
            {
                // Windows can refuse an open for a moment while the file is being replaced.
                Thread.Sleep(5);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // There but unreadable: starting again would accept whatever the folders hold now, so stop instead.
                throw Unavailable(e);
            }
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                foreach (var r in json["roots"] as JsonArray ?? []) if (r?.GetValueKind() == JsonValueKind.String) store.Roots.Add(r.GetValue<string>());
                foreach (var t in json["trusted"] as JsonArray ?? []) if (t?.GetValueKind() == JsonValueKind.String) store.Trusted.Add(t.GetValue<string>());
                foreach (var (root, prints) in json["uses"] as JsonObject ?? [])
                {
                    if (prints is not JsonArray list) continue;
                    store.Uses[root] = new(list.Where(p => p?.GetValueKind() == JsonValueKind.String).Select(p => p!.GetValue<string>()), StringComparer.Ordinal);
                }
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
            var uses = new JsonObject();
            foreach (var root in store.Roots)
                if (store.Uses.TryGetValue(root, out var prints))
                    uses[root] = new JsonArray([.. prints.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p))]);
            var json = new JsonObject
            {
                ["version"] = 1,
                ["roots"] = new JsonArray([.. store.Roots.Select(r => (JsonNode?)JsonValue.Create(r))]),
                ["trusted"] = new JsonArray([.. store.Trusted.Select(t => (JsonNode?)JsonValue.Create(t))]),
                ["uses"] = uses,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Written aside and moved over the record in one step, so a read without the lock never sees half a file.
            var temp = StorePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, json.ToJsonString());
            MoveOver(temp, StorePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(e);
        }
    }

    /// <summary>
    /// Moves <paramref name="temp"/> over <paramref name="path"/>. Windows refuses to replace a file another thread or
    /// process has open, even one opened with FileShare.Delete, so a lock-free <see cref="Read"/> that is reading the
    /// record makes the move fail for a moment: try again for a short while, then give up and remove the temp file.
    /// </summary>
    internal static void MoveOver(string temp, string path)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (true)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(5);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    private static IOException Unavailable(Exception inner) =>
        new(L.F("core.launch.settings_record_unwritable", inner.Message), inner);

    private static IOException Busy(Exception? inner) => new(L.T("core.launch.settings_record_busy"), inner);
}
