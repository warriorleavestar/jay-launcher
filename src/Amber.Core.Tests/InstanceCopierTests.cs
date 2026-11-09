using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.GameLibrary;

namespace Amber.Core.Tests;

/// <summary>"Copy an existing instance" (official design §6.11).</summary>
public sealed class InstanceCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-copy-" + Guid.NewGuid().ToString("N"));
    private readonly GameDirectory _dir;

    public InstanceCopierTests()
    {
        _dir = new GameDirectory(_root);
        var v = _dir.VersionDir("src");
        Directory.CreateDirectory(Path.Combine(v, "mods"));
        Directory.CreateDirectory(Path.Combine(v, "saves", "World"));
        Directory.CreateDirectory(Path.Combine(v, "natives"));
        Directory.CreateDirectory(Path.Combine(v, "logs"));
        Directory.CreateDirectory(Path.Combine(v, TrashService.TrashFolder));
        File.WriteAllText(Path.Combine(v, "src.json"), """{"id":"src","inheritsFrom":"1.21.1","mainClass":"x"}""");
        File.WriteAllText(Path.Combine(v, "src.jar"), "jar");
        File.WriteAllText(Path.Combine(v, "mods", "a.jar"), "mod");
        File.WriteAllText(Path.Combine(v, "saves", "World", "level.dat"), "w");
        File.WriteAllText(Path.Combine(v, "natives", "lwjgl.so"), "n");
        File.WriteAllText(Path.Combine(v, "logs", "latest.log"), "l");
        File.WriteAllText(Path.Combine(v, "options.txt"), "fov:1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void CopiesContentAndRenamesVersionFiles()
    {
        InstanceCopier.Copy(_dir, "src", "copy", includeSaves: false);
        var v = _dir.VersionDir("copy");
        Assert.True(File.Exists(Path.Combine(v, "copy.json")));
        Assert.True(File.Exists(Path.Combine(v, "copy.jar")));
        Assert.False(File.Exists(Path.Combine(v, "src.json")));
        Assert.Equal("copy", _dir.ReadVersion("copy").Id);
        Assert.Equal("1.21.1", _dir.ReadVersion("copy").InheritsFrom);
        Assert.True(File.Exists(Path.Combine(v, "mods", "a.jar")));
        Assert.True(File.Exists(Path.Combine(v, "options.txt")));
        Assert.False(Directory.Exists(Path.Combine(v, "saves")));
        Assert.False(Directory.Exists(Path.Combine(v, "natives")));
        Assert.False(Directory.Exists(Path.Combine(v, "logs")));
        Assert.False(Directory.Exists(Path.Combine(v, TrashService.TrashFolder)));
        Assert.False(File.Exists(Path.Combine(v, GameDirectory.IncompleteMarker)));
        // The source is untouched.
        Assert.True(File.Exists(Path.Combine(_dir.VersionDir("src"), "src.json")));
        Assert.Contains("copy", _dir.ListInstalled());
    }

    [Fact]
    public void BringsWorldsWhenAsked()
    {
        InstanceCopier.Copy(_dir, "src", "with-worlds", includeSaves: true);
        Assert.True(File.Exists(Path.Combine(_dir.VersionDir("with-worlds"), "saves", "World", "level.dat")));
    }

    [Fact]
    public void RefusesAnExistingTargetAndLeavesItAlone()
    {
        Directory.CreateDirectory(_dir.VersionDir("taken"));
        File.WriteAllText(Path.Combine(_dir.VersionDir("taken"), "keep.txt"), "x");
        Assert.Throws<IOException>(() => InstanceCopier.Copy(_dir, "src", "taken", false));
        Assert.True(File.Exists(Path.Combine(_dir.VersionDir("taken"), "keep.txt")));
    }

    [Fact]
    public void CancelledCopyLeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InstanceCopier.Copy(_dir, "src", "gone", false, cts.Token));
        Assert.False(Directory.Exists(_dir.VersionDir("gone")));
    }

    [Fact]
    public void RejectsUnsafeNames() =>
        Assert.Throws<InvalidDataException>(() => InstanceCopier.Copy(_dir, "src", "../escape", false));
}
