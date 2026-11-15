using Amber.App.Services;
using Amber.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=dock-density (UI batch 8e): Ctrl+` docks the game log at the bottom on any page (240 high,
/// draggable), it pops out into its own window and docks back, a short window turns it into a drawer that Esc closes;
/// density compact tightens controls in every Jay window but never the locked safety surfaces. Screenshots of both
/// densities. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task DockDensityAsync()
    {
        var log = main.Log;
        var settings = Page<SettingsViewModel>();
        try
        {
            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("library");
            await Task.Delay(300);
            Dispatcher.UIThread.Invoke(() =>
            {
                for (var i = 0; i < 30; i++) log.AddRaw($"[Render thread/INFO]: line {i}");
                log.AddRaw("[Render thread/ERROR]: Something broke in a mod");
            });
            await Task.Delay(300);
            Check("默认不停靠，日志视图还没建（第一次停靠才建）", !main.LogDocked && Dispatcher.UIThread.Invoke(() => !window.LogDock.IsVisible && window.LogDock.Child is null));
            PressKey(window, Key.OemTilde, KeyModifiers.Control);
            await Task.Delay(400);
            Check("Ctrl+`：日志停靠到内容区底部，高 240", main.LogDocked && Dispatcher.UIThread.Invoke(() => window.LogDock.IsEffectivelyVisible
                && Grid.GetRow(window.LogDock) == 6 && Math.Abs(window.LogDock.Bounds.Height - 240) < 1 && window.LogDockSplitter.IsVisible && !window.LogDockIsDrawer),
                Dispatcher.UIThread.Invoke(() => window.LogDock.Bounds.Height.ToString("0")));
            Check("停靠面板里是正在跑的日志，有错误计数", log.Lines.Count >= 31 && log.HasErrors);
            await ShotAsync("log-docked-1440");

            Dispatcher.UIThread.Invoke(() => window.LogDockSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, -60) }));
            await Task.Delay(200);
            Check("拖分隔条：变高", Dispatcher.UIThread.Invoke(() => Math.Abs(window.LogDock.Height - 300) < 1), Dispatcher.UIThread.Invoke(() => window.LogDock.Height.ToString("0")));

            main.Shell.Navigate("discover");
            await Task.Delay(400);
            Check("换页面：停靠还在", main.LogDocked && Dispatcher.UIThread.Invoke(() => window.LogDock.IsEffectivelyVisible));
            main.Shell.Navigate("library");
            await Task.Delay(200);

            // Pop out, then dock back.
            main.PopOutLog();
            await Task.Delay(500);
            Check("弹出成独立窗口，底部停靠收起", Dispatcher.UIThread.Invoke(() => window.PoppedOutLog is { IsVisible: true }) && !main.LogDocked);
            Dispatcher.UIThread.Invoke(() =>
            {
                var back = window.PoppedOutLog!.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == Amber.Core.Localization.L.T("log.dock.dock_back"));
                back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            });
            await Task.Delay(400);
            Check("独立窗口里点“停靠到底部”：窗口关上，回到底部", main.LogDocked && Dispatcher.UIThread.Invoke(() => window.PoppedOutLog is null));

            // Short window: a drawer over the page; Esc closes it.
            window.Width = 900;
            window.Height = 520;
            await Task.Delay(600);
            Check("矮窗口：停靠变成盖在页面上的抽屉", Dispatcher.UIThread.Invoke(() => window.LogDockIsDrawer && Grid.GetRow(window.LogDock) == 4 && !window.LogDockSplitter.IsVisible));
            await ShotAsync("log-drawer-900x520");
            PressKey(window, Key.Escape);
            Check("Esc 收起抽屉", !main.LogDocked);
            window.Width = 1440;
            window.Height = 900;
            await Task.Delay(500);
            main.LogDocked = true;
            await Task.Delay(200);
            PressKey(window, Key.OemTilde, KeyModifiers.Control);
            Check("再按 Ctrl+` 取消停靠", !main.LogDocked);

            // Density.
            main.Shell.Navigate("about");
            await Task.Delay(500);
            double SafeButton() => Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DataFolderButton").Bounds.Height);
            var safeStandard = SafeButton();
            main.Shell.Navigate("settings#Density");
            await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "DensityBox"), 3);
            var themeBox = Dispatcher.UIThread.Invoke(() => window.PageHost.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ThemeBox"));
            var standardHeight = Dispatcher.UIThread.Invoke(() => themeBox.Bounds.Height);
            await ShotAsync("density-standard-settings");
            settings.SelectedDensityIndex = 1;
            await Task.Delay(400);
            var compactHeight = Dispatcher.UIThread.Invoke(() => themeBox.Bounds.Height);
            Check("紧凑：控件变矮，窗口带 density-compact", compactHeight < standardHeight - 3 && Dispatcher.UIThread.Invoke(() => window.Classes.Contains("density-compact")
                && window.Resources.TryGetResource("ControlHeight", null, out var h) && h is 30.0), $"{standardHeight:0} → {compactHeight:0}");
            Check("紧凑存进设置", File.ReadAllText(AppSettings.FilePath).Contains("\"Density\": \"compact\"", StringComparison.Ordinal));
            await ShotAsync("density-compact-settings");
            main.Shell.Navigate("about");
            await Task.Delay(500);
            var safeCompact = SafeButton();
            Check("紧凑不改锁定的安全界面（关于和隐私的按钮高度不变）", Math.Abs(safeCompact - safeStandard) < 0.5, $"{safeStandard:0} → {safeCompact:0}");
            main.Shell.Navigate("library");
            await Task.Delay(400);
            await ShotAsync("density-compact-library");
            settings.SelectedDensityIndex = 0;
            await Task.Delay(300);
            Check("改回标准：去掉覆盖", Dispatcher.UIThread.Invoke(() => !window.Classes.Contains("density-compact") && !window.Resources.ContainsKey("ControlHeight")));
        }
        finally
        {
            main.LogDocked = false;
            settings.SelectedDensityIndex = 0;
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
