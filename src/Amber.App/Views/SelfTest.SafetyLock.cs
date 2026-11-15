using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// 安全外观锁定自检（AMBER_SELFTEST_SCOPE=safety-lock，新界面第 1 批，官方设计 §10.7）：
/// 一组极端主题（全黑、全白、全品红、字号最小、圆角 64 + 组件样式全改）逐个导入，
/// 安全确认框、安全面板、安全按钮、隐私行在深浅两色下和默认主题逐像素一致；带保留词的主题包被拒绝；
/// 每个安全窗口都调用过 LockToDefault。只用 AMBER_DATA_DIR 指向的独立数据目录，不碰游戏目录，不联网。
/// </summary>
internal sealed class SafetyLockSelfTest(MainWindow window, MainViewModel main, string reportDir)
{
    private readonly List<string> _report = [];

    private void Check(string name, bool ok, string detail = "")
    {
        _report.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
    }

    private static readonly string[] Extremes = ["black", "white", "magenta", "tiny", "round"];

    public async Task RunAsync()
    {
        Directory.CreateDirectory(reportDir);
        var original = window.Content;
        try
        {
            Check("自检使用显式独立数据目录", Environment.GetEnvironmentVariable("AMBER_DATA_DIR") is { Length: > 0 });
            var settings = main.Pages.OfType<SettingsViewModel>().Single();

            // 在主窗口里摆一组安全组件，外面套一张普通卡片（主题的 card 样式会作用在它外面）。
            var panel = new SafetyPanel
            {
                Title = "删除实例“生存服”？",
                Body = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "这一步做完就找不回来了。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                        {
                            new SafetyButton { Content = "保留", Kind = SafetyKind.Safe },
                            new SafetyButton { Content = "确定", Kind = SafetyKind.Confirm },
                            new SafetyButton { Content = "永久删除", Kind = SafetyKind.Danger },
                        } },
                    },
                },
                Width = 460,
            };
            var row = new PrivacyRow();
            // The friends page's two locked confirms, as the page builds them, with a long name and device name and as
            // wide as a 600×420 window leaves them: a new device their account vouched for, and removing a friend.
            var (newDevice, removeFriend) = FriendPanels();
            var stage = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 16,
                    Children = { new Border { Classes = { "card" }, Child = panel }, row, new Border { Classes = { "card" }, Child = newDevice }, new Border { Classes = { "card" }, Child = removeFriend } },
                },
            };
            window.Content = stage;
            Check("安全面板标题行写的是 Jay 安全提示", panel.BadgeText == Amber.Core.Localization.L.T("safety.title"), panel.BadgeText);
            _locked = [("安全面板", panel), ("隐私行", row), ("朋友新设备", newDevice), ("移除朋友", removeFriend)];
            await Task.Delay(300);
            foreach (var (name, friendPanel) in new[] { ("朋友新设备", newDevice), ("移除朋友", removeFriend) })
            {
                var buttons = Dispatcher.UIThread.Invoke(() => friendPanel.GetVisualDescendants().OfType<SafetyButton>().ToList());
                Check($"{name}的确认是安全面板，按钮是安全按钮，安全的那个排第一",
                    buttons.Count == 2 && buttons[0].Kind == SafetyKind.Safe && buttons[1].Kind == SafetyKind.Confirm);
                Check($"{name}在 600×420 窗口的宽度里放得下（长名字也不裁掉按钮）", Dispatcher.UIThread.Invoke(() => buttons.All(b =>
                    b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), friendPanel) is { } corner && corner.X <= friendPanel.Bounds.Width + 0.5 && corner.Y <= friendPanel.Bounds.Height + 0.5)));
            }

            var baseline = new Dictionary<string, byte[]>();
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                foreach (var (name, bytes) in await CaptureAsync($"default-{variant}")) baseline[$"{variant}/{name}"] = bytes;
            }

            foreach (var extreme in Extremes)
            {
                var path = Path.Combine(reportDir, $"extreme-{extreme}.jaytheme");
                var (tokens, styles) = Pack(extreme);
                ThemeSelfTest.WritePack(path, $$"""{ "id": "selftest-extreme-{{extreme}}", "name": "极端 {{extreme}}" }""", tokens, ("styles.json", styles));
                settings.ImportThemePack(path);
                await Task.Delay(500);
                Check($"极端主题 {extreme} 已套用", ThemeService.Current?.Id == $"selftest-extreme-{extreme}", ThemeService.Current?.Id ?? "(默认)");
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    foreach (var (name, bytes) in await CaptureAsync($"{extreme}-{variant}"))
                    {
                        var same = baseline.TryGetValue($"{variant}/{name}", out var b) && b.AsSpan().SequenceEqual(bytes);
                        Check($"极端主题 {extreme}（{variant}）下 {name} 和默认逐像素一致", same);
                    }
                }
            }

            // A theme's sidebar picture can't show through the privacy row: the real window, a noisy sidebar picture
            // with no cover, both colour modes, a wide and the smallest window; the composited pixels of the row match.
            window.Content = original;
            var noisePack = Path.Combine(reportDir, "noise-sidebar.jaytheme");
            ImageTheme.Write(noisePack, "selftest-noise-sidebar", "Noise sidebar", ImageTheme.Extract([]),
                new Dictionary<string, ImageThemeSlot> { [ThemeSlots.Sidebar] = new(new ThemeSlot { Image = "", Dim = 0 }, NoisePng(480, 1400), ".png") });
            var sidebarRow = window.GetVisualDescendants().OfType<PrivacyRow>().First(r => r.Name == "PrivacyRow");
            var (windowWidth, windowHeight) = (window.ClientSize.Width, window.ClientSize.Height);
            foreach (var (w, h) in new[] { (1280, 800), (600, 420) })
            {
                window.Width = w;
                window.Height = h;
                settings.ResetThemePackCommand.Execute(null);
                await Task.Delay(600);
                var plain = new Dictionary<ThemeVariant, (byte[] Row, byte[] Above)>();
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    plain[variant] = await RowPixelsAsync(sidebarRow, $"privacy-row-{w}x{h}-default-{variant}");
                }
                settings.ImportThemePack(noisePack);
                await Task.Delay(800);
                foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current!.RequestedThemeVariant = variant;
                    var (rowNow, aboveNow) = await RowPixelsAsync(sidebarRow, $"privacy-row-{w}x{h}-noise-{variant}");
                    Check($"{w}×{h}（{variant}）：侧栏图片确实画出来了（隐私行上面一块跟默认不一样）", !aboveNow.AsSpan().SequenceEqual(plain[variant].Above));
                    Check($"{w}×{h}（{variant}）：侧栏图片透不过隐私行，整窗截图里这一行和默认外观逐像素一致", rowNow.AsSpan().SequenceEqual(plain[variant].Row));
                }
            }
            settings.ResetThemePackCommand.Execute(null);
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            // Back to the size the baseline was taken at, so the later comparisons see the same layout.
            (window.Width, window.Height) = (windowWidth, windowHeight);
            window.Content = stage;
            await Task.Delay(300);

            // 带保留词的主题包：名字、作者、布局文字里有“Jay 安全提示”都整包拒绝。
            var reserved = Path.Combine(reportDir, "reserved.jaytheme");
            ThemeSelfTest.WritePack(reserved, """{ "id": "selftest-reserved", "name": "Jay 安全提示" }""", "{}");
            var before = ThemeService.Current?.Id;
            settings.ImportThemePack(reserved);
            await Task.Delay(300);
            Check("名字冒充“Jay 安全提示”的主题包被拒绝", !Directory.Exists(Path.Combine(ThemeService.Store.Root, "selftest-reserved")) && ThemeService.Current?.Id == before);

            // 恢复默认，再看一次确认框的窗口是锁定的。
            settings.ResetThemePackCommand.Execute(null);
            await Task.Delay(300);
            // Compact density makes ordinary controls shorter; the locked ones stay exactly as they were.
            settings.SelectedDensityIndex = 1;
            await Task.Delay(300);
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                foreach (var (name, bytes) in await CaptureAsync($"compact-{variant}"))
                    Check($"紧凑密度（{variant}）下 {name} 和标准密度逐像素一致", baseline.TryGetValue($"{variant}/{name}", out var b) && b.AsSpan().SequenceEqual(bytes));
            }
            settings.SelectedDensityIndex = 0;
            await Task.Delay(300);
            var dialog = SafetyDialog.CreateForSelfTest("删除实例“生存服”？", "这一步做完就找不回来了。", "永久删除", ["12 个世界", "3 张截图"]);
            dialog.Show(window);
            await Task.Delay(400);
            Check("安全确认框调用过 LockToDefault", ThemeService.IsLocked(dialog));
            Check("安全确认框默认焦点在“保留 / 取消”", dialog.KeepButton.IsFocused || dialog.FocusManager?.GetFocusedElement() == dialog.KeepButton);
            Check("安全按钮不是普通按钮的样式（主题的 Button 选择器匹配不到）", !dialog.ConfirmButton.Classes.Contains("primary") && dialog.ConfirmButton.GetType() == typeof(SafetyButton));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Max(1, dialog.Panel.Bounds.Width), (int)Math.Max(1, dialog.Panel.Bounds.Height)));
                bmp.Render(dialog.Panel);
                bmp.Save(Path.Combine(reportDir, "safety-dialog.png"));
            });
            dialog.Close();
            var consent = new RemoteConsentDialog(new Amber.Core.RemoteControl.RemoteConsentRequest("自检", "0000"), TimeSpan.FromSeconds(30));
            Check("远程控制同意窗调用过 LockToDefault", ThemeService.IsLocked(consent));
            var deny = consent.GetLogicalDescendants().OfType<SafetyButton>().FirstOrDefault();
            Check("远程控制同意窗的按钮是安全按钮", deny is not null);
            consent.Close();
        }
        catch (Exception e)
        {
            Check("自检没有抛异常", false, e.ToString());
        }
        finally
        {
            window.Content = original;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            _report.Add("DONE");
            File.WriteAllLines(Path.Combine(reportDir, "report.txt"), _report);
            window.Close();
        }
    }

    private (string Name, Control Control)[] _locked = [];

    /// <summary>The friends page's new-device and remove-friend panels, built the way PeoplePage.axaml builds them.</summary>
    private static (SafetyPanel NewDevice, SafetyPanel RemoveFriend) FriendPanels()
    {
        const string name = "Robin the Very Long Named Friend From Far Away";
        const string device = "Robin's second laptop with a long device name";
        static SafetyPanel Locked(string title, Control? text, string safe, string confirm)
        {
            var body = new StackPanel { Spacing = 6 };
            if (text is not null) body.Children.Add(text);
            body.Children.Add(new WrapPanel
            {
                ItemSpacing = 8,
                LineSpacing = 6,
                Children = { new SafetyButton { Content = safe, Kind = SafetyKind.Safe }, new SafetyButton { Content = confirm, Kind = SafetyKind.Confirm } },
            });
            return new SafetyPanel { Title = title, Body = body, Width = 552 - 48 };
        }
        var newDevice = Locked(L.F("multiplayer.contacts.new_device", name),
            new SelectableTextBlock { Text = L.F("multiplayer.contacts.new_device_body", device, "ABCD-EFGH-IJKL-MNOP"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            L.T("multiplayer.contacts.new_device_remove"), L.T("multiplayer.contacts.new_device_ok"));
        var removeFriend = Locked(L.F("people.safety.remove_confirm", name), null, L.T("people.safety.remove_keep"), L.T("people.safety.remove_yes"));
        return (newDevice, removeFriend);
    }

    private async Task<List<(string Name, byte[] Pixels)>> CaptureAsync(string tag)
    {
        await Task.Delay(300);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var result = new List<(string, byte[])>();
            foreach (var (name, control) in _locked)
            {
                var size = new PixelSize((int)Math.Max(1, control.Bounds.Width), (int)Math.Max(1, control.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(control);
                var bytes = new byte[size.Width * size.Height * 4];
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, size.Width * 4); }
                finally { handle.Free(); }
                bmp.Save(Path.Combine(reportDir, $"{tag}-{Array.FindIndex(_locked, l => l.Name == name)}.png"));
                result.Add((name, bytes));
            }
            return result;
        });
    }

    /// <summary>
    /// Renders the whole window and returns the privacy row's pixels (inset past its rounded corners) and a strip of the
    /// sidebar just above it; also saves the window.
    /// </summary>
    private async Task<(byte[] Row, byte[] Above)> RowPixelsAsync(PrivacyRow row, string tag)
    {
        await Task.Delay(400);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bmp = new RenderTargetBitmap(size);
            bmp.Render(window);
            bmp.Save(Path.Combine(reportDir, tag + ".png"));
            var all = new byte[size.Width * size.Height * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(all, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), all.Length, size.Width * 4); }
            finally { handle.Free(); }
            var origin = row.TranslatePoint(new Point(0, 0), window) ?? default;
            var inner = new PixelRect((int)Math.Ceiling(origin.X) + 4, (int)Math.Ceiling(origin.Y) + 4,
                Math.Max(1, (int)row.Bounds.Width - 9), Math.Max(1, (int)row.Bounds.Height - 9));
            var above = new PixelRect(inner.X, Math.Max(0, (int)origin.Y - 40), inner.Width, 30);
            byte[] cut(PixelRect r)
            {
                var result = new byte[r.Width * r.Height * 4];
                for (var y = 0; y < r.Height; y++)
                    Array.Copy(all, ((r.Y + y) * size.Width + r.X) * 4, result, y * r.Width * 4, r.Width * 4);
                return result;
            }
            return (cut(inner), cut(above));
        });
    }

    /// <summary>A sidebar picture of hard-edged colour blocks, the worst case behind small text.</summary>
    private static byte[] NoisePng(int width, int height)
    {
        var random = new Random(7);
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            for (var y = 0; y < height; y += 6)
                for (var x = 0; x < width; x += 6)
                    using (var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)) })
                        canvas.DrawRect(x, y, 6, 6, paint);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (string Tokens, string Styles) Pack(string kind)
    {
        string all(string hex) => "{ " + string.Join(", ", ThemePackReader.ColorKeys.Select(k => $"\"{k}\": \"{hex}\"")) + " }";
        const string wild = """
            { "button.primary": { "background": "#FF00FF", "foreground": "#00FF00", "radius": 64, "padding": [40, 20] },
              "card": { "background": "#00FFFF", "radius": 64, "borderWidth": 8 },
              "input": { "radius": 64 } }
            """;
        return kind switch
        {
            "black" => ($$"""{ "colors": { "both": {{all("#000000")}} } }""", "{}"),
            "white" => ($$"""{ "colors": { "both": {{all("#FFFFFF")}} } }""", "{}"),
            "magenta" => ($$"""{ "colors": { "both": {{all("#FF00FF")}} } }""", wild),
            "tiny" => ("""{ "sizes": { "FontCaption": 11, "FontBodySmall": 11, "FontBody": 12, "FontSubtitle": 12, "FontTitle": 14, "FontDisplay": 16 } }""", "{}"),
            _ => ("""{ "radius": { "RadiusSm": 32, "RadiusMd": 48, "RadiusLg": 64, "RadiusPill": 999 }, "spacing": { "Space4": 80 } }""", wild),
        };
    }
}
