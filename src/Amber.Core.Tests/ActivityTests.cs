using Amber.Core.Activity;
using Amber.Core.Auth;
using Amber.Core.Java;
using Amber.Core.Launch;
using Amber.Core.Net;

namespace Amber.Core.Tests;

public class ActivityTests
{
    private DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset Now() => _now;

    // ---- TaskCenter ----

    [Fact]
    public async Task SameDedupeKeyRunsOnce()
    {
        var center = new TaskCenter(Now);
        var gate = new TaskCompletionSource();
        var runs = 0;
        Func<TaskContext, CancellationToken, Task<TaskOutcome>> run = async (ctx, ct) =>
        {
            Interlocked.Increment(ref runs);
            ctx.Report(0.5, "half");
            await gate.Task;
            return TaskOutcome.Success();
        };
        var a = center.Start("install", "1.21.4", run, "install:1.21.4");
        var b = center.Start("install", "1.21.4", run, "install:1.21.4");
        Assert.Same(a, b);
        Assert.Equal(0.5, center.OverallProgress);
        gate.SetResult();
        await WaitAsync(() => !a.IsRunning);
        Assert.Equal(1, runs);
        Assert.Equal(TaskState.Succeeded, a.State);
        Assert.Null(center.OverallProgress);
        // 结束了以后同一个 key 可以再跑。
        var c = center.Start("install", "1.21.4", (_, _) => Task.FromResult(TaskOutcome.Success()), "install:1.21.4");
        Assert.NotSame(a, c);
    }

    [Fact]
    public async Task CancelEndsAsCancelledAndFailureKeepsMessage()
    {
        var center = new TaskCenter(Now);
        var completed = new List<TaskHandle>();
        center.Completed += completed.Add;
        var cancelled = center.Start("java", "Java 21", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return TaskOutcome.Success();
        });
        Assert.True(cancelled.CanCancel);
        cancelled.Cancel();
        await WaitAsync(() => !cancelled.IsRunning);
        Assert.Equal(TaskState.Cancelled, cancelled.State);

        var failed = center.Start("java", "Java 17", (_, _) => throw new IOException("连不上"));
        await WaitAsync(() => !failed.IsRunning);
        Assert.Equal(TaskState.Failed, failed.State);
        Assert.Equal("连不上", failed.Message);

        var partial = center.Start("mods", "3 个模组", (_, _) => Task.FromResult(TaskOutcome.Partial("装好 2 个，1 个失败")));
        await WaitAsync(() => !partial.IsRunning);
        Assert.Equal(TaskState.PartlySucceeded, partial.State);
        Assert.Equal(3, completed.Count);
        Assert.Equal(3, center.ClearFinished());
        Assert.Empty(center.All);
    }

    [Fact]
    public void TrackedTaskIgnoresLateProgressAndSecondCompletion()
    {
        var center = new TaskCenter(Now);
        var cancels = 0;
        var t = center.Track("import", "整合包", () => cancels++, "import:a");
        Assert.Same(t, center.Track("import", "整合包", null, "import:a"));
        t.Report(0.3, "30%");
        Assert.True(t.Succeed());
        t.Report(0.1, "late");
        Assert.Equal(1, t.Progress);
        Assert.NotEqual("late", t.Detail);
        Assert.False(t.Fail("again"));
        Assert.Equal(TaskState.Succeeded, t.State);
        t.Cancel();
        Assert.Equal(0, cancels); // 结束了不再取消
        Assert.False(t.CanCancel);
    }

    [Fact]
    public void FinishedTasksAreCapped()
    {
        var center = new TaskCenter(Now);
        for (var i = 0; i < TaskCenter.MaxFinished + 10; i++) center.Track("x", $"{i}").Succeed();
        center.Track("x", "last");
        Assert.True(center.All.Count <= TaskCenter.MaxFinished + 1);
        Assert.Single(center.Active);
    }

    // ---- RequestCenter ----

    private static RequestSpec Knock(string who, bool safety = true, TimeSpan? timeout = null) =>
        new("knock", $"{who} 敲门", null, [new("deny", "拒绝"), new("allow", "让 TA 进来", IsDefault: true)], safety, timeout, $"knock:{who}");

    [Fact]
    public void ResolvingInOnePlaceRemovesEverywhereOnce()
    {
        var center = new RequestCenter(Now);
        var bar = new List<Guid>();
        var panel = new List<Guid>();
        center.Resolved += r => bar.Add(r.Id);
        center.Resolved += r => panel.Add(r.Id);
        var knock = center.Post(Knock("Alex"));
        Assert.Same(knock, center.Post(Knock("Alex"))); // 同一个人再敲一次不会多一条
        Assert.True(center.Resolve(knock, "allow", RequestSource.Panel));
        Assert.False(center.Resolve(knock, "deny", RequestSource.Bar)); // 请求条那边晚了一步
        Assert.Equal("allow", knock.Choice);
        Assert.Equal(RequestSource.Panel, knock.ResolvedBy);
        Assert.Equal([knock.Id], bar);
        Assert.Equal([knock.Id], panel);
        Assert.Empty(center.Pending);
        Assert.Same(knock, center.History.Single());
    }

    [Fact]
    public void SafetyRequestsComeFirstAndTimeoutsExpire()
    {
        var center = new RequestCenter(Now);
        var multi = center.Post(new RequestSpec("multi", "再开一个", null, [new("start", "仍然开始"), new("cancel", "取消", true)]));
        _now += TimeSpan.FromSeconds(1);
        var remote = center.Post(Knock("Bob", timeout: TimeSpan.FromSeconds(30)) with { Kind = "remote", DedupeKey = "remote:Bob", TimeoutChoice = "deny" });
        Assert.Equal([remote, multi], center.Pending);
        Assert.Equal(TimeSpan.FromSeconds(30), remote.Remaining(_now));
        _now += TimeSpan.FromSeconds(29);
        Assert.Equal(0, center.Expire());
        _now += TimeSpan.FromSeconds(2);
        Assert.Equal(1, center.Expire());
        Assert.Equal("deny", remote.Choice);
        Assert.Equal(RequestSource.Timeout, remote.ResolvedBy);
        Assert.Equal([multi], center.Pending);
        Assert.True(center.Withdraw("knock:nobody") == false);
    }

    [Fact]
    public void RequestNeedsAChoice() =>
        Assert.Throws<ArgumentException>(() => new RequestCenter().Post(new RequestSpec("x", "x", null, [])));

    // ---- LaunchSession ----

    [Fact]
    public void LaunchSessionFollowsTheStateTable()
    {
        var s = new LaunchSession("生存", _now);
        Assert.False(s.EnteredGame(_now)); // 进程还没起来
        Assert.True(s.ProcessUp(_now + TimeSpan.FromSeconds(10)));
        Assert.True(s.HasProcess);
        Assert.True(s.EnteredGame(_now + TimeSpan.FromSeconds(30)));
        Assert.True(s.ExitRequested(_now + TimeSpan.FromMinutes(12)));
        Assert.True(s.ProcessEnded(_now + TimeSpan.FromMinutes(13), 0, crashed: false));
        Assert.Equal(LaunchState.Exited, s.State);
        Assert.False(s.EnteredGame(_now + TimeSpan.FromMinutes(14))); // 迟到的“进入游戏”不改回去
        Assert.Equal(TimeSpan.FromMinutes(13) - TimeSpan.FromSeconds(10), s.PlayTime(_now + TimeSpan.FromHours(1)));
        Assert.False(s.IsActive);
    }

    [Fact]
    public void LaunchSessionFailureAndCrash()
    {
        var failed = new LaunchSession("a", _now);
        Assert.True(failed.Fail(_now, "登录过期"));
        Assert.False(failed.Cancel(_now));
        Assert.Equal(LaunchState.Failed, failed.State);
        Assert.Equal("登录过期", failed.Reason);

        var crashed = new LaunchSession("b", _now);
        crashed.ProcessUp(_now);
        Assert.False(crashed.Cancel(_now)); // 进程起来以后不是“取消”而是退出
        Assert.True(crashed.ProcessEnded(_now, 1, crashed: true, "缺少依赖"));
        Assert.Equal(LaunchState.Crashed, crashed.State);
        Assert.Equal(1, crashed.ExitCode);

        foreach (var end in new[] { LaunchState.Exited, LaunchState.Crashed, LaunchState.Failed, LaunchState.Cancelled })
            Assert.All(Enum.GetValues<LaunchState>(), to => Assert.False(LaunchSession.CanMove(end, to)));
    }

    [Fact]
    public void LaunchGateSwallowsDoubleClicks()
    {
        var gate = new LaunchGate();
        Assert.True(gate.TryPass(_now));
        Assert.False(gate.TryPass(_now + TimeSpan.FromMilliseconds(300)));
        Assert.True(gate.TryPass(_now + TimeSpan.FromMilliseconds(700)));
        Assert.True(gate.TryPass(_now)); // 时钟往回拨：放行，不卡死
    }

    // ---- LaunchFailure ----

    [Fact]
    public void FailuresGetTheRightFixes()
    {
        var login = LaunchFailure.Classify(new InvalidOperationException("x", new LoginRequiredException("过期")), "auto");
        Assert.Equal(LaunchFailureKind.Login, login.Kind);
        Assert.Equal(LaunchFix.Relogin, login.Fixes[0]);

        var java = LaunchFailure.Classify(new JavaNotFoundException(21, "没有 Java 21"), "auto");
        Assert.Equal(LaunchFailureKind.Java, java.Kind);
        Assert.Equal(21, java.JavaMajor);
        Assert.Equal(LaunchFix.DownloadJava, java.Fixes[0]);

        var net = LaunchFailure.Classify(new AggregateException(new HttpRequestException("连不上")), "official");
        Assert.Equal(LaunchFailureKind.Network, net.Kind);
        Assert.Contains(LaunchFix.RetryWithMirror, net.Fixes);
        Assert.DoesNotContain(LaunchFix.RetryWithMirror, LaunchFailure.Classify(new DownloadException("x", []), "mirror").Fixes);
        Assert.Equal(LaunchFailureKind.Network, LaunchFailure.Classify(new TaskCanceledException("t", new TimeoutException()), "auto").Kind);

        var other = LaunchFailure.Classify(new InvalidDataException("坏了"), "auto");
        Assert.Equal(LaunchFailureKind.Other, other.Kind);
        foreach (var f in new[] { login, java, net, other })
        {
            Assert.Contains(LaunchFix.ViewLog, f.Fixes);
            Assert.Contains(LaunchFix.ExportReport, f.Fixes);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
