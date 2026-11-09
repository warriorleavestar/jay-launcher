using System.IO.Compression;
using System.Text;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 主题包自检（AMBER_SELFTEST_SCOPE=theme）：导出当前主题 → 改强调色和圆角再导入 → 界面真的换了 → 坏包被拒绝不留文件 →
/// 恢复默认。四种语言、小中大窗口各截一张设置页。只用 AMBER_DATA_DIR 指向的独立数据目录。
/// </summary>
internal sealed class ThemeSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];

    private void Check(string name, bool ok, string detail = "")
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
    }

    private async Task ShotAsync(string name)
    {
        await Task.Delay(400);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(reportDir, name + ".png"));
    }

    private static uint? Brush(string key, ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush b ? b.Color.ToUInt32() : null;

    private static object? Resource(string key) => Application.Current!.TryGetResource(key, null, out var value) ? value : null;

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        try
        {
            Check("自检使用显式独立数据目录", Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 });
            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            main.CurrentPage = settings;
            await Task.Delay(600);
            var defaultAccent = Brush("Accent", ThemeVariant.Dark);

            // 1. 导出当前主题，读回来和默认值一致。
            var exported = Path.Combine(reportDir, "exported.jaytheme");
            settings.ExportThemePack(exported);
            var roundTrip = ThemePackReader.Read(exported);
            Check("导出当前主题能被读回，颜色和默认一致", roundTrip.DarkColors["Accent"] == ThemeDefaults.Dark["Accent"] && roundTrip.Problems.Count == 0);

            // 2. 改强调色、圆角、字号，导入后立即生效（不用重启）。
            var custom = Path.Combine(reportDir, "selftest.jaytheme");
            WritePack(custom, """{ "id": "selftest-blue", "name": "自检蓝" }""",
                """{ "seed": { "accent": "#2F80ED" }, "radius": { "RadiusMd": 0 }, "sizes": { "FontBody": 16 } }""",
                ("styles.json", """{ "card": { "radius": 0, "borderWidth": 3 }, "sidebar": { "background": "#123456" } }"""));
            settings.ImportThemePack(custom);
            await Task.Delay(400);
            Check("导入后设置里选中了新主题", settings.SelectedThemePack?.Id == "selftest-blue", settings.SelectedThemePack?.Id ?? "(空)");
            Check("导入后强调色立即换成主题的颜色", Brush("Accent", ThemeVariant.Dark) == 0xFF2F80ED && Brush("Accent", ThemeVariant.Light) == 0xFF2F80ED);
            Check("导入后圆角和字号生效", Resource("RadiusMd") is CornerRadius { TopLeft: 0 } && Resource("FontBody") is 16.0);
            var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
            var sidebar = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("sidebar"));
            Check("组件样式生效：卡片方角粗边、侧栏换色", card.CornerRadius.TopLeft == 0 && card.BorderThickness.Left == 3
                && sidebar.Background is ISolidColorBrush { Color: var sc } && sc.ToUInt32() == 0xFF123456,
                $"卡片圆角 {card.CornerRadius.TopLeft} 边 {card.BorderThickness.Left}");
            // 安全窗口锁定：同一时刻，锁定的窗口里查到的还是 Jay 默认的强调色。
            var locked = new Window { Content = new Border { Classes = { "card" } } };
            ThemeService.LockToDefault(locked);
            Check("安全窗口不受主题影响（强调色仍是默认）",
                locked.TryFindResource("Accent", ThemeVariant.Dark, out var lockedAccent) && lockedAccent is ISolidColorBrush lb && lb.Color.ToUInt32() == defaultAccent);
            Check("设置文件记下了主题包", File.ReadAllText(AppSettings.FilePath).Contains("\"ThemePack\": \"selftest-blue\"", StringComparison.Ordinal));
            foreach (var lang in new[] { "zh-Hans", "zh-Hant", "en", "ja" })
                foreach (var (w, h) in new[] { (760, 440), (1024, 640), (1600, 900) })
                {
                    L.SetLanguage(lang);
                    window.Width = w;
                    window.Height = h;
                    await ShotAsync($"theme-{lang}-{w}x{h}");
                }
            L.SetLanguage("zh-Hans");

            // 2b. 排版和层级：导航挪到顶上、只显示图标、藏掉工具和日志；启动页按宽度重新排。
            await LayoutAsync(settings);

            // 3. 坏包：带 .dll 的整个拒绝，不留文件，当前主题不变。
            var bad = Path.Combine(reportDir, "bad.jaytheme");
            WritePack(bad, """{ "id": "selftest-bad", "name": "坏" }""", "{}", ("payload.dll", "MZ"));
            settings.ImportThemePack(bad);
            await Task.Delay(300);
            Check("带程序文件的主题包被拒绝，不安装", !Directory.Exists(Path.Combine(ThemeService.Store.Root, "selftest-bad")) && settings.SelectedThemePack?.Id == "selftest-topbar");

            // 4. 恢复默认：颜色回到默认，设置文件清掉主题包。
            settings.ResetThemePackCommand.Execute(null);
            await Task.Delay(400);
            Check("恢复默认主题后强调色回到默认", Brush("Accent", ThemeVariant.Dark) == defaultAccent && ThemeService.Current is null);
            Check("恢复默认后设置文件不再指定主题包", !File.ReadAllText(AppSettings.FilePath).Contains("selftest-", StringComparison.Ordinal));
            main.CurrentPage = main.Pages[0];
            await Task.Delay(500);
            var home = window.GetVisualDescendants().OfType<HomePage>().First();
            Check("恢复默认后导航回到左边、九个页面都在、启动页用回自带布局",
                window.ShellNav == "left" && main.NavPages.Count == main.Pages.Count && !home.ThemeLayout.UsingThemeLayout
                && home.ThemeLayout.Regions["home.title"].Parent is not null);
            main.CurrentPage = settings;
            await Task.Delay(300);

            // 5. 删除主题。
            settings.SelectedThemePack = settings.ThemePacks.First(o => o.Id == "selftest-blue");
            settings.RemoveThemePackCommand.Execute(null);
            await Task.Delay(300);
            Check("删除主题后目录被删、选回默认", !Directory.Exists(Path.Combine(ThemeService.Store.Root, "selftest-blue")) && settings.SelectedThemePack?.Id == ThemeService.DefaultId
                && Brush("Accent", ThemeVariant.Dark) == defaultAccent);
            await ShotAsync("theme-after-reset");
        }
        catch (Exception e)
        {
            Check("自检没有异常", false, e.ToString());
        }
        finally
        {
            _report.Add(_report.All(l => l.StartsWith("PASS", StringComparison.Ordinal)) ? "ALL PASS" : "HAS FAILURES");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
            window.Close();
        }
    }

    private async Task LayoutAsync(SettingsViewModel settings)
    {
        var path = Path.Combine(reportDir, "topbar.jaytheme");
        WritePack(path, """{ "id": "selftest-topbar", "name": "自检顶栏" }""", """{ "seed": { "accent": "#0F9D8A" } }""",
            ("layout/shell.json", """{ "nav": "top", "navStyle": "icons", "pages": ["home", "multiplayer", "settings"], "hidden": ["tools", "log"] }"""),
            ("layout/pages/home.json", """
            {
              "wide": { "type": "grid", "columns": ["*", "360"], "rows": ["auto", "*"], "spacing": 16, "children": [
                  { "type": "region", "name": "home.title", "columnSpan": 2 },
                  { "type": "scroll", "row": 1, "children": ["home.pending", "home.onboarding", "home.instance", "home.crash"] },
                  { "type": "region", "name": "home.launch", "row": 1, "column": 1, "valign": "start" } ] },
              "default": { "type": "dock", "children": [
                  { "type": "region", "name": "home.launch", "dock": "top", "margin": [0, 0, 0, 12] },
                  { "type": "scroll", "children": ["home.pending", "home.onboarding", "home.instance", "home.crash"] } ] }
            }
            """));
        settings.ImportThemePack(path);
        await Task.Delay(500);
        Check("导入带排版的主题包：导航挪到顶上", window.ShellNav == "top" && window.SidebarHost.Bounds.Width > window.Bounds.Width - 4
            && window.SidebarHost.Bounds.Height < 120, $"导航 {window.ShellNav} {window.SidebarHost.Bounds}");
        Check("导航按主题排序、藏掉工具和日志，启动页和设置页还在",
            main.NavPages.Take(3).SequenceEqual([main.Pages[0], main.Pages.OfType<MultiplayerViewModel>().Single(), settings])
            && !main.NavPages.OfType<ToolsViewModel>().Any() && !main.NavPages.OfType<LogViewModel>().Any() && main.Pages.Count == 9);
        // 当前页（设置）不在导航里也不会被清空；藏起来的页还能跳过去。
        main.CurrentPage = main.Pages.OfType<LogViewModel>().Single();
        await Task.Delay(300);
        Check("藏起来的页面还能跳过去", window.GetVisualDescendants().OfType<LogPage>().Any() && main.NavSelection is null);

        main.CurrentPage = main.Pages[0];
        window.Width = 1600;
        window.Height = 900;
        await Task.Delay(600);
        var home = window.GetVisualDescendants().OfType<HomePage>().First();
        var launch = home.ThemeLayout.Regions["home.launch"];
        Check("宽窗口：启动页用主题的布局，启动卡片在右边一栏",
            home.ThemeLayout.UsingThemeLayout && launch.TranslatePoint(default, home) is { X: > 600 },
            $"启动卡片位置 {launch.TranslatePoint(default, home)}");
        await ShotAsync("layout-home-1600x900");

        window.Width = 1024;
        window.Height = 640;
        await Task.Delay(600);
        Check("中等窗口：换成另一棵树，启动卡片在最上面",
            home.ThemeLayout.UsingThemeLayout && launch.TranslatePoint(default, home) is { Y: < 20 } && home.ThemeLayout.Regions["home.title"].Parent is null,
            $"启动卡片位置 {launch.TranslatePoint(default, home)}");
        await ShotAsync("layout-home-1024x640");
        window.Width = 760;
        window.Height = 440;
        await ShotAsync("layout-home-760x440");
        main.CurrentPage = main.Pages.OfType<MultiplayerViewModel>().Single();
        window.Width = 1280;
        window.Height = 800;
        await ShotAsync("layout-multiplayer-1280x800");
        main.CurrentPage = settings;
        await Task.Delay(300);
    }

    private static void WritePack(string path, string manifest, string tokens, params (string Name, string Text)[] extra)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in new[] { ("theme.json", manifest), ("tokens.json", tokens) }.Concat(extra))
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }
}
