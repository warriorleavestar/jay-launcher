using Amber.App.Services;

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

}
