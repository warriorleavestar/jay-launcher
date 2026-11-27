using System.Diagnostics;
using Amber.App.Services;

namespace Amber.Core.Tests;

/// <summary>
/// Reduced motion: a theme pack applied at startup doesn't read the system setting on the UI thread, and the helper
/// process that reads it (gsettings, defaults) is bounded by a timeout.
/// </summary>
public class SystemMotionTests
{
    [Fact]
    public void ReapplyBeforeStartDoesNotReadTheSystemSetting() => UiTestThread.Run(() =>
    {
        // The test process never calls SystemMotion.Start (no app is running), which is the state at startup when
        // ThemeService.ApplyAtStartup runs before Start.
        var before = SystemMotion.SystemQueries;
        SystemMotion.Reapply();
        Assert.Equal(before, SystemMotion.SystemQueries);
    });

    [Fact]
    public void HelperThatExitsReturnsItsOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("true\n", SystemMotion.Run("sh", ["-c", "echo true"], timeoutMs: 5000));
        Assert.Null(SystemMotion.Run("sh", ["-c", "echo true; exit 1"], timeoutMs: 5000));
    }

    [Fact]
    public void HelperThatNeverExitsIsKilledAtTheTimeLimit()
    {
        if (OperatingSystem.IsWindows()) return;
        var pid = 0;
        var clock = Stopwatch.StartNew();
        Assert.Null(SystemMotion.Run("sleep", ["30"], timeoutMs: 500, started: p => pid = p.Id));
        clock.Stop();
        Assert.InRange(clock.ElapsedMilliseconds, 400, 5000);
        AssertGone(pid);
    }

    [Fact]
    public void HelperThatKeepsWritingToStderrIsKilledAtTheTimeLimit()
    {
        if (OperatingSystem.IsWindows()) return;
        var pid = 0;
        var clock = Stopwatch.StartNew();
        Assert.Null(SystemMotion.Run("sh", ["-c", "while :; do echo noise >&2; done"], timeoutMs: 500, started: p => pid = p.Id));
        clock.Stop();
        Assert.InRange(clock.ElapsedMilliseconds, 400, 5000);
        AssertGone(pid);
    }

    [Fact]
    public void HelperThatFillsStderrBeforeExitingStillReturns()
    {
        // More than a pipe buffer of stderr: without draining it the helper would block and hit the timeout.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("true\n", SystemMotion.Run("sh", ["-c", "i=0; while [ $i -lt 20000 ]; do echo noise >&2; i=$((i+1)); done; echo true"], timeoutMs: 10000));
    }

    private static void AssertGone(int pid)
    {
        Assert.NotEqual(0, pid);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            Assert.True(DateTime.UtcNow < deadline, $"helper process {pid} is still running");
            Thread.Sleep(50);
        }
    }
}
