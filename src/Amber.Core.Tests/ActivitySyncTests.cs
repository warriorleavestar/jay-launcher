using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>The activity center redraws its task list once per batch of progress reports, not once per report.</summary>
public class ActivitySyncTests
{
    [Fact]
    public void ManyProgressReportsMakeOneSync() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new ActivityViewModel(service);
        var finish = new TaskCompletionSource<TaskOutcome>();
        var handle = service.Tasks.Start("test", "Copying", (_, _) => finish.Task);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Tasks);

        var syncs = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActivityViewModel.HasRunningTasks)) syncs++; };
        for (var i = 1; i <= 200; i++) handle.Report(i / 200.0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, syncs);
        Assert.Equal(100, vm.Tasks[0].Percent, 3);

        // A report after the sync is picked up by the next one.
        handle.Report(0.5);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, syncs);
        Assert.Equal(50, vm.Tasks[0].Percent, 3);
        finish.SetResult(TaskOutcome.Success());
    });
}
