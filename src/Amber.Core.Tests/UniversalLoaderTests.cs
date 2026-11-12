using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Bake;
using Amber.Core.Content;
using Amber.Core.Game;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>The universal loader per instance (redesign batch 10): the switch, its default, the card's fit and counts.</summary>
public sealed class UniversalLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-ul-" + Guid.NewGuid().ToString("N"));

    public UniversalLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly CrossLoader.Capabilities Pinned = CrossLoader.Capabilities.PinnedRelease;

    [Fact]
    public void FitSaysWhyAnInstanceIsOutOfRange()
    {
        Assert.Null(UniversalLoaderFit.Unsupported(Pinned, "neoforge", "1.21.1"));
        Assert.Null(UniversalLoaderFit.Unsupported(Pinned, "fabric", "1.21.1"));
        Assert.Null(UniversalLoaderFit.Unsupported(Pinned, "forge", "1.20.1"));
        Assert.Equal(L.T("core.bake.fit.no_loader"), UniversalLoaderFit.Unsupported(Pinned, null, "1.21.1"));
        Assert.Equal(L.F("core.bake.unsupported_loader", "optifine"), UniversalLoaderFit.Unsupported(Pinned, "optifine", "1.21.1"));
        Assert.Equal(L.T("core.bake.fit.unavailable"), UniversalLoaderFit.Unsupported(null, "neoforge", "1.21.1"));
        var wrongVersion = UniversalLoaderFit.Unsupported(Pinned, "neoforge", "1.20.4");
        Assert.NotNull(wrongVersion);
        Assert.Contains("1.20.4", wrongVersion);
        Assert.Contains("1.21.1", wrongVersion);
    }

    [Fact]
    public void OnlyEnabledModsFromOtherEcosystemsCount()
    {
        var extras = CrossLoader.Extras(Pinned, "neoforge", "1.21.1");
        LocalMod Mod(string name, bool enabled, params string[] loaders) =>
            new(Path.Combine(_root, name + ".jar"), name + ".jar", enabled, name, name, "1", null, null, null,
                new ModRequirements(new HashSet<string>(loaders), [name], new Dictionary<string, LoaderRequirement>()));
        var mods = new[]
        {
            Mod("sodium-fabric", true, "fabric"),
            Mod("create", true, "neoforge"),
            Mod("old-forge", true, "forge"),
            Mod("disabled-fabric", false, "fabric"),
            Mod("both", true, "fabric", "neoforge"),
            new LocalMod(Path.Combine(_root, "unknown.jar"), "unknown.jar", true, null, "unknown", null, null, null, null),
        };
        Assert.Equal(2, UniversalLoaderFit.CrossMods(mods, "neoforge", extras));
        Assert.Equal(0, UniversalLoaderFit.CrossMods(mods, "neoforge", []));
    }

    [Fact]
    public void SupportedListsEveryPlatformAndVersion()
    {
        var text = UniversalLoaderFit.Supported(Pinned);
        Assert.Contains("NeoForge 1.21.1", text);
        Assert.Contains("1.20.1", text);
        Assert.StartsWith(BakePlatform.DisplayName(BakePlatform.Vanilla), text);
    }

    [Fact]
    public void FolderSizeAddsUpNestedFilesAndIsZeroWhenMissing()
    {
        Assert.Equal(0, UniversalLoaderFit.FolderSize(Path.Combine(_root, "missing")));
        Directory.CreateDirectory(Path.Combine(_root, "cache", "a"));
        File.WriteAllBytes(Path.Combine(_root, "cache", "one"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "cache", "a", "two"), new byte[32]);
        Assert.Equal(42, UniversalLoaderFit.FolderSize(Path.Combine(_root, "cache")));
    }

    [Fact]
    public void AnInstancesOwnChoiceBeatsTheDefault()
    {
        Assert.True(new InstanceSettings().Over(new InstanceSettings { UniversalLoader = true }).UniversalLoader);
        Assert.False(new InstanceSettings { UniversalLoader = false }.Over(new InstanceSettings { UniversalLoader = true }).UniversalLoader);
        Assert.True(new InstanceSettings { UniversalLoader = true }.Over(new InstanceSettings { UniversalLoader = false }).UniversalLoader);
    }

    [Fact]
    public void TheSwitchIsPerInstanceAndTheOldGlobalSwitchIsTheDefault()
    {
        Version("neo");
        Version("other");
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            Assert.False(service.UsesUniversalLoader("neo"));
            var changed = new List<string>();
            service.UniversalLoaderChanged += changed.Add;
            service.SetUniversalLoader("neo", true);
            Assert.True(service.UsesUniversalLoader("neo"));
            Assert.False(service.UsesUniversalLoader("other"));
            Assert.Equal(["neo"], changed);
            service.SetUniversalLoader("neo", true);
            Assert.Single(changed);

            // The migrated global switch is the default for instances that have not chosen; an explicit "off" stays off.
            service.Settings.ExperimentalBake = true;
            Assert.True(service.UsesUniversalLoader("other"));
            service.SetUniversalLoader("neo", false);
            Assert.False(service.UsesUniversalLoader("neo"));
            Assert.False(InstanceSettings.Load(service.Directory, "neo").UniversalLoader);
        });
    }

    [Fact]
    public void SavingTheInstanceFormKeepsTheSwitch()
    {
        Version("neo");
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            service.SetUniversalLoader("neo", true);
            var vm = new InstanceViewModel(service);
            vm.SelectedId = "neo";
            vm.TabIndex = (int)InstanceTab.Settings;
            vm.JvmArgs = "-Dfoo=1";
            vm.SaveSettingsCommand.Execute(null);
            var saved = InstanceSettings.Load(service.Directory, "neo");
            Assert.Equal("-Dfoo=1", saved.JvmArgs);
            Assert.True(saved.UniversalLoader);
        });
    }

    [Fact]
    public void TheCardFollowsTheSwitchAndSaysWhyNot()
    {
        Version("neo", """[{"name":"net.neoforged:neoforge:21.1.1"}]""", "1.21.1");
        Version("plain");
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            var vm = new InstanceViewModel(service);
            vm.SelectedId = "plain";
            Assert.False(vm.UniversalLoaderSupported);
            Assert.False(vm.CanToggleUniversalLoader);
            Assert.False(vm.ShowUniversalLoaderChip);

            vm.SelectedId = "neo";
            Assert.Equal(("neoforge", "1.21.1"), (vm.Current!.Loader, vm.Current.McVersion));
            Assert.Equal(service.UniversalLoaderCapabilities is not null, vm.UniversalLoaderSupported);
            if (vm.UniversalLoaderSupported)
            {
                Assert.True(vm.CanToggleUniversalLoader);
                vm.UniversalLoaderOn = true;
                Assert.True(service.UsesUniversalLoader("neo"));
                Assert.True(vm.ShowUniversalLoaderChip);
                Assert.Contains(vm.UniversalLoaderState, vm.UniversalLoaderChip);
                // Turned on elsewhere (Discover) or off here: the card follows the saved switch.
                service.SetUniversalLoader("neo", false);
                vm.RefreshUniversalLoader();
                Assert.False(vm.UniversalLoaderOn);
                Assert.False(vm.ShowUniversalLoaderChip);
            }
            else
            {
                // Without an installed or published loader the card explains instead of offering the switch.
                Assert.Equal(L.T("core.bake.fit.unavailable"), vm.UniversalLoaderUnsupported);
            }
        });
    }

    private void Version(string id, string libraries = "[]", string? mc = null)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        var inherits = mc is null ? "" : $$""","inheritsFrom":"{{mc}}" """;
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$"""{"id":"{{id}}","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":{{libraries}}{{inherits}}}""");
        if (mc is not null)
        {
            var parent = Path.Combine(_root, "versions", mc);
            Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent, mc + ".json"), $$"""{"id":"{{mc}}","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        }
    }
}
