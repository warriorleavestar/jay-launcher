using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Bake;

/// <summary>
/// A usable universal loader (experimental): the folder of a loader build that runs <c>amber.ul.Bake</c>.
/// Today the loader has no packaged layout yet, so this is a build folder with <c>build/loader</c> (classes),
/// <c>build/loader-cp.txt</c> (its libraries, relative to the folder) and the bridge jars next to the classes.
/// Mapping tables are no longer read from the loader folder: Jay downloads them from <c>requirements</c>.
/// </summary>
public sealed record BakeToolkit(string Root, IReadOnlyList<string> Classpath)
{
    public const string RootVariable = "AMBER_BAKE_LOADER";

    /// <summary>Returns null when the folder is not a loader build.</summary>
    public static BakeToolkit? Find(string? configured)
    {
        var root = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(RootVariable) : configured;
        if (string.IsNullOrWhiteSpace(root)) return null;
        root = Path.GetFullPath(root.Trim());
        var classes = Path.Combine(root, "build", "loader");
        var list = Path.Combine(root, "build", "loader-cp.txt");
        if (!Directory.Exists(classes) || !File.Exists(list)) return null;
        var entries = File.ReadAllText(list).Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => Path.GetFullPath(Path.Combine(root, e)));
        return new BakeToolkit(root, [classes, .. entries]);
    }
}

/// <summary>What <c>amber.ul.Bake version</c> reported.</summary>
public sealed record BakeLoaderInfo(string LoaderVersion, int Protocol, Version Interface, IReadOnlyDictionary<string, IReadOnlyList<string>> Platforms)
{
    /// <summary>The cache protocol this Jay understands. A different protocol means a different cache layout.</summary>
    public const int SupportedProtocol = 1;

    /// <summary>Interface 1.1 brought platforms; 1.2 brought warm and gc.</summary>
    public static readonly Version MinimumInterface = new(1, 1);
    public static readonly Version WarmInterface = new(1, 2);

    public bool CanWarm => Interface >= WarmInterface;

    internal static BakeLoaderInfo Parse(JsonObject result)
    {
        var platforms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (result["platforms"] is JsonObject list)
        {
            foreach (var (kind, versions) in list)
                platforms[kind] = versions is JsonArray a ? a.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList() : [];
        }
        else if (result["supportedMc"] is JsonArray mcs)
        {
            // An interface 1.0 loader only knew the vanilla base.
            platforms[BakePlatform.Vanilla] = mcs.Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList();
        }
        var text = result["interface"]?.GetValue<string>();
        var version = Version.TryParse(text, out var parsed) ? parsed : new Version(1, 0);
        return new BakeLoaderInfo(result["loaderVersion"]?.GetValue<string>() ?? "", result["protocol"]?.GetValue<int>() ?? 0, version, platforms);
    }

    /// <summary>Throws a localized <see cref="BakeException"/> when Jay cannot talk to this loader.</summary>
    public void EnsureCompatible()
    {
        if (Protocol != SupportedProtocol)
            throw new BakeException(L.F("core.bake.protocol_mismatch", Protocol, SupportedProtocol, LoaderVersion), [], "protocol_mismatch");
        if (Interface < MinimumInterface)
            throw new BakeException(L.F("core.bake.interface_old", Interface.ToString(2), MinimumInterface.ToString(2), LoaderVersion), [], "protocol_mismatch");
    }
}

/// <summary>A progress or problem line from the loader.</summary>
public sealed record BakeEvent(string Stage, string Message, bool IsProblem, bool IsError, string? Code = null);

/// <summary>The result of bake-instance: launch with this instead of the vanilla main class and class path.</summary>
public sealed record BakedLaunch(string MainClass, IReadOnlyList<string> Classpath, IReadOnlyList<string> JvmArgs, IReadOnlyList<string> GameArgsPrefix, string StateFile, string StateKey);

/// <summary>A bake failed. The message is localized; Problems are the loader's problem lines, Code the loader's error code.</summary>
public sealed class BakeException(string message, IReadOnlyList<string> problems, string? code = null) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
    public string? Code { get; } = code;
}

/// <summary>What <see cref="BakeService.PrepareAsync"/> did. Launch is null when the instance is not baked.</summary>
public sealed record BakePreparation(BakedLaunch? Launch, string Reason, bool Reused, BakeWarmStats? Warm);

/// <summary>warm's statistics.</summary>
public sealed record BakeWarmStats(int Hits, int Misses, int CacheFiles, long Milliseconds);

/// <summary>gc's answer. On a dry run Removed is 0.</summary>
public sealed record BakeGcResult(bool Applied, int LiveStates, int Removable, long RemovableBytes, int Removed, int Failed);

/// <summary>Runs one loader stage as a process and hands over its output lines. Tests replace it with fixtures.</summary>
public interface IBakeProcess
{
    /// <summary>Returns the exit code. Cancelling must stop the process.</summary>
    Task<int> RunAsync(string javaPath, IReadOnlyList<string> classpath, string stage, string? requestFile, Action<string> stdout, Action<string> stderr, CancellationToken ct);
}

/// <summary>The real loader: <c>java -cp &lt;loader&gt; amber.ul.Bake &lt;stage&gt; --request &lt;file&gt;</c>, killed on cancel.</summary>
public sealed class JavaBakeProcess : IBakeProcess
{
    public static readonly JavaBakeProcess Instance = new();

    public async Task<int> RunAsync(string javaPath, IReadOnlyList<string> classpath, string stage, string? requestFile, Action<string> stdout, Action<string> stderr, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
        psi.ArgumentList.Add("-Dstdout.encoding=UTF-8");
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(Path.PathSeparator, classpath.Select(p => AsciiPaths.MapArgument(p))));
        psi.ArgumentList.Add("amber.ul.Bake");
        psi.ArgumentList.Add(stage);
        if (requestFile is not null)
        {
            psi.ArgumentList.Add("--request");
            psi.ArgumentList.Add(AsciiPaths.MapArgument(requestFile));
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.launch.game.javastartfailed"));
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr(e.Data); };
        process.BeginErrorReadLine();
        using var cancel = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line) stdout(line);
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}

/// <summary>
/// Experimental "baking" through the universal loader, interface v1.2 (docs: amber-bake-interface.md in the loader).
/// Jay owns the downloads (mapping tables from <c>requirements</c>, the NeoForge/Forge installer), the instance's
/// bake record and cleanup; the loader owns remapping, Mixin, the platform bridges and its cache layout.
/// Each stage is one Java process; cancelling kills it.
/// </summary>
/// <param name="cacheRoot">The loader's cache root, shared by every instance (baked jars, nested jars).</param>
/// <param name="dataRoot">Jay's own bake files: downloaded mapping tables, the mapping cache, platform installs, the list of states.</param>
/// <param name="download">How to download files (Jay's downloader with mirrors). Null: downloads are refused.</param>
public sealed class BakeService(BakeToolkit toolkit, string cacheRoot, string dataRoot,
    Func<IReadOnlyCollection<DownloadItem>, CancellationToken, Task>? download = null, IBakeProcess? process = null)
{
    public const string StateFolder = ".amber-ul";

    /// <summary>
    /// The loader is built for Java 21, and a baked game runs on the loader's own launcher class, so both the bake
    /// and the baked launch need at least this Java even when the version asks for less (Forge 1.20.1 asks for 17).
    /// </summary>
    public const int MinJavaMajor = 21;

    /// <summary>The Java major version to bake and launch with for a version that asks for <paramref name="versionMajor"/>.</summary>
    public static int JavaMajorFor(int versionMajor) => Math.Max(versionMajor, MinJavaMajor);
    public BakeToolkit Toolkit => toolkit;
    public string CacheRoot => cacheRoot;
    public string DataRoot => dataRoot;
    public string MappingCache => Path.Combine(dataRoot, "mapcache");
    public string MappingsDir(string mcVersion) => Path.Combine(dataRoot, "mappings", GameDirectory.CheckId(mcVersion));
    private string StatesFile => Path.Combine(dataRoot, "states.txt");
    private readonly IBakeProcess _process = process ?? JavaBakeProcess.Instance;

    /// <summary>Whether this instance can be baked by this loader. reason is localized when not.</summary>
    public static bool Supports(GameInstance instance, BakeLoaderInfo loader, out string reason)
    {
        if (BakePlatform.KindOf(instance) is not { } kind)
        {
            reason = L.F("core.bake.unsupported_loader", instance.Loader ?? "");
            return false;
        }
        var versions = loader.Platforms.GetValueOrDefault(kind) ?? [];
        if (!versions.Contains(instance.McVersion))
        {
            reason = L.F("core.bake.unsupported_version", instance.McVersion, BakePlatform.DisplayName(kind),
                versions.Count == 0 ? "-" : string.Join(L.T("core.mp.format.listseparator"), versions));
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// The whole preparation for one instance: check the loader (protocol and interface), the instance's platform and
    /// version, reuse the previous bake when its inputs are unchanged, otherwise download what <c>requirements</c>
    /// lists, prepare the NeoForge/Forge install and run <c>bake-instance</c>; then <c>warm</c> when asked and the
    /// loader has it. Launch is null when the instance cannot be baked (Reason says why).
    /// </summary>
    public async Task<BakePreparation> PrepareAsync(string javaPath, int javaMajor, GameDirectory dir, GameInstance instance, bool warm,
        IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var loader = await VersionAsync(javaPath, ct);
        loader.EnsureCompatible();
        if (!Supports(instance, loader, out var reason)) return new BakePreparation(null, reason, false, null);
        var kind = BakePlatform.KindOf(instance)!;
        if (kind == BakePlatform.Forge) BakePlatform.DisableForgeUpdateCheck(instance.WorkDir);
        if (Current(dir, instance, javaMajor) is { } current) return new BakePreparation(current, L.T("core.bake.reused"), true, null);
        await EnsureRequirementsAsync(javaPath, dir, instance, progress, ct);
        var installDir = await BakePlatform.PrepareAsync(dir, instance, dataRoot, download, progress, ct);
        progress?.Report(new BakeEvent("bake-instance", L.F("core.bake.instance_progress", BakePlatform.DisplayName(kind)), false, false));
        var launch = await BakeInstanceAsync(javaPath, javaMajor, dir, instance, installDir, progress, ct);
        if (!warm || !loader.CanWarm) return new BakePreparation(launch, L.T("core.bake.done"), false, null);
        progress?.Report(new BakeEvent("warm", L.T("core.bake.warming"), false, false));
        var stats = await WarmAsync(javaPath, launch.StateFile, progress, ct);
        return new BakePreparation(launch, L.F("core.bake.done_warm", stats.CacheFiles, stats.Hits + stats.Misses), false, stats);
    }

    /// <summary><c>version</c>: what the loader supports. Does not check compatibility; see <see cref="BakeLoaderInfo.EnsureCompatible"/>.</summary>
    public async Task<BakeLoaderInfo> VersionAsync(string javaPath, CancellationToken ct) =>
        BakeLoaderInfo.Parse(await RunAsync(javaPath, "version", null, null, ct));

    /// <summary>The vanilla game jar and libraries (no loader libraries: the universal loader replaces Fabric / Quilt / FML boot).</summary>
    public static (string GameJar, List<string> Libraries) GameInputs(GameDirectory dir, GameInstance instance)
    {
        var vanilla = dir.ResolveVersion(instance.McVersion);
        var libraries = LibraryResolver.Resolve(vanilla, dir, RuleEvaluator.Current).Where(l => l.OnClasspath).Select(l => l.LocalPath).ToList();
        return (dir.VersionJarPath(vanilla.Jar ?? instance.McVersion), libraries);
    }

    /// <summary>Enabled mod jars, sorted by file name so requests are stable.</summary>
    public static List<string> ModJars(GameInstance instance) =>
        !Directory.Exists(instance.ModsDir) ? [] :
        Directory.EnumerateFiles(instance.ModsDir, "*.jar").Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// <c>requirements</c>: asks which official tables this game version and platform need and downloads them into
    /// <c>&lt;data&gt;/mappings/&lt;mc&gt;/</c>. Loader libraries are not downloaded: a loader build brings its own.
    /// Returns the mappings directory.
    /// </summary>
    public async Task<string> EnsureRequirementsAsync(string javaPath, GameDirectory dir, GameInstance instance, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var kind = BakePlatform.KindOf(instance) ?? BakePlatform.Vanilla;
        var request = new JsonObject
        {
            ["schema"] = "amber-ul/requirements-request@1",
            ["mcVersion"] = instance.McVersion,
            ["side"] = "client",
            ["platform"] = kind,
        };
        var versionJson = dir.VersionJsonPath(instance.McVersion);
        if (File.Exists(versionJson)) request["versionJson"] = versionJson;
        var result = await RunAsync(javaPath, "requirements", request, progress, ct);
        var items = new List<DownloadItem>();
        foreach (var file in result["files"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (file["kind"]?.GetValue<string>() != "mappings") continue;
            var relative = file["path"]?.GetValue<string>() ?? "";
            var url = file["url"]?.GetValue<string>() ?? "";
            if (relative.Length == 0 || url.Length == 0) throw new InvalidDataException(L.T("core.bake.bad_result"));
            // The path comes from the loader: it must stay inside the mappings folder.
            var target = GameDirectory.Contained(Path.Combine(dataRoot, "mappings"), relative.StartsWith("mappings/", StringComparison.Ordinal) ? relative["mappings/".Length..] : relative);
            items.Add(new DownloadItem(url, target, file["sha1"]?.GetValue<string>(), file["size"]?.GetValue<long>(), Within: dataRoot));
        }
        var missing = items.Where(i => !File.Exists(i.LocalPath) || (i.Size is { } size && new FileInfo(i.LocalPath).Length != size)).ToList();
        if (missing.Count > 0)
        {
            if (download is null) throw new BakeException(L.F("core.bake.download_unavailable", Path.GetFileName(missing[0].LocalPath)), []);
            progress?.Report(new BakeEvent("requirements", L.F("core.bake.downloading_mappings", missing.Count), false, false));
            await download(missing, ct);
        }
        return MappingsDir(instance.McVersion);
    }

    /// <summary>Stage 1, <c>bake-mod</c>: one mod at a time (vanilla/Fabric base only). Content-addressed in the loader's cache.</summary>
    public async Task<int> BakeModsAsync(string javaPath, GameDirectory dir, GameInstance instance, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var mods = ModJars(instance);
        for (var i = 0; i < mods.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BakeEvent("bake-mod", L.F("core.bake.mod_progress", i + 1, mods.Count, Path.GetFileName(mods[i])), false, false));
            var request = new JsonObject
            {
                ["schema"] = "amber-ul/bake-mod-request@1",
                ["mcVersion"] = instance.McVersion,
                ["modJar"] = mods[i],
                ["cacheRoot"] = cacheRoot,
                ["mappingsDir"] = MappingsDir(instance.McVersion),
                ["mappingCache"] = MappingCache,
                ["gameJar"] = gameJar,
                ["libraries"] = new JsonArray([.. libraries.Select(l => (JsonNode)l)]),
            };
            await RunAsync(javaPath, "bake-mod", request, progress, ct);
        }
        return mods.Count;
    }

    /// <summary>
    /// Stage 2, <c>bake-instance</c>: resolves the mod set, bakes the game, the platform and the mods, writes
    /// <c>&lt;instance&gt;/.amber-ul/state.json</c>. <paramref name="installDir"/> is the prepared NeoForge/Forge install (null for vanilla).
    /// </summary>
    public async Task<BakedLaunch> BakeInstanceAsync(string javaPath, int javaMajor, GameDirectory dir, GameInstance instance, string? installDir,
        IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var request = InstanceRequest(dir, instance, javaMajor, installDir);
        var result = await RunAsync(javaPath, "bake-instance", request, progress, ct);
        var launch = ParseLaunch(result);
        BakeRecord.Write(instance.WorkDir, new BakeRecord(InputKey(dir, instance, javaMajor), launch));
        Remember(launch.StateFile);
        return launch;
    }

    internal JsonObject InstanceRequest(GameDirectory dir, GameInstance instance, int javaMajor, string? installDir)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var kind = BakePlatform.KindOf(instance) ?? BakePlatform.Vanilla;
        var request = new JsonObject
        {
            ["schema"] = "amber-ul/bake-instance-request@1",
            ["mcVersion"] = instance.McVersion,
            ["side"] = "client",
            ["gameJar"] = gameJar,
            ["libraries"] = new JsonArray([.. libraries.Select(l => (JsonNode)l)]),
            ["libRoot"] = dir.LibrariesDir,
            ["mods"] = new JsonArray([.. ModJars(instance).Select(m => (JsonNode)new JsonObject { ["path"] = m })]),
            ["javaMajor"] = javaMajor,
            ["instanceDir"] = instance.WorkDir,
            ["gameDir"] = instance.WorkDir,
            ["cacheRoot"] = cacheRoot,
            ["mappingsDir"] = MappingsDir(instance.McVersion),
            ["mappingCache"] = MappingCache,
        };
        if (kind != BakePlatform.Vanilla)
        {
            if (installDir is null) throw new ArgumentNullException(nameof(installDir));
            request["platform"] = new JsonObject { ["kind"] = kind, ["installDir"] = installDir };
        }
        return request;
    }

    /// <summary>Stage 3, <c>warm</c> (interface 1.2): builds or verifies every cache product a launch needs, no game window.</summary>
    public async Task<BakeWarmStats> WarmAsync(string javaPath, string stateFile, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        var result = await RunAsync(javaPath, "warm", new JsonObject { ["schema"] = "amber-ul/warm-request@1", ["stateFile"] = stateFile }, progress, ct);
        var stats = result["stats"] as JsonObject;
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        return new BakeWarmStats(Int(stats?["bakeHits"]), Int(stats?["bakeMisses"]), Int(stats?["cacheFiles"]),
            stats?["ms"] is JsonValue ms && ms.TryGetValue<long>(out var t) ? t : 0);
    }

    /// <summary>
    /// <c>gc</c> (interface 1.2) over the shared cache root and mapping cache. The live set is every state file Jay has
    /// baked that still exists; a state without <c>cacheFiles</c> is warmed first, and one that cannot be warmed loses
    /// its bake record (it is baked again on its next launch) so nothing it used is kept by mistake.
    /// </summary>
    public async Task<BakeGcResult?> CollectGarbageAsync(string javaPath, bool apply, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(cacheRoot) || !new[] { "baked", "nested", "mappings" }.Any(d => Directory.Exists(Path.Combine(cacheRoot, d))))
            return null;
        var live = new List<string>();
        var dropped = new List<string>();
        foreach (var state in KnownStates())
        {
            if (!HasCacheFiles(state))
            {
                try { await WarmAsync(javaPath, state, progress, ct); }
                catch (BakeException)
                {
                    // The record no longer matches anything the loader can account for: bake again next time.
                    try { File.Delete(Path.Combine(Path.GetDirectoryName(state)!, BakeRecord.FileName)); } catch (IOException) { }
                    dropped.Add(state);
                    continue;
                }
                if (!HasCacheFiles(state)) { dropped.Add(state); continue; }
            }
            live.Add(state);
        }
        var request = new JsonObject
        {
            ["schema"] = "amber-ul/gc-request@1",
            ["cacheRoot"] = cacheRoot,
            ["liveStateFiles"] = new JsonArray([.. live.Select(s => (JsonNode)s)]),
            ["apply"] = apply,
            ["allowNoLiveStates"] = live.Count == 0,
        };
        if (Directory.Exists(MappingCache)) request["mappingCache"] = MappingCache;
        var result = await RunAsync(javaPath, "gc", request, progress, ct);
        if (dropped.Count > 0) Forget(dropped);
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : n is JsonArray a ? a.Count : 0;
        return new BakeGcResult(result["apply"]?.GetValue<bool>() ?? apply, Int(result["liveStates"]), Int(result["removable"]),
            result["removableBytes"] is JsonValue b && b.TryGetValue<long>(out var bytes) ? bytes : 0, Int(result["removed"]), Int(result["failed"]));
    }

    internal static bool HasCacheFiles(string stateFile)
    {
        try { return JsonNode.Parse(File.ReadAllText(stateFile)) is JsonObject o && o["cacheFiles"] is JsonArray; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The state files Jay has baked that still exist (instances deleted since then drop out).</summary>
    public IReadOnlyList<string> KnownStates()
    {
        if (!File.Exists(StatesFile)) return [];
        return File.ReadAllLines(StatesFile).Select(l => l.Trim()).Where(l => l.Length > 0 && File.Exists(l))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    private void Remember(string stateFile)
    {
        if (stateFile.Length == 0) return;
        var full = Path.GetFullPath(stateFile);
        var known = KnownStates().ToList();
        if (known.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) return;
        known.Add(full);
        WriteStates(known);
    }

    /// <summary>Drops states that no longer take part in cleanup (their instance bakes again next time).</summary>
    private void Forget(IReadOnlyList<string> dropped)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        WriteStates(KnownStates().Where(s => !dropped.Contains(s, comparer)).ToList());
    }

    private void WriteStates(IEnumerable<string> states)
    {
        Directory.CreateDirectory(dataRoot);
        var temp = StatesFile + ".tmp";
        File.WriteAllLines(temp, states, new UTF8Encoding(false));
        File.Move(temp, StatesFile, true);
    }

    internal static BakedLaunch ParseLaunch(JsonObject result)
    {
        var launch = result["launch"]?.AsObject() ?? throw new InvalidDataException(L.T("core.bake.bad_result"));
        static List<string> Strings(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
        return new BakedLaunch(
            launch["mainClass"]?.GetValue<string>() ?? throw new InvalidDataException(L.T("core.bake.bad_result")),
            Strings(launch["classpath"]),
            Strings(launch["jvmArgs"]),
            Strings(launch["gameArgsPrefix"]),
            launch["stateFile"]?.GetValue<string>() ?? "",
            result["stateKey"]?.GetValue<string>() ?? "");
    }

    /// <summary>
    /// The instance's bake input fingerprint: game version, platform and its version, game jar and libraries, enabled
    /// mods, Java major version, the loader build (by content) and the downloaded mapping tables. Game files use size
    /// and modification time; the loader's own files use content, so a rebuild in the same folder invalidates.
    /// </summary>
    public string InputKey(GameDirectory dir, GameInstance instance, int javaMajor)
    {
        var (gameJar, libraries) = GameInputs(dir, instance);
        var text = new StringBuilder("bake-input-v3|").Append(instance.McVersion).Append('|').Append(javaMajor).Append('|').Append(toolkit.Root)
            .Append("|platform:").Append(BakePlatform.KindOf(instance)).Append(':').Append(instance.Loader).Append(':').Append(instance.LoaderVersion);
        AppendStamp(text, dir.VersionJsonPath(instance.Id));
        AppendStamp(text, gameJar);
        foreach (var library in libraries) AppendStamp(text, library);
        foreach (var mod in ModJars(instance)) AppendStamp(text, mod);
        // Class path order matters: same-named classes and resources load in this order.
        foreach (var entry in toolkit.Classpath)
        {
            text.Append("|classpath:").Append(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    AppendContent(text, file);
            }
            else AppendContent(text, entry);
        }
        // The bridge jars sit next to the classes folder (the loader finds them as siblings).
        var build = Path.Combine(toolkit.Root, "build");
        if (Directory.Exists(build))
            foreach (var bridge in Directory.EnumerateFiles(build, "*.jar").Order(StringComparer.Ordinal)) AppendContent(text, bridge);
        var mappings = MappingsDir(instance.McVersion);
        if (Directory.Exists(mappings))
            foreach (var file in Directory.EnumerateFiles(mappings).Order(StringComparer.Ordinal)) AppendStamp(text, file);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void AppendStamp(StringBuilder text, string file) => text.Append('|').Append(file).Append(':').Append(Stamp(file));

    private static void AppendContent(StringBuilder text, string file)
    {
        text.Append('|').Append(file).Append(':');
        if (!File.Exists(file))
        {
            text.Append("missing");
            return;
        }
        using var input = File.OpenRead(file);
        text.Append(Convert.ToHexStringLower(SHA256.HashData(input)));
    }

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    /// <summary>
    /// The previous bake when its inputs are unchanged, its state file is there and every cache file the state lists
    /// still exists (gc or a deleted cache folder make it stale). Null otherwise.
    /// </summary>
    public BakedLaunch? Current(GameDirectory dir, GameInstance instance, int javaMajor)
    {
        var record = BakeRecord.Read(instance.WorkDir);
        if (record is null || !File.Exists(record.Launch.StateFile)) return null;
        if (record.Key != InputKey(dir, instance, javaMajor)) return null;
        return CacheFilesPresent(record.Launch.StateFile) ? record.Launch : null;
    }

    internal static bool CacheFilesPresent(string stateFile)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(stateFile)) is not JsonObject state || state["cacheFiles"] is not JsonArray files) return true;
            return files.All(f => f?.GetValue<string>() is { } path && (File.Exists(path) || Directory.Exists(path)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Runs one stage and returns its result object. stdout is one JSON object per line; only the last few stderr
    /// lines are kept for the error message. The request goes in a UTF-8 file, never on the command line.
    /// </summary>
    internal async Task<JsonObject> RunAsync(string javaPath, string stage, JsonObject? request, IProgress<BakeEvent>? progress, CancellationToken ct)
    {
        string? requestFile = null;
        if (request is not null)
        {
            Directory.CreateDirectory(cacheRoot);
            requestFile = Path.Combine(cacheRoot, $"request-{stage}-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(requestFile, MapPaths(request).ToJsonString(), new UTF8Encoding(false), ct);
        }

        var problems = new List<string>();
        var stderr = new Queue<string>();
        JsonObject? result = null;
        string? error = null, errorCode = null;
        int exit;
        try
        {
            exit = await _process.RunAsync(javaPath, toolkit.Classpath, stage, requestFile, line =>
            {
                var item = ParseLine(line);
                if (item is null) return;
                switch (Str(item, "type"))
                {
                    case "progress":
                        if (Str(item, "step") != "log")
                            progress?.Report(new BakeEvent(stage, Str(item, "message") ?? "", false, false));
                        break;
                    case "problem":
                        var text = DescribeProblem(item);
                        var isError = Str(item, "severity") == "error";
                        if (isError) problems.Add(text);
                        progress?.Report(new BakeEvent(stage, text, true, isError, Str(item, "code")));
                        break;
                    case "result":
                        result = item;
                        break;
                    case "error":
                        error = Str(item, "message");
                        errorCode = Str(item, "code");
                        break;
                }
            }, line =>
            {
                lock (stderr)
                {
                    stderr.Enqueue(line);
                    if (stderr.Count > 8) stderr.Dequeue();
                }
            }, ct);
        }
        finally
        {
            if (requestFile is not null) try { File.Delete(requestFile); } catch (IOException) { }
        }
        ct.ThrowIfCancellationRequested();
        if (exit == 0 && result is not null) return result;
        string tail;
        lock (stderr) tail = string.Join(" / ", stderr);
        if (error is null && tail.Contains("UnsupportedClassVersionError", StringComparison.Ordinal))
            throw new BakeException(L.F("core.bake.java_too_old", MinJavaMajor), problems, "java_too_old");
        if (errorCode == "unknown_stage")
            throw new BakeException(L.F("core.bake.stage_unsupported", stage), problems, errorCode);
        if (errorCode == "stale_state")
            throw new BakeException(L.T("core.bake.stale_state"), problems, errorCode);
        var reason = error ?? (problems.Count > 0 ? string.Join(L.T("core.mp.format.listseparator"), problems.Take(3)) : tail);
        if (reason.Length == 0) reason = L.F("core.bake.exit_code", exit);
        throw new BakeException(exit == 2 ? L.F("core.bake.dependency_failed", reason) : L.F("core.bake.failed", stage, reason), problems, errorCode);
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static JsonObject? ParseLine(string line)
    {
        if (!line.StartsWith('{')) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Problem codes Jay explains in its own words (the loader's message is English and stays in the log).
    /// Unknown codes fall back to the loader's message.
    /// </summary>
    private static string? ProblemKey(string code) => code switch
    {
        "missing_dep" => "core.bake.problem.missing_dep",
        "version_mismatch" => "core.bake.problem.version_mismatch",
        "incompatible_loader" => "core.bake.problem.incompatible_loader",
        "incompatible_mc" => "core.bake.problem.incompatible_mc",
        "conflict" => "core.bake.problem.conflict",
        "duplicate_mod" => "core.bake.problem.duplicate_mod",
        "nested_unresolved" => "core.bake.problem.nested_unresolved",
        "unsupported_feature" => "core.bake.problem.unsupported_feature",
        "mixin_incompatible" => "core.bake.problem.mixin_incompatible",
        _ => null,
    };

    internal static string DescribeProblem(JsonObject problem)
    {
        var mod = Str(problem, "modId") ?? Str(problem, "file") ?? "";
        var code = Str(problem, "code");
        var message = Str(problem, "message") ?? code ?? "";
        if (code is not null && ProblemKey(code) is { } key)
        {
            var dependency = problem["dependency"] as JsonObject;
            var depId = dependency is null ? null : Str(dependency, "id");
            var detail = depId is null ? "" : L.F("core.bake.problem.dependency", depId, Str(dependency!, "range") ?? "*",
                Str(dependency!, "found") is { } found ? L.F("core.bake.problem.found", found) : L.T("core.bake.problem.not_found"));
            message = L.T(key) + (detail.Length > 0 ? L.T("core.mp.format.listseparator") + detail : "");
        }
        return mod.Length > 0 ? $"{mod}: {message}" : message;
    }

    /// <summary>Paths in a request in the form Java can read (Chinese paths on a non-Chinese system go through an ASCII junction).</summary>
    private static JsonNode MapPaths(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : MapPaths(p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(n => n is null ? null : MapPaths(n))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(AsciiPaths.MapArgument(s)),
        _ => node.DeepClone(),
    };
}

/// <summary>&lt;instance&gt;/.amber-ul/jay-bake.json: the last bake's input key and launch block.</summary>
public sealed record BakeRecord(string Key, BakedLaunch Launch)
{
    public const string FileName = "jay-bake.json";

    public static string PathFor(string workDir) => Path.Combine(workDir, BakeService.StateFolder, FileName);

    public static void Write(string workDir, BakeRecord record)
    {
        var path = PathFor(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject
        {
            ["key"] = record.Key,
            ["mainClass"] = record.Launch.MainClass,
            ["classpath"] = new JsonArray([.. record.Launch.Classpath.Select(s => (JsonNode)s)]),
            ["jvmArgs"] = new JsonArray([.. record.Launch.JvmArgs.Select(s => (JsonNode)s)]),
            ["gameArgsPrefix"] = new JsonArray([.. record.Launch.GameArgsPrefix.Select(s => (JsonNode)s)]),
            ["stateFile"] = record.Launch.StateFile,
            ["stateKey"] = record.Launch.StateKey,
        };
        var temp = path + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public static BakeRecord? Read(string workDir)
    {
        var path = PathFor(workDir);
        if (!File.Exists(path)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            static List<string> Strings(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            var launch = new BakedLaunch(o["mainClass"]?.GetValue<string>() ?? "", Strings(o["classpath"]), Strings(o["jvmArgs"]),
                Strings(o["gameArgsPrefix"]), o["stateFile"]?.GetValue<string>() ?? "", o["stateKey"]?.GetValue<string>() ?? "");
            return launch.MainClass.Length == 0 ? null : new BakeRecord(o["key"]?.GetValue<string>() ?? "", launch);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }
}
