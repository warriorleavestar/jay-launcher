using Amber.Core.Install;

namespace Amber.Core.Tests;

/// <summary>Cancelling an OptiFine install must stop the patcher process, not leave it writing into the game folder.</summary>
public class OptiFinePatcherCancelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-ofcancel-" + Guid.NewGuid().ToString("N"));

    public OptiFinePatcherCancelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task CancelStopsThePatcherBeforeItWritesItsOutput()
    {
        if (OperatingSystem.IsWindows()) return; // the stand-in "java" is a shell script

        // Stands in for java: the patcher's output path is the sixth argument; write it after a pause.
        var java = Path.Combine(_dir, "java");
        File.WriteAllText(java, "#!/bin/sh\nsleep 2\necho patched > \"$6\"\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var output = Path.Combine(_dir, "OptiFine.jar");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OptiFineInstaller.RunPatcherAsync(java, Path.Combine(_dir, "installer.jar"), Path.Combine(_dir, "vanilla.jar"), output, cts.Token));

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(output));
    }
}
