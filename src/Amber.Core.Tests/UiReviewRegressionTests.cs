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
}
