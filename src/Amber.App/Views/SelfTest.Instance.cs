using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Diagnostics;
using Amber.Core.GameLibrary;
using Amber.Core.Launch;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=instance (UI batch 4b): the instance page's hero, start button states, the "last session" panel
/// and the overview tab, in a throwaway game directory inside the report folder. Start states are simulated with
/// sessions; no game is started and nothing goes online. Screenshots: every start state, three widths, two languages.
/// Also: a theme pack's instances.json layout places the "last session" panel (<see cref="InstanceThemeLayoutAsync"/>).
/// </summary>
internal sealed partial class SelfTest
{
    private async Task InstanceDetailAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "instance-game");
        const string id = "fabric-1.20.1";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        var home = main.Pages.OfType<HomeViewModel>().First();
        var accounts = main.Pages.OfType<AccountsViewModel>().First();
        var removeAccount = false;
        try
        {
            WriteLibraryVersion(game, id, """[{"name":"net.fabricmc:fabric-loader:0.16.9"}]""");
            WriteLibraryVersion(game, "1.21.4", "[]");
            service.Settings.Instances = new()
            {
                [id] = new() { DisplayName = "生存整合包", LastPlayed = DateTimeOffset.Now.AddDays(-1), PlayMinutes = 2160 },
            };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var workDir = service.WorkDirFor(id);
            Directory.CreateDirectory(Path.Combine(workDir, "mods"));
            File.WriteAllBytes(Path.Combine(workDir, "mods", "sodium-fabric-0.5.11.jar"), []);
            Directory.CreateDirectory(Path.Combine(workDir, "config"));
            File.WriteAllLines(Path.Combine(workDir, "config", "iris.properties"), ["enableShaders=true"]);

            // Start without an account so the first state is "sign in to play".
            var hadAccount = service.Accounts.Store.Selected;
            home.SelectedAccount = null;
            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1024;
            window.Height = 720;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().FirstOrDefault();
            Check("实例页：打开 library/{实例} 落在这个实例", instance.Current?.Id == id && page is not null);
            Check("英雄区：名字用游戏库里的名字，事实行有加载器和共玩时长", instance.HeroName == "生存整合包" && instance.HeroLoader.StartsWith("Fabric")
                && instance.HeroFacts.Contains("共玩 36 小时"), $"{instance.HeroName} / {instance.HeroLoader} / {instance.HeroFacts}");
            Check("页签：第一个是概览，进来默认在概览", instance.TabIndex == (int)InstanceTab.Overview);

            if (home.SelectedAccount is null)
            {
                Check("没账号：主按钮是蓝色【登录后开始】，不是琥珀色", instance.Start?.Kind == StartKind.NeedAccount && instance.StartPrimary && !instance.StartAmber
                    && page?.StartButton.Classes.Contains("play") == false && page.StartButton.IsEnabled, instance.StartLabel);
                Check("没账号：英雄区下说“还没有账号”", instance.HasHeroNote && page?.HeroNote.IsEffectivelyVisible == true, instance.HeroNote);
                await ShotAsync("instance-no-account");
            }

            if (hadAccount is null && !service.Accounts.Store.Accounts.Any())
            {
                accounts.OfflineName = "Steve";
                accounts.AddOfflineCommand.Execute(null);
                removeAccount = true;
            }
            home.SelectedAccount = service.Accounts.Store.Selected ?? service.Accounts.Store.Accounts.FirstOrDefault();
            await WaitAsync(() => instance.Start?.Kind == StartKind.Ready, 5);
            Check("就绪：琥珀色【开始游戏】，旁边没有别的按钮", instance.Start?.Kind == StartKind.Ready && page?.StartButton.Classes.Contains("play") == true
                && page.SideButton.IsVisible == false && page.HeroForceKill.IsVisible == false, instance.StartLabel);
            Check("就绪：写了用哪个账号进入", instance.AccountText.Length > 0, instance.AccountText);
            var amber = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
            Check("实例页：这一屏只有一个琥珀色按钮", amber == 1, $"{amber}");

            foreach (var (width, tier, height) in new[] { (1440d, WidthTier.Wide, 200d), (1024d, WidthTier.Medium, 168d), (600d, WidthTier.Compact, 112d) })
            {
                window.Width = width;
                await Task.Delay(450);
                Check($"英雄区 {width}：{tier} 档，至少高 {height}，开始按钮整个在英雄区里", page?.Tier == tier && (page.Hero.MinHeight == height || page.Short)
                    && page.StartButton.TranslatePoint(new Avalonia.Point(0, page.StartButton.Bounds.Height), page.Hero) is { } bottom && bottom.Y <= page.Hero.Bounds.Height,
                    $"{page?.Tier} {page?.Hero.Bounds.Height}");
                await ShotAsync($"instance-ready-{width}");
            }
            Check("紧凑：只留 ⋯，固定和文件夹收起", page?.HeroPin.IsVisible == false && page.HeroMore.IsEffectivelyVisible);
            window.Width = 1024;
            await Task.Delay(300);

            // ---- The launch line: in place under the start button while game files come down ----
            var filesAt = DateTimeOffset.Now.AddSeconds(-4);
            var fetching = home.AddPreparingRunForTest(id);
            home.ReportFilesForTest(100, 480, 10_000_000, filesAt);
            home.ReportFilesForTest(312, 480, 27_600_000, filesAt.AddSeconds(4));
            await WaitAsync(() => instance.ShowLaunchLine, 3);
            await Task.Delay(200);
            var facts = Dispatcher.UIThread.Invoke(() => page?.LaunchLine.Facts ?? "");
            Check("开始游戏：进度行就在开始按钮下面，写步骤、百分比、速度和大约还要多久",
                Dispatcher.UIThread.Invoke(() => page?.LaunchLineCard.IsEffectivelyVisible == true && page.LaunchLine.Step == L.F("home.status.completing_files", 312, 480))
                && facts.Split(" · ") is [var percent, var speed, var left] && percent == "65%" && speed == Fmt.Speed(4_400_000) && left == Fmt.Remaining(home.LaunchRemaining ?? default),
                $"{instance.LaunchStep} | {facts}");
            await ShotAsync("instance-launch-line");
            home.RemoveRunForTest(fetching);
            home.Status = "";
            await Task.Delay(200);
            Check("准备完了进度行收起，不留空位", !instance.ShowLaunchLine && Dispatcher.UIThread.Invoke(() => page?.LaunchLineCard.IsVisible == false));

            // ---- Start states, simulated ----
            var now = DateTimeOffset.Now;
            var session = home.SetSessionForTest(id, new LaunchSession(id, now));
            await WaitAsync(() => instance.Start?.Kind == StartKind.Preparing, 3);
            Check("准备中：按钮还在原位，带进度，旁边单独一个【取消】", instance.StartBusy && page?.StartButton.IsEnabled == true
                && page.SideButton.IsEffectivelyVisible && instance.SideLabel == L.T("common.cancel"), instance.StartLabel);
            await ShotAsync("instance-preparing");
            session.ProcessUp(now);
            await WaitAsync(() => instance.Start?.Kind == StartKind.Entering, 3);
            Check("进入中：旁边是【退出游戏】", instance.Start?.Side == StartSide.QuitGame && page?.SideButton.IsEffectivelyVisible == true, instance.SideLabel);
            await ShotAsync("instance-entering");
            session.EnteredGame(now);
            await WaitAsync(() => instance.Start?.Kind == StartKind.Playing, 3);
            Check("正在玩：不是实心琥珀，是正在玩的颜色", instance.StartPlaying && page?.StartButton.Classes.Contains("playing") == true
                && !page.StartButton.Classes.Contains("play"), instance.StartLabel);
            Check("正在玩：写了玩了几分钟", instance.StartLabel.Contains("分钟"), instance.StartLabel);
            await Task.Delay(200);
            // The quit button beside the start button is shorter than it: its label must still sit in the middle.
            var offCentre = page is null ? ["no page"] : ButtonsWithOffCentreContent(page.Hero);
            Check("Playing: the labels and icons in the header sit in the middle of their buttons", offCentre.Count == 0, string.Join("; ", offCentre));
            await ShotAsync("instance-playing");
            window.Width = 600;
            await Task.Delay(400);
            await ShotAsync("instance-playing-600");
            window.Width = 1024;
            await Task.Delay(300);
            session.ExitRequested(now);
            await WaitAsync(() => instance.Start?.Kind == StartKind.Exiting, 3);
            Check("退出中：先不给强制结束", instance.StartBusy && !instance.HasSide);
            await ShotAsync("instance-exiting");
            session.ProcessEnded(now, 1, crashed: true);
            await WaitAsync(() => instance.Start?.Kind == StartKind.RetryAfterCrash, 3);
            Check("崩溃后：按钮变成【再试一次】", instance.StartLabel == L.T("instance.hero.retry"), instance.StartLabel);

            service.MarkInstallStarted(id);
            await WaitAsync(() => instance.Start?.Kind == StartKind.ResumeInstall, 3);
            Check("没装完：【继续安装并开始】，旁边【只继续安装】", instance.Start?.Side == StartSide.InstallOnly && page?.SideButton.IsEffectivelyVisible == true
                && instance.HasHeroNote, $"{instance.StartLabel} / {instance.SideLabel}");
            await ShotAsync("instance-incomplete");
            service.MarkInstallFinished(id);
            home.SetSessionForTest(id, new LaunchSession(id, now));
            var ended = home.SessionOf(id)!;
            ended.Cancel(now);
            await WaitAsync(() => instance.Start?.Kind == StartKind.Ready, 3);

            // ---- Last session panel ----
            var crash = CrashAnalyzer.Analyze("""
                Exception in thread "main" java.lang.OutOfMemoryError: Java heap space
                Mixin apply for mod sodium failed sodium.mixins.json:core.MixinWindow
                Suspected Mods: Sodium (sodium)
                Failed to compile shader composite1
                """);
            home.ShowCrashForTest("1.21.4", crash);
            await Task.Delay(300);
            Check("上一局：别的实例崩溃时这里不显示", !instance.ShowLastRun && page?.LastRunPanel.IsVisible == false);
            home.ShowCrashForTest(id, crash);
            await WaitAsync(() => instance.ShowLastRun, 3);
            Check("上一局：显示在英雄区下面、页签上面", page?.LastRunPanel.IsEffectivelyVisible == true && instance.LastRunCauses.Count >= 2,
                string.Join(" | ", instance.LastRunCauses.Select(c => c.Title)));
            var fixLabels = instance.LastRunCauses.SelectMany(c => c.Fixes).Select(f => f.Label).ToList();
            Check("上一局：修复按钮挨着原因（调内存、禁用模组、关光影）", fixLabels.Contains(L.T("instance.lastrun.fix.memory"))
                && fixLabels.Any(l => l.Contains("Sodium") || l.Contains("sodium")) && fixLabels.Contains(L.T("instance.lastrun.fix.shaders_off")), string.Join(", ", fixLabels));
            amber = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
            Check("上一局：琥珀色还是只有英雄区那一个", amber == 1, $"{amber}");
            await ShotAsync("instance-lastrun-1024");
            window.Width = 600;
            await Task.Delay(400);
            await ShotAsync("instance-lastrun-600");
            window.Width = 1024;

            var memoryFix = instance.LastRunCauses.SelectMany(c => c.Fixes).First(f => f.Fix.Kind == CrashFixKind.RaiseMemory);
            await instance.ApplyFixCommand.ExecuteAsync(memoryFix);
            var saved = InstanceSettings.Load(service.Directory, id);
            Check("修复：调内存就地写进这个实例的设置，按钮打勾", memoryFix.Done && saved.AutoMemory == false && saved.MaxMemoryMb >= 2048, $"{saved.MaxMemoryMb}");
            var modFix = instance.LastRunCauses.SelectMany(c => c.Fixes).FirstOrDefault(f => f.Fix.Kind == CrashFixKind.DisableMod);
            if (modFix is not null)
            {
                await instance.ApplyFixCommand.ExecuteAsync(modFix);
                Check("修复：禁用模组就地改名为 .disabled", modFix.Done && File.Exists(Path.Combine(workDir, "mods", "sodium-fabric-0.5.11.jar.disabled")));
            }
            var shaderFix = instance.LastRunCauses.SelectMany(c => c.Fixes).First(f => f.Fix.Kind == CrashFixKind.ShadersOff);
            await instance.ApplyFixCommand.ExecuteAsync(shaderFix);
            Check("修复：关光影只改设置，不删光影包", shaderFix.Done && File.ReadAllText(Path.Combine(workDir, "config", "iris.properties")).Contains("enableShaders=false"));
            await ShotAsync("instance-lastrun-fixed");
            instance.DismissLastRunCommand.Execute(null);
            await Task.Delay(200);
            Check("上一局：× 关掉后不再显示", !instance.ShowLastRun && page?.LastRunPanel.IsVisible == false);
            home.Crash = null;

            // ---- A theme pack's instances.json places the last session panel ----
            if (page is null) Check("主题排版（实例页）：找得到实例页", false);
            else await InstanceThemeLayoutAsync(instance, home, page, id, crash);

            // ---- Overview ----
            instance.TabIndex = (int)InstanceTab.Overview;
            await Task.Delay(400);
            Check("概览：这个实例的事实（版本、Java、内存、位置、共玩）", instance.OverviewFacts.Count == 5
                && instance.OverviewFacts[2].Value.Contains("MB"), string.Join(" | ", instance.OverviewFacts.Select(f => $"{f.Label}={f.Value}")));
            Check("概览：新实例每块换成一句教学", !instance.HasRecentWorlds && !instance.HasRecentShots && !instance.HasSavedServers);
            Check("概览：不自己去查服务器（没有进行中的查询）", instance.Servers.Count == 0);
            await ShotAsync("instance-overview-1024");
            window.Width = 1440;
            await Task.Delay(450);
            Check("概览：宽档两栏", page?.OverviewGrid.ColumnDefinitions.Count == 2);
            await ShotAsync("instance-overview-1440");
            window.Width = 1024;
            await Task.Delay(300);
            instance.GoToTabCommand.Execute("Worlds");
            Check("概览：“全部 ›”切到对应页签", instance.TabIndex == (int)InstanceTab.Worlds);

            // ---- Keys ----
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D1, KeyModifiers = KeyModifiers.Alt, Source = window });
            Check("键盘：Alt+1 到概览", instance.TabIndex == (int)InstanceTab.Overview);
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control, Source = window });
            Check("键盘：Ctrl+Tab 到下一个页签", instance.TabIndex == (int)InstanceTab.Mods);

            // ---- English ----
            L.SetLanguage("en");
            instance.TabIndex = (int)InstanceTab.Overview;
            await Task.Delay(500);
            Check("英文：按钮、事实行和概览跟着换", instance.StartLabel == L.T("home.launch") && instance.HeroFacts.Contains("played")
                && instance.OverviewFacts.FirstOrDefault()?.Label == "Version", $"{instance.StartLabel} / {instance.HeroFacts} / {instance.OverviewFacts.FirstOrDefault()?.Label}");
            foreach (var width in new[] { 1440d, 1024d, 600d })
            {
                window.Width = width;
                await Task.Delay(450);
                await ShotAsync($"instance-en-{width}");
            }
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            home.Crash = null;
            if (removeAccount && service.Accounts.Store.Accounts.FirstOrDefault(a => a.Name == "Steve") is { } steve)
            {
                service.Accounts.Store.Remove(steve);
                service.Accounts.Store.Save();
                service.NotifyAccountsChanged();
            }
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
        }
    }

    /// <summary>
    /// A theme pack whose layout/pages/instances.json places the last session panel (instances.lastrun): after a failed
    /// last run the panel is mounted where the theme put it and visible, wide and at 600×420, also with long
    /// (pseudo-localized) text. After restoring the default theme it is back in its own place under the header.
    /// The pack is removed afterwards and the theme the data directory had is selected again. Nothing goes online.
    /// </summary>
    private async Task InstanceThemeLayoutAsync(InstanceViewModel instance, HomeViewModel home, InstancePage page, string id, CrashAnalysis crash)
    {
        const string packId = "selftest-instance-layout";
        var settings = Page<SettingsViewModel>();
        var originalPack = settings.SelectedThemePack?.Id;
        var originalPseudo = L.Pseudo;
        var panel = page.LastRunPanel;
        Rect? RectOf(Control c) => c.TranslatePoint(default, window) is { } p ? new Rect(p, c.Bounds.Size) : null;
        // Mounted in this window, visible, not squeezed to nothing, its left and right edges inside the window and its top on screen.
        bool OnScreen(Control c) => TopLevel.GetTopLevel(c) == window && c.IsEffectivelyVisible && RectOf(c) is { Width: > 0, Height: > 0 } r
            && r.X >= -1 && r.Right <= window.Bounds.Width + 1 && r.Y >= -1 && r.Y < window.Bounds.Height;
        string Placed(Control c) => $"{RectOf(c)?.ToString() ?? "不在窗口里"} 窗口 {window.Bounds.Width:F0}×{window.Bounds.Height:F0}";
        // The fix buttons bind to the page's view model through $parent[UserControl]: they must still find it in the theme's tree.
        List<Button> FixButtons() => panel.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is LastRunFixButton).ToList();
        try
        {
            var path = Path.Combine(reportDir, "instance-layout.jaytheme");
            ThemeSelfTest.WritePack(path, $$"""{ "format": 2, "id": "{{packId}}", "name": "自检实例排版" }""", "{}",
                ("layout/pages/instances.json", """
                {
                  "wide": { "type": "grid", "columns": ["*", "360"], "rows": ["auto", "*", "auto"], "spacing": 12, "children": [
                      { "type": "region", "name": "instances.header", "columnSpan": 2 },
                      { "type": "region", "name": "instances.details", "row": 1 },
                      { "type": "scroll", "row": 1, "column": 1, "children": ["instances.lastrun"] },
                      { "type": "region", "name": "instances.status", "row": 2, "columnSpan": 2 } ] },
                  "compact": { "type": "scroll", "children": [
                      { "type": "stack", "spacing": 8, "children": ["instances.lastrun", "instances.header", "instances.details", "instances.status"] } ] }
                }
                """));
            settings.ImportThemePack(path);
            await Task.Delay(500);
            Check("主题排版（实例页）：带 instances.json 的主题包装上，上一局区块认得、不用 Jay 补", ThemeService.Current?.Id == packId
                && ThemeService.Current.Pages.ContainsKey("instances")
                && !ThemeService.Current.Problems.Any(p => p.Code is "unknown_region" or "required_region_added"),
                string.Join(", ", ThemeService.Current?.Problems.Select(p => $"{p.Code} {p.Detail}") ?? []));

            // A failed last run, as in 崩溃后 above: the session ends crashed, and the analysis is shown for this instance.
            var now = DateTimeOffset.Now;
            var session = home.SetSessionForTest(id, new LaunchSession(id, now));
            session.ProcessUp(now);
            session.EnteredGame(now);
            session.ProcessEnded(now, 1, crashed: true);
            home.ShowCrashForTest(id, crash);
            window.Width = 1600;
            window.Height = 900;
            await WaitAsync(() => instance.Start?.Kind == StartKind.RetryAfterCrash && instance.ShowLastRun, 5);
            await Task.Delay(500);
            Check("主题排版 1600×900：实例页用主题的布局，上一局挂在页面上、看得见", page.ThemeLayout.UsingThemeLayout && instance.ShowLastRun
                && OnScreen(panel) && !page.NoticeScroll.IsVisualAncestorOf(panel), Placed(panel));
            Check("主题排版 1600×900：上一局在主题放的位置（页签右边一栏）",
                RectOf(panel) is { } wide && RectOf(page.DetailsCard) is { } details && wide.X >= details.Right - 1 && wide.Y < details.Bottom,
                $"上一局 {RectOf(panel)} 页签 {RectOf(page.DetailsCard)}");
            var fixes = FixButtons();
            Check("主题排版：上一局的修复按钮还在、还连着这个实例", fixes.Count == instance.LastRunCauses.Sum(c => c.Fixes.Count) && fixes.Count > 0
                && fixes.All(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, instance.ApplyFixCommand)), $"{fixes.Count}");
            Check("主题排版：崩溃后英雄区照样是【再试一次】", instance.StartLabel == L.T("instance.hero.retry") && page.StartButton.IsEffectivelyVisible, instance.StartLabel);
            await ShotAsync("instance-theme-lastrun-1600x900");

            window.Width = 600;
            window.Height = 420;
            await Task.Delay(600);
            Check("主题排版 600×420：换成紧凑的那棵树，上一局挂在页面上、看得见", page.ThemeLayout.UsingThemeLayout && OnScreen(panel), Placed(panel));
            Check("主题排版 600×420：上一局在主题放的位置（英雄区上面）",
                RectOf(panel) is { } compact && RectOf(page.Hero) is { } hero && compact.Bottom <= hero.Y + 1, $"上一局 {RectOf(panel)} 英雄区 {RectOf(page.Hero)}");
            await ShotAsync("instance-theme-lastrun-600x420");

            // Long translations: the pseudo-localization of the layout check (English, every text 40% longer and bracketed).
            L.Pseudo = true;
            L.SetLanguage("en");
            await WaitAsync(() => instance.LastRunTitle.StartsWith('⟦'), 3);
            foreach (var (w, h) in new[] { (600, 420), (1600, 900) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(600);
                var title = panel.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == instance.LastRunTitle);
                var clipped = LayoutCheck.Find(panel, panel.Bounds.Size);
                Check($"主题排版 {w}×{h} 长译文：上一局看得见、标题换了长文字，面板里没有被切掉或叠在一起的内容",
                    OnScreen(panel) && title?.Text?.StartsWith('⟦') == true && clipped.Count == 0,
                    $"{Placed(panel)}；{title?.Text}；{string.Join("；", clipped.Take(5))}");
                await ShotAsync($"instance-theme-lastrun-pseudo-{w}x{h}");
            }
            L.Pseudo = originalPseudo;
            L.SetLanguage("zh-Hans");

            // Back to the default theme: the panel returns to the notice column between the header and the tabs.
            window.Width = 1024;
            window.Height = 720;
            settings.ResetThemePackCommand.Execute(null);
            await Task.Delay(600);
            var notices = page.NoticeScroll.Content as StackPanel;
            Check("恢复默认排版：实例页用回自带布局，上一局回到原来的位置（提示栏里，Bisect 卡片后面）", ThemeService.Current is null
                && !page.ThemeLayout.UsingThemeLayout && notices is not null && ReferenceEquals(panel.Parent, notices)
                && notices.Children.IndexOf(panel) == notices.Children.IndexOf(page.BisectCard) + 1,
                $"{panel.Parent?.GetType().Name} {notices?.Children.IndexOf(panel)}");
            Check("恢复默认排版：上一局看得见，在英雄区下面、页签上面", OnScreen(panel) && RectOf(panel) is { } back && RectOf(page.Hero) is { } top
                && RectOf(page.DetailsCard) is { } tabs && back.Y >= top.Bottom - 1 && back.Y < tabs.Y,
                $"上一局 {RectOf(panel)} 英雄区 {RectOf(page.Hero)} 页签 {RectOf(page.DetailsCard)}");
            fixes = FixButtons();
            Check("恢复默认排版：修复按钮还连着这个实例", fixes.Count > 0 && fixes.All(b => ReferenceEquals(b.Command, instance.ApplyFixCommand)), $"{fixes.Count}");
            await ShotAsync("instance-theme-lastrun-restored");
        }
        finally
        {
            L.Pseudo = originalPseudo;
            L.SetLanguage("zh-Hans");
            home.Crash = null;
            home.SetSessionForTest(id, new LaunchSession(id, DateTimeOffset.Now)).Cancel(DateTimeOffset.Now);
            window.Width = 1024;
            window.Height = 720;
            if (settings.ThemePacks.FirstOrDefault(o => o.Id == packId) is { } mine)
            {
                settings.SelectedThemePack = mine;
                settings.RemoveThemePackCommand.Execute(null);
            }
            settings.SelectedThemePack = settings.ThemePacks.FirstOrDefault(o => o.Id == originalPack) ?? settings.ThemePacks.FirstOrDefault();
            await WaitAsync(() => instance.Start?.Kind == StartKind.Ready, 3);
        }
    }
}
