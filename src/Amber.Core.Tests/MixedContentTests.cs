using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Amber.App.ViewModels;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Install;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>Mixed packs through the universal loader: what an instance accepts, search, files, dependencies, packs, runtime folder.</summary>
public sealed class MixedContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-mixed-" + Guid.NewGuid().ToString("N"));

    public MixedContentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> Platforms = new()
    {
        ["vanilla"] = ["1.21.1"],
        ["neoforge"] = ["1.21.1"],
        ["forge"] = ["1.20.1"],
    };

    private static CrossLoader.Capabilities Caps(params string[] bridges) => new(Platforms, bridges);

    private static readonly CrossLoader.Capabilities Full = CrossLoader.Capabilities.PinnedRelease;

    [Theory]
    [InlineData("neoforge", "1.21.1", "fabric,quilt,forge")]
    [InlineData("forge", "1.20.1", "fabric,quilt,neoforge")]
    [InlineData("fabric", "1.21.1", "quilt")]
    [InlineData("quilt", "1.21.1", "")]
    [InlineData("neoforge", "1.20.1", "")]
    [InlineData("forge", "1.21.1", "")]
    [InlineData("fabric", "1.20.1", "")]
    [InlineData("optifine", "1.21.1", "")]
    public void ExtrasFollowThePinnedPackage(string loader, string mc, string expected) =>
        Assert.Equal(expected, string.Join(",", CrossLoader.Extras(Full, loader, mc)));

    /// <summary>
    /// Loader 0.4.0 lists Forge 1.12.2 as its own platform, not under "forge": whatever asks "does the universal loader
    /// run Forge on this version" (the new-instance "Jay loader" choice) must map forge + 1.12.2 to it.
    /// </summary>
    [Fact]
    public void PinnedCapsListForge1122AsItsOwnPlatform()
    {
        Assert.Equal(["1.12.2"], Full.Platforms[BakePlatform.ForgeLegacy]);
        Assert.DoesNotContain("1.12.2", Full.Platforms[BakePlatform.Forge]);
        Assert.Equal(BakePlatform.ForgeLegacy, CrossLoader.PlatformOf("forge", "1.12.2"));
        Assert.Equal(BakePlatform.Forge, CrossLoader.PlatformOf("forge", "1.20.1"));
        Assert.Equal(BakePlatform.Forge, CrossLoader.PlatformOf("forge"));
        Assert.Empty(CrossLoader.Extras(Full, "forge", "1.12.2"));
        Assert.Empty(CrossLoader.MissingBridges(Full, "forge", "1.12.2"));
    }

    [Fact]
    public void ExtrasNeedTheBridges()
    {
        Assert.Equal(["fabric", "quilt"], CrossLoader.Extras(Caps("fabricbridge.jar"), "neoforge", "1.21.1"));
        Assert.Equal(["forge"], CrossLoader.Extras(Caps("forgecompat.jar"), "neoforge", "1.21.1"));
        Assert.Empty(CrossLoader.Extras(Caps(), "neoforge", "1.21.1"));
        // NeoForge 1.20.1 mods are read as Forge mods: no bridge needed.
        Assert.Equal(["neoforge"], CrossLoader.Extras(Caps(), "forge", "1.20.1"));
        Assert.Empty(CrossLoader.Extras(null, "neoforge", "1.21.1"));
        Assert.Empty(CrossLoader.Extras(Full, null, "1.21.1"));
    }

    [Fact]
    public void AcceptsTableWinsOverTheBridges()
    {
        var perVersion = CrossLoader.AcceptTable.Parse(JsonNode.Parse("""{"neoforge":{"1.21.1":["NeoForge","fabric"]},"forge":["forge"]}"""));
        var caps = Full with { Accepts = perVersion };
        Assert.Equal(["fabric"], CrossLoader.Extras(caps, "neoforge", "1.21.1"));
        Assert.Empty(CrossLoader.Extras(caps, "forge", "1.20.1"));
        // A platform the table leaves out falls back to the bridges.
        Assert.Equal(["quilt"], CrossLoader.Extras(caps, "fabric", "1.21.1"));
        // The game version must still be one the platform lists.
        Assert.Empty(CrossLoader.Extras(caps, "neoforge", "1.20.1"));
        Assert.Null(CrossLoader.AcceptTable.Parse(null));
    }

    /// <summary>The loader's own form (interface revision 1.3.1), as <c>version</c> and the manifest print it.</summary>
    private const string FullAccepts = """
        [{"platform":"vanilla","mc":"1.21.1","available":true,"accepts":["fabric","quilt"],"missingBridges":[]},
         {"platform":"neoforge","mc":"1.21.1","available":true,"accepts":["neoforge","fabric","quilt","forge"],"missingBridges":[]},
         {"platform":"forge","mc":"1.20.1","available":true,"accepts":["forge","neoforge","fabric","quilt"],"missingBridges":[]}]
        """;

    /// <summary>A core-only package: no bridges, so only the vanilla base runs.</summary>
    private const string CoreOnlyAccepts = """
        [{"platform":"vanilla","mc":"1.21.1","available":true,"accepts":["fabric","quilt"],"missingBridges":[]},
         {"platform":"neoforge","mc":"1.21.1","available":false,"accepts":[],"missingBridges":["fmlbridge.jar","fabricbridge.jar","forgecompat.jar"]},
         {"platform":"forge","mc":"1.20.1","available":false,"accepts":[],"missingBridges":["fmlbridge-forge.jar","fabricbridge-forge.jar"]}]
        """;

    [Fact]
    public void AcceptsArrayFromTheLoader()
    {
        // Bridges say nothing here: the table alone decides.
        var full = Caps() with { Accepts = CrossLoader.AcceptTable.Parse(JsonNode.Parse(FullAccepts)) };
        Assert.Equal(["fabric", "quilt", "forge"], CrossLoader.Extras(full, "neoforge", "1.21.1"));
        Assert.Equal(["fabric", "quilt", "neoforge"], CrossLoader.Extras(full, "forge", "1.20.1"));
        Assert.Equal(["quilt"], CrossLoader.Extras(full, "fabric", "1.21.1"));
        Assert.Empty(CrossLoader.MissingBridges(full, "neoforge", "1.21.1"));

        // Even with every bridge jar present, available:false wins.
        var coreOnly = Full with { Accepts = CrossLoader.AcceptTable.Parse(JsonNode.Parse(CoreOnlyAccepts)) };
        Assert.Empty(CrossLoader.Extras(coreOnly, "neoforge", "1.21.1"));
        Assert.Empty(CrossLoader.Extras(coreOnly, "forge", "1.20.1"));
        Assert.Equal(["quilt"], CrossLoader.Extras(coreOnly, "fabric", "1.21.1"));
        Assert.Equal(["fmlbridge.jar", "fabricbridge.jar", "forgecompat.jar"], CrossLoader.MissingBridges(coreOnly, "neoforge", "1.21.1"));
        Assert.Equal(["fmlbridge-forge.jar", "fabricbridge-forge.jar"], CrossLoader.MissingBridges(coreOnly, "forge", "1.20.1"));
        Assert.Empty(CrossLoader.MissingBridges(coreOnly, "forge", "1.21.1"));
        Assert.Empty(CrossLoader.MissingBridges(null, "neoforge", "1.21.1"));

        // A mixed pack on a core-only package is refused.
        var info = new ModpackInfo("Modrinth", "M", null, "1.21.1", LoaderKind.NeoForge, "21.1.1", 0) { ExtraLoaders = [LoaderKind.Fabric] };
        Assert.NotNull(ModpackService.MixedRefusal(info, CrossLoader.Extras(coreOnly, "neoforge", "1.21.1"), bakeReady: true));
    }

    [Fact]
    public void EcosystemPrefersNativeBuilds()
    {
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        Assert.Equal("neoforge", CrossLoader.EcosystemOf("neoforge", ["fabric", "neoforge"], extras));
        Assert.Equal("fabric", CrossLoader.EcosystemOf("neoforge", ["quilt", "fabric"], extras));
        Assert.Equal("forge", CrossLoader.EcosystemOf("neoforge", ["forge"], extras));
        Assert.Null(CrossLoader.EcosystemOf("fabric", ["forge"], CrossLoader.Extras(Full, "fabric", "1.21.1")));
        Assert.True(CrossLoader.IsCross("neoforge", ["fabric"], extras));
        Assert.False(CrossLoader.IsCross("neoforge", ["neoforge", "fabric"], extras));
        Assert.False(CrossLoader.IsCross("quilt", ["fabric"], []));
        Assert.False(CrossLoader.IsCross("neoforge", ["fabric"], []));
    }

    [Fact]
    public void DependenciesComeFromTheModsOwnEcosystemFirst()
    {
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        Assert.Equal(["fabric", "neoforge", "quilt", "forge"], CrossLoader.DependencyOrder("neoforge", "fabric", extras));
        Assert.Equal(["quilt", "fabric", "neoforge", "forge"], CrossLoader.DependencyOrder("neoforge", "quilt", extras));
        Assert.Equal(["forge", "neoforge", "fabric", "quilt"], CrossLoader.DependencyOrder("neoforge", "forge", extras));
    }

    private static ContentFile File(string id, params string[] loaders) =>
        new(ContentSource.Modrinth, "p", id, id, id + ".jar", "https://cdn.modrinth.com/" + id, null, 1, ["1.21.1"], loaders, "release", DateTimeOffset.UnixEpoch, []);

    [Fact]
    public void UsableFilesListNativeFirstAndDropTheRest()
    {
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        var files = CrossLoader.Usable([File("fab", "fabric"), File("nf", "neoforge"), File("liteloader", "liteloader"), File("forge", "forge")], "neoforge", extras);
        Assert.Equal(["nf", "fab", "forge"], files.Select(f => f.FileId));
    }

    [Fact]
    public void CrossDependencyOrderOnlyForCrossFiles()
    {
        var instance = new GameInstance("NF", "1.21.1", "neoforge", "21.1.1", "");
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        Assert.Equal(["fabric", "neoforge", "quilt", "forge"], ContentViewModel.CrossDependencyOrder(File("a", "fabric"), instance, extras));
        Assert.Null(ContentViewModel.CrossDependencyOrder(File("b", "neoforge"), instance, extras));
        Assert.Null(ContentViewModel.CrossDependencyOrder(File("c", "fabric"), instance, []));
    }

    [Fact]
    public async Task DependenciesOfAFabricModOnNeoForgeResolveToFabricBuilds()
    {
        var provider = new FakeProvider();
        provider.Files["fabric-api"] = [File("fapi-fabric", "fabric")];
        provider.Files["ffapi"] = [File("ffapi-neo", "neoforge")];
        var installer = new ContentInstaller(new Net.Downloader(new HttpClient(), Net.SourceChain.Default()), new Dictionary<ContentSource, IContentProvider> { [ContentSource.Modrinth] = provider });
        var mod = File("mod", "fabric") with { Dependencies = [new ContentDependency(ContentSource.Modrinth, "fabric-api", null, DependencyKind.Required)] };
        var instance = new GameInstance("NF", "1.21.1", "neoforge", "21.1.1", "");
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        var plan = await installer.ResolveAsync(mod, instance, loaderPreference: ContentViewModel.CrossDependencyOrder(mod, instance, extras));
        Assert.Equal(["mod", "fapi-fabric"], plan.Files.Select(f => f.FileId));
        Assert.Empty(plan.Problems);
        // Without the order (an ordinary install) the NeoForge lookup finds nothing for Fabric API.
        var plain = await installer.ResolveAsync(mod, instance);
        Assert.Single(plain.Files);
        Assert.Single(plain.Problems);
    }

    private sealed class FakeProvider : IContentProvider
    {
        public Dictionary<string, List<ContentFile>> Files { get; } = [];
        public ContentSource Source => ContentSource.Modrinth;
        public Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentProject> GetProjectAsync(string projectId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ContentFile>> GetFilesAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContentFile>>(Files.GetValueOrDefault(projectId, []).Where(f => loader is null || f.Loaders.Contains(loader)).Select(f => f with { ProjectId = projectId }).ToList());

        public Task<ContentFile> GetFileAsync(string projectId, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ---- Search ----

    private sealed class Capture(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task ModrinthSearchesExtraLoadersAndReadsHitLoaders()
    {
        var handler = new Capture("""{"hits":[{"project_id":"a","title":"A","categories":["fabric","utility","quilt"]},{"project_id":"b","title":"B","categories":["neoforge"]}],"total_hits":2}""");
        var provider = new ModrinthProvider(new HttpClient(handler));
        var result = await provider.SearchAsync(new SearchQuery(ContentType.Mod, "x", "1.21.1", "neoforge") { ExtraLoaders = ["fabric", "quilt"] });
        var facets = Uri.UnescapeDataString(handler.Requests[0].Query);
        Assert.Contains("""["categories:neoforge","categories:fabric","categories:quilt"]""", facets);
        Assert.Equal(["fabric", "quilt"], result.Items[0].Loaders);
        Assert.Equal(["neoforge"], result.Items[1].Loaders);

        // Without extras the facet is unchanged.
        await provider.SearchAsync(new SearchQuery(ContentType.Mod, "x", "1.21.1", "neoforge"));
        Assert.Contains("""["categories:neoforge"]""", Uri.UnescapeDataString(handler.Requests[1].Query));
    }

    [Fact]
    public async Task CurseForgeSearchesExtraLoadersAndReadsIndexedLoaders()
    {
        var handler = new Capture("""{"data":[{"id":1,"name":"A","latestFilesIndexes":[{"gameVersion":"1.21.1","modLoader":4},{"gameVersion":"1.20.1","modLoader":1},{"gameVersion":"1.21.1","modLoader":6}]}],"pagination":{"totalCount":1}}""");
        var provider = new CurseForgeProvider(new HttpClient(handler), () => "test-key");
        var result = await provider.SearchAsync(new SearchQuery(ContentType.Mod, "x", "1.21.1", "neoforge") { ExtraLoaders = ["fabric", "neoforge"] });
        var query = Uri.UnescapeDataString(handler.Requests[0].Query);
        Assert.Contains("modLoaderTypes=[6,4]", query);
        Assert.DoesNotContain("modLoaderType=", query);
        Assert.Equal(["fabric", "neoforge"], result.Items[0].Loaders);

        await provider.SearchAsync(new SearchQuery(ContentType.Mod, "x", "1.21.1", "neoforge"));
        Assert.Contains("modLoaderType=6", Uri.UnescapeDataString(handler.Requests[1].Query));
    }

    // ---- Packs ----

    private string Zip(string name, string entry, string text)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write(text);
        return path;
    }

    [Fact]
    public void MrpackWithFabricAndNeoForgeInstallsNeoForgeAsTheBase()
    {
        var info = ModpackService.Inspect(Zip("m.mrpack", "modrinth.index.json",
            """{"formatVersion":1,"game":"minecraft","name":"M","files":[],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.5","neoforge":"21.1.1"}}"""));
        Assert.Equal(LoaderKind.NeoForge, info.Loader);
        Assert.Equal("21.1.1", info.LoaderVersion);
        Assert.Equal([LoaderKind.Fabric], info.ExtraLoaders);
        Assert.True(info.IsMixed);

        var plain = ModpackService.Inspect(Zip("p.mrpack", "modrinth.index.json",
            """{"formatVersion":1,"game":"minecraft","name":"P","files":[],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.5"}}"""));
        Assert.Equal(LoaderKind.Fabric, plain.Loader);
        Assert.False(plain.IsMixed);
    }

    [Fact]
    public void CurseForgePackWithTwoLoadersIsMixed()
    {
        var info = ModpackService.Inspect(Zip("c.zip", "manifest.json",
            """{"manifestType":"minecraftModpack","name":"C","version":"1","minecraft":{"version":"1.20.1","modLoaders":[{"id":"fabric-0.16.5","primary":true},{"id":"forge-47.2.0"},{"id":"liteloader-1"}]},"files":[]}"""));
        Assert.Equal(LoaderKind.Forge, info.Loader);
        Assert.Equal([LoaderKind.Fabric], info.ExtraLoaders);

        var plain = ModpackService.Inspect(Zip("d.zip", "manifest.json",
            """{"manifestType":"minecraftModpack","name":"D","version":"1","minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}]},"files":[]}"""));
        Assert.Equal(LoaderKind.Forge, plain.Loader);
        Assert.False(plain.IsMixed);
    }

    [Fact]
    public void MixedPackImportsOnlyWhenTheLoaderRunsTheMix()
    {
        var info = new ModpackInfo("Modrinth", "M", null, "1.21.1", LoaderKind.NeoForge, "21.1.1", 0) { ExtraLoaders = [LoaderKind.Fabric] };
        var extras = CrossLoader.Extras(Full, "neoforge", "1.21.1");
        Assert.Null(ModpackService.MixedRefusal(info, extras, bakeReady: true));
        Assert.NotNull(ModpackService.MixedRefusal(info, extras, bakeReady: false));
        Assert.NotNull(ModpackService.MixedRefusal(info, [], bakeReady: true));
        var onOld = info with { McVersion = "1.20.4" };
        Assert.NotNull(ModpackService.MixedRefusal(onOld, CrossLoader.Extras(Full, "neoforge", "1.20.4"), bakeReady: true));
        Assert.Null(ModpackService.MixedRefusal(info with { ExtraLoaders = [] }, [], bakeReady: false));
        Assert.Equal("NeoForge + Fabric", ModpackService.MixedLoaders(info));
    }

    // ---- Loader package and runtime folder ----

    [Fact]
    public void PackageReadsBridgesAndAccepts()
    {
        var root = Path.Combine(_root, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "bridges"));
        System.IO.File.WriteAllText(Path.Combine(root, "loader.jar"), "l");
        System.IO.File.WriteAllText(Path.Combine(root, "bridges", "fabricbridge.jar"), "f");
        var files = new JsonObject();
        foreach (var name in new[] { "loader.jar", "bridges/fabricbridge.jar" })
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            files[name] = new JsonObject { ["sha256"] = BakePackage.Sha256(path), ["size"] = new FileInfo(path).Length };
        }
        var manifest = new JsonObject
        {
            ["schema"] = BakePackage.Schema,
            ["loaderVersion"] = "0.3.1",
            ["protocol"] = 1,
            ["interface"] = "1.3.1",
            ["platforms"] = JsonNode.Parse("""{"neoforge":["1.21.1"]}"""),
            ["classpath"] = new JsonArray("loader.jar"),
            // forgecompat.jar is named but not shipped: it is not counted.
            ["bridges"] = JsonNode.Parse("""{"fabricbridge.jar":"bridges/fabricbridge.jar","forgecompat.jar":"bridges/forgecompat.jar"}"""),
            ["accepts"] = JsonNode.Parse("""{"neoforge":{"1.21.1":["neoforge","fabric","quilt"]}}"""),
            ["files"] = files,
        };
        System.IO.File.WriteAllText(Path.Combine(root, BakePackage.ManifestName), manifest.ToJsonString());
        var package = BakePackage.Read(root);
        Assert.Equal(["fabricbridge.jar"], package.Bridges);
        var caps = CrossLoader.Capabilities.Of(package);
        Assert.Equal(["fabric", "quilt"], CrossLoader.Extras(caps, "neoforge", "1.21.1"));
        Assert.Equal(["fabric", "quilt"], CrossLoader.Extras(caps with { Accepts = null }, "neoforge", "1.21.1"));
    }

    [Fact]
    public void RuntimeFolderSitsBesideTheDataFolder()
    {
        var data = Path.Combine(_root, "Amber");
        var runtime = BakeRuntime.For(data);
        Assert.Equal(data + "-runtime", runtime.Root);
        foreach (var grant in runtime.SandboxGrants)
            Assert.False(grant.Path.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), grant.Path);
        Assert.Contains(runtime.SandboxGrants, g => g.Path == runtime.CacheRoot && g.Writable);
        Assert.Contains(runtime.SandboxGrants, g => g.Path == runtime.LoaderRoot && !g.Writable);
    }

    [Fact]
    public void OldLoaderAndCachesMoveOutOfTheDataFolder()
    {
        var data = Path.Combine(_root, "Amber");
        var oldPackage = Path.Combine(data, "universal-loader", "0.3.0");
        Directory.CreateDirectory(oldPackage);
        System.IO.File.WriteAllText(Path.Combine(oldPackage, "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(data, "bake-cache", "x"));
        System.IO.File.WriteAllText(Path.Combine(data, "accounts.json"), "secret");
        var runtime = BakeRuntime.For(data);
        // bake-data already exists in the runtime folder: the old one stays.
        Directory.CreateDirectory(Path.Combine(data, "bake-data"));
        Directory.CreateDirectory(runtime.DataRoot);

        var moved = runtime.MigrateFrom(data);
        Assert.Equal(2, moved.Count);
        Assert.True(System.IO.File.Exists(Path.Combine(runtime.LoaderRoot, "0.3.0", "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(runtime.CacheRoot, "x")));
        Assert.False(Directory.Exists(Path.Combine(data, "universal-loader")));
        Assert.True(Directory.Exists(Path.Combine(data, "bake-data")));
        Assert.True(System.IO.File.Exists(Path.Combine(data, "accounts.json")));
        Assert.False(System.IO.File.Exists(Path.Combine(runtime.Root, "accounts.json")));
        Assert.Empty(runtime.MigrateFrom(data));

        Assert.Equal(Path.Combine(runtime.LoaderRoot, "0.3.0"), runtime.RebaseLoaderPath(oldPackage, data));
        var elsewhere = Path.Combine(_root, "my-loader");
        Assert.Equal(elsewhere, runtime.RebaseLoaderPath(elsewhere, data));
        Assert.Null(runtime.RebaseLoaderPath(null, data));
    }
}
