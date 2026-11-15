using System.Diagnostics;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>Mac/Linux：Jay 没了 easytier-core 不能留着。用 sleep 代替 easytier-core 和“另一个 Jay”。</summary>
public sealed class EasyTierProcessGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-etguard-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Process Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        var p = Process.Start(info)!;
        _started.Add(p);
        return p;
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task<bool> Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50);
        }
        return true;
    }

    [Fact]
    public void WrapKeepsArgumentsIntact()
    {
        var info = new ProcessStartInfo("/opt/et/easytier-core");
        info.ArgumentList.Add("--network-name");
        info.ArgumentList.Add("a b; rm -rf ~");
        EasyTierProcessGuard.Wrap(info);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("/opt/et/easytier-core", info.FileName);
            return;
        }
        Assert.Equal("/bin/sh", info.FileName);
        Assert.Equal(["-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/opt/et/easytier-core", "--network-name", "a b; rm -rf ~"], info.ArgumentList);
    }

    [Fact]
    public async Task CoreEndsSoonAfterJayIsKilled()
    {
        if (OperatingSystem.IsWindows()) return;
        var jay = Start("/bin/sleep", "600");
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", jay.Id.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Core(watch.Id) > 0), "core started");
        var core = Core(watch.Id);
        await Task.Delay(1200);
        Assert.True(Alive(core), "Jay 还在时 core 一直跑");
        jay.Kill(); // 相当于 kill -9 掉 Jay
        Assert.True(await Until(() => !Alive(core)), "Jay 没了以后 core 跟着结束");
        Assert.True(await Until(() => watch.HasExited));
    }

    /// <summary>A core that ignores SIGTERM is killed a few seconds after Jay is gone, instead of running on.</summary>
    [Fact]
    public async Task ACoreThatIgnoresTermIsKilledAfterJayIsGone()
    {
        if (OperatingSystem.IsWindows()) return;
        var jay = Start("/bin/sleep", "600");
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", jay.Id.ToString(), "/bin/sh", "-c", "trap '' TERM; exec sleep 600");
        Assert.True(await Until(() => Core(watch.Id) > 0), "core started");
        var core = Core(watch.Id);
        jay.Kill();
        Assert.True(await Until(() => !Alive(core), 15), "the core is killed");
        Assert.True(await Until(() => watch.HasExited));
    }

    [Fact]
    public async Task StoppingTheWatcherStopsTheCore()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        Process.Start("kill", ["-TERM", watch.Id.ToString()])!.WaitForExit();
        Assert.True(await Until(() => !Alive(core)));
    }

    [Fact]
    public async Task TrackerCleansUpAWrappedCoreWhenTheWatcherWasLeftBehind()
    {
        // 看门进程自己被留下（比如记录它的 Jay 已经没了但看门进程还没轮到检查）：下次开始联机前的清理按 sh 认出它，连 core 一起结束。
        if (OperatingSystem.IsWindows()) return;
        var info = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        info.ArgumentList.Add("600");
        EasyTierProcessGuard.Wrap(info);
        var watch = Process.Start(info)!;
        _started.Add(watch);
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        Assert.NotNull(EasyTierProcessTracker.Track(watch, _dir, EasyTierProcessGuard.TrackedExecutable(info), Environment.ProcessId, ownerStartTicks: 1));
        EasyTierProcessTracker.Cleanup(_dir, "/opt/not-easytier", EasyTierProcessGuard.WatcherExecutable);
        Assert.True(await Until(() => watch.HasExited && !Alive(core)));
    }

    /// <summary>
    /// Fast PID reuse: the recorded process is gone and its PID now belongs to another process that started shortly after,
    /// inside (1 s) or just outside (2.5 s) the start-time tolerance. Whatever that process runs (another program, another
    /// shell, or the watcher of a different Jay), it is not this record's leftover: it keeps running and the record goes.
    /// </summary>
    [Theory]
    [InlineData("program", 1.0)]
    [InlineData("program", 2.5)]
    [InlineData("shell", 1.0)]
    [InlineData("shell", 2.5)]
    [InlineData("other-watcher", 1.0)]
    [InlineData("other-watcher", 2.5)]
    public async Task AReusedPidIsNotTakenForALeftover(string reusedBy, double startedSecondsLater)
    {
        if (OperatingSystem.IsWindows()) return;
        var core = "/opt/not-easytier/easytier-core";
        var other = reusedBy switch
        {
            "program" => Start("/bin/sleep", "600"),
            "shell" => Start("/bin/sh", "-c", "sleep 600; :"),
            // A watcher, but of a Jay with another PID (this test process) than the one the record names.
            _ => Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, EasyTierProcessGuard.WatcherName, Environment.ProcessId.ToString(), "/bin/sleep", "600"),
        };
        if (reusedBy != "program") Assert.True(await Until(() => Core(other.Id) > 0), "the shell is running");
        // The record of a watcher (or core) that a Jay which is gone now started, just before the PID was handed on.
        var deadJay = Start("/bin/true");
        deadJay.WaitForExit();
        var tracked = reusedBy == "program" ? core : EasyTierProcessGuard.WatcherExecutable;
        var record = EasyTierProcessTracker.Track(other, _dir, tracked, deadJay.Id, ownerStartTicks: 1)!;
        Assert.NotNull(record);
        var lines = File.ReadAllLines(record);
        lines[1] = (long.Parse(lines[1]) - (long)(startedSecondsLater * TimeSpan.TicksPerSecond)).ToString();
        File.WriteAllLines(record, lines);

        EasyTierProcessTracker.Cleanup(_dir, core, EasyTierProcessGuard.WatcherExecutable);

        await Task.Delay(300);
        Assert.False(other.HasExited, $"the process that reused the PID ({reusedBy}) was stopped");
        Assert.False(File.Exists(record), "the stale record is removed");
    }

    [Fact]
    public async Task StopEndsWatcherAndCoreThroughSigtermWithoutFallback()
    {
        // #229: Kill(entireProcessTree) SIGSTOPs the watcher first, and on macOS a stopped member of Jay's orphaned process
        // group gets SIGHUP sent to the whole group, Jay included. Stop must end both through the watcher's TERM trap,
        // well inside the timeout, so the SIGKILL fallback is never reached. (A SIGSTOP itself can't be observed here.)
        if (OperatingSystem.IsWindows()) return;
        var info = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        info.ArgumentList.Add("600");
        EasyTierProcessGuard.Wrap(info);
        var watch = Process.Start(info)!;
        _started.Add(watch);
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        var started = DateTime.UtcNow;
        EasyTierProcessGuard.Stop(watch);
        Assert.True(watch.HasExited, "watcher ended through SIGTERM");
        Assert.True(DateTime.UtcNow - started < EasyTierProcessGuard.StopTimeout, "no fallback to the tree kill");
        Assert.True(await Until(() => !Alive(core)), "core ended with the watcher");
    }

    [Fact]
    public async Task CoreIgnoresHangUpSentToTheGroup()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        await Task.Delay(300);
        Process.Start("kill", ["-HUP", watch.Id.ToString(), core.ToString()])!.WaitForExit();
        await Task.Delay(1500);
        Assert.False(watch.HasExited, "watcher survives SIGHUP");
        Assert.True(Alive(core), "core survives SIGHUP");
    }

    [Fact]
    public async Task StopIsPromptWhileTheWatcherIsPolling()
    {
        // The trap must run at once, not after the watcher's one-second poll.
        if (OperatingSystem.IsWindows()) return;
        var watch = Start("/bin/sh", "-c", EasyTierProcessGuard.WatchScript, "jay-easytier", Environment.ProcessId.ToString(), "/bin/sleep", "600");
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        await Task.Delay(300);
        var started = DateTime.UtcNow;
        Process.Start("kill", ["-TERM", watch.Id.ToString()])!.WaitForExit();
        Assert.True(await Until(() => watch.HasExited && !Alive(core), 5));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), $"took {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task StopFallbackKillsWithoutStopping()
    {
        // A core that ignores TERM: after the timeout both processes go, through plain SIGKILLs.
        if (OperatingSystem.IsWindows()) return;
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("trap '' TERM; exec sleep 600");
        EasyTierProcessGuard.Wrap(info);
        var watch = Process.Start(info)!;
        _started.Add(watch);
        Assert.True(await Until(() => Core(watch.Id) > 0));
        var core = Core(watch.Id);
        EasyTierProcessGuard.Stop(watch, timeout: TimeSpan.FromSeconds(1));
        Assert.True(await Until(() => watch.HasExited && !Alive(core), 5));
    }

    [Fact]
    public async Task StopRightAfterStartLeavesNoCore()
    {
        // A SIGTERM can reach sh before its TERM trap is set (Dispose right after Launch). The core must not outlive it.
        // With the stop this early, the watcher's own TERM to the core can land in the forked child before it has dropped
        // the shell's inherited trap and exec'd the core, so it is lost and Stop ends both through its SIGKILL fallback.
        // That fallback doesn't wait for the watcher to be reaped, so wait for its exit here instead of sampling HasExited.
        if (OperatingSystem.IsWindows()) return;
        for (var i = 0; i < 20; i++)
        {
            // A sleep length nothing else uses, so leftovers can be found by their command line.
            var marker = $"6{Random.Shared.Next(10, 99)}.{Random.Shared.Next(100000, 999999)}";
            var info = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
            info.ArgumentList.Add(marker);
            EasyTierProcessGuard.Wrap(info);
            var watch = Process.Start(info)!;
            _started.Add(watch);
            if (i % 2 == 1) await Task.Delay(i); // vary where in the script the signal lands
            EasyTierProcessGuard.Stop(watch);
            Assert.True(watch.WaitForExit(TimeSpan.FromSeconds(5)), $"watcher ended (round {i})");
            Assert.True(await Until(() => WithMarker(marker).Count == 0, 5), $"core left behind (round {i})");
        }
    }

    [Fact]
    public async Task TermBeforeTheTrapIsSetStillEndsTheCore()
    {
        // Deterministic version of the race above: the real script with a pause between starting the core and setting
        // the TERM trap, and the SIGTERM sent inside that pause.
        if (OperatingSystem.IsWindows()) return;
        const string started = "c=$!; ";
        Assert.Contains(started, EasyTierProcessGuard.WatchScript);
        var script = EasyTierProcessGuard.WatchScript.Replace(started, started + "sleep 1; ", StringComparison.Ordinal);
        var marker = $"6{Random.Shared.Next(10, 99)}.{Random.Shared.Next(100000, 999999)}";
        var watch = Start("/bin/sh", "-c", script, "jay-easytier", Environment.ProcessId.ToString(), "/bin/sleep", marker);
        Assert.True(await Until(() => WithMarker(marker).Count > 0, 5), "core started");
        Process.Start("kill", ["-TERM", watch.Id.ToString()])!.WaitForExit();
        Assert.True(await Until(() => watch.HasExited && WithMarker(marker).Count == 0, 5), "core left behind");
    }

    /// <summary>The core: the watcher's child running "sleep 600" (the watcher also has short-lived "sleep 1" children).</summary>
    private static int Core(int watcher) =>
        Children(watcher).FirstOrDefault(pid => CommandLine(pid).Contains("600", StringComparison.Ordinal));

    private static string CommandLine(int pid)
    {
        var info = new ProcessStartInfo("ps") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in new[] { "-o", "args=", "-p", pid.ToString() }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text;
    }

    /// <summary>Direct children, the same way the guard finds them (Linux reads /proc, macOS asks pgrep).</summary>
    private static List<int> Children(int pid) => EasyTierProcessGuard.ChildPids(pid);

    /// <summary>Processes whose command line holds <paramref name="marker"/>.</summary>
    private static List<int> WithMarker(string marker)
    {
        var info = new ProcessStartInfo("pgrep") { UseShellExecute = false, RedirectStandardOutput = true };
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(marker);
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(n => n != p.Id)];
    }
}
