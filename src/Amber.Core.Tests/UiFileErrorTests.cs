using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>
/// File errors in UI actions (a locked file, a missing folder, no permission) end as an error status, never as an
/// exception on the UI thread: release builds have no handler there, so one would close Jay.
/// </summary>
public sealed class UiFileErrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-ui-file-errors-" + Guid.NewGuid().ToString("N"));

    public UiFileErrorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private LauncherService Service(string? settingsPath = null) => new(new AppSettings
    {
        GameDirectory = _root,
        SavePathForTest = settingsPath ?? Path.Combine(_root, "settings.json"),
        PlayerName = "",
        AutoJavaDownload = false,
    });

    [Fact]
    public void AddFilesSkipsItselfAndReportsAFailedCopyWithoutStoppingTheRest() => UiTestThread.Run(() =>
    {
        using var service = Service();
        var vm = new InstanceViewModel(service);
        var target = Directory.CreateDirectory(Path.Combine(_root, "mods")).FullName;
        var already = Path.Combine(target, "already.jar");
        File.WriteAllText(already, "in place");
        var good = Path.Combine(_root, "good.jar");
        File.WriteAllText(good, "new");
        var missing = Path.Combine(_root, "missing.jar");

        vm.AddFiles([missing, already, good], target);

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "good.jar")));
        Assert.Equal("in place", File.ReadAllText(already));
        Assert.Equal(StatusKind.Error, vm.StatusInfo?.Kind);
        Assert.Equal("instance.files.add_failed", vm.StatusInfo?.Key);
    });

    [Fact]
    public void AddFilesPickedFromTheTargetFolderIsNotAnError() => UiTestThread.Run(() =>
    {
        using var service = Service();
        var vm = new InstanceViewModel(service);
        var target = Directory.CreateDirectory(Path.Combine(_root, "mods")).FullName;
        var already = Path.Combine(target, "already.jar");
        File.WriteAllText(already, "in place");

        vm.AddFiles([already], target);

        Assert.Equal("in place", File.ReadAllText(already));
        Assert.NotEqual(StatusKind.Error, vm.StatusInfo?.Kind);
    });

    [Fact]
    public void ExportToAnUnwritablePlaceBecomesAnErrorStatus() => UiTestThread.Run(() =>
    {
        using var service = Service();
        var home = new HomeViewModel(service, new LogViewModel());
        // A file where the folder should be: any write below it fails.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var path = Path.Combine(blocker, "report.zip");

        home.ExportFailureReport(path);
        Assert.Equal("common.save_file_failed", home.StatusInfo?.Key);
        Assert.Equal(StatusKind.Error, home.StatusInfo?.Kind);

        home.ExportCrashBundle(path);
        Assert.Equal("common.save_file_failed", home.StatusInfo?.Key);
        Assert.False(File.Exists(path));
    });

    [Fact]
    public void SaveAndReloadStillAppliesWhenTheSettingsFileCannotBeWritten() => UiTestThread.Run(() =>
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        using var service = Service(Path.Combine(blocker, "settings.json"));
        Exception? reported = null;
        service.SettingsSaveFailed += e => reported = e;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;

        service.Settings.GameDirectory = other;
        service.SaveAndReload();

        Assert.IsAssignableFrom<IOException>(reported);
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(service.Directory.Root));
    });
}
