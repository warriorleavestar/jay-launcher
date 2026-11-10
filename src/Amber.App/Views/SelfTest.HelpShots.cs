using Amber.App.ViewModels;
using Amber.Core.Help;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=help-shots (official §9.7): makes the help articles' screenshots in all four help languages from
/// the real window, with hand-written instances in a throwaway game directory inside the report folder. Writes
/// <c>{lang}/{name}.png</c> under AMBER_HELP_SHOTS_DIR (or the report folder's <c>shots</c>); copy them to
/// <c>docs/help/shots</c>. Only the page is captured (no sidebar, account or privacy line), and nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task HelpShotsAsync()
    {
        var output = Environment.GetEnvironmentVariable("AMBER_HELP_SHOTS_DIR") is { Length: > 0 } dir ? dir : Path.Combine(reportDir, "shots");
        var service = main.Service;
        var game = Path.Combine(reportDir, "shots-game");
        WriteLibraryVersion(game, "fabric-1.20.1", """[{"name":"net.fabricmc:fabric-loader:0.16.9"}]""");
        WriteLibraryVersion(game, "neoforge-1.21.1", """[{"name":"net.neoforged:neoforge:21.1.1"}]""");
        WriteLibraryVersion(game, "1.21.4", "[]");
        WriteLibraryVersion(game, "quilt-1.20.4", """[{"name":"org.quiltmc:quilt-loader:0.26.0"}]""");
        var now = DateTimeOffset.Now;
        service.Settings.Instances = new()
        {
            ["fabric-1.20.1"] = new() { DisplayName = "Survival+", LastPlayed = now.AddDays(-1), PlayMinutes = 2160 },
            ["neoforge-1.21.1"] = new() { DisplayName = "Skyblock", LastPlayed = now.AddHours(-3) },
            ["quilt-1.20.4"] = new() { DisplayName = "Tech", Pinned = true, LastPlayed = now.AddMinutes(-40), PlayMinutes = 300 },
        };
        service.Settings.RecentInstances = ["neoforge-1.21.1", "fabric-1.20.1"];
        service.Settings.GameDirectory = game;
        service.SaveAndReload();
        service.SetCurrentInstance("neoforge-1.21.1");
        window.Width = 1100;
        window.Height = 700;

        var mp = Page<MultiplayerViewModel>();
        var settings = Page<SettingsViewModel>();
        // name → how to get there. The names are HelpLibrary.ShotNames; articles use ![…](shot:name).
        var shots = new (string Name, Func<Task> Show)[]
        {
            ("library", async () => { main.Shell.Navigate("library"); await WaitAsync(() => Page<LibraryViewModel>().IsLoaded, 10); }),
            ("multiplayer-lobby", () => { main.Shell.Navigate("play"); mp.ShowTabCommand.Execute("play"); return Task.CompletedTask; }),
            ("connection", () => { main.Shell.Navigate("play"); mp.DiagnosticsOpen = true; return Task.CompletedTask; }),
            ("drill", () => { main.Shell.Navigate("play"); mp.OpenDrillCommand.Execute(null); return Task.CompletedTask; }),
            ("appearance", () => { main.Shell.Navigate("settings"); settings.SelectGroup(Amber.Core.Navigation.SettingsCatalog.Appearance); return Task.CompletedTask; }),
        };
        Check("Help shots: list matches HelpLibrary.ShotNames", shots.Select(s => s.Name).SequenceEqual(HelpLibrary.ShotNames));
        var written = 0;
        foreach (var lang in HelpLibrary.Languages)
        {
            L.SetLanguage(lang);
            Directory.CreateDirectory(Path.Combine(output, lang));
            foreach (var (name, show) in shots)
            {
                mp.DiagnosticsOpen = false;
                mp.DrillOpen = false;
                mp.LessonOpen = false;
                await Dispatcher.UIThread.InvokeAsync(show);
                await Task.Delay(700);
                var path = Path.Combine(output, lang, name + ".png");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Render the whole window (so the page has its real background), then keep only the page.
                    var host = window.PageHost;
                    using var full = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    full.Render(window);
                    var at = host.TranslatePoint(new Point(0, 0), window) ?? default;
                    var area = new Rect(at, host.Bounds.Size).Inflate(12).Intersect(new Rect(window.Bounds.Size));
                    using var page = new RenderTargetBitmap(new PixelSize((int)area.Width, (int)area.Height));
                    using (var ctx = page.CreateDrawingContext())
                        ctx.DrawImage(full, area, new Rect(area.Size));
                    page.Save(path);
                });
                if (File.Exists(path)) written++;
            }
        }
        mp.DiagnosticsOpen = false;
        mp.DrillOpen = false;
        L.SetLanguage("zh-Hans");
        Check("Help shots: every shot written in all four languages", written == HelpLibrary.Languages.Count * shots.Length, $"{written} → {output}");
    }
}
