using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>
/// Mojang's Java runtimes (Java 8 for Forge 1.12.2 among them): each runtime's file list is checked against the SHA-1
/// that all.json gives for it, so a mirror cannot swap the list (and with it every file's expected hash).
/// </summary>
public sealed class JavaRuntimeManifestTests : IDisposable
{
    private readonly TestServer _official = new();
    private readonly TestServer _mirror = new();
    private readonly HttpClient _http = Downloader.CreateHttpClient();

    public void Dispose()
    {
        _official.Dispose();
        _mirror.Dispose();
        _http.Dispose();
    }

    private static string Sha1(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));

    private Downloader MirrorFirst() => new(_http, new SourceChain([
        new PrefixMirrorSource("mirror", new Dictionary<string, string> { [_official.Root] = _mirror.Root }), new OfficialSource()]));

    [Fact]
    public async Task AManifestWithTheWrongHashFromOneSourceIsSkipped()
    {
        var good = Encoding.UTF8.GetBytes("""{"files":{"bin/java.exe":{"type":"file"}}}""");
        var swapped = Encoding.UTF8.GetBytes("""{"files":{"bin/evil.exe":{"type":"file"}}}""");
        _official.Add("manifest.json", good);
        _mirror.Add("manifest.json", swapped);

        var text = await MirrorFirst().GetStringAsync(_official.Root + "manifest.json", Sha1(good));
        Assert.Equal(Encoding.UTF8.GetString(good), text);
        Assert.True(_mirror.Requests > 0);

        // Without a hash the first source wins, as before.
        Assert.Equal(Encoding.UTF8.GetString(swapped), await MirrorFirst().GetStringAsync(_official.Root + "manifest.json"));
    }

    [Fact]
    public async Task NoSourceWithTheRightHashIsAnError()
    {
        var swapped = Encoding.UTF8.GetBytes("""{"files":{}}""");
        _official.Add("manifest.json", swapped);
        _mirror.Add("manifest.json", swapped);
        var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
            MirrorFirst().GetStringAsync(_official.Root + "manifest.json", new string('0', 40)));
        Assert.Contains("manifest.json", e.Message);
    }

    [Fact]
    public void ARuntimeComesWithItsFileListsHash()
    {
        var sha = new string('a', 40);
        using var platform = JsonDocument.Parse($$$"""
        {
          "jre-legacy": [{"manifest": {"sha1": "{{{sha}}}", "size": 1, "url": "https://piston-meta.mojang.com/v1/packages/{{{sha}}}/manifest.json"}, "version": {"name": "8u51"}}],
          "java-runtime-delta": [{"manifest": {"sha1": "{{{sha}}}", "url": "https://piston-meta.mojang.com/x/manifest.json"}, "version": {"name": "21.0.7"}}],
          "minecraft-java-exe": []
        }
        """);
        Assert.Equal(("jre-legacy", $"https://piston-meta.mojang.com/v1/packages/{sha}/manifest.json", sha), JavaRuntimeInstaller.FindComponent(platform.RootElement, "jre-legacy", 8));
        // By major version when the component is not named (Java 8's versions read "8u…").
        Assert.Equal("jre-legacy", JavaRuntimeInstaller.FindComponent(platform.RootElement, null, 8)?.Name);
        Assert.Equal("java-runtime-delta", JavaRuntimeInstaller.FindComponent(platform.RootElement, null, 21)?.Name);
        Assert.Null(JavaRuntimeInstaller.FindComponent(platform.RootElement, null, 17));
    }

    [Theory]
    [InlineData("""{"sha1": "short", "url": "https://piston-meta.mojang.com/m.json"}""")]
    [InlineData("""{"sha1": "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "url": "https://piston-meta.mojang.com/m.json"}""")]
    [InlineData("""{"url": "https://piston-meta.mojang.com/m.json"}""")]
    [InlineData("""{"sha1": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "http://piston-meta.mojang.com/m.json"}""")]
    public void ARuntimeWithoutACheckableFileListIsNotOffered(string manifest)
    {
        using var platform = JsonDocument.Parse($$$"""{"jre-legacy": [{"manifest": {{{manifest}}}, "version": {"name": "8u51"}}]}""");
        Assert.Null(JavaRuntimeInstaller.FindComponent(platform.RootElement, "jre-legacy", 8));
        Assert.Null(JavaRuntimeInstaller.FindComponent(platform.RootElement, null, 8));
    }

    [Fact]
    public void AJavaWithoutAReleaseFileIsAskedForItsVersion()
    {
        Assert.Equal(0, JavaFinder.MajorOf(Path.Combine(Path.GetTempPath(), "no-such-java", "bin", "java.exe")));
        // A Java home with no release file (as some older Mojang runtimes): a stand-in java that only answers the
        // version question, so the answer can only come from running it.
        var home = Path.Combine(Path.GetTempPath(), "amber-norelease-" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
        try
        {
            string java;
            if (OperatingSystem.IsWindows())
            {
                java = Path.Combine(bin, "java.cmd");
                File.WriteAllText(java, "@echo off\r\necho     java.version = 17.0.12 1>&2\r\necho     os.arch = amd64 1>&2\r\n");
            }
            else
            {
                java = Path.Combine(bin, "java");
                File.WriteAllText(java, "#!/bin/sh\necho '    java.version = 17.0.12' >&2\necho '    os.arch = amd64' >&2\n");
                File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Assert.False(File.Exists(Path.Combine(home, "release")));
            Assert.Equal(17, JavaFinder.MajorOf(java));
            // The sandbox's own check (which Java 9+ flags to pass) takes the same way when the release file is missing.
            Assert.Equal(17, Amber.Core.Launch.WindowsSandbox.JavaMajor(java));
        }
        finally
        {
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }
}
