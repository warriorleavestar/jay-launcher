using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>The instance page's settings form: what it writes back while the Java scan is still running.</summary>
public sealed class InstanceSettingsFormTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-instance-form-" + Guid.NewGuid().ToString("N"));

    public InstanceSettingsFormTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void EditDuringJavaScanKeepsTheStoredJava() => UiTestThread.Run(() =>
    {
        foreach (var id in new[] { "a", "b" })
        {
            var dir = Path.Combine(_root, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), $$"""{"id":"{{id}}","mainClass":"Example","libraries":[]}""");
            File.WriteAllBytes(Path.Combine(dir, id + ".jar"), []);
        }
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var javaA = Path.Combine(_root, "java-a", "bin", "java");
        var javaB = Path.Combine(_root, "java-b", "bin", "java");
        new InstanceSettings { JavaPath = javaA }.Save(service.Directory, "a");
        new InstanceSettings { JavaPath = javaB }.Save(service.Directory, "b");
        var vm = new InstanceViewModel(service);

        // First load: the scan has not come back yet (its continuation waits for the UI thread).
        vm.SelectedId = "a";
        vm.TabIndex = (int)InstanceTab.Settings;
        Assert.Equal(javaA, vm.SelectedJava?.Path);
        vm.MemoryText = "4096";
        Assert.Equal(javaA, InstanceSettings.Load(service.Directory, "a").JavaPath);
        Assert.Equal(4096, InstanceSettings.Load(service.Directory, "a").MaxMemoryMb);

        // A later load starts from the previous instance's list; its choice must not leak into this instance.
        vm.SelectedId = "b";
        vm.TabIndex = (int)InstanceTab.Settings;
        Assert.Equal(javaB, vm.SelectedJava?.Path);
        vm.MemoryText = "2048";
        Assert.Equal(javaB, InstanceSettings.Load(service.Directory, "b").JavaPath);

        // Once the scan returns (the list then holds every Java found, plus the automatic entry and the stored path),
        // the stored path is still the selection.
        var expected = Amber.Core.Java.JavaFinder.FindAll(service.Directory.Root).Count + 2;
        var until = DateTime.UtcNow.AddSeconds(20);
        while (vm.JavaChoices.Count < expected)
        {
            Assert.True(DateTime.UtcNow < until, "the Java scan did not finish");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(javaB, vm.SelectedJava?.Path);
        Assert.Equal(javaB, InstanceSettings.Load(service.Directory, "b").JavaPath);
    });

    [Fact]
    public void DeniedInstanceSettingsWriteShowsAnError() => UiTestThread.Run(() =>
    {
        var dir = Path.Combine(_root, "versions", "a");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"), """{"id":"a","mainClass":"Example","libraries":[]}""");
        File.WriteAllBytes(Path.Combine(dir, "a.jar"), []);
        // A folder where the settings file belongs: writing it fails with access denied.
        Directory.CreateDirectory(Path.Combine(dir, InstanceSettings.FileName));
        var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var vm = new InstanceViewModel(service);
        vm.SelectedId = "a";
        vm.TabIndex = (int)InstanceTab.Settings;

        vm.MemoryText = "4096";

        Assert.Equal("settings.error.save_failed", vm.StatusInfo?.Key);
    });
}
