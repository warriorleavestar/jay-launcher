using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>
/// The instance settings page when Jay can't use or can't save what the settings file holds: the app keeps running,
/// what the player entered stays on the page, and the page says what happened.
/// </summary>
public sealed class InstanceSettingsPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-instance-settings-page-" + Guid.NewGuid().ToString("N"));

    public InstanceSettingsPageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private (LauncherService Service, InstanceViewModel Page) Open()
    {
        InstanceSettingsTrust.StorePathOverride = Path.Combine(_root + "-data", "instance-settings-trust.json");
        var dir = Path.Combine(_root, "versions", "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"), """{"id":"a","mainClass":"Example","libraries":[]}""");
        File.WriteAllBytes(Path.Combine(dir, "a.jar"), []);
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        return (service, new InstanceViewModel(service));
    }

    [Fact]
    public void AFailedSaveKeepsWhatThePlayerEnteredAndSaysSo() => UiTestThread.Run(() =>
    {
        try
        {
            var (service, page) = Open();
            page.SelectedId = "a";
            page.TabIndex = (int)InstanceTab.Settings;
            var lockPath = InstanceSettingsTrust.StorePath + ".lock";
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            // Another Jay holds the record for longer than a save waits.
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                page.JvmArgs = "-Dkept=1"; // saves on change
                Assert.Equal("instance.settings.save_failed", page.StatusInfo?.Key);
                Assert.Equal("-Dkept=1", page.JvmArgs);
                Assert.Null(InstanceSettings.Load(service.Directory, "a").JvmArgs);
            }
            // The next change saves what is on the page.
            page.GameArgs = "--next";
            var saved = InstanceSettings.Load(service.Directory, "a");
            Assert.Equal("-Dkept=1", saved.JvmArgs);
            Assert.Equal("--next", saved.GameArgs);
        }
        finally
        {
            try { Directory.Delete(_root + "-data", true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ValuesJayDidNotSaveAreLeftOutAndSaidSoOnThePageAndAtLaunch() => UiTestThread.Run(() =>
    {
        try
        {
            var (service, page) = Open();
            // Jay has seen this folder (adopted it) before something else writes the file.
            InstanceSettings.Load(service.Directory, "a");
            File.WriteAllText(Path.Combine(_root, "versions", "a", InstanceSettings.FileName), """{"PreLaunchCommand":"echo not-jays","MaxMemoryMb":3072}""");
            page.SelectedId = "a";
            page.TabIndex = (int)InstanceTab.Settings;
            Assert.Equal("", page.PreLaunch);
            Assert.Equal("3072", page.MemoryText);
            Assert.Equal("instance.settings.dropped_not_saved", page.StatusInfo?.Key);
            // Merged with the global settings (as a launch sees them), the reason is still known.
            var effective = service.EffectiveSettings("a");
            Assert.True(effective.DroppedUnknownValues);
            Assert.Null(effective.PreLaunchCommand);
        }
        finally
        {
            try { Directory.Delete(_root + "-data", true); } catch (IOException) { }
        }
    });
}
