using System.Collections.Specialized;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>The Logs tab of a running game: new lines don't redo the rows that didn't change or close an opened stack.</summary>
public sealed class LiveLogRowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-live-log-" + Guid.NewGuid().ToString("N"));

    public LiveLogRowsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static GameLogLine Line(GameLogLevel level, string message) => new(DateTimeOffset.Now, level, "main", message);

    [Fact]
    public void NewLiveLinesKeepUnchangedRowsAndExpandedStacks() => UiTestThread.Run(() =>
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "versions", "a")).FullName;
        File.WriteAllText(Path.Combine(dir, "a.json"), """{"id":"a","mainClass":"Example","libraries":[]}""");
        File.WriteAllBytes(Path.Combine(dir, "a.jar"), []);
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var vm = new InstanceViewModel(service) { SelectedId = "a" };
        var log = new LogViewModel();
        var run = log.BeginRun("a", DateTimeOffset.Now);
        using var store = RunLogStore.Begin(vm.Current!.WorkDir, run.StartedAt, noTrace: false);
        run.Store = store;
        vm.LiveLogFor = id => id == "a" ? run : null;

        log.Add(run, Line(GameLogLevel.Info, "Loading mods"));
        log.Add(run, Line(GameLogLevel.Error, "Something failed"));
        log.Add(run, Line(GameLogLevel.Error, "\tat example.Mod.init(Mod.java:10)"));
        log.Add(run, Line(GameLogLevel.Info, "Still loading"));
        log.Flush();
        vm.IsCurrent = true;
        vm.TabIndex = (int)InstanceTab.Logs;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsLiveRun);
        Assert.Equal(3, vm.LogRows.Count);
        var before = vm.LogRows.ToList();
        var stack = before.Single(r => r.HasStack);
        vm.ToggleStackCommand.Execute(stack);
        Assert.True(stack.Expanded);

        // One more line: only an append, the other rows are the same objects and the stack stays open.
        var changes = new List<NotifyCollectionChangedAction>();
        vm.LogRows.CollectionChanged += (_, e) => changes.Add(e.Action);
        log.Add(run, Line(GameLogLevel.Info, "Done"));
        log.Flush();
        vm.PollLive();
        Assert.Equal(4, vm.LogRows.Count);
        for (var i = 0; i < before.Count; i++) Assert.Same(before[i], vm.LogRows[i]);
        Assert.True(stack.Expanded);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);

        // A row whose event changed (it got a stack trace) is replaced, and keeps its expanded state.
        var done = vm.LogRows[^1];
        vm.ToggleStackCommand.Execute(done);
        log.Add(run, Line(GameLogLevel.Error, "\tat example.Mod.done(Mod.java:20)"));
        log.Flush();
        vm.PollLive();
        Assert.Equal(4, vm.LogRows.Count);
        Assert.NotSame(done, vm.LogRows[^1]);
        Assert.True(vm.LogRows[^1].HasStack && vm.LogRows[^1].Expanded);
        for (var i = 0; i < before.Count; i++) Assert.Same(before[i], vm.LogRows[i]);

        // The live log is full and its oldest line rolls out: every event index shifts, the rows are still kept.
        for (var i = 0; i < 4995; i++) log.Add(run, Line(GameLogLevel.Info, "Line " + i));
        log.Flush();
        changes.Clear();
        vm.PollLive();
        Assert.DoesNotContain(vm.LogRows, r => r.Message == "Loading mods");
        Assert.Same(stack, vm.LogRows[0]);
        Assert.True(stack.Expanded);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    });
}
