using System.Diagnostics;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Diagnostics;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>隔离数据目录内的烘焙警告布局/交互自检；不启动游戏，不访问下载服务。</summary>
internal sealed class BakeNoticeSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];
    private BakeNoticeHost Host => window.GetVisualDescendants().OfType<BakeNoticeHost>().Single();
    private BakeNoticeState State => Toasts.BakeNotices!;
    private HomeViewModel Home => main.Pages.OfType<HomeViewModel>().Single();
    // Launch notices show on the instance's own page (the separate launch page is gone).
    private InstancePage Page => window.GetVisualDescendants().OfType<InstancePage>().Single();
    private StatusBar LaunchStatus => Page.FindControl<StatusBar>("LaunchStatus")!;
    private Border NoticeCard => Page.FindControl<Border>("BakeNoticeCard")!;
    private const string Instance = "E2E-Bake-Sodium";

    /// <summary>The test profile had no such instance, so a bare stand-in was written: enough for the notices, not for baking.</summary>
    private bool _standIn;

    private async Task GoInstanceAsync()
    {
        main.Shell.Navigate("library/" + Instance);
        var page = main.Pages.OfType<InstanceViewModel>().Single();
        for (var i = 0; i < 60 && !(page.IsCurrent && page.Current?.Id == Instance); i++) await Task.Delay(50);
        await Task.Delay(100);
        window.UpdateLayout();
    }

    private async Task GoElsewhereAsync()
    {
        main.Shell.Navigate("settings");
        await Task.Delay(150);
    }

    private void ShowCrash()
    {
        Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
        Home.CrashInstance = Instance;
    }

    private void Check(string name, bool ok, string detail = "")
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        if (!ok) throw new InvalidOperationException(name);
    }

    /// <summary>The bake-related fields of the saved settings file (ExperimentalBake, BakeLoaderPath, …) as one string.</summary>
    private static string BakeSettingsSnapshot()
    {
        if (!File.Exists(AppSettings.FilePath)) return "";
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppSettings.FilePath));
        return string.Join(";", doc.RootElement.EnumerateObject()
            .Where(p => p.Name.Contains("Bake", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
    }

    private void Show(string id, Action? retry = null) => Toasts.BakeFailed(L.F("home.bake.crashed_toast", id), retry ?? (() => { }));

    private static Rect BoundsIn(Control control, Visual target) =>
        new(control.TranslatePoint(default, target) ?? default, control.Bounds.Size);

    private async Task ShotAsync(string name)
    {
        await Task.Delay(300);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
    }

    private async Task ClearAsync()
    {
        State.Clear();
        await Task.Delay(100);
        window.UpdateLayout();
    }

    private async Task CheckLayoutAsync(string name, bool multiple = false)
    {
        // The notice's template can arrive after the collection changed: wait for the real control, not a fixed delay.
        for (var i = 0; i < 40; i++)
        {
            window.UpdateLayout();
            if (State.HasNotice && Host.IsEffectivelyVisible && Host.Bounds.Height > 0
                && Host.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == L.T("home.bake.retry_without"))) break;
            await Task.Delay(50);
        }
        Check(name + "：警告已在实例页呈现", Host.IsEffectivelyVisible && State.HasNotice && Host.Bounds.Height > 0);
        var page = Page;
        var client = new Rect(window.ClientSize);
        var pageScroll = page.FindControl<ScrollViewer>("PageScroll")!;
        var notices = page.FindControl<ScrollViewer>("NoticeScroll")!;
        pageScroll.Offset = default;
        notices.Offset = default;
        window.UpdateLayout();
        await Task.Delay(50);
        var start = page.FindControl<Button>("StartButton")!;
        var startBounds = BoundsIn(start, window);
        var card = NoticeCard;
        var cardBounds = BoundsIn(card, window);
        Check(name + "：开始按钮不被遮挡且仍可操作", client.Contains(startBounds) && !cardBounds.Intersects(startBounds) && start.IsEffectivelyEnabled,
            $"start {startBounds}, notice {cardBounds}");
        Check(name + "：通知区最多占半页", notices.Bounds.Height <= Math.Max(160, page.Bounds.Height * 0.5) + 1,
            $"notices {notices.Bounds.Height:F0}, page {page.Bounds.Height:F0}");
        card.BringIntoView();
        window.UpdateLayout();
        await Task.Delay(100);
        cardBounds = BoundsIn(card, window);
        Check(name + "：提示完整位于通知区和窗口内", client.Contains(cardBounds) && BoundsIn(notices, window).Inflate(1).Contains(cardBounds),
            $"notice {cardBounds}, area {BoundsIn(notices, window)}");
        var retry = Host.GetVisualDescendants().OfType<Button>().First(b => b.Command is not null && b.Content?.ToString() == L.T("home.bake.retry_without"));
        Check(name + "：紧凑重试按钮完整可见", cardBounds.Contains(BoundsIn(retry, window)));
        var description = Host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == State.Current!.Text);
        Check(name + "：长说明最多两行且完整内容保留在提示框", description.Bounds.Height <= 36 && ToolTip.GetTip(description)?.ToString() == State.Current!.Text);
        // The last-session panel sits under the notice in the same scrolling area: its actions must stay reachable.
        var log = page.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == L.T("home.crash.view_log"));
        for (var i = 0; i < 5; i++)
        {
            log.BringIntoView();
            window.UpdateLayout();
            await Task.Delay(100);
            if (client.Contains(BoundsIn(log, window))) break;
        }
        var logBounds = BoundsIn(log, window);
        Check(name + "：滚动后崩溃操作可达", log.IsEffectivelyVisible && client.Contains(logBounds),
            $"log {logBounds}, window {client}");
        if (multiple) Check(name + "：三条通知只呈现最新一条", State.Notices.Count == 3 && retry.Command == State.Current!.RetryCommand
            && Host.GetVisualDescendants().OfType<Button>().Count() == 1);
        await ShotAsync(name);
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("AMBER_DATA_DIR");
            Check("自检使用显式独立数据/游戏目录", !string.IsNullOrWhiteSpace(dataDir)
                && Path.GetFullPath(Home.Service.Directory.Root).StartsWith(Path.GetFullPath(dataDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var versionJson = Path.Combine(Home.Service.Directory.Root, "versions", Instance, Instance + ".json");
            if (!File.Exists(versionJson))
            {
                _standIn = true;
                Directory.CreateDirectory(Path.GetDirectoryName(versionJson)!);
                File.WriteAllText(versionJson, $$"""{"id":"{{Instance}}","type":"release","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
                Home.Service.SaveAndReload();
            }
            await GoInstanceAsync();
            Check("启动提示所在的实例页已打开", main.Pages.OfType<InstanceViewModel>().Single().Current?.Id == Instance);
            var settingsBefore = BakeSettingsSnapshot();
            const string instance = Instance;
            var original = new LaunchRequest(instance, JoinServer: "localhost:25565", JoinWorld: null);
            var request = original with { SkipBake = true };
            var calls = new List<LaunchRequest>();
            foreach (var theme in new[] { "dark", "light" })
            {
                App.ApplyTheme(theme);
                foreach (var size in new[] { (1280, 700), (760, 440) })
                {
                    L.SetLanguage("zh-Hans");
                    window.Width = size.Item1;
                    window.Height = size.Item2;
                    ShowCrash();
                    await ClearAsync();
                    Show(instance, () => calls.Add(request));
                    await CheckLayoutAsync($"{theme}-{size.Item1}x{size.Item2}");
                    Check("截图尺寸与文件名一致", window.Bounds.Width == size.Item1 && window.Bounds.Height == size.Item2);
                }
                await ClearAsync();
                Show("Earlier-instance-A");
                Show("Earlier-instance-B");
                Show(instance, () => calls.Add(request));
                await CheckLayoutAsync($"{theme}-minimum-three-notices", multiple: true);
            }
            App.ApplyTheme("dark");
            await GoElsewhereAsync();
            State.Clear();
            Show(instance, () => calls.Add(request));
            await Task.Delay(100);
            var beforeNavigation = State.Current;
            Check("在别的页面时发生的失败仍保存通知", beforeNavigation is not null);
            await GoInstanceAsync();
            Check("回到实例页绑定原通知状态和实例回调", ReferenceEquals(Host.DataContext, State) && State.Current == beforeNavigation);
            foreach (var language in new[] { "zh-Hant", "en", "ja" })
            {
                L.SetLanguage(language);
                ShowCrash();
                await ClearAsync();
                var displayedInstance = language == "zh-Hant" ? instance : "Sodium-Fabric-1.21.1-with-extra-long-instance-name-for-layout-validation";
                Show(displayedInstance);
                await CheckLayoutAsync($"{language}-minimum");
            }
            State.Clear();
            Show(instance, () => calls.Add(request));
            await Task.Delay(100);
            window.UpdateLayout();
            var button = Host.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == L.T("home.bake.retry_without"));
            var pendingRetry = State.Current!.RetryCommand;
            Check("重试按钮可通过自动化接口点击", button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            ControlAutomationPeer.CreatePeerForElement(button).GetProvider<IInvokeProvider>()!.Invoke();
            for (var i = 0; i < 40 && calls.Count == 0; i++) await Task.Delay(50);
            Check("实际按钮点击已触发一次回调并移除通知", calls.Count == 1 && State.Notices.Count == 0);
            pendingRetry.Execute(null);
            Check("按钮只回调一次且保留实例/加入目标/SkipBake", calls.Count == 1 && calls[0] == request && request.VersionId == original.VersionId);
            // Only the bake settings matter here: the language and theme switches above legitimately save other fields.
            var settingsAfter = BakeSettingsSnapshot();
            Check("按钮移除通知，烘焙设置未改变", State.Notices.Count == 0 && settingsAfter == settingsBefore,
                $"before={settingsBefore}; after={settingsAfter}");
            window.UpdateLayout();
            Check("通知结束后提示卡收起", !Host.IsVisible && !NoticeCard.IsVisible);
            using (var simulatedGame = Process.GetCurrentProcess())
            {
                var run = Home.AddRunForTest(instance, simulatedGame);
                try
                {
                    Show("Earlier-instance-A", () => calls.Add(request));
                    Show("Earlier-instance-B", () => calls.Add(request));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    window.UpdateLayout();
                    Check("活动局的进度行优先显示，不被旧通知遮住", State.Notices.Count == 2 && !Host.IsEffectivelyVisible
                        && Page.FindControl<Border>("LaunchLineCard")!.IsEffectivelyVisible && Home.ShowProgress);
                    var stale = State.Notices.ToArray();
                    // 已有模拟运行局：真实启动请求进入多开确认后返回，不进入下载或启动流程。
                    Home.Service.RequestLaunch(original);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Check("新的启动请求清除所有旧通知", !State.HasNotice && Home.PendingLaunch == original);
                    foreach (var notice in stale) notice.RetryCommand.Execute(null);
                    Check("旧通知的回调全部失效", calls.Count == 1);
                }
                finally
                {
                    // 先处理可能仍在排队的请求，再撤下模拟局，避免它落入真实下载/启动流程。
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Home.DismissPendingLaunchCommand.Execute(null);
                    Home.RemoveRunForTest(run);
                }
            }
            await GoElsewhereAsync();
            Show(instance, () => calls.Add(request));
            await Task.Delay(50);
            var clock = Stopwatch.StartNew();
            await Task.Delay(6500);
            Check("停留在其他页面时八秒前仍保留重试机会", State.Notices.Count == 1);
            await GoInstanceAsync();
            while (State.Notices.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(11)) await Task.Delay(100);
            Check("回到实例页不重置八秒期限且不调用重试", State.Notices.Count == 0 && clock.Elapsed >= TimeSpan.FromSeconds(7.8) && calls.Count == 1);
            await CheckInstanceBakeEntryAsync(instance);
            await CheckProblemNoticeAsync();
            await CheckSettingsCleanupAsync();
            await CheckSettingsInstallLoaderAsync();
            await CheckLoaderDownloadOfferAsync();
        }
        catch (Exception ex)
        {
            _report.Add("FAIL " + ex);
            Environment.ExitCode = 1;
            await ShotAsync("failed-state");
        }
        finally
        {
            var closedCalls = 0;
            var state = Toasts.BakeNotices!;
            var closingNotice = state.Show("window-close", () => closedCalls++);
            window.Closed += (_, _) =>
            {
                closingNotice.RetryCommand.Execute(null);
                var closed = state.Notices.Count == 0 && closedCalls == 0;
                _report.Add($"{(closed ? "PASS" : "FAIL")} 关闭窗口会清空警告并使旧按钮失效");
                if (!closed) Environment.ExitCode = 1;
            };
            window.Close();
            if (!_report.Any(line => line.StartsWith("FAIL ", StringComparison.Ordinal))) _report.Add("DONE");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        }
    }

    /// <summary>
    /// The bake entry on the instance page: since the mods-tab redesign it is "Bake now" in the toolbar's ⋯ menu. Checks
    /// that the ⋯ button is reachable at small and medium sizes and that its menu offers the localized entry. Doesn't bake.
    /// </summary>
    private async Task CheckInstanceBakeEntryAsync(string instance)
    {
        var page = main.Pages.OfType<InstanceViewModel>().Single();
        main.CurrentPage = page;
        await Task.Delay(200);
        page.SelectedId = instance;
        page.TabIndex = (int)InstanceTab.Mods;
        await Task.Delay(200);
        if (page.Current?.Id != instance || _standIn)
        {
            // The test profile has no such instance (it is prepared by hand on the test machines).
            _report.Add($"SKIP 实例页烘焙入口：测试目录里没有实例 {instance}");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
            return;
        }
        Check("实例页保留模组页签及实验烘焙入口", page.BakeEnabled);
        foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            L.SetLanguage(language);
            foreach (var (width, height) in new[] { (760, 440), (1280, 700) })
            {
                window.Width = width;
                window.Height = height;
                await Task.Delay(200);
                window.UpdateLayout();
                // The ⋯ button sits next to the mod search box. Menu item bindings only resolve once the menu has opened,
                // so find the button by place and look for the bake item after opening it.
                var search = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "ModSearchBox" && t.IsEffectivelyVisible);
                var more = (search?.Parent as Panel)?.Children.OfType<Button>()
                    .FirstOrDefault(b => b.IsEffectivelyVisible && b.Flyout is MenuFlyout && AutomationProperties.GetName(b) == L.T("common.more_actions"));
                Check($"实例烘焙 {language}-{width}：模组工具栏有 ⋯ 按钮", more is not null);
                more!.BringIntoView();
                await Task.Delay(100);
                window.UpdateLayout();
                var bounds = BoundsIn(more, window);
                var hit = window.InputHitTest(bounds.Center) as Visual;
                Check($"实例烘焙 {language}-{width}：⋯ 按钮完整可见且中心命中", more.IsEffectivelyEnabled && new Rect(window.ClientSize).Contains(bounds)
                    && hit is not null && (ReferenceEquals(hit, more) || hit.GetVisualAncestors().Contains(more)));
                var flyout = (MenuFlyout)more.Flyout!;
                flyout.ShowAt(more);
                await Task.Delay(200);
                var item = flyout.Items.OfType<MenuItem>().FirstOrDefault(m => m.Command == page.BakeNowCommand);
                Check($"实例烘焙 {language}-{width}：⋯ 菜单里有烘焙，文案随语言切换且可用", item is not null && item.IsVisible && item.Header?.ToString() == L.T("instance.bake.now")
                    && page.BakeNowCommand.CanExecute(item.CommandParameter));
                await ShotAsync($"instance-bake-{language}-{width}x{height}");
                flyout.Hide();
                await Task.Delay(100);
            }
        }
        L.SetLanguage("zh-Hans");
    }

    /// <summary>A bake problem with nothing to retry (dependency problem, loader too old): text only, no retry button.</summary>
    private async Task CheckProblemNoticeAsync()
    {
        await GoInstanceAsync();
        foreach (var language in new[] { "zh-Hans", "en" })
        {
            L.SetLanguage(language);
            window.Width = 1280;
            window.Height = 700;
            State.Clear();
            Home.Crash = null;
            Toasts.BakeProblem(L.F("home.bake.fallback", L.F("core.bake.dependency_failed", "modmenu: " + L.T("core.bake.problem.missing_dep"))));
            for (var i = 0; i < 40 && !(State.HasNotice && Host.IsEffectivelyVisible); i++) { await Task.Delay(50); window.UpdateLayout(); }
            Check($"烘焙问题提示 {language}：显示在实例页", State.HasNotice && Host.IsEffectivelyVisible && State.Current!.Text.Contains("modmenu"));
            var retry = Host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content?.ToString() == L.T("home.bake.retry_without"));
            Check($"烘焙问题提示 {language}：没有重试按钮", retry is null || !retry.IsEffectivelyVisible);
            await ShotAsync($"bake-problem-{language}");
            State.Current!.RetryCommand.Execute(null);
            Check($"烘焙问题提示 {language}：点不到的重试不会出错并收起", !State.HasNotice);
        }
        Toasts.BakeFailed(L.F("home.bake.mixin_disabled", 2, "sodium" + L.T("core.mp.format.listseparator") + "lithium"), () => { });
        for (var i = 0; i < 40 && !Host.IsEffectivelyVisible; i++) { await Task.Delay(50); window.UpdateLayout(); }
        Check("Mixin 被关提示：带“不烘焙再启动”", Host.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content?.ToString() == L.T("home.bake.retry_without")));
        await ShotAsync("bake-mixin-disabled-en");
        State.Clear();
    }

    /// <summary>Settings › Experimental: the cleanup button is there, reachable by settings#BakeCleanup, and reports without a loader.</summary>
    private async Task CheckSettingsCleanupAsync()
    {
        var settings = main.Pages.OfType<SettingsViewModel>().Single();
        foreach (var language in new[] { "zh-Hans", "en", "ja" })
        {
            L.SetLanguage(language);
            foreach (var (width, height) in new[] { (760, 440), (1280, 700) })
            {
                window.Width = width;
                window.Height = height;
                main.Shell.Navigate("settings#BakeCleanup");
                for (var i = 0; i < 60 && window.RevealedField?.Name != "BakeCleanup"; i++) await Task.Delay(50);
                Check($"清理烘焙缓存 {language}-{width}：settings#BakeCleanup 定位到按钮", window.RevealedField?.Name == "BakeCleanup");
                var button = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, settings.CleanBakeCacheCommand));
                button.BringIntoView();
                await Task.Delay(150);
                window.UpdateLayout();
                Check($"清理烘焙缓存 {language}-{width}：按钮完整可见、文案随语言", button.IsEffectivelyVisible && new Rect(window.ClientSize).Contains(BoundsIn(button, window))
                    && button.Content?.ToString() == L.T("settings.bake.gc"));
                await ShotAsync($"settings-bake-cleanup-{language}-{width}x{height}");
            }
        }
        // No loader build in this test data folder: the button says so and never deletes anything.
        L.SetLanguage("en");
        await settings.CleanBakeCacheCommand.ExecuteAsync(null);
        Check("没有加载器时清理只给出原因", settings.BakeCleanupStatus == L.T("core.bake.loader_missing_short") || settings.BakeCleanupStatus == L.T("core.bake.gc_empty"));
        await ShotAsync("settings-bake-cleanup-status-en");
    }

    /// <summary>
    /// Settings › Experimental › "Install loader from zip…": the entry is findable and visible, a good package is
    /// checked, unpacked into the data folder and selected, a tampered one is refused without changing the selection.
    /// With AMBER_SELFTEST_BAKE_ZIP the real loader zip is installed the same way (and stays selected).
    /// </summary>
    private async Task CheckSettingsInstallLoaderAsync()
    {
        var settings = main.Pages.OfType<SettingsViewModel>().Single();
        foreach (var language in new[] { "zh-Hans", "en", "ja" })
        {
            L.SetLanguage(language);
            foreach (var (width, height) in new[] { (760, 440), (1280, 700) })
            {
                window.Width = width;
                window.Height = height;
                main.Shell.Navigate("settings#BakeLoaderInstall");
                for (var i = 0; i < 60 && window.RevealedField?.Name != "BakeLoaderInstall"; i++) await Task.Delay(50);
                Check($"安装加载器 {language}-{width}：settings#BakeLoaderInstall 定位到按钮", window.RevealedField?.Name == "BakeLoaderInstall");
                var button = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BakeInstallButton");
                await ScrollIntoViewAsync(button);
                Check($"安装加载器 {language}-{width}：按钮完整可见、文案随语言", button.IsEffectivelyVisible && new Rect(window.ClientSize).Contains(BoundsIn(button, window))
                    && button.Content?.ToString() == L.T("settings.bake.install"));
                var download = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BakeDownloadButton");
                await ScrollIntoViewAsync(download);
                var published = Amber.Core.Bake.BakeLoaderRelease.Pinned.IsPublished;
                Check($"下载加载器 {language}-{width}：按钮完整可见、文案随语言、有说明", download.IsVisible && new Rect(window.ClientSize).Contains(BoundsIn(download, window))
                    && download.Content?.ToString() == L.T("settings.bake.download")
                    && ToolTip.GetTip(download)?.ToString() == L.T(published ? "settings.bake.download_hint" : "core.bake.download_unpublished"));
                var unpublished = window.PageHost.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "BakeDownloadUnpublishedText");
                Check($"下载加载器 {language}-{width}：未发布时按钮关着并写明原因", published
                    // The whole install row follows the "Bake" switch, so with bake off the button is greyed by its parent.
                    ? download.IsEnabled && download.IsEffectivelyEnabled == settings.ExperimentalBake && !unpublished.IsVisible
                    : !download.IsEffectivelyEnabled && unpublished.IsEffectivelyVisible && unpublished.Text == L.T("core.bake.download_unpublished"));
                var cancel = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BakeDownloadCancelButton");
                Check($"下载加载器 {language}-{width}：没在下载时不显示取消", !cancel.IsVisible);
                await ShotAsync($"settings-bake-install-{language}-{width}x{height}");
            }
        }

        L.SetLanguage("en");
        window.Width = 1280;
        window.Height = 700;
        var before = settings.BakeLoaderPath;
        var good = FakeLoaderZip("good", tamper: false);
        await settings.InstallBakeLoaderAsync(good);
        var installed = Path.Combine(LauncherService.BakeLoaderInstallRoot, "0.0.0-selftest");
        Check("从压缩包安装：核对后解压到数据文件夹并选用", settings.BakeLoaderPath == installed && File.Exists(Path.Combine(installed, "loader.jar"))
            && settings.BakeLoaderStatus.Contains("0.0.0-selftest", StringComparison.Ordinal), settings.BakeLoaderStatus);
        main.Shell.Navigate("settings#BakeLoaderInstall");
        await Task.Delay(300);
        await ShotAsync("settings-bake-install-done-en");

        var bad = FakeLoaderZip("bad", tamper: true);
        settings.BakeLoaderPath = before;
        await settings.InstallBakeLoaderAsync(bad);
        Check("被改过的加载器包被拒，选用的文件夹不变", settings.BakeLoaderPath == before
            && settings.BakeLoaderStatus.StartsWith(L.F("settings.bake.install_failed", ""), StringComparison.Ordinal)
            && settings.BakeLoaderStatus.Contains("loader.jar", StringComparison.Ordinal), settings.BakeLoaderStatus);
        await ShotAsync("settings-bake-install-refused-en");

        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_BAKE_DOWNLOAD") == "1")
        {
            // Opt-in, real network: "Download loader" fetches the pinned release, checks it and selects it.
            settings.BakeLoaderPath = "";
            main.Shell.Navigate("settings#BakeLoaderInstall");
            await Task.Delay(300);
            var download = window.PageHost.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BakeDownloadButton");
            await ScrollIntoViewAsync(download);
            Check("下载加载器：已发布时按钮可用", Amber.Core.Bake.BakeLoaderRelease.Pinned.IsPublished && download.IsEffectivelyEnabled);
            await ShotAsync("settings-bake-download-enabled-en");
            download.Command!.Execute(null);
            for (var i = 0; i < 40 && !settings.IsDownloadingBakeLoader; i++) await Task.Delay(50);
            var sawProgress = false;
            for (var i = 0; i < 2400 && settings.IsDownloadingBakeLoader; i++)
            {
                if (!sawProgress && settings.BakeLoaderStatus.Contains('%'))
                {
                    sawProgress = true;
                    await ShotAsync("settings-bake-download-progress-en");
                }
                await Task.Delay(100);
            }
            var pin = Amber.Core.Bake.BakeLoaderRelease.Pinned;
            var toolkit = Amber.Core.Bake.BakeToolkit.Find(settings.BakeLoaderPath);
            Check("下载加载器：按固定的大小和 SHA-256 下好、装好并选用", toolkit?.Package is { } pkg && pkg.LoaderVersion == pin.LoaderVersion
                && settings.BakeLoaderPath == Path.Combine(LauncherService.BakeLoaderInstallRoot, pin.LoaderVersion)
                && settings.BakeLoaderStatus.Contains(pin.Sha256[..12], StringComparison.Ordinal), settings.BakeLoaderStatus);
            var package = toolkit!.Package!;
            package.Verify();
            Check("下载加载器：清单里每个文件都核对过（含许可文件）", package.Files.ContainsKey("LICENSE") && package.Files.ContainsKey("THIRD_PARTY_NOTICES.md")
                && package.Files.Keys.Any(f => f.StartsWith("licenses/", StringComparison.Ordinal)), $"{package.Files.Count} files");
            _report.Add("INFO 下载的加载器装在 " + settings.BakeLoaderPath + "，" + package.Files.Count + " 个文件");
            await ShotAsync("settings-bake-download-done-en");
        }
        else if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_BAKE_ZIP") is { Length: > 0 } real && File.Exists(real))
        {
            await settings.InstallBakeLoaderAsync(real);
            var ok = Amber.Core.Bake.BakeToolkit.Find(settings.BakeLoaderPath) is { Package: { } p } && p.Interface >= new Version(1, 3);
            Check("真实加载器包从压缩包装好并选用", ok, settings.BakeLoaderStatus);
            _report.Add("INFO 加载器装在 " + settings.BakeLoaderPath);
            await ShotAsync("settings-bake-install-real-en");
        }
        else settings.BakeLoaderPath = before;
    }

    /// <summary>
    /// Baking on and no loader installed: the launch status box offers "Download loader" in place of the retry button.
    /// The button's action is a stand-in here (the self-test never touches download services); the real one is wired.
    /// </summary>
    private async Task CheckLoaderDownloadOfferAsync()
    {
        Check("没有加载器时的下载入口已接到设置页", Home.DownloadBakeLoader is not null);
        await GoInstanceAsync();
        foreach (var language in new[] { "zh-Hans", "en", "ja" })
        {
            L.SetLanguage(language);
            window.Width = 1280;
            window.Height = 700;
            await ClearAsync();
            var clicked = false;
            Toasts.BakeAction(L.T("home.bake.loader_missing_notice"), L.T("home.bake.download_loader"), L.T("settings.bake.download_hint"), () => clicked = true);
            Button? action = null;
            for (var i = 0; i < 40; i++)
            {
                window.UpdateLayout();
                action = Host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "BakeNoticeAction" && b.IsEffectivelyVisible);
                if (action is not null) break;
                await Task.Delay(50);
            }
            Check($"缺加载器提示 {language}：按钮是“下载加载器”", action?.Content?.ToString() == L.T("home.bake.download_loader")
                && ToolTip.GetTip(action)?.ToString() == L.T("settings.bake.download_hint"), action?.Content?.ToString() ?? "无按钮");
            NoticeCard.BringIntoView();
            window.UpdateLayout();
            Check($"缺加载器提示 {language}：按钮完整在提示卡和窗口内", BoundsIn(NoticeCard, window).Contains(BoundsIn(action!, window))
                && new Rect(window.ClientSize).Contains(BoundsIn(action!, window)));
            await ShotAsync($"bake-loader-missing-offer-{language}");
            action!.Command!.Execute(null);
            Check($"缺加载器提示 {language}：点按钮执行下载并收起", clicked && !State.HasNotice);
        }
        Show("x");
        for (var i = 0; i < 40 && !Host.IsEffectivelyVisible; i++) { await Task.Delay(50); window.UpdateLayout(); }
        Check("其他提示仍是“不烘焙再启动”", Host.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Content?.ToString() == L.T("home.bake.retry_without")));
        State.Clear();
        L.SetLanguage("en");
    }

    /// <summary>Scrolls a control into the window and waits until the layout has settled there (wrapping text can move it after the first pass).</summary>
    private async Task ScrollIntoViewAsync(Control control)
    {
        for (var i = 0; i < 20; i++)
        {
            control.BringIntoView();
            await Task.Delay(50);
            window.UpdateLayout();
            if (new Rect(window.ClientSize).Contains(BoundsIn(control, window))) return;
        }
    }

    /// <summary>A tiny loader package zip for the install check (not a real loader). tamper: one file differs from its manifest.</summary>
    private string FakeLoaderZip(string name, bool tamper)
    {
        var source = Path.Combine(reportDir, "fake-loader-" + name);
        if (Directory.Exists(source)) Directory.Delete(source, true);
        var folder = Path.Combine(source, "amber-ul-0.0.0-selftest");
        var files = new System.Text.Json.Nodes.JsonObject();
        foreach (var (relative, text) in new[] { ("loader.jar", "loader"), ("libs/asm.jar", "asm"), ("bridges/fmlbridge.jar", "fml") })
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            files[relative] = new System.Text.Json.Nodes.JsonObject { ["sha256"] = Amber.Core.Bake.BakePackage.Sha256(path), ["size"] = new FileInfo(path).Length };
        }
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = Amber.Core.Bake.BakePackage.Schema,
            ["loaderVersion"] = "0.0.0-selftest",
            ["protocol"] = 1,
            ["interface"] = "1.3",
            ["platforms"] = new System.Text.Json.Nodes.JsonObject { ["vanilla"] = new System.Text.Json.Nodes.JsonArray("1.21.1") },
            ["javaMin"] = new System.Text.Json.Nodes.JsonObject { ["vanilla"] = 21 },
            ["classpath"] = new System.Text.Json.Nodes.JsonArray("loader.jar", "libs/asm.jar"),
            ["files"] = files,
        };
        File.WriteAllText(Path.Combine(folder, Amber.Core.Bake.BakePackage.ManifestName), manifest.ToJsonString());
        if (tamper) File.WriteAllText(Path.Combine(folder, "loader.jar"), "changed");
        var zip = Path.Combine(reportDir, $"fake-loader-{name}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        System.IO.Compression.ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

}
