using Amber.App.ViewModels;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=gallery（新界面第 0 批）：控件库每一格在深浅两色下截图；现有每一页在 1024×700 深浅截图；
/// 检查用到的颜色都来自令牌、每屏琥珀按钮不超过一个、图标都解析得到。不碰游戏目录，不联网。
/// </summary>
internal sealed partial class SelfTest
{
    private async Task GalleryAsync()
    {
        var original = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                var name = variant == ThemeVariant.Dark ? "夜" : "晨";
                Application.Current.RequestedThemeVariant = variant;
                var gallery = new Gallery();
                var host = new Window { Width = 1100, Height = 2200, Content = gallery, ShowInTaskbar = false };
                host.Show();
                await Task.Delay(600);
                Check($"控件库（{name}）：图标都解析得到", gallery.IconCount >= 60, $"{gallery.IconCount} 个");
                var plays = gallery.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
                Check($"控件库（{name}）：琥珀按钮只有一个", plays == 1, $"{plays} 个");
                // 琥珀按钮用的是 Play 令牌，不是强调色。
                var play = gallery.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("play"));
                var playColor = (play.Background as ISolidColorBrush)?.Color.ToUInt32();
                var palette = variant == ThemeVariant.Dark ? ThemeDefaults.Dark : ThemeDefaults.Light;
                Check($"控件库（{name}）：开始游戏用琥珀，主按钮用松鸦蓝", playColor == palette["Play"],
                    $"开始游戏 {playColor:X8}，令牌 {palette["Play"]:X8}");
                var primary = gallery.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary") && b.IsEnabled);
                Check($"控件库（{name}）：主按钮是松鸦蓝", (primary.Background as ISolidColorBrush)?.Color.ToUInt32() == palette["Accent"]);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var size = new PixelSize((int)host.Bounds.Width, (int)Math.Min(host.Bounds.Height, gallery.GetVisualDescendants().OfType<StackPanel>().First().Bounds.Height + 40));
                    using var bitmap = new RenderTargetBitmap(size);
                    bitmap.Render(host);
                    bitmap.Save(Path.Combine(reportDir, $"gallery-{(variant == ThemeVariant.Dark ? "dark" : "light")}.png"));
                });
                host.Close();

                // 现有每一页：换上新颜色以后在 1024×700 截图，琥珀按钮每屏最多一个。
                window.Width = 1024;
                window.Height = 700;
                foreach (var page in main.Pages)
                {
                    main.CurrentPage = page;
                    await Task.Delay(500);
                    var amber = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("play") && b.IsEffectivelyVisible);
                    Check($"{page.GetType().Name}（{name}）：琥珀按钮每屏最多一个", amber <= 1, $"{amber} 个");
                    await ShotAsync($"{(variant == ThemeVariant.Dark ? "dark" : "light")}-{page.GetType().Name}");
                }
            }
        }
        finally
        {
            Application.Current.RequestedThemeVariant = original;
        }
    }
}
