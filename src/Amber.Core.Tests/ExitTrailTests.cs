using System.Diagnostics;
using Amber.Core.Diagnostics;

namespace Amber.Core.Tests;

[Collection(nameof(ExitTrailTests))]
public sealed class ExitTrailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-exit-trail-" + Guid.NewGuid().ToString("N"));

    private string Trail => Path.Combine(_dir, ExitTrail.FolderName, ExitTrail.TrailName);
    private string Previous => Path.Combine(_dir, ExitTrail.FolderName, ExitTrail.PreviousName);

    /// <summary>
    /// Reads a trail file the way another process would. Multiplayer tests in other collections run at the same time and
    /// note lines through the same static trail, which keeps the file open for writing (FileShare.ReadWrite); a plain
    /// File.ReadAllText asks for FileShare.Read and fails on Windows while such a write is in progress.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void CleanExitIsNotReportedOnNextStart()
    {
        ExitTrail.Start(_dir, hookProcess: false);
        Assert.False(ExitTrail.PreviousRunEndedUnexpectedly);
        ExitTrail.Note("mp", "Info: multiplayer.invite.connecting_relay");
        ExitTrail.CleanExit("app exit, code 0");
        Assert.Contains("[exit] app exit, code 0", ReadShared(Trail));

        ExitTrail.Start(_dir, hookProcess: false);
        Assert.False(ExitTrail.PreviousRunEndedUnexpectedly);
        Assert.False(File.Exists(Previous));
    }

    [Fact]
    public void RunWithoutCleanExitKeepsItsTrail()
    {
        ExitTrail.Start(_dir, hookProcess: false);
        ExitTrail.Note("mp", "Info: multiplayer.invite.connecting_relay");
        // No CleanExit: the process was killed or ended some other way.
        ExitTrail.Start(_dir, hookProcess: false);
        Assert.True(ExitTrail.PreviousRunEndedUnexpectedly);
        Assert.Contains("multiplayer.invite.connecting_relay", ReadShared(Previous));
        Assert.DoesNotContain("connecting_relay", ReadShared(Trail));
        Assert.Contains("previous run did not exit cleanly", ReadShared(Trail));
    }

    [Fact]
    public void ExceptionsKeepTheStackWithoutTheHomeFolder()
    {
        ExitTrail.Start(_dir, hookProcess: false);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { throw new InvalidOperationException("failed at " + Path.Combine(home, "secret", "file.txt")); }
        catch (InvalidOperationException e) { ExitTrail.Exception("test", e); }
        var text = ReadShared(Trail);
        Assert.Contains("[exception] test", text);
        Assert.Contains(nameof(ExceptionsKeepTheStackWithoutTheHomeFolder), text);
        if (home.Length > 1) Assert.DoesNotContain(home, text);
    }

    [UnixFact]
    public async Task HelperProcessExitIsRecorded()
    {
        if (OperatingSystem.IsWindows()) return;
        ExitTrail.Start(_dir, hookProcess: false);
        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 7" }, UseShellExecute = false })!;
        ExitTrail.TrackProcess("helper", process);
        await process.WaitForExitAsync();
        for (var i = 0; i < 50 && !ReadShared(Trail).Contains("helper exited"); i++) await Task.Delay(100);
        var text = ReadShared(Trail);
        Assert.Contains("helper started", text);
        Assert.Contains("code 7", text);
    }

    [Fact]
    public void TrailIsTrimmedWhenLarge()
    {
        ExitTrail.Start(_dir, hookProcess: false);
        var line = new string('x', 1000);
        for (var i = 0; i < ExitTrail.MaxBytes / 1000 + 20; i++) ExitTrail.Note("fill", line);
        Assert.True(new FileInfo(Trail).Length <= ExitTrail.MaxBytes);
        Assert.StartsWith("(older lines trimmed)", ReadShared(Trail));
    }

    public void Dispose()
    {
        ExitTrail.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
