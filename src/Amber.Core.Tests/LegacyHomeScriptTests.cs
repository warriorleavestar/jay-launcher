using System.Diagnostics;
using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>Old versions write to the system home: exported scripts get the same separate home, and a launch without one is refused.</summary>
public sealed class LegacyHomeScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-legacy-home-" + Guid.NewGuid().ToString("N")[..8]);
    private string Game => Path.Combine(_root, "game");
    private string Data => Path.Combine(_root, "data");

    public LegacyHomeScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GameLauncher Launcher()
    {
        // A version released before 1.6.1: it uses the system home as its game folder.
        var dir = Directory.CreateDirectory(Path.Combine(Game, "versions", "old")).FullName;
        File.WriteAllText(Path.Combine(dir, "old.json"),
            """{"id":"old","type":"release","releaseTime":"2012-03-01T00:00:00+00:00","mainClass":"net.minecraft.client.Minecraft","libraries":[],"minecraftArguments":"${auth_player_name}"}""");
        return new GameLauncher(new GameDirectory(Game)) { NoTraceRoot = Path.Combine(_root, "notrace"), LegacyHomeDataDir = Data };
    }

    private static LaunchOptions Options => new()
    {
        VersionId = "old", JavaPath = "java", Session = new GameSession("Tester", Guid.Empty, "0", "legacy"),
    };

    [Fact]
    public void ExportedScriptForAnOldVersionUsesTheSeparateHome()
    {
        var script = Path.Combine(_root, OperatingSystem.IsWindows() ? "run.bat" : "run.sh");
        Launcher().WriteLaunchScript(script, Options);
        var text = File.ReadAllText(script);
        var home = Path.Combine(Data, "legacyhome");
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("# AMBER_LAUNCH_DATA_V2", text);
            Assert.Contains(System.Text.Json.JsonSerializer.Serialize(home)[1..^1], text);
        }
        else
        {
            Assert.Contains("'-Duser.home=" + home, text);
        }
    }

    [Fact]
    public void OldVersionWithoutASeparateHomeIsNotStarted()
    {
        if (OperatingSystem.IsWindows()) return;
        var launcher = Launcher();
        // Something that isn't a link already sits where the home's .minecraft must point at the instance.
        var home = AsciiPaths.LegacyHome(Game, dataDirectory: Data)!;
        var link = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "minecraft") : Path.Combine(home, ".minecraft");
        Directory.Delete(link);
        Directory.CreateDirectory(link);

        var script = Path.Combine(_root, "run.sh");
        Assert.Throws<InvalidOperationException>(() => launcher.WriteLaunchScript(script, Options));
        Assert.False(File.Exists(script));
        Assert.Throws<InvalidOperationException>(() => launcher.Launch(Options));
    }

    [Fact]
    public void WindowsScriptCarriesAppDataOnlyWhenGiven()
    {
        static string[] Data(string script, string marker)
        {
            var lines = script.ReplaceLineEndings("\n").Split('\n');
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(lines[Array.IndexOf(lines, marker) + 1])!;
        }
        var plain = Toolbox.BuildLaunchScript("java", "C:\\game", ["-Xmx1m"], "", windows: true);
        Assert.DoesNotContain("# AMBER_LAUNCH_DATA_V2\n", plain.ReplaceLineEndings("\n"));
        Assert.Equal(["java", "C:\\game", "-Xmx1m"], Data(plain, "# AMBER_LAUNCH_DATA_V1"));

        var legacy = Toolbox.BuildLaunchScript("java", "C:\\game", ["-Xmx1m"], "", windows: true, appData: "C:\\home & (1)");
        Assert.DoesNotContain("# AMBER_LAUNCH_DATA_V1\n", legacy.ReplaceLineEndings("\n"));
        Assert.Equal(["java", "C:\\game", "C:\\home & (1)", "-Xmx1m"], Data(legacy, "# AMBER_LAUNCH_DATA_V2"));
        Assert.Throws<ArgumentException>(() => Toolbox.BuildLaunchScript("java", "C:\\game", [], "", windows: true, appData: "a\0b"));
    }

    [Fact]
    public async Task WindowsScriptSetsAppDataForTheGame()
    {
        if (!OperatingSystem.IsWindows()) return;
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var receiver = Path.Combine(_root, "receive.ps1");
        var result = Path.Combine(_root, "appdata.txt");
        File.WriteAllText(receiver, "[IO.File]::WriteAllText($env:AMBER_TEST_APPDATA_OUTPUT, $env:APPDATA)");
        var script = Path.Combine(_root, "run.bat");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var appData = Path.Combine(_root, "legacy home & (100%)");
        Toolbox.WriteLaunchScript(script, powershell, work, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", receiver], "0", appData);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c \"\"{script}\"\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["AMBER_TEST_APPDATA_OUTPUT"] = result;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Equal(appData, File.ReadAllText(result));
    }
}
