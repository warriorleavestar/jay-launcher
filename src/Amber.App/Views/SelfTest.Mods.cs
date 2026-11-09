using System.IO.Compression;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=mods (UI batch 5): Instance › Mods in a throwaway game directory inside the report folder with
/// hand-written mod jars: search, filter chips, inspector (column when wide, over the table otherwise), ticks and the
/// bulk bar, bulk disable / enable / delete with undo, table keys, "Update all" only when there are updates, the vanilla
/// empty state, and screenshots at three widths in two languages. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task ModsTabAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "mods-game");
        const string id = "fabric-loader-1.20.1";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            var dir = Path.Combine(game, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"),
                $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            service.Settings.Instances = new() { [id] = new() { DisplayName = "模组测试" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var mods = Path.Combine(service.WorkDirFor(id), "mods");
            Directory.CreateDirectory(mods);
            WriteFabricMod(mods, "sodium-fabric-0.5.8.jar", "sodium", "Sodium", "0.5.8", "jellysquid3", "让游戏更流畅的渲染优化。");
            WriteFabricMod(mods, "iris-1.7.0.jar", "iris", "Iris", "1.7.0", "coderbot", "光影加载器。");
            WriteFabricMod(mods, "journeymap-5.9.18.jar", "journeymap", "JourneyMap", "5.9.18", "techbrew", "小地图。");
            WriteFabricMod(mods, "modmenu-7.2.2.jar", "modmenu", "Mod Menu", "7.2.2", "Prospector", "模组菜单。");
            WriteFabricMod(mods, "oldmod-1.0.jar.disabled", "oldmod", "Old Mod", "1.0", "someone", "一个关着的模组。");

            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1440;
            window.Height = 760;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            instance.TabIndex = (int)InstanceTab.Mods;
            await WaitAsync(() => instance.Mods.Count == 5, 10);
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().First();
            Check("模组页：读出 5 个模组（名字、版本来自 jar，不联网）", instance.Mods.Count == 5 && instance.Mods.Any(m => m.Name == "Sodium" && m.Version == "0.5.8"),
                string.Join(",", instance.Mods.Select(m => m.Name)));
            Check("模组页：没检查过更新时不显示【全部更新】", !instance.HasModUpdates && page.UpdateAllButton.IsVisible == false);
            Check("模组页：底部写了数量和开着的数量", instance.ModFooter.Contains('5') && instance.ModFooter.Contains('4'), instance.ModFooter);
            Check("模组页：表格的数据就是模组列表本身（没筛选时）", ReferenceEquals(page.ModsList.ItemsSource, instance.Mods));
            await ShotAsync("mods-1440");

            instance.ModSearch = "sod";
            await Task.Delay(200);
            Check("搜索：按名字找", instance.ModsView.Select(m => m.Name).SequenceEqual(["Sodium"]));
            instance.ModSearch = "techbrew";
            await Task.Delay(200);
            Check("搜索：按作者找", instance.ModsView.Select(m => m.Name).SequenceEqual(["JourneyMap"]));
            instance.ModSearch = "没有这个";
            await Task.Delay(200);
            Check("搜索：没结果时说明并给【清除筛选】", instance.ModsNoMatches);
            instance.ClearModFiltersCommand.Execute(null);
            instance.ModFilterIndex = (int)ModFilter.Disabled;
            await Task.Delay(200);
            Check("筛选：已关", instance.ModsView.Select(m => m.Name).SequenceEqual(["Old Mod"]));
            instance.ModFilterIndex = 0;
            await Task.Delay(200);
            Check("筛选：回到全部", instance.ModsView.Count() == 5 && ReferenceEquals(page.ModsList.ItemsSource, instance.Mods));

            // Inspector
            var sodium = instance.Mods.First(m => m.Name == "Sodium");
            instance.InspectedMod = sodium;
            await Task.Delay(300);
            Check("检查器：宽档固定在右边一栏", page.Tier == WidthTier.Wide && page.ModInspector.IsEffectivelyVisible && Grid.GetColumn(page.ModInspector) == 1);
            await ShotAsync("mods-inspector-1440");
            window.Width = 1024;
            await Task.Delay(450);
            Check("检查器：中档盖在表格右边", page.Tier == WidthTier.Medium && Grid.GetColumnSpan(page.ModInspector) == 2 && page.ModInspector.IsEffectivelyVisible);
            await ShotAsync("mods-inspector-1024");
            instance.CloseInspectorCommand.Execute(null);
            await Task.Delay(200);
            Check("检查器：× 关掉", !page.ModInspector.IsVisible);

            // Ticks and bulk bar
            var iris = instance.Mods.First(m => m.Name == "Iris");
            var map = instance.Mods.First(m => m.Name == "JourneyMap");
            iris.Selected = map.Selected = true;
            await Task.Delay(200);
            Check("勾选：浮起批量栏，写已选 2 个", page.ModBulkBar.IsEffectivelyVisible && instance.ModSelectionText.Contains('2'), instance.ModSelectionText);
            amberCheck(page);
            await ShotAsync("mods-bulk-1024");
            instance.DisableSelectedModsCommand.Execute(null);
            await Task.Delay(200);
            Check("批量禁用：两个文件都改成 .disabled", File.Exists(Path.Combine(mods, "iris-1.7.0.jar.disabled")) && File.Exists(Path.Combine(mods, "journeymap-5.9.18.jar.disabled")));
            instance.EnableSelectedModsCommand.Execute(null);
            await Task.Delay(200);
            Check("批量启用：改回来", File.Exists(Path.Combine(mods, "iris-1.7.0.jar")) && File.Exists(Path.Combine(mods, "journeymap-5.9.18.jar")));
            instance.DeleteSelectedModsCommand.Execute(null);
            await WaitAsync(() => instance.Mods.Count == 3, 5);
            Check("批量删除：两个模组从表格和文件夹里消失", instance.Mods.Count == 3 && !File.Exists(Path.Combine(mods, "iris-1.7.0.jar")) && !instance.HasModSelection,
                $"{instance.Mods.Count} {instance.SelectedModCount} {instance.StatusInfo?.Key} {string.Join(" ", instance.StatusInfo?.Args ?? [])} {string.Join(",", Directory.GetFiles(mods).Select(Path.GetFileName))}");
            Toasts.UndoLatest();
            await WaitAsync(() => instance.Mods.Count == 5, 5);
            Check("批量删除：Ctrl+Z 一次全部撤销", instance.Mods.Count == 5 && File.Exists(Path.Combine(mods, "iris-1.7.0.jar")) && File.Exists(Path.Combine(mods, "journeymap-5.9.18.jar")));

            // Keys on the table
            sodium = instance.Mods.First(m => m.Name == "Sodium");
            page.ModsList.SelectedItem = sodium;
            await Task.Delay(200);
            page.ModsList.ContainerFromItem(sodium)?.Focus();
            PressKey(page.ModsList, Key.Space);
            Check("键盘：Space 勾选", sodium.Selected);
            PressKey(page.ModsList, Key.E);
            await Task.Delay(200);
            Check("键盘：E 开 / 关", !sodium.Enabled && File.Exists(Path.Combine(mods, "sodium-fabric-0.5.8.jar.disabled")));
            PressKey(page.ModsList, Key.E);
            await Task.Delay(200);
            PressKey(page.ModsList, Key.Escape);
            Check("键盘：Esc 先关检查器", !instance.HasInspected && sodium.Selected);
            PressKey(page.ModsList, Key.Escape);
            Check("键盘：Esc 再清选择", !instance.HasModSelection);
            PressKey(page.ModsList, Key.A, KeyModifiers.Control);
            Check("键盘：Ctrl+A 全选", instance.SelectedModCount == 5);
            instance.ClearModSelectionCommand.Execute(null);

            // Updates found
            var modMenu = instance.Mods.First(m => m.Name == "Mod Menu");
            modMenu.Update = new ModUpdate(modMenu.Mod.Path, new ContentFile(ContentSource.Modrinth, "mOgUt4GM", "x", "Mod Menu 7.2.3", "modmenu-7.2.3.jar", "", null, 0,
                ["1.20.1"], ["fabric"], "release", DateTimeOffset.Now, []));
            await Task.Delay(200);
            Check("检查出更新后：底部写可更新个数，出现【全部更新】", instance.HasModUpdates && page.UpdateAllButton.IsEffectivelyVisible && instance.ModFooter.Contains("可更新"), instance.ModFooter);
            instance.ModFilterIndex = (int)ModFilter.Updates;
            await Task.Delay(200);
            Check("筛选：有更新", instance.ModsView.Select(m => m.Name).SequenceEqual(["Mod Menu"]));
            instance.ModFilterIndex = 0;

            window.Width = 600;
            await Task.Delay(450);
            Check("紧凑：工具栏只留＋和 ⋯", page.Tier == WidthTier.Compact && !page.CheckUpdatesButton.IsVisible && !page.AddModText.IsVisible);
            await ShotAsync("mods-600");

            L.SetLanguage("en");
            foreach (var width in new[] { 1440d, 1024d, 600d })
            {
                window.Width = width;
                await Task.Delay(450);
                await ShotAsync($"mods-en-{width}");
            }
            L.SetLanguage("zh-Hans");

            // Vanilla
            WriteLibraryVersion(game, "1.21.4", "[]");
            service.NotifyInstalledChanged();
            await WaitAsync(() => instance.Instances.Contains("1.21.4"), 5);
            main.Shell.Navigate("library/1.21.4");
            await WaitAsync(() => instance.Current?.Id == "1.21.4", 10);
            instance.TabIndex = (int)InstanceTab.Mods;
            window.Width = 1024;
            await Task.Delay(500);
            Check("原版实例：空状态说要先装加载器", instance.IsVanilla && !instance.HasMods, $"{instance.Current?.Id} {instance.Mods.Count}");
            await ShotAsync("mods-vanilla");
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
        }

        void amberCheck(InstancePage p)
        {
            var amber = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
            Check("模组页：琥珀色只有英雄区那一个", amber == 1, $"{amber}");
        }
    }

    private static void PressKey(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });

    private static void WriteFabricMod(string dir, string file, string id, string name, string version, string author, string description)
    {
        using var zip = ZipFile.Open(Path.Combine(dir, file), ZipArchiveMode.Create);
        var entry = zip.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($$$"""
            {"schemaVersion":1,"id":"{{{id}}}","version":"{{{version}}}","name":"{{{name}}}","description":"{{{description}}}","authors":["{{{author}}}"],
             "depends":{"fabricloader":">=0.14","minecraft":"1.20.x"}}
            """);
    }
}
