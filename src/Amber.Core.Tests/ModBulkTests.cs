using Amber.App.Services;
using Amber.App.ViewModels;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Loading and bulk-changing the Mods tab works out the counts once, not once per mod.</summary>
public sealed class ModBulkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-mod-bulk-" + Guid.NewGuid().ToString("N"));

    public ModBulkTests()
    {
        var dir = Path.Combine(_root, "versions", "f");
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "f.json"),
            """{"id":"f","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
        for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(dir, "mods", $"mod{i:D2}.jar"), []);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void LoadingAndBulkChangesCountOnce() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings
        {
            GameDirectory = _root,
            SavePathForTest = Path.Combine(_root, "settings.json"),
            PlayerName = "",
            AutoJavaDownload = false,
        });
        var vm = new InstanceViewModel(service);
        vm.SelectedId = "f";
        vm.TabIndex = (int)InstanceTab.Mods;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.Mods.Count < 40 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(40, vm.Mods.Count);
        Assert.InRange(vm.ModCountUpdatesForTest, 1, 6);

        foreach (var m in vm.Mods) m.Selected = true;
        var before = vm.ModCountUpdatesForTest;
        vm.DisableSelectedModsCommand.Execute(null);
        Assert.All(vm.Mods, m => Assert.False(m.Enabled));
        Assert.InRange(vm.ModCountUpdatesForTest - before, 1, 4);
        Assert.Equal(40, Directory.GetFiles(Path.Combine(_root, "versions", "f", "mods"), "*.disabled").Length);

        before = vm.ModCountUpdatesForTest;
        vm.EnableSelectedModsCommand.Execute(null);
        Assert.All(vm.Mods, m => Assert.True(m.Enabled));
        Assert.InRange(vm.ModCountUpdatesForTest - before, 1, 4);
        Assert.Contains("40", vm.ModFooter);

        // Enabling or disabling rows under a search keeps the filtered view as it is (rows do not jump away).
        vm.ModSearch = "mod0";
        var view = vm.ModsView;
        Assert.NotSame(vm.Mods, view);
        view.First().Enabled = false;
        Assert.Same(view, vm.ModsView);
        vm.DisableSelectedModsCommand.Execute(null);
        Assert.Same(view, vm.ModsView);
    });
}
