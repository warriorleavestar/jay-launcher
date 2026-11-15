using Amber.App.Services;

namespace Amber.Core.Tests;

/// <summary>The installed-instances listing is reused briefly but never shows a stale list after a change.</summary>
public sealed class InstalledVersionsCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-installed-cache-" + Guid.NewGuid().ToString("N"));

    public InstalledVersionsCacheTests() => Directory.CreateDirectory(Path.Combine(_root, "versions"));

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

    private void Write(string id)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), "{\"id\":\"" + id + "\"}");
    }

    [Fact]
    public void JaysOwnChangesShowAtOnce()
    {
        using var service = NewService();
        Assert.Empty(service.InstalledVersions());
        // The folder may be created within the same timestamp tick, so rely on the notification as Jay's installers do.
        Write("a");
        service.NotifyInstalledChanged();
        Assert.Equal(["a"], service.InstalledVersions());
    }

    [Fact]
    public void AChangedVersionsFolderIsReadAgain()
    {
        using var service = NewService();
        Write("a");
        service.NotifyInstalledChanged();
        Assert.Equal(["a"], service.InstalledVersions());
        Write("b");
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "versions"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(["b", "a"], service.InstalledVersions());
    }

    [Fact]
    public void AListingIsNotKeptLongerThanItsLife()
    {
        using var service = NewService();
        Write("a");
        service.NotifyInstalledChanged();
        Assert.Equal(["a"], service.InstalledVersions());
        // A version file written into an existing folder does not change the versions folder's time.
        var dir = Path.Combine(_root, "versions", "b");
        Directory.CreateDirectory(dir);
        service.NotifyInstalledChanged();
        Assert.Equal(["a"], service.InstalledVersions());
        File.WriteAllText(Path.Combine(dir, "b.json"), "{\"id\":\"b\"}");
        Thread.Sleep(LauncherService.InstalledCacheLife + TimeSpan.FromMilliseconds(200));
        Assert.Equal(["b", "a"], service.InstalledVersions());
    }

    [Fact]
    public void CallersGetTheirOwnCopy()
    {
        using var service = NewService();
        Write("a");
        service.NotifyInstalledChanged();
        service.InstalledVersions().Clear();
        Assert.Equal(["a"], service.InstalledVersions());
    }
}
