using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=library (UI batch 4): the library home with five hand-written instances in a throwaway game directory
/// inside the report folder: continue card, needs you, pinned first, search, filter chips, sort, grid/list, open, pin,
/// empty state, and screenshots at three widths in two languages. No network, no game files.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task LibraryAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var originalRecent = service.Settings.RecentInstances;
        var game = Path.Combine(reportDir, "library-game");
        var empty = Path.Combine(reportDir, "library-empty");
        var library = main.Pages.OfType<LibraryViewModel>().First();
        try
        {
            WriteLibraryVersion(game, "fabric-1.20.1", """[{"name":"net.fabricmc:fabric-loader:0.16.9"}]""");
            WriteLibraryVersion(game, "forge-1.20.1", """[{"name":"net.minecraftforge:forge:47.3.0"}]""");
            WriteLibraryVersion(game, "neoforge-1.21.1", """[{"name":"net.neoforged:neoforge:21.1.1"}]""");
            WriteLibraryVersion(game, "1.21.4", "[]");
            WriteLibraryVersion(game, "quilt-1.20.4", """[{"name":"org.quiltmc:quilt-loader:0.26.0"}]""");
            Directory.CreateDirectory(Path.Combine(game, "versions", "fabric-1.20.1", "mods"));
            Directory.CreateDirectory(empty);
            var now = DateTimeOffset.Now;
            service.Settings.Instances = new()
            {
                ["fabric-1.20.1"] = new() { DisplayName = "生存整合包", LastPlayed = now.AddDays(-1), PlayMinutes = 2160 },
                ["forge-1.20.1"] = new() { DisplayName = "工业", State = "incomplete" },
                ["neoforge-1.21.1"] = new() { DisplayName = "空岛", LastPlayed = now.AddHours(-3) },
                ["quilt-1.20.4"] = new() { DisplayName = "Tech", Pinned = true },
            };
            service.Settings.RecentInstances = ["fabric-1.20.1", "neoforge-1.21.1"];
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            // Pages rescan the new game directory in the background and may pick their own instance when the scan lands;
            // let them settle before choosing the current one, as a player would.
            await Task.Delay(1500);
            service.SetCurrentInstance("fabric-1.20.1");
            main.Shell.Navigate("library");
            await WaitAsync(() => library.IsLoaded && library.TotalCount == 5, 10);
            window.Width = 1024;
            window.Height = 700;
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<LibraryPage>().FirstOrDefault();

            Check("游戏库：打开 Jay 落在游戏库主页", main.Shell.Router.Current == Route.Library && page is not null);
            Check("游戏库：5 个实例都在", library.TotalCount == 5 && library.Cards.Count == 5, $"{library.TotalCount}");
            Check("游戏库：继续玩是上次玩的那个，按钮是琥珀色开始键", library.Continue?.Id == "fabric-1.20.1" && page?.ContinuePlay.Classes.Contains("play") == true
                && page.ContinuePlay.IsEffectivelyVisible,
                $"{library.Continue?.Id} current={service.CurrentInstance} recent={string.Join(",", service.Settings.RecentInstances ?? [])}");
            var amber = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
            Check("游戏库：这一屏只有一个琥珀色按钮", amber == 1, $"{amber}");
            Check("游戏库：继续玩卡写了版本、加载器和上次玩", library.Continue?.Facts.Contains("Fabric") == true && library.Continue.LastPlayedText.Contains("昨天"),
                $"{library.Continue?.Facts} / {library.Continue?.LastPlayedText}");
            Check("游戏库：需要你处理里有没装完的实例", library.Needs.Any(n => n.Text.Contains("工业") && n.Text.Contains("没装完")) && page?.NeedsCard.IsEffectivelyVisible == true,
                string.Join(" | ", library.Needs.Select(n => n.Text)));
            Check("游戏库：固定的排最前，然后按最近玩", library.Cards.Select(c => c.Id).Take(3).SequenceEqual(["quilt-1.20.4", "neoforge-1.21.1", "fabric-1.20.1"]),
                string.Join(",", library.Cards.Select(c => c.Id)));
            Check("游戏库：没装完的实例标成有问题", library.Cards.First(c => c.Id == "forge-1.20.1").HasProblem);
            Check("游戏库：中档是封面视图", page?.CardGrid.IsEffectivelyVisible == true && page.CardRows.IsVisible == false);
            await ShotAsync("library-1024-zh");
            await LibraryScrollbarGutterAsync(page);

            library.Search = "forge";
            await Task.Delay(200);
            Check("搜索：按加载器找", library.Cards.Select(c => c.Id).OrderBy(x => x).SequenceEqual(["forge-1.20.1", "neoforge-1.21.1"]), string.Join(",", library.Cards.Select(c => c.Id)));
            library.Search = "生存";
            await Task.Delay(200);
            Check("搜索：按名字找", library.Cards.Count == 1 && library.Cards[0].Id == "fabric-1.20.1");
            library.Search = "没有这个";
            await Task.Delay(200);
            Check("搜索：没结果时说明并给【清除筛选】", library.NoMatches && page?.NoMatches.IsEffectivelyVisible == true);
            library.ClearFiltersCommand.Execute(null);
            library.FilterIndex = 2;
            await Task.Delay(200);
            Check("筛选：有问题", library.Cards.Count == 1 && library.Cards[0].Id == "forge-1.20.1");
            library.FilterIndex = 1;
            await Task.Delay(200);
            Check("筛选：已固定", library.Cards.Count == 1 && library.Cards[0].Id == "quilt-1.20.4");
            library.FilterIndex = 0;
            library.SortIndex = 2;
            await Task.Delay(200);
            Check("排序：按游戏版本（数字比较，固定的仍在前）", library.Cards.Select(c => c.Id).SequenceEqual(["quilt-1.20.4", "1.21.4", "neoforge-1.21.1", "fabric-1.20.1", "forge-1.20.1"])
                || library.Cards.Select(c => c.Id).SequenceEqual(["quilt-1.20.4", "1.21.4", "neoforge-1.21.1", "forge-1.20.1", "fabric-1.20.1"]),
                string.Join(",", library.Cards.Select(c => c.Id)));
            library.SortIndex = 0;
            library.UseListCommand.Execute(null);
            await Task.Delay(300);
            Check("视图：切到列表", page?.CardRows.IsEffectivelyVisible == true && page.CardGrid.IsVisible == false);
            await ShotAsync("library-1024-list");
            library.UseGridCommand.Execute(null);

            foreach (var lang in new[] { "zh-Hans", "en" })
            {
                L.SetLanguage(lang);
                foreach (var (w, h) in new[] { (600.0, 420.0), (1440.0, 900.0) })
                {
                    window.Width = w;
                    window.Height = h;
                    await Task.Delay(600);
                    var tag = $"{lang} {w}×{h}";
                    if (w < 700)
                        Check($"游戏库（{tag}）：紧凑档用列表、继续玩卡带方图", library.IsCompact && page?.CardRows.IsEffectivelyVisible == true && page.ContinueThumb.IsVisible);
                    else
                        Check($"游戏库（{tag}）：宽档“需要你处理”在继续玩右边", Grid.GetColumn(page!.NeedsCard) == 1 && page.CardGrid.IsEffectivelyVisible);
                    Check($"游戏库（{tag}）：继续玩的开始键看得见", page?.ContinuePlay.IsEffectivelyVisible == true);
                    await ShotAsync($"library-{w}-{lang}");
                }
            }
            L.SetLanguage("zh-Hans");
            window.Width = 1024;
            window.Height = 700;
            await Task.Delay(400);

            var sky = library.Cards.First(c => c.Id == "neoforge-1.21.1");
            sky.TogglePinCommand.Execute(null);
            await WaitAsync(() => library.Cards.FirstOrDefault()?.Id is "neoforge-1.21.1" or "quilt-1.20.4" && library.Cards.First(c => c.Id == "neoforge-1.21.1").IsPinned, 5);
            Check("固定：点图钉后记进设置并排到前面", service.MetaOf("neoforge-1.21.1")?.Pinned == true
                && library.Cards.Take(2).Any(c => c.Id == "neoforge-1.21.1"));
            library.Cards.First(c => c.Id == "neoforge-1.21.1").OpenCommand.Execute(null);
            await Task.Delay(400);
            Check("打开：点卡片进实例详情", main.Shell.Router.Current.ToString() == "library/neoforge-1.21.1" && service.CurrentInstance == "neoforge-1.21.1",
                main.Shell.Router.Current.ToString());
            main.Shell.Navigate("library");
            await Task.Delay(300);
            service.RecordPlayed("1.21.4", DateTimeOffset.Now);
            await WaitAsync(() => service.Settings.RecentInstances?.FirstOrDefault() == "1.21.4", 3);
            Check("上次玩：开始一局后排到最近列表最前", service.Settings.RecentInstances?[0] == "1.21.4" && service.MetaOf("1.21.4")?.LastPlayed is not null);

            service.Settings.GameDirectory = empty;
            service.SaveAndReload();
            await WaitAsync(() => library.IsLoaded && library.TotalCount == 0, 10);
            await Task.Delay(400);
            Check("没有实例：空状态带【新建实例】", library.IsEmpty && page?.EmptyLibrary.IsEffectivelyVisible == true && !library.HasContinue);
            await ShotAsync("library-empty");
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            service.Settings.Instances = originalMeta;
            service.Settings.RecentInstances = originalRecent;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
        }
    }

    /// <summary>
    /// The page's vertical scrollbar overlays the content. In a short window (so the page scrolls) at three widths,
    /// the right-aligned 【+ New instance】 button must end left of the scrollbar.
    /// </summary>
    private async Task LibraryScrollbarGutterAsync(LibraryPage? page)
    {
        if (page is null) return;
        foreach (var w in new[] { 760.0, 1024, 1280 })
        {
            window.Width = w;
            window.Height = 480;
            page.LibraryScroll.Offset = default;
            await Task.Delay(500);
            var bar = page.LibraryScroll.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.TemplatedParent == page.LibraryScroll);
            var barLeft = bar?.TranslatePoint(new Avalonia.Point(0, 0), window)?.X ?? double.NaN;
            var button = page.NewInstanceButton;
            var buttonRight = (button.TranslatePoint(new Avalonia.Point(0, 0), window)?.X ?? double.NaN) + button.Bounds.Width;
            Check($"游戏库（{w}×480）：【新建实例】右边在滚动条左边", bar is { IsEffectivelyVisible: true } && buttonRight <= barLeft,
                $"button right {buttonRight:0.#}, scrollbar left {barLeft:0.#}, visible {bar?.IsEffectivelyVisible}");
            if (w == 1280) await ShotAsync("library-scrollbar-gutter");
        }
        window.Width = 1024;
        window.Height = 700;
        await Task.Delay(400);
    }

    private static void WriteLibraryVersion(string root, string id, string libraries)
    {
        var dir = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $$"""{"id":"{{id}}","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":{{libraries}}}""");
    }
}
