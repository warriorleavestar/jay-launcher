using Amber.Core.Content;
using Amber.Core.Diagnostics;
using Amber.Core.Game;
using Amber.Core.GameLibrary;
using Amber.Core.Launch;
using Xunit;

namespace Amber.Core.Tests;

public sealed class StartButtonTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static LaunchSession At(LaunchState state)
    {
        var s = new LaunchSession("a", T0);
        if (state == LaunchState.Preparing) return s;
        s.ProcessUp(T0);
        if (state == LaunchState.Entering) return s;
        if (state == LaunchState.Crashed) { s.ProcessEnded(T0, 1, crashed: true); return s; }
        if (state == LaunchState.Exited) { s.ProcessEnded(T0, 0, crashed: false); return s; }
        s.EnteredGame(T0);
        if (state == LaunchState.Playing) return s;
        s.ExitRequested(T0);
        return s;
    }

    [Fact]
    public void ReadyIsTheAmberPlayButton()
    {
        var plan = StartButton.For(null, hasAccount: true, InstanceState.Ready);
        Assert.Equal(StartKind.Ready, plan.Kind);
        Assert.True(plan.Amber);
        Assert.False(plan.Busy);
        Assert.Equal(StartSide.None, plan.Side);
    }

    [Fact]
    public void MissingAccountIsNotAmberAndSaysWhatIsMissing()
    {
        var plan = StartButton.For(null, hasAccount: false, InstanceState.Ready);
        Assert.Equal(StartKind.NeedAccount, plan.Kind);
        Assert.False(plan.Amber);
        Assert.NotNull(plan.NoteKey);
    }

    [Theory]
    [InlineData(InstanceState.Incomplete, StartKind.ResumeInstall, StartSide.InstallOnly)]
    [InlineData(InstanceState.Broken, StartKind.Repair, StartSide.OpenFolder)]
    public void InstallProblemsStartByFixing(InstanceState state, StartKind kind, StartSide side)
    {
        var plan = StartButton.For(null, true, state);
        Assert.Equal(kind, plan.Kind);
        Assert.Equal(side, plan.Side);
        Assert.True(plan.Amber);
    }

    [Theory]
    [InlineData(LaunchState.Preparing, StartKind.Preparing, StartSide.Cancel, true, true)]
    [InlineData(LaunchState.Entering, StartKind.Entering, StartSide.QuitGame, true, true)]
    [InlineData(LaunchState.Playing, StartKind.Playing, StartSide.QuitGame, false, false)]
    [InlineData(LaunchState.Exiting, StartKind.Exiting, StartSide.None, false, true)]
    public void RunningStatesKeepTheButtonAndPutStopNextToIt(LaunchState state, StartKind kind, StartSide side, bool amber, bool busy)
    {
        // A running session wins over account and install state: you can always stop what is running.
        var plan = StartButton.For(At(state), hasAccount: false, InstanceState.Broken);
        Assert.Equal(kind, plan.Kind);
        Assert.Equal(side, plan.Side);
        Assert.Equal(amber, plan.Amber);
        Assert.Equal(busy, plan.Busy);
    }

    [Fact]
    public void ForceKillAppearsOnlyWhenOffered()
    {
        Assert.Equal(StartSide.ForceKill, StartButton.For(At(LaunchState.Exiting), true, InstanceState.Ready, forceKillOffered: true).Side);
        Assert.Equal("home.button.force_kill", StartButton.For(At(LaunchState.Exiting), true, InstanceState.Ready, true).SideKey);
    }

    [Fact]
    public void AfterACrashTheButtonSaysTryAgain()
    {
        Assert.Equal(StartKind.RetryAfterCrash, StartButton.For(At(LaunchState.Crashed), true, InstanceState.Ready).Kind);
        Assert.Equal(StartKind.Ready, StartButton.For(At(LaunchState.Exited), true, InstanceState.Ready).Kind);
        // Broken files still come first: retrying would crash the same way.
        Assert.Equal(StartKind.Repair, StartButton.For(At(LaunchState.Crashed), true, InstanceState.Broken).Kind);
    }
}

public sealed class CrashFixesTests
{
    private static CrashFinding F(string key) => new("t", "a") { Key = key };

    [Fact]
    public void AnalyzerTagsFindingsWithTheirRuleKey()
    {
        var analysis = CrashAnalyzer.Analyze("Exception in thread \"main\" java.lang.OutOfMemoryError: Java heap space");
        Assert.Contains(analysis.Findings, f => f.Key == "core.crash.memory.exhausted");
        Assert.All(CrashAnalyzer.Analyze("Unsupported class file major version 65").Findings, f => Assert.NotNull(f.Key));
    }

    [Theory]
    [InlineData("core.crash.memory.exhausted", CrashFixKind.RaiseMemory)]
    [InlineData("core.crash.memory.heapreservation", CrashFixKind.RaiseMemory)]
    [InlineData("core.crash.memory.system", CrashFixKind.ViewLog)]
    [InlineData("core.crash.java.tooold", CrashFixKind.ChooseJava)]
    [InlineData("core.crash.mods.duplicate", CrashFixKind.OpenMods)]
    [InlineData("core.crash.files.corrupt", CrashFixKind.RepairFiles)]
    [InlineData("core.crash.forge.librarydownload", CrashFixKind.RepairFiles)]
    [InlineData("core.crash.files.config", CrashFixKind.ViewLog)]
    [InlineData("core.crash.graphics.shader", CrashFixKind.ShadersOff)]
    [InlineData("core.crash.world.ticking", CrashFixKind.ViewLog)]
    public void EachCauseGetsItsFix(string key, CrashFixKind kind) =>
        Assert.Equal(kind, CrashFixes.For(F(key))[0].Kind);

    [Fact]
    public void ModCausesOfferToDisableTheFirstSuspect()
    {
        var fixes = CrashFixes.For(F("core.crash.mods.mixin"), ["sodium", "iris"]);
        Assert.Equal([new CrashFix(CrashFixKind.DisableMod, "sodium"), new CrashFix(CrashFixKind.OpenMods)], fixes);
        // A suspect list is not a reason to disable a mod for a memory problem.
        Assert.DoesNotContain(CrashFixes.For(F("core.crash.memory.exhausted"), ["sodium"]), f => f.Kind == CrashFixKind.DisableMod);
    }

    [Theory]
    [InlineData("Sodium (sodium)", "Sodium", "sodium")]
    [InlineData("  Iris  ", "Iris", null)]
    [InlineData("(weird)", "(weird)", null)]
    public void SuspectsSplitIntoNameAndId(string suspect, string name, string? id) =>
        Assert.Equal((name, id), CrashFixes.SplitSuspect(suspect));

    [Fact]
    public void UntaggedFindingFallsBackToTheLog() =>
        Assert.Equal(CrashFixKind.ViewLog, CrashFixes.For(new CrashFinding("t", "a")).Single().Kind);
}

public sealed class ShaderToggleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-shader-toggle-" + Guid.NewGuid().ToString("N"));

    public ShaderToggleTests() => Directory.CreateDirectory(Path.Combine(_root, "config"));
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void TurnsIrisAndOptifineOffAndKeepsOtherLines()
    {
        File.WriteAllLines(Path.Combine(_root, "config", "iris.properties"), ["shaderPack=Complementary.zip", "enableShaders=true"]);
        File.WriteAllLines(Path.Combine(_root, "optionsshaders.txt"), ["shaderPack=BSL.zip", "antialiasingLevel=0"]);

        Assert.True(ShaderToggle.TurnOff(_root));

        Assert.Equal(["shaderPack=Complementary.zip", "enableShaders=false"], File.ReadAllLines(Path.Combine(_root, "config", "iris.properties")));
        Assert.Equal(["shaderPack=OFF", "antialiasingLevel=0"], File.ReadAllLines(Path.Combine(_root, "optionsshaders.txt")));
        Assert.False(ShaderToggle.TurnOff(_root)); // already off
    }

    [Fact]
    public void DoesNotCreateFilesForRenderersThatAreNotThere()
    {
        Assert.False(ShaderToggle.TurnOff(_root));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AddsTheKeyWhenTheFileLacksIt()
    {
        File.WriteAllLines(Path.Combine(_root, "config", "oculus.properties"), ["shaderPack=x.zip"]);
        Assert.True(ShaderToggle.TurnOff(_root));
        Assert.Contains("enableShaders=false", File.ReadAllLines(Path.Combine(_root, "config", "oculus.properties")));
    }
}

public sealed class RaisedMemoryTests
{
    [Theory]
    [InlineData(2048, 16384, 4096)]
    [InlineData(4000, 16384, 5632)]
    [InlineData(10240, 16384, 12288)]
    [InlineData(12288, 16384, 12288)]
    [InlineData(4096, 4096, 4096)]
    public void RaisesByTwoGigabytesUpToThreeQuartersOfMemory(int current, long total, int expected) =>
        Assert.Equal(expected, MemoryPlanner.RaisedMemoryMb(current, total));
}
