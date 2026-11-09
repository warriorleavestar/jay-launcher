using System.IO.Compression;
using Amber.Core.Game;

namespace Amber.Core.Tests;

/// <summary>World backups: create, list, restore with a safety backup first (official design §6.6).</summary>
public sealed class WorldBackupsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "jay-wb-test-" + Guid.NewGuid().ToString("N")[..8]);

    private string Saves => Path.Combine(_work, "saves");

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private WorldInfo MakeWorld(string folder, string content)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Saves, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, "level.dat"), content);
        Directory.CreateDirectory(Path.Combine(dir, "region"));
        File.WriteAllText(Path.Combine(dir, "region", "r.0.0.mca"), content + "-region");
        File.WriteAllText(Path.Combine(dir, "session.lock"), "lock");
        return new WorldInfo(folder, dir, folder, "", null, null);
    }

    [Fact]
    public void CreateListsNewestFirstAndSkipsOtherWorlds()
    {
        var world = MakeWorld("New World", "v1");
        MakeWorld("New World-2", "other");
        var first = WorldBackups.Create(world, _work, now: new DateTime(2026, 11, 8, 21, 40, 0));
        var second = WorldBackups.Create(world, _work, now: new DateTime(2026, 11, 9, 8, 0, 0));
        WorldBackups.Create(new WorldInfo("New World-2", Path.Combine(Saves, "New World-2"), "", "", null, null), _work, now: new DateTime(2026, 11, 9, 9, 0, 0));
        File.WriteAllText(Path.Combine(WorldBackups.Dir(_work), "random.zip"), "");

        var list = WorldBackups.List(_work, "New World");
        Assert.Equal([second, first], list.Select(b => b.Path));
        Assert.Equal(new DateTime(2026, 11, 8, 21, 40, 0), list[1].TakenAt.DateTime);
        using var zip = ZipFile.OpenRead(first);
        Assert.Contains(zip.Entries, e => e.FullName == "New World/region/r.0.0.mca");
        Assert.DoesNotContain(zip.Entries, e => e.Name == "session.lock");
        Assert.Empty(Directory.EnumerateFiles(WorldBackups.Dir(_work), "*.partial"));
    }

    [Fact]
    public void SameSecondDoesNotOverwrite()
    {
        var world = MakeWorld("W", "v1");
        var at = new DateTime(2026, 11, 9, 8, 0, 0);
        var a = WorldBackups.Create(world, _work, now: at);
        var b = WorldBackups.Create(world, _work, now: at);
        Assert.NotEqual(a, b);
        Assert.Equal(2, WorldBackups.List(_work, "W").Count);
    }

    [Fact]
    public void CancelLeavesNoHalfWrittenBackup()
    {
        var world = MakeWorld("W", "v1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WorldBackups.Create(world, _work, ct: cts.Token));
        Assert.Empty(Directory.EnumerateFiles(WorldBackups.Dir(_work)));
    }

    [Fact]
    public void RestoreBacksUpTheCurrentWorldFirst()
    {
        var world = MakeWorld("W", "old");
        var backup = WorldBackups.Create(world, _work, now: new DateTime(2026, 11, 1, 10, 0, 0));
        File.WriteAllText(Path.Combine(world.Path, "level.dat"), "new");
        File.WriteAllText(Path.Combine(world.Path, "added.txt"), "x");

        var (safety, restored) = WorldBackups.Restore(WorldBackups.List(_work, "W").Single(b => b.Path == backup), Saves, _work);
        Assert.Equal("old", File.ReadAllText(Path.Combine(restored, "level.dat")));
        Assert.Equal("old-region", File.ReadAllText(Path.Combine(restored, "region", "r.0.0.mca")));
        Assert.False(File.Exists(Path.Combine(restored, "added.txt")));
        Assert.NotNull(safety);
        using (var zip = ZipFile.OpenRead(safety!))
        using (var reader = new StreamReader(zip.GetEntry("W/level.dat")!.Open()))
            Assert.Equal("new", reader.ReadToEnd());
        Assert.Equal(["W"], Directory.EnumerateDirectories(Saves).Select(Path.GetFileName));
    }

    [Fact]
    public void RestoreRejectsPathsOutsideTheWorld()
    {
        Directory.CreateDirectory(WorldBackups.Dir(_work));
        var path = Path.Combine(WorldBackups.Dir(_work), "W-20261101-100000.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("W/level.dat");
            zip.CreateEntry("W/../../evil.txt");
        }
        var backup = WorldBackups.List(_work, "W").Single();
        Assert.Throws<InvalidDataException>(() => WorldBackups.Restore(backup, Saves, _work));
        Assert.False(File.Exists(Path.Combine(_work, "evil.txt")));
        Assert.Empty(Directory.Exists(Saves) ? Directory.EnumerateDirectories(Saves) : []);
    }
}
