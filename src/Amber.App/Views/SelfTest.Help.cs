using Amber.App.ViewModels;
using Amber.Core.Help;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=help (UI batch 8d): F1 opens this page's article in the drawer on every route; Ctrl+F searches
/// in the drawer (pinyin initials too); a jay: link goes to the setting; Esc closes; "Open help center" and Shift+F1
/// open the help center, whose catalog follows the width (left column / menu / list then article). The command palette
/// lists articles under "?". Screenshots at three widths in two languages. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task HelpAsync()
    {
        var help = main.Help;
        var center = Page<HelpCenterViewModel>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("library");
            await Task.Delay(400);
            PressKey(window, Key.F1);
            await Task.Delay(300);
            Check("F1：打开本页帮助抽屉（游戏库）", help.IsOpen && help.Article?.Article.Id == "library"
                && Dispatcher.UIThread.Invoke(() => window.HelpDrawer.IsEffectivelyVisible && Math.Abs(window.HelpDrawer.Bounds.Width - 400) < 1),
                help.Article?.Article.Id ?? "none");
            Check("抽屉标题写“帮助 · 页面名”", help.Header == L.F("help.drawer.title", L.T("shell.nav.library")), help.Header);
            await ShotAsync("help-drawer-1440");
            var shotBlock = help.Article?.Blocks.FirstOrDefault(b => b.Block.Kind == HelpBlockKind.Shot);
            var shownImages = Dispatcher.UIThread.Invoke(() => window.HelpDrawer.GetVisualDescendants().OfType<Image>()
                .Count(i => i.IsEffectivelyVisible && i.Source is not null && i.Bounds.Width > 100));
            Check("文章里的截图显示出来（游戏库）", shotBlock is { IsShot: true } && shownImages > 0, $"{shotBlock?.Block.Shot} images={shownImages}");
            PressKey(window, Key.F1);
            Check("再按 F1 关上", !help.IsOpen);

            // Every route has an article.
            var missing = new List<string>();
            foreach (var route in (string[])["library", "library/start", "library/new", "library/logs", "discover", "play", "settings", "settings/tools", "accounts", "about", "help"])
            {
                main.Shell.Navigate(route);
                await Task.Delay(250);
                main.OpenHelp();
                if (help.Article is null) missing.Add(route);
                help.Close();
            }
            Check("每个路由 F1 都有文章", missing.Count == 0, string.Join(",", missing));

            // Search in the drawer, then a jay: link.
            main.Shell.Navigate("play");
            await Task.Delay(300);
            main.OpenHelp();
            Check("联机页 F1 打开“联机”", help.Article?.Article.Id == "multiplayer");
            PressKey(window, Key.F, KeyModifiers.Control);
            Check("抽屉里 Ctrl+F：光标进搜索框", Dispatcher.UIThread.Invoke(() => window.HelpDrawerSearch.IsFocused));
            help.Query = "ghzy";
            await Task.Delay(200);
            Check("拼音首字母 ghzy 找到“给孩子用”", help.Results.FirstOrDefault()?.Id == "child-mode" && !help.ShowArticle);
            help.OpenArticleCommand.Execute(help.Results[0]);
            Check("点结果：显示文章，搜索清空", help.Article?.Article.Id == "child-mode" && help.ShowArticle && !help.HasQuery);
            await ShotAsync("help-drawer-child-1440");
            help.Query = "完全没有这个词xyz";
            await Task.Delay(200);
            Check("搜不到时写明", help.NoResults && Dispatcher.UIThread.Invoke(() => window.HelpNoResults.IsEffectivelyVisible));
            help.Query = "";
            var link = help.Article!.Blocks.SelectMany(b => b.Links).First(l => l.IsRoute);
            help.FollowLinkCommand.Execute(link);
            var revealed = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "ChildModeBox" && window.RevealedField.IsEffectivelyVisible), 3);
            Check("文章里的 jay: 链接直接跳到设置，抽屉关上", revealed && !help.IsOpen);

            main.OpenHelp();
            PressKey(window, Key.Escape);
            Check("Esc 关闭抽屉", !help.IsOpen);

            // Help center from the drawer and from Shift+F1.
            main.Shell.Navigate("library/logs");
            await Task.Delay(300);
            main.OpenHelp();
            help.OpenCenterCommand.Execute(null);
            await WaitAsync(() => main.CurrentPage is HelpCenterViewModel, 3);
            await Task.Delay(400);
            var page = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<HelpCenterPage>().First());
            Check("“打开帮助中心”：路由 help/logs，显示同一篇", main.Shell.Router.Current.ToString().StartsWith("help/logs", StringComparison.Ordinal)
                && center.Article?.Article.Id == "logs" && !help.IsOpen, main.Shell.Router.Current.ToString());
            Check("宽窗口：左边分类目录 + 正文", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Wide && page.CatalogPane.IsEffectivelyVisible
                && page.ArticleBody.IsEffectivelyVisible && !page.ArticlePicker.IsVisible) && center.Groups.Count >= 8, center.Groups.Count.ToString());
            await ShotAsync("help-center-1440");
            main.Shell.Navigate("library");
            await Task.Delay(300);
            PressKey(window, Key.F1, KeyModifiers.Shift);
            await WaitAsync(() => main.CurrentPage is HelpCenterViewModel, 3);
            Check("Shift+F1 打开帮助中心", main.CurrentPage is HelpCenterViewModel);
            center.Query = "F8";
            await Task.Delay(200);
            Check("帮助中心搜索 F8 找到“看日志”", center.Results.Any(r => r.Id == "logs"));
            center.Query = "";

            // Medium: the catalog is a menu.
            window.Width = 900;
            window.Height = 760;
            await Task.Delay(600);
            page = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<HelpCenterPage>().First());
            Check("中等宽度：目录变成下拉", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Medium && page.ArticlePicker.IsEffectivelyVisible && !page.CatalogPane.IsVisible));
            await ShotAsync("help-center-900");

            // Compact: catalog first, then the article with a back button.
            window.Width = 600;
            window.Height = 640;
            center.BackToCatalogCommand.Execute(null);
            await Task.Delay(600);
            Check("紧凑：第一屏是目录", Dispatcher.UIThread.Invoke(() => page.Tier == WidthTier.Compact && page.CatalogPane.IsEffectivelyVisible && !page.ArticleScroll.IsVisible));
            await ShotAsync("help-center-600-catalog");
            center.OpenCommand.Execute("network-list");
            await Task.Delay(400);
            Check("紧凑：点进文章，有返回", Dispatcher.UIThread.Invoke(() => page.ArticleScroll.IsEffectivelyVisible && !page.CatalogPane.IsVisible
                && page.CatalogBackButton.IsEffectivelyVisible) && center.Article?.Article.Id == "network-list");
            await ShotAsync("help-center-600-article");
            main.OpenHelp();
            await Task.Delay(300);
            Check("紧凑：帮助抽屉铺满", Dispatcher.UIThread.Invoke(() => window.HelpDrawer.Bounds.Width > 450), Dispatcher.UIThread.Invoke(() => window.HelpDrawer.Bounds.Width.ToString("0")));
            help.Close();

            // Command palette: "?" lists help articles.
            main.Palette.Open();
            main.Palette.Query = "?logs";
            var paletteHit = main.Palette.Results.Any(r => r.Item.Id == "help:logs");
            main.Palette.Close();
            Check("命令面板 ? 能找到帮助文章", paletteHit);

            // English.
            L.SetLanguage("en");
            await Task.Delay(500);
            Check("换英文：文章跟着换", center.Article?.Title == "Jay collects no data: the network list", center.Article?.Title ?? "");
            await ShotAsync("help-center-600-en");
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(600);
            main.OpenHelp();
            await Task.Delay(300);
            await ShotAsync("help-1440-en");
            help.Close();
            await TermHintsAsync();
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            help.Close();
            help.Query = "";
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
