using System.Text.Json.Nodes;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Bake;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>World maintenance (bake interface 1.3.3): the request, the loader's echo, progress lines and the resumable place.</summary>
public sealed class WorldMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-maint-" + Guid.NewGuid().ToString("N")[..8]);

    public WorldMaintenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void TheRequestFieldHasTheWorldTheUpgradeAndTheRadius()
    {
        Assert.Equal("""{"world":"Survival","upgrade":true,"pregenRadius":128}""", WorldMaintenance.Pregen("Survival", 128).ToRequest().ToJsonString());
        Assert.Equal("""{"world":"Old","upgrade":true,"pregenRadius":0}""", WorldMaintenance.UpgradeOnly("Old").ToRequest().ToJsonString());
        Assert.Equal(WorldMaintenance.MaxRadius, WorldMaintenance.Pregen("W", 4096).PregenRadius);
    }

    [Theory]
    [InlineData(0, 17 * 17)]
    [InlineData(64, 129 * 129)]
    [InlineData(256, 513 * 513)]
    public void TheSquareIncludesTheSpawnArea(int radius, long chunks) => Assert.Equal(chunks, WorldMaintenance.Chunks(radius));

    [Fact]
    public void TheEstimateIsARangeThatShrinksWithWhatIsDone()
    {
        var (low, high) = WorldMaintenance.Duration(128);
        Assert.True(low < high && low > TimeSpan.Zero);
        var (_, laterHigh) = WorldMaintenance.Duration(128, alreadyDone: WorldMaintenance.Chunks(64));
        Assert.True(laterHigh < high);
        Assert.Equal(0, WorldMaintenance.DiskBytes(64, alreadyDone: WorldMaintenance.Chunks(64)));
    }

    [Fact]
    public void TheLaunchEchoIsReadAndSurvivesTheRecord()
    {
        var result = BakeService.ParseLine("""{"type":"result","stateKey":"k","launch":{"mainClass":"amber.ul.Launcher","stateFile":"s","maintenance":{"world":"W","upgrade":"needed","worldDataVersion":3465,"gameDataVersion":3955,"spawnRadius":8,"pregenRadius":64,"progressFile":"W/amber-ul-pregen.json","cancelFile":"c/maintenance.cancel"}}}""")!;
        var launch = BakeService.ParseLaunch(result);
        Assert.Equal(new MaintenanceLaunch("W", "needed", 8, 64, "W/amber-ul-pregen.json", "c/maintenance.cancel"), launch.Maintenance);
        BakeRecord.Write(_root, new BakeRecord("key", launch));
        Assert.Equal(launch.Maintenance, BakeRecord.Read(_root)!.Launch.Maintenance);
        Assert.Null(BakeService.ParseLaunch(BakeService.ParseLine("""{"type":"result","launch":{"mainClass":"m","stateFile":"s"}}""")!).Maintenance);
    }

    [Fact]
    public void CancelWritesTheLoadersCancelFile()
    {
        var file = Path.Combine(_root, ".amber-ul", "maintenance.cancel");
        Assert.True(new MaintenanceLaunch("W", "off", 8, 0, null, file).RequestCancel());
        Assert.True(File.Exists(file));
        Assert.False(new MaintenanceLaunch("W", "off", 8, 0, null, null).RequestCancel());
    }

    [Fact]
    public void ProgressLinesAreReadFromTheGamesOutput()
    {
        var p = MaintenanceProgress.Parse("""[12:00:01] [main/INFO]: @amber-ul/progress {"type":"progress","schema":"amber-ul/maintenance-progress@1","stage":"maintenance","step":"pregen","done":300,"total":1200,"world":"W"}""")!;
        Assert.Equal(("pregen", 300L, 1200L, "W"), (p.Step, p.Done, p.Total, p.World));
        Assert.Equal(0.25, p.Fraction);
        Assert.False(p.IsEnd);
        Assert.Null(MaintenanceProgress.Parse("""@amber-ul/progress {"schema":"amber-ul/maintenance-progress@1","step":"upgrade","done":5,"total":9}""")!.Fraction);
        Assert.True(MaintenanceProgress.Parse("""@amber-ul/progress {"schema":"amber-ul/maintenance-progress@1","step":"cancelled"}""")!.IsEnd);
        Assert.Null(MaintenanceProgress.Parse("""@amber-ul/progress {"schema":"amber-ul/warm-progress@1","step":"done"}"""));
        Assert.Null(MaintenanceProgress.Parse("@amber-ul/progress {broken"));
        Assert.Null(MaintenanceProgress.Parse("plain game output"));
    }

    [Fact]
    public void TheResumablePlaceIsRead()
    {
        var world = Directory.CreateDirectory(Path.Combine(_root, "W")).FullName;
        Assert.Null(PregenState.Read(world));
        File.WriteAllText(Path.Combine(world, PregenState.FileName),
            """{"schema":"amber-ul/pregen@1","dimension":"minecraft:overworld","centerX":0,"centerZ":0,"radius":128,"done":5000,"total":66049,"complete":false}""");
        var state = PregenState.Read(world)!;
        Assert.Equal((128, 5000L, false), (state.Radius, state.Done, state.Complete));
        Assert.Equal(5000, state.DoneFor(128));
        Assert.Equal(289, PregenState.Read(world)!.DoneFor(0));
    }

    [Fact]
    public void TheRunEndsAsItsLastLineSays()
    {
        MaintenanceRun Run(string? step, string? message = null)
        {
            var run = new MaintenanceRun(WorldMaintenance.Pregen("W", 64));
            if (step is not null) run.Report(new MaintenanceProgress(step, 0, 0, "W", message));
            return run;
        }
        Assert.Equal(TaskState.Succeeded, HomeViewModel.MaintenanceOutcome(Run("done"), 0).State);
        Assert.Equal(TaskState.Cancelled, HomeViewModel.MaintenanceOutcome(Run("cancelled"), 0).State);
        Assert.Equal(TaskState.Failed, HomeViewModel.MaintenanceOutcome(Run("failed", "disk full"), 0).State);
        Assert.Contains("1", HomeViewModel.MaintenanceOutcome(Run("pregen"), 1).Message);
        var cancelled = Run(null);
        cancelled.Cancel();
        Assert.Equal(TaskState.Cancelled, HomeViewModel.MaintenanceOutcome(cancelled, 0).State);
    }

    [Fact]
    public void ACancelBeforeTheGameRunsStopsThePreparation_AndOneAfterReachesTheLoader()
    {
        var run = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        var stopped = false;
        run.WhilePreparing(() => stopped = true);
        run.Cancel();
        Assert.True(stopped);

        var file = Path.Combine(_root, "maintenance.cancel");
        var later = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        later.Launched(new MaintenanceLaunch("W", "needed", 8, 0, null, file));
        later.Cancel();
        Assert.True(File.Exists(file));
    }
}
