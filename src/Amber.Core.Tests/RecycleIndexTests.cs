using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>Settings › Storage › Recently deleted (official design §6.20), only inside a temp game directory.</summary>
public sealed class RecycleIndexTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "jay-recycle-test-" + Guid.NewGuid().ToString("N")[..8]);

    public RecycleIndexTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private string Instance(string id)
    {
        var dir = Path.Combine(_game, "versions", id);
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        Directory.CreateDirectory(Path.Combine(dir, "saves", "World A"));
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{}");
        File.WriteAllText(Path.Combine(dir, "mods", "iris.jar"), "jar");
        File.WriteAllText(Path.Combine(dir, "saves", "World A", "level.dat"), "x");
        return dir;
    }

    [Fact]
    public void CollectsKindsAndOwnersNewestFirst()
    {
        var dir = Instance("pack");
        var t0 = DateTimeOffset.Now.AddHours(-2);
        TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), _game, t0);
        TrashService.MoveToTrash(Path.Combine(dir, "saves", "World A"), _game, t0.AddHours(1));
        var items = RecycleIndex.Collect(_game, [dir]);
        Assert.Equal([RecycledKind.World, RecycledKind.Mod], items.Select(i => i.Kind));
        Assert.All(items, i => Assert.Equal("pack", i.InstanceId));
        Assert.Equal("World A", items[0].Name);

        TrashService.MoveToTrash(dir, _game);
        var all = RecycleIndex.Collect(_game, []);
        Assert.Equal(RecycledKind.Instance, all[0].Kind);
        Assert.Equal("pack", all[0].InstanceId);
    }

    [Fact]
    public void DaysLeftCountsDownFromTheRetention()
    {
        var dir = Instance("a");
        var now = DateTimeOffset.Now;
        TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), _game, now.AddDays(-2).AddHours(-1));
        var item = RecycleIndex.Collect(_game, [])[0];
        Assert.Equal(5, item.DaysLeft(7, now));
        Assert.Equal(0, item.DaysLeft(1, now));
    }

    [Fact]
    public void DeleteForeverOnlyRemovesTrashEntries()
    {
        var dir = Instance("a");
        var entry = TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), _game);
        RecycleIndex.DeleteForever(entry);
        Assert.False(Directory.Exists(entry.EntryDir));
        Assert.Empty(RecycleIndex.Collect(_game, []));

        // A forged record that points outside the trash folder is refused and nothing is deleted.
        var forged = entry with { Id = Path.Combine("..", "versions") };
        Assert.Throws<TrashException>(() => RecycleIndex.DeleteForever(forged));
        Assert.True(Directory.Exists(Path.Combine(_game, "versions")));
    }

    [Fact]
    public void EmptyClearsEverythingAndRestoreStillWorksBefore()
    {
        var dir = Instance("a");
        var mod = TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), _game);
        TrashService.MoveToTrash(Path.Combine(dir, "saves", "World A"), _game);
        TrashService.Restore(mod);
        Assert.True(File.Exists(Path.Combine(dir, "mods", "iris.jar")));
        Assert.Equal(1, RecycleIndex.Empty(RecycleIndex.Collect(_game, [dir])));
        Assert.Empty(RecycleIndex.Collect(_game, [dir]));
    }

    [Fact]
    public void InstanceOfReadsTheVersionsFolder()
    {
        Assert.Equal("pack", RecycleIndex.InstanceOf(Path.Combine(_game, "versions", "pack", "mods", "x.jar"), _game));
        Assert.Equal("pack", RecycleIndex.InstanceOf(Path.Combine(_game, "versions", "pack"), _game));
        Assert.Null(RecycleIndex.InstanceOf(Path.Combine(_game, "saves", "w"), _game));
    }
}
