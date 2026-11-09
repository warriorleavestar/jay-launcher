using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Auth;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>
/// Experimental baking against the universal loader's interface v1.2. No real loader: a fake process answers each
/// stage with JSON lines, the way amber.ul.Bake does.
/// </summary>
public sealed class BakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-bake-" + Guid.NewGuid().ToString("N")[..8]);

    public BakeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // Remove the platform installs' library links first, so only the links go, never their targets.
            var platforms = Path.Combine(Data, "platforms");
            if (Directory.Exists(platforms))
                foreach (var install in Directory.GetDirectories(platforms))
                    if (new DirectoryInfo(Path.Combine(install, "libraries")) is { LinkTarget: not null } link) link.Delete();
            Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Game => Path.Combine(_root, "game");
    private string Data => Path.Combine(_root, "bake-data");
    private string Cache => Path.Combine(_root, "bake-cache");

    private void Version(string id, string? inherits = null, string libraries = "[]", string extra = "")
    {
        var dir = Directory.CreateDirectory(Path.Combine(Game, "versions", id)).FullName;
        var parent = inherits is null ? "" : $"\"inheritsFrom\":\"{inherits}\",";
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$$"""{"id":"{{{id}}}",{{{parent}}}{{{extra}}}"type":"release","mainClass":"net.minecraft.client.main.Main","libraries":{{{libraries}}},"arguments":{"game":["--username","${auth_player_name}"],"jvm":["-cp","${classpath}"]}}""");
        File.WriteAllText(Path.Combine(dir, id + ".jar"), "jar");
    }

    private BakeToolkit Toolkit()
    {
        var root = Path.Combine(_root, "loader");
        Directory.CreateDirectory(Path.Combine(root, "build", "loader"));
        File.WriteAllText(Path.Combine(root, "build", "loader-cp.txt"), "libs/a.jar;libs/b.jar\n");
        return BakeToolkit.Find(root)!;
    }

    private BakeService Service(FakeLoader? loader = null, List<DownloadItem>? downloads = null) =>
        new(Toolkit(), Cache, Data, (items, _) =>
        {
            downloads?.AddRange(items);
            foreach (var item in items)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                if (FakeFiles.TryGetValue(item.Url, out var source)) File.Copy(source, item.LocalPath, true);
                else File.WriteAllText(item.LocalPath, new string('x', (int)(item.Size ?? 1)));
            }
            return Task.CompletedTask;
        }, loader ?? new FakeLoader());

    /// <summary>Downloads served from local files (the fake installer).</summary>
    private Dictionary<string, string> FakeFiles { get; } = [];

    private GameInstance Instance(string loader = "fabric", string mc = "1.21.1", string? loaderVersion = "0.16", string id = "1.21.1")
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work-" + id)).FullName;
        return new GameInstance(id, mc, loader == "vanilla" ? null : loader, loader == "vanilla" ? null : loaderVersion, work);
    }

    private const string Version12 = """{"type":"result","schema":"amber-ul/version-result@1","loaderVersion":"0.3.0-proto","pipelineVersion":"ul-proto-9","protocol":1,"interface":"1.2","supportedMc":["1.20.1","1.21.1"],"platforms":{"vanilla":["1.21.1"],"neoforge":["1.21.1"],"forge":["1.20.1"]},"mappingCache":{"composer":"amber-map-1","keyFormat":"amber-tablekey/1"}}""";

    /// <summary>Answers stages like amber.ul.Bake: one JSON object per line, an exit code, optionally waiting for cancel.</summary>
    private sealed class FakeLoader : IBakeProcess
    {
        public Dictionary<string, Func<JsonObject?, (string[] Lines, int Exit)>> Stages { get; } = new()
        {
            ["version"] = _ => ([Version12], 0),
        };
        public List<(string Stage, JsonObject? Request)> Calls { get; } = [];
        public HashSet<string> Hang { get; } = [];
        public List<string> RequestFiles { get; } = [];

        public async Task<int> RunAsync(string javaPath, IReadOnlyList<string> classpath, string stage, string? requestFile, Action<string> stdout, Action<string> stderr, CancellationToken ct)
        {
            var request = requestFile is null ? null : JsonNode.Parse(await File.ReadAllTextAsync(requestFile, ct)) as JsonObject;
            if (requestFile is not null) RequestFiles.Add(requestFile);
            Calls.Add((stage, request));
            if (Hang.Contains(stage))
            {
                stdout("""{"type":"progress","stage":"bake-instance","step":"resolve","done":0,"total":3,"message":"resolving"}""");
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (!Stages.TryGetValue(stage, out var answer))
            {
                stdout($$"""{"type":"error","code":"unknown_stage","message":"unknown stage '{{stage}}'"}""");
                stderr("java.lang.IllegalArgumentException");
                return 1;
            }
            var (lines, exit) = answer(request);
            foreach (var line in lines) stdout(line);
            return exit;
        }

        public int Count(string stage) => Calls.Count(c => c.Stage == stage);
        public JsonObject Last(string stage) => Calls.Last(c => c.Stage == stage).Request!;
    }

    /// <summary>bake-instance: writes a state file (with the given cache files) and answers with its launch block.</summary>
    private static Func<JsonObject?, (string[], int)> BakeInstanceOk(params string[] cacheFiles) => request =>
    {
        var state = Path.Combine(request!["instanceDir"]!.GetValue<string>(), BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, new JsonObject { ["cacheFiles"] = new JsonArray([.. cacheFiles.Select(f => (JsonNode)f)]), ["libraries"] = new JsonArray() }.ToJsonString());
        var result = new JsonObject
        {
            ["type"] = "result",
            ["stateKey"] = "sk",
            ["launch"] = new JsonObject
            {
                ["mainClass"] = "amber.ul.Launcher",
                ["classpath"] = new JsonArray("loader.jar"),
                ["jvmArgs"] = new JsonArray("-Damber.ul.mappingCache=x"),
                ["gameArgsPrefix"] = new JsonArray("--state", state, "--"),
                ["stateFile"] = state,
            },
        };
        return ([result.ToJsonString()], 0);
    };

    private static (string[], int) Requirements(string mc) =>
        ([$$"""{"type":"result","schema":"amber-ul/requirements-result@1","files":[{"kind":"loader-lib","url":"https://example.invalid/asm.jar","sha1":"a","size":3,"path":"libs/asm-9.7.1.jar"},{"kind":"mappings","url":"https://example.invalid/intermediary.jar","sha1":"b","size":4,"path":"mappings/{{mc}}/intermediary-{{mc}}-v2.jar"},{"kind":"mappings","url":"https://example.invalid/client.txt","sha1":"c","size":5,"path":"mappings/{{mc}}/client_mappings.txt"}]}"""], 0);

    private FakeLoader VanillaLoader(params string[] cacheFiles)
    {
        var loader = new FakeLoader();
        loader.Stages["requirements"] = r => Requirements(r!["mcVersion"]!.GetValue<string>());
        loader.Stages["bake-instance"] = BakeInstanceOk(cacheFiles);
        loader.Stages["warm"] = _ => (["""{"type":"result","stateFile":"s","stats":{"bakeHits":7,"bakeMisses":2,"cacheFiles":31,"ms":1200}}"""], 0);
        return loader;
    }

    [Fact]
    public void ToolkitNeedsOnlyTheBuiltLoader()
    {
        Assert.Null(BakeToolkit.Find(Path.Combine(_root, "missing")));
        var toolkit = Toolkit();
        Assert.Equal(3, toolkit.Classpath.Count);
        Assert.EndsWith(Path.Combine("build", "loader"), toolkit.Classpath[0]);
        Assert.EndsWith(Path.Combine("libs", "b.jar"), toolkit.Classpath[2]);
    }

    [Fact]
    public void VersionReportsPlatformsAndInterface()
    {
        var info = BakeLoaderInfo.Parse(BakeService.ParseLine(Version12)!);
        Assert.Equal(new Version(1, 2), info.Interface);
        Assert.True(info.CanWarm);
        Assert.Equal(["1.20.1"], info.Platforms["forge"]);
        info.EnsureCompatible();

        // An interface 1.0 loader: only supportedMc, read as the vanilla base, and refused (no platforms, no warm).
        var old = BakeLoaderInfo.Parse(BakeService.ParseLine("""{"type":"result","loaderVersion":"0.2","protocol":1,"supportedMc":["1.21.1"]}""")!);
        Assert.Equal(["1.21.1"], old.Platforms[BakePlatform.Vanilla]);
        Assert.False(old.CanWarm);
        Assert.Equal("protocol_mismatch", Assert.Throws<BakeException>(old.EnsureCompatible).Code);

        // A loader with another cache protocol: refused with both numbers in the message.
        var other = BakeLoaderInfo.Parse(BakeService.ParseLine("""{"type":"result","loaderVersion":"0.9","protocol":2,"interface":"2.0","platforms":{}}""")!);
        var e = Assert.Throws<BakeException>(other.EnsureCompatible);
        Assert.Contains("2", e.Message);
        Assert.Contains("1", e.Message);
    }

    [Fact]
    public void OnlySupportedPlatformsAndVersionsAreBaked()
    {
        var info = BakeLoaderInfo.Parse(BakeService.ParseLine(Version12)!);
        Assert.True(BakeService.Supports(Instance("fabric"), info, out _));
        Assert.True(BakeService.Supports(Instance("quilt"), info, out _));
        Assert.True(BakeService.Supports(Instance("vanilla"), info, out _));
        Assert.True(BakeService.Supports(Instance("neoforge", loaderVersion: "21.1.265"), info, out _));
        Assert.True(BakeService.Supports(Instance("forge", "1.20.1", "1.20.1-47.4.28", "f"), info, out _));
        Assert.False(BakeService.Supports(Instance("forge", "1.21.1", "52.0.1"), info, out var forge));
        Assert.Contains("1.20.1", forge);
        Assert.False(BakeService.Supports(Instance("fabric", "1.20.1"), info, out var version));
        Assert.Contains("1.20.1", version);
        Assert.False(BakeService.Supports(Instance("optifine"), info, out var loader));
        Assert.Contains("optifine", loader);
    }

    [Fact]
    public void BakedLaunchReplacesMainClassAndClasspathButKeepsGameArguments()
    {
        Version("1.21.1");
        var launcher = new GameLauncher(new GameDirectory(Game));
        var baked = new BakedLaunch("amber.ul.Launcher", [Path.Combine(_root, "loader.jar"), Path.Combine(_root, "lib.jar")],
            ["-Dlog4j2.formatMsgNoLookups=true"], ["--state", Path.Combine(_root, "state.json"), "--"], Path.Combine(_root, "state.json"), "key");
        var args = launcher.BuildArguments(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            Baked = baked,
        });

        var main = args.IndexOf("amber.ul.Launcher");
        Assert.True(main > 0);
        Assert.DoesNotContain("net.minecraft.client.main.Main", args);
        Assert.Equal(["--state", Path.Combine(_root, "state.json"), "--"], args.Skip(main + 1).Take(3));
        Assert.Equal(["--username", "Tester"], args.Skip(main + 4).Take(2));
        Assert.Contains("-Dlog4j2.formatMsgNoLookups=true", args.Take(main));
        var classpath = args[args.IndexOf("-cp") + 1];
        Assert.Equal(string.Join(Path.PathSeparator, baked.Classpath), classpath);
        Assert.DoesNotContain("1.21.1.jar", classpath);
    }

    [Fact]
    public void NormalLaunchIsUnchanged()
    {
        Version("1.21.1");
        var args = new GameLauncher(new GameDirectory(Game)).BuildArguments(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        });
        Assert.Contains("net.minecraft.client.main.Main", args);
        Assert.DoesNotContain("amber.ul.Launcher", args);
    }

    [Fact]
    public void ResultParsingReadsLaunchAndRejectsIncompleteResults()
    {
        var result = BakeService.ParseLine("""{"type":"result","stateKey":"abc","launch":{"mainClass":"amber.ul.Launcher","classpath":["a","b"],"jvmArgs":["-Dx=1"],"gameArgsPrefix":["--state","s","--"],"stateFile":"s"}}""")!;
        var launch = BakeService.ParseLaunch(result);
        Assert.Equal("amber.ul.Launcher", launch.MainClass);
        Assert.Equal(["a", "b"], launch.Classpath);
        Assert.Equal("abc", launch.StateKey);
        Assert.Throws<InvalidDataException>(() => BakeService.ParseLaunch(new JsonObject { ["type"] = "result" }));
        Assert.Null(BakeService.ParseLine("[amber-ul] not json"));
        Assert.Null(BakeService.ParseLine("{broken"));
    }

    [Fact]
    public void ProblemsNameTheModAndTheDependency()
    {
        var problem = BakeService.ParseLine("""{"type":"problem","severity":"error","code":"missing_dep","modId":"sodium","file":"sodium.jar","dependency":{"id":"fabric-api","range":">=0.100"},"message":"needs fabric-api"}""")!;
        var text = BakeService.DescribeProblem(problem);
        Assert.StartsWith("sodium: ", text);
        Assert.Contains("fabric-api", text);
        Assert.Contains(">=0.100", text);
        // Unknown codes keep the loader's own message.
        var other = BakeService.ParseLine("""{"type":"problem","severity":"warning","code":"something_new","file":"x.jar","message":"loader says so"}""")!;
        Assert.Equal("x.jar: loader says so", BakeService.DescribeProblem(other));
    }

    [Fact]
    public void RuntimeMixinProblemsAreReadFromTheGameOutput()
    {
        var line = "[12:00:01] [main/INFO]: [amber-ul] 问题 mixin_incompatible (warning) 模组=sodium mixin=me.jellysquid.FooMixin 目标=net.minecraft.client.Foo 原因=target_missing";
        var problem = BakeRuntimeProblem.Parse(line)!;
        Assert.Equal("mixin_incompatible", problem.Code);
        Assert.Equal("sodium", problem.Mod);
        Assert.Equal("me.jellysquid.FooMixin", problem.Mixin);
        Assert.Equal("net.minecraft.client.Foo", problem.Target);
        Assert.Contains("sodium", problem.Describe());
        Assert.Null(BakeRuntimeProblem.Parse("[amber-ul] 映射图 新建 1.21.1"));
        Assert.Null(BakeRuntimeProblem.Parse("ordinary game line"));
    }

    [Fact]
    public async Task RequirementsDownloadOnlyTheMappingTablesIntoTheDataFolder()
    {
        Version("1.21.1");
        var downloads = new List<DownloadItem>();
        var loader = VanillaLoader();
        var bake = Service(loader, downloads);
        var dir = await bake.EnsureRequirementsAsync("java", new GameDirectory(Game), Instance(), null, CancellationToken.None);

        Assert.Equal(bake.MappingsDir("1.21.1"), dir);
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, d => Assert.StartsWith(Path.Combine(Data, "mappings", "1.21.1"), d.LocalPath));
        Assert.Contains(downloads, d => d.LocalPath.EndsWith("client_mappings.txt") && d.Sha1 == "c" && d.Size == 5);
        var request = loader.Last("requirements");
        Assert.Equal("vanilla", request["platform"]!.GetValue<string>());
        Assert.EndsWith("1.21.1.json", request["versionJson"]!.GetValue<string>());

        // Already there with the right size: not downloaded again.
        downloads.Clear();
        await bake.EnsureRequirementsAsync("java", new GameDirectory(Game), Instance(), null, CancellationToken.None);
        Assert.Empty(downloads);
    }

    [Fact]
    public async Task RequirementsPathsCannotLeaveTheMappingsFolder()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["requirements"] = _ => (["""{"type":"result","files":[{"kind":"mappings","url":"https://example.invalid/x","sha1":"a","size":1,"path":"mappings/../../evil.txt"}]}"""], 0);
        var bake = Service(loader);
        await Assert.ThrowsAnyAsync<Exception>(() => bake.EnsureRequirementsAsync("java", new GameDirectory(Game), Instance(), null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task UnsupportedPlatformFromRequirementsIsAnError()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["requirements"] = _ => (["""{"type":"problem","severity":"error","code":"incompatible_loader","modId":null,"file":null,"message":"platform forge is not supported on Minecraft 1.21.1"}"""], 1);
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).EnsureRequirementsAsync("java", new GameDirectory(Game), Instance(), null, CancellationToken.None));
        Assert.Single(e.Problems);
    }

    [Fact]
    public async Task FirstPrepareBakesAndTheSecondReusesWithoutCallingTheLoaderStages()
    {
        Version("1.21.1");
        var instance = Instance();
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(instance.ModsDir).FullName, "a.jar"), "a");
        var loader = VanillaLoader();
        var bake = Service(loader);
        var dir = new GameDirectory(Game);

        var first = await bake.PrepareAsync("java", 21, dir, instance, warm: false, null, CancellationToken.None);
        Assert.False(first.Reused);
        Assert.Equal("amber.ul.Launcher", first.Launch!.MainClass);
        Assert.Equal(1, loader.Count("bake-instance"));
        Assert.Equal(0, loader.Count("bake-mod"));
        Assert.Equal(0, loader.Count("warm"));
        var request = loader.Last("bake-instance");
        Assert.Null(request["platform"]);
        Assert.Equal(bake.MappingsDir("1.21.1"), request["mappingsDir"]!.GetValue<string>());
        Assert.Equal(bake.MappingCache, request["mappingCache"]!.GetValue<string>());
        Assert.Single(request["mods"]!.AsArray());
        Assert.Contains(first.Launch.StateFile, bake.KnownStates());
        Assert.All(loader.RequestFiles, f => Assert.False(File.Exists(f)));

        var second = await bake.PrepareAsync("java", 21, dir, instance, warm: false, null, CancellationToken.None);
        Assert.True(second.Reused);
        Assert.Equal(1, loader.Count("bake-instance"));
        Assert.Equal(1, loader.Count("requirements"));

        // A new mod: baked again.
        File.WriteAllText(Path.Combine(instance.ModsDir, "b.jar"), "b");
        var third = await bake.PrepareAsync("java", 21, dir, instance, warm: false, null, CancellationToken.None);
        Assert.False(third.Reused);
        Assert.Equal(2, loader.Count("bake-instance"));
    }

    [Fact]
    public async Task WarmRunsAfterBakeNowOnlyWhenTheLoaderHasIt()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        var bake = Service(loader);
        var done = await bake.PrepareAsync("java", 21, new GameDirectory(Game), Instance(), warm: true, null, CancellationToken.None);
        Assert.Equal(new BakeWarmStats(7, 2, 31, 1200), done.Warm);
        Assert.Equal(done.Launch!.StateFile, loader.Last("warm")["stateFile"]!.GetValue<string>());
        Assert.Contains("31", done.Reason);

        var older = VanillaLoader();
        older.Stages["version"] = _ => ([Version12.Replace("\"interface\":\"1.2\"", "\"interface\":\"1.1\"")], 0);
        var other = Instance(id: "other");
        Version("other", "1.21.1");
        var skipped = await Service(older).PrepareAsync("java", 21, new GameDirectory(Game), other, warm: true, null, CancellationToken.None);
        Assert.NotNull(skipped.Launch);
        Assert.Null(skipped.Warm);
        Assert.Equal(0, older.Count("warm"));
    }

    [Fact]
    public async Task ProtocolMismatchStopsBeforeAnyBake()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["version"] = _ => ([Version12.Replace("\"protocol\":1", "\"protocol\":2")], 0);
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("protocol_mismatch", e.Code);
        Assert.Equal(0, loader.Count("requirements"));
        Assert.Equal(0, loader.Count("bake-instance"));
    }

    [Fact]
    public async Task DependencyProblemsFailTheBakeWithTheirReasons()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["bake-instance"] = _ => ([
            """{"type":"progress","stage":"bake-instance","step":"resolve","done":1,"total":3,"message":"resolving"}""",
            """{"type":"problem","severity":"warning","code":"duplicate_mod","modId":"lithium","file":"lithium-2.jar","message":"duplicate"}""",
            """{"type":"problem","severity":"error","code":"missing_dep","modId":"modmenu","file":"modmenu.jar","dependency":{"id":"fabric-api","range":"*"},"message":"needs fabric-api"}""",
        ], 2);
        var events = new List<BakeEvent>();
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false,
            new SyncProgress<BakeEvent>(events.Add), CancellationToken.None));
        Assert.Single(e.Problems);
        Assert.Contains("modmenu", e.Message);
        Assert.Contains("fabric-api", e.Message);
        Assert.Contains(events, x => x is { IsProblem: true, IsError: false, Code: "duplicate_mod" });
        Assert.Contains(events, x => x is { IsProblem: true, IsError: true, Code: "missing_dep" });
        Assert.Null(BakeRecord.Read(Instance().WorkDir));
    }

    [Fact]
    public async Task ErrorLinesBecomeLocalizedFailures()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["bake-instance"] = _ => (["""{"type":"error","code":"internal","message":"java.lang.NullPointerException: boom"}"""], 1);
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Contains("boom", e.Message);
        Assert.Equal("internal", e.Code);

        // A stage the loader does not know (an older build): unknown_stage.
        loader.Stages.Remove("warm");
        var stale = Path.Combine(_root, "state.json");
        File.WriteAllText(stale, "{}");
        var unknown = await Assert.ThrowsAsync<BakeException>(() => Service(loader).WarmAsync("java", stale, null, CancellationToken.None));
        Assert.Equal("unknown_stage", unknown.Code);
    }

    [Fact]
    public async Task CancellingKillsTheStageAndLeavesNoRequestFile()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Hang.Add("bake-instance");
        using var cts = new CancellationTokenSource();
        var events = new List<BakeEvent>();
        var task = Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false,
            new SyncProgress<BakeEvent>(e => { events.Add(e); if (e.Message == "resolving") cts.Cancel(); }), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.All(loader.RequestFiles, f => Assert.False(File.Exists(f)));
        Assert.Null(BakeRecord.Read(Instance().WorkDir));
    }

    [Fact]
    public async Task MissingCacheFilesMakeThePreviousBakeStale()
    {
        Version("1.21.1");
        var kept = Path.Combine(Cache, "baked", "mods", "a.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "a");
        var loader = VanillaLoader(kept);
        var bake = Service(loader);
        var dir = new GameDirectory(Game);
        await bake.PrepareAsync("java", 21, dir, Instance(), false, null, CancellationToken.None);
        Assert.NotNull(bake.Current(dir, Instance(), 21));
        File.Delete(kept);   // gc removed it, or the cache folder was deleted
        Assert.Null(bake.Current(dir, Instance(), 21));
    }

    [Fact]
    public async Task GcSendsEveryLiveStateAndWarmsOrDropsStatesWithoutCacheFiles()
    {
        Version("1.21.1");
        Version("old", "1.21.1");
        var loader = VanillaLoader();
        var bake = Service(loader);
        var dir = new GameDirectory(Game);
        var live = (await bake.PrepareAsync("java", 21, dir, Instance(), false, null, CancellationToken.None)).Launch!.StateFile;
        // A second instance whose state has no cacheFiles (interface 1.1 bake) and that cannot be warmed (no libraries).
        loader.Stages["bake-instance"] = r =>
        {
            var (lines, exit) = BakeInstanceOk()(r);
            File.WriteAllText(Path.Combine(r!["instanceDir"]!.GetValue<string>(), BakeService.StateFolder, "state.json"), "{}");
            return (lines, exit);
        };
        var old = Instance(id: "old");
        var stale = (await bake.PrepareAsync("java", 21, dir, old, false, null, CancellationToken.None)).Launch!.StateFile;
        Assert.Equal(2, bake.KnownStates().Count);

        Directory.CreateDirectory(Path.Combine(Cache, "baked"));
        Directory.CreateDirectory(bake.MappingCache);
        loader.Stages["warm"] = _ => (["""{"type":"error","code":"stale_state","message":"state file has no libraries"}"""], 1);
        loader.Stages["gc"] = _ => (["""{"type":"result","apply":false,"cacheRoot":"c","liveStates":1,"liveFiles":3,"kept":3,"skipped":0,"removable":2,"removableBytes":2048,"remove":[{"path":"a","bytes":1024,"reason":"unreferenced"},{"path":"b","bytes":1024,"reason":"stale temp"}]}"""], 0);

        var result = await bake.CollectGarbageAsync("java", apply: false, null, CancellationToken.None);
        Assert.Equal(new BakeGcResult(false, 1, 2, 2048, 0, 0), result);
        var request = loader.Last("gc");
        Assert.Equal([live], request["liveStateFiles"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(request["apply"]!.GetValue<bool>());
        Assert.False(request["allowNoLiveStates"]!.GetValue<bool>());
        Assert.Equal(bake.MappingCache, request["mappingCache"]!.GetValue<string>());
        Assert.Equal(stale, loader.Last("warm")["stateFile"]!.GetValue<string>());
        // The state that could not be warmed lost its record (it bakes again) and left the list.
        Assert.Null(BakeRecord.Read(old.WorkDir));
        Assert.Equal([live], bake.KnownStates());
    }

    [Fact]
    public async Task GcWithoutACacheDoesNothingAndDeletedInstancesLeaveTheLiveSet()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        var bake = Service(loader);
        Assert.Null(await bake.CollectGarbageAsync("java", apply: true, null, CancellationToken.None));
        Assert.Equal(0, loader.Count("gc"));

        var state = (await bake.PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None)).Launch!.StateFile;
        Directory.CreateDirectory(Path.Combine(Cache, "baked"));
        Directory.Delete(Instance().WorkDir, true);   // the instance was deleted
        loader.Stages["gc"] = _ => (["""{"type":"result","apply":true,"liveStates":0,"removable":4,"removableBytes":10,"removed":4,"failed":0,"remove":[]}"""], 0);
        var result = await bake.CollectGarbageAsync("java", apply: true, null, CancellationToken.None);
        Assert.Equal(4, result!.Removed);
        var request = loader.Last("gc");
        Assert.Empty(request["liveStateFiles"]!.AsArray());
        Assert.True(request["allowNoLiveStates"]!.GetValue<bool>());
        Assert.True(request["apply"]!.GetValue<bool>());
        Assert.DoesNotContain(state, bake.KnownStates());
    }

    /// <summary>A NeoForge/Forge install as Jay leaves it (processor outputs in libraries) plus a fake official installer.</summary>
    private void PlatformInstall(string kind, string mc, string version, bool withSrg = true, bool withPatched = true)
    {
        Version(mc);
        var mcp = mc == "1.20.1" ? "1.20.1-20230612.114412" : "1.21.1-20240808.144430";
        var patched = kind == "forge" ? $"net.minecraftforge:forge:{version}:client" : $"net.neoforged:neoforge:{version}:client";
        var profile = new JsonObject
        {
            ["version"] = kind == "forge" ? $"{mc}-forge-{version.Split('-')[^1]}" : $"neoforge-{version}",
            ["json"] = "/version.json",
            ["data"] = new JsonObject
            {
                ["MC_SRG"] = new JsonObject { ["client"] = $"[net.minecraft:client:{mcp}:srg]" },
                ["MC_EXTRA"] = new JsonObject { ["client"] = $"[net.minecraft:client:{mcp}:extra]" },
                ["MC_SLIM"] = new JsonObject { ["client"] = $"[net.minecraft:client:{mcp}:slim]" },
                ["PATCHED"] = new JsonObject { ["client"] = $"[{patched}]" },
                ["BINPATCH"] = new JsonObject { ["client"] = "/data/client.lzma" },
            },
            ["libraries"] = new JsonArray(),
        };
        var installer = Path.Combine(_root, $"{kind}-installer.jar");
        using (var zip = ZipFile.Open(installer, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("install_profile.json", profile.ToJsonString());
            Add("version.json", """{"id":"x","arguments":{"game":["--fml.neoForgeVersion","21.1.265"]},"libraries":[]}""");
            Add("data/client.lzma", "lzma");
        }
        FakeFiles[BakePlatform.InstallerUrl(kind, version)] = installer;
        var dir = new GameDirectory(Game);
        void Lib(string coordinate)
        {
            var path = dir.LibraryPath(MavenCoordinate.Parse(coordinate).RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, coordinate);
        }
        if (withSrg) Lib($"net.minecraft:client:{mcp}:srg");
        Lib($"net.minecraft:client:{mcp}:extra");
        if (withPatched) Lib(patched);
    }

    [Fact]
    public async Task NeoForgePlatformInstallIsPreparedFromTheInstallerAndTheGameLibraries()
    {
        PlatformInstall("neoforge", "1.21.1", "21.1.265");
        var dir = new GameDirectory(Game);
        var instance = Instance("neoforge", loaderVersion: "21.1.265");
        var downloads = new List<DownloadItem>();
        var loader = VanillaLoader();
        var bake = Service(loader, downloads);

        var done = await bake.PrepareAsync("java", 21, dir, instance, false, null, CancellationToken.None);
        Assert.NotNull(done.Launch);
        var request = loader.Last("bake-instance");
        var platform = request["platform"]!.AsObject();
        Assert.Equal("neoforge", platform["kind"]!.GetValue<string>());
        var installDir = platform["installDir"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(Data, "platforms"), installDir);
        Assert.True(File.Exists(Path.Combine(installDir, "inst", "version.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "inst", "install_profile.json")));
        Assert.True(File.Exists(Path.Combine(installDir, "inst", "data", "client.lzma")));
        // libraries is a link to the game's libraries, not a copy.
        var libraries = new DirectoryInfo(Path.Combine(installDir, "libraries"));
        Assert.NotNull(libraries.LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(installDir, "libraries", "net", "minecraft", "client")));
        var marker = Assert.Single(Directory.GetFiles(Path.Combine(installDir, "platform-cache"), "*.json"));
        Assert.Contains("-client-", Path.GetFileName(marker));
        var json = JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
        Assert.EndsWith("-srg.jar", json["srg"]!.GetValue<string>());
        Assert.EndsWith("-extra.jar", json["extra"]!.GetValue<string>());
        Assert.Equal("neoforge", loader.Last("requirements")["platform"]!.GetValue<string>());
        Assert.Contains(downloads, d => d.Url.EndsWith("neoforge-21.1.265-installer.jar"));

        // Prepared twice: the installer is not downloaded again and the marker is unchanged.
        var stamp = File.GetLastWriteTimeUtc(marker);
        downloads.Clear();
        await BakePlatform.PrepareAsync(dir, instance, Data, (items, _) => { downloads.AddRange(items); return Task.CompletedTask; }, null, CancellationToken.None);
        Assert.Empty(downloads);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(marker));
    }

    [Fact]
    public async Task ForgeNeedsThePatchedJarAndAnIncompleteInstallSaysWhatIsMissing()
    {
        PlatformInstall("forge", "1.20.1", "1.20.1-47.4.28", withPatched: false);
        var instance = Instance("forge", "1.20.1", "1.20.1-47.4.28", "forge-1.20.1");
        var e = await Assert.ThrowsAsync<BakeException>(() => BakePlatform.PrepareAsync(new GameDirectory(Game), instance, Data,
            (items, _) =>
            {
                foreach (var item in items) { Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!); File.Copy(FakeFiles[item.Url], item.LocalPath, true); }
                return Task.CompletedTask;
            }, null, CancellationToken.None));
        Assert.Contains("forge-1.20.1-47.4.28-client.jar", e.Message);
    }

    [Fact]
    public void ForgeUpdateCheckIsTurnedOff()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "forge-work")).FullName;
        BakePlatform.DisableForgeUpdateCheck(work);
        var path = Path.Combine(work, "config", "fml.toml");
        Assert.Equal("versionCheck = false", File.ReadAllText(path).Trim());
        File.WriteAllText(path, "earlyWindowControl = true\nversionCheck = true\nmaxThreads = -1\n");
        BakePlatform.DisableForgeUpdateCheck(work);
        Assert.Equal("earlyWindowControl = true\nversionCheck = false\nmaxThreads = -1\n", File.ReadAllText(path));
        File.WriteAllText(path, "maxThreads = -1\n");
        BakePlatform.DisableForgeUpdateCheck(work);
        Assert.Contains("versionCheck = false", File.ReadAllText(path));
    }

    [Fact]
    public void PreviousBakeIsReusedUntilModsChange()
    {
        Version("1.21.1");
        var instance = Instance();
        var mods = Directory.CreateDirectory(instance.ModsDir).FullName;
        File.WriteAllText(Path.Combine(mods, "a.jar"), "a");
        var dir = new GameDirectory(Game);
        var bake = Service();
        var state = Path.Combine(instance.WorkDir, BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");
        var launch = new BakedLaunch("amber.ul.Launcher", ["a"], [], ["--state", state, "--"], state, "k");
        BakeRecord.Write(instance.WorkDir, new BakeRecord(bake.InputKey(dir, instance, 21), launch));

        Assert.Equal(launch.MainClass, bake.Current(dir, instance, 21)?.MainClass);
        Assert.Null(bake.Current(dir, instance, 25));
        Assert.Null(bake.Current(dir, instance with { Loader = "quilt" }, 21));

        File.WriteAllText(Path.Combine(mods, "b.jar"), "b");
        Assert.Null(bake.Current(dir, instance, 21));

        File.Delete(Path.Combine(mods, "b.jar"));
        Assert.NotNull(bake.Current(dir, instance, 21));
        File.Delete(state);
        Assert.Null(bake.Current(dir, instance, 21));
    }

    private (BakeService Bake, GameDirectory Directory, GameInstance Instance) ReusableBake()
    {
        Version("1.21.1");
        var toolkit = Toolkit();
        foreach (var relative in new[] { "libs/a.jar", "libs/b.jar", "build/loader/amber/ul/Bake.class", "build/loader/loader.properties", "build/fmlbridge.jar" })
        {
            var file = Path.Combine(toolkit.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "old");
        }
        var instance = Instance();
        var state = Path.Combine(instance.WorkDir, BakeService.StateFolder, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "{}");
        var directory = new GameDirectory(Game);
        var bake = new BakeService(toolkit, Cache, Data);
        var mappings = Directory.CreateDirectory(bake.MappingsDir("1.21.1")).FullName;
        File.WriteAllText(Path.Combine(mappings, "client_mappings.txt"), "old");
        var launch = new BakedLaunch("amber.ul.Launcher", toolkit.Classpath, [], ["--state", state, "--"], state, "key");
        BakeRecord.Write(instance.WorkDir, new BakeRecord(bake.InputKey(directory, instance, 21), launch));
        Assert.NotNull(bake.Current(directory, instance, 21));
        return (bake, directory, instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdatingClasspathInTheSameToolkitInvalidatesPreviousLaunch(bool removeOldDependency)
    {
        var (bake, dir, instance) = ReusableBake();
        var root = bake.Toolkit.Root;
        File.WriteAllText(Path.Combine(root, "libs", "a-v2.jar"), "new");
        File.WriteAllText(Path.Combine(root, "build", "loader-cp.txt"), "libs/a-v2.jar;libs/b.jar\n");
        if (removeOldDependency) File.Delete(Path.Combine(root, "libs", "a.jar"));

        // Found again before every launch: an old record must not keep using the old class path.
        var updated = new BakeService(BakeToolkit.Find(root)!, bake.CacheRoot, bake.DataRoot);
        Assert.Null(updated.Current(dir, instance, 21));
    }

    [Theory]
    [InlineData("libs/a.jar")]
    [InlineData("build/loader/amber/ul/Bake.class")]
    [InlineData("build/loader/loader.properties")]
    [InlineData("build/fmlbridge.jar")]
    public void ChangedToolkitInputsInvalidateEvenWhenSizeAndTimestampArePreserved(string relative)
    {
        var (bake, dir, instance) = ReusableBake();
        var file = Path.Combine(bake.Toolkit.Root, relative);
        var time = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "new");
        File.SetLastWriteTimeUtc(file, time);

        Assert.Null(bake.Current(dir, instance, 21));
    }

    [Fact]
    public void ChangedMappingTablesInvalidate()
    {
        var (bake, dir, instance) = ReusableBake();
        File.WriteAllText(Path.Combine(bake.MappingsDir("1.21.1"), "client_mappings.txt"), "newer and longer");
        Assert.Null(bake.Current(dir, instance, 21));
    }

    [Fact]
    public void GeneratedFilesAndOtherVersionsDoNotInvalidatePreviousBake()
    {
        var (bake, dir, instance) = ReusableBake();
        Directory.CreateDirectory(bake.CacheRoot);
        File.WriteAllText(Path.Combine(bake.CacheRoot, "generated.jar"), "generated");
        File.WriteAllText(Path.Combine(bake.Toolkit.Root, "build", "build.log"), "build output");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(bake.MappingsDir("1.20.1")).FullName, "client_mappings.txt"), "unused");
        Directory.CreateDirectory(bake.MappingCache);
        File.WriteAllText(Path.Combine(bake.MappingCache, "graph.amap"), "graph");
        // Line endings and blanks in the list do not change the class path.
        File.WriteAllText(Path.Combine(bake.Toolkit.Root, "build", "loader-cp.txt"), "libs/a.jar\r\n  libs/b.jar\r\n");
        var unchanged = new BakeService(BakeToolkit.Find(bake.Toolkit.Root)!, bake.CacheRoot, bake.DataRoot);

        Assert.NotNull(unchanged.Current(dir, instance, 21));
    }

    [Fact]
    public void GameInputsUseTheVanillaParentWithoutLoaderLibraries()
    {
        Version("1.21.1", libraries: """[{"name":"com.example:base:1","downloads":{"artifact":{"path":"com/example/base/1/base-1.jar","url":"https://example.invalid/base-1.jar"}}}]""");
        Version("fabric-1.21.1", "1.21.1", """[{"name":"net.fabricmc:fabric-loader:0.16.0","url":"https://maven.fabricmc.net/"}]""");
        var (jar, libraries) = BakeService.GameInputs(new GameDirectory(Game), new GameInstance("fabric-1.21.1", "1.21.1", "fabric", "0.16.0", _root));
        Assert.EndsWith(Path.Combine("1.21.1", "1.21.1.jar"), jar);
        Assert.Single(libraries);
        Assert.DoesNotContain(libraries, l => l.Contains("fabric-loader"));
    }
}

/// <summary>Reports synchronously (Progress&lt;T&gt; posts to the thread pool, too late for assertions).</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
