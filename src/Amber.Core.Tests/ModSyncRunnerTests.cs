using System.Security.Cryptography;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Running a mod sync in a folder: public files downloaded, the rest from the host, hashes checked, old files recycled.</summary>
public sealed class ModSyncRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jay-syncrun-").FullName;

    private static (byte[] Bytes, ModManifestEntry Entry) File(string name, string? id = null, string? version = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return (bytes, new ModManifestEntry(name, Convert.ToHexStringLower(SHA1.HashData(bytes)), bytes.Length, id, id, version));
    }

    [Fact]
    public async Task SyncsPublicAndPrivateFilesAndRecyclesOldOnes()
    {
        var mods = Path.Combine(_dir, "mods");
        Directory.CreateDirectory(mods);
        var oldJei = File("jei-1.jar", "jei", "1");
        var map = File("journeymap.jar", "journeymap");
        var risky = File("weird-server-mod.jar", "weird");
        foreach (var f in new[] { oldJei, map, risky }) System.IO.File.WriteAllBytes(Path.Combine(mods, f.Entry.FileName), f.Bytes);

        var newJei = File("jei-2.jar", "jei", "2");
        var create = File("create.jar", "create");
        var secret = File("private.jar", "private");
        var broken = File("broken.jar", "broken");
        var host = new ModManifest([newJei.Entry, create.Entry, secret.Entry, broken.Entry]);
        var mine = new ModManifest([oldJei.Entry, map.Entry, risky.Entry]);
        var diff = ModSync.Compare(mine, host);

        var all = new[] { newJei, create, secret, broken }.ToDictionary(f => f.Entry.Sha1);
        var recycled = new List<string>();
        var runner = new ModSyncRunner
        {
            FindPublic = (sha1s, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(
                sha1s.Where(s => s == newJei.Entry.Sha1 || s == create.Entry.Sha1).ToDictionary(s => s, s => "https://cdn.example/" + s)),
            Download = (url, path, _) =>
            {
                System.IO.File.WriteAllBytes(path, all[url[^40..]].Bytes);
                return Task.CompletedTask;
            },
            FromHost = (sha1, _) =>
            {
                if (sha1 == broken.Entry.Sha1)
                {
                    // The host sends something else: rejected by hash.
                    var wrong = Path.Combine(_dir, "wrong.jar");
                    System.IO.File.WriteAllBytes(wrong, [1, 2, 3]);
                    return Task.FromResult<string?>(wrong);
                }
                var received = Path.Combine(_dir, "received-" + sha1);
                System.IO.File.WriteAllBytes(received, all[sha1].Bytes);
                return Task.FromResult<string?>(received);
            },
            Recycle = path =>
            {
                recycled.Add(Path.GetFileName(path));
                System.IO.File.Delete(path);
            },
        };

        var result = await runner.RunAsync(diff, mods, intoNewInstance: true);
        Assert.Equal(2, result.Plan.FromPublic);
        Assert.Equal(2, result.Plan.FromHost);
        Assert.Equal(["broken.jar"], result.Failed.Select(f => f.FileName));
        Assert.Equal(3, result.Added.Count);
        Assert.Equal(["create.jar", "jei-2.jar", "journeymap.jar", "private.jar"], Directory.GetFiles(mods).Select(Path.GetFileName).Order());
        Assert.Equal(["jei-1.jar", "weird-server-mod.jar"], recycled.Order());
        Assert.Equal(ModSync.Compare(ModManifest.Read(mods), host).Missing.Select(m => m.FileName), ["broken.jar"]);
    }

    [Fact]
    public async Task UnreachablePublicSitesFallBackToTheHost()
    {
        var mods = Path.Combine(_dir, "mods2");
        var a = File("a.jar");
        var runner = new ModSyncRunner
        {
            FindPublic = (_, _) => throw new HttpRequestException("offline"),
            Download = (_, _, _) => throw new InvalidOperationException(),
            FromHost = (_, _) =>
            {
                var received = Path.Combine(_dir, "a-recv");
                System.IO.File.WriteAllBytes(received, a.Bytes);
                return Task.FromResult<string?>(received);
            },
            Recycle = _ => { },
        };
        var result = await runner.RunAsync(ModSync.Compare(ModManifest.Empty, new ModManifest([a.Entry])), mods, intoNewInstance: false);
        Assert.True(result.Succeeded);
        Assert.True(System.IO.File.Exists(Path.Combine(mods, "a.jar")));
    }

    [Theory]
    [InlineData("../../evil.jar", "evil.jar")]
    [InlineData("C:\\x\\a?b.jar", "ab.jar")]
    [InlineData("..", "mod.jar")]
    public void FileNamesStayInTheModsFolder(string name, string safe) => Assert.Equal(safe, ModSyncRunner.SafeFileName(name));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
