using System.Diagnostics;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>The bubblewrap test run is bounded: a probe that never exits is killed instead of hanging the launch.</summary>
public sealed class LinuxSandboxProbeTests
{
    [Fact]
    public void HangingProbeTimesOut()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Stopwatch.StartNew();
        // The child keeps stderr open while it sleeps, as a stuck bwrap would.
        var problem = LinuxSandbox.RunProbe("/bin/sh", ["-c", "sleep 30"], 500);
        Assert.NotNull(problem);
        Assert.Contains("timeout", problem);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public void CleanExitIsNoProblemAndFailureNamesTheFirstErrorLine()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(LinuxSandbox.RunProbe("/bin/sh", ["-c", "exit 0"], 10_000));
        var problem = LinuxSandbox.RunProbe("/bin/sh", ["-c", "echo 'no namespaces' >&2; echo second >&2; exit 1"], 10_000);
        Assert.NotNull(problem);
        Assert.Contains("no namespaces", problem);
        Assert.DoesNotContain("second", problem);
    }
}
