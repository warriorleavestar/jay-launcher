using System.Text.Json.Nodes;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Bake;
using Amber.Core.Content;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=mixed-content: mods from other ecosystems through the universal loader on Discover.
/// A throwaway game directory in the report folder holds a hand-written NeoForge 1.21.1 instance; a stand-in loader
/// package (manifest + bridge names, nothing runnable) says what the loader runs. Checks: with baking off, adding a
/// Forge-only mod shows the "download loader" and then the "turn on baking" prompt; [Turn on baking] turns it on; with
/// baking ready, search finds the Forge-only mod with the "universal loader" badge and its versions are listed with a
/// note and no prompt; a mixed .mrpack preview says which mods run through the loader. Searches Modrinth and
/// downloads one small mod into the test directory.
/// </summary>
internal sealed partial class SelfTest
{
    private const string MixedForgeOnlyMod = "dont cut tripwire";

    private async Task MixedContentAsync()
    {
        var service = Page<HomeViewModel>().Service;
        var content = main.Pages.OfType<ContentViewModel>().First();
        var settings = service.Settings;
        var (originalDir, originalBake, originalLoader, originalMeta) = (settings.GameDirectory, settings.ExperimentalBake, settings.BakeLoaderPath, settings.Instances);
        var game = Path.Combine(reportDir, "game");
        const string id = "mix-neoforge";
        try
        {
            WriteMixedVersions(game, id);
            settings.Instances = [];
            settings.GameDirectory = game;
            settings.ExperimentalBake = false;
            settings.BakeLoaderPath = "";
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            // Let the posted instance-list refreshes run first: they put the current instance back otherwise.
            await Task.Delay(1500);
            service.SetCurrentInstance(id);
            var instance = service.GetInstance(id);
            Check("测试实例认成 NeoForge 1.21.1", instance is { Loader: "neoforge", McVersion: "1.21.1" }, $"{instance.Loader} {instance.McVersion}");
            Check("运行时目录在数据目录旁边，不在里面",
                !LauncherService.BakeRuntime.Root.StartsWith(Path.TrimEndingDirectorySeparator(AppSettings.DataDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                LauncherService.BakeRuntime.Root);

            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("discover");
            await WaitAsync(() => main.CurrentPage is ContentViewModel, 5);
            content.TypeIndex = (int)ContentType.Mod;
            content.SourceIndex = 0;
            await WaitAsync(() => content.TargetInstance == id, 5);
            var page = window.PageHost.GetVisualDescendants().OfType<ContentPage>().First();

            // 1. Baking off, no loader: the Forge-only mod is not in the instance's search; unfiltered, adding it asks for the loader.
            content.FilterByInstance = true;
            await SearchMixedAsync(content, MixedForgeOnlyMod, needResults: false);
            Check("烘焙关：按实例搜不到只有 Forge 版的模组（搜索不变）",
                !content.Results.Any(r => r.Title.Contains("Tripwire", StringComparison.OrdinalIgnoreCase)) && content.Results.All(r => !r.ViaUniversalLoader),
                string.Join(", ", content.Results.Select(r => r.Title).Take(5)));
            content.FilterByInstance = false;
            var item = await PickMixedAsync(content, "Tripwire");
            Check("不按实例过滤时能找到它", item is not null, string.Join(", ", content.Results.Select(r => r.Title).Take(5)));
            if (item is null) return;
            content.InstallCommand.Execute(null);
            await WaitAsync(() => !content.IsBusy && content.CrossPrompt != CrossLoaderPrompt.None, 60);
            var hasJar = Directory.Exists(instance.ModsDir) && Directory.EnumerateFiles(instance.ModsDir, "*.jar").Any();
            Check("烘焙关、没装加载器：装上后提示要下载通用加载器，并有【下载加载器】",
                content.CrossPrompt == CrossLoaderPrompt.Download && hasJar
                && Dispatcher.UIThread.Invoke(() => page.DownloadLoaderButton.IsEffectivelyVisible && !page.TurnOnBakeButton.IsVisible && page.LoaderNoteBox.IsEffectivelyVisible),
                $"{content.CrossPrompt} {content.LoaderNote} | {content.Status}");
            Check("状态写明只能经通用加载器运行", content.Status.Contains(Amber.Core.Localization.L.T("content.install.ul_not_ready").Trim()), content.Status);
            await ShotAsync("mixed-needs-loader");

            // 2. A loader is installed but baking is off: the prompt turns into [Turn on baking].
            settings.BakeLoaderPath = WriteStandInLoader(Path.Combine(reportDir, "stand-in-loader"));
            settings.Save();
            content.InstallCommand.Execute(null);
            await WaitAsync(() => !content.IsBusy && content.CrossPrompt == CrossLoaderPrompt.TurnOn, 60);
            Check("装了加载器、烘焙关：提示开启烘焙，并有【开启烘焙】",
                content.CrossPrompt == CrossLoaderPrompt.TurnOn && Dispatcher.UIThread.Invoke(() => page.TurnOnBakeButton.IsEffectivelyVisible && !page.DownloadLoaderButton.IsVisible),
                $"{content.CrossPrompt} {content.LoaderNote}");
            await ShotAsync("mixed-turn-on");
            Check("Turn on for this instance: only this instance uses the universal loader, the default in Settings stays off",
                Press(page.TurnOnBakeButton) && await WaitAsync(() => service.UsesUniversalLoader(id), 10) && !settings.ExperimentalBake);

            // 3. Baking ready: the instance's own search finds it, with the badge; its versions carry a note and no prompt.
            content.FilterByInstance = true;
            item = await PickMixedAsync(content, "Tripwire");
            Check("烘焙就绪：按实例搜索也能搜到只有 Forge 版的模组，带“经通用加载器”标记",
                item is { ViaUniversalLoader: true } && Dispatcher.UIThread.Invoke(() =>
                    page.ResultsList.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "UniversalLoaderBadge" && b.IsEffectivelyVisible)),
                string.Join(", ", content.Results.Select(r => r.Title + (r.ViaUniversalLoader ? "*" : "")).Take(6)));
            await WaitAsync(() => content.Files.Count > 0, 30);
            Check("版本列表标着经通用加载器，说明在安装按钮上方，没有开启/下载按钮",
                content.Files.Count > 0 && content.Files.All(f => f.ViaUniversalLoader) && content.CrossPrompt == CrossLoaderPrompt.None && content.HasLoaderNote
                && Dispatcher.UIThread.Invoke(() => !page.TurnOnBakeButton.IsVisible && !page.DownloadLoaderButton.IsVisible),
                $"{content.Files.Count} {content.LoaderNote}");
            await ShotAsync("mixed-ready");
            await SearchMixedAsync(content, "sodium");
            var sodium = content.Results.FirstOrDefault(r => r.Title.Equals("Sodium", StringComparison.OrdinalIgnoreCase));
            Check("有 NeoForge 版的模组不带标记", sodium is { ViaUniversalLoader: false }, string.Join(", ", content.Results.Select(r => r.Title + (r.ViaUniversalLoader ? "*" : "")).Take(6)));

            // 3b. The instance's own card and the chip next to the start button (redesign batch 10).
            await UniversalLoaderCardAsync(service, id);

            // 4. A mixed pack: the preview names the loader that runs the extra mods.
            var pack = Path.Combine(reportDir, "mixed.mrpack");
            using (var zip = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                writer.Write("""{"formatVersion":1,"game":"minecraft","name":"Mixed","versionId":"1","files":[],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.5","neoforge":"21.1.1"}}""");
            var download = main.Pages.OfType<DownloadViewModel>().First();
            await download.PreviewPackAsync(pack);
            Check("混合整合包预览：底座是 NeoForge，写明 Fabric 模组经通用加载器",
                download.PackInfo is { Loader: Amber.Core.Install.LoaderKind.NeoForge, IsMixed: true } && download.PackFacts.Contains("Fabric"), download.PackFacts);
            download.ClearPackCommand.Execute(null);
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            content.FilterByInstance = true;
            settings.ExperimentalBake = originalBake;
            settings.BakeLoaderPath = originalLoader;
            settings.Instances = originalMeta;
            settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }
    }

    /// <summary>
    /// AMBER_SELFTEST_SCOPE=mixed-e2e: the browser half of the mixed-pack end-to-end test. In the game directory
    /// AMBER_SELFTEST_MIXED_GAME, for the existing NeoForge 1.21.1 instance AMBER_SELFTEST_MIXED_INSTANCE (a test
    /// instance), with baking on and a real loader package selected in the settings: installs a Fabric-only mod
    /// (with its Fabric dependencies) and a NeoForge mod from Modrinth through Discover. The launch with baking is
    /// then run from the CLI (`launch … --bake … --smoke`).
    /// </summary>
    private async Task MixedEndToEndAsync()
    {
        var service = Page<HomeViewModel>().Service;
        var content = main.Pages.OfType<ContentViewModel>().First();
        var game = Environment.GetEnvironmentVariable("AMBER_SELFTEST_MIXED_GAME");
        var id = Environment.GetEnvironmentVariable("AMBER_SELFTEST_MIXED_INSTANCE");
        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(id)) { Check("AMBER_SELFTEST_MIXED_GAME / _INSTANCE 已设置", false); return; }
        try
        {
            Check("烘焙已开、加载器已装（真实加载器包）", service.CrossLoaderFor(service.GetInstance(id)) is { Ready: true, Extras.Count: > 0 },
                service.Settings.BakeLoaderPath);
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            service.SetCurrentInstance(id);
            var instance = service.GetInstance(id);
            main.Shell.Navigate("discover");
            await WaitAsync(() => main.CurrentPage is ContentViewModel, 5);
            content.TypeIndex = (int)ContentType.Mod;
            content.SourceIndex = 0;
            content.FilterByInstance = true;
            content.TargetInstance = id;
            Check("装到的是测试实例", await WaitAsync(() => content.TargetInstance == id, 5), content.TargetInstance ?? "");

            // AMBER_SELFTEST_MIXED_FABRIC: the Fabric-only mod's Modrinth slug (default visuality, which needs Fabric API and Cloth Config).
            var fabricSlug = Environment.GetEnvironmentVariable("AMBER_SELFTEST_MIXED_FABRIC") is { Length: > 0 } f ? f : "visuality";
            foreach (var (query, title, cross) in new[] { (fabricSlug, fabricSlug, true), ("appleskin", "appleskin", false) })
            {
                await SearchMixedAsync(content, query);
                var item = content.Results.FirstOrDefault(r => r.Project.PageUrl?.EndsWith("/" + title, StringComparison.OrdinalIgnoreCase) == true);
                Check($"搜到 {title}，标记{(cross ? "经通用加载器" : "原生")}", item is not null && item.ViaUniversalLoader == cross,
                    string.Join(", ", content.Results.Select(r => r.Title + (r.ViaUniversalLoader ? "*" : "")).Take(6)));
                if (item is null) continue;
                content.Selected = item;
                content.Detail = item;
                await WaitAsync(() => content.SelectedFile is not null, 30);
                if (content.TargetInstance != id) content.TargetInstance = id;
                Check($"装 {title} 前目标仍是测试实例", content.TargetInstance == id, content.TargetInstance ?? "");
                content.InstallCommand.Execute(null);
                await Task.Delay(300);
                await WaitAsync(() => !content.IsBusy, 180);
                Check($"装好 {title}", content.Status.Contains(item.Title, StringComparison.OrdinalIgnoreCase), content.Status);
            }
            var jars = Directory.Exists(instance.ModsDir) ? Directory.EnumerateFiles(instance.ModsDir, "*.jar").Select(Path.GetFileName).Order().ToList() : [];
            _report.Add("INFO mods: " + string.Join(" | ", jars));
            if (fabricSlug == "visuality")
                Check("Fabric 模组的依赖装的是 Fabric 版（Fabric API、Cloth Config Fabric）",
                jars.Any(j => j!.StartsWith("fabric-api", StringComparison.OrdinalIgnoreCase)) && jars.Any(j => j!.Contains("cloth-config", StringComparison.OrdinalIgnoreCase) && j.Contains("fabric", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", jars));
            Check("NeoForge 模组装的是 NeoForge 版", jars.Any(j => j!.Contains("appleskin", StringComparison.OrdinalIgnoreCase) && j.Contains("neoforge", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", jars));
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
    }

    private async Task SearchMixedAsync(ContentViewModel content, string query, bool needResults = true)
    {
        content.Query = query;
        content.SearchCommand.Execute(null);
        await Task.Delay(300);
        await WaitAsync(() => !content.IsSearching && (!needResults || content.Results.Count > 0), 30);
    }

    private async Task<ProjectItem?> PickMixedAsync(ContentViewModel content, string title)
    {
        await SearchMixedAsync(content, MixedForgeOnlyMod);
        var item = content.Results.FirstOrDefault(r => r.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (item is null) return null;
        content.Selected = item;
        content.Detail = item;
        await WaitAsync(() => content.SelectedFile is not null, 30);
        return item;
    }

    /// <summary>
    /// The instance's "Universal loader" card and chip: on for this instance, the chip opens the card, one mod runs through
    /// it, "See what it changed" before any game, off hides the chip and is written to the instance only.
    /// </summary>
    private async Task UniversalLoaderCardAsync(LauncherService service, string id)
    {
        var instancePage = Page<InstanceViewModel>();
        main.Shell.Navigate("library/" + id);
        await WaitAsync(() => main.CurrentPage is InstanceViewModel && instancePage.Current?.Id == id, 10);
        var view = await Dispatcher.UIThread.InvokeAsync(() => window.PageHost.GetVisualDescendants().OfType<InstancePage>().First());
        await WaitAsync(() => instancePage.UniversalLoaderMods.Length > 0, 10);
        Check("Instance header: the chip says the universal loader is on and not prepared yet",
            instancePage.ShowUniversalLoaderChip && Dispatcher.UIThread.Invoke(() => view.UniversalLoaderChip.IsEffectivelyVisible)
            && instancePage.UniversalLoaderChip.Contains(Amber.Core.Localization.L.T("instance.ul.state.pending")), instancePage.UniversalLoaderChip);
        Check("The chip opens the card in the Settings tab", Press(view.UniversalLoaderChip)
            && await WaitAsync(() => instancePage.TabIndex == (int)InstanceTab.Settings && Dispatcher.UIThread.Invoke(() => view.UniversalLoaderCard.IsEffectivelyVisible), 5));
        await Task.Delay(300);
        Check("Card: switch on, runs this instance, one mod runs through it, [Prepare again] and [Start without it this time]",
            Dispatcher.UIThread.Invoke(() => view.UniversalLoaderSwitch.IsChecked == true && view.UniversalLoaderSwitch.IsEffectivelyEnabled
                && !view.UniversalLoaderUnsupportedText.IsVisible && view.UniversalLoaderPrepareButton.IsEffectivelyVisible && view.UniversalLoaderSkipButton.IsEffectivelyVisible)
            && instancePage.UniversalLoaderMods == Amber.Core.Localization.L.F("instance.ul.mods", 1), instancePage.UniversalLoaderMods);
        await ShotAsync("mixed-instance-card");
        Check("[See what it changed] before any game says it has not run here yet",
            Press(view.UniversalLoaderChangesButton) && await WaitAsync(() => instancePage.ShowUniversalLoaderChanges, 3)
            && instancePage.UniversalLoaderChangesNote == Amber.Core.Localization.L.T("instance.ul.changes_never"), instancePage.UniversalLoaderChangesNote);
        Dispatcher.UIThread.Invoke(() => view.UniversalLoaderSwitch.IsChecked = false);
        Check("Turning it off hides the chip and is written to this instance only",
            await WaitAsync(() => !instancePage.ShowUniversalLoaderChip, 3) && !service.UsesUniversalLoader(id)
            && Amber.Core.Launch.InstanceSettings.Load(service.Directory, id).UniversalLoader == false && !service.Settings.ExperimentalBake);
        Dispatcher.UIThread.Invoke(() => view.UniversalLoaderSwitch.IsChecked = true);
        Check("Turning it back on shows the chip again", await WaitAsync(() => instancePage.ShowUniversalLoaderChip && service.UsesUniversalLoader(id), 3));
        main.Shell.Navigate("discover");
        await WaitAsync(() => main.CurrentPage is ContentViewModel, 5);
    }

    private static void WriteMixedVersions(string game, string id)
    {
        void Write(string name, string json)
        {
            var dir = Path.Combine(game, "versions", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".json"), json);
        }
        Write("1.21.1", """{"id":"1.21.1","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
        Write(id, $$$"""{"id":"{{{id}}}","inheritsFrom":"1.21.1","type":"release","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher","libraries":[{"name":"net.neoforged:neoforge:21.1.1:universal"}],"arguments":{"game":["--fml.neoForgeVersion","21.1.1"]}}""");
        new Amber.Core.Launch.InstanceSettings { Isolate = true }.Save(new Amber.Core.Game.GameDirectory(game), id);
    }

    /// <summary>A loader package that only describes itself (what <see cref="CrossLoader"/> reads); it cannot bake.</summary>
    private static string WriteStandInLoader(string root)
    {
        var contents = new Dictionary<string, string>
        {
            ["loader.jar"] = "stand-in",
            ["bridges/fabricbridge.jar"] = "stand-in",
            ["bridges/forgecompat.jar"] = "stand-in",
        };
        var files = new JsonObject();
        foreach (var (name, text) in contents)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            files[name] = new JsonObject { ["sha256"] = BakePackage.Sha256(path), ["size"] = new FileInfo(path).Length };
        }
        var manifest = new JsonObject
        {
            ["schema"] = BakePackage.Schema,
            ["loaderVersion"] = "0.0.0-selftest",
            ["protocol"] = 1,
            ["interface"] = "1.3",
            ["platforms"] = JsonNode.Parse("""{"vanilla":["1.21.1"],"neoforge":["1.21.1"],"forge":["1.20.1"]}"""),
            ["classpath"] = new JsonArray("loader.jar"),
            ["bridges"] = JsonNode.Parse("""{"fabricbridge.jar":"bridges/fabricbridge.jar","forgecompat.jar":"bridges/forgecompat.jar"}"""),
            ["files"] = files,
        };
        File.WriteAllText(Path.Combine(root, BakePackage.ManifestName), manifest.ToJsonString());
        return root;
    }
}
