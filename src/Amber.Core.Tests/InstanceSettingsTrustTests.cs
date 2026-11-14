using Amber.Core.Auth;
using Amber.Core.Game;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>
/// The sandboxed game can write its instance folder. What it writes there must not decide what Jay runs outside the
/// sandbox (commands, Java, arguments) or which of the player's files the next launch may read.
/// </summary>
public sealed class InstanceSettingsTrustTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "jay-settings-trust-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _marker = "jay-test-" + Guid.NewGuid().ToString("N")[..8];

    public InstanceSettingsTrustTests() => Directory.CreateDirectory(Path.Combine(_game, "versions"));

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch (IOException) { }
    }

    private GameDirectory Dir => new(_game);

    private string FileOf(string id) => Path.Combine(_game, "versions", id, InstanceSettings.FileName);

    private void WriteRaw(string id, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf(id))!);
        File.WriteAllText(FileOf(id), json);
    }

    [Fact]
    public void SettingsJaySavedLoadAsSaved()
    {
        new InstanceSettings { PreLaunchCommand = "echo " + _marker, JvmArgs = "-Dx=" + _marker, MaxMemoryMb = 3072 }.Save(Dir, "a");
        var loaded = InstanceSettings.Load(Dir, "a");
        Assert.Equal("echo " + _marker, loaded.PreLaunchCommand);
        Assert.Equal("-Dx=" + _marker, loaded.JvmArgs);
        Assert.Equal(3072, loaded.MaxMemoryMb);
        Assert.False(loaded.DroppedUnknownValues);
    }

    [Fact]
    public void ValuesWrittenBehindJaysBackAreDropped()
    {
        new InstanceSettings { MaxMemoryMb = 2048 }.Save(Dir, "a");
        // What a game could do from inside the sandbox: rewrite the file in its own instance folder.
        WriteRaw("a", $$"""{"MaxMemoryMb":5120,"PreLaunchCommand":"calc {{_marker}}","PostExitCommand":"calc","JavaPath":"/tmp/{{_marker}}/java","JvmArgs":"-javaagent:/tmp/{{_marker}}.jar","GameArgs":"--x"}""");
        var loaded = InstanceSettings.Load(Dir, "a");
        Assert.True(loaded.DroppedUnknownValues);
        Assert.Null(loaded.PreLaunchCommand);
        Assert.Null(loaded.PostExitCommand);
        Assert.Null(loaded.JavaPath);
        Assert.Null(loaded.JvmArgs);
        Assert.Null(loaded.GameArgs);
        Assert.Equal(5120, loaded.MaxMemoryMb);
    }

    [Fact]
    public void ChangingOneSavedValueIsEnough()
    {
        new InstanceSettings { PreLaunchCommand = "echo " + _marker }.Save(Dir, "a");
        WriteRaw("a", $$"""{"PreLaunchCommand":"echo {{_marker}}","JvmArgs":"-Dx=1"}""");
        Assert.True(InstanceSettings.Load(Dir, "a").DroppedUnknownValues);
    }

    [Fact]
    public void SettingsThatWereThereBeforeJayFirstLookedAreKept()
    {
        WriteRaw("old", $$"""{"PreLaunchCommand":"echo {{_marker}}"}""");
        Assert.Equal("echo " + _marker, InstanceSettings.Load(Dir, "old").PreLaunchCommand);
        // Saving another instance first adopts the folder the same way.
        var other = Path.Combine(Path.GetTempPath(), "jay-settings-trust-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var dir = new GameDirectory(other);
            Directory.CreateDirectory(Path.Combine(other, "versions", "old"));
            File.WriteAllText(Path.Combine(other, "versions", "old", InstanceSettings.FileName), $$"""{"GameArgs":"--{{_marker}}"}""");
            new InstanceSettings { MaxMemoryMb = 1024 }.Save(dir, "new");
            Assert.Equal("--" + _marker, InstanceSettings.Load(dir, "old").GameArgs);
        }
        finally
        {
            try { Directory.Delete(other, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ACopiedOrRenamedInstanceKeepsItsSettings()
    {
        new InstanceSettings { PostExitCommand = "echo " + _marker }.Save(Dir, "a");
        Directory.CreateDirectory(Path.Combine(_game, "versions", "b"));
        File.Copy(FileOf("a"), FileOf("b"));
        var copy = InstanceSettings.Load(Dir, "b");
        Assert.Equal("echo " + _marker, copy.PostExitCommand);
        Assert.False(copy.DroppedUnknownValues);
    }

    [Fact]
    public void SettingsWithoutCommandsOrArgumentsNeedNoRecord()
    {
        new InstanceSettings().Save(Dir, "a");
        WriteRaw("a", """{"MaxMemoryMb":6144,"Isolate":true}""");
        var loaded = InstanceSettings.Load(Dir, "a");
        Assert.False(loaded.DroppedUnknownValues);
        Assert.Equal(6144, loaded.MaxMemoryMb);
    }

    [Fact]
    public void WithoutRemovesOneOccurrenceOfEach()
    {
        Assert.Equal(["b", "a", "c", "a"], GameSandbox.Without(["a", "b", "a", "c", "a", "d"], ["a", "d", "x"]));
    }

    [Fact]
    public void FilesNamedOnlyByTheVersionJsonGetNoGrant()
    {
        var secret = Path.Combine(_game, "outside", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        File.WriteAllText(secret, "x");
        var mine = Path.Combine(_game, "outside", "agent.jar");
        File.WriteAllText(mine, "x");
        var id = "v";
        Directory.CreateDirectory(Path.Combine(_game, "versions", id));
        var arg = "-Dleak=" + secret.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_game, "versions", id, id + ".json"),
            $$$"""{"id":"v","type":"release","mainClass":"x","libraries":[],"arguments":{"jvm":["{{{arg}}}"],"game":["{{{secret.Replace("\\", "\\\\")}}}"]}}""");
        var options = new LaunchOptions
        {
            VersionId = id,
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            ExtraJvmArgs = ["-javaagent:" + mine],
        };
        List<string> fromVersion = [];
        var args = new GameLauncher(Dir).BuildArguments(options, null, out _, fromVersion: fromVersion);
        Assert.Contains("-Dleak=" + secret, args);
        Assert.Contains("-Dleak=" + secret, fromVersion);
        Assert.Contains(secret, fromVersion);

        var grants = GameSandbox.For(Path.Combine(_game, "versions", id), _game, Path.Combine(_game, "java", "bin", "java"),
            GameSandbox.Without(args, fromVersion), SandboxOptions.Off);
        Assert.DoesNotContain(grants.ReadOnly, p => GameSandbox.SamePath(p, secret));
        Assert.Contains(grants.ReadOnly, p => GameSandbox.SamePath(p, mine));
    }
}
