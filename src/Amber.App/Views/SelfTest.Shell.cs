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
/// 检查隐私行、四个入口、设置和账号任何尺寸都看得见；侧栏只在宽窗口展开；矮窗口顶栏收到 40；
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

            await RailOnMultiplayerAsync(shell);
            await RailFooterWithThemesAsync();
            await MatrixAsync();
            await KeyboardAndHistoryAsync(shell);
            await KeyboardScopeAsync(shell);
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
                Check($"外壳（{tag}）：四个入口都在窗口里", items.Count == 4 && items.All(Inside), $"{items.Count(Inside)}/{items.Count}");
                Check($"外壳（{tag}）：顶栏网络状态看得见，灯旁边有字", Inside(window.NetworkButton) && Inside(window.NetworkLight)
                    && window.NetworkText.IsEffectivelyVisible && window.NetworkText.Text == L.T("shell.network.none"), window.NetworkText.Text ?? "");
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
                        : shorts.Count == 4 && shorts.All(t => t.IsEffectivelyVisible && !t.TextLayout.TextLines.Any(l => l.HasCollapsed)
                            && t.TranslatePoint(new Point(0, 0), t.Parent as Visual ?? t)?.Y > 4),
                        string.Join(",", shorts.Select(t => t.Text)));
                    var privacyText = privacy.GetVisualDescendants().OfType<TextBlock>().First();
                    var text = privacyText.Text ?? "";
                    var breaks = privacyText.TextLayout.TextLines.Skip(1).Select(l => l.FirstTextSourceIndex).ToList();
                    Check($"外壳（{tag}）：图标栏隐私行不从单词中间断行", breaks.All(i => i <= 0 || i >= text.Length
                        || !(char.IsAsciiLetter(text[i - 1]) && char.IsAsciiLetter(text[i]))), $"{text} 断在 {string.Join(",", breaks)}");
                    if (!window.SidebarHost.Classes.Contains("horizontal")) RailFooterCentered(tag, privacy, items);
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

    /// <summary>The reported case: Multiplayer page in a 1280×677 window, sidebar in icon-rail mode.</summary>
    private async Task RailOnMultiplayerAsync(ShellViewModel shell)
    {
        shell.Navigate("play");
        window.Width = 1280;
        window.Height = 677;
        await Task.Delay(600);
        if (!window.SidebarExpanded && !window.SidebarHost.Classes.Contains("horizontal"))
            RailFooterCentered("联机 1280×677", window.GetVisualDescendants().OfType<PrivacyRow>().First(),
                window.NavList.GetVisualDescendants().OfType<ListBoxItem>().ToList());
        await ShotAsync("shell-rail-multiplayer-1280x677");
    }

    /// <summary>Icon rail: the settings gear and the account avatar sit centred under the privacy shield and the nav icons.</summary>
    private void RailFooterCentered(string tag, PrivacyRow privacy, List<ListBoxItem> items)
    {
        double CenterX(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, 0), window)?.X ?? double.NaN;
        var gear = window.SettingsButton.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
        var avatar = window.AccountButton.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Count == 0 && b.CornerRadius.TopLeft > 0 && b.Bounds.Width <= 32 && b.Parent is StackPanel);
        var navIcon = items.Count > 0 ? CenterX(items[0]) : double.NaN;
        var shield = CenterX(privacy);
        var gearX = CenterX(gear);
        var avatarX = CenterX(avatar);
        Check($"外壳（{tag}）：图标栏设置和账号图标与隐私行、入口图标居中对齐",
            Math.Abs(gearX - shield) <= 2 && Math.Abs(avatarX - shield) <= 2 && Math.Abs(gearX - navIcon) <= 2 && Math.Abs(avatarX - navIcon) <= 2,
            $"settings {gearX:0.#}, account {avatarX:0.#}, privacy {shield:0.#}, nav {navIcon:0.#}; "
            + $"row {string.Join("/", window.SettingsButton.GetVisualDescendants().OfType<StackPanel>().Take(1).Select(s => $"{s.Bounds} desired {s.DesiredSize}"))}, "
            + $"label {string.Join("/", window.SettingsButton.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("foot-title")).Select(t => $"visible {t.IsVisible} desired {t.DesiredSize}"))}");
    }

    /// <summary>
    /// The footer stayed flush left under the built-in high-contrast theme: the hidden label kept its old size.
    /// Switch to each built-in theme in the icon rail, check the footer again, then go back to the default look.
    /// </summary>
    private async Task RailFooterWithThemesAsync()
    {
        window.Width = 1024;
        window.Height = 700;
        try
        {
            foreach (var id in Amber.Core.Themes.BuiltInThemes.Ids)
            {
                ThemeService.Apply(Amber.Core.Themes.BuiltInThemes.Load(id));
                await Task.Delay(600);
                RailFooterCentered($"主题 {id}", window.GetVisualDescendants().OfType<PrivacyRow>().First(),
                    window.NavList.GetVisualDescendants().OfType<ListBoxItem>().ToList());
                await ShotAsync($"shell-rail-{id}");
            }
        }
        finally
        {
            ThemeService.Apply(null);
            await Task.Delay(400);
        }
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
        Press(Key.D4, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+4 去朋友", main.CurrentPage is PeopleViewModel && shell.SelectedSection?.Id == "people" && shell.Heading == L.T("shell.nav.people"));
        Press(Key.OemComma, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+, 去设置，侧栏设置按钮是选中状态", main.CurrentPage is SettingsViewModel && window.SettingsButton.Classes.Contains("active") && shell.SelectedSection is null);
        Press(Key.D1, KeyModifiers.Control);
        await Task.Delay(300);
        Check("Ctrl+1 回游戏库", main.CurrentPage is LibraryViewModel && shell.SelectedSection?.Id == "library");

        // Inside the library there are no sub-page tabs: new instance, then the focused instance; back and forward walk
        // the library's own history, and the top bar's breadcrumb leads home.
        shell.Navigate(Amber.Core.Navigation.LegacyRoutes.NewInstance);
        await Task.Delay(300);
        Check("新建实例：顶栏是面包屑“游戏库 › 新建实例”", main.CurrentPage is DownloadViewModel && shell.HasCrumb && shell.Heading == L.T("shell.crumb.new_instance")
            && window.CrumbHome.IsEffectivelyVisible && (window.CrumbHome.Content as string) == L.T("shell.nav.library"), shell.Heading);
        Check("游戏库没有子页签了", window.FocusRegions.All(r => r.Name != "SubPageTabs") && window.FindControl<Control>("SubPageTabs") is null);
        shell.Navigate(Amber.Core.Navigation.LegacyRoutes.CurrentInstance);
        await Task.Delay(300);
        Check("去实例详情", main.CurrentPage is InstanceViewModel && shell.Router.Current.ToString() == "library/current");
        Press(Key.Left, KeyModifiers.Alt);
        await Task.Delay(300);
        Check("Alt+← 返回到新建实例", main.CurrentPage is DownloadViewModel && shell.CanGoForward);
        Press(Key.Right, KeyModifiers.Alt);
        await Task.Delay(300);
        Check("Alt+→ 前进到实例详情", main.CurrentPage is InstanceViewModel && !shell.CanGoForward);
        // 切到发现再切回：回到离开时的位置。
        shell.GoSectionCommand.Execute("discover");
        await Task.Delay(300);
        Check("切到发现时返回按钮不能点（发现还没有历史）", !window.BackButton.IsEnabled);
        shell.GoSectionCommand.Execute("library");
        await Task.Delay(300);
        Check("切回游戏库，回到离开时的实例详情", main.CurrentPage is InstanceViewModel);
        window.BackButton.Command?.Execute(null);
        await Task.Delay(300);
        Check("顶栏 ← 按钮返回", main.CurrentPage is DownloadViewModel);
        window.CrumbHome.Command?.Execute(null);
        await Task.Delay(300);
        Check("点面包屑里的“游戏库”回游戏库主页，面包屑收起", main.CurrentPage is LibraryViewModel && !shell.HasCrumb && !window.CrumbHome.IsVisible
            && shell.Heading == L.T("shell.nav.library"));
        shell.Navigate(Amber.Core.Navigation.LegacyRoutes.NewInstance);
        await Task.Delay(300);
        // 已经在游戏库时再点游戏库：回游戏库首页。
        shell.GoSectionCommand.Execute("library");
        await Task.Delay(300);
        Check("再点一次当前入口回到入口首页", main.CurrentPage is LibraryViewModel);
        await ShotAsync("shell-history");
    }

    /// <summary>
    /// Everything the nine old pages held is still reachable (§11.9). The launch page, the standalone log page and the
    /// toolbox have no page of their own any more: their ids and their saved routes lead to where each thing lives now.
    /// </summary>
    private async Task ReachEveryOldPageAsync(ShellViewModel shell)
    {
        var reached = new HashSet<Type>();
        async Task Visit(Action go)
        {
            go();
            await Task.Delay(250);
            reached.Add(main.CurrentPage.GetType());
        }
        var expected = new Dictionary<string, Type>
        {
            ["home"] = typeof(LibraryViewModel), ["instances"] = typeof(InstanceViewModel), ["downloads"] = typeof(DownloadViewModel),
            ["log"] = typeof(InstanceViewModel), ["content"] = typeof(ContentViewModel), ["multiplayer"] = typeof(MultiplayerViewModel),
            ["tools"] = typeof(SettingsViewModel), ["settings"] = typeof(SettingsViewModel), ["accounts"] = typeof(AccountsViewModel),
        };
        var wrong = new List<string>();
        foreach (var id in Amber.Core.Themes.ThemeLayout.PageIds)
        {
            var route = Amber.Core.Navigation.LegacyRoutes.For(id);
            await Visit(() => shell.Navigate(route?.ToString() ?? "library"));
            if (route is null || !expected.TryGetValue(id, out var type) || main.CurrentPage.GetType() != type) wrong.Add($"{id}→{main.CurrentPage.GetType().Name}");
        }
        Check("旧版九页的东西都还走得到（启动页、日志页、工具箱并到了新位置）", wrong.Count == 0 && expected.Count == Amber.Core.Themes.ThemeLayout.PageIds.Length, string.Join("、", wrong));

        // Routes saved by the previous shell (help links, tips, history).
        shell.Navigate("library/start");
        await Task.Delay(250);
        Check("旧路由 library/start 落在游戏库主页", main.CurrentPage is LibraryViewModel && shell.Router.Current.ToString() == "library", shell.Router.Current.ToString());
        shell.Navigate("library/logs");
        await Task.Delay(250);
        Check("旧路由 library/logs 落在实例的日志页签", main.CurrentPage is InstanceViewModel && shell.Router.Current.ToString() == Amber.Core.Navigation.LegacyRoutes.Logs,
            shell.Router.Current.ToString());
        shell.Navigate("settings/tools");
        var junk = await WaitAsync(() => window.RevealedField?.Name == "JunkBox", 3);
        Check("旧路由 settings/tools 落在设置 › 存储的清理", junk && main.CurrentPage is SettingsViewModel { SelectedGroupId: Amber.Core.Navigation.SettingsCatalog.Storage },
            $"{shell.Router.Current} {window.RevealedField?.Name}");

        await Visit(() => window.AccountButton.Command?.Execute(null));
        await Visit(() => main.Shell.Navigate("people"));
        await Visit(() => Page<SettingsViewModel>().GoAboutCommand.Execute(null));
        await Visit(() => main.Shell.Navigate("help"));
        var missing = main.Pages.Where(p => p.HasView).Select(p => p.GetType()).Where(t => !reached.Contains(t)).Select(t => t.Name).ToList();
        Check("有界面的每一页都能从新外壳走到", missing.Count == 0 && reached.Count == 10, missing.Count == 0 ? $"{reached.Count} 页" : "走不到：" + string.Join("、", missing));
        await MePageAsync(shell);
        await PeoplePageAsync(shell);
        await NetworkPanelAsync(shell);
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
        await ShotAsync("shell-expanded");
        window.Width = 760;
        await Task.Delay(400);
        Check("总是展开但展开后内容区不到 640：仍是图标栏", !window.SidebarExpanded);
        // Expanded → rail at runtime: the footer buttons must re-centre, not keep the expanded layout.
        RailFooterCentered("展开后收成图标栏", window.GetVisualDescendants().OfType<PrivacyRow>().First(),
            window.NavList.GetVisualDescendants().OfType<ListBoxItem>().ToList());
        await ShotAsync("shell-rail-after-expanded");
        settings.ResetFieldCommand.Execute("sidebar");
        await Task.Delay(300);
        Check("恢复默认：自动", settings.SelectedSidebarIndex == 0 && !File.ReadAllText(AppSettings.FilePath).Contains("\"SidebarMode\": \"expanded\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Me" behind the sidebar avatar: game accounts, Jay account, my friend code, my devices, and light/dark and the
    /// language (they used to be in the account menu) in sync with the settings.
    /// </summary>
    private async Task MePageAsync(ShellViewModel shell)
    {
        var settings = Page<SettingsViewModel>();
        var mp = Page<MultiplayerViewModel>();
        var theme = settings.SelectedThemeIndex;
        var language = settings.SelectedLanguageIndex;
        shell.Navigate("library");
        await Task.Delay(200);
        Dispatcher.UIThread.Invoke(() => window.AccountButton.Command?.Execute(null));
        await Task.Delay(300);
        Check("点头像打开“我”页，头像是选中状态，没有弹出菜单", main.CurrentPage is AccountsViewModel && shell.Router.Current.ToString() == "me"
            && shell.Heading == L.T("me.title") && window.AccountButton.Classes.Contains("active") && window.AccountButton.Flyout is null, shell.Router.Current.ToString());
        shell.Navigate("accounts");
        await Task.Delay(250);
        Check("旧路由 accounts 落在“我”页", main.CurrentPage is AccountsViewModel && shell.Router.Current.ToString() == "me", shell.Router.Current.ToString());
        var page = await FindAsync<AccountsPage>(_ => true);
        if (page is null)
        {
            Check("“我”页显示出来", false);
            return;
        }
        Check("“我”页有游戏账号、Jay 账户、好友码、设备、外观五块", Dispatcher.UIThread.Invoke(() => new Control[] { page.GameAccountsCard, page.JayCard, page.FriendCodeCard, page.DevicesCard, page.AppearanceCard }
            .All(c => c.IsVisible) && page.ThemeLayout.Regions.Keys.Order().SequenceEqual(new[] { "me.accounts", "me.add", "me.appearance", "me.code", "me.devices", "me.jay", "me.status", "me.title" })),
            string.Join(",", page.ThemeLayout.Regions.Keys.Order()));
        var code = Dispatcher.UIThread.Invoke(() => page.MyFriendCodeText.Text ?? "");
        Check("我的好友码写出来，和联机设置里的是同一个", code.StartsWith("JAYC1-", StringComparison.Ordinal) && code == mp.MyContactCode, code.Length > 12 ? code[..12] : code);
        Check("好友码里没有 IP 地址", !System.Text.RegularExpressions.Regex.IsMatch(code, @"\d{1,3}(\.\d{1,3}){3}"));
        Check("我的设备：这台电脑和它的安全码；没有 Jay 账户时写明换电脑要重新加", Dispatcher.UIThread.Invoke(() =>
            (page.ThisDeviceCodeText.Text ?? "") == mp.MyFingerprint && mp.MyFingerprint.Length > 0
            && page.DevicesNote.Text == L.T(Page<AccountsViewModel>().HasJayAccounts ? "me.devices.with_jay" : "me.devices.no_jay")
            && page.DevicesCreateJay.IsVisible == !Page<AccountsViewModel>().HasJayAccounts), page.DevicesNote.Text ?? "");
        Dispatcher.UIThread.Invoke(() => page.AppearanceCard.BringIntoView());
        await Task.Delay(200);
        Dispatcher.UIThread.Invoke(() => page.MeThemeDark.IsChecked = true);
        await Task.Delay(300);
        Check("“我”页：点一下换深色，设置里同步", Dispatcher.UIThread.Invoke(() =>
            Application.Current!.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark && settings.SelectedThemeIndex == 2));
        await ShotAsync("shell-me-dark");
        Dispatcher.UIThread.Invoke(() => settings.SelectedThemeIndex = 1);
        await Task.Delay(200);
        Check("“我”页跟着设置里的改动", Dispatcher.UIThread.Invoke(() => page.MeThemeLight.IsChecked == true));
        var before = L.Language;
        var target = before == "en" ? "zh-Hans" : "en";
        var index = 1 + L.Languages.Select((l, i) => (l, i)).First(p => p.l.Code == target).i;
        Dispatcher.UIThread.Invoke(() => page.MeLanguage.SelectedIndex = index);
        var changed = await WaitAsync(() => L.Language == target, 3);
        Check("“我”页：语言立刻换，设置里同步", changed && Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex) == index);
        Dispatcher.UIThread.Invoke(() => settings.SelectedLanguageIndex = language);
        L.SetLanguage(before);
        Dispatcher.UIThread.Invoke(() => settings.SelectedThemeIndex = theme);
        await Task.Delay(300);
        foreach (var (w, h) in new (double, double)[] { (600, 420), (1024, 700), (1440, 900) })
        {
            window.Width = w;
            window.Height = h;
            await Task.Delay(450);
            Dispatcher.UIThread.Invoke(() => page.GameAccountsCard.BringIntoView());
            await Task.Delay(150);
            await ShotAsync($"shell-me-{w}x{h}");
        }
        window.Width = 1024;
        window.Height = 700;
        await Task.Delay(400);

        // The safety code, "reset identity" and device verification moved here from the lobby's settings tab (batch 7).
        Check("我的设备：安全码、【重置联机身份】、设备认证都在这页", Dispatcher.UIThread.Invoke(() =>
            page.IdentityCard.IsEffectivelyVisible && page.ResetIdentityButton.IsEffectivelyVisible && page.DeviceCheckCard.IsEffectivelyVisible));
        shell.Navigate("play/settings#IdentityCard");
        var identity = await WaitAsync(() => window.RevealedField?.Name == "IdentityCard", 3);
        Check("旧路由 play/settings#IdentityCard 去“我”页的我的设备", identity && main.CurrentPage is AccountsViewModel, $"{shell.Router.Current} {window.RevealedField?.Name}");
        // Leaving a page drops its view: look the page up again after every return.
        async Task<AccountsPage> BackToMeAsync()
        {
            shell.Navigate("me");
            await Task.Delay(250);
            return await FindAsync<AccountsPage>(p => p.IsEffectivelyVisible) ?? page;
        }
        page = await BackToMeAsync();
        Dispatcher.UIThread.Invoke(() => page.MeThemesButton.Command?.Execute(null));
        var packs = await WaitAsync(() => window.RevealedField?.Name == "ThemePackBox", 3);
        Check("【主题包】去设置 › 外观的主题包", packs && main.CurrentPage is SettingsViewModel, $"{shell.Router.Current} {window.RevealedField?.Name}");
        page = await BackToMeAsync();
        Dispatcher.UIThread.Invoke(() => page.MeAboutButton.Command?.Execute(null));
        await Task.Delay(300);
        Check("【关于和隐私】打开关于页", main.CurrentPage is AboutViewModel);
        page = await BackToMeAsync();
        Dispatcher.UIThread.Invoke(() => page.OpenPeopleButton.Command?.Execute(null));
        await Task.Delay(300);
        Check("【添加和管理朋友】去朋友页", main.CurrentPage is PeopleViewModel && shell.Router.Current.ToString() == "people");
    }

    /// <summary>
    /// "Friends", the fourth entry, as far as the shell goes: the entry, the empty state, one row per person, the
    /// conversation at three widths in two languages, the <c>people/add</c> route and what "for a child" leaves of it.
    /// The page itself (adding, the emoji check, people I've met, the conversation) is the <c>friends</c> scope.
    /// </summary>
    private async Task PeoplePageAsync(ShellViewModel shell)
    {
        var mp = Page<MultiplayerViewModel>();
        var people = Page<PeopleViewModel>();
        var settings = Page<SettingsViewModel>();
        var dir = Path.Combine(reportDir, "shell-friends");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.Invoke(() => mp.ContactsDir = dir);
        shell.Navigate("people");
        await Task.Delay(300);
        var page = await FindAsync<PeoplePage>(_ => true);
        if (page is null)
        {
            Check("朋友页显示出来", false);
            return;
        }
        Check("朋友页：侧栏第四个入口选中", shell.SelectedSection?.Id == "people" && shell.Sections.Select(x => x.Id).SequenceEqual(new[] { "library", "discover", "multiplayer", "people" }),
            string.Join(",", shell.Sections.Select(x => x.Id)));
        Check("朋友页：还没有朋友时给【复制我的好友码】和【贴上对方的好友码】，没有空列表", Dispatcher.UIThread.Invoke(() =>
            page.PeopleEmpty.IsEffectivelyVisible && page.EmptyCopyButton.IsEffectivelyVisible && page.EmptyAddButton.IsEffectivelyVisible && !page.FriendListCard.IsVisible));
        Check("朋友页：没连能送信的网络时在顶上写明，并给【连接网络】和【加朋友】", Dispatcher.UIThread.Invoke(() =>
            page.DeliveryText.Text == L.T("people.delivery.none") && page.ConnectNetworkButton.IsEffectivelyVisible && page.AddFriendButton.IsEffectivelyVisible));
        await ShotAsync("shell-people-empty");

        byte[] Key(byte n)
        {
            var k = new byte[32];
            k[0] = n;
            return k;
        }
        using var bob = Amber.Core.Identity.JayAccount.FromMnemonic(Amber.Core.Identity.Bip39.Generate(Amber.Core.Identity.MnemonicLanguage.English), null);
        Dispatcher.UIThread.Invoke(() => mp.Contacts.Import([
            new Amber.Core.Multiplayer.Contacts.Contact(Key(1), "Bob", true, DateTimeOffset.UtcNow.AddDays(-3)) { JayAccount = bob.Address },
            new Amber.Core.Multiplayer.Contacts.Contact(Key(2), "Bob", true, DateTimeOffset.UtcNow) { JayAccount = bob.Address },
            new Amber.Core.Multiplayer.Contacts.Contact(Key(3), "Sam", true, DateTimeOffset.UtcNow),
        ]));
        var listed = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => page.FriendListCard.IsEffectivelyVisible && people.Friends.Count == 2), 3);
        Check("朋友页：一个人一行（同一 Jay 账户的两台设备算一个人）", listed && Dispatcher.UIThread.Invoke(() =>
            !page.PeopleEmpty.IsEffectivelyVisible && page.PeoplePickHint.IsEffectivelyVisible), string.Join(",", people.Friends.Select(f => f.Name)));
        Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Bob"));
        await Task.Delay(300);
        Check("朋友页：选中后右边是和 TA 的对话，顶上常驻“端到端加密 · 已核对”", Dispatcher.UIThread.Invoke(() =>
            page.FriendDetail.IsEffectivelyVisible && page.FriendName.Text == "Bob" && people.Selected is { HasJay: true, DeviceItems.Count: 2 }
            && page.SafetyChipText.Text == L.T("people.safety.verified") && !page.PeoplePickHint.IsVisible));
        // The multiplayer page rebuilds its list on every change: the selection stays on the same person.
        Dispatcher.UIThread.Invoke(mp.RefreshContacts);
        await Task.Delay(200);
        Check("朋友页：列表刷新后还选着同一个人", Dispatcher.UIThread.Invoke(() => people.Selected?.Name == "Bob" && page.FriendDetail.IsEffectivelyVisible));
        foreach (var lang in new[] { "zh-Hans", "en" })
        {
            L.SetLanguage(lang);
            foreach (var (w, h) in new (double, double)[] { (600, 420), (1024, 700), (1440, 900) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(450);
                var bounds = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
                bool Inside(Control c) => c.IsEffectivelyVisible && c.TranslatePoint(new Point(0, 0), window) is { } p
                    && bounds.Contains(new Rect(p, c.Bounds.Size).Deflate(0.5));
                Check($"朋友页（{lang} {w}×{h}）：顶上一行和对话都在窗口里，窄窗口只显示选中的人", Dispatcher.UIThread.Invoke(() =>
                    Inside(page.FriendDetailCard) && Inside(page.PeopleStatusCard)
                    && (w < 700 ? !page.FriendListCard.IsVisible && page.BackToListButton.IsEffectivelyVisible
                        : Inside(page.FriendListCard) && page.FriendDetailCard.Bounds.Left >= page.FriendListCard.Bounds.Right - 1)),
                    $"list {page.FriendListCard.Bounds} detail {page.FriendDetailCard.Bounds}");
                await ShotAsync($"shell-people-{lang}-{w}x{h}");
            }
        }
        L.SetLanguage("zh-Hans");
        window.Width = 1024;
        window.Height = 700;
        await Task.Delay(400);

        shell.Navigate("people/add");
        await Task.Delay(300);
        Check("people/add 打开朋友页的加朋友面板", main.CurrentPage is PeopleViewModel && people.IsAdding && Dispatcher.UIThread.Invoke(() => page.AddFriendCard.IsEffectivelyVisible),
            shell.Router.Current.ToString());
        Dispatcher.UIThread.Invoke(() => people.IsAdding = false);

        // "For a child": no public networks, so no friends through them. The page says so instead of showing a dead list.
        var child = settings.ChildMode;
        try
        {
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = true);
            shell.Navigate("people");
            await Task.Delay(400);
            page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible) ?? page;
            Check("“给孩子用”打开后：朋友页只留一句说明", Dispatcher.UIThread.Invoke(() =>
                page.PeopleChildNote.IsEffectivelyVisible && !page.FriendListCard.IsVisible && !page.FriendDetail.IsVisible && !page.PeopleStatusCard.IsVisible && people.Badge == 0));
            await ShotAsync("shell-people-child");
            shell.Navigate("me");
            await Task.Delay(300);
            var me = await FindAsync<AccountsPage>(_ => true);
            Check("“给孩子用”打开后：“我”页不写好友码", me is not null && Dispatcher.UIThread.Invoke(() =>
                !me.MyFriendCodeText.IsEffectivelyVisible && me.FriendCodeChildNote.IsVisible));
            Check("“给孩子用”打开后：网络面板只有基础中转，不能添加网络", Dispatcher.UIThread.Invoke(() =>
                shell.Network.Rows.Count == 1 && shell.Network.Rows[0].IsBaseRelays && shell.Network.IsChildMode && !shell.Network.CanAddNetworks));
        }
        finally
        {
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = child);
            await Task.Delay(200);
        }
    }

    /// <summary>
    /// The network status in the top bar and its panel: the basic relays as one built-in network (invites only), each
    /// public network with whether it is reviewed, what it can do and a switch. Nothing connects in this check.
    /// </summary>
    private async Task NetworkPanelAsync(ShellViewModel shell)
    {
        var mp = Page<MultiplayerViewModel>();
        var network = shell.Network;
        shell.Navigate("library");
        await Task.Delay(200);
        Dispatcher.UIThread.Invoke(network.Refresh);
        Check("顶栏网络状态：没开房间、没连网络时写“未连接”，灯是灭的", Dispatcher.UIThread.Invoke(() =>
            window.NetworkText.Text == L.T("shell.network.none") && window.NetworkLight.Classes.Contains("off") && !window.NetworkLight.Classes.Contains("online")),
            window.NetworkText.Text ?? "");
        Dispatcher.UIThread.Invoke(() => window.NetworkButton.Flyout!.ShowAt(window.NetworkButton));
        var open = await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.NetworkFlyout.IsOpen && window.NetworkSettings.IsEffectivelyVisible), 3);
        Check("点网络状态打开网络面板", open);
        Check("网络面板：没有公共网络时只有“基础中转”（内置、只管邀请、没有开关），并写明邀请照常能用", Dispatcher.UIThread.Invoke(() =>
            network.Rows.Count == 1 && network.Rows[0] is { IsBaseRelays: true, CanSwitch: false, IsOff: true } relays
            && relays.Name == L.T("shell.network.relays") && relays.TrustText == L.T("shell.network.trust.builtin")
            && relays.CanText == L.T("shell.network.can.invites") && window.NetworkEmptyNote.IsEffectivelyVisible
            && window.NetworkAdd.IsEffectivelyVisible), string.Join(" | ", network.Rows.Select(r => $"{r.Name}:{r.CanText}")));
        await PopupShotAsync(window.NetworkPanel, "shell-network-panel-empty");

        // A network signed by someone else (not reviewed): listed with that label, everything it can do, and a switch.
        var someone = Amber.Core.Multiplayer.Public.TrustSigner.Generate(out _);
        var info = new Amber.Core.Multiplayer.Public.NetworkInfo("selftest-shell", "自检网络", "jay-selftest-shell", "shell-secret", ["tcp://shell-node.example.invalid:11010"]);
        Dispatcher.UIThread.Invoke(() => mp.AddNetworkText(Amber.Core.Multiplayer.Public.NetworkString.Create(info, someone), true));
        await Task.Delay(300);
        try
        {
            var row = Dispatcher.UIThread.Invoke(() => network.Rows.FirstOrDefault(r => r.Id == "selftest-shell"));
            Check("网络面板：加了一个没审核的网络后多一行，写着未审核、能做的三件事，有开关", row is { CanSwitch: true, IsOff: true, IsUnreviewed: true }
                && row.TrustText == L.T("shell.network.trust.unreviewed")
                && row.CanText == string.Join(L.T("common.list_separator"), L.T("shell.network.can.invites"), L.T("shell.network.can.public_rooms"), L.T("shell.network.can.messages"))
                && Dispatcher.UIThread.Invoke(() => network.Rows.Count == 2 && !network.ShowEmptyNote), string.Join(" | ", network.Rows.Select(r => $"{r.Name}:{r.TrustText}:{r.CanText}")));
            var switches = Dispatcher.UIThread.Invoke(() => window.NetworkPanel.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.IsEffectivelyVisible).ToList());
            Check("网络面板：只有公共网络那一行有开关，开关有读屏名字", switches.Count == 1
                && Dispatcher.UIThread.Invoke(() => Avalonia.Automation.AutomationProperties.GetName(switches[0])) == L.F("shell.network.switch", "自检网络"), $"{switches.Count}");
            await PopupShotAsync(window.NetworkPanel, "shell-network-panel");
            // Turning it on needs the multiplayer component or the public network notice first: Jay goes to Public rooms,
            // where that is asked, and the switch stays off. Nothing is connected.
            Dispatcher.UIThread.Invoke(() => { if (switches.Count == 1) switches[0].IsChecked = true; });
            await Task.Delay(500);
            Check("网络面板：打开开关但还缺东西时，去公开房间页问，面板收起，开关回到关", Dispatcher.UIThread.Invoke(() =>
                main.CurrentPage is MultiplayerViewModel { Tab: MultiplayerTab.Public } && !window.NetworkFlyout.IsOpen
                && !mp.IsPublicConnected && !mp.IsPublicConnecting && row is { IsOff: true, Wanted: false } && mp.SelectedNetworkChoice?.Id == "selftest-shell"),
                $"{shell.Router.Current} connecting={mp.IsPublicConnecting}");
            Dispatcher.UIThread.Invoke(() => mp.PublicNoticeOpen = false);

            // A room is open: the basic relays are in use, and the top bar says so on every page.
            shell.Navigate("library");
            Dispatcher.UIThread.Invoke(() => mp.Mode = "host");
            await Task.Delay(300);
            Check("开着房间时：顶栏写“基础中转”，灯亮，面板里写这个房间正在用", Dispatcher.UIThread.Invoke(() =>
                window.NetworkText.Text == L.T("shell.network.relays") && window.NetworkLight.Classes.Contains("online")
                && network.Rows[0] is { IsOn: true } r && r.Detail == L.T("shell.network.relays.in_use")), window.NetworkText.Text ?? "");
            await ShotAsync("shell-network-in-room");
            Dispatcher.UIThread.Invoke(() => mp.Mode = "");
            await Task.Delay(200);
            Check("房间结束后回到“未连接”", Dispatcher.UIThread.Invoke(() => window.NetworkText.Text == L.T("shell.network.none") && network.Rows[0].IsOff));

            Dispatcher.UIThread.Invoke(() => window.NetworkButton.Flyout!.ShowAt(window.NetworkButton));
            await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.NetworkFlyout.IsOpen && window.NetworkAdd.IsEffectivelyVisible), 3);
            Dispatcher.UIThread.Invoke(() => window.NetworkAdd.Command?.Execute(null));
            var card = await WaitAsync(() => window.RevealedField?.Name == "NetworksCard", 3);
            Check("【添加网络…】去设置 › 网络的公共网络卡，面板收起", card && Dispatcher.UIThread.Invoke(() =>
                main.CurrentPage is SettingsViewModel { SelectedGroupId: Amber.Core.Navigation.SettingsCatalog.Network } && !window.NetworkFlyout.IsOpen), $"{shell.Router.Current} {window.RevealedField?.Name}");
            Dispatcher.UIThread.Invoke(() => network.OpenSettingsCommand.Execute(null));
            await Task.Delay(250);
            Check("【网络设置】去设置 › 网络", shell.Router.Current.ToString() == Amber.Core.Navigation.LegacyRoutes.NetworkSettings
                && main.CurrentPage is SettingsViewModel { SelectedGroupId: Amber.Core.Navigation.SettingsCatalog.Network }, shell.Router.Current.ToString());
        }
        finally
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                mp.Mode = "";
                mp.SelectedNetworkChoice = mp.NetworkChoices.FirstOrDefault(c => c.Id == "selftest-shell") ?? mp.SelectedNetworkChoice;
                if (mp.CanRemoveNetwork) mp.RemoveNetworkCommand.Execute(null);
            });
            await Task.Delay(200);
            Check("删掉这个网络后面板回到只有基础中转", Dispatcher.UIThread.Invoke(() => network.Rows.Count == 1 && mp.NetworkChoices.Count == 1));
            shell.Navigate("library");
        }
    }

    /// <summary>A flyout is its own popup: render it on its own.</summary>
    private async Task PopupShotAsync(Control inside, string name) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (TopLevel.GetTopLevel(inside) is not { } popup || popup.Bounds.Width < 1) return;
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height));
        bitmap.Render(popup);
        bitmap.Save(Path.Combine(reportDir, $"{++_shot:D2}-{name}.png"));
    });
}
