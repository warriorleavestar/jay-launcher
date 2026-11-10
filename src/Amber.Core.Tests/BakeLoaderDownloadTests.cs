using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amber.Core.Bake;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>"Download loader": the pinned zip from Jay's release, accepted only with the pinned SHA-256, then the normal package install.</summary>
public sealed class BakeLoaderDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-ul-download-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _good;
    private readonly BakeLoaderRelease _release;

    public BakeLoaderDownloadTests()
    {
        Directory.CreateDirectory(_root);
        _good = PackageZip();
        _release = new BakeLoaderRelease(BakeLoaderRelease.JayReleases, "0.0.0-test", "universal-loader-0.0.0-test", "amber-ul-0.0.0-test.zip",
            Convert.ToHexStringLower(SHA256.HashData(_good)), _good.Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string InstallRoot => Path.Combine(_root, "universal-loader");

    /// <summary>A tiny but complete loader package zip (not a real loader).</summary>
    private static byte[] PackageZip()
    {
        var files = new Dictionary<string, string> { ["loader.jar"] = "loader", ["libs/asm.jar"] = "asm", ["bridges/fmlbridge.jar"] = "fml" };
        var entries = new JsonObject();
        foreach (var (name, text) in files)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            entries[name] = new JsonObject { ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["size"] = bytes.Length };
        }
        var manifest = new JsonObject
        {
            ["schema"] = BakePackage.Schema,
            ["loaderVersion"] = "0.0.0-test",
            ["protocol"] = 1,
            ["interface"] = "1.3",
            ["platforms"] = new JsonObject { ["vanilla"] = new JsonArray("1.21.1") },
            ["javaMin"] = new JsonObject { ["vanilla"] = 21 },
            ["classpath"] = new JsonArray("loader.jar", "libs/asm.jar"),
            ["files"] = entries,
        };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in files.Append(new(BakePackage.ManifestName, manifest.ToJsonString())))
                using (var w = new StreamWriter(zip.CreateEntry("amber-ul-0.0.0-test/" + name).Open())) w.Write(text);
        }
        return ms.ToArray();
    }

    private byte[] Tampered()
    {
        var bad = (byte[])_good.Clone();
        bad[bad.Length / 2] ^= 0xFF;
        return bad;
    }

    private sealed class Handler(Func<string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            return respond(url, ct);
        }
    }

    private static Task<HttpResponseMessage> Bytes(byte[] data) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

    private (BakeLoaderDownloader Fetcher, Handler Handler) Create(Func<string, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new Handler(respond);
        var downloader = new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        IReadOnlyList<(string, string)> sources = [("GitHub", "https://a.test/ul.zip"), ("mirror", "https://b.test/ul.zip")];
        return (new BakeLoaderDownloader(downloader, InstallRoot, _release, sources), handler);
    }

    [Fact]
    public void ThePinPointsAtTheLoadersReleasesAndIsEitherUnsetOrComplete()
    {
        var pinned = BakeLoaderRelease.Pinned;
        Assert.Equal(BakeLoaderRelease.LoaderReleases, pinned.ReleasesBase);
        Assert.Matches(@"^https://github\.com/[^/]+/[^/]+/releases/download/$", pinned.ReleasesBase);
        if (!pinned.IsPublished)
        {
            // Placeholder until the loader project publishes its first release: nothing half filled in.
            Assert.Equal("", pinned.Tag);
            Assert.Equal("", pinned.Sha256);
            return;
        }
        Assert.Matches("^[0-9a-f]{64}$", pinned.Sha256);
        Assert.True(pinned.Size > 0);
        Assert.Contains(pinned.LoaderVersion, pinned.AssetName);
        Assert.Equal($"{pinned.ReleasesBase}{pinned.Tag}/{pinned.AssetName}", pinned.Url);
    }

    [Fact]
    public void TheUrlComesFromTheReleaseBaseSoTheRepositoryIsOneValue()
    {
        Assert.Equal("https://github.com/warriorleavestar/jay-launcher/releases/download/universal-loader-0.0.0-test/amber-ul-0.0.0-test.zip", _release.Url);
        var other = _release with { ReleasesBase = "https://github.com/warriorleavestar/jay-loader/releases/download" };
        Assert.Equal("https://github.com/warriorleavestar/jay-loader/releases/download/universal-loader-0.0.0-test/amber-ul-0.0.0-test.zip", other.Url);
        var official = _release.Sources(mirrorsFirst: false);
        Assert.Equal(("GitHub", _release.Url), official[0]);
        Assert.All(official.Skip(1), s => Assert.EndsWith(_release.Url, s.Url));
        Assert.Equal(("GitHub", _release.Url), _release.Sources(mirrorsFirst: true)[^1]);
    }

    [Fact]
    public async Task AnUnpublishedPinDownloadsNothing()
    {
        var unset = new BakeLoaderRelease(BakeLoaderRelease.JayReleases, "", "", "", "", 0);
        Assert.False(unset.IsPublished);
        Assert.False((_release with { Sha256 = "not-a-hash" }).IsPublished);
        var handler = new Handler((_, _) => throw new InvalidOperationException("no network expected"));
        var fetcher = new BakeLoaderDownloader(new Downloader(new HttpClient(handler), new SourceChain([new OfficialSource()])), InstallRoot, unset);
        var e = await Assert.ThrowsAsync<BakeException>(() => fetcher.FetchAsync());
        Assert.Equal("not_published", e.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadsThePinnedZipAndInstallsItLikeAPickedZip()
    {
        var (fetcher, handler) = Create((_, _) => Bytes(_good));
        var seen = new List<DownloadProgress>();
        var zip = await fetcher.FetchAsync(new SyncProgress<DownloadProgress>(seen.Add));

        Assert.Equal(fetcher.ZipPath, zip);
        Assert.Equal(_good, File.ReadAllBytes(zip));
        Assert.Single(handler.Requests);
        Assert.Contains(seen, p => p.CurrentFile == "GitHub");
        var package = BakePackage.InstallFromZip(zip, InstallRoot);
        Assert.Equal(Path.Combine(InstallRoot, "0.0.0-test"), package.Root);
        Assert.NotNull(BakeToolkit.Find(package.Root)?.Package);
    }

    [Fact]
    public async Task AZipWithTheWrongHashIsThrownAwayAndTheNextSourceTried()
    {
        var (fetcher, handler) = Create((url, _) => Bytes(url.Contains("a.test") ? Tampered() : _good));
        var zip = await fetcher.FetchAsync();
        Assert.Equal(_good, File.ReadAllBytes(zip));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OnlyWrongZipsMeansRefusedAndNothingInstalled()
    {
        var (fetcher, _) = Create((_, _) => Bytes(Tampered()));
        var e = await Assert.ThrowsAsync<BakeException>(() => fetcher.FetchAsync());
        Assert.Equal("download_failed", e.Code);
        Assert.Equal(2, e.Problems.Count);
        Assert.False(File.Exists(fetcher.ZipPath));
        Assert.False(File.Exists(fetcher.ZipPath + ".part"));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "0.0.0-test")));
    }

    [Fact]
    public void CheckRefusesWrongSizeAndWrongHash()
    {
        var path = Path.Combine(_root, "x.zip");
        File.WriteAllBytes(path, _good);
        _release.Check(path);
        File.WriteAllBytes(path, Tampered());
        var hash = Assert.Throws<InvalidDataException>(() => _release.Check(path));
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Tampered())), hash.Message);
        File.WriteAllBytes(path, [.. _good, 0]);
        Assert.Throws<InvalidDataException>(() => _release.Check(path));
    }

    [Fact]
    public async Task CancelStopsTheDownloadAndInstallsNothing()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var (fetcher, handler) = Create(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var task = fetcher.FetchAsync(null, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(handler.Requests);   // no other source tried after a cancel
        Assert.False(File.Exists(fetcher.ZipPath));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "0.0.0-test")));
    }

    [Fact]
    public async Task AGoodZipAlreadyDownloadedIsUsedWithoutNetwork()
    {
        var (fetcher, _) = Create((_, _) => throw new InvalidOperationException("no network expected"));
        Directory.CreateDirectory(Path.GetDirectoryName(fetcher.ZipPath)!);
        File.WriteAllBytes(fetcher.ZipPath, _good);
        Assert.Equal(fetcher.ZipPath, await fetcher.FetchAsync());
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) { lock (this) report(value); }
    }
}
