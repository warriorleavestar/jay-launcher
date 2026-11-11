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
            Check("点【开启烘焙】：设置里实验性烘焙打开", Press(page.TurnOnBakeButton) && await WaitAsync(() => settings.ExperimentalBake, 10));

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
