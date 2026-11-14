using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Regressions from the UI code review (round 1).</summary>
public sealed class UiReviewRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-ui-review-" + Guid.NewGuid().ToString("N")[..8]);

    public UiReviewRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void LockedSettingsFileRunsOnDefaultsAndIsNeverOverwritten()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"GameDirectory":"keep"}""");
        var reads = 0;
        var settings = AppSettings.Load(path, _ => { reads++; throw new IOException("locked"); });
        Assert.True(settings.ReadOnly);
        Assert.Equal(3, reads);
        settings.SavePathForTest = path;
        settings.Save();
        Assert.Contains("keep", File.ReadAllText(path));
    }

    [Fact]
    public void ReadableSettingsFileIsNotReadOnly()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"GameDirectory":"x"}""");
        var settings = AppSettings.Load(path, File.ReadAllText);
        Assert.False(settings.ReadOnly);
        Assert.Equal("x", settings.GameDirectory);
        Assert.True(AppSettings.Load(Path.Combine(_dir, "none.json"), File.ReadAllText).IsFresh);
    }

    [Fact]
    public void NullSettingsInAHandEditedFileFallBackToDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"GameDirectory":null,"StrongSandboxFolders":null,"Tutorial":null,"ChildMode":null,"Theme":null,"Instances":{"Pack":{"DisplayName":"x"}}}""");
        var settings = AppSettings.Load(path, File.ReadAllText);
        Assert.False(string.IsNullOrWhiteSpace(settings.GameDirectory));
        _ = new Amber.Core.Game.GameDirectory(settings.GameDirectory);
        Assert.NotNull(settings.StrongSandboxFolders);
        Assert.NotNull(settings.Tutorial);
        Assert.NotNull(settings.ChildMode);
        Assert.Equal("system", settings.Theme);
        Assert.Equal("x", settings.Instances!["pack"].DisplayName);
    }

    [Fact]
    public void SavedSettingsAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{}");
        new AppSettings { SavePathForTest = path }.Save();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void DownloadSourceSwitchedElsewhereSurvivesTheNextSettingsChange() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = _dir, PlayerName = "", SavePathForTest = Path.Combine(_dir, "settings.json") });
        var settings = new SettingsViewModel(service);
        service.SetDownloadSource("mirror");
        Dispatcher.UIThread.RunJobs();
        settings.Fullscreen = !settings.Fullscreen;
        Assert.Equal("mirror", service.Settings.DownloadSource);
    });
}

/// <summary>The Appearance theme list is written in the current language.</summary>
[Collection("GlobalLanguage")]
public sealed class SettingsThemeLanguageTests
{
    [Fact]
    public void BuiltInThemeNamesFollowTheLanguage() => UiTestThread.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-theme-lang-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var language = L.Language;
        try
        {
            L.SetLanguage("zh-Hans");
            using var service = new LauncherService(new AppSettings { GameDirectory = dir, PlayerName = "", SavePathForTest = Path.Combine(dir, "settings.json") });
            var settings = new SettingsViewModel(service);
            L.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(L.T("settings.themepack.default"), settings.ThemePacks[0].Label);
            Assert.Equal(L.T("settings.themepack.builtin.jay-high-contrast"), settings.ThemePacks.Single(o => o.Id == BuiltInThemes.HighContrast).Label);
        }
        finally
        {
            L.SetLanguage(language);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}

/// <summary>The Microsoft sign-in panel opens only https web links in the browser.</summary>
public sealed class AccountsLinkTests
{
    [Theory]
    [InlineData("https://www.microsoft.com/link", true)]
    [InlineData("http://www.microsoft.com/link", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    public void DeviceCodeAddressMustBeHttps(string address, bool opens) => UiTestThread.Run(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-accounts-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = dir, PlayerName = "", SavePathForTest = Path.Combine(dir, "settings.json") });
            var opened = new List<string>();
            var accounts = new AccountsViewModel(service) { OpenUrl = opened.Add, DeviceUri = address };
            accounts.OpenDeviceUriCommand.Execute(null);
            Assert.Equal(opens, opened.Count == 1);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });
}
