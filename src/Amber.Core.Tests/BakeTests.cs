using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Auth;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>
/// Experimental baking against the universal loader's interface v1.3. No real loader: a fake process answers each
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
        /// <summary>The Java each stage ran on.</summary>
        public List<(string Stage, string Java)> JavaPaths { get; } = [];
        public HashSet<string> Hang { get; } = [];
        /// <summary>A stage that dies before printing anything, with these stderr lines (for example an old JVM).</summary>
        public Dictionary<string, string[]> Crash { get; } = [];
        public List<string> RequestFiles { get; } = [];

        public async Task<int> RunAsync(string javaPath, IReadOnlyList<string> classpath, string stage, string? requestFile, Action<string> stdout, Action<string> stderr, CancellationToken ct)
        {
            var request = requestFile is null ? null : JsonNode.Parse(await File.ReadAllTextAsync(requestFile, ct)) as JsonObject;
            if (requestFile is not null) RequestFiles.Add(requestFile);
            Calls.Add((stage, request));
            JavaPaths.Add((stage, javaPath));
            if (Hang.Contains(stage))
            {
                stdout("""{"type":"progress","stage":"bake-instance","step":"resolve","done":0,"total":3,"message":"resolving"}""");
                await Task.Delay(Timeout.Infinite, ct);
            }
            if (Crash.TryGetValue(stage, out var crash))
            {
                foreach (var line in crash) stderr(line);
                return 1;
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

    // ---- Interface v1.3: packaged loader, javaMin, cache state, runtime problems ----

    private const string Version13 = """{"type":"result","schema":"amber-ul/version-result@1","loaderVersion":"0.3.0-proto","pipelineVersion":"ul-proto-9","protocol":1,"interface":"1.3","supportedMc":["1.20.1","1.21.1"],"platforms":{"vanilla":["1.21.1"],"neoforge":["1.21.1"],"forge":["1.20.1"]},"javaMin":{"vanilla":21,"neoforge":21,"forge":21},"javaRuntime":21}""";

    /// <summary>A loader package folder (loader.jar, libs/, bridges/, manifest.json with the real hashes).</summary>
    private string Package(string? folder = null, string javaMin = """{"vanilla":21,"neoforge":21,"forge":21}""", Action<JsonObject>? tweak = null)
    {
        var root = folder ?? Path.Combine(_root, "pkg", "amber-ul-0.3.0-proto");
        var contents = new Dictionary<string, string>
        {
            ["loader.jar"] = "loader",
            ["libs/asm-9.10.1.jar"] = "asm",
            ["libs/sponge-mixin-0.17.5.jar"] = "mixin",
            ["bridges/fmlbridge.jar"] = "fml",
            ["bridges/fmlbridge-forge.jar"] = "fmlforge",
        };
        var files = new JsonObject();
        foreach (var (name, text) in contents)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            files[name] = new JsonObject { ["sha256"] = BakePackage.Sha256(path), ["sha1"] = "x", ["size"] = new FileInfo(path).Length };
        }
        var manifest = new JsonObject
        {
            ["schema"] = "amber-ul/package@1",
            ["loaderVersion"] = "0.3.0-proto",
            ["protocol"] = 1,
            ["interface"] = "1.3",
            ["platforms"] = JsonNode.Parse("""{"vanilla":["1.21.1"],"neoforge":["1.21.1"],"forge":["1.20.1"]}"""),
            ["javaMin"] = JsonNode.Parse(javaMin),
            ["entry"] = JsonNode.Parse("""{"bake":"amber.ul.Bake","launch":"amber.ul.Launcher"}"""),
            ["classpath"] = new JsonArray("loader.jar", "libs/asm-9.10.1.jar", "libs/sponge-mixin-0.17.5.jar"),
            ["bridges"] = JsonNode.Parse("""{"fmlbridge.jar":"bridges/fmlbridge.jar","fmlbridge-forge.jar":"bridges/fmlbridge-forge.jar"}"""),
            ["files"] = files,
        };
        tweak?.Invoke(manifest);
        File.WriteAllText(Path.Combine(root, BakePackage.ManifestName), manifest.ToJsonString());
        return root;
    }

    private string PackageZip(string name = "loader.zip", Action<string>? beforeZip = null, Action<ZipArchive>? extra = null)
    {
        var folder = Path.Combine(_root, "zip-src-" + Guid.NewGuid().ToString("N")[..6], "amber-ul-0.3.0-proto");
        Package(folder);
        beforeZip?.Invoke(folder);
        var zip = Path.Combine(_root, name);
        ZipFile.CreateFromDirectory(Path.GetDirectoryName(folder)!, zip);
        if (extra is not null)
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            extra(archive);
        }
        return zip;
    }

    private BakeService PackagedService(string packageRoot, FakeLoader loader) =>
        new(BakeToolkit.Find(packageRoot)!, Cache, Data, (items, _) =>
        {
            foreach (var item in items)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                File.WriteAllText(item.LocalPath, new string('x', (int)(item.Size ?? 1)));
            }
            return Task.CompletedTask;
        }, loader);

    [Fact]
    public void PackagedLoaderIsFoundFromItsManifestAndChecked()
    {
        var root = Package();
        var toolkit = BakeToolkit.Find(root)!;
        Assert.NotNull(toolkit.Package);
        Assert.Equal("0.3.0-proto", toolkit.Package!.LoaderVersion);
        Assert.Equal(new Version(1, 3), toolkit.Package.Interface);
        Assert.Equal(3, toolkit.Classpath.Count);
        Assert.EndsWith("loader.jar", toolkit.Classpath[0]);
        Assert.All(toolkit.Classpath, p => Assert.StartsWith(root, p));
        Assert.Equal(["fmlbridge-forge.jar", "fmlbridge.jar"], toolkit.BridgeJars.Select(Path.GetFileName));
        toolkit.Verify();

        // One changed byte in a library: refused, naming the file.
        File.WriteAllText(Path.Combine(root, "libs", "asm-9.10.1.jar"), "asX");
        var e = Assert.Throws<BakeException>(toolkit.Verify);
        Assert.Equal("package_mismatch", e.Code);
        Assert.Contains("libs/asm-9.10.1.jar", e.Message);
        // A missing bridge: refused too.
        Package(root);
        File.Delete(Path.Combine(root, "bridges", "fmlbridge.jar"));
        Assert.Equal("package_mismatch", Assert.Throws<BakeException>(BakeToolkit.Find(root)!.Verify).Code);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("classpath-outside")]
    [InlineData("file-escapes")]
    [InlineData("bad-hash")]
    public void MalformedManifestsAreRefusedWithAClearMessage(string what)
    {
        var root = Package(tweak: m =>
        {
            switch (what)
            {
                case "schema": m["schema"] = "amber-ul/package@9"; break;
                case "classpath-outside": m["classpath"] = new JsonArray("loader.jar", "../elsewhere.jar"); break;
                case "file-escapes": m["files"]!["../evil.jar"] = new JsonObject { ["sha256"] = new string('0', 64), ["size"] = 1 }; break;
                case "bad-hash": m["files"]!["loader.jar"]!["sha256"] = "short"; break;
            }
        });
        var e = Assert.Throws<BakeException>(() => BakeToolkit.Find(root));
        Assert.Equal("package_bad", e.Code);
    }

    [Fact]
    public async Task TamperedPackageStopsBeforeAnyLoaderProcess()
    {
        Version("1.21.1");
        var root = Package();
        var loader = VanillaLoader();
        loader.Stages["version"] = _ => ([Version13], 0);
        var bake = PackagedService(root, loader);
        File.AppendAllText(Path.Combine(root, "loader.jar"), "!");
        var e = await Assert.ThrowsAsync<BakeException>(() => bake.PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("package_mismatch", e.Code);
        Assert.Empty(loader.Calls);
    }

    [Fact]
    public async Task PackageBakesWithItsOwnClasspathAndBridgesCountInTheInputKey()
    {
        Version("1.21.1");
        var root = Package();
        var loader = VanillaLoader();
        loader.Stages["version"] = _ => ([Version13], 0);
        var bake = PackagedService(root, loader);
        var dir = new GameDirectory(Game);
        var instance = Instance();
        var done = await bake.PrepareAsync("java", 21, dir, instance, false, null, CancellationToken.None);
        Assert.NotNull(done.Launch);
        Assert.NotNull(bake.Current(dir, instance, 21));

        // Swapping a bridge for a different build means a new bake (and the package check refuses it before that).
        File.WriteAllText(Path.Combine(root, "bridges", "fmlbridge.jar"), "fml2");
        Assert.Null(bake.Current(dir, instance, 21));
    }

    [Fact]
    public void InstallingFromZipChecksUnpacksAndReplacesTheSameVersion()
    {
        var install = Path.Combine(_root, "installed");
        var package = BakePackage.InstallFromZip(PackageZip(), install);
        Assert.Equal(Path.Combine(install, "0.3.0-proto"), package.Root);
        Assert.True(File.Exists(Path.Combine(package.Root, "loader.jar")));
        Assert.True(File.Exists(Path.Combine(package.Root, "bridges", "fmlbridge-forge.jar")));
        Assert.NotNull(BakeToolkit.Find(package.Root)!.Package);
        // Installing the same version again replaces it, leaving no temporary folders.
        File.WriteAllText(Path.Combine(package.Root, "stray.txt"), "old");
        BakePackage.InstallFromZip(PackageZip("again.zip"), install);
        Assert.False(File.Exists(Path.Combine(package.Root, "stray.txt")));
        Assert.Equal(["0.3.0-proto"], Directory.GetDirectories(install).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("extra")]
    [InlineData("escape")]
    [InlineData("no-manifest")]
    public void BadZipsAreRefusedAndNothingIsInstalled(string what)
    {
        var install = Path.Combine(_root, "installed");
        var zip = what switch
        {
            "changed" => PackageZip(beforeZip: folder => File.WriteAllText(Path.Combine(folder, "libs", "asm-9.10.1.jar"), "tampered")),
            "extra" => PackageZip(beforeZip: folder => File.WriteAllText(Path.Combine(folder, "libs", "surprise.jar"), "x")),
            "escape" => PackageZip(extra: archive =>
            {
                using var writer = new StreamWriter(archive.CreateEntry("amber-ul-0.3.0-proto/../../evil.jar").Open());
                writer.Write("evil");
            }),
            _ => PackageZip(beforeZip: folder => File.Delete(Path.Combine(folder, BakePackage.ManifestName))),
        };
        var e = Assert.Throws<BakeException>(() => BakePackage.InstallFromZip(zip, install));
        Assert.Contains(e.Code, new[] { "package_bad", "package_mismatch" });
        Assert.False(Directory.Exists(Path.Combine(install, "0.3.0-proto")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.jar")));
        Assert.Empty(Directory.Exists(install) ? Directory.GetDirectories(install) : []);
    }

    [Fact]
    public async Task JavaMinComesFromTheLoader()
    {
        Version("1.21.1");
        Version("1.20.1");
        // The manifest says Forge needs 25: the instance's 17 becomes 25, and 21 is refused before any process.
        var root = Package(javaMin: """{"vanilla":21,"neoforge":21,"forge":25}""");
        var loader = VanillaLoader();
        loader.Stages["version"] = _ => ([Version13], 0);
        var bake = PackagedService(root, loader);
        var forge = Instance("forge", "1.20.1", "47.4.28", "1.20.1");
        Assert.Equal(25, bake.JavaMajorFor(forge, 17));
        Assert.Equal(21, bake.JavaMajorFor(Instance(), 17));
        var e = await Assert.ThrowsAsync<BakeException>(() => bake.PrepareAsync("java", 21, new GameDirectory(Game), forge, false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Contains("25", e.Message);
        Assert.Empty(loader.Calls);

        // version's javaMin counts too (a build folder has no manifest).
        var dev = VanillaLoader();
        dev.Stages["version"] = _ => ([Version13.Replace("\"vanilla\":21", "\"vanilla\":22")], 0);
        e = await Assert.ThrowsAsync<BakeException>(() => Service(dev).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Equal(0, dev.Count("bake-instance"));

        // bake-instance's own java_too_old error reads the same.
        var refusing = VanillaLoader();
        refusing.Stages["version"] = _ => ([Version13], 0);
        refusing.Stages["bake-instance"] = _ => (["""{"type":"error","code":"java_too_old","message":"javaMajor 21 is below 21"}"""], 1);
        e = await Assert.ThrowsAsync<BakeException>(() => Service(refusing).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Contains("Java 21", e.Message);
    }

    /// <summary>bake-instance answering with interface 1.3's cache object and problems file.</summary>
    private static Func<JsonObject?, (string[], int)> BakeInstance13(string state, bool warm) => request =>
    {
        var (lines, exit) = BakeInstanceOk("c1")(request);
        var result = JsonNode.Parse(lines[0])!.AsObject();
        result["cache"] = new JsonObject { ["state"] = state, ["stateKeyChanged"] = state != "reused", ["bakeMisses"] = state == "reused" ? 0 : 3, ["newCacheFiles"] = 0, ["warm"] = warm };
        result["launch"]!["problemsFile"] = Path.Combine(request!["instanceDir"]!.GetValue<string>(), ".amber-ul", "runtime-problems.jsonl");
        result["launch"]!["javaMin"] = 21;
        return ([result.ToJsonString()], exit);
    };

    [Theory]
    [InlineData("new", true, true)]
    [InlineData("rebuilt", true, true)]
    [InlineData("reused", false, false)]
    [InlineData("rebuilt", false, false)]
    public async Task WarmRunsOnlyWhenTheLoaderAsksForIt(string state, bool warmFlag, bool expectWarm)
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Stages["version"] = _ => ([Version13], 0);
        loader.Stages["bake-instance"] = BakeInstance13(state, warmFlag);
        var done = await Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), warm: true, null, CancellationToken.None);
        Assert.Equal(state, done.Cache!.State);
        Assert.Equal(expectWarm ? 1 : 0, loader.Count("warm"));
        Assert.Equal(expectWarm, done.Warm is not null);
        if (state == "reused") Assert.Equal(Amber.Core.Localization.L.T("core.bake.done_reused"), done.Reason);
        // The problems file travels with the launch block and the bake record.
        Assert.EndsWith("runtime-problems.jsonl", done.Launch!.ProblemsFile);
        Assert.Equal(done.Launch.ProblemsFile, BakeRecord.Read(Instance().WorkDir)!.Launch.ProblemsFile);
    }

    [Fact]
    public void RuntimeProblemsAreReadFromPrefixedLinesAndTheProblemsFile()
    {
        var mixin = """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"warning","code":"mixin_incompatible","modId":"sodium","mixin":"me.jellysquid.FooMixin","target":"net.minecraft.client.Foo","category":"target_missing","providesApi":"false","message":"switched off"}""";
        // The game's logger may put a timestamp in front of the prefix.
        var problem = BakeRuntimeProblem.Parse("[12:00:01] [main/INFO]: " + BakeRuntimeProblem.Prefix + mixin)!;
        Assert.Equal("mixin_incompatible", problem.Code);
        Assert.Equal("sodium", problem.Mod);
        Assert.Equal("me.jellysquid.FooMixin", problem.Mixin);
        Assert.True(problem.DisablesFeatures);
        Assert.Contains("sodium", problem.Describe());
        Assert.Null(BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + "{not json"));
        // Without the prefix (and not legacy), the Chinese line is for people only.
        Assert.Null(BakeRuntimeProblem.Parse("[amber-ul] 问题 mixin_incompatible (warning) 模组=sodium mixin=a 目标=b 原因=c"));

        var file = Path.Combine(_root, "runtime-problems.jsonl");
        File.WriteAllLines(file,
        [
            """{"type":"launch","schema":"amber-ul/runtime-problem@1","loaderVersion":"0.3.0-proto","interface":"1.3","time":"2026-11-10T00:00:00Z"}""",
            mixin,
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"warning","code":"mixin_semantic_risk","modId":"lithium","mixin":"a.B","handler":"onTick","action":"injector_removed","message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"info","code":"fabric_api_bridged","modId":"fabric-networking-api-v1","mixins":"3","platform":"neoforge","message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"warning","code":"fabric_api_bridge_version","modId":"fabric-api-base","detail":"0.4.42 vs 0.4.40","message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"error","code":"fabric_init_not_run","modId":"examplemod","message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"info","code":"launch_rebaked","bakeMisses":"4","message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-problem@1","severity":"error","code":"missing_dep","modId":"forgedemo","file":"forgedemo.jar","dependency":{"id":"fabricdemo","range":"[1.0,)"},"message":"m"}""",
            """{"type":"problem","schema":"amber-ul/runtime-pro""",   // cut off by a crash
        ]);
        var all = BakeRuntimeProblem.ReadFile(file);
        Assert.Equal(["mixin_incompatible", "mixin_semantic_risk", "fabric_api_bridged", "fabric_api_bridge_version", "fabric_init_not_run", "launch_rebaked", "missing_dep"], all.Select(p => p.Code));
        Assert.Equal(problem.Key, all[0].Key);   // the same problem from stdout and the file counts once
        Assert.Contains("onTick", all[1].Describe());
        Assert.False(all[2].IsNotable);
        Assert.Contains("0.4.42", all[3].Describe());
        Assert.True(all[4].IsError);
        Assert.True(all[5].AsksForWarm);
        Assert.Contains("4", all[5].Describe());
        Assert.Contains("fabricdemo", all[6].Describe());
        Assert.All(all, p => Assert.False(string.IsNullOrWhiteSpace(p.Describe())));
        Assert.Empty(BakeRuntimeProblem.ReadFile(Path.Combine(_root, "missing.jsonl")));
    }

    [Fact]
    public void SupportedPlatformsComeFromTheLoaderNotFromJay()
    {
        var wider = BakeLoaderInfo.Parse(JsonNode.Parse(Version13.Replace("\"forge\":[\"1.20.1\"]", "\"forge\":[\"1.20.1\",\"1.21.1\"]"))!.AsObject());
        Assert.True(BakeService.Supports(Instance("forge", "1.21.1", "52.0.1"), wider, out _));
        var today = BakeLoaderInfo.Parse(JsonNode.Parse(Version13)!.AsObject());
        Assert.False(BakeService.Supports(Instance("forge", "1.21.1", "52.0.1"), today, out var reason));
        Assert.Contains("1.20.1", reason);
        Assert.Equal(21, today.JavaMinFor("forge"));
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
    public void EmbeddedLaunchSeparatesVmMainAndGameArgumentsWithoutLeakingItsMarker()
    {
        Version("1.21.1");
        var invocation = new GameLauncher(new GameDirectory(Game)).BuildEmbeddedInvocation(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = "unused-when-embedded",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            ExtraJvmArgs = ["-Djay.test=true"],
            ExtraGameArgs = ["--demo"],
        });

        Assert.Equal("net.minecraft.client.main.Main", invocation.MainClass);
        Assert.Contains("-Djay.test=true", invocation.VmOptions);
        Assert.Contains(invocation.VmOptions, value => value.StartsWith("-Djava.class.path=", StringComparison.Ordinal));
        Assert.DoesNotContain("-cp", invocation.VmOptions);
        Assert.DoesNotContain(invocation.VmOptions, value => value.StartsWith("-Djay.embedded.marker=", StringComparison.Ordinal));
        Assert.Equal("--demo", invocation.Arguments[^1]);
        Assert.Null(invocation.TransientDirectory);
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
        // A code newer than this Jay reads like unsupported_feature, with the mod's name (the interface asks for this).
        var other = BakeService.ParseLine("""{"type":"problem","severity":"error","code":"something_new","modId":"newmod","file":"x.jar","message":"loader says so"}""")!;
        Assert.Equal("newmod: " + L.T("core.bake.problem.unsupported_feature"), BakeService.DescribeProblem(other));
        var noId = BakeService.ParseLine("""{"type":"problem","severity":"error","code":"something_new","file":"x.jar","message":"loader says so"}""")!;
        Assert.Equal("x.jar: " + L.T("core.bake.problem.unsupported_feature"), BakeService.DescribeProblem(noId));
        // A problem without a code keeps the loader's own message.
        var bare = BakeService.ParseLine("""{"type":"problem","severity":"error","file":"x.jar","message":"loader says so"}""")!;
        Assert.Equal("x.jar: loader says so", BakeService.DescribeProblem(bare));
    }

    [Theory]
    [InlineData("forge_api_missing")]
    [InlineData("quilt_api_missing")]
    public void LoaderMatrixRefusalsHaveTheirOwnText(string code)
    {
        Assert.True(BakeService.IsKnownProblem(code));
        var problem = BakeService.ParseLine($$"""{"type":"problem","severity":"error","code":"{{code}}","modId":"somemod","file":"s.jar","message":"needs net.example.Api"}""")!;
        var text = BakeService.DescribeProblem(problem);
        Assert.Equal("somemod: " + L.T("core.bake.problem." + code), text);
        Assert.NotEqual(L.T("core.bake.problem.unsupported_feature"), L.T("core.bake.problem." + code));
        // The same code arriving while the game runs reads the same way.
        var runtime = BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + problem.ToJsonString())!;
        Assert.Equal(text, runtime.Describe());
        Assert.True(runtime.IsError);
    }

    [Fact]
    public void UnresolvedLambdaMixinNamesTheModAndSaysToRemoveIt()
    {
        Assert.True(BakeService.IsKnownProblem("mixin_lambda_unresolved"));
        var problem = BakeService.ParseLine("""{"type":"problem","severity":"error","code":"mixin_lambda_unresolved","modId":"visuality","file":"visuality.jar","message":"lambda$tick$32 not found"}""")!;
        var text = BakeService.DescribeProblem(problem);
        Assert.Equal("visuality: " + L.T("core.bake.problem.mixin_lambda_unresolved"), text);
        Assert.NotEqual(L.T("core.bake.problem.unsupported_feature"), L.T("core.bake.problem.mixin_lambda_unresolved"));
        Assert.True(BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + problem.ToJsonString())!.IsError);
    }

    [Fact]
    public void ChangedMixinTargetIsAWarningWithItsOwnText()
    {
        Assert.True(BakeService.IsKnownProblem("mixin_target_changed"));
        var problem = BakeService.ParseLine("""{"type":"problem","severity":"warning","code":"mixin_target_changed","modId":"somemod","file":"s.jar","message":"@At target gone"}""")!;
        var text = BakeService.DescribeProblem(problem);
        Assert.Equal("somemod: " + L.T("core.bake.problem.mixin_target_changed"), text);
        Assert.NotEqual(L.T("core.bake.problem.mixin_lambda_unresolved"), L.T("core.bake.problem.mixin_target_changed"));
        // A known warning keeps Jay's text while the game runs too, not the loader's English message.
        var runtime = BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + problem.ToJsonString())!;
        Assert.False(runtime.IsError);
        Assert.Equal(text, runtime.Describe());
    }

    [Fact]
    public void UnknownRuntimeCodesReadLikeUnsupportedFeatureOnlyWhenTheyAreErrors()
    {
        var error = BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + """{"type":"problem","schema":"amber-ul/runtime-problem@1","code":"brand_new","severity":"error","modId":"m","message":"x"}""")!;
        Assert.Equal("m: " + L.T("core.bake.problem.unsupported_feature"), error.Describe());
        var note = BakeRuntimeProblem.Parse(BakeRuntimeProblem.Prefix + """{"type":"problem","schema":"amber-ul/runtime-problem@1","code":"brand_new","severity":"info","modId":"m","message":"just so you know"}""")!;
        Assert.Equal("m: just so you know", note.Describe());
    }

    [Fact]
    public void RuntimeMixinProblemsAreReadFromTheGameOutput()
    {
        var line = "[12:00:01] [main/INFO]: [amber-ul] 问题 mixin_incompatible (warning) 模组=sodium mixin=me.jellysquid.FooMixin 目标=net.minecraft.client.Foo 原因=target_missing";
        // Only a loader without a problems file (before interface 1.3) is read from its text lines.
        Assert.Null(BakeRuntimeProblem.Parse(line));
        var problem = BakeRuntimeProblem.Parse(line, legacy: true)!;
        Assert.Equal("mixin_incompatible", problem.Code);
        Assert.Equal("sodium", problem.Mod);
        Assert.Equal("me.jellysquid.FooMixin", problem.Mixin);
        Assert.Equal("net.minecraft.client.Foo", problem.Target);
        Assert.Contains("sodium", problem.Describe());
        Assert.Null(BakeRuntimeProblem.Parse("[amber-ul] 映射图 新建 1.21.1", legacy: true));
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
        // A sandboxed launch writes only inside the instance: the game directory is the instance the loader keeps .amber-ul in.
        Assert.Equal(instance.WorkDir, request["instanceDir"]!.GetValue<string>());
        Assert.Equal(instance.WorkDir, request["gameDir"]!.GetValue<string>());
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
    public async Task OldJavaGetsAClearMessage()
    {
        Version("1.21.1");
        var loader = VanillaLoader();
        loader.Crash["version"] = ["Error: LinkageError occurred while loading main class amber.ul.Bake",
            "	java.lang.UnsupportedClassVersionError: amber/ul/Bake has been compiled by a more recent version of the Java Runtime (class file version 65.0)"];
        // Asked for 17: refused before any process starts.
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java", 17, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Contains("21", e.Message);
        Assert.Empty(loader.Calls);
        // A hand-picked Java that only claims to be 21: the JVM error on stderr becomes the same clear message.
        e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java", 21, new GameDirectory(Game), Instance(), false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Equal(0, loader.Count("bake-instance"));
    }

    [Theory]
    [InlineData(8, 21)]
    [InlineData(17, 21)]
    [InlineData(21, 21)]
    [InlineData(25, 25)]
    public void BakeUsesAtLeastJava21(int versionMajor, int expected) => Assert.Equal(expected, Service().JavaMajorFor(Instance("forge", "1.20.1", "47.4.28"), versionMajor));

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

    // ---- Interface v1.3.2: forge-1.12.2 (stages on Java 21, game on Java 8) ----

    private const string LegacyForge = "1.12.2-14.23.5.2860";

    /// <summary>A Forge 1.12.2 install as Jay leaves it (universal jar and libraries in the game's libraries) plus a fake old-format installer.</summary>
    private void LegacyInstall(bool withLaunchWrapper = true)
    {
        Version("1.12.2");
        var profile = new JsonObject
        {
            ["install"] = new JsonObject { ["path"] = $"net.minecraftforge:forge:{LegacyForge}", ["filePath"] = $"forge-{LegacyForge}-universal.jar" },
            ["versionInfo"] = new JsonObject
            {
                ["id"] = "1.12.2-forge-14.23.5.2860",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["libraries"] = new JsonArray(
                    new JsonObject { ["name"] = $"net.minecraftforge:forge:{LegacyForge}" },
                    new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" },
                    new JsonObject { ["name"] = "org.example:server-only:1.0", ["clientreq"] = false }),
            },
        };
        var installer = Path.Combine(_root, "forge-1.12.2-installer.jar");
        using (var zip = ZipFile.Open(installer, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("install_profile.json").Open());
            w.Write(profile.ToJsonString());
        }
        FakeFiles[BakePlatform.InstallerUrl(BakePlatform.ForgeLegacy, LegacyForge)] = installer;
        var dir = new GameDirectory(Game);
        foreach (var coordinate in new[] { $"net.minecraftforge:forge:{LegacyForge}", withLaunchWrapper ? "net.minecraft:launchwrapper:1.12" : null })
        {
            if (coordinate is null) continue;
            var path = dir.LibraryPath(MavenCoordinate.Parse(coordinate).RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, coordinate);
        }
    }

    private const string Version132 = """{"type":"result","schema":"amber-ul/version-result@1","loaderVersion":"0.4.0","protocol":1,"interface":"1.3","interfaceRevision":"1.3.2","supportedMc":["1.12.2","1.21.1"],"platforms":{"vanilla":["1.21.1"],"forge-1.12.2":["1.12.2"]},"javaMin":{"vanilla":21,"forge-1.12.2":21},"javaLaunch":{"forge-1.12.2":{"min":8,"max":8}},"javaRuntime":21}""";

    /// <summary>A loader that knows forge-1.12.2: its bake-instance answers like LegacyPlatform (no cache list, transformer cache, Java 8 launch).</summary>
    private FakeLoader LegacyLoader(string? transformerCache = null)
    {
        var loader = new FakeLoader();
        loader.Stages["version"] = _ => ([Version132], 0);
        loader.Stages["requirements"] = _ => (["""{"type":"result","schema":"amber-ul/requirements-result@1","files":[],"javaMin":21,"javaLaunch":{"min":8,"max":8}}"""], 0);
        loader.Stages["bake-instance"] = request =>
        {
            var state = Path.Combine(request!["instanceDir"]!.GetValue<string>(), BakeService.StateFolder, "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            File.WriteAllText(state, new JsonObject { ["schema"] = "amber-ul/state@1", ["platform"] = new JsonObject { ["kind"] = "forge-1.12.2" } }.ToJsonString());
            var cache = transformerCache ?? Path.Combine(request["instanceDir"]!.GetValue<string>(), BakeService.StateFolder, "legacy-1.12.2", "0123456789abcdef");
            var result = new JsonObject
            {
                ["type"] = "result",
                ["stateKey"] = "legacy",
                ["launch"] = new JsonObject
                {
                    ["mainClass"] = "amber.ul.legacy.LegacyLaunch",
                    ["classpath"] = new JsonArray("fmlbridge-1.12.2.jar"),
                    ["jvmArgs"] = new JsonArray("-Damber.ul.legacy.cache=" + cache),
                    ["gameArgsPrefix"] = new JsonArray(),
                    ["javaMin"] = 8,
                    ["javaMax"] = 8,
                    ["modsDir"] = "mods",
                    ["stateFile"] = state,
                },
                ["cache"] = new JsonObject { ["state"] = "new", ["warm"] = false, ["transformerCache"] = cache },
            };
            return ([result.ToJsonString()], 0);
        };
        loader.Stages["warm"] = _ => (["""{"type":"error","code":"not_supported","message":"warm does not apply to forge-1.12.2"}"""], 1);
        return loader;
    }

    [Fact]
    public void Forge1122IsItsOwnPlatformOnJava8()
    {
        Assert.Equal(BakePlatform.ForgeLegacy, BakePlatform.KindOf(Instance("forge", "1.12.2", LegacyForge, "f1122")));
        Assert.Equal(BakePlatform.Forge, BakePlatform.KindOf(Instance("forge", "1.20.1", "47.4.28")));
        Assert.Null(BakePlatform.KindOf(Instance("optifine", "1.12.2", "HD_U_G5", "o1122")));
        Assert.Equal(L.T("core.bake.platform.forge_legacy"), BakePlatform.DisplayName(BakePlatform.ForgeLegacy));
        Assert.Equal(new JavaRange(8, 8), BakePlatform.KnownJavaLaunch(BakePlatform.ForgeLegacy));
        Assert.Null(BakePlatform.KnownJavaLaunch(BakePlatform.Forge));

        // A build folder (no manifest): Jay's own knowledge. Game on 8 whatever the version asks, stages on 21.
        var bake = Service();
        var legacy = Instance("forge", "1.12.2", LegacyForge, "f1122");
        Assert.Equal(8, bake.JavaMajorFor(legacy, 8));
        Assert.Equal(8, bake.JavaMajorFor(legacy, 17));
        Assert.Equal(21, bake.ToolJavaMajorFor(legacy, 8));
        // Other platforms are unchanged: one Java for both.
        Assert.Equal(21, bake.JavaMajorFor(Instance(), 17));
        Assert.Equal(25, bake.ToolJavaMajorFor(Instance(), 25));
        Assert.Equal(21, bake.ToolJavaMajorFor(Instance(), 21));
    }

    [Fact]
    public void JavaLaunchComesFromThePackageManifest()
    {
        var root = Package(javaMin: """{"vanilla":21,"forge-1.12.2":21}""", tweak: m =>
            m["javaLaunch"] = JsonNode.Parse("""{"forge-1.12.2":{"min":8,"max":11},"bad":{"min":9,"max":8},"vanilla":"x"}"""));
        var toolkit = BakeToolkit.Find(root)!;
        Assert.Equal(new JavaRange(8, 11), toolkit.JavaLaunchFor(BakePlatform.ForgeLegacy));
        Assert.Null(toolkit.JavaLaunchFor("bad"));
        Assert.Null(toolkit.JavaLaunchFor(BakePlatform.Vanilla));
        var bake = PackagedService(root, new FakeLoader());
        var legacy = Instance("forge", "1.12.2", LegacyForge, "f1122");
        Assert.Equal(8, bake.JavaMajorFor(legacy, 8));
        Assert.Equal(11, bake.JavaMajorFor(legacy, 17));

        var info = BakeLoaderInfo.Parse(JsonNode.Parse(Version132)!.AsObject());
        Assert.Equal(new JavaRange(8, 8), info.JavaLaunch[BakePlatform.ForgeLegacy]);
        Assert.Equal(21, info.JavaMinFor(BakePlatform.ForgeLegacy));
    }

    [Fact]
    public async Task Forge1122BakesOnTheLoadersJavaAndLaunchesOnJava8()
    {
        LegacyInstall();
        var dir = new GameDirectory(Game);
        var instance = Instance("forge", "1.12.2", LegacyForge, "f1122");
        var loader = LegacyLoader();
        var downloads = new List<DownloadItem>();
        var bake = Service(loader, downloads);

        // The instance's Java (unknown version here: the path does not exist) is kept for the game; the stages get Java 21.
        var asked = new List<int>();
        var (java, gamePath) = await bake.ChooseJavaAsync(instance, 8, "instance-java", (major, _) => { asked.Add(major); return Task.FromResult("java" + major); }, CancellationToken.None);
        Assert.Equal("instance-java", gamePath);
        Assert.Equal(new BakeJava("java21", 21, 8), java);
        Assert.Equal([21], asked);

        var done = await bake.PrepareAsync(java, dir, instance, warm: true, null, CancellationToken.None);
        var launch = done.Launch!;
        Assert.All(loader.JavaPaths, p => Assert.Equal("java21", p.Java));
        Assert.Equal(0, loader.Count("warm"));   // nothing to warm: the transformer cache fills while the game runs
        Assert.Equal("forge-1.12.2", loader.Last("requirements")["platform"]!.GetValue<string>());
        var request = loader.Last("bake-instance");
        Assert.Equal(8, request["javaMajor"]!.GetValue<int>());
        Assert.Equal(dir.LibrariesDir, request["libRoot"]!.GetValue<string>());
        Assert.Contains(downloads, d => d.Url.EndsWith($"forge-{LegacyForge}-installer.jar"));

        // The platform folder: Forge's version.json without server-only libraries, and libraries linked, not copied.
        var installDir = request["platform"]!["installDir"]!.GetValue<string>();
        Assert.Equal("forge-1.12.2", request["platform"]!["kind"]!.GetValue<string>());
        var versionJson = JsonNode.Parse(File.ReadAllText(Path.Combine(installDir, "version.json")))!.AsObject();
        Assert.Equal("net.minecraft.launchwrapper.Launch", versionJson["mainClass"]!.GetValue<string>());
        Assert.Equal(2, versionJson["libraries"]!.AsArray().Count);
        Assert.NotNull(new DirectoryInfo(Path.Combine(installDir, "libraries")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(installDir, "libraries", "net", "minecraft", "launchwrapper", "1.12", "launchwrapper-1.12.jar")));

        // The launch block: Java 8 exactly, and the transformer cache is inside the instance (the shared cache stays read-only).
        Assert.Equal(8, launch.JavaMin);
        Assert.Equal(8, launch.JavaMax);
        Assert.Equal(Path.Combine(instance.WorkDir, BakeService.StateFolder, "legacy-1.12.2", "0123456789abcdef"), launch.WritableCache);
        // Forge 1.12.2's update check is off.
        Assert.Contains("B:Global=false", File.ReadAllText(Path.Combine(instance.WorkDir, "config", "forge.cfg")));

        // Unchanged inputs: the record is reused, with its Java range.
        var again = await bake.PrepareAsync(java, dir, instance, warm: false, null, CancellationToken.None);
        Assert.True(again.Reused);
        Assert.Equal(8, again.Launch!.JavaMax);
        Assert.Equal(launch.WritableCache, again.Launch.WritableCache);
        Assert.Equal(1, loader.Count("bake-instance"));

        // The record is in the instance folder, which the game may write: a cache path outside the instance is dropped.
        var recordPath = BakeRecord.PathFor(instance.WorkDir);
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
        record["writableCache"] = Path.Combine(Cache, "baked");
        File.WriteAllText(recordPath, record.ToJsonString());
        var tampered = await bake.PrepareAsync(java, dir, instance, warm: false, null, CancellationToken.None);
        Assert.True(tampered.Reused);
        Assert.Null(tampered.Launch!.WritableCache);
    }

    [Theory]
    [InlineData(17, "java_too_new")]
    [InlineData(21, "java_too_new")]
    [InlineData(7, "java_too_old")]
    public async Task Forge1122RefusesAGameJavaOutsideJava8BeforeAnyProcess(int gameMajor, string code)
    {
        LegacyInstall();
        var loader = LegacyLoader();
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync(new BakeJava("java21", 21, gameMajor),
            new GameDirectory(Game), Instance("forge", "1.12.2", LegacyForge, "f1122"), false, null, CancellationToken.None));
        Assert.Equal(code, e.Code);
        Assert.Empty(loader.Calls);
        if (code == "java_too_new") Assert.Equal(L.F("core.bake.java_too_new", 8, gameMajor), e.Message);
    }

    [Fact]
    public async Task Forge1122StagesStillNeedTheLoadersJava()
    {
        LegacyInstall();
        var loader = LegacyLoader();
        // Running the stages on Java 8 would fail with UnsupportedClassVersionError: refused first.
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync("java8", 8,
            new GameDirectory(Game), Instance("forge", "1.12.2", LegacyForge, "f1122"), false, null, CancellationToken.None));
        Assert.Equal("java_too_old", e.Code);
        Assert.Empty(loader.Calls);
    }

    [Fact]
    public async Task Forge1122PlatformNamesMissingLibrariesAndAcceptsAShortVersion()
    {
        LegacyInstall(withLaunchWrapper: false);
        Func<IReadOnlyCollection<DownloadItem>, CancellationToken, Task> download = (items, _) =>
        {
            foreach (var item in items) { Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!); File.Copy(FakeFiles[item.Url], item.LocalPath, true); }
            return Task.CompletedTask;
        };
        // An instance that knows only the build number still finds the 1.12.2-… installer.
        var e = await Assert.ThrowsAsync<BakeException>(() => BakePlatform.PrepareAsync(new GameDirectory(Game),
            Instance("forge", "1.12.2", "14.23.5.2860", "f1122"), Data, download, null, CancellationToken.None));
        Assert.Contains("launchwrapper-1.12.jar", e.Message);
        Assert.DoesNotContain("server-only", e.Message);
    }

    [Fact]
    public async Task ATransformerCacheOutsideTheInstanceIsDropped()
    {
        LegacyInstall();
        // A loader that still points at the shared cache: not kept, so nothing outside the instance is created or written.
        var loader = LegacyLoader(transformerCache: Path.Combine(Cache, "legacy-1.12.2", "0123456789abcdef"));
        var done = await Service(loader).PrepareAsync(new BakeJava("java21", 21, 8), new GameDirectory(Game),
            Instance("forge", "1.12.2", LegacyForge, "f1122"), false, null, CancellationToken.None);
        Assert.Null(done.Launch!.WritableCache);

        // Only a folder inside <instance>/.amber-ul/: not the shared cache, not .amber-ul itself, not another
        // instance; relative or escaping paths are refused.
        var work = Path.Combine(Game, "versions", "f1122");
        var other = Path.Combine(Game, "versions", "other");
        var launch = new BakedLaunch("m", [], [], [], "s", "k");
        string? Check(string path) => BakeService.Checked(launch with { WritableCache = path }, work).WritableCache;
        Assert.Null(Check(Cache));
        Assert.Null(Check(Path.Combine(Cache, "legacy-1.12.2", "x")));
        Assert.Null(Check(Path.Combine(work, BakeService.StateFolder)));
        Assert.Null(Check(work));
        Assert.Null(Check(Path.Combine(other, BakeService.StateFolder, "legacy-1.12.2", "x")));
        Assert.Null(Check(Path.Combine(work, BakeService.StateFolder, "..", "..", "other")));
        Assert.Null(Check(Path.Combine(BakeService.StateFolder, "legacy-1.12.2", "x")));
        var inside = Path.Combine(work, BakeService.StateFolder, "legacy-1.12.2", "x");
        Assert.Equal(inside, Check(inside));
    }

    [Fact]
    public async Task CleanupKeepsForge1122StatesWithoutWarmingThem()
    {
        LegacyInstall();
        var loader = LegacyLoader();
        JsonObject? gc = null;
        loader.Stages["gc"] = request => { gc = request; return (["""{"type":"result","apply":false,"liveStates":1,"removable":0,"removableBytes":0}"""], 0); };
        var bake = Service(loader);
        var instance = Instance("forge", "1.12.2", LegacyForge, "f1122");
        var state = (await bake.PrepareAsync(new BakeJava("java21", 21, 8), new GameDirectory(Game), instance, false, null, CancellationToken.None)).Launch!.StateFile;
        Directory.CreateDirectory(Path.Combine(Cache, "baked"));

        var result = await bake.CollectGarbageAsync("java21", apply: false, null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(0, loader.Count("warm"));
        Assert.Contains(Path.GetFullPath(state), gc!["liveStateFiles"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.True(File.Exists(BakeRecord.PathFor(instance.WorkDir)));
    }

    [Fact]
    public void Forge1122ProblemCodesReadInJaysWords()
    {
        var missing = JsonNode.Parse("""{"type":"problem","code":"missing_dependency","severity":"error","modId":"jei","dependency":{"id":"baubles","range":"[1.5,)"}}""")!.AsObject();
        var old = JsonNode.Parse("""{"type":"problem","code":"missing_dep","severity":"error","modId":"jei","dependency":{"id":"baubles","range":"[1.5,)"}}""")!.AsObject();
        Assert.Equal(BakeService.DescribeProblem(old), BakeService.DescribeProblem(missing));
        Assert.StartsWith("jei: " + L.T("core.bake.problem.missing_dep"), BakeService.DescribeProblem(missing));
        Assert.True(BakeService.IsKnownProblem("bad_metadata"));
        Assert.True(BakeService.IsKnownProblem("platform_unavailable"));
        var bad = JsonNode.Parse("""{"type":"problem","code":"bad_metadata","severity":"error","file":"broken.jar"}""")!.AsObject();
        Assert.Equal("broken.jar: " + L.T("core.bake.problem.bad_metadata"), BakeService.DescribeProblem(bad));
    }

    [Fact]
    public void Forge1122RuntimeProblemsReadInJaysWords()
    {
        // What LegacyLaunch reports before the game takes over (loader 0.4.0), as lines of the problems file.
        var path = Path.Combine(_root, "runtime-problems.jsonl");
        File.WriteAllLines(path,
        [
            """{"type":"launch","loaderVersion":"0.4.0"}""",
            """{"type":"problem","code":"legacy_java_unsupported","severity":"error","javaVersion":"17","required":"8"}""",
            """{"type":"problem","code":"legacy_launchwrapper_missing","severity":"error"}""",
            """{"type":"problem","code":"legacy_launch_failed","severity":"error","exception":"java.lang.RuntimeException","rootCause":"java.lang.NoClassDefFoundError: x","log":"logs/latest.log"}""",
            """{"type":"problem","code":"legacy_launch_failed","severity":"error","exception":"java.lang.RuntimeException"}""",
        ]);
        var problems = BakeRuntimeProblem.ReadFile(path);
        Assert.Equal(4, problems.Count);
        Assert.All(problems, p => Assert.True(p.IsError));
        Assert.Equal(L.F("core.bake.runtime.legacy_java", "17"), problems[0].Describe());
        Assert.Equal(L.T("core.bake.runtime.legacy_launchwrapper"), problems[1].Describe());
        Assert.Equal(L.F("core.bake.runtime.legacy_failed", "java.lang.NoClassDefFoundError: x"), problems[2].Describe());
        // Without a root cause (LaunchWrapper caught it): the exception, and the game log has the rest.
        Assert.Equal(L.F("core.bake.runtime.legacy_failed", "java.lang.RuntimeException"), problems[3].Describe());
    }

    [Fact]
    public async Task ABadPlatformErrorReadsInJaysWords()
    {
        LegacyInstall();
        var loader = LegacyLoader();
        loader.Stages["bake-instance"] = _ => (["""{"type":"error","code":"bad_platform","message":"Forge libraries missing: x.jar"}"""], 1);
        var e = await Assert.ThrowsAsync<BakeException>(() => Service(loader).PrepareAsync(new BakeJava("java21", 21, 8), new GameDirectory(Game),
            Instance("forge", "1.12.2", LegacyForge, "f1122"), false, null, CancellationToken.None));
        Assert.Equal("bad_platform", e.Code);
        Assert.Equal(L.F("core.bake.bad_platform", "Forge libraries missing: x.jar"), e.Message);
    }

    [Fact]
    public void LegacyForgeUpdateCheckIsTurnedOff()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "legacy-work")).FullName;
        var cfg = Path.Combine(work, "config", "forge.cfg");
        BakePlatform.DisableLegacyForgeUpdateCheck(work);
        Assert.Equal("version_checking {\n    B:Global=false\n}\n", File.ReadAllText(cfg));

        // Forge's own file: the value is switched, everything else stays.
        File.WriteAllText(cfg, "general {\r\n    B:disableVersionCheck=false\r\n}\r\n\r\nversion_checking {\r\n    # Enable the entire mod update check system.\r\n    B:Global=true\r\n}\r\n");
        BakePlatform.DisableLegacyForgeUpdateCheck(work);
        var text = File.ReadAllText(cfg);
        Assert.Contains("    B:Global=false\r\n", text);
        Assert.Contains("B:disableVersionCheck=false", text);
        Assert.Contains("# Enable the entire mod update check system.", text);

        // The category without the value: added inside it.
        File.WriteAllText(cfg, "version_checking {\n    B:other=true\n}\n");
        BakePlatform.DisableLegacyForgeUpdateCheck(work);
        Assert.Equal("version_checking {\n    B:Global=false\n    B:other=true\n}\n", File.ReadAllText(cfg));
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
