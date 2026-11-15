using System.Runtime.InteropServices;
using Amber.Core.Multiplayer;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>Jay downloads its own hardened EasyTier build (jay-easytier), never the upstream release.</summary>
public sealed class EasyTierHardenedBuildTests : IDisposable
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-et-hardened-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<string, Architecture> Platforms => new()
    {
        { "windows", Architecture.X64 }, { "windows", Architecture.Arm64 },
        { "macos", Architecture.X64 }, { "macos", Architecture.Arm64 },
        { "linux", Architecture.X64 }, { "linux", Architecture.Arm64 },
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryPackageComesFromTheHardenedRelease(string os, Architecture arch)
    {
        var asset = EasyTierRelease.For(os, arch);
        Assert.Equal($"{EasyTierRelease.SourceUrl}/releases/download/{EasyTierRelease.ReleaseTag}/{asset.Name}", asset.Url);
        Assert.Equal($"easytier-{asset.Platform}-v{EasyTierRelease.Version}-jay-{EasyTierRelease.Commit}.zip", asset.Name);
        Assert.DoesNotContain("EasyTier/EasyTier", asset.Url, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
    }

    [Fact]
    public void ReleaseTagCommitAndInstallFolderAgree()
    {
        Assert.StartsWith($"jay-v{EasyTierRelease.Version}-", EasyTierRelease.ReleaseTag, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{12}$", EasyTierRelease.Commit);
        Assert.Contains(EasyTierRelease.Commit, EasyTierRelease.InstallTag, StringComparison.Ordinal);
        Assert.NotEqual(EasyTierRelease.Version, EasyTierRelease.InstallTag);
    }

    [Fact]
    public void AnUpstreamInstallIsNotReused()
    {
        // Files an older Jay unpacked from the upstream release live under easytier/<version>/<platform>.
        var asset = new EasyTierRelease.Asset("test-x64", "easytier-test-x64.zip", new string('0', 64), 1, "easytier-core", "easytier-cli");
        var old = Path.Combine(_root, "easytier", EasyTierRelease.Version, asset.Platform);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "easytier-core"), "upstream");
        File.WriteAllText(Path.Combine(old, "easytier-cli"), "upstream");
        using var http = new HttpClient();
        var installer = new EasyTierInstaller(new Downloader(http, new SourceChain([new OfficialSource()])), _root, Path.Combine(_root, "no-bundle"), asset, []);
        Assert.False(installer.IsInstalled);
        Assert.Equal(Path.Combine(_root, "easytier", EasyTierRelease.InstallTag, asset.Platform), installer.InstallDir);
    }

    [Fact]
    public void RelayNodeScriptsPinTheSameBuild()
    {
        var sh = File.ReadAllText(Path.Combine(Repo, "node", "install.sh"));
        var ps = File.ReadAllText(Path.Combine(Repo, "node", "install-windows.ps1"));
        foreach (var script in new[] { sh, ps })
        {
            Assert.Contains(EasyTierRelease.ReleaseTag, script, StringComparison.Ordinal);
            Assert.Contains(EasyTierRelease.Commit, script, StringComparison.Ordinal);
            Assert.Contains(EasyTierRelease.SourceUrl.Replace("https://", "", StringComparison.Ordinal), script, StringComparison.Ordinal);
            Assert.DoesNotContain("EasyTier/EasyTier/releases", script, StringComparison.Ordinal);
        }
        Assert.Contains(EasyTierRelease.For("linux", Architecture.X64).Sha256, sh, StringComparison.Ordinal);
        Assert.Contains(EasyTierRelease.For("linux", Architecture.Arm64).Sha256, sh, StringComparison.Ordinal);
        Assert.Contains(EasyTierRelease.For("windows", Architecture.X64).Sha256, ps, StringComparison.Ordinal);
    }
}
