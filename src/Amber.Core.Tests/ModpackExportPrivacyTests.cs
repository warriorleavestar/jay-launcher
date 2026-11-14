using System.IO.Compression;
using System.Text.Json.Nodes;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>An exported CurseForge pack is shared with others: its manifest names no author, and it still reads as the same pack.</summary>
public sealed class ModpackExportPrivacyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-pack-export-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new(new NoNetworkHandler());
    private readonly ModpackService _packs;

    public ModpackExportPrivacyTests()
    {
        Directory.CreateDirectory(_root);
        var dir = new GameDirectory(Path.Combine(_root, "game"));
        var downloader = new Downloader(_http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var context = new LoaderContext(downloader, dir, new VanillaInstaller(downloader, dir),
            (_, _) => throw new InvalidOperationException("tests must not run Java"));
        // No CurseForge key: every mod goes into overrides and nothing is looked up online.
        _packs = new ModpackService(context, new ModrinthProvider(_http), new CurseForgeProvider(_http, () => null));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CurseForgeExportNamesNoAuthorAndStillReadsAsThePack()
    {
        var work = Path.Combine(_root, "game", "versions", "fabric-1.21.1");
        Directory.CreateDirectory(Path.Combine(work, "mods"));
        Directory.CreateDirectory(Path.Combine(work, "config"));
        await File.WriteAllBytesAsync(Path.Combine(work, "mods", "sample.jar"), [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);
        await File.WriteAllTextAsync(Path.Combine(work, "config", "sample.toml"), "enabled = true\n");
        var instance = new GameInstance("fabric-1.21.1", "1.21.1", "fabric", "0.16.0", work);
        var output = Path.Combine(_root, "out", "pack.zip");

        await _packs.ExportCurseForgeAsync(instance, output, "Sample pack", "1.0.0", ["mods/sample.jar", "config/sample.toml"]);

        using (var zip = ZipFile.OpenRead(output))
        {
            JsonNode manifest;
            await using (var stream = zip.GetEntry("manifest.json")!.Open())
                manifest = (await JsonNode.ParseAsync(stream))!;
            Assert.Equal("", manifest["author"]!.GetValue<string>());
            Assert.Equal("Sample pack", manifest["name"]!.GetValue<string>());
            Assert.NotNull(zip.GetEntry("overrides/mods/sample.jar"));
            Assert.NotNull(zip.GetEntry("overrides/config/sample.toml"));
        }

        var info = ModpackService.Inspect(output);
        Assert.Equal("CurseForge", info.Format);
        Assert.Equal("Sample pack", info.Name);
        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("1.21.1", info.McVersion);
        Assert.Equal(LoaderKind.Fabric, info.Loader);
        Assert.Equal("0.16.0", info.LoaderVersion);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("tests must not use the network");
    }
}
