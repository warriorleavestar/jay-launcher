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
    public void TheJayLoaderBuildsOnWhatTheUniversalLoaderRuns()
    {
        Assert.Equal(Amber.Core.Install.LoaderKind.NeoForge, UniversalLoaderFit.BaseLoaderFor(Pinned, "1.21.1"));
        Assert.Equal(Amber.Core.Install.LoaderKind.Forge, UniversalLoaderFit.BaseLoaderFor(Pinned, "1.20.1"));
        Assert.Null(UniversalLoaderFit.BaseLoaderFor(Pinned, "1.20.4"));
        Assert.Null(UniversalLoaderFit.BaseLoaderFor(null, "1.21.1"));
        Assert.Null(UniversalLoaderFit.BaseLoaderFor(Pinned, null));
        // A platform that preparing does not know (a newer loader's own Forge 1.12.2 line) is not offered either.
        var legacy = new CrossLoader.Capabilities(new Dictionary<string, IReadOnlyList<string>> { ["forge-1.12.2"] = ["1.12.2"] }, []);
        Assert.Null(UniversalLoaderFit.BaseLoaderFor(legacy, "1.12.2"));
        Assert.NotNull(UniversalLoaderFit.Unsupported(legacy, "forge", "1.12.2"));
        // 1.12.2 starts only on Forge 14.23.5.x; other versions take any build.
        Assert.True(UniversalLoaderFit.AcceptsBase("1.12.2", Amber.Core.Install.LoaderKind.Forge, "14.23.5.2860"));
        Assert.False(UniversalLoaderFit.AcceptsBase("1.12.2", Amber.Core.Install.LoaderKind.Forge, "14.23.4.2759"));
        Assert.True(UniversalLoaderFit.AcceptsBase("1.21.1", Amber.Core.Install.LoaderKind.NeoForge, "21.1.77"));
        Assert.Equal("1.21.1-Jay", Amber.Core.GameLibrary.NewInstanceNames.WithSuffix("1.21.1", "Jay", []));
        Assert.Equal("1.21.1-Jay-2", Amber.Core.GameLibrary.NewInstanceNames.WithSuffix("1.21.1", "Jay", ["1.21.1-jay"]));
    }

    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.20.1")]
    [InlineData("1.20.4")]
    [InlineData("1.12.2")]
    public void WhatTheNewInstancePanelOffersIsWhatPreparingAccepts(string mc)
    {
        var platforms = new Dictionary<string, IReadOnlyList<string>>
        {
            [BakePlatform.Vanilla] = ["1.21.1"], [BakePlatform.NeoForge] = ["1.21.1"], [BakePlatform.Forge] = ["1.20.1"], ["forge-1.12.2"] = ["1.12.2"],
        };
        var caps = new CrossLoader.Capabilities(platforms, []);
        var loader = new BakeLoaderInfo("0.4.0", 1, new Version(1, 3), platforms);
        var offered = UniversalLoaderFit.BaseLoaderFor(caps, mc);
        if (offered is not { } kind)
        {
            Assert.NotNull(UniversalLoaderFit.Unsupported(caps, "forge", mc));
            Assert.NotNull(UniversalLoaderFit.Unsupported(caps, "neoforge", mc));
            return;
        }
        var name = kind == Amber.Core.Install.LoaderKind.NeoForge ? "neoforge" : "forge";
        Assert.Null(UniversalLoaderFit.Unsupported(caps, name, mc));
        var instance = new GameInstance("jay", mc, name, "1.0", "");
        Assert.True(BakeService.Supports(instance, loader, out var reason), reason);
    }

    [Fact]
    public void TheJayLoaderIsItsOwnChoiceInTheNewInstancePanel()
    {
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            var vm = new DownloadViewModel(service);
            var jay = vm.LoaderChoices.Single(c => c.IsJay);
            Assert.Equal(L.T("newinstance.loader.jay"), jay.Label);
            Assert.Same(jay, vm.LoaderChoices[^1]);
            var caps = service.UniversalLoaderCapabilities;
            RemoteVersionItem Item(string id) => new(new Amber.Core.Game.ManifestEntry { Id = id, Type = "release" }, false);

            vm.Target = Item("1.21.1");
            Assert.Equal(caps is null ? null : UniversalLoaderFit.BaseLoaderFor(caps, "1.21.1"), jay.Kind);
            Assert.Equal(jay.Kind is not null, jay.IsAvailable);
            if (jay.Kind is { } kind) Assert.Contains(kind.ToString(), jay.Description);
            // The real NeoForge choice is still found first by kind.
            Assert.False(vm.LoaderChoices.First(c => c.Kind == Amber.Core.Install.LoaderKind.NeoForge).IsJay);

            vm.Target = Item("1.20.4");
            Assert.Null(jay.Kind);
            Assert.False(jay.IsAvailable);
            Assert.Equal(Amber.Core.GameLibrary.LoaderBlock.NotRunByJay, jay.Block);
            Assert.Equal(caps is null ? L.T("core.bake.fit.unavailable") : L.F("newinstance.loader.block.jay", "1.20.4", UniversalLoaderFit.Supported(caps)), jay.BlockReason);
        });
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
    public void TheCardSaysPreparedOnceTheModsArePrepared()
    {
        Version("neo", """[{"name":"net.neoforged:neoforge:21.1.1"}]""", "1.21.1");
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            service.SetUniversalLoader("neo", true);
            var vm = new InstanceViewModel(service);
            vm.SelectedId = "neo";
            vm.TabIndex = (int)InstanceTab.Settings;
            Assert.False(vm.UniversalLoaderPrepared);

            var workDir = vm.Current!.WorkDir;
            var state = Path.Combine(workDir, BakeService.StateFolder, "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            File.WriteAllText(state, "{}");
            BakeRecord.Write(workDir, new BakeRecord("prepared", new BakedLaunch("test.Main", [], [], [], state, "key")));
            service.NoteUniversalLoaderPrepared("neo");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(vm.UniversalLoaderPrepared);
        });
    }

    [Fact]
    public void CancellingTheCardsDownloadStopsIt()
    {
        Version("neo", """[{"name":"net.neoforged:neoforge:21.1.1"}]""", "1.21.1");
        if (!BakeLoaderRelease.Pinned.IsPublished) return;
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            var vm = new InstanceViewModel(service);
            vm.SelectedId = "neo";
            var stopped = new TaskCompletionSource<bool>();
            vm.DownloadLoader = async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { stopped.TrySetResult(true); }
                return "cancelled";
            };
            var download = vm.DownloadUniversalLoaderCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            vm.CancelWorkCommand.Execute(null);
            while (!download.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(stopped.Task.IsCompleted);
            Assert.False(vm.IsBusy);
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

/// <summary>A mixed pack whose import finishes after the game directory was switched.</summary>
public sealed class MixedImportContextTests
{
    [Fact]
    public void AnImportFinishedAfterSwitchingGameDirectoriesChangesOnlyTheOneItWentInto()
    {
        var first = Path.Combine(Path.GetTempPath(), "amber-mix-a-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "amber-mix-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = first, PlayerName = "" });
                var vm = new ContentViewModel(service, _ => null!, () => false);
                var started = service.Context;
                service.Settings.GameDirectory = second;
                service.Reload();
                var lastBefore = service.Settings.LastVersion;

                vm.FinishImport(started, "pack", mixed: true);

                var there = InstanceSettings.Load(started.Directory, "pack");
                Assert.True(there.UniversalLoader == true);
                Assert.True(there.Isolate == true);
                var here = InstanceSettings.Load(service.Directory, "pack");
                Assert.NotEqual(true, here.UniversalLoader);
                Assert.NotEqual(true, here.Isolate);
                Assert.Equal(lastBefore, service.Settings.LastVersion);

                vm.FinishImport(service.Context, "pack", mixed: true);
                Assert.True(InstanceSettings.Load(service.Directory, "pack").UniversalLoader == true);
                Assert.Equal("pack", service.Settings.LastVersion);
            });
        }
        finally
        {
            try { Directory.Delete(first, true); } catch (IOException) { }
            try { Directory.Delete(second, true); } catch (IOException) { }
        }
    }
}
