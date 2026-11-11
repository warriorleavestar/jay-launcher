using Amber.App.ViewModels;
using Amber.Core.Content;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=discover (UI batch 6b): Discover in a throwaway game directory inside the report folder. Type
/// chips (a drop-down when compact), the source segment, [+ Queue] on rows and Space, the "to install" bar installing two
/// mods with their dependencies as one task, the detail as a column / drawer / full area, Esc, context mode with
/// [Done, go back], and the single amber button for modpacks. Searches Modrinth and downloads the queued mods.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task DiscoverAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "dc-game");
        const string id = "fabric-1.20.1";
        var content = Page<ContentViewModel>();
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            var dir = Path.Combine(game, "versions", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"),
                $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
            new Amber.Core.Launch.InstanceSettings { Isolate = true }.Save(new Amber.Core.Game.GameDirectory(game), id);
            service.Settings.Instances = [];
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            service.SetCurrentInstance(id);

            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("discover");
            await WaitAsync(() => main.CurrentPage is ContentViewModel, 5);
            content.TypeIndex = (int)ContentType.Mod;
            content.SourceIndex = 0;
            await WaitAsync(() => content.TargetInstance == id, 5);
            await SearchAsync("lithium");
            var page = window.PageHost.GetVisualDescendants().OfType<ContentPage>().First();
            Check("发现：类型是一排芯片，来源是分段", Dispatcher.UIThread.Invoke(() => page.TypeChips.IsEffectivelyVisible && !page.TypeCombo.IsVisible && page.SourceChips.IsEffectivelyVisible));
            Check("发现：搜到结果，装到的是测试实例", content.Results.Count > 0 && content.TargetInstance == id, $"{content.Results.Count} 个");
            Check("发现：“装到”标记写实例的名字，默认是刚看的那个实例", Dispatcher.UIThread.Invoke(() => page.TargetMarker.IsEffectivelyVisible
                && page.TargetMarker.SelectedItem is InstanceChoice { Id: id } choice && choice.Name == (main.Service.MetaOf(id)?.DisplayName is { Length: > 0 } name ? name : id)), content.TargetChoice?.Name ?? "");

            // [+ Queue] on a row.
            var first = content.Results[0];
            var queue = await FindAsync<Button>(b => ReferenceEquals(b.Command, content.ToggleQueueCommand) && ReferenceEquals(b.CommandParameter, first) && b.IsEffectivelyVisible);
            Check("结果行：点【＋待装】加入待装，按钮变“已加入”", Press(queue) && await WaitAsync(() => content.QueueCount == 1 && first.Queued, 20), content.Status);
            var bar = await FindAsync<Border>(b => b.Name == "QueueBar" && b.IsEffectivelyVisible);
            Check("待装条出现，写数量和名字", bar is not null && content.QueueText.Contains(first.Title), content.QueueText);
            Check("详情跟着打开（宽窗是右边一栏）", content.HasSelection && Dispatcher.UIThread.Invoke(() => Grid.GetColumn(page.DetailPane) == 1));
            await ShotAsync("discover-1440");
            // The drawer covers the list on purpose at medium widths: check the list itself.
            var keepDetail = content.Detail;
            content.Detail = null;
            await LayoutHereAsync("发现页");
            content.Detail = keepDetail;
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(400);

            // Space on the selected row takes it out again, and back in.
            Dispatcher.UIThread.Invoke(() =>
            {
                page.ResultsList.SelectedItem = first;
                // List keys only act with focus inside the list; the ListBox itself can't take focus, its row can.
                page.ResultsList.ContainerFromItem(first)?.Focus();
                PressKey(page.ResultsList, Key.Space);
            });
            Check("Space：从待装里拿掉", await WaitAsync(() => content.QueueCount == 0 && !first.Queued, 5));
            Dispatcher.UIThread.Invoke(() => PressKey(page.ResultsList, Key.Space));
            Check("Space：再放回去", await WaitAsync(() => content.QueueCount == 1, 20));

            // A second mod from another search: the queue survives a new search.
            await SearchAsync("ferrite core");
            var second = content.Results.FirstOrDefault(r => !r.Queued);
            if (second is not null) await content.ToggleQueueCommand.ExecuteAsync(second);
            Check("换个关键词搜索，待装还在，可以再加一个", content.QueueCount == 2, content.QueueText);
            Check("待装条按钮写“安装 2 个”", content.InstallQueueLabel.Contains('2'), content.InstallQueueLabel);
            Check("琥珀色：模组页没有琥珀按钮", AmberCount() == 0, $"{AmberCount()}");

            var install = await FindAsync<Button>(b => b.Name == "InstallQueueButton" && b.IsEffectivelyVisible);
            var pressed = Press(install);
            await WaitAsync(() => content.IsBusy || content.QueueCount == 0, 5);
            var finished = await WaitAsync(() => !content.IsBusy, 180);
            var modsDir = Path.Combine(dir, "mods");
            Check("【安装 2 个】一次装完（一个任务），连前置一起装进测试实例的 mods",
                pressed && finished && content.QueueCount == 0 && SafeList(modsDir).Count(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) >= 2
                && service.Tasks.All.Any(t => t.Kind == "content"), $"{content.Status}；{string.Join(",", SafeList(modsDir))}");
            await ShotAsync("discover-installed");

            // Widths: drawer, then full area and a type drop-down.
            content.Detail = content.Results.FirstOrDefault();
            foreach (var (w, h) in new[] { (900, 700), (600, 640) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(700);
                var ok = Dispatcher.UIThread.Invoke(() => Grid.GetColumn(page.DetailPane) == 0 && page.DetailPane.ZIndex == 1
                    && (w >= 760 ? page.TypeChips.IsVisible : page.TypeCombo.IsVisible && Grid.GetRow(page.SourceChips) == 1));
                Check($"{w}×{h}：详情盖在结果上{(w < 760 ? "，类型变成下拉、来源换到第二行" : "")}", ok);
                await ShotAsync($"discover-{w}");
            }
            L.SetLanguage("en");
            await Task.Delay(500);
            await ShotAsync("discover-600-en");
            L.SetLanguage("zh-Hans");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(600);

            // Esc closes the detail.
            Dispatcher.UIThread.Invoke(() => PressKey(page, Key.Escape));
            Check("Esc 关掉详情", await WaitAsync(() => !content.HasSelection, 3));

            // Context mode: breadcrumb and [Done, go back].
            var returned = false;
            content.BrowseFor(id, ContentType.Mod, back: () => returned = true);
            var done = await FindAsync<Button>(b => b.Name == "DoneReturnButton" && b.IsEffectivelyVisible);
            Check("上下文模式：写着给哪个实例找什么，【完成，返回】回去", done is not null && content.ReturnContext?.Contains(id) == true && Press(done) && returned);

            // Modpacks: the only amber button is [Install and play].
            content.TypeIndex = (int)ContentType.Modpack;
            await WaitAsync(() => !content.IsSearching && content.Results.Count > 0, 30);
            content.Selected = content.Results.FirstOrDefault();
            await WaitAsync(() => content.SelectedFile is not null, 30);
            Check("整合包：详情给【安装并开始游戏】（这一屏唯一的琥珀按钮）和【只安装】，结果行没有【＋待装】",
                await WaitAsync(() => AmberCount() == 1, 5) && Dispatcher.UIThread.Invoke(() => page.InstallOnlyButton.IsEffectivelyVisible)
                && !window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, content.ToggleQueueCommand) && b.IsEffectivelyVisible), $"{AmberCount()}");
            Check("整合包：自己建实例，不问“装到”", Dispatcher.UIThread.Invoke(() => !page.TargetMarker.IsEffectivelyVisible));
            await ShotAsync("discover-modpack");
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            content.ClearQueueCommand.Execute(null);
            content.Detail = null;
            content.TypeIndex = (int)ContentType.Mod;
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
        }

        async Task SearchAsync(string query)
        {
            content.Query = query;
            content.SearchCommand.Execute(null);
            await Task.Delay(200);
            await WaitAsync(() => !content.IsSearching && content.Results.Count > 0, 30);
        }

        int AmberCount() => Dispatcher.UIThread.Invoke(() =>
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible));

        static IEnumerable<string> SafeList(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path).Select(Path.GetFileName)! : [];
    }
}
