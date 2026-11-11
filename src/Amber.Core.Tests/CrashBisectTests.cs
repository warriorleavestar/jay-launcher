using Amber.Core.Content;

namespace Amber.Core.Tests;

/// <summary>"Help me fix": halving the mods to find the one that crashes, across restarts (official design J6).</summary>
public sealed class CrashBisectTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "jay-bisect-test-" + Guid.NewGuid().ToString("N")[..8]);
    private string Mods => Path.Combine(_work, "mods");

    public CrashBisectTests()
    {
        Directory.CreateDirectory(Mods);
        foreach (var name in new[] { "a.jar", "b.jar", "c.jar", "d.jar", "e.jar", "f.jar", "g.jar" }) File.WriteAllText(Path.Combine(Mods, name), name);
        File.WriteAllText(Path.Combine(Mods, "old.jar.disabled"), "off before");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private bool Crashes(string culprit) => File.Exists(Path.Combine(Mods, culprit));

    [Theory]
    [InlineData("a.jar")]
    [InlineData("d.jar")]
    [InlineData("g.jar")]
    public void FindsTheCulpritAcrossRestartsAndRestoresEverything(string culprit)
    {
        var bisect = CrashBisect.Start(_work);
        Assert.Equal(7, bisect.Candidates.Count);
        Assert.DoesNotContain("old.jar", bisect.Candidates);
        var rounds = 0;
        while (bisect.Found is null && rounds++ < 10)
        {
            bisect.NextRound();
            // Jay closes and opens again between rounds: the session comes back from the instance.
            bisect = CrashBisect.Load(_work)!;
            Assert.True(bisect.AwaitingAnswer);
            bisect.Answer(stillCrashes: Crashes(culprit));
            bisect = CrashBisect.Load(_work)!;
        }
        Assert.Equal(culprit, bisect.Found);
        Assert.True(rounds <= 3);
        bisect.RestoreAll();
        Assert.Null(CrashBisect.Load(_work));
        Assert.Equal(7, Directory.EnumerateFiles(Mods, "*.jar").Count());
        Assert.True(File.Exists(Path.Combine(Mods, "old.jar.disabled")));
    }

    [Fact]
    public void RestoreInTheMiddleTurnsTheHalfBackOn()
    {
        var bisect = CrashBisect.Start(_work);
        var off = bisect.NextRound();
        Assert.Equal(3, off.Count);
        Assert.All(off, name => Assert.True(File.Exists(Path.Combine(Mods, name + ".disabled"))));
        CrashBisect.Load(_work)!.RestoreAll();
        Assert.Equal(7, Directory.EnumerateFiles(Mods, "*.jar").Count());
        Assert.Empty(Directory.EnumerateFiles(Mods, "*.jar.disabled").Where(f => !f.EndsWith("old.jar.disabled")));
    }

    [Fact]
    public void NextRoundWhileWaitingDoesNothing()
    {
        var bisect = CrashBisect.Start(_work);
        var first = bisect.NextRound();
        Assert.Equal(first, bisect.NextRound());
        Assert.Equal(1, bisect.Round);
        Assert.Equal(3, bisect.RoundsLeft);
    }

    /// <summary>
    /// A planted session file (from a modpack or another program) must not make [Help me fix…] rename anything outside mods.
    /// </summary>
    [Theory]
    [InlineData("../options.txt")]
    [InlineData("..\\options.txt")]
    [InlineData("../saves/world/level.dat")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\x.jar")]
    [InlineData("sub/a.jar")]
    [InlineData("options.txt")]
    [InlineData("..")]
    public void APlantedSessionWithPathsIsIgnored(string name)
    {
        var victim = Path.Combine(_work, "options.txt");
        File.WriteAllText(victim, "keep");
        var json = System.Text.Json.JsonSerializer.Serialize(new { StartedAt = DateTimeOffset.Now, Candidates = new[] { "a.jar", name }, Disabled = new[] { name }, Round = 1 });
        File.WriteAllText(CrashBisect.PathFor(_work), json);
        var bisect = CrashBisect.Load(_work);
        Assert.Null(bisect);
        Assert.True(File.Exists(victim));
        Assert.False(File.Exists(victim + ".disabled"));
    }

    [Theory]
    [InlineData("a.jar", true)]
    [InlineData("Some Mod-1.2.JAR", true)]
    [InlineData("../a.jar", false)]
    [InlineData("a/b.jar", false)]
    [InlineData("a.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ModFileNames(string? name, bool ok) => Assert.Equal(ok, CrashBisect.IsModFileName(name));
}
