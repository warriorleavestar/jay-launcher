using System.IO.Compression;
using System.Net;
using System.Text;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Install;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>What a pack may make Jay do: download only from the hosts its format allows, and unpack only what fits.</summary>
public sealed class PackLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-pack-limits-" + Guid.NewGuid().ToString("N"));
    private readonly Handler _handler = new();
    private readonly HttpClient _http;
    private readonly GameDirectory _dir;
    private readonly ModpackService _packs;

    public PackLimitsTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(_handler);
        _dir = new GameDirectory(Path.Combine(_root, "game"));
        Directory.CreateDirectory(_dir.VersionDir("1.20.1"));
        File.WriteAllText(_dir.VersionJsonPath("1.20.1"), """{"id":"1.20.1","mainClass":"Example","libraries":[]}""");
        var downloader = new Downloader(_http, new SourceChain([new OfficialSource()])) { RetriesPerSource = 1 };
        var context = new LoaderContext(downloader, _dir, new VanillaInstaller(downloader, _dir), (_, _) => throw new InvalidOperationException("no Java in tests"));
        _packs = new ModpackService(context, new ModrinthProvider(_http), new CurseForgeProvider(_http, () => null));
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(request.RequestUri!.Host == "cdn.modrinth.com"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/a/versions/b/a.jar", true)]
    [InlineData("https://github.com/o/r/releases/download/v1/a.jar", true)]
    [InlineData("https://raw.githubusercontent.com/o/r/main/a.jar", true)]
    [InlineData("https://gitlab.com/o/r/-/raw/main/a.jar", true)]
    [InlineData("https://CDN.Modrinth.com/a.jar", true)]
    [InlineData("https://edge.forgecdn.net/files/1/2/a.jar", true)]
    [InlineData("https://mediafilez.forgecdn.net/files/1/2/a.jar", true)]
    [InlineData("https://example.com/a.jar", false)]
    [InlineData("https://cdn.modrinth.com.example.com/a.jar", false)]
    [InlineData("https://192.168.1.10/a.jar", false)]
    [InlineData("https://cdn.modrinth.com:8443/a.jar", false)]
    [InlineData("https://user@cdn.modrinth.com/a.jar", false)]
    [InlineData("http://cdn.modrinth.com/a.jar", false)]
    public void OnlyTheFormatsHostsAreAllowed(string url, bool allowed) => Assert.Equal(allowed, ModpackService.IsAllowedMrpackHost(url));

    [Fact]
    public async Task MrpackFileOnAnotherHostIsSkippedWithoutContactingIt()
    {
        var pack = Path.Combine(_root, "pack.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            Write(zip, "modrinth.index.json", """
                {"formatVersion":1,"game":"minecraft","name":"P","dependencies":{"minecraft":"1.20.1"},"files":[
                 {"path":"mods/a.jar","hashes":{"sha1":"aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d"},"downloads":["https://cdn.modrinth.com/data/a.jar"],"fileSize":5},
                 {"path":"mods/b.jar","hashes":{"sha1":"aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d"},"downloads":["https://tracker.example/b.jar"],"fileSize":5}]}
                """);
            Write(zip, "overrides/config/c.txt", "config");
        }
        var skipped = new List<string>();
        Assert.Equal("Fresh", await _packs.ImportAsync(pack, "Fresh", _dir.VersionDir, onSkip: skipped.Add));

        var work = _dir.VersionDir("Fresh");
        Assert.Equal("hello", File.ReadAllText(Path.Combine(work, "mods", "a.jar")));
        Assert.False(File.Exists(Path.Combine(work, "mods", "b.jar")));
        Assert.Equal("config", File.ReadAllText(Path.Combine(work, "config", "c.txt")));
        var note = Assert.Single(skipped);
        Assert.Contains("mods/b.jar", note);
        Assert.Contains("tracker.example", note);
        Assert.DoesNotContain(_handler.Requests, u => u.Host == "tracker.example");
    }

    [Fact]
    public void EntryHoldingMoreThanItDeclaresIsRefused()
    {
        var path = Path.Combine(_root, "lying.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("a.txt", CompressionLevel.NoCompression).Open();
            stream.Write("hello world"u8);
        }
        // Declare 5 bytes in the local and the central header; the data stays 11 bytes.
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 30 < bytes.Length; i++)
        {
            var signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50) BitConverter.GetBytes(5u).CopyTo(bytes, i + 22);
            else if (signature == 0x02014b50) BitConverter.GetBytes(5u).CopyTo(bytes, i + 24);
        }
        File.WriteAllBytes(path, bytes);

        var target = Path.Combine(_root, "out.txt");
        using var read = ZipFile.OpenRead(path);
        Assert.Throws<InvalidDataException>(() => ModpackService.ExtractLimited(read.Entries[0], target));
        Assert.True(!File.Exists(target) || new FileInfo(target).Length <= 5);
    }

    [Fact]
    public void EntryIsUnpackedWithItsContentAndTime()
    {
        var path = Path.Combine(_root, "ok.zip");
        var time = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("a.txt");
            entry.LastWriteTime = time;
            using var stream = entry.Open();
            stream.Write("hello world"u8);
        }
        var target = Path.Combine(_root, "out.txt");
        using var read = ZipFile.OpenRead(path);
        ModpackService.ExtractLimited(read.Entries[0], target);
        Assert.Equal("hello world", File.ReadAllText(target));
        Assert.Equal(read.Entries[0].LastWriteTime.DateTime, File.GetLastWriteTime(target));
    }

    [Fact]
    public void FreeSpaceIsKnownForAFolderThatDoesNotExistYet()
    {
        var free = ModpackService.FreeSpace(Path.Combine(_root, "not", "yet"));
        Assert.NotNull(free);
        Assert.True(free > 0);
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
