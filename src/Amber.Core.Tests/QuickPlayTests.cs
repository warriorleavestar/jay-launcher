using System.Text.Json.Nodes;
using Amber.Core.Bake;
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
}
