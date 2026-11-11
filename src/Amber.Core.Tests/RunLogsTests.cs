using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>Saved run logs, event grouping and the cleaned error report (official design §6.9).</summary>
public sealed class RunLogsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "jay-runlog-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 20, 14, 0, TimeSpan.FromHours(13));

    [Fact]
    public void RunIsSavedAndReadBackAfterRestart()
    {
        using (var store = RunLogStore.Begin(_work, T0, noTrace: false))
        {
            store.Add(T0, GameLogLevel.Info, "Loading 87 mods");
            store.Add(T0.AddSeconds(5), GameLogLevel.Error, "Mixin apply failed\n\tat a.b(C.java:1)");
            store.Add(T0.AddSeconds(6), GameLogLevel.Info, "Jay: the game crashed", fromJay: true);
            store.Finish(T0.AddMinutes(2), RunOutcome.Crashed, -1, "Mixin apply failed", "crash-reports/crash.txt");
        }
        var runs = RunLogStore.List(_work);
        var run = Assert.Single(runs);
        Assert.Equal(RunOutcome.Crashed, run.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), run.Duration);
        Assert.Equal("Mixin apply failed", run.Summary);
        var lines = RunLogStore.Read(run.LogPath);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Mixin apply failed\n\tat a.b(C.java:1)", lines[1].Message);
        Assert.Equal(GameLogLevel.Error, lines[1].Level);
        Assert.True(lines[2].FromJay);
        Assert.Equal(T0.AddSeconds(5), lines[1].Time);
    }

    /// <summary>
    /// The run's meta file lives in the instance folder, so it can't point the crash report at any other file: only the name is
    /// kept, under this instance's crash-reports folder; network paths and names with folders in them are dropped.
    /// </summary>
    [Theory]
    [InlineData("crash-reports/crash.txt", "crash.txt")]
    [InlineData("/old/place/crash-reports/crash-2026-11-09_20.14.05-client.txt", "crash-2026-11-09_20.14.05-client.txt")]
    [InlineData("C:\\Games\\x\\crash-reports\\c.txt", "c.txt")]
    [InlineData("\\\\attacker\\share\\x.txt", "x.txt")]
    [InlineData("/home/someone/.ssh/id_ed25519", null)]
    [InlineData("../../options.txt/..", null)]
    [InlineData("C:x.txt", null)]
    [InlineData("", null)]
    public void SavedCrashReportPathStaysInTheInstance(string saved, string? expected)
    {
        using (var store = RunLogStore.Begin(_work, T0, noTrace: false))
            store.Finish(T0.AddMinutes(1), RunOutcome.Crashed, -1, null, saved);
        var run = Assert.Single(RunLogStore.List(_work));
        Assert.Equal(expected is null ? null : Path.Combine(_work, "crash-reports", expected), run.CrashReport);
    }

    [Fact]
    public void NoTraceKeepsChatOutOfTheSavedLog()
    {
        using (var store = RunLogStore.Begin(_work, T0, noTrace: true))
        {
            store.Add(T0, GameLogLevel.Info, "[CHAT] <Steve> secret plans");
            store.Add(T0, GameLogLevel.Info, "<Alex> hello");
            store.Add(T0, GameLogLevel.Info, "Saving chunks");
            store.Finish(T0.AddMinutes(1), RunOutcome.Exited, 0);
        }
        var lines = RunLogStore.Read(RunLogStore.List(_work)[0].LogPath);
        Assert.Equal(["Saving chunks"], lines.Select(l => l.Message));
    }

    [Fact]
    public void OnlyTheNewestRunsAreKept()
    {
        for (var i = 0; i < RunLogStore.Kept + 3; i++)
            using (var store = RunLogStore.Begin(_work, T0.AddMinutes(i), noTrace: false))
                store.Finish(T0.AddMinutes(i).AddSeconds(30), RunOutcome.Exited, 0);
        var runs = RunLogStore.List(_work);
        Assert.Equal(RunLogStore.Kept, runs.Count);
        Assert.Equal(T0.AddMinutes(RunLogStore.Kept + 2), runs[0].StartedAt);
    }

    [Fact]
    public void StackTracesFoldAndRepeatsMerge()
    {
        StoredLogLine L(GameLogLevel level, string message, bool jay = false) => new(T0, level, message, jay);
        var events = LogEvents.Group([
            L(GameLogLevel.Info, "Loading"),
            L(GameLogLevel.Error, "Mixin apply failed"),
            L(GameLogLevel.Info, "\tat org.spongepowered.Mixin.apply(Mixin.java:42)"),
            L(GameLogLevel.Info, "Caused by: java.lang.NoSuchFieldError: x"),
            L(GameLogLevel.Info, "\t... 12 more"),
            L(GameLogLevel.Warn, "Texture missing"),
            L(GameLogLevel.Warn, "Texture missing"),
            L(GameLogLevel.Warn, "Texture missing"),
            L(GameLogLevel.Info, "Jay: exited", jay: true),
        ]);
        Assert.Equal(4, events.Count);
        Assert.Equal(3, events[1].Stack.Count);
        Assert.True(events[1].IsError);
        Assert.Equal(3, events[2].Repeats);
        Assert.Equal(5, events[2].Index);
        Assert.False(events[3].IsError);
    }

    [Fact]
    public void ReportRemovesTokensUserNamesAndEmails()
    {
        var log = new List<StoredLogLine>
        {
            new(T0, GameLogLevel.Info, @"Launching with --username Steve --accessToken eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk --uuid 0123456789abcdef", false),
            new(T0, GameLogLevel.Info, @"Loading C:\Users\haoyu\AppData\Roaming\.minecraft\mods\iris.jar", false),
            new(T0, GameLogLevel.Info, "Reading /home/haoyu/.jay/games/x and /Users/haoyu/Library", false),
            new(T0, GameLogLevel.Info, "Signed in as someone@example.com, Authorization: Bearer abc.def-ghi_jkl123", false),
            new(T0, GameLogLevel.Info, "Session: token:abcdefgh12345678:0123456789abcdef", false),
        };
        var text = ErrorReport.Build(new ErrorReportInput("Survival", "1.20.1", "fabric 0.16.9", ["iris-1.7.0.jar"], log, "17.0.9", "Steve"),
            userName: "haoyu", homeDir: @"C:\Users\haoyu");
        Assert.DoesNotContain("haoyu", text);
        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("0123456789abcdef", text);
        Assert.DoesNotContain("someone@example.com", text);
        Assert.DoesNotContain("abc.def-ghi_jkl123", text);
        Assert.Contains("Steve", text);
        Assert.Contains(@"~\AppData\Roaming\.minecraft\mods\iris.jar", text);
        Assert.Contains("/home/<user>/.jay", text);
        Assert.Contains("iris-1.7.0.jar", text);
        Assert.Contains("1.20.1", text);
    }

    [Fact]
    public void ReportKeepsOnlyTheLastLines()
    {
        var log = Enumerable.Range(0, ErrorReport.MaxLines + 50).Select(i => new StoredLogLine(T0, GameLogLevel.Info, "line " + i, false)).ToList();
        var text = ErrorReport.Build(new ErrorReportInput("x", "1.20.1", null, [], log, null, null), "u", "/home/u");
        Assert.DoesNotContain("line 49\n", text.Replace("\r", ""));
        Assert.Contains("line 50", text);
        Assert.Contains("line " + (ErrorReport.MaxLines + 49), text);
    }
}
