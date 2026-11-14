using System.Text.Json.Nodes;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Bake;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Threading;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>World maintenance (bake interface 1.3.3): the request, the loader's echo, progress lines and the resumable place.</summary>
public sealed class WorldMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-maint-" + Guid.NewGuid().ToString("N")[..8]);

    public WorldMaintenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

    private string Instance => Directory.CreateDirectory(Path.Combine(_root, "game", "versions", "W")).FullName;

    private string CancelPath(string instance) => Path.Combine(instance, ".amber-ul", "maintenance.cancel");

    private static MaintenanceLaunch Echo(string? cancelFile) => new("W", "needed", 8, 0, null, cancelFile);

    /// <summary>A place outside the instance with a file that must never change.</summary>
    private (string Folder, string Sentinel, byte[] Bytes) Outside()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "game", "versions", "other", "saves", "World")).FullName;
        var sentinel = Path.Combine(folder, "level.dat");
        byte[] bytes = [0x1f, 0x8b, 0x08, 0x00, 0x42];
        File.WriteAllBytes(sentinel, bytes);
        return (folder, sentinel, bytes);
    }

    private static void FolderLink(string link, string target)
    {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(link, target);
        else Directory.CreateSymbolicLink(link, target);
    }

    /// <summary>A file symbolic link; false where this account may not make one (Windows without developer mode).</summary>
    private static bool FileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void CancelWritesTheLoadersCancelFile()
    {
        var instance = Instance;
        var file = CancelPath(instance);
        Assert.True(Echo(file).RequestCancel(instance));
        Assert.True(File.Exists(file));
        Assert.True(Echo(file).RequestCancel(instance));
        Assert.False(Echo(null).RequestCancel(instance));
        Assert.Empty(Directory.GetFiles(instance, ".jay-write-*"));
    }

    [Fact]
    public void ACancelFileNamedAnywhereElseIsNeverWritten()
    {
        var instance = Instance;
        var (folder, sentinel, bytes) = Outside();
        Assert.False(Echo(sentinel).RequestCancel(instance));
        Assert.False(Echo(Path.Combine(folder, "maintenance.cancel")).RequestCancel(instance));
        Assert.False(Echo(Path.Combine(instance, ".amber-ul", "..", "..", "other", "saves", "World", "level.dat")).RequestCancel(instance));
        Assert.False(Echo(Path.Combine(instance, "maintenance.cancel")).RequestCancel(instance));
        Assert.Equal(bytes, File.ReadAllBytes(sentinel));
        Assert.False(File.Exists(Path.Combine(folder, "maintenance.cancel")));
        Assert.False(File.Exists(Path.Combine(instance, "maintenance.cancel")));
    }

    [Fact]
    public void ACancelFileThatIsALinkIsNotWrittenThrough()
    {
        var instance = Instance;
        var (_, sentinel, bytes) = Outside();
        var file = CancelPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!FileLink(file, sentinel)) return;
        Echo(file).RequestCancel(instance);
        Assert.Equal(bytes, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void ACancelFolderThatIsALinkIsNotWrittenThrough()
    {
        var instance = Instance;
        var (folder, sentinel, bytes) = Outside();
        File.Copy(sentinel, Path.Combine(folder, "maintenance.cancel"));
        FolderLink(Path.Combine(instance, ".amber-ul"), folder);
        Assert.False(Echo(CancelPath(instance)).RequestCancel(instance));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(folder, "maintenance.cancel")));
        Assert.Equal(bytes, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void ACancelFolderSwappedForALinkMeanwhileIsNeverWrittenThrough()
    {
        // The game swaps the folder (and the file) for links as fast as it can while Jay asks to cancel.
        var instance = Instance;
        var (folder, sentinel, bytes) = Outside();
        var outsideCancel = Path.Combine(folder, "maintenance.cancel");
        File.WriteAllBytes(outsideCancel, bytes);
        var real = Path.Combine(instance, ".amber-ul");
        var aside = Path.Combine(instance, ".amber-ul-real");
        Directory.CreateDirectory(real);
        var fileLinks = !OperatingSystem.IsWindows() || FileLink(Path.Combine(_root, "probe"), sentinel);
        using var stop = new CancellationTokenSource();
        var swaps = 0;
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // A step that failed part way (Jay made the folder again meanwhile): put the real one back first.
                    if (Directory.Exists(aside))
                    {
                        if (Directory.Exists(real)) Directory.Delete(real, true);
                        Directory.Move(aside, real);
                    }
                    Directory.Move(real, aside);
                    FolderLink(real, folder);
                    Directory.Delete(real);
                    Directory.Move(aside, real);
                    if (fileLinks)
                    {
                        var file = Path.Combine(real, "maintenance.cancel");
                        File.Delete(file);
                        File.CreateSymbolicLink(file, sentinel);
                        File.Delete(file);
                    }
                    swaps++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            }
        });
        try
        {
            var echo = Echo(CancelPath(instance));
            var until = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < until)
            {
                echo.RequestCancel(instance);
                // Room for the game: Jay holding the folder keeps it from being swapped, which would prove nothing.
                Thread.Sleep(1);
            }
        }
        finally
        {
            stop.Cancel();
            swapper.Wait(TimeSpan.FromSeconds(10));
        }
        Assert.True(swaps > 0);
        Assert.Equal(bytes, File.ReadAllBytes(sentinel));
        Assert.Equal(bytes, File.ReadAllBytes(outsideCancel));
        Assert.Equal([sentinel, outsideCancel], Directory.GetFiles(folder).Order());
    }

    [Fact]
    public void TheCancelSentAgainAtStartDoesNotFollowALinkEither()
    {
        var instance = Instance;
        var (_, sentinel, bytes) = Outside();
        var file = CancelPath(instance);
        var run = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        run.Launched(Echo(file), instance);
        run.Cancel();
        Assert.True(File.Exists(file));
        // The loader deletes a cancel file older than the game; the game leaves a link in its place.
        File.Delete(file);
        if (FileLink(file, sentinel))
        {
            run.Report(new MaintenanceProgress("start", 0, 0, "W", null));
            Assert.Equal(bytes, File.ReadAllBytes(sentinel));
            File.Delete(file);
        }
        run.Report(new MaintenanceProgress("start", 0, 0, "W", null));
        Assert.True(File.Exists(file));
        Assert.False(new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint));
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
    public void AProgressLineIsReadUpToItsJson_WhateverFollowsOnTheLine()
    {
        var p = MaintenanceProgress.Parse("""[12:00:01] [main/INFO]: @amber-ul/progress {"schema":"amber-ul/maintenance-progress@1","step":"pregen","done":3,"total":4} [Server thread/WARN] Can't keep up!""")!;
        Assert.Equal(("pregen", 3L, 4L), (p.Step, p.Done, p.Total));
        Assert.Equal("done", MaintenanceProgress.Parse("""@amber-ul/progress {"schema":"amber-ul/maintenance-progress@1","step":"done"}{"more":1}""")!.Step);
        Assert.Equal("failed", MaintenanceProgress.Parse("""@amber-ul/progress {"schema":"amber-ul/maintenance-progress@1","step":"failed","message":"a } b"} trailing {broken""")!.Step);
        Assert.Null(MaintenanceProgress.Parse("""@amber-ul/progress ["schema"] {"schema":"amber-ul/maintenance-progress@1","step":"done"}"""));
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
    public void AnOversizedPregenFileIsIgnored()
    {
        var world = Directory.CreateDirectory(Path.Combine(_root, "W")).FullName;
        var json = """{"schema":"amber-ul/pregen@1","radius":64,"done":10,"total":20,"complete":false""";
        File.WriteAllText(Path.Combine(world, PregenState.FileName), json + ",\"pad\":\"" + new string('x', PregenState.MaxBytes) + "\"}");
        Assert.Null(PregenState.Read(world));
        File.WriteAllText(Path.Combine(world, PregenState.FileName), json + "}");
        Assert.Equal(10, PregenState.Read(world)!.Done);
    }

    [Fact]
    public void APregenFifoIsRefusedAtOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var world = Directory.CreateDirectory(Path.Combine(_root, "W")).FullName;
        using (var mk = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(world, PregenState.FileName)])) mk.WaitForExit();
        var read = Task.Run(() => PregenState.Read(world));
        Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "reading the pregen file waited on a FIFO");
        Assert.Null(read.Result);
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
    public void TheQuitAfterTheLastWordDoesNotChangeHowTheRunEnded()
    {
        // The loader's whole sequence: start … the last word … quit, then the game exits.
        MaintenanceRun Sequence(string end, string? message = null)
        {
            var run = new MaintenanceRun(WorldMaintenance.Pregen("W", 64));
            foreach (var step in new[] { "start", "upgrade", "open", "spawn", "pregen", end, "quit" })
                run.Report(new MaintenanceProgress(step, 1, 2, "W", step == end ? message : null));
            return run;
        }
        var done = Sequence("done");
        Assert.Equal("quit", done.Last?.Step);
        Assert.Equal("done", done.End?.Step);
        Assert.Equal(TaskState.Succeeded, HomeViewModel.MaintenanceOutcome(done, 0).State);
        Assert.Equal(TaskState.Cancelled, HomeViewModel.MaintenanceOutcome(Sequence("cancelled"), 0).State);
        var failed = HomeViewModel.MaintenanceOutcome(Sequence("failed", "disk full"), 0);
        Assert.Equal(TaskState.Failed, failed.State);
        Assert.Contains("disk full", failed.Message);
        // The exit code still counts: a game that said done but then exited with an error did not end well.
        var crashed = HomeViewModel.MaintenanceOutcome(Sequence("done"), 3);
        Assert.Equal(TaskState.Failed, crashed.State);
        Assert.Contains("3", crashed.Message);
        // Only quit, no last word: the game stopped on its own.
        var quitOnly = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        quitOnly.Report(new MaintenanceProgress("quit", 0, 0, "W", null));
        Assert.Equal(TaskState.Failed, HomeViewModel.MaintenanceOutcome(quitOnly, 0).State);
    }

    private LauncherService Service(string game) => new(new AppSettings
    {
        GameDirectory = game,
        SavePathForTest = Path.Combine(_root, "settings.json"),
    });

    private static void Version(string game, string id) => File.WriteAllText(
        Path.Combine(Directory.CreateDirectory(Path.Combine(game, "versions", id)).FullName, id + ".json"),
        $$"""{"id":"{{id}}","type":"release","mainClass":"x","libraries":[]}""");

    private static async Task<TaskOutcome> Settle(MaintenanceRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!run.Done.IsCompleted && DateTime.UtcNow < deadline)
        {
            UiTestThread.Run(() => Dispatcher.UIThread.RunJobs());
            await Task.Delay(10);
        }
        return await run.Done.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AGameFolderSwitchedDuringTheBackupStopsTheRun()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Version(first, "W");
        Version(second, "W");
        using var service = Service(first);
        HomeViewModel? home = null;
        UiTestThread.Run(() =>
        {
            service.Accounts.AddOffline("TestPlayer");
            home = new HomeViewModel(service, new LogViewModel());
            home.SelectedAccount = home.Accounts.First();
        });
        var context = service.Context;
        // Switched to another folder with an instance of the same name: the run must not land there.
        service.Settings.GameDirectory = second;
        service.SaveAndReload();
        var run = new MaintenanceRun(WorldMaintenance.UpgradeOnly("World")) { WorkDir = service.WorkDirFor(context, "W") };
        UiTestThread.Run(() => service.RequestLaunch(new LaunchRequest("W") { Maintenance = run, ExpectedContext = context }));
        var outcome = await Settle(run);
        Assert.Equal(TaskState.Failed, outcome.State);
        Assert.Equal(L.T("home.bake.retry_directory_changed"), outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(second, "versions", "W", "logs")));
    }

    [Fact]
    public async Task AnInstanceThatNowRunsFromAnotherFolderStopsTheRun()
    {
        var game = Path.Combine(_root, "game1");
        Version(game, "W");
        using var service = Service(game);
        HomeViewModel? home = null;
        UiTestThread.Run(() =>
        {
            service.Accounts.AddOffline("TestPlayer");
            home = new HomeViewModel(service, new LogViewModel());
            home.SelectedAccount = home.Accounts.First();
        });
        // The backup was made in a folder the instance no longer runs from (its shared-folder setting changed).
        var elsewhere = Path.Combine(_root, "elsewhere");
        Assert.NotEqual(Path.GetFullPath(elsewhere), Path.GetFullPath(service.WorkDirFor(service.Context, "W")));
        var run = new MaintenanceRun(WorldMaintenance.UpgradeOnly("World")) { WorkDir = elsewhere };
        UiTestThread.Run(() => service.RequestLaunch(new LaunchRequest("W") { Maintenance = run, ExpectedContext = service.Context }));
        var outcome = await Settle(run);
        Assert.Equal(TaskState.Failed, outcome.State);
        Assert.Equal(L.T("maintenance.directory_changed"), outcome.Message);
    }

    [Fact]
    public void ACancelBeforeTheGameRunsStopsThePreparation_AndOneAfterReachesTheLoader()
    {
        var run = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        var stopped = false;
        run.WhilePreparing(() => stopped = true);
        run.Cancel();
        Assert.True(stopped);

        var instance = Instance;
        var file = CancelPath(instance);
        var later = new MaintenanceRun(WorldMaintenance.UpgradeOnly("W"));
        later.Launched(Echo(file), instance);
        later.Cancel();
        Assert.True(File.Exists(file));
    }
}
