using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Amber.App.Views;

/// <summary>
/// Image themes (AMBER_SELFTEST_SCOPE=image-theme): Settings › Appearance › Make a theme from pictures with four test
/// pictures made in the report folder, the colours picked from them, the live preview, [Use it] (the four slots show in
/// the real window, the mascot behind the page and gone in a narrow window), [Save as .jaytheme to share], a picture
/// dragged onto the window, and back to the default look. Uses only AMBER_DATA_DIR; no network.
/// </summary>
internal sealed class ImageThemeSelfTest(MainWindow window, MainViewModel main, string reportDir)
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

    private T? Named<T>(string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>A test picture: a two-colour gradient, optionally a figure on a transparent background.</summary>
    private string Picture(string name, int w, int h, SKColor from, SKColor to, bool figure = false, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { IsAntialias = true };
            if (figure)
            {
                paint.Color = from;
                canvas.DrawCircle(w / 2f, h * 0.22f, w * 0.18f, paint);
                paint.Color = to;
                canvas.DrawRoundRect(new SKRect(w * 0.2f, h * 0.38f, w * 0.8f, h), 40, 40, paint);
            }
            else
            {
                paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [from, to], SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, w, h, paint);
            }
        }
        var path = Path.Combine(reportDir, "pictures", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bitmap.Encode(format, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static uint? Brush(string key, ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush b ? b.Color.ToUInt32() : null;

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        try
        {
            Check("自检使用显式独立数据目录", Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 });
            window.Width = 1280;
            window.Height = 800;
            var background = Picture("night.webp", 2400, 1350, new SKColor(0x4A, 0x1C, 0x6B), new SKColor(0xC2, 0x3B, 0x6E), format: SKEncodedImageFormat.Webp);
            var sidebar = Picture("side.jpg", 600, 1400, new SKColor(0x1B, 0x2A, 0x5C), new SKColor(0x6A, 0x3D, 0x9A), format: SKEncodedImageFormat.Jpeg);
            var mascot = Picture("oc.png", 800, 1600, new SKColor(0xF4, 0xC4, 0x30), new SKColor(0xE0, 0x45, 0x7B), figure: true);
            var banner = Picture("banner.png", 2000, 500, new SKColor(0x0F, 0x9D, 0x8A), new SKColor(0x2B, 0x5D, 0xB8));
            var notPicture = Path.Combine(reportDir, "pictures", "notes.png");
            File.WriteAllText(notPicture, "this is not a picture");

            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            main.Shell.Navigate("settings#ImageThemeCard");
            await Task.Delay(800);
            Check("settings#ImageThemeCard 打开外观分组并看得到“用图片做主题”", settings.SelectedGroupId == Amber.Core.Navigation.SettingsCatalog.Appearance
                && Named<Border>("ImageThemeCard") is { IsEffectivelyVisible: true } && Named<Button>("OpenImageThemeButton") is { IsEffectivelyVisible: true });
            Named<Button>("OpenImageThemeButton")!.Command!.Execute(null);
            await Task.Delay(400);
            var vm = settings.ImageTheme;
            var editor = Named<ImageThemeEditor>("ImageThemeEditor");
            Check("面板打开：四个图片位、预览、【用上】还不能点", editor is { IsEffectivelyVisible: true } && vm.Slots.Count == 4
                && Named<Border>("ImageThemePreview") is { IsEffectivelyVisible: true } && Named<Button>("ApplyImageThemeButton") is { IsEffectivelyEnabled: false });
            Check("图片位的名字和说明", vm.Slots.Select(s => s.Title).SequenceEqual([L.T("theme.image.slot.background"), L.T("theme.image.slot.sidebar"),
                L.T("theme.image.slot.mascot"), L.T("theme.image.slot.banner")]) && vm.Slots.All(s => s.Hint.Length > 0));

            // Pictures in: a WebP background (turned into JPEG/PNG), a JPEG sidebar, a transparent PNG mascot, a banner.
            Check("不是图片的文件不放进去，并写原因", !vm.SetPicture(ThemeSlots.Background, notPicture) && !vm.Background.HasPicture
                && vm.Status.Contains(L.T("theme.image.error.unreadable")), vm.Status);
            var allIn = vm.SetPicture(ThemeSlots.Background, background) & vm.SetPicture(ThemeSlots.Sidebar, sidebar)
                & vm.SetPicture(ThemeSlots.Mascot, mascot) & vm.SetPicture(ThemeSlots.Banner, banner);
            await Task.Delay(300);
            Check("四张图都放进去了", allIn && vm.Slots.All(s => s.HasPicture), vm.Status);
            Check("背景缩到 1920 宽，WebP 转成了能放进主题包的格式", vm.Background.Picture is { Width: 1920, Extension: ".jpg" or ".png" },
                $"{vm.Background.Picture?.Width} {vm.Background.Picture?.Extension}");
            Check("立绘保留透明，存成 PNG", vm.Mascot.Picture is { Extension: ".png", Height: 1200 }, $"{vm.Mascot.Picture?.Height}");
            Check("从图里取到颜色，第一个默认选中", vm.Swatches.Count >= 2 && vm.Swatches.Count(s => s.IsSelected) == 1 && vm.Swatches[0].IsSelected,
                string.Join(" ", vm.Swatches.Select(s => s.Name)));
            Check("调节项按图片位出现：背景有明暗和模糊，侧栏有明暗，立绘有大小、透明度和左右，横幅只有位置",
                vm.Background is { ShowsDim: true, ShowsBlur: true, ShowsFocus: true } && vm.Sidebar is { ShowsDim: true, ShowsBlur: false }
                && vm.Mascot is { IsMascot: true, ShowsFocus: false, ShowsDim: false } && vm.Banner is { ShowsDim: false, ShowsBlur: false, ShowsFocus: true });

            // Live preview follows the accent and the knobs.
            var firstAccent = (vm.PreviewAccent as ISolidColorBrush)?.Color.ToUInt32();
            vm.Swatches[1].PickCommand.Execute(null);
            await Task.Delay(200);
            var secondAccent = (vm.PreviewAccent as ISolidColorBrush)?.Color.ToUInt32();
            Check("换强调色，预览马上跟着换", firstAccent != secondAccent && vm.Swatches[1].IsSelected && !vm.Swatches[0].IsSelected);
            vm.Background.Blur = 12;
            vm.Background.Dim = 0.5;
            vm.Mascot.Size = 0.5;
            await Task.Delay(200);
            Check("预览里背景、侧栏、立绘、横幅都画出来了", Named<SlotImage>("PreviewBackground") is { HasPicture: true } && Named<SlotImage>("PreviewMascot") is { HasPicture: true }
                && editor!.GetVisualDescendants().OfType<SlotImage>().Count(s => s.HasPicture) == 4);
            Check("预览里背景按设置模糊", Named<SlotImage>("PreviewBackground")?.Effect is BlurEffect { Radius: > 0 });
            vm.Name = "";
            await Task.Delay(100);
            Check("名字空着时【用上】不能点", Named<Button>("ApplyImageThemeButton") is { IsEffectivelyEnabled: false });
            vm.Name = "自检图片主题";
            await Task.Delay(100);
            await ShotAsync("image-theme-panel-1280");
            window.Width = 820;
            await Task.Delay(500);
            Check("设置栏窄时预览挪到上面", editor!.IsStacked);
            await ShotAsync("image-theme-panel-820");
            window.Width = 1280;
            await Task.Delay(400);

            // [Use it]
            var before = settings.ThemePacks.Count;
            vm.ApplyCommand.Execute(null);
            await Task.Delay(800);
            var current = ThemeService.Current;
            Check("【用上】装好并换过去", current is { Name: "自检图片主题" } && settings.ThemePacks.Count == before + 1 && settings.SelectedThemePack?.Id == current.Id
                && vm.Status == L.F("theme.image.applied", "自检图片主题"), vm.Status);
            Check("主题包里有四个图片位，强调色是选的那个", current is not null && current.Slots.Count == 4 && current.DarkColors.GetValueOrDefault("Accent") == vm.Palette.Accent);
            Check("开始按钮还是 Jay 的琥珀色", Brush("Play", ThemeVariant.Dark) == ThemeDefaults.Dark["Play"]);
            var bg = Named<SlotImage>("ThemeBackground");
            var side = Named<SlotImage>("ThemeSidebar");
            var oc = Named<SlotImage>("ThemeMascot");
            Check("窗口背景和侧栏底图出现了", bg is { HasPicture: true, IsVisible: true, IsHitTestVisible: false } && side is { HasPicture: true, IsHitTestVisible: false });

            main.Shell.Navigate("library");
            await Task.Delay(800);
            oc = Named<SlotImage>("ThemeMascot");
            var page = Named<ContentControl>("PageHost");
            var column = oc?.Parent as Panel;
            Check("立绘在内容区右下角、在页面后面、不接点击", oc is { HasPicture: true, IsHitTestVisible: false } && oc.MascotBounds.Width > 0
                && oc.MascotBounds.Right >= oc.Bounds.Width - 20 && oc.MascotBounds.Bottom >= oc.Bounds.Height - 1
                && column is not null && page is not null && column.Children.IndexOf(oc) < column.Children.IndexOf(page), oc?.MascotBounds.ToString() ?? "");
            var sidebarRight = window.SidebarHost.TranslatePoint(new Point(window.SidebarHost.Bounds.Width, 0), window)?.X ?? 0;
            var mascotLeft = oc?.TranslatePoint(oc.MascotBounds.TopLeft, window)?.X ?? 0;
            Check("立绘碰不到侧栏和隐私行", mascotLeft >= sidebarRight, $"{mascotLeft} ≥ {sidebarRight}");
            Check("游戏库“继续玩”卡片有横幅底图", window.GetVisualDescendants().OfType<SlotImage>().Any(s => s.Slot == ThemeSlots.Banner && s.HasPicture));
            await ShotAsync("image-theme-library-dark");
            settings.SelectedThemeIndex = 1;
            await Task.Delay(600);
            await ShotAsync("image-theme-library-light");
            settings.SelectedThemeIndex = 2;
            await Task.Delay(300);
            window.Width = 640;
            await Task.Delay(600);
            Check("窄窗口里立绘让开", Named<SlotImage>("ThemeMascot") is { MascotBounds.Width: 0 });
            await ShotAsync("image-theme-library-640");
            window.Width = 1280;
            await Task.Delay(400);

            // [Save as .jaytheme to share]: an ordinary pack someone else can import.
            var shared = Path.Combine(reportDir, "shared.jaytheme");
            vm.SaveAs(shared);
            var read = File.Exists(shared) ? ThemePackReader.Read(shared) : null;
            Check("存出来的 .jaytheme 能导入：四个图片位、没有问题、给旧版 Jay 留了背景", read is { Format: 2, Slots.Count: 4, BackgroundImage: not null }
                && read.Problems.Count == 0 && read.Slots[ThemeSlots.Background].Blur == 12, string.Join("; ", read?.Problems.Select(p => p.ToString()) ?? []));

            // A picture dragged onto the window: the drop zones offer the four slots.
            main.Shell.Navigate("library");
            await Task.Delay(400);
            var data = new DataTransfer();
            if (await window.StorageProvider.TryGetFileFromPathAsync(new Uri(banner)) is { } file) data.Add(DataTransferItem.CreateFile(file));
            window.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, window, new Point(10, 10), KeyModifiers.None));
            await Task.Delay(400);
            var drop = main.Drop;
            Check("把图拖进窗口：给出四个图片位", drop.IsActive && drop.Targets.Select(t => t.Label).SequenceEqual(
                [L.T("drop.picture.background"), L.T("drop.picture.sidebar"), L.T("drop.picture.mascot"), L.T("drop.picture.banner")]),
                string.Join(", ", drop.Targets.Select(t => t.Label)));
            vm.Mascot.ClearCommand.Execute(null);
            await drop.DropAsync(drop.Targets[2]);
            await Task.Delay(800);
            Check("放到“做主题立绘”：打开设置里的面板，图放进立绘位", main.CurrentPage is SettingsViewModel && vm.IsOpen && vm.Mascot.FileName == Path.GetFileName(banner)
                && Named<ImageThemeEditor>("ImageThemeEditor") is { IsEffectivelyVisible: true });
            drop.End();

            // Back to the default look: the slots are empty again.
            settings.SelectedThemePack = settings.ThemePacks.First(o => o.IsDefault);
            await Task.Delay(800);
            Check("换回默认外观后四个图片位都空了", ThemeService.SlotPicture(ThemeSlots.Background) is null && Named<SlotImage>("ThemeBackground") is { IsVisible: false }
                && Named<SlotImage>("ThemeSidebar") is { HasPicture: false });
            vm.CloseCommand.Execute(null);
            await Task.Delay(200);
            Check("【收起】后只留打开按钮", !vm.IsOpen && Named<Button>("OpenImageThemeButton") is { IsEffectivelyVisible: true });
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
}
