using System.Runtime.InteropServices;
using System.Text;
using Amber.Core.Bake;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// build/pins.json is the one list of pinned binaries (privacy plan item 4). These tests run in CI's validate job and
/// fail when a pin written anywhere else disagrees with it.
/// </summary>
public sealed class PinsTests
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static IReadOnlyList<Pins.Pin> FromFile()
    {
        using var stream = File.OpenRead(Path.Combine(Repo, "build", "pins.json"));
        return Pins.Parse(stream);
    }

    [Fact]
    public void TheEmbeddedFileIsTheOneInTheRepository()
    {
        using var embedded = typeof(Pins).Assembly.GetManifestResourceStream(Pins.ResourceName)!;
        using var copy = new MemoryStream();
        embedded.CopyTo(copy);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Repo, "build", "pins.json")), copy.ToArray());
        Assert.Equal(FromFile().Select(p => p.Id), Pins.All.Select(p => p.Id));
    }

    [Fact]
    public void TheLoaderDownloadReadsItsPinFromTheFile()
    {
        var pin = Pins.Get(BakeLoaderRelease.PinId);
        var release = BakeLoaderRelease.Pinned;
        Assert.Equal(pin.Sha256, release.Sha256);
        Assert.Equal(pin.Size, release.Size);
        Assert.Equal(pin.Tag, release.Tag);
        Assert.Equal(pin.File, release.AssetName);
        Assert.Equal(pin.Version, release.LoaderVersion);
        Assert.Equal(pin.Urls[0], release.Url);
        Assert.Equal(BakeLoaderRelease.LoaderReleases, release.ReleasesBase);
        Assert.True(release.IsPublished);
        // The hash is written only in pins.json now.
        Assert.DoesNotContain(pin.Sha256!, File.ReadAllText(Path.Combine(Repo, "src", "Amber.Core", "Bake", "BakeLoaderRelease.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void ALoaderPinWhoseUrlDoesNotMatchItsTagIsRefused()
    {
        var good = Pins.Get(BakeLoaderRelease.PinId);
        var bad = good with { Urls = ["https://github.com/someone/else/releases/download/v9/other.zip"] };
        Assert.Throws<InvalidDataException>(() => BakeLoaderRelease.FromPin(bad));
    }

    public static TheoryData<string, Architecture> EasyTierPlatforms => new()
    {
        { "windows", Architecture.X64 }, { "windows", Architecture.Arm64 },
        { "macos", Architecture.X64 }, { "macos", Architecture.Arm64 },
        { "linux", Architecture.X64 }, { "linux", Architecture.Arm64 },
    };

    [Theory]
    [MemberData(nameof(EasyTierPlatforms))]
    public void EasyTierInCodeMatchesThePins(string os, Architecture arch)
    {
        var asset = EasyTierRelease.For(os, arch);
        var pin = Pins.Get("easytier-" + asset.Platform);
        Assert.Equal(pin.Sha256, asset.Sha256);
        Assert.Equal(pin.Size, asset.Size);
        Assert.Equal(pin.File, asset.Name);
        Assert.Equal(pin.Urls[0], asset.Url);
        Assert.Equal(EasyTierRelease.Version, pin.Version);
        Assert.Equal(EasyTierRelease.ReleaseTag, pin.Tag);
        Assert.Equal(EasyTierRelease.Commit, pin.Commit);
        Assert.Equal(EasyTierRelease.SourceUrl, pin.Source);
    }

    [Fact]
    public void EveryEasyTierPinIsUsedByTheCode()
    {
        var platforms = EasyTierPlatforms.Select(row => EasyTierRelease.For((string)row[0], (Architecture)row[1]).Platform).ToHashSet();
        Assert.All(Pins.All.Where(p => p.Id.StartsWith("easytier-", StringComparison.Ordinal)), p => Assert.Contains(p.Platform!, platforms));
    }

    [Fact]
    public void TheJavaRuntimeManifestMatchesThePin()
    {
        var pin = Pins.Get("mojang-java-runtime-manifest");
        var code = File.ReadAllText(Path.Combine(Repo, "src", "Amber.Core", "Java", "JavaRuntimeInstaller.cs"));
        Assert.Contains($"\"{pin.Urls[0]}\"", code, StringComparison.Ordinal);
        Assert.Contains(pin.Sha1!, pin.Urls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPlaceAPinIsUsedExistsAndNamesIt()
    {
        foreach (var pin in FromFile())
        {
            Assert.NotEmpty(pin.UsedBy);
            foreach (var used in pin.UsedBy)
            {
                var path = Path.Combine(Repo, used.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{pin.Id}: {used} does not exist");
                var text = File.ReadAllText(path);
                // A file names a pin by its hash, its id (it reads pins.json), or, for code that builds names, its tag or platform.
                var names = new[] { pin.Sha256, pin.Sha1, pin.Id, "pins.json" }.OfType<string>();
                Assert.True(names.Any(n => text.Contains(n, StringComparison.Ordinal))
                    || (pin.Platform is { } platform && text.Contains($"\"{platform}\"", StringComparison.Ordinal)),
                    $"{pin.Id}: {used} does not mention it");
            }
        }
    }

    [Fact]
    public void ScriptsThatWriteAHashCarryTheSameOne()
    {
        // Every 64-hex string in the scripts and in Amber.Core must be a pinned SHA-256 (no stray, unlisted pins).
        var pinned = Pins.All.Select(p => p.Sha256).OfType<string>().ToHashSet();
        var files = new[] { "node/install.sh", "node/install-windows.ps1", "scripts/bundle-easytier.sh", "scripts/attach-loader.ps1" }
            .Select(f => Path.Combine(Repo, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(Repo, "src", "Amber.Core"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        foreach (var file in files)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])"))
                Assert.True(pinned.Contains(m.Value.ToLowerInvariant()), $"{Path.GetRelativePath(Repo, file)} has a SHA-256 that is not in build/pins.json: {m.Value}");
    }

    [Fact]
    public void AMalformedPinsFileIsRefused()
    {
        static Stream S(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => Pins.Parse(S("""{"schema":"other@1","pins":[]}""")));
        Assert.Throws<InvalidDataException>(() => Pins.Parse(S("""{"schema":"jay-pins@1","pins":[{"id":"a","urls":["https://x/a"]}]}""")));
        Assert.Throws<InvalidDataException>(() => Pins.Parse(S("""{"schema":"jay-pins@1","pins":[{"id":"a","urls":["https://x/a"],"sha256":"ABC"}]}""")));
        Assert.Throws<InvalidDataException>(() => Pins.Parse(S($$"""{"schema":"jay-pins@1","pins":[{"id":"a","urls":["http://x/a"],"sha256":"{{new string('0', 64)}}"}]}""")));
        var h = new string('0', 64);
        Assert.Throws<InvalidDataException>(() => Pins.Parse(S($$"""{"schema":"jay-pins@1","pins":[{"id":"a","urls":["https://x/a"],"sha256":"{{h}}"},{"id":"a","urls":["https://x/b"],"sha256":"{{h}}"}]}""")));
        Assert.Single(Pins.Parse(S($$"""{"schema":"jay-pins@1","pins":[{"id":"a","urls":["https://x/a"],"sha256":"{{h}}","size":3}]}""")));
    }
}
