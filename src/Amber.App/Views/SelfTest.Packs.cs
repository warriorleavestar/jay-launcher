using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=packs (UI batch 7a): Instance › Resource packs and Shaders in a throwaway game directory inside
/// the report folder. On / off lists read from options.txt (top = highest), ticking, ↑↓ and Alt+↑↓ write the order back,
/// pack age labels, Delete; shaders: the Iris banner without a loader, picking a pack writes iris.properties, "No shaders".
/// Screenshots at two widths in two languages. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task PacksAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "packs-game");
        const string id = "fabric-loader-1.20.1";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            var dir = Path.Combine(game, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"),
                $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            service.Settings.Instances = new() { [id] = new() { DisplayName = "资源包测试" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var work = service.WorkDirFor(id);
            var packs = Directory.CreateDirectory(Path.Combine(work, "resourcepacks")).FullName;
            WritePack(packs, "Faithful.zip", 15);
            WritePack(packs, "Old Classic.zip", 3);
            WritePack(packs, "Future.zip", 34);
            WritePack(packs, "Fonts.zip", 15);
            File.WriteAllLines(Path.Combine(work, "options.txt"), ["version:3465", """resourcePacks:["vanilla","file/Fonts.zip","file/Faithful.zip"]""", "lang:zh_cn"]);
            var shaders = Directory.CreateDirectory(Path.Combine(work, "shaderpacks")).FullName;
            File.WriteAllText(Path.Combine(shaders, "BSL_v8.2.zip"), "zip");
            File.WriteAllText(Path.Combine(shaders, "Complementary.zip"), "zip");

            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1440;
            window.Height = 760;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            instance.TabIndex = (int)InstanceTab.ResourcePacks;
            await WaitAsync(() => instance.EnabledPacks.Count == 2, 10);
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().First();
            Check("资源包：已启用按 options.txt 读出，越靠上越优先", instance.EnabledPacks.Select(p => p.Name).SequenceEqual(["Faithful.zip", "Fonts.zip"]),
                string.Join(",", instance.EnabledPacks.Select(p => p.Name)));
            Check("资源包：没启用的按名字列在下面", instance.DisabledPacks.Select(p => p.Name).SequenceEqual(["Future.zip", "Old Classic.zip"]),
                string.Join(",", instance.DisabledPacks.Select(p => p.Name)));
            Check("资源包：版本旧 / 版本新标出来，合适的不标",
                instance.DisabledPacks.Single(p => p.Name == "Old Classic.zip").Age == PackAge.Older
                && instance.DisabledPacks.Single(p => p.Name == "Future.zip").Age == PackAge.Newer
                && !instance.EnabledPacks[0].HasAge);
            await ShotAsync("packs-1440");

            instance.TogglePackCommand.Execute(instance.DisabledPacks.Single(p => p.Name == "Old Classic.zip"));
            Check("勾上：放到最上面，写回 options.txt，别的选项不动",
                instance.EnabledPacks[0].Name == "Old Classic.zip"
                && ResourcePackOptions.Enabled(work).SequenceEqual(["Old Classic.zip", "Faithful.zip", "Fonts.zip"])
                && File.ReadAllLines(Path.Combine(work, "options.txt")).Contains("lang:zh_cn"),
                string.Join(",", ResourcePackOptions.Enabled(work)));
            instance.MovePackDownCommand.Execute(instance.EnabledPacks[0]);
            Check("↓：优先级降一格并存下", ResourcePackOptions.Enabled(work).SequenceEqual(["Faithful.zip", "Old Classic.zip", "Fonts.zip"]));
            instance.MovePackUpCommand.Execute(instance.EnabledPacks[0]);
            Check("↑ 到顶不动", ResourcePackOptions.Enabled(work)[0] == "Faithful.zip");

            // Keys on the list: Alt+↓ moves, Space turns off.
            var list = page.EnabledPackList;
            Dispatcher.UIThread.Invoke(() =>
            {
                list.SelectedItem = instance.EnabledPacks[0];
                // The ListBox itself can't take focus; the row can, and list keys only act with focus inside the list.
                list.ContainerFromItem(instance.EnabledPacks[0])?.Focus();
            });
            PressKey(list, Key.Down, KeyModifiers.Alt);
            await Task.Delay(300);
            Check("Alt+↓：焦点留在列表里，接着按键还管用", Dispatcher.UIThread.Invoke(() =>
                TopLevel.GetTopLevel(list)?.FocusManager?.GetFocusedElement() is Visual v && (v == list || list.IsVisualAncestorOf(v))));
            Check("Alt+↓：在列表里调顺序", ResourcePackOptions.Enabled(work).SequenceEqual(["Old Classic.zip", "Faithful.zip", "Fonts.zip"]),
                string.Join(",", ResourcePackOptions.Enabled(work)));
            Dispatcher.UIThread.Invoke(() => list.SelectedItem = instance.EnabledPacks.Single(p => p.Name == "Fonts.zip"));
            PressKey(list, Key.Space);
            Check("空格：关掉，回到未启用", !ResourcePackOptions.Enabled(work).Contains("Fonts.zip") && instance.DisabledPacks.Any(p => p.Name == "Fonts.zip"));
            instance.DeletePackCommand.Execute(instance.DisabledPacks.Single(p => p.Name == "Future.zip"));
            Check("删除：进“最近删除”，列表里没了", !File.Exists(Path.Combine(packs, "Future.zip")) && instance.DisabledPacks.All(p => p.Name != "Future.zip"));

            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            Check("窄窗口：资源包行不超出页面", Dispatcher.UIThread.Invoke(() =>
                list.Bounds.Width > 0 && list.Bounds.Width <= page.Bounds.Width + 1), $"{list.Bounds.Width}/{page.Bounds.Width}");
            await ShotAsync("packs-600");
            L.SetLanguage("en");
            await Task.Delay(400);
            await ShotAsync("packs-600-en");
            L.SetLanguage("zh-Hans");

            // Shaders: no loader yet → banner and [Install Iris].
            window.Width = 1440;
            window.Height = 760;
            instance.TabIndex = (int)InstanceTab.ShaderPacks;
            await WaitAsync(() => instance.ShaderPacks.Count == 2, 10);
            await Task.Delay(400);
            Check("光影：没装 Iris 时写明要先装，并给【装 Iris】", !instance.HasShaderLoader
                && Dispatcher.UIThread.Invoke(() => page.ShaderLoaderCard.IsEffectivelyVisible || page.ShaderNoteBox.IsEffectivelyVisible));
            Check("光影：Forge 上搜 Oculus，其余搜 Iris", InstanceViewModel.ShaderLoaderQuery("forge") == "Oculus" && InstanceViewModel.ShaderLoaderQuery("fabric") == "Iris");
            await ShotAsync("shaders-no-loader-1440");

            var mods = Directory.CreateDirectory(Path.Combine(work, "mods")).FullName;
            File.WriteAllText(Path.Combine(mods, "iris-mc1.20.1-1.7.0.jar"), "jar");
            instance.LoadShaderPacks();
            Check("装了 Iris：横幅消失，默认“不用光影”", instance.HasShaderLoader && instance.ShadersOff);
            instance.UseShaderCommand.Execute(instance.ShaderPacks.Single(p => p.Name == "BSL_v8.2.zip"));
            var props = Path.Combine(work, "config", "iris.properties");
            Check("选光影：写进 iris.properties 并打开", File.Exists(props) && File.ReadAllLines(props).Contains("shaderPack=BSL_v8.2.zip")
                && File.ReadAllLines(props).Contains("enableShaders=true") && ShaderSelection.Current(work) == "BSL_v8.2.zip");
            instance.LoadShaderPacks();
            Check("重新打开：记得用的是哪个", instance.ShaderPacks.Single(p => p.IsOn).Name == "BSL_v8.2.zip");
            await Task.Delay(300);
            await ShotAsync("shaders-1440");
            instance.UseShaderCommand.Execute(null);
            Check("【不用光影】：关掉，选择留着", ShaderSelection.Current(work) is null && instance.ShadersOff
                && File.ReadAllLines(props).Contains("shaderPack=BSL_v8.2.zip"));
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(500);
            await ShotAsync("shaders-600");
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            window.Width = 1440;
            window.Height = 900;
        }
    }

    private static void WritePack(string dir, string file, int format)
    {
        using var archive = ZipFile.Open(Path.Combine(dir, file), ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("pack.mcmeta").Open());
        writer.Write($$$"""{"pack":{"pack_format":{{{format}}},"description":"test"}}""");
    }
}
