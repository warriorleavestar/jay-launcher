using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>"Recently deleted" moves and deletes files off the UI thread and still ends in the right state.</summary>
public sealed class RecycleViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-recycle-vm-" + Guid.NewGuid().ToString("N"));

    public RecycleViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void RestoreDeleteAndEmptyRunInTheBackground() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
        var mods = Path.Combine(_root, "mods");
        Directory.CreateDirectory(mods);
        foreach (var name in new[] { "a.jar", "b.jar", "c.jar" })
        {
            File.WriteAllText(Path.Combine(mods, name), name);
            TrashService.MoveToTrash(Path.Combine(mods, name), _root);
        }
        var recycle = new RecycleViewModel(service);
        recycle.Refresh();
        Assert.Equal(3, recycle.Rows.Count);

        Wait(recycle.RestoreCommand.ExecuteAsync(recycle.Rows.First(r => r.Name.StartsWith("a", StringComparison.Ordinal))));
        Assert.True(File.Exists(Path.Combine(mods, "a.jar")));
        Assert.Equal(2, recycle.Rows.Count);

        Wait(recycle.DeleteForeverCommand.ExecuteAsync(recycle.Rows[0]));
        Assert.Single(recycle.Rows);

        Wait(recycle.EmptyAllCommand.ExecuteAsync(null));
        Assert.True(recycle.IsEmpty);
        Assert.Equal(StatusKind.Success, recycle.Message?.Kind);
    });
}
