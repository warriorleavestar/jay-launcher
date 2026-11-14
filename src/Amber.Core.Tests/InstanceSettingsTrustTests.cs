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
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private string _data => _game + "-data";

    /// <summary>A record of this test's own (for this test's async flow only), so a test can break it safely.</summary>
    private string UsePrivateStore()
    {
        var store = Path.Combine(_data, "instance-settings-trust.json");
        InstanceSettingsTrust.StorePathOverride = store;
        return store;
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
    [Fact]
    public void AFolderIsRecordedBeforeAnyGameFromItRuns()
    {
        UsePrivateStore();
        // No settings file at all, then one with nothing that leaves the sandbox: the folder is recorded as seen either way.
        Assert.Null(InstanceSettings.Load(Dir, "a").PreLaunchCommand);
        WriteRaw("b", """{"MaxMemoryMb":2048}""");
        Assert.False(InstanceSettings.Load(Dir, "b").DroppedUnknownValues);
        // What a game started afterwards writes is not taken for settings that were there before.
        WriteRaw("c", $$"""{"PreLaunchCommand":"calc {{_marker}}"}""");
        var loaded = InstanceSettings.Load(Dir, "c");
        Assert.True(loaded.DroppedUnknownValues);
        Assert.Null(loaded.PreLaunchCommand);
    }

    [Fact]
    public void ALaunchRecordsTheFolderFirst()
    {
        UsePrivateStore();
        // The launch fails later (there is no such version), but only after the folder was recorded.
        Assert.ThrowsAny<Exception>(() => new GameLauncher(Dir).Launch(new LaunchOptions
        {
            VersionId = "missing",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        }));
        WriteRaw("a", $$"""{"JvmArgs":"-javaagent:/tmp/{{_marker}}.jar"}""");
        Assert.True(InstanceSettings.Load(Dir, "a").DroppedUnknownValues);
    }

    [Fact]
    public void NoRecordMeansNoLaunchAndNoSave()
    {
        var store = UsePrivateStore();
        Directory.CreateDirectory(store); // in the way: the record can be neither read nor replaced
        var launch = Assert.Throws<IOException>(() => new GameLauncher(Dir).Launch(new LaunchOptions
        {
            VersionId = "missing",
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        }));
        Assert.NotNull(launch.InnerException);
        Assert.Throws<IOException>(() => new InstanceSettings { PreLaunchCommand = "echo " + _marker }.Save(Dir, "a"));
        Assert.False(File.Exists(FileOf("a")));
        // Loading fails closed: values that leave the sandbox are not used without the record.
        WriteRaw("b", $$"""{"PostExitCommand":"echo {{_marker}}","MaxMemoryMb":3072}""");
        var loaded = InstanceSettings.Load(Dir, "b");
        Assert.Null(loaded.PostExitCommand);
        Assert.Equal(3072, loaded.MaxMemoryMb);
    }

    [Fact]
    public void ASaveWaitsForAnotherJayHoldingTheRecord()
    {
        var store = UsePrivateStore();
        new InstanceSettings { PreLaunchCommand = "echo 1 " + _marker }.Save(Dir, "a");
        Task save;
        using (new FileStream(store + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            save = Task.Run(() => new InstanceSettings { PreLaunchCommand = "echo 2 " + _marker }.Save(Dir, "b"));
            Thread.Sleep(300);
            Assert.False(save.IsCompleted);
        }
        save.Wait(TimeSpan.FromSeconds(10));
        Assert.True(save.IsCompletedSuccessfully);
        // Neither save lost the other's record.
        Assert.Equal("echo 1 " + _marker, InstanceSettings.Load(Dir, "a").PreLaunchCommand);
        Assert.Equal("echo 2 " + _marker, InstanceSettings.Load(Dir, "b").PreLaunchCommand);
    }

    [Fact]
    public void ConcurrentSavesKeepEveryRecord()
    {
        UsePrivateStore();
        var store = InstanceSettingsTrust.StorePath;
        Parallel.For(0, 16, i =>
        {
            InstanceSettingsTrust.StorePathOverride = store;
            new InstanceSettings { GameArgs = $"--n {i} {_marker}" }.Save(Dir, "i" + i);
        });
        for (var i = 0; i < 16; i++) Assert.Equal($"--n {i} {_marker}", InstanceSettings.Load(Dir, "i" + i).GameArgs);
    }

    [Fact]
    public void TrimmingKeepsRecordsInstancesStillUse()
    {
        UsePrivateStore();
        InstanceSettingsTrust.MaxTrusted = 2;
        new InstanceSettings { JvmArgs = "-Da=" + _marker }.Save(Dir, "x");
        new InstanceSettings { JvmArgs = "-Db=" + _marker }.Save(Dir, "y");
        new InstanceSettings { JvmArgs = "-Dc=" + _marker }.Save(Dir, "z");
        // Three in use, limit two: all three stay.
        Assert.Equal("-Da=" + _marker, InstanceSettings.Load(Dir, "x").JvmArgs);
        Assert.Equal("-Db=" + _marker, InstanceSettings.Load(Dir, "y").JvmArgs);
        Assert.Equal("-Dc=" + _marker, InstanceSettings.Load(Dir, "z").JvmArgs);
        // x and y move on: x's old value is used by nothing any more and is the one dropped. (The record is written
        // just before the file, so a value is dropped at the save after the one that stopped using it.)
        new InstanceSettings { JvmArgs = "-Dd=" + _marker }.Save(Dir, "x");
        new InstanceSettings { JvmArgs = "-De=" + _marker }.Save(Dir, "y");
        Assert.Equal("-Dc=" + _marker, InstanceSettings.Load(Dir, "z").JvmArgs);
        Assert.Equal("-Dd=" + _marker, InstanceSettings.Load(Dir, "x").JvmArgs);
        Assert.Equal("-De=" + _marker, InstanceSettings.Load(Dir, "y").JvmArgs);
        WriteRaw("x", $$"""{"JvmArgs":"-Da={{_marker}}"}""");
        Assert.True(InstanceSettings.Load(Dir, "x").DroppedUnknownValues);
    }

    private string VersionWith(string id, string mainClass, bool legacy = false, string? logArg = null)
    {
        Directory.CreateDirectory(Path.Combine(_game, "versions", id));
        var args = legacy ? "\"minecraftArguments\":\"--username ${auth_player_name}\"" : "\"arguments\":{\"jvm\":[],\"game\":[]}";
        var logging = "";
        if (logArg is not null)
        {
            var config = Path.Combine(_game, "assets", "log_configs", "client.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, """<Configuration><Appenders><RollingRandomAccessFile name="File" fileName="logs/latest.log"/></Appenders><Loggers><Logger name="net.minecraft.server.MinecraftServer"/><Root level="info"><AppenderRef ref="File"/></Root></Loggers></Configuration>""");
            logging = $$$""","logging":{"client":{"argument":{{{System.Text.Json.JsonSerializer.Serialize(logArg)}}},"file":{"id":"client.xml"},"type":"log4j2-xml"}}""";
        }
        File.WriteAllText(Path.Combine(_game, "versions", id, id + ".json"),
            $$$"""{"id":"{{{id}}}","type":"release","mainClass":{{{System.Text.Json.JsonSerializer.Serialize(mainClass)}}},"libraries":[],{{{args}}}{{{logging}}}}""");
        return id;
    }

    private LaunchOptions Options(string id, bool noTrace = false) => new()
    {
        VersionId = id,
        JavaPath = "java",
        Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
        NoTrace = noTrace,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMainClassJavaWouldReadAsAnOptionIsRefused(bool legacy)
    {
        var agent = Path.Combine(_game, "outside", "agent.jar");
        foreach (var main in new[] { "-javaagent:" + agent, "-Dx=1", "@" + agent, "a b" })
        {
            var id = VersionWith("v" + Math.Abs(main.GetHashCode()), main, legacy);
            Assert.Throws<InvalidDataException>(() => new GameLauncher(Dir).BuildArguments(Options(id), null, out _));
        }
    }

    [Fact]
    public void TheMainClassCountsAsComingFromTheVersion()
    {
        var id = VersionWith("plain", "net.example.Main");
        List<string> fromVersion = [];
        var args = new GameLauncher(Dir).BuildArguments(Options(id), null, out _, fromVersion: fromVersion);
        Assert.Contains("net.example.Main", args);
        Assert.Contains("net.example.Main", fromVersion);
    }

    [Fact]
    public void WithNoTraceTheVersionsLoggingArgumentStillGetsNoGrant()
    {
        var secret = Path.Combine(_game, "outside", "secret.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(secret)!);
        File.WriteAllText(secret, "x");
        var launcher = new GameLauncher(Dir) { NoTraceRoot = Path.Combine(_data, "notrace") };

        // An argument that ignores ${path} and names another file instead.
        var bad = VersionWith("bad", "net.example.Main", logArg: "-javaagent:" + secret);
        List<string> fromVersion = [], jayFiles = [];
        var args = launcher.BuildArguments(Options(bad, noTrace: true), null, out var dir1, fromVersion: fromVersion, jayFiles: jayFiles);
        Assert.Contains("-javaagent:" + secret, args);
        Assert.Contains("-javaagent:" + secret, fromVersion);
        var grants = GameSandbox.For(Path.Combine(_game, "versions", bad), _game, Path.Combine(_game, "java", "bin", "java"),
            GameSandbox.Without(args, fromVersion), SandboxOptions.Off, jayFiles);
        Assert.DoesNotContain(grants.ReadOnly, p => GameSandbox.SamePath(p, secret));
        launcher.DeleteNoTraceDir(dir1);

        // The usual argument: Jay's filtered copy of the config is still readable by the game.
        var good = VersionWith("good", "net.example.Main", logArg: "-Dlog4j.configurationFile=${path}");
        fromVersion.Clear();
        jayFiles.Clear();
        args = launcher.BuildArguments(Options(good, noTrace: true), null, out var dir2, fromVersion: fromVersion, jayFiles: jayFiles);
        var config = Assert.Single(jayFiles);
        Assert.StartsWith(Path.Combine(_data, "notrace"), config);
        Assert.Contains("-Dlog4j.configurationFile=" + config, args);
        grants = GameSandbox.For(Path.Combine(_game, "versions", good), _game, Path.Combine(_game, "java", "bin", "java"),
            GameSandbox.Without(args, fromVersion), SandboxOptions.Off, jayFiles);
        Assert.Contains(grants.ReadOnly, p => GameSandbox.SamePath(p, config));
        launcher.DeleteNoTraceDir(dir2);
    }
}
