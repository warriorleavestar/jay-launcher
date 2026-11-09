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
                """{ "seed": { "accent": "#2F80ED" }, "radius": { "RadiusMd": 0 }, "sizes": { "FontBody": 16 } }""");
            settings.ImportThemePack(custom);
            await Task.Delay(400);
            Check("导入后设置里选中了新主题", settings.SelectedThemePack?.Id == "selftest-blue", settings.SelectedThemePack?.Id ?? "(空)");
            Check("导入后强调色立即换成主题的颜色", Brush("Accent", ThemeVariant.Dark) == 0xFF2F80ED && Brush("Accent", ThemeVariant.Light) == 0xFF2F80ED);
            Check("导入后圆角和字号生效", Resource("RadiusMd") is CornerRadius { TopLeft: 0 } && Resource("FontBody") is 16.0);
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

            // 3. 坏包：带 .dll 的整个拒绝，不留文件，当前主题不变。
            var bad = Path.Combine(reportDir, "bad.jaytheme");
            WritePack(bad, """{ "id": "selftest-bad", "name": "坏" }""", "{}", ("payload.dll", "MZ"));
            settings.ImportThemePack(bad);
            await Task.Delay(300);
            Check("带程序文件的主题包被拒绝，不安装", !Directory.Exists(Path.Combine(ThemeService.Store.Root, "selftest-bad")) && settings.SelectedThemePack?.Id == "selftest-blue");

            // 4. 恢复默认：颜色回到默认，设置文件清掉主题包。
            settings.ResetThemePackCommand.Execute(null);
            await Task.Delay(400);
            Check("恢复默认主题后强调色回到默认", Brush("Accent", ThemeVariant.Dark) == defaultAccent && ThemeService.Current is null);
            Check("恢复默认后设置文件不再指定主题包", !File.ReadAllText(AppSettings.FilePath).Contains("selftest-blue", StringComparison.Ordinal));

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
