using System.IO.Compression;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>Resource pack order and shader choice in the game's own files (official design §6.7).</summary>
public sealed class ResourcePackOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-rp-test-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourcePackOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Options => Path.Combine(_dir, "options.txt");

    [Fact]
    public void ReadsTopFirstAndKeepsOtherOptions()
    {
        File.WriteAllLines(Options, ["version:3465", """resourcePacks:["vanilla","fabric","file/Low.zip","file/High.zip"]""", "lang:zh_cn"]);
        Assert.Equal(["High.zip", "Low.zip"], ResourcePackOptions.Enabled(_dir));

        ResourcePackOptions.SetEnabled(_dir, ["Low.zip", "New.zip", "High.zip"]);
        var lines = File.ReadAllLines(Options);
        Assert.Equal("version:3465", lines[0]);
        Assert.Equal("""resourcePacks:["vanilla","fabric","file/High.zip","file/New.zip","file/Low.zip"]""", lines[1]);
        Assert.Equal("lang:zh_cn", lines[2]);
        Assert.Equal(["Low.zip", "New.zip", "High.zip"], ResourcePackOptions.Enabled(_dir));
    }

    [Fact]
    public void MissingOrBrokenOptionsStartFromVanilla()
    {
        Assert.Empty(ResourcePackOptions.Enabled(_dir));
        File.WriteAllText(Options, "resourcePacks:[not json\n");
        Assert.Empty(ResourcePackOptions.Enabled(_dir));
        ResourcePackOptions.SetEnabled(_dir, ["A.zip"]);
        Assert.Contains("""resourcePacks:["vanilla","file/A.zip"]""", File.ReadAllLines(Options));
    }

    [Fact]
    public void MoveSwapsNeighboursAndStopsAtTheEnds()
    {
        Assert.Equal(["b", "a", "c"], ResourcePackOptions.Move(["a", "b", "c"], "b", -1));
        Assert.Equal(["a", "b", "c"], ResourcePackOptions.Move(["a", "b", "c"], "a", -1));
        Assert.Equal(["a", "c", "b"], ResourcePackOptions.Move(["a", "b", "c"], "b", 1));
    }

    [Theory]
    [InlineData("1.20.1", 15)]
    [InlineData("1.20.4", 22)]
    [InlineData("1.21.1", 34)]
    [InlineData("1.16.5", 6)]
    [InlineData("1.12.2", 3)]
    [InlineData("1.8.9", 1)]
    [InlineData("24w10a", null)]
    [InlineData("1.21.9", null)]
    public void ExpectedFormatPerRelease(string version, int? format) => Assert.Equal(format, PackFormat.Expected(version));

    [Fact]
    public void PackAgeComparesWithTheInstance()
    {
        var zip = Path.Combine(_dir, "old.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open()))
            writer.Write("""{"pack":{"pack_format":3,"description":"old"}}""");
        Assert.Equal(3, PackFormat.Read(zip));
        Assert.Equal(PackAge.Older, PackFormat.Age(3, "1.20.1"));
        Assert.Equal(PackAge.Fits, PackFormat.Age(15, "1.20.1"));
        Assert.Equal(PackAge.Newer, PackFormat.Age(34, "1.20.1"));
        Assert.Equal(PackAge.Unknown, PackFormat.Age(null, "1.20.1"));
        Assert.Null(PackFormat.Read(Path.Combine(_dir, "missing.zip")));
    }

    [Fact]
    public void ShaderSelectionWritesIrisConfig()
    {
        Assert.Null(ShaderSelection.Current(_dir));
        ShaderSelection.Select(_dir, "BSL.zip");
        Assert.Equal("BSL.zip", ShaderSelection.Current(_dir));
        var file = Path.Combine(_dir, "config", "iris.properties");
        File.AppendAllLines(file, ["maxShadowRenderDistance=32"]);
        ShaderSelection.Select(_dir, null);
        Assert.Null(ShaderSelection.Current(_dir));
        Assert.Contains("maxShadowRenderDistance=32", File.ReadAllLines(file));
        Assert.Contains("shaderPack=BSL.zip", File.ReadAllLines(file));
    }

    [Fact]
    public void ShaderLoaderNeedsAnEnabledIrisOculusOrOptiFine()
    {
        Assert.False(ShaderSelection.HasLoader(_dir));
        var mods = Directory.CreateDirectory(Path.Combine(_dir, "mods")).FullName;
        File.WriteAllText(Path.Combine(mods, "sodium-fabric-0.5.jar"), "");
        File.WriteAllText(Path.Combine(mods, "iris-mc1.20.1-1.7.jar.disabled"), "");
        Assert.False(ShaderSelection.HasLoader(_dir));
        File.WriteAllText(Path.Combine(mods, "Iris-mc1.20.1-1.7.jar"), "");
        Assert.True(ShaderSelection.HasLoader(_dir));
    }

    [Fact]
    public void OptiFineSettingsFollowTheSelection()
    {
        var file = Path.Combine(_dir, "optionsshaders.txt");
        File.WriteAllLines(file, ["shaderPack=OFF", "antialiasingLevel=0"]);
        Assert.True(ShaderSelection.HasLoader(_dir));
        ShaderSelection.Select(_dir, "BSL.zip");
        Assert.Contains("shaderPack=BSL.zip", File.ReadAllLines(file));
        ShaderSelection.Select(_dir, null);
        Assert.Contains("shaderPack=OFF", File.ReadAllLines(file));
        Assert.Contains("antialiasingLevel=0", File.ReadAllLines(file));
    }
}
