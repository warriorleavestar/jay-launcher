using System.Text.Json.Nodes;
using Amber.Core.Auth;
using Amber.Core.Bake;
using Amber.Core.Game;
using Amber.Core.Launch;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>"Start and go to…": where a start goes, and how the universal loader is asked to do it (interface 1.3.3).</summary>
public sealed class QuickPlayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-quickplay-" + Guid.NewGuid().ToString("N")[..8]);
    private string Saves => Path.Combine(_root, "saves");

    public QuickPlayTests() => Directory.CreateDirectory(Saves);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void World(string folder)
    {
        Directory.CreateDirectory(Path.Combine(Saves, folder));
        File.WriteAllBytes(Path.Combine(Saves, folder, "level.dat"), [0]);
    }

    [Fact]
    public void NeverChosenMeansTheServerWhenOneIsSet()
    {
        Assert.Equal(StartInto.Title, QuickPlay.Parse(null, null));
        Assert.Equal(StartInto.Server, QuickPlay.Parse(null, "mc.example.org"));
        // An explicit title wins over an old server setting.
        Assert.Equal(StartInto.Title, QuickPlay.Parse("title", "mc.example.org"));
        foreach (var into in Enum.GetValues<StartInto>()) Assert.Equal(into, QuickPlay.Parse(QuickPlay.Key(into), null));
    }

    [Fact]
    public void TheRememberedChoiceDecidesTheStart()
    {
        World("Survival");
        var (world, note) = QuickPlay.Choose(null, null, new InstanceSettings { StartInto = "world", StartWorld = "Survival" }, null, Saves);
        Assert.Equal("Survival", world?.World);
        Assert.Null(note);

        var (server, _) = QuickPlay.Choose(null, null, new InstanceSettings { StartInto = "server", JoinServer = " mc.example.org:25566 " }, null, Saves);
        Assert.Equal("mc.example.org:25566", server?.Server);

        var (title, titleNote) = QuickPlay.Choose(null, null, new InstanceSettings { StartInto = "title", JoinServer = "mc.example.org" }, null, Saves);
        Assert.Null(title);
        Assert.Null(titleNote);
    }

    [Fact]
    public void AnAskedForTargetWinsOverTheRememberedOne()
    {
        World("Creative");
        var settings = new InstanceSettings { StartInto = "server", JoinServer = "mc.example.org" };
        Assert.Equal("Creative", QuickPlay.Choose("Creative", null, settings, null, Saves).Target?.World);
        // A world's [Enter] never also joins the remembered server.
        Assert.Null(QuickPlay.Choose("Creative", null, settings, null, Saves).Target?.Server);
        Assert.Equal("127.0.0.1:40001", QuickPlay.Choose(null, "127.0.0.1:40001", settings, null, Saves).Target?.Server);
    }

    [Fact]
    public void TheRoomOnlyWhileInOne_ElseTheTitleScreenWithANote()
    {
        var settings = new InstanceSettings { StartInto = "room" };
        Assert.Equal("127.0.0.1:52011", QuickPlay.Choose(null, null, settings, "127.0.0.1:52011", Saves).Target?.Server);
        var (none, note) = QuickPlay.Choose(null, null, settings, null, Saves);
        Assert.Null(none);
        Assert.Equal("quickplay.note.no_room", note);
    }

    [Theory]
    [InlineData("Gone")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("C:x")]
    public void AMissingOrUnsafeWorldOpensTheTitleScreen(string folder)
    {
        var (target, note) = QuickPlay.Choose(null, null, new InstanceSettings { StartInto = "world", StartWorld = folder }, null, Saves);
        Assert.Null(target);
        Assert.Equal("quickplay.note.no_world", note);
    }

    [Fact]
    public void TheRequestFieldIsExactlyOneTarget()
    {
        Assert.Equal("""{"singleplayer":"New World"}""", QuickPlay.ToWorld("New World").ToRequest().ToJsonString());
        Assert.Equal("""{"multiplayer":"mc.example.org:25565"}""", QuickPlay.ToServer("mc.example.org:25565").ToRequest().ToJsonString());
    }

    [Fact]
    public void TheLoadersEchoIsReadFromTheLaunchBlock_AndSurvivesTheRecord()
    {
        var result = BakeService.ParseLine("""{"type":"result","stateKey":"k","launch":{"mainClass":"amber.ul.Launcher","classpath":[],"jvmArgs":["-Damber.ul.legacy.quickPlay=W"],"gameArgsPrefix":[],"stateFile":"s","quickPlay":{"singleplayer":"W","via":"amber-ul"}}}""")!;
        var launch = BakeService.ParseLaunch(result);
        Assert.Equal("W", launch.QuickPlay?.World);
        Assert.Equal("amber-ul", launch.QuickPlay?.Via);

        BakeRecord.Write(_root, new BakeRecord("key", launch));
        var back = BakeRecord.Read(_root)!.Launch;
        Assert.True(back.QuickPlay!.SameTarget(QuickPlay.ToWorld("W")));
        Assert.Equal("amber-ul", back.QuickPlay.Via);

        var plain = BakeService.ParseLaunch(BakeService.ParseLine("""{"type":"result","launch":{"mainClass":"m","stateFile":"s"}}""")!);
        Assert.Null(plain.QuickPlay);
    }

    [Fact]
    public void APreviousBakeIsReusedOnlyForTheSameTarget()
    {
        Assert.True(BakeService.SameQuickPlay(null, null));
        Assert.True(BakeService.SameQuickPlay(QuickPlay.ToServer("a:1") with { Via = "game" }, QuickPlay.ToServer("a:1")));
        Assert.False(BakeService.SameQuickPlay(null, QuickPlay.ToServer("a:1")));
        Assert.False(BakeService.SameQuickPlay(QuickPlay.ToWorld("a"), null));
        Assert.False(BakeService.SameQuickPlay(QuickPlay.ToWorld("a"), QuickPlay.ToServer("a")));
    }

    [Theory]
    [InlineData("1.3.2", false)]
    [InlineData("1.3", false)]
    [InlineData("1.3.3", true)]
    [InlineData("1.4", true)]
    public void OnlyALoaderWithInterface133GetsQuickPlay(string iface, bool can)
    {
        var info = new BakeLoaderInfo("x", 1, Version.Parse(iface), new Dictionary<string, IReadOnlyList<string>>());
        Assert.Equal(can, info.CanQuickPlay);
    }

    [Theory]
    [InlineData("1.3.2", false)]
    [InlineData("1.3.3", true)]
    [InlineData("1.3.10", true)]
    public void TheRevisionCounts_NotJustTheInterface(string revision, bool can)
    {
        var info = BakeLoaderInfo.Parse(BakeService.ParseLine(
            $$$"""{"type":"result","loaderVersion":"x","protocol":1,"interface":"1.3","interfaceRevision":"{{{revision}}}","platforms":{}}""")!);
        Assert.Equal(can, info.CanQuickPlay);
        Assert.True(info.CanWarm);
    }

    private string Game => Path.Combine(_root, "game");

    /// <summary>A 1.20+ style version (quick play rules in its arguments) or a legacy one (minecraftArguments only).</summary>
    private void WriteVersion(string id, bool modern)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Game, "versions", id)).FullName;
        var args = modern
            ? "\"arguments\":{\"game\":[\"--username\",\"${auth_player_name}\"," +
              "{\"rules\":[{\"action\":\"allow\",\"features\":{\"is_quick_play_multiplayer\":true}}],\"value\":[\"--quickPlayMultiplayer\",\"${quickPlayMultiplayer}\"]}," +
              "{\"rules\":[{\"action\":\"allow\",\"features\":{\"is_quick_play_singleplayer\":true}}],\"value\":[\"--quickPlaySingleplayer\",\"${quickPlaySingleplayer}\"]}," +
              "{\"rules\":[{\"action\":\"allow\",\"features\":{\"has_quick_plays_support\":true}}],\"value\":[\"--quickPlayPath\",\"${quickPlayPath}\"]}]," +
              "\"jvm\":[\"-cp\",\"${classpath}\"]}"
            : "\"minecraftArguments\":\"--username ${auth_player_name}\"";
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            "{\"id\":\"" + id + "\",\"type\":\"release\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]," + args + "}");
        File.WriteAllText(Path.Combine(dir, id + ".jar"), "jar");
    }

    private List<string> Args(string id, string? server, string? world, BakedLaunch? baked) =>
        new GameLauncher(new GameDirectory(Game)).BuildArguments(new LaunchOptions
        {
            VersionId = id,
            JavaPath = "java",
            Session = new GameSession("Tester", Guid.NewGuid(), "0", "legacy"),
            JoinServer = server,
            JoinWorld = world,
            Baked = baked,
        });

    private static BakedLaunch Baked(IReadOnlyList<string> prefix, QuickPlay? quickPlay) =>
        new("amber.ul.Launcher", ["loader.jar"], [], prefix, "state.json", "key") { QuickPlay = quickPlay };

    [Fact]
    public void WithoutTheLoader_TheGamesOwnArgumentsAreUsed()
    {
        WriteVersion("1.21.1", modern: true);
        WriteVersion("1.12.2", modern: false);
        var modern = Args("1.21.1", "mc.example.org:25566", null, null);
        Assert.Equal(["--quickPlayMultiplayer", "mc.example.org:25566"], modern.SkipWhile(a => a != "--quickPlayMultiplayer").Take(2));
        Assert.DoesNotContain("--server", modern);
        Assert.DoesNotContain("--quickPlayPath", modern);
        Assert.Equal(["--quickPlaySingleplayer", "Survival"], Args("1.21.1", null, "Survival", null).SkipWhile(a => a != "--quickPlaySingleplayer").Take(2));

        var legacy = Args("1.12.2", "mc.example.org:25566", null, null);
        Assert.Equal(["--server", "mc.example.org", "--port", "25566"], legacy.SkipWhile(a => a != "--server").Take(4));
    }

    [Fact]
    public void WhenTheLaunchBlockCarriesQuickPlay_JayAddsNoneOfItsOwn()
    {
        WriteVersion("1.21.1", modern: true);
        WriteVersion("1.12.2", modern: false);
        // 1.21.1: the loader ends gameArgsPrefix with the game's own flag; the version's rules must stay off.
        var modern = Args("1.21.1", "mc.example.org:25566", null,
            Baked(["--state", "state.json", "--", "--quickPlayMultiplayer", "mc.example.org:25566"], QuickPlay.ToServer("mc.example.org:25566") with { Via = "game" }));
        Assert.Single(modern, a => a == "--quickPlayMultiplayer");
        Assert.DoesNotContain("--quickPlaySingleplayer", modern);
        Assert.DoesNotContain("--quickPlayPath", modern);

        // 1.12.2: the loader's --server/--port only, never a second pair.
        var legacy = Args("1.12.2", "mc.example.org:25566", null,
            Baked(["--server", "mc.example.org", "--port", "25566"], QuickPlay.ToServer("mc.example.org:25566") with { Via = "game" }));
        Assert.Single(legacy, a => a == "--server");
        Assert.Single(legacy, a => a == "--port");

        // 1.12.2 singleplayer through the loader hook: nothing in the game arguments.
        var world = Args("1.12.2", null, "Survival", Baked([], QuickPlay.ToWorld("Survival") with { Via = "amber-ul" }));
        Assert.DoesNotContain("--server", world);
        Assert.DoesNotContain("Survival", world);
    }
}
