using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>The user's pre-launch command: cancelling the start stops it and everything it started.</summary>
public sealed class UserCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-user-command-" + Guid.NewGuid().ToString("N"));

    public UserCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static bool Alive(int pid)
    {
        var stat = $"/proc/{pid}/stat";
        try
        {
            if (!File.Exists(stat)) return false;
            // A killed child whose parent is gone may linger as a zombie until it is reaped: that is not running.
            var text = File.ReadAllText(stat);
            var state = text[(text.LastIndexOf(')') + 2)..].Split(' ')[0];
            return state != "Z" && state != "X";
        }
        catch (IOException) { return false; }
    }

    [Fact]
    public void CancellingStopsTheCommandAndItsChildren()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pidFile = Path.Combine(_root, "child.pid");
        HomeViewModel? home = null;
        using var cts = new CancellationTokenSource();
        UiTestThread.Run(() =>
        {
            var service = new LauncherService(new AppSettings
            {
                GameDirectory = _root,
                SavePathForTest = Path.Combine(_root, "settings.json"),
                PlayerName = "",
                AutoJavaDownload = false,
            });
            home = new HomeViewModel(service, new LogViewModel());
        });
        // Run off the test UI thread: it does not pump its dispatcher while this test waits.
        var run = Task.Run(() => home!.RunUserCommandAsync($"sleep 20 </dev/null >/dev/null 2>&1 & echo $! > '{pidFile}'; wait", "a", Path.Combine(_root, "work"), null, cts.Token));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(Alive(pid));
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => run.GetAwaiter().GetResult());
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (Alive(pid) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.False(Alive(pid));
    }
}
