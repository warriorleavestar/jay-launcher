using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>A settings change rebuilds the launcher services only when it changes what they are built from.</summary>
public sealed class SettingsPersistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-settings-persist-" + Guid.NewGuid().ToString("N"));

    public SettingsPersistTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void OnlyServiceInputsRebuildTheServices() => UiTestThread.Run(() =>
    {
        var file = Path.Combine(_root, "settings.json");
        using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "", SavePathForTest = file });
        var settings = new SettingsViewModel(service);
        var refreshed = 0;
        service.InstalledVersionsChanged += () => refreshed++;

        var context = service.Context;
        settings.NoTrace = !settings.NoTrace;
        Assert.Same(context, service.Context);
        Assert.Equal(0, refreshed);
        Assert.True(File.Exists(file));

        // A default the instance pages show: they refresh, the services stay.
        settings.IsolateVersions = !settings.IsolateVersions;
        Assert.Same(context, service.Context);
        Assert.Equal(1, refreshed);

        // The download source is built into the downloader: rebuilt.
        settings.SourceIndex = settings.SourceIndex == 1 ? 2 : 1;
        Assert.NotSame(context, service.Context);
    });

    [Fact]
    public void MemorySliderWritesOnceItPauses() => UiTestThread.Run(() =>
    {
        var file = Path.Combine(_root, "settings.json");
        using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "", SavePathForTest = file });
        var settings = new SettingsViewModel(service) { IsCurrent = true };
        settings.AutoMemory = false;
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written - TimeSpan.FromMinutes(1));
        written = File.GetLastWriteTimeUtc(file);

        foreach (var mb in new[] { 2560, 3072, 3584, 4096 }) settings.MaxMemoryMb = mb;
        // Counted at once (a save made elsewhere writes it), but no write per step.
        Assert.Equal(4096, service.Settings.MaxMemoryMb);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        // Leaving the page writes what is still waiting.
        settings.IsCurrent = false;
        Assert.Contains("\"MaxMemoryMb\": 4096", File.ReadAllText(file), StringComparison.Ordinal);
    });
}
