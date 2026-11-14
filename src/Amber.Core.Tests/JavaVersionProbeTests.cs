using System.IO.Compression;
using System.Text;
using Amber.App.Services;
using Amber.Core.Java;
using Amber.Core.Launch;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>
/// A fake "java" outside every folder Jay scans, with no file that says its version. Run without limits it can read a
/// marker file next to the test, write next to it and see a variable of Jay's environment; it then answers with another
/// version, so a wrong answer or a file it wrote shows that it ran unrestricted.
/// </summary>
internal sealed class EscapingJava
{
    public const string Secret = "JAY_PROBE_TEST_SECRET";
    public const string HonestVersion = "1.8.0_402";
    public const string EscapedVersion = "66.0.1";

    public string Root { get; }
    public string Home { get; }
    public string Java { get; }
    public string Outside { get; }
    public string Marker => Path.Combine(Outside, "marker.txt");
    public string Escape => Path.Combine(Outside, "escape.txt");

    /// <summary><paramref name="body"/> replaces the honest answer (a Java that says nothing, says too much, or hangs).</summary>
    public EscapingJava(string root, string? body = null)
    {
        Root = root;
        Home = Directory.CreateDirectory(Path.Combine(root, "picked-java")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        File.WriteAllText(Marker, "only the player may read this");
        var bin = Directory.CreateDirectory(Path.Combine(Home, "bin")).FullName;
        if (OperatingSystem.IsWindows())
        {
            Java = Path.Combine(bin, "java.cmd");
            File.WriteAllText(Java, body ?? string.Join("\r\n",
                "@echo off",
                "set LEAK=",
                $"type \"{Marker}\" >nul 2>nul && set LEAK=read",
                $"if defined {Secret} set LEAK=%LEAK%env",
                $"(echo escaped> \"{Escape}\") 2>nul",
                $"if exist \"{Escape}\" set LEAK=%LEAK%write",
                $"if defined LEAK (echo     java.version = {EscapedVersion} 1>&2) else (echo     java.version = {HonestVersion} 1>&2)",
                "echo     os.arch = amd64 1>&2",
                ""));
        }
        else
        {
            Java = Path.Combine(bin, "java");
            File.WriteAllText(Java, body ?? string.Join("\n",
                "#!/bin/sh",
                "LEAK=",
                $"cat '{Marker}' >/dev/null 2>&1 && LEAK=read",
                $"[ -n \"${Secret}\" ] && LEAK=\"${{LEAK}}env\"",
                $"(echo escaped > '{Escape}') 2>/dev/null && LEAK=\"${{LEAK}}write\"",
                $"if [ -n \"$LEAK\" ]; then echo '    java.version = {EscapedVersion}' >&2; else echo '    java.version = {HonestVersion}' >&2; fi",
                "echo '    os.arch = amd64' >&2",
                ""));
            File.SetUnixFileMode(Java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable(Secret, "s3cret");
    }

    /// <summary>Where this platform has a restricted probe at all (Windows; Linux with working bubblewrap).</summary>
    public static bool ProbeAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && LinuxSandbox.Problem() is null;

    /// <summary>Nothing outside changed, and the Java never saw what it must not.</summary>
    public void AssertContained()
    {
        Assert.False(File.Exists(Escape), "the probe wrote outside its temp folder");
        Assert.Equal(["marker.txt"], Directory.GetFiles(Outside).Select(Path.GetFileName));
    }
}

[Collection("java-probe")]
public sealed class JavaVersionProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-probe-" + Guid.NewGuid().ToString("N")[..8]);

    public JavaVersionProbeTests()
    {
        Directory.CreateDirectory(_root);
        JavaFinder.ForgetMajors();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EscapingJava.Secret, null);
        JavaVersionProbe.Timeout = TimeSpan.FromSeconds(10);
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AJavaWithoutVersionFilesRunsOnlyInTheRestrictedProbe_WhereItsEscapesFail()
    {
        var java = new EscapingJava(_root);
        var runs = JavaVersionProbe.RestrictedRuns;
        var major = JavaFinder.MajorOf(java.Java);
        if (!EscapingJava.ProbeAvailable)
        {
            // No way to run it restricted: the version stays unknown and it never runs.
            Assert.Equal(0, major);
            Assert.Equal(runs, JavaVersionProbe.RestrictedRuns);
            java.AssertContained();
            return;
        }
        Assert.Equal(8, major);
        Assert.True(JavaVersionProbe.RestrictedRuns > runs);
        java.AssertContained();

        // Remembered: the same file is not run again.
        var after = JavaVersionProbe.RestrictedRuns;
        Assert.Equal(8, JavaFinder.MajorOf(java.Java));
        Assert.Equal(after, JavaVersionProbe.RestrictedRuns);
        java.AssertContained();
    }

    [Fact]
    public void TheJavaFolderIsGrantedOnlyForTheProbe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var java = new EscapingJava(_root);
        Assert.Equal(8, JavaFinder.MajorOf(java.Java));
        var sid = WindowsSandbox.ContainerSid(JavaVersionProbe.ContainerName);
        Assert.False(WindowsSandbox.NamesExplicitly(java.Home, sid));
    }

    [Fact]
    public void AJavaThatSaysNothingUsableStaysUnknown()
    {
        var silent = OperatingSystem.IsWindows() ? "@echo off\r\necho hello 1>&2\r\n" : "#!/bin/sh\necho hello >&2\n";
        var java = new EscapingJava(_root, silent);
        Assert.Equal(0, JavaFinder.MajorOf(java.Java));
        Assert.Null(JavaVersionProbe.Restricted(java.Home, java.Java));
    }

    [Fact]
    public void TooMuchOutputIsNotAnAnswer()
    {
        if (!EscapingJava.ProbeAvailable) return;
        // 128 KB before the version line.
        var flood = OperatingSystem.IsWindows()
            ? "@echo off\r\nfor /L %%i in (1,1,2000) do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2\r\necho     java.version = 17.0.1 1>&2\r\n"
            : "#!/bin/sh\ni=0\nwhile [ $i -lt 2000 ]; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx >&2; i=$((i+1)); done\necho '    java.version = 17.0.1' >&2\n";
        var java = new EscapingJava(_root, flood);
        Assert.Null(JavaVersionProbe.Restricted(java.Home, java.Java));
    }

    [Fact]
    public async Task AJavaThatHangsIsStoppedInTime_AndACancelStopsTheProbe()
    {
        if (!EscapingJava.ProbeAvailable) return;
        var hang = OperatingSystem.IsWindows()
            // No network in the probe (ping returns at once), so it spins instead.
            ? "@echo off\r\n:spin\r\ngoto spin\r\n"
            : "#!/bin/sh\nsleep 60\necho '    java.version = 17.0.1' >&2\n";
        var java = new EscapingJava(_root, hang);
        JavaVersionProbe.Timeout = TimeSpan.FromSeconds(2);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(JavaVersionProbe.Restricted(java.Home, java.Java));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());

        JavaVersionProbe.Timeout = TimeSpan.FromSeconds(30);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        watch.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JavaFinder.MajorOfAsync(java.Java, cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void TheVersionIsReadFromFilesWithoutRunningAnything()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "jre8")).FullName;
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(home, "bin")).FullName, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(exe, "not a program");
        var runs = JavaVersionProbe.RestrictedRuns;

        // An old runtime without a release file: rt.jar's manifest.
        Directory.CreateDirectory(Path.Combine(home, "lib"));
        using (var zip = ZipFile.Open(Path.Combine(home, "lib", "rt.jar"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open(), new UTF8Encoding(false)))
            writer.Write("Manifest-Version: 1.0\r\nImplementation-Version: 1.8.0_51\r\nSpecification-Version: 1.8\r\n\r\n");
        Assert.Equal("1.8.0_51", JavaVersionProbe.FromFiles(home, exe)?.Version);
        Assert.Equal(8, JavaFinder.MajorOf(exe));

        // A release file says it first.
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"17.0.12\"\nOS_ARCH=\"x86_64\"\n");
        Assert.Equal(new JavaVersionProbe.Found("17.0.12", true), JavaVersionProbe.FromFiles(home, exe));
        Assert.Equal(runs, JavaVersionProbe.RestrictedRuns);
    }

    [Fact]
    public void WindowsReadsTheVersionResourceOfTheProgramFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Any program with a version resource will do: it is read as data, never started.
        var home = Directory.CreateDirectory(Path.Combine(_root, "resource-java")).FullName;
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(home, "bin")).FullName, "java.exe");
        var source = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        File.Copy(source, exe);
        var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(source).FileMajorPart;
        var runs = JavaVersionProbe.RestrictedRuns;
        var found = JavaVersionProbe.FromFiles(home, exe);
        Assert.NotNull(found);
        Assert.Equal(expected, JavaFinder.ParseMajor(found!.Version));
        Assert.True(found.Is64 == Environment.Is64BitOperatingSystem);
        Assert.Equal(runs, JavaVersionProbe.RestrictedRuns);
    }

    private LauncherService Service() => new(new AppSettings
    {
        GameDirectory = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName,
        SavePathForTest = Path.Combine(_root, "settings.json"),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSandboxDecisionNeverRunsThePickedJavaUnrestricted(bool strong)
    {
        if (!OperatingSystem.IsWindows()) return;
        var java = new EscapingJava(_root);
        using var service = Service();
        service.Settings.StrongSandbox = strong;
        // Never says yes: the test must not bring up Windows' admin prompt.
        var options = await service.SandboxForLaunchAsync(java.Java, _ => Task.FromResult(false), _ => { });
        Assert.Equal(WindowsSandboxLevel.Standard, options.WindowsLevel);
        java.AssertContained();
        Assert.Equal(8, WindowsSandbox.JavaMajor(java.Java));
        java.AssertContained();
    }

    [Fact]
    public void ASandboxedLaunchWithAJavaOfUnknownVersionIsRefusedBeforeAnythingRuns()
    {
        if (!OperatingSystem.IsWindows()) return;
        var java = new EscapingJava(_root, "@echo off\r\necho hello 1>&2\r\n");
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        var versionDir = Directory.CreateDirectory(Path.Combine(game, "versions", "1.21.1")).FullName;
        File.WriteAllText(Path.Combine(versionDir, "1.21.1.json"), """{"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        File.WriteAllText(Path.Combine(versionDir, "1.21.1.jar"), "");
        var launcher = new GameLauncher(new Amber.Core.Game.GameDirectory(game));
        var e = Assert.Throws<JavaVersionUnknownException>(() => launcher.Launch(new LaunchOptions
        {
            VersionId = "1.21.1",
            JavaPath = java.Java,
            Session = new Amber.Core.Auth.GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            Sandbox = new SandboxOptions { Enabled = true },
        }, _ => { }));
        Assert.Equal(java.Java, e.JavaPath);
        java.AssertContained();
    }
}
