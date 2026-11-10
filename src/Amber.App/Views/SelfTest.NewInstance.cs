using System.IO.Compression;
using Amber.App.ViewModels;
using Avalonia;
using Amber.Core.Content;
using Amber.Core.Install;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=new-instance (UI batch 6a): the new-instance panel in a throwaway game directory inside the
/// report folder. Sources rail / segment by width, type chips, loader options greyed out with a reason, the inline name
/// check gating [Install], a modpack file preview, the Discover entry, copying an instance (with and without worlds),
/// another launcher's folder, Esc, and screenshots at three widths in two languages. Only the version list and the
/// Fabric loader list go online; nothing is installed here (the full self-check installs).
/// </summary>
internal sealed partial class SelfTest
{
    private async Task NewInstancePanelAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "ni-game");
        var other = Path.Combine(reportDir, "ni-other", ".minecraft");
        var download = Page<DownloadViewModel>();
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            WriteLibraryVersion(game, "fabric-iso", """[{"name":"net.fabricmc:fabric-loader:0.16.9"}]""");
            var iso = Path.Combine(game, "versions", "fabric-iso");
            new Amber.Core.Launch.InstanceSettings { Isolate = true }.Save(new Amber.Core.Game.GameDirectory(game), "fabric-iso");
            Directory.CreateDirectory(Path.Combine(iso, "mods"));
            File.WriteAllText(Path.Combine(iso, "mods", "a.jar"), "x");
            Directory.CreateDirectory(Path.Combine(iso, "saves", "World"));
            File.WriteAllText(Path.Combine(iso, "saves", "World", "level.dat"), "x");
            new Amber.Core.Launch.InstanceSettings { Isolate = false }.Save(new Amber.Core.Game.GameDirectory(game), "1.20.1");
            WriteLibraryVersion(other, "1.12.2", "[]");
            service.Settings.Instances = [];
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            download.SetOtherCandidatesForTest(other);

            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("library/new");
            Check("Ctrl+N 的去处 library/new 打开新建实例", await WaitAsync(() => main.CurrentPage is DownloadViewModel, 5), main.CurrentPage.Title);
            download.SourceIndex = 0;
            await WaitAsync(() => download.Items.Count > 0, 60);
            var page = window.PageHost.GetVisualDescendants().OfType<DownloadPage>().First();
            await Task.Delay(400);
            Check("宽窗：来源是左侧一栏", Dispatcher.UIThread.Invoke(() => Grid.GetColumn(page.SourceList) == 0 && page.SourceList.Bounds.Width > 100));
            Check("装到：写出游戏库位置", download.InstallToText.Contains(game), download.InstallToText);
            Check("Opens with the latest release picked and [Install] ready (§6.11)", download.Target is not null && download.Target.Id == download.LatestRelease && download.InstallCommand.CanExecute(null), download.Target?.Id);
            Check("琥珀色：新建实例里没有琥珀按钮（安装是蓝色）", AmberButtons() == 0, $"{AmberButtons()}");
            await ShotAsync("new-1440");

            // Type chips.
            Dispatcher.UIThread.Invoke(() => page.TypeChips.SelectedIndex = 1);
            await Task.Delay(300);
            Check("类型芯片：快照", download.Items.Count > 0 && download.Items.All(i => i.Entry.Type == "snapshot"), $"{download.Items.Count}");
            var snapshot = download.Items.First();
            Dispatcher.UIThread.Invoke(() => page.TypeChips.SelectedIndex = 0);
            await Task.Delay(300);
            Check("类型芯片：回到正式版", download.Items.All(i => i.Entry.Type == "release"));

            // Loader options with reasons.
            download.Search = "1.12.2";
            var old = await VersionRowAsync(download, "1.12.2");
            Dispatcher.UIThread.Invoke(() => { if (old is not null) old.IsSelected = true; });
            await WaitAsync(() => download.Target?.Id == "1.12.2", 5);
            var fabric = download.LoaderChoices.First(c => c.Kind == LoaderKind.Fabric);
            var neo = download.LoaderChoices.First(c => c.Kind == LoaderKind.NeoForge);
            var forge = download.LoaderChoices.First(c => c.Kind == LoaderKind.Forge);
            Check("加载器：1.12.2 的 Fabric、NeoForge 置灰并写原因，Forge 可选",
                !fabric.IsAvailable && fabric.BlockReason.Contains("1.14") && !neo.IsAvailable && forge.IsAvailable, $"{fabric.BlockReason} / {neo.BlockReason}");
            await Task.Delay(300);
            var fabricItem = Dispatcher.UIThread.Invoke(() => page.LoaderSegment.ContainerFromItem(fabric) as ListBoxItem);
            Check("加载器：置灰的项点不了", Dispatcher.UIThread.Invoke(() => fabricItem is { IsEffectivelyEnabled: false }),
                Dispatcher.UIThread.Invoke(() => $"{fabricItem is null} {fabricItem?.IsEnabled} {fabricItem?.DataContext} {page.LoaderSegment.IsEffectivelyVisible}"));
            download.SelectedLoader = fabric;
            Check("加载器：代码里选了不支持的也会退回原版", download.SelectedLoader.Kind is null);
            download.ConfigureCommand.Execute(snapshot);
            Check("加载器：快照的 Forge 置灰、Fabric 可选", !forge.IsAvailable && forge.BlockReason.Length > 0 && fabric.IsAvailable, forge.BlockReason);
            Check("原版：名字就是版本号，不能改；可以直接装", download.CustomName == snapshot.Id && !download.CanRename && download.InstallCommand.CanExecute(null));

            // Name check.
            download.Search = "1.21.1";
            var row = await VersionRowAsync(download, "1.21.1");
            Dispatcher.UIThread.Invoke(() => { if (row is not null) row.IsSelected = true; });
            await WaitAsync(() => download.Target?.Id == "1.21.1", 5);
            Dispatcher.UIThread.Invoke(() => page.LoaderSegment.SelectedItem = fabric);
            await WaitAsync(() => download.SelectedLoaderVersion is not null, 30);
            Check("选 Fabric：列出加载器版本，名字默认“1.21.1-Fabric”，说明写 Fabric 适合什么",
                download.SelectedLoaderVersion is not null && download.CustomName == "1.21.1-Fabric" && download.LoaderDescription.StartsWith("Fabric", StringComparison.Ordinal),
                $"{download.CustomName} {download.LoaderHint}");
            Check("名字合格时【安装】可用", download.InstallCommand.CanExecute(null));
            foreach (var (name, expect) in new[] { ("CON", "保留"), ("a/b", "不能有"), ("fabric-iso", "同名"), ("", "起个名字") })
            {
                Dispatcher.UIThread.Invoke(() => page.NameBox.Text = name);
                await Task.Delay(100);
                var shown = Dispatcher.UIThread.Invoke(() => page.NameProblemText.IsEffectivelyVisible ? page.NameProblemText.Text : null);
                Check($"名字“{name}”：行内说明，【安装】不可用", shown?.Contains(expect) == true && !download.InstallCommand.CanExecute(null), shown ?? "没显示");
            }
            Dispatcher.UIThread.Invoke(() => page.NameBox.Text = "我的整合包");
            await Task.Delay(100);
            Check("改回合格的名字后【安装】可用", download.InstallCommand.CanExecute(null) && !download.HasNameProblem);

            // "Add mods while you're at it" (§6.11): offline here, results are filled in by hand.
            var wasOpen = download.ModsOpen;
            download.SearchModsOffline = true;
            Dispatcher.UIThread.Invoke(() => download.ModsOpen = true);
            ContentProject Fake(string id) => new(ContentSource.Modrinth, id, id, "A test mod", null, null, 0, null);
            Dispatcher.UIThread.Invoke(() => download.ShowModResults([Fake("Sodium"), Fake("Iris"), Fake("JourneyMap")]));
            await Task.Delay(300);
            var ticks = Dispatcher.UIThread.Invoke(() => page.NewInstanceModsList.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).ToList());
            Check("Add mods: the panel has a mod list once Fabric is picked", ticks.Count == 3 && download.CanAddMods, $"{ticks.Count}");
            foreach (var tick in ticks.Take(2)) Dispatcher.UIThread.Invoke(() => tick.Command?.Execute(tick.CommandParameter));
            await Task.Delay(200);
            Check("Add mods: ticking shows the picks in the header", download.PickedMods.Count == 2
                && download.ModsHeader == L.P("newinstance.mods.header_picked", 2, 2, "Sodium" + L.T("common.list_separator") + "Iris"), download.ModsHeader);
            await ShotAsync("new-1440-add-mods");
            Dispatcher.UIThread.Invoke(() => page.LoaderSegment.SelectedIndex = 0);
            await Task.Delay(200);
            Check("Add mods: vanilla drops the picks and says to pick a loader", download.PickedMods.Count == 0 && download.ShowModsNeedLoader);
            Dispatcher.UIThread.Invoke(() => { page.LoaderSegment.SelectedItem = fabric; download.ModsOpen = wasOpen; });
            download.SearchModsOffline = false;
            await WaitAsync(() => download.SelectedLoaderVersion is not null, 30);
            Dispatcher.UIThread.Invoke(() => page.NameBox.Text = "我的整合包");
            await Task.Delay(100);
            download.Search = "";
            await WaitAsync(() => download.Items.Count > 10, 5);
            Check("搜索清空后列表重建，已选的版本、加载器和名字都还在",
                download.Target?.Id == "1.21.1" && download.SelectedLoader == fabric && download.CustomName == "我的整合包", $"{download.Target?.Id} {download.CustomName}");
            await ShotAsync("new-1440-configured");

            // Widths: rail → segment; button bar stays.
            foreach (var (w, h) in new[] { (900, 700), (600, 640) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(600);
                var top = Dispatcher.UIThread.Invoke(() => Grid.GetColumn(page.SourceList) == 1);
                var bar = Dispatcher.UIThread.Invoke(() => page.InstallButton.IsEffectivelyVisible
                    && page.InstallButton.TranslatePoint(new Avalonia.Point(0, page.InstallButton.Bounds.Height), window) is { } p && p.Y <= window.Bounds.Height + 1);
                Check($"{w}×{h}：来源变成顶部分段，【安装】在窗口里看得见", top && bar);
                await ShotAsync($"new-{w}");
            }
            L.SetLanguage("en");
            await Task.Delay(500);
            await ShotAsync("new-600-en");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(600);
            await ShotAsync("new-1440-en");
            L.SetLanguage("zh-Hans");
            await Task.Delay(400);

            // Modpack file.
            download.SourceIndex = (int)NewInstanceSource.PackFile;
            var pack = Path.Combine(reportDir, "ni-pack.mrpack");
            WriteMrpack(pack);
            await download.PreviewPackAsync(pack);
            Check("整合包文件：先预览名字、版本、加载器和文件数，不导入",
                download.HasPackPreview && download.PackTitle == "自检整合包 1.0" && download.PackFacts.Contains("1.20.1") && download.PackFacts.Contains("Fabric")
                && download.ImportPackCommand.CanExecute(null) && !Directory.Exists(Path.Combine(game, "versions", "自检整合包")), download.PackFacts);
            Check("整合包文件：【导入并开始游戏】是这一屏唯一的琥珀按钮", await WaitAsync(() => AmberButtons() == 1, 3), $"{AmberButtons()}");
            await ShotAsync("new-pack");
            var bad = Path.Combine(reportDir, "ni-bad.zip");
            File.WriteAllText(bad, "not a zip");
            await download.PreviewPackAsync(bad);
            Check("整合包文件：读不出来时说明原因、不给导入", download.HasPackError && !download.HasPackPreview && !download.ImportPackCommand.CanExecute(null), download.PackError ?? "");
            download.ClearPackCommand.Execute(null);

            // Online modpacks: an entry into Discover.
            download.SourceIndex = (int)NewInstanceSource.OnlinePack;
            var discover = await FindAsync<Button>(b => b.Name == "OpenDiscoverButton" && b.IsEffectivelyVisible);
            Check("在线整合包：进发现页的整合包", Press(discover)
                && await WaitAsync(() => main.CurrentPage is ContentViewModel { TypeIndex: (int)ContentType.Modpack }, 5), main.CurrentPage.Title);
            main.Shell.Navigate("library/new");
            await WaitAsync(() => main.CurrentPage is DownloadViewModel, 5);

            // Copy an instance.
            download.SourceIndex = (int)NewInstanceSource.Copy;
            await Task.Delay(300);
            Check("复制：列出现有实例", download.CopySources.Contains("fabric-iso") && download.CopySources.Contains("1.20.1"), string.Join(",", download.CopySources));
            download.CopySourceId = "1.20.1";
            Check("复制：共用文件夹的实例不给“带上存档”，说明为什么", download.CopySharesFolder);
            download.CopySourceId = "fabric-iso";
            Check("复制：独立文件夹的实例默认名字加序号", download.CopyName == "fabric-iso-2" && !download.CopySharesFolder, download.CopyName);
            download.CopySaves = true;
            await ShotAsync("new-copy");
            var copy = await FindAsync<Button>(b => b.Name == "CopyButton" && b.IsEffectivelyVisible);
            Check("复制：带存档复制，模组和存档都在，仍然是独立文件夹",
                Press(copy) && await WaitAsync(() => download.JustInstalled == "fabric-iso-2", 10)
                && File.Exists(Path.Combine(game, "versions", "fabric-iso-2", "fabric-iso-2.json"))
                && File.Exists(Path.Combine(game, "versions", "fabric-iso-2", "mods", "a.jar"))
                && File.Exists(Path.Combine(game, "versions", "fabric-iso-2", "saves", "World", "level.dat"))
                && !service.UsesSharedDir("fabric-iso-2") && service.InstalledVersions().Contains("fabric-iso-2"), download.Status);
            Check("复制：结果卡给【立即启动】【添加模组】", download.HasInstalled && download.InstalledHasLoader);
            download.CloseInstalledCommand.Execute(null);

            // Another launcher's folder.
            download.SourceIndex = (int)NewInstanceSource.Import;
            await WaitAsync(() => !download.IsScanning && download.OtherDirectories.Count > 0, 5);
            Check("其他启动器：列出找到的游戏文件夹和实例数",
                download.OtherDirectories.Count == 1 && download.OtherDirectories[0].CountText.Contains('1'), string.Join(",", download.OtherDirectories.Select(d => d.Path)));
            await ShotAsync("new-import");
            var use = await FindAsync<Button>(b => ReferenceEquals(b.Command, download.UseGameDirectoryCommand) && b.IsEffectivelyVisible);
            Check("其他启动器：【用这个文件夹】换到那个文件夹（里面的 .minecraft），回到游戏库",
                Press(use) && await WaitAsync(() => main.CurrentPage is LibraryViewModel, 5)
                && Path.GetFullPath(service.Settings.GameDirectory!) == Path.GetFullPath(other) && service.InstalledVersions().Contains("1.12.2"),
                service.Settings.GameDirectory ?? "");

            // Esc closes.
            main.Shell.Navigate("library/new");
            await WaitAsync(() => main.CurrentPage is DownloadViewModel, 5);
            await Task.Delay(300);
            var page2 = window.PageHost.GetVisualDescendants().OfType<DownloadPage>().First();
            Dispatcher.UIThread.Invoke(() => PressKey(page2, Key.Escape));
            Check("Esc 关掉新建实例", await WaitAsync(() => main.CurrentPage is LibraryViewModel, 5), main.CurrentPage.Title);
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            download.SourceIndex = 0;
            download.Search = "";
            download.CloseConfigureCommand.Execute(null);
            download.CloseInstalledCommand.Execute(null);
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }

        int AmberButtons() => Dispatcher.UIThread.Invoke(() =>
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible));
    }

    private static void WriteMrpack(string path)
    {
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open());
        w.Write("""{"formatVersion":1,"game":"minecraft","versionId":"1.0","name":"自检整合包","files":[],"dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.9"}}""");
    }
}
