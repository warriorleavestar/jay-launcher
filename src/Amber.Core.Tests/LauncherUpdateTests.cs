using System.Security.Cryptography;
using Amber.App.Services;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

public class LauncherUpdateTests
{
    private static VerifiedUpdate Offer(string name, ReleaseVerdict verdict = ReleaseVerdict.Valid) =>
        new(new("1.0.0", null, null), new(verdict, "v1.0.0", 2),
            new Dictionary<string, string> { [name] = Convert.ToHexStringLower(SHA256.HashData("package"u8)) },
            new Dictionary<string, Uri> { [name] = new("https://updates.example.test/" + name) });

    [Theory]
    [InlineData("win-x64", "Jay-win-x64-setup.exe")]
    [InlineData("osx-x64", "Jay-osx-x64.pkg")]
    [InlineData("osx-arm64", "Jay-osx-arm64.pkg")]
    [InlineData("android", "Jay-android.apk")]
    public void OnlyOffersTheSignedInstallerForThisPlatform(string platform, string name)
    {
        Assert.Equal(name, LauncherUpdate.PackageFor(Offer(name), platform));
        Assert.Null(LauncherUpdate.PackageFor(Offer(name, ReleaseVerdict.Unsigned), platform));
        Assert.Null(LauncherUpdate.PackageFor(Offer("other.exe"), platform));
        Assert.Null(LauncherUpdate.PackageFor(Offer(name) with { Assets = new Dictionary<string, Uri>() }, platform));
        Assert.Null(LauncherUpdate.PackageFor(Offer(name), "ios-arm64"));
    }

    [Fact]
    public async Task FailedDownloadPreservesExistingBytesAndRetryRevalidatesCache()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            const string name = "Jay-android.apk";
            var offer = Offer(name);
            var url = offer.Assets[name].AbsoluteUri;
            var fake = new FakeHttp().On(url, "corrupt").On(url, "package");
            using var http = new HttpClient(fake);
            var check = new UpdateCheck(http, directory.FullName);
            var previous = Path.Combine(directory.FullName, name);
            await File.WriteAllTextAsync(previous, "previous");
            await Assert.ThrowsAsync<InvalidDataException>(() => check.DownloadAsync(offer, name, directory.FullName));
            Assert.Equal("previous", await File.ReadAllTextAsync(previous));
            Assert.Empty(directory.GetFiles("*.part"));
            var path = await check.DownloadAsync(offer, name, directory.FullName);
            Assert.Equal("package", await File.ReadAllTextAsync(path));
            Assert.Equal(path, await check.DownloadAsync(offer, name, directory.FullName));
            Assert.Equal(2, fake.Requests.Count);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, UpdateCheck.StateFileName), "3");
            await Assert.ThrowsAsync<InvalidOperationException>(() => check.DownloadAsync(offer, name, directory.FullName));
            Assert.Equal(2, fake.Requests.Count);
        }
        finally { directory.Delete(true); }
    }
}
