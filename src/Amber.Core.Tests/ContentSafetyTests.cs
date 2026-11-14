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
