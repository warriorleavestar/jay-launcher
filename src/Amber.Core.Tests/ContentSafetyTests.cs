using System.IO.Compression;
using System.Net;
using System.Text;
using Amber.Core.Auth;
using Amber.Core.Content;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>Content and account files that come from outside: packs, the authlib-injector feed, skins, shader settings, crash bundles.</summary>
public sealed class ContentSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-content-safety-" + Guid.NewGuid().ToString("N"));

    public ContentSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void AnInnerPackThatCannotBeUnpackedLeavesNoTempFolder()
    {
        var outer = Path.Combine(_root, "launcher.zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
        {
            // Declared as stored but cut short, so unpacking it fails.
            var entry = zip.CreateEntry("modpack.mrpack", CompressionLevel.NoCompression);
            using var writer = entry.Open();
            writer.Write(new byte[64]);
        }
        var bytes = File.ReadAllBytes(outer);
        // Corrupt the entry's data so the CRC check fails while the archive itself still opens.
        var data = Encoding.ASCII.GetBytes("modpack.mrpack");
        var at = IndexOf(bytes, data) + data.Length;
        bytes[at] ^= 0xFF;
        File.WriteAllBytes(outer, bytes);
        var before = TempPackFolders();
        Assert.ThrowsAny<Exception>(() => ModpackService.Inspect(outer));
        Assert.Empty(TempPackFolders().Except(before));
    }

    [Theory]
    [InlineData("""{"version":"..\\..\\evil","download_url":"https://example.org/a.jar"}""")]
    [InlineData("""{"version":"../evil","download_url":"https://example.org/a.jar"}""")]
    [InlineData("""{"version":"1.2.3","download_url":"http://example.org/a.jar"}""")]
    [InlineData("""{"version":1,"download_url":"https://example.org/a.jar"}""")]
    public async Task AnAuthlibFeedWithABadVersionOrAddressIsRefused(string feed)
    {
        var http = new FakeHttp().On("https://authlib-injector.yushi.moe/artifact/latest.json", feed)
            .On("https://example.org/a.jar", "jar").On("http://example.org/a.jar", "jar");
        var cache = Path.Combine(_root, "cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AuthlibInjector(new HttpClient(http), cache).EnsureJarAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(cache, StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(cache));
    }

    [Fact]
    public async Task AGoodAuthlibFeedIsDownloadedUnderItsVersion()
    {
        var http = new FakeHttp().On("https://authlib-injector.yushi.moe/artifact/latest.json", """{"version":"1.2.5","download_url":"https://example.org/a.jar"}""")
            .On("https://example.org/a.jar", "jar");
        var path = await new AuthlibInjector(new HttpClient(http), Path.Combine(_root, "cache")).EnsureJarAsync();
        Assert.Equal("authlib-injector-1.2.5.jar", Path.GetFileName(path));
        Assert.Equal("jar", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.org/not-a-png")]
    public async Task OnlyRealSkinImagesFromWebAddressesAreSaved(string url)
    {
        var http = new FakeHttp().On("https://example.org/not-a-png", "<html>");
        var service = new AccountService(new HttpClient(http), new AccountStore(Path.Combine(_root, "accounts.json")), _root, () => null);
        var account = new Account { Kind = AccountKind.External, Name = "Steve", SkinUrl = url };
        Assert.Null(await service.GetSkinFileAsync(account));
        Assert.False(Directory.Exists(Path.Combine(_root, "skins")) && Directory.EnumerateFiles(Path.Combine(_root, "skins")).Any());
    }

    /// <summary>A body that hands out <paramref name="chunk"/> bytes after each <paramref name="pause"/>, up to <paramref name="total"/> (or forever).</summary>
    private sealed class TrickleStream(int chunk, TimeSpan pause, long total = long.MaxValue) : Stream
    {
        private long _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("synchronous read");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent >= total) return 0;
            await Task.Delay(pause, ct);
            var n = (int)Math.Min(Math.Min(chunk, buffer.Length), total - _sent);
            buffer.Span[..n].Fill(0x41);
            _sent += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ASkinBodyIsReadWithoutBlockingAndWithinItsLimits()
    {
        var stall = TimeSpan.FromMilliseconds(300);
        // A normal body arrives whole (only asynchronous reads are used: the stream refuses synchronous ones).
        Assert.Equal(5000, (await AccountService.ReadCappedAsync(new TrickleStream(1000, TimeSpan.Zero, 5000), 10_000, stall, default)).Length);
        // Too large.
        await Assert.ThrowsAsync<InvalidDataException>(() => AccountService.ReadCappedAsync(new TrickleStream(1000, TimeSpan.Zero, 20_000), 10_000, stall, default));
        // A server that goes quiet is given up on after the stall time, not held forever.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountService.ReadCappedAsync(new TrickleStream(1, TimeSpan.FromMinutes(5)), 10_000, stall, default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        // A trickle that never stalls long enough still ends at the overall deadline the caller passes.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountService.ReadCappedAsync(new TrickleStream(1, TimeSpan.FromMilliseconds(50)), 1_000_000, stall, deadline.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ShaderPackNamesRoundTripThroughTheJavaPropertiesFormat()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "iris.properties"), "#Iris\nenableShaders=true\n");
        ShaderSelection.Select(_root, "光影 BSL.zip");
        var text = File.ReadAllText(Path.Combine(config, "iris.properties"));
        Assert.Contains(@"shaderPack=\u5149\u5f71 BSL.zip", text);
        Assert.True(text.All(c => c < 0x80));
        Assert.Equal("光影 BSL.zip", ShaderSelection.Current(_root));
        // Written by Java itself: escaped colon, a comment, and a later duplicate that wins.
        File.WriteAllText(Path.Combine(config, "iris.properties"), "#c\nshaderPack=old.zip\nenableShaders=true\nshaderPack=A\\:B \\u00e9.zip\n");
        Assert.Equal("A:B é.zip", ShaderSelection.Current(_root));
    }

    [Fact]
    public void CrashBundlesHideTheAccountNameWhereItStandsAlone()
    {
        var text = "Environment Variables:\nPATH=/usr/bin\nUSERNAME=alice\nUSER=alice\nOS=Windows_NT\njvm_args: -Duser.name=alice -Xmx2G\n";
        var redacted = ErrorReport.Redact(text, "alice", "/home/alice");
        Assert.DoesNotContain("alice", redacted);
        Assert.Contains("OS=Windows_NT", redacted);
        Assert.Contains("-Xmx2G", redacted);
        Assert.Equal(redacted, ErrorReport.Redact(redacted, "alice", "/home/alice"));
    }

    private static HashSet<string> TempPackFolders() =>
        [.. Directory.EnumerateDirectories(Path.GetTempPath(), "amber-pack-*")];

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
