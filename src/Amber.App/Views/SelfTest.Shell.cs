using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=shell（新界面第 2 批）：外壳矩阵（600×420、760×480、1024×700、1440×900 × 四种语言）截图，
/// 检查隐私行、三个入口、设置和账号任何尺寸都看得见；侧栏只在宽窗口展开；矮窗口顶栏收到 40；
/// 快捷键切入口、返回前进、每个入口记得离开时的位置、旧的九页都能从新外壳走到、settings#字段 定位、带上下文返回。
/// 不碰游戏目录，不联网。
/// </summary>
internal sealed partial class SelfTest
{
    private async Task ShellAsync()
    {
        var shell = main.Shell;
        try
        {
            Check("最小窗口是 600×420（屏幕放不下时更小）", window.MinWidth <= 600 && window.MinHeight <= 420 && window.MinWidth >= 300,
                $"{window.MinWidth}×{window.MinHeight}");
            Check("第一次打开新版有“东西搬到哪了”卡片", window.MovedNotice.IsVisible);
            window.MovedNoticeClose.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            Check("关掉卡片后不再显示，并记进设置", !window.MovedNotice.IsVisible && File.ReadAllText(AppSettings.FilePath).Contains("\"ShellMovedNoticeSeen\": true", StringComparison.Ordinal));

            await MatrixAsync();
            await KeyboardAndHistoryAsync(shell);
            await ReachEveryOldPageAsync(shell);
            await FieldAndReturnAsync(shell);
            await SidebarSettingAsync();
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            L.SetLanguage("zh-Hans");
        }
    }

    private static readonly (double W, double H)[] ShellSizes = [(600, 420), (760, 480), (1024, 700), (1440, 900)];

    private async Task MatrixAsync()
    {
        main.Shell.Navigate("library");
        foreach (var lang in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            L.SetLanguage(lang);
            foreach (var (w, h) in ShellSizes)
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(450);
                var tag = $"{lang} {w}×{h}";
                var bounds = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
                bool Inside(Control c) => c.IsEffectivelyVisible && c.TranslatePoint(new Point(0, 0), window) is { } p
                    && bounds.Contains(new Rect(p, c.Bounds.Size).Deflate(0.5));
                var privacy = window.GetVisualDescendants().OfType<PrivacyRow>().First();
                Check($"外壳（{tag}）：隐私行看得见", Inside(privacy));
                var items = window.NavList.GetVisualDescendants().OfType<ListBoxItem>().ToList();
                Check($"外壳（{tag}）：三个入口都在窗口里", items.Count == 3 && items.All(Inside), $"{items.Count(Inside)}/{items.Count}");
                Check($"外壳（{tag}）：设置和账号看得见", Inside(window.SettingsButton) && Inside(window.AccountButton));
                var expect = w >= 1340;
                Check($"外壳（{tag}）：侧栏{(expect ? "展开" : "收成图标栏")}", window.SidebarExpanded == expect
                    && Math.Abs(window.SidebarHost.Bounds.Width - (expect ? ShellRules.SidebarWidth : ShellRules.RailWidth)) < 1, $"{window.SidebarHost.Bounds.Width}");
                var topBar = window.ClientSize.Height < LayoutTiers.Short ? 40 : 48;
                Check($"外壳（{tag}）：顶栏高 {topBar}", Math.Abs(window.TopBar.Bounds.Height - topBar) < 1, $"{window.TopBar.Bounds.Height}");
                if (!expect)
                {
                    // 图标栏：短名在图标下面、没被截断（很矮时只留图标）；隐私行的“无遥测”不从单词中间断行。
                    var shorts = window.NavList.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("nav-short")).ToList();
                    var tight = window.HeightTierNow == HeightTier.Tight; // 回差：从 420 拉到 480 时还算很矮
                    Check($"外壳（{tag}）：图标栏短名{(tight ? "收起" : "在图标下面、没被截断")}", tight
                        ? shorts.All(t => !t.IsEffectivelyVisible)
                        : shorts.Count == 3 && shorts.All(t => t.IsEffectivelyVisible && !t.TextLayout.TextLines.Any(l => l.HasCollapsed)
                            && t.TranslatePoint(new Point(0, 0), t.Parent as Visual ?? t)?.Y > 4),
                        string.Join(",", shorts.Select(t => t.Text)));
                    var privacyText = privacy.GetVisualDescendants().OfType<TextBlock>().First();
                    var text = privacyText.Text ?? "";
                    var breaks = privacyText.TextLayout.TextLines.Skip(1).Select(l => l.FirstTextSourceIndex).ToList();
                    Check($"外壳（{tag}）：图标栏隐私行不从单词中间断行", breaks.All(i => i <= 0 || i >= text.Length
                        || !(char.IsAsciiLetter(text[i - 1]) && char.IsAsciiLetter(text[i]))), $"{text} 断在 {string.Join(",", breaks)}");
                }
                Check($"外壳（{tag}）：返回按钮可点区域 ≥ 32", window.BackButton.Bounds.Width >= 32 && window.BackButton.Bounds.Height >= 32);
                await ShotAsync($"shell-{lang}-{w}x{h}");
            }
        }
        L.SetLanguage("zh-Hans");
        window.Width = 1024;
        window.Height = 700;
        await Task.Delay(400);
    }

    private void Press(Key key, KeyModifiers modifiers) =>
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = window });

    private async Task KeyboardAndHistoryAsync(ShellViewModel shell)
    {
        shell.Navigate("library");
        await Task.Delay(200);
        Press(Key.D2, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+2 去发现", main.CurrentPage is ContentViewModel && shell.SelectedSection?.Id == "discover");
        Press(Key.D3, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+3 去联机", main.CurrentPage is MultiplayerViewModel && shell.SelectedSection?.Id == "multiplayer");
        Press(Key.OemComma, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+, 去设置，侧栏设置按钮是选中状态", main.CurrentPage is SettingsViewModel && window.SettingsButton.Classes.Contains("active") && shell.SelectedSection is null);
        Press(Key.D1, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+1 回游戏库", main.CurrentPage is LibraryViewModel && shell.SelectedSection?.Id == "library");

        // 游戏库里点子页签：新建实例 → 运行记录；返回、前进在游戏库自己的历史里走。
        shell.SelectedSubPage = main.Pages.OfType<DownloadViewModel>().Single();
        await Task.Delay(300);
        shell.SelectedSubPage = main.Pages.OfType<LogViewModel>().Single();
        await Task.Delay(300);
        Check("子页签切到运行记录", main.CurrentPage is LogViewModel && shell.Router.Current.ToString() == "library/logs");
        Press(Key.Left, KeyModifiers.Alt);
        await Task.Delay(300);
        Check("Alt+← 返回到新建实例", main.CurrentPage is DownloadViewModel && shell.CanGoForward);
        Press(Key.Right, KeyModifiers.Alt);
        await Task.Delay(300);
        Check("Alt+→ 前进到运行记录", main.CurrentPage is LogViewModel && !shell.CanGoForward);
        // 切到发现再切回：回到离开时的位置。
        shell.GoSectionCommand.Execute("discover");
        await Task.Delay(300);
        Check("切到发现时返回按钮不能点（发现还没有历史）", !window.BackButton.IsEnabled);
        shell.GoSectionCommand.Execute("library");
        await Task.Delay(300);
        Check("切回游戏库，回到离开时的运行记录", main.CurrentPage is LogViewModel);
        window.BackButton.Command?.Execute(null);
        await Task.Delay(300);
        Check("顶栏 ← 按钮返回", main.CurrentPage is DownloadViewModel);
        // 已经在游戏库时再点游戏库：回游戏库首页。
        shell.GoSectionCommand.Execute("library");
        await Task.Delay(300);
        Check("再点一次当前入口回到入口首页", main.CurrentPage is LibraryViewModel);
        await ShotAsync("shell-history");
    }

    /// <summary>旧版九页每一页都能从新外壳的按钮走到（§11.9：功能一个不少）。</summary>
    private async Task ReachEveryOldPageAsync(ShellViewModel shell)
    {
        var reached = new HashSet<Type>();
        async Task Visit(Action go)
        {
            go();
            await Task.Delay(250);
            reached.Add(main.CurrentPage.GetType());
        }
        shell.GoSectionCommand.Execute("library");
        await Task.Delay(200);
        foreach (var sub in shell.SubPages.ToList()) await Visit(() => shell.SelectedSubPage = sub);
        await Visit(() => shell.GoSectionCommand.Execute("discover"));
        await Visit(() => shell.GoSectionCommand.Execute("multiplayer"));
        await Visit(() => window.SettingsButton.Command!.Execute(null));
        foreach (var sub in shell.SubPages.ToList()) await Visit(() => shell.SelectedSubPage = sub);
        await Visit(() => main.Shell.AccountMenu.ManageCommand.Execute(null));
        await Visit(() => Page<SettingsViewModel>().GoAboutCommand.Execute(null));
        await Visit(() => main.Shell.Navigate("help"));
        var missing = main.Pages.Select(p => p.GetType()).Where(t => !reached.Contains(t)).Select(t => t.Name).ToList();
        Check("旧版九页、游戏库主页、关于页和帮助中心都能从新外壳走到", missing.Count == 0 && reached.Count == 12, missing.Count == 0 ? $"{reached.Count} 页" : "走不到：" + string.Join("、", missing));
        await ShotAsync("shell-accounts");
        await AccountMenuAsync();
    }

    private async Task FieldAndReturnAsync(ShellViewModel shell)
    {
        shell.Navigate("settings#sidebar");
        var ok = await WaitAsync(() => window.RevealedField?.Name == "SidebarBox", 3);
        await Task.Delay(300);
        Check("settings#sidebar 定位到侧栏选项并给焦点", ok && window.RevealedField!.IsFocused && window.RevealedField.Classes.Contains("field-flash"),
            window.RevealedField?.Name ?? "没找到");
        await ShotAsync("shell-field");

        shell.Navigate("library");
        await Task.Delay(200);
        shell.Navigate("discover", "游戏库 › 添加内容");
        await Task.Delay(300);
        Check("带上下文跳到发现：顶栏有面包屑和【完成，返回】", shell.HasReturn && shell.ReturnLabel == "游戏库 › 添加内容");
        await ShotAsync("shell-return");
        shell.ReturnCommand.Execute(null);
        await Task.Delay(300);
        Check("【完成，返回】回到游戏库", main.CurrentPage is LibraryViewModel && !shell.HasReturn);
    }

    private async Task SidebarSettingAsync()
    {
        var settings = main.Pages.OfType<SettingsViewModel>().Single();
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        Check("宽窗口自动展开侧栏", window.SidebarExpanded);
        settings.SelectedSidebarIndex = (int)SidebarMode.Collapsed;
        await Task.Delay(400);
        Check("设置成总是收起：宽窗口里也是图标栏", !window.SidebarExpanded && window.SidebarHost.Bounds.Width < 100);
        window.Width = 900;
        settings.SelectedSidebarIndex = (int)SidebarMode.Expanded;
        await Task.Delay(400);
        Check("设置成总是展开：内容区还够宽时展开", window.SidebarExpanded);
        window.Width = 760;
        await Task.Delay(400);
        Check("总是展开但展开后内容区不到 640：仍是图标栏", !window.SidebarExpanded);
        settings.ResetFieldCommand.Execute("sidebar");
        await Task.Delay(300);
        Check("恢复默认：自动", settings.SelectedSidebarIndex == 0 && !File.ReadAllText(AppSettings.FilePath).Contains("\"SidebarMode\": \"expanded\"", StringComparison.Ordinal));
    }

    /// <summary>Account menu (§6.13): rows, one-click dark mode and language that stay in sync with settings, about.</summary>
    private async Task AccountMenuAsync()
    {
        var settings = Page<SettingsViewModel>();
        var menu = main.Shell.AccountMenu;
        var theme = settings.SelectedThemeIndex;
        var language = settings.SelectedLanguageIndex;
        Dispatcher.UIThread.Invoke(() => window.AccountButton.Flyout!.ShowAt(window.AccountButton));
        var open = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.AccountMenuFlyout.IsOpen && window.AccountMenuDark.IsEffectivelyVisible), 3);
        Check("Account menu opens from the sidebar avatar", open);
        var rows = Dispatcher.UIThread.Invoke(() => menu.Accounts.Count);
        var expected = Page<HomeViewModel>().Accounts.Count;
        Check("Account menu lists every account and marks the default", rows == expected
            && Dispatcher.UIThread.Invoke(() => menu.Accounts.Count(a => a.IsDefault)) == (expected > 0 ? 1 : 0), $"{rows}/{expected}");
        Dispatcher.UIThread.Invoke(() => window.AccountMenuDark.IsChecked = true);
        await Task.Delay(300);
        Check("Account menu: dark in one click, settings in sync", Dispatcher.UIThread.Invoke(() =>
            Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark && settings.SelectedThemeIndex == 2));
        await ShotAsync("shell-account-menu");
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The menu is its own popup: render it on its own.
            if (TopLevel.GetTopLevel(window.AccountMenuPanel) is not { } popup || popup.Bounds.Width < 1) return;
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height));
            bitmap.Render(popup);
            bitmap.Save(Path.Combine(reportDir, $"{++_shot:D2}-shell-account-menu-popup.png"));
        });
        Dispatcher.UIThread.Invoke(() => settings.SelectedThemeIndex = 1);
        await Task.Delay(200);
        Check("Account menu follows a change made in settings", Dispatcher.UIThread.Invoke(() => window.AccountMenuLight.IsChecked == true));
        var before = L.Language;
        var target = before == "en" ? "zh-Hans" : "en";
        var index = 1 + L.Languages.Select((l, i) => (l, i)).First(p => p.l.Code == target).i;
        Dispatcher.UIThread.Invoke(() => window.AccountMenuLanguage.SelectedIndex = index);
        var changed = await WaitAsync(() => L.Language == target, 3);
        Check("Account menu: language changes at once, settings in sync", changed && Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex) == index);
        Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex = language);
        L.SetLanguage(before);
        await Task.Delay(200);
        Dispatcher.UIThread.Invoke(() => menu.AboutCommand.Execute(null));
        await Task.Delay(300);
        Check("Account menu: About and privacy opens the about page and closes the menu", Dispatcher.UIThread.Invoke(() =>
            main.CurrentPage is AboutViewModel && !window.AccountMenuFlyout.IsOpen));
        Dispatcher.UIThread.Invoke(() => settings.SelectedThemeIndex = theme);
    }
}
