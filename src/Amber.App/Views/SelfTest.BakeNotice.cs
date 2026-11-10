using System.Diagnostics;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Diagnostics;
using Amber.Core.Localization;
using Avalonia;
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
    private HomePage HomeView => window.GetVisualDescendants().OfType<HomePage>().Single();

    private void Check(string name, bool ok)
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
        if (!ok) throw new InvalidOperationException(name);
    }

    private void Check(string name, bool ok, string detail) => Check(ok || detail.Length == 0 ? name : $"{name}（{detail}）", ok);

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

    private async Task<double> EmptyCardHeightAsync()
    {
        State.Clear();
        await Task.Delay(100);
        window.UpdateLayout();
        return HomeView.FindControl<Border>("LaunchStatusCard")!.Bounds.Height;
    }

    private async Task CheckLayoutAsync(string name, double originalCardHeight, bool multiple = false)
    {
        // 新增内容的模板可能晚于通知集合更新；等真实控件出现再检查，不用固定延时猜布局完成。
        for (var i = 0; i < 40; i++)
        {
            window.UpdateLayout();
            if (State.HasNotice && Host.IsEffectivelyVisible && Host.Bounds.Height > 0
                && Host.GetVisualDescendants().OfType<Button>().Any(b => b.Content?.ToString() == L.T("home.bake.retry_without"))) break;
            await Task.Delay(50);
        }
        Check(name + "：警告已在状态框呈现", Host.IsVisible && State.HasNotice && Host.Bounds.Height > 0);
        var homePage = HomeView;
        var primary = homePage.FindControl<Button>("PrimaryButton")!;
        var primaryBounds = BoundsIn(primary, window);
        var noticeBounds = BoundsIn(Host, window);
        var card = homePage.FindControl<Border>("LaunchStatusCard")!;
        Check(name + "：提示完整位于原状态框", BoundsIn(card, window).Contains(noticeBounds));
        Check(name + "：状态框不因提示增高", card.Bounds.Height <= originalCardHeight + 1);
        Check(name + "：主启动按钮不被遮挡且仍可操作", primaryBounds.Bottom <= window.Bounds.Height
            && noticeBounds.Right <= primaryBounds.Left && primary.IsEffectivelyEnabled);
        var retry = Host.GetVisualDescendants().OfType<Button>().First(b => b.Command is not null && b.Content?.ToString() == L.T("home.bake.retry_without"));
        Check(name + "：紧凑重试按钮完整可见", BoundsIn(card, window).Contains(BoundsIn(retry, window)));
        var description = Host.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == State.Current!.Text);
        Check(name + "：长说明最多两行且完整内容保留在提示框", description.Bounds.Height <= 36 && ToolTip.GetTip(description)?.ToString() == State.Current!.Text);
        var homeScroller = homePage.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(name + "：崩溃详情保留可滚动区域", homeScroller.Bounds.Height >= 60);
        homeScroller.Offset = new Vector(0, homeScroller.Extent.Height);
        await Task.Delay(100);
        var log = homePage.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == L.T("home.crash.view_log"));
        var logBounds = BoundsIn(log, homeScroller);
        Check(name + "：滚动后崩溃操作可达", logBounds.Top >= 0 && logBounds.Bottom <= homeScroller.Bounds.Height);
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
            main.CurrentPage = Home;
            var settingsBefore = File.ReadAllText(AppSettings.FilePath);
            const string instance = "E2E-Bake-Sodium";
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
                    Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
                    var cardHeight = await EmptyCardHeightAsync();
                    Show(instance, () => calls.Add(request));
                    await CheckLayoutAsync($"{theme}-{size.Item1}x{size.Item2}", cardHeight);
                    Check("截图尺寸与文件名一致", window.Bounds.Width == size.Item1 && window.Bounds.Height == size.Item2);
                }
                var threeCardHeight = await EmptyCardHeightAsync();
                Show("Earlier-instance-A");
                Show("Earlier-instance-B");
                Show(instance, () => calls.Add(request));
                await CheckLayoutAsync($"{theme}-minimum-three-notices", threeCardHeight, multiple: true);
            }
            App.ApplyTheme("dark");
            main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
            State.Clear();
            Show(instance, () => calls.Add(request));
            await Task.Delay(100);
            var beforeNavigation = State.Current;
            Check("非启动页发生的失败仍保存通知", beforeNavigation is not null);
            main.CurrentPage = Home;
            await Task.Delay(100);
            window.UpdateLayout();
            Check("返回启动页绑定原通知状态和实例回调", ReferenceEquals(Host.DataContext, State) && State.Current == beforeNavigation);
            foreach (var language in new[] { "zh-Hant", "en", "ja" })
            {
                L.SetLanguage(language);
                Home.Crash = CrashAnalyzer.Analyze("java.lang.NoClassDefFoundError: net/fabricmc/loader/api/FabricLoader", []);
                var cardHeight = await EmptyCardHeightAsync();
                var displayedInstance = language == "zh-Hant" ? instance : "Sodium-Fabric-1.21.1-with-extra-long-instance-name-for-layout-validation";
                Show(displayedInstance);
                await CheckLayoutAsync($"{language}-minimum", cardHeight);
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
            Check("按钮移除通知，烘焙设置未改变", State.Notices.Count == 0 && File.ReadAllText(AppSettings.FilePath) == settingsBefore);
            window.UpdateLayout();
            Check("通知结束后原状态文字恢复", !Host.IsVisible && HomeView.GetVisualDescendants().OfType<StatusBar>().Single().IsVisible);
            using (var simulatedGame = Process.GetCurrentProcess())
            {
                var run = Home.AddRunForTest(instance, simulatedGame);
                try
                {
                    Show("Earlier-instance-A", () => calls.Add(request));
                    Show("Earlier-instance-B", () => calls.Add(request));
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    window.UpdateLayout();
                    Check("活动局的状态及进度优先显示，不被旧通知遮住", State.Notices.Count == 2 && !Host.IsEffectivelyVisible
                        && HomeView.GetVisualDescendants().OfType<StatusBar>().Single().IsEffectivelyVisible && Home.ShowProgress);
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
            main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
            Show(instance, () => calls.Add(request));
            await Task.Delay(50);
            var clock = Stopwatch.StartNew();
            await Task.Delay(6500);
            Check("停留在其他页面时八秒前仍保留重试机会", State.Notices.Count == 1);
            main.CurrentPage = Home;
            while (State.Notices.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(11)) await Task.Delay(100);
            Check("回到启动页不重置八秒期限且不调用重试", State.Notices.Count == 0 && clock.Elapsed >= TimeSpan.FromSeconds(7.8) && calls.Count == 1);
            await CheckInstanceBakeEntryAsync(instance);
            await CheckProblemNoticeAsync();
            await CheckSettingsCleanupAsync();
            await CheckSettingsInstallLoaderAsync();
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

    /// <summary>合并后的实例页仍使用现有模组工具栏；只检查入口，不触发补文件或烘焙进程。</summary>
    private async Task CheckInstanceBakeEntryAsync(string instance)
    {
        var page = main.Pages.OfType<InstanceViewModel>().Single();
        main.CurrentPage = page;
        await Task.Delay(200);
        page.SelectedId = instance;
        page.TabIndex = (int)InstanceTab.Mods;
        Check("实例页保留模组页签及实验烘焙入口", page.Current?.Id == instance && page.BakeEnabled);
        foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
        {
            L.SetLanguage(language);
            foreach (var (width, height) in new[] { (760, 440), (1280, 700) })
            {
                window.Width = width;
                window.Height = height;
                await Task.Delay(200);
                window.UpdateLayout();
                // "Bake now" lives in the mods toolbar's "More actions" menu: find that button, open it, check the item.
                Button? more = null;
                MenuItem? item = null;
                var seen = new List<string>();
                foreach (var candidate in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Flyout is MenuFlyout).ToList())
                {
                    candidate.BringIntoView();
                    await Task.Delay(100);
                    candidate.Flyout!.ShowAt(candidate);
                    await Task.Delay(150);
                    var items = ((MenuFlyout)candidate.Flyout).Items.OfType<MenuItem>().ToList();
                    seen.Add(string.Join("|", items.Select(m => m.Header?.ToString())));
                    item = items.FirstOrDefault(m => ReferenceEquals(m.Command, page.BakeNowCommand) || m.Header?.ToString() == L.T("instance.bake.now"));
                    if (item is not null) { more = candidate; break; }
                    candidate.Flyout.Hide();
                }
                if (item is null) _report.Add("INFO menus: " + string.Join(" ; ", seen));
                Check($"实例烘焙 {language}-{width}：“更多操作”菜单里有立即烘焙", more is not null && item is not null && ReferenceEquals(item.Command, page.BakeNowCommand));
                var bounds = BoundsIn(more!, window);
                Check($"实例烘焙 {language}-{width}：菜单按钮完整可见", more!.IsEffectivelyEnabled && new Rect(window.ClientSize).Contains(bounds));
                Check($"实例烘焙 {language}-{width}：菜单项可用且文案随语言切换", item!.IsEffectivelyVisible && item.IsEffectivelyEnabled
                    && item.Header?.ToString() == L.T("instance.bake.now"));
                more.Flyout!.Hide();
                await ShotAsync($"instance-bake-{language}-{width}x{height}");
            }
        }
    }

    /// <summary>A bake problem with nothing to retry (dependency problem, loader too old): text only, no retry button.</summary>
    private async Task CheckProblemNoticeAsync()
    {
        main.CurrentPage = Home;
        foreach (var language in new[] { "zh-Hans", "en" })
        {
            L.SetLanguage(language);
            window.Width = 1280;
            window.Height = 700;
            State.Clear();
            Home.Crash = null;
            Toasts.BakeProblem(L.F("home.bake.fallback", L.F("core.bake.dependency_failed", "modmenu: " + L.T("core.bake.problem.missing_dep"))));
            for (var i = 0; i < 40 && !(State.HasNotice && Host.IsEffectivelyVisible); i++) { await Task.Delay(50); window.UpdateLayout(); }
            Check($"烘焙问题提示 {language}：显示在状态框", State.HasNotice && Host.IsEffectivelyVisible && State.Current!.Text.Contains("modmenu"));
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
                button.BringIntoView();
                await Task.Delay(150);
                window.UpdateLayout();
                Check($"安装加载器 {language}-{width}：按钮完整可见、文案随语言", button.IsEffectivelyVisible && new Rect(window.ClientSize).Contains(BoundsIn(button, window))
                    && button.Content?.ToString() == L.T("settings.bake.install"));
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

        if (Environment.GetEnvironmentVariable("AMBER_SELFTEST_BAKE_ZIP") is { Length: > 0 } real && File.Exists(real))
        {
            await settings.InstallBakeLoaderAsync(real);
            var ok = Amber.Core.Bake.BakeToolkit.Find(settings.BakeLoaderPath) is { Package: { } p } && p.Interface >= new Version(1, 3);
            Check("真实加载器包从压缩包装好并选用", ok, settings.BakeLoaderStatus);
            _report.Add("INFO 加载器装在 " + settings.BakeLoaderPath);
            await ShotAsync("settings-bake-install-real-en");
        }
        else settings.BakeLoaderPath = before;
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
