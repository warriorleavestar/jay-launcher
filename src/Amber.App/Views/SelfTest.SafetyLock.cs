using Amber.App.Controls.Safety;
using Amber.App.Services;
using Amber.App.ViewModels;
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
            // Settings › Multiplayer's [Reset all to defaults] question, as the real view shows it.
            var mpView = new MultiplayerSettingsView { DataContext = settings, Width = 560, HorizontalAlignment = HorizontalAlignment.Left };
            settings.ConfirmingAdvancedReset = true;
            var stage = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { new Border { Classes = { "card" }, Child = panel }, row, mpView } };
            window.Content = stage;
            Check("安全面板标题行写的是 Jay 安全提示", panel.BadgeText == Amber.Core.Localization.L.T("safety.title"), panel.BadgeText);
            await Task.Delay(300);
            Check("设置 › 联机【全部恢复默认】的确认是锁定的安全面板，按钮是安全按钮，焦点在【取消】",
                mpView.MultiplayerResetConfirm is SafetyPanel && mpView.MultiplayerResetConfirm.IsEffectivelyVisible
                    && mpView.MultiplayerResetCancel.GetType() == typeof(SafetyButton) && mpView.MultiplayerResetDo.GetType() == typeof(SafetyButton)
                    && mpView.MultiplayerResetCancel.IsFocused);

            var baseline = new Dictionary<string, byte[]>();
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                foreach (var (name, bytes) in await CaptureAsync(panel, row, mpView.MultiplayerResetConfirm, $"default-{variant}")) baseline[$"{variant}/{name}"] = bytes;
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
                    foreach (var (name, bytes) in await CaptureAsync(panel, row, mpView.MultiplayerResetConfirm, $"{extreme}-{variant}"))
                    {
                        var same = baseline.TryGetValue($"{variant}/{name}", out var b) && b.AsSpan().SequenceEqual(bytes);
                        Check($"极端主题 {extreme}（{variant}）下 {name} 和默认逐像素一致", same);
                    }
                }
            }

            // Esc in the question cancels; nothing is reset.
            var customizedBefore = settings.AnyAdvancedCustomized;
            await Dispatcher.UIThread.InvokeAsync(() => mpView.MultiplayerResetCancel.RaiseEvent(new Avalonia.Input.KeyEventArgs
                { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape }));
            await Task.Delay(200);
            Check("【全部恢复默认】的确认按 Esc 等于取消，什么都不改", !settings.ConfirmingAdvancedReset && settings.AnyAdvancedCustomized == customizedBefore);
            settings.ConfirmingAdvancedReset = true;
            window.Content = original;
            await Task.Delay(200);
            Check("离开页面也等于取消", !settings.ConfirmingAdvancedReset && settings.AnyAdvancedCustomized == customizedBefore);

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

    private async Task<List<(string Name, byte[] Pixels)>> CaptureAsync(Control panel, Control row, Control resetConfirm, string tag)
    {
        await Task.Delay(300);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var result = new List<(string, byte[])>();
            foreach (var (name, control) in new[] { ("安全面板", panel), ("隐私行", row), ("联机设置的恢复默认确认", resetConfirm) })
            {
                var size = new PixelSize((int)Math.Max(1, control.Bounds.Width), (int)Math.Max(1, control.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(control);
                var bytes = new byte[size.Width * size.Height * 4];
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, size.Width * 4); }
                finally { handle.Free(); }
                bmp.Save(Path.Combine(reportDir, $"{tag}-{(name == "安全面板" ? "panel" : name == "隐私行" ? "privacy" : "mp-reset")}.png"));
                result.Add((name, bytes));
            }
            return result;
        });
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
