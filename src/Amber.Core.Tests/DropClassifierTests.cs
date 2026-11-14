using System.IO.Compression;
using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>Drag-and-drop import (official design §6.25): zips are judged by what is inside them.</summary>
public sealed class DropClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-drop-test-" + Guid.NewGuid().ToString("N")[..8]);

    public DropClassifierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Zip(string name, params (string Entry, string Text)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(text);
        }
        return path;
    }

    private string Plain(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void ExtensionsDecideJarsPacksAndThemes()
    {
        Assert.Equal(DropKind.Mod, DropClassifier.Classify(Plain("sodium.jar")));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Plain("pack.mrpack")));
        Assert.Equal(DropKind.Theme, DropClassifier.Classify(Plain("dusk.jaytheme")));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Plain("notes.txt")));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Path.Combine(_root, "missing.zip")));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Plain("broken.zip")));
    }

    [Fact]
    public void EveryPackFormatTheImportReadsIsAModpack()
    {
        // MCBBS, HMCL, MultiMC / Prism, a pack inside one folder, a launcher bundle and a zipped game folder.
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("mcbbs.zip", ("mcbbs.packmeta", """{"addons":[{"id":"game","version":"1.20.1"}]}"""))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("mcbbs2.zip", ("manifest.json", """{"addons":[{"id":"game","version":"1.20.1"}]}"""))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("hmcl.zip", ("modpack.json", """{"gameVersion":"1.20.1"}"""), ("minecraft/mods/a.jar", ""))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("mmc.zip", ("Pack/mmc-pack.json", "{}"), ("Pack/instance.cfg", "name=Pack"))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("wrapped.zip", ("My Pack/modrinth.index.json", "{}"))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("bundle.zip", ("Launcher.exe", "MZ"), ("modpack.mrpack", "x"))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("folder.zip",
            (".minecraft/versions/1.20.1/1.20.1.json", "{}"), (".minecraft/saves/W/level.dat", ""))));
        // A version file deeper than a pack keeps it doesn't make an ordinary zip a pack.
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Zip("deep.zip", ("a/b/c/versions/x/x.json", "{}"))));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Zip("bad.zip", ("manifest.json", "not json"))));
    }

    [Fact]
    public void ZipsAreJudgedByTheirContents()
    {
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("a.zip", ("modrinth.index.json", "{}"))));
        Assert.Equal(DropKind.Modpack, DropClassifier.Classify(Zip("cf.zip", ("manifest.json", """{"minecraft":{},"manifestType":"minecraftModpack"}"""), ("overrides/x", ""))));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Zip("other.zip", ("manifest.json", """{"name":"not a pack"}"""))));
        Assert.Equal(DropKind.World, DropClassifier.Classify(Zip("w1.zip", ("level.dat", ""), ("region/r.0.0.mca", ""))));
        Assert.Equal(DropKind.World, DropClassifier.Classify(Zip("w2.zip", ("My World/level.dat", ""))));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Zip("deep.zip", ("a/b/level.dat", ""))));
        Assert.Equal(DropKind.Shader, DropClassifier.Classify(Zip("s1.zip", ("shaders/final.fsh", ""))));
        Assert.Equal(DropKind.Shader, DropClassifier.Classify(Zip("s2.zip", ("BSL/shaders/final.fsh", ""))));
        Assert.Equal(DropKind.Datapack, DropClassifier.Classify(Zip("d.zip", ("pack.mcmeta", "{}"), ("data/x/function/a.mcfunction", ""))));
        Assert.Equal(DropKind.ResourcePack, DropClassifier.Classify(Zip("r.zip", ("pack.mcmeta", "{}"), ("assets/minecraft/textures/a.png", ""))));
        Assert.Equal(DropKind.Unknown, DropClassifier.Classify(Zip("e.zip", ("readme.txt", ""))));
    }

    [Fact]
    public void SameKindsAreGroupedInFirstSeenOrder()
    {
        var groups = DropClassifier.Group([Plain("a.jar"), Plain("p.mrpack"), Plain("b.jar"), Plain("x.txt")]);
        Assert.Equal([DropKind.Mod, DropKind.Modpack, DropKind.Unknown], groups.Select(g => g.Kind));
        Assert.Equal(2, groups[0].Paths.Count);
    }

    [Fact]
    public void CopyIntoNeverReplacesAFile()
    {
        var source = Plain("iris.jar");
        var mods = Path.Combine(_root, "inst", "mods");
        var first = DropClassifier.CopyInto(source, mods);
        var second = DropClassifier.CopyInto(source, mods);
        Assert.Equal(Path.Combine(mods, "iris.jar"), first);
        Assert.Equal(Path.Combine(mods, "iris (2).jar"), second);
        Assert.True(File.Exists(source));
    }
}
