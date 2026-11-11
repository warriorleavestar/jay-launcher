using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>UI review: a file the user picked that can't be written or read becomes an error status, not an exception that closes Jay.</summary>
public sealed class FileErrorStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-file-errors-" + Guid.NewGuid().ToString("N"));

    public FileErrorStatusTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private LauncherService NewService() => new(new AppSettings
    {
        GameDirectory = _root,
        SavePathForTest = Path.Combine(_root, "settings.json"),
        PlayerName = "",
        AutoJavaDownload = false,
    });

    /// <summary>A path that can't be written as a file: it is an existing folder.</summary>
    private string Blocked()
    {
        var path = Path.Combine(_root, "blocked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertSaveFailed(PageViewModel vm, string path)
    {
        var status = vm.StatusInfo;
        Assert.NotNull(status);
        Assert.Equal(StatusKind.Error, status!.Kind);
        Assert.Equal("common.save_file_failed", status.Key);
        Assert.Equal(path, status.Args[0]);
        Assert.False(string.IsNullOrEmpty(status.Detail));
    }

    [Fact]
    public void ExportingChatToAPlaceThatCantBeWrittenShowsAnError() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new MultiplayerViewModel(service);
        var path = Blocked();
        vm.ExportChat(path);
        AssertSaveFailed(vm, path);

        var ok = Path.Combine(_root, "chat.txt");
        vm.ExportChat(ok);
        Assert.Equal(StatusKind.Success, vm.StatusInfo?.Kind);
        Assert.True(File.Exists(ok));
    });

    [Fact]
    public void ExportingDiagnosticsToAPlaceThatCantBeWrittenShowsAnError() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new MultiplayerViewModel(service);
        var path = Blocked();
        vm.ExportDiagnostics(path);
        AssertSaveFailed(vm, path);
    });

    [Fact]
    public void ExportingAFailureReportToAPlaceThatCantBeWrittenShowsAnError() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var home = new HomeViewModel(service, new LogViewModel());
        var path = Blocked();
        home.ExportFailureReport(path);
        AssertSaveFailed(home, path);
        home.ExportCrashBundle(path);
        AssertSaveFailed(home, path);
    });

    [Fact]
    public void OneUnreadableFileDoesNotStopTheRestBeingAdded() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new InstanceViewModel(service);
        var source = Path.Combine(_root, "picked");
        Directory.CreateDirectory(source);
        var good = Path.Combine(source, "good.jar");
        File.WriteAllText(good, "jar");
        var missing = Path.Combine(source, "gone.jar");
        var target = Path.Combine(_root, "mods");

        vm.AddFiles([missing, good], target);

        Assert.True(File.Exists(Path.Combine(target, "good.jar")));
        var status = vm.StatusInfo;
        Assert.Equal(StatusKind.Error, status?.Kind);
        Assert.Equal("instance.files.add_failed", status!.Key);
        Assert.Equal("gone.jar", status.Args[0]);
    });

    [Fact]
    public void PickingAFileThatIsAlreadyInTheTargetFolderKeepsIt() => UiTestThread.Run(() =>
    {
        using var service = NewService();
        var vm = new InstanceViewModel(service);
        var target = Path.Combine(_root, "mods");
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "same.jar");
        File.WriteAllText(file, "jar");

        vm.AddFiles([file], target);

        Assert.Equal("jar", File.ReadAllText(file));
        Assert.NotEqual(StatusKind.Error, vm.StatusInfo?.Kind);
    });

    [Fact]
    public void SaveAndReloadReportsASettingsFileThatCantBeWritten()
    {
        var settings = new AppSettings { GameDirectory = _root, SavePathForTest = Blocked(), PlayerName = "", AutoJavaDownload = false };
        using var service = new LauncherService(settings);
        Exception? reported = null;
        var reloaded = false;
        service.SettingsSaveFailed += e => reported = e;
        service.SettingsChangedElsewhere += () => reloaded = true;

        service.SaveAndReload();

        Assert.True(reported is IOException or UnauthorizedAccessException, reported?.GetType().Name);
        Assert.True(reloaded);
    }
}
