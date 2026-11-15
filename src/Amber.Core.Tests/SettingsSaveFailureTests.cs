using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>Settings that cannot be written show an error instead of throwing on the UI thread.</summary>
public sealed class SettingsSaveFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-settings-save-" + Guid.NewGuid().ToString("N"));

    public SettingsSaveFailureTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private LauncherService Service() => new(new AppSettings
    {
        GameDirectory = _root,
        SavePathForTest = Path.Combine(_root, "settings.json"),
        PlayerName = "",
        AutoJavaDownload = false,
    });

    [Fact]
    public void DeniedSettingsWriteShowsAnError() => UiTestThread.Run(() =>
    {
        using var service = Service();
        var vm = new SettingsViewModel(service);
        // The settings file is written through a temporary file; a folder in its place makes the write fail with access denied.
        Directory.CreateDirectory(Path.Combine(_root, "settings.json.tmp"));

        vm.AutoJavaDownload = !vm.AutoJavaDownload;
        Assert.Equal("settings.error.save_failed", vm.StatusInfo?.Key);

        vm.SetStatus(null);
        try
        {
            vm.SelectedDensityIndex = 1;
            Assert.Equal("settings.error.save_failed", vm.StatusInfo?.Key);
        }
        finally
        {
            vm.SelectedDensityIndex = 0;
        }
    });

    [Fact]
    public void BadRestartDelayIsReportedAsSavedWithErrors() => UiTestThread.Run(() =>
    {
        using var service = Service();
        var vm = new SettingsViewModel(service);
        vm.BreakerRestartDelay = "not a number";
        Assert.True(vm.HasBreakerDelayError);
        Assert.Equal("settings.saved_with_errors", vm.StatusInfo?.Key);
    });
}
