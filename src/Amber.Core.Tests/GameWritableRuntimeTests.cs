#pragma warning disable CA1416
using Amber.Core.Java;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>
/// Folders the game may write to: read-only grants inside a writable one get their own label on Windows, and the Java
/// scan never runs a program it finds in the game folder's runtime folder.
/// </summary>
public sealed class GameWritableRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-runtime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ReadOnlyGrantInsideWritableOneGetsItsOwnLabel()
    {
        var game = Path.Combine(_root, "game");
        var data = Path.Combine(_root, "data");
        var grants = new SandboxGrants
        {
            ReadWrite = [game],
            ReadOnly = [Path.Combine(game, "libraries"), Path.Combine(_root, "java"), Path.Combine(data, "log4j.xml")],
            Hidden = [data],
        }.Normalized();
        // Inside the writable game folder: labelled, but a volume without labels doesn't stop the launch.
        Assert.False(WindowsSandbox.NeedsOwnLabel(Path.Combine(game, "libraries"), grants));
        // Inside a hidden folder: the label is what makes it readable, so it must work.
        Assert.True(WindowsSandbox.NeedsOwnLabel(Path.Combine(data, "log4j.xml"), grants));
        // Elsewhere it inherits nothing writable.
        Assert.Null(WindowsSandbox.NeedsOwnLabel(Path.Combine(_root, "java"), grants));
    }

    [Fact]
    public void JavaInTheGameRuntimeFolderIsReadNotRun()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(_root, "ran");
        var unknown = Fake("unknown", release: null, marker);
        var installed = Fake("java-runtime-delta", release: "JAVA_VERSION=\"21.0.3\"\nOS_ARCH=\"x86_64\"\n", marker);

        var found = JavaFinder.FindAll(Path.Combine(_root, "game"));

        Assert.False(File.Exists(marker), "a program in the game's runtime folder was run");
        Assert.DoesNotContain(found, j => j.JavaPath == unknown);
        var java = Assert.Single(found, j => j.JavaPath == installed);
        Assert.Equal(21, java.MajorVersion);
        Assert.True(java.Is64Bit);
    }

    /// <summary>A "java" in the Mojang runtime layout that leaves a marker behind if anything runs it.</summary>
    private string Fake(string name, string? release, string marker)
    {
        var home = Path.Combine(_root, "game", "runtime", name, "linux", name);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        var exe = Path.Combine(home, "bin", "java");
        File.WriteAllText(exe, $"#!/bin/sh\ntouch '{marker}'\necho 'java.version = 17.0.1' >&2\necho 'os.arch = amd64' >&2\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (release is not null) File.WriteAllText(Path.Combine(home, "release"), release);
        return exe;
    }
}
