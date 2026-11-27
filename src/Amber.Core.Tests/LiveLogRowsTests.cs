using System.Collections.Specialized;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>The Logs tab of a running game: new lines don't redo the rows that didn't change or close an opened stack.</summary>
[Collection("GlobalLanguage")]
public sealed class LiveLogRowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-live-log-" + Guid.NewGuid().ToString("N"));

    public LiveLogRowsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static GameLogLine Line(GameLogLevel level, string message) => new(DateTimeOffset.Now, level, "main", message);

    /// <summary>An instance "a" with a running game whose live log is <c>run</c>, and its Logs tab open.</summary>
    private (InstanceViewModel Vm, LogViewModel Log, RunLog Run, RunLogStore Store) LiveRun()
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
        var store = RunLogStore.Begin(vm.Current!.WorkDir, run.StartedAt, noTrace: false);
        run.Store = store;
        vm.LiveLogFor = id => id == "a" ? run : null;
        return (vm, log, run, store);
    }

    private static void OpenLogsTab(InstanceViewModel vm)
    {
        vm.IsCurrent = true;
        vm.TabIndex = (int)InstanceTab.Logs;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsLiveRun);
    }

    [Fact]
    public void SameErrorTwiceInOneMillisecondKeepsEachRowsState() => UiTestThread.Run(() =>
    {
        var (vm, log, run, store) = LiveRun();
        using var _ = store;
        // The same error at the same time, each with its own stack: two events whose first lines are equal.
        var at = DateTimeOffset.Now;
        log.Add(run, new GameLogLine(at, GameLogLevel.Error, "main", "Something failed"));
        log.Add(run, new GameLogLine(at, GameLogLevel.Error, "main", "\tat example.First.run(First.java:1)"));
        log.Add(run, new GameLogLine(at, GameLogLevel.Error, "main", "Something failed"));
        log.Add(run, new GameLogLine(at, GameLogLevel.Error, "main", "\tat example.Second.run(Second.java:2)"));
        log.Flush();
        OpenLogsTab(vm);
        Assert.Equal(2, vm.LogRows.Count);
        var (first, second) = (vm.LogRows[0], vm.LogRows[1]);
        Assert.Equal(first.Event.Line, second.Event.Line);

        // Open the second stack and go to the second error (F8 twice).
        vm.ToggleStackCommand.Execute(second);
        vm.JumpLogError(1);
        vm.JumpLogError(1);
        Assert.Same(second, vm.SelectedLogRow);

        log.Add(run, Line(GameLogLevel.Info, "Done"));
        log.Flush();
        vm.PollLive();
        Assert.Equal(3, vm.LogRows.Count);
        Assert.Same(first, vm.LogRows[0]);
        Assert.Same(second, vm.LogRows[1]);
        Assert.False(first.Expanded);
        Assert.True(second.Expanded);
        Assert.Same(second, vm.SelectedLogRow);
        Assert.True(second.IsHighlighted);

        // F8 goes on from the second error (wraps to the first), Shift+F8 comes back.
        vm.JumpLogError(1);
        Assert.Same(first, vm.SelectedLogRow);
        vm.JumpLogError(-1);
        Assert.Same(second, vm.SelectedLogRow);
    });

    [Fact]
    public void KeptRowFollowsALanguageChange() => UiTestThread.Run(() =>
    {
        var before = L.Language;
        try
        {
            L.SetLanguage("zh-Hans");
            var (vm, log, run, store) = LiveRun();
            using var _ = store;
            log.Add(run, Line(GameLogLevel.Error, "Something failed"));
            log.Add(run, Line(GameLogLevel.Error, "\tat example.Mod.init(Mod.java:10)"));
            log.Flush();
            OpenLogsTab(vm);
            var row = vm.LogRows.Single();
            Assert.Equal(L.P("logs.stack.show", 1, 1), row.StackLabel);

            // The row is kept across a refresh, then the language changes.
            log.Add(run, Line(GameLogLevel.Info, "Done"));
            log.Flush();
            vm.PollLive();
            Assert.Same(row, vm.LogRows[0]);
            var changed = new List<string?>();
            row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            L.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(changed, n => string.IsNullOrEmpty(n) || n == nameof(LogRow.StackLabel));
            Assert.Equal("▸ 1 line of stack trace", row.StackLabel);
        }
        finally
        {
            L.SetLanguage(before);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void NewLiveLinesKeepUnchangedRowsAndExpandedStacks() => UiTestThread.Run(() =>
    {
        var (vm, log, run, store) = LiveRun();
        using var _ = store;

        log.Add(run, Line(GameLogLevel.Info, "Loading mods"));
        log.Add(run, Line(GameLogLevel.Error, "Something failed"));
        log.Add(run, Line(GameLogLevel.Error, "\tat example.Mod.init(Mod.java:10)"));
        log.Add(run, Line(GameLogLevel.Info, "Still loading"));
        log.Flush();
        OpenLogsTab(vm);
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
