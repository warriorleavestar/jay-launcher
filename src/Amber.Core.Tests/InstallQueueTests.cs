using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>Discover's "to install" bar (official design §6.12).</summary>
public sealed class InstallQueueTests
{
    private static ContentFile File(string project, string id, string name, ContentSource source = ContentSource.Modrinth) =>
        new(source, project, id, name, name, "https://x/" + name, null, 1, ["1.20.1"], ["fabric"], "release", default, []);

    private static QueuedContent Item(string project, string id) =>
        new(ContentType.Mod, "pack", File(project, id, project + "-" + id + ".jar"), project);

    [Fact]
    public void OneEntryPerProject()
    {
        var queue = new InstallQueue();
        var changes = 0;
        queue.Changed += () => changes++;
        queue.Add(Item("sodium", "v1"));
        queue.Add(Item("iris", "v1"));
        queue.Add(Item("sodium", "v2"));
        Assert.Equal(2, queue.Count);
        Assert.Equal("v2", queue.Items.Single(i => i.File.ProjectId == "sodium").File.FileId);
        Assert.Equal(3, changes);
        Assert.True(queue.Contains(ContentSource.Modrinth, "iris"));
        Assert.False(queue.Contains(ContentSource.CurseForge, "iris"));
    }

    [Fact]
    public void ToggleAddsThenRemoves()
    {
        var queue = new InstallQueue();
        Assert.True(queue.Toggle(Item("iris", "v1")));
        Assert.False(queue.Toggle(Item("iris", "v1")));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void ClearOnlyNotifiesWhenSomethingWasQueued()
    {
        var queue = new InstallQueue();
        var changes = 0;
        queue.Changed += () => changes++;
        queue.Clear();
        Assert.Equal(0, changes);
        queue.Add(Item("a", "1"));
        queue.Clear();
        Assert.Equal(2, changes);
        Assert.False(queue.Remove(ContentSource.Modrinth, "a"));
    }

    [Fact]
    public void MergeDownloadsSharedDependenciesOnce()
    {
        var api = File("fabric-api", "f1", "fabric-api.jar");
        var plan = InstallQueue.Merge(
        [
            new InstallPlan([File("sodium", "s1", "sodium.jar"), api], ["missing x"]),
            new InstallPlan([File("iris", "i1", "iris.jar"), api, File("other", "o1", "FABRIC-API.jar")], ["missing x", "missing y"]),
        ]);
        Assert.Equal(["sodium.jar", "fabric-api.jar", "iris.jar"], plan.Files.Select(f => f.FileName));
        Assert.Equal(["missing x", "missing y"], plan.Problems);
    }
}
