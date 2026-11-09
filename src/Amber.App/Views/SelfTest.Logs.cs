using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Launch;
using Amber.Core.Localization;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=logs (UI batch 7c): Instance › Logs in a throwaway game directory inside the report folder with
/// two saved runs. Run list newest first (column when wide, picker otherwise), the diagnosis line for a crash, stack
/// traces folded and repeats merged, filters and search, F8 jumps to errors, the locked privacy preview before
/// exporting, and the report itself has no user name. Screenshots at three widths in two languages. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task LogsTabAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "logs-game");
        const string id = "1.20.1";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        try
        {
            WriteLibraryVersion(game, id, "[]");
            service.Settings.Instances = new() { [id] = new() { DisplayName = "日志测试" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var work = service.WorkDirFor(id);
            var t0 = DateTimeOffset.Now.AddDays(-1);
            using (var ok = RunLogStore.Begin(work, t0, noTrace: false))
            {
                ok.Add(t0, GameLogLevel.Info, "Loading 12 mods");
                ok.Add(t0.AddSeconds(30), GameLogLevel.Info, "Stopping!");
                ok.Finish(t0.AddHours(2), RunOutcome.Exited, 0);
            }
            var t1 = DateTimeOffset.Now.AddMinutes(-30);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using (var crash = RunLogStore.Begin(work, t1, noTrace: false))
            {
                crash.Add(t1, GameLogLevel.Info, "[Jay] Launching 1.20.1", fromJay: true);
                crash.Add(t1.AddSeconds(1), GameLogLevel.Info, "Loading 87 mods from " + Path.Combine(home, "games", "mods"));
                for (var i = 0; i < 3; i++) crash.Add(t1.AddSeconds(2), GameLogLevel.Warn, "[Iris] Shader pack not found");
                crash.Add(t1.AddSeconds(5), GameLogLevel.Error, "Mixin apply failed for sodium.mixins.json");
                for (var i = 0; i < 42; i++) crash.Add(t1.AddSeconds(5), GameLogLevel.Info, $"\tat org.spongepowered.asm.mixin.Step{i}(Mixin.java:{i})");
                crash.Add(t1.AddSeconds(6), GameLogLevel.Error, "Failed to start the game");
                crash.Add(t1.AddSeconds(7), GameLogLevel.Info, "[Jay] The game crashed, analysing…", fromJay: true);
                crash.Finish(t1.AddSeconds(8), RunOutcome.Crashed, -1, "OptiFine and Sodium don't work together");
            }

            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1440;
            window.Height = 900;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            instance.TabIndex = (int)InstanceTab.Logs;
            await WaitAsync(() => instance.LogRuns.Count == 2, 10);
            await Task.Delay(500);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().First();
            Check("运行记录：两局，新的在前，崩溃标 ✕", instance.LogRuns[0].Record.Outcome == RunOutcome.Crashed && instance.LogRuns[0].Glyph == "✕"
                && instance.LogRuns[1].Glyph == "✓", string.Join(" | ", instance.LogRuns.Select(r => r.Glyph + r.Label)));
            Check("宽窗口：运行记录是左栏，没有下拉", Dispatcher.UIThread.Invoke(() => page.LogRunsPane.IsEffectivelyVisible && !page.LogRunPicker.IsVisible));
            Check("崩溃那局：顶部写诊断", instance.HasLogDiagnosis && instance.LogDiagnosis.Contains("Sodium"), instance.LogDiagnosis);
            var stack = instance.LogRows.FirstOrDefault(r => r.HasStack);
            Check("堆栈收成一行“▸ 42 行堆栈”", stack is not null && stack.Event.Stack.Count == 42 && stack.StackLabel.Contains("42"), stack?.StackLabel ?? "");
            Check("重复的警告合成一行 ×3", instance.LogRows.Any(r => r.HasRepeats && r.RepeatsLabel == "×3"));
            Check("JAY 行单独标出", instance.LogRows.Count(r => r.IsJay) == 2);
            Check("错误和警告计数", instance.LogErrorCount == 2 && instance.LogWarningCount == 3, instance.LogErrorBadge + " " + instance.LogWarningBadge);
            await ShotAsync("logs-1440");

            instance.ToggleStackCommand.Execute(stack);
            Check("点开堆栈", stack!.Expanded && stack.StackLabel.Contains("收起"));
            Dispatcher.UIThread.Invoke(() => page.Focus());
            PressKey(window, Key.F8);
            var first = instance.SelectedLogRow;
            PressKey(window, Key.F8);
            Check("F8：在错误之间跳，跳了就不再跟随", first?.IsError == true && instance.SelectedLogRow?.IsError == true && instance.SelectedLogRow != first && !instance.LogFollow);
            PressKey(window, Key.F8, KeyModifiers.Shift);
            Check("Shift+F8：上一个错误", instance.SelectedLogRow == first);

            instance.LogFilterIndex = 2;
            Check("只看错误", instance.LogRows.Count == 2 && instance.LogRows.All(r => r.IsError));
            instance.LogFilterIndex = 1;
            Check("警告和错误", instance.LogRows.Count == 3);
            instance.LogFilterIndex = 0;
            instance.LogSearch = "shader";
            Check("搜索", instance.LogRows.Count == 1 && instance.LogRows[0].Message.Contains("Shader"));
            instance.LogSearch = "没有这一行";
            Check("搜不到时写明", instance.LogEmpty && instance.LogEmptyText.Contains("没有符合"), instance.LogEmptyText);
            instance.LogSearch = "";

            // Export: the locked privacy preview first.
            var export = instance.ExportErrorReportCommand.ExecuteAsync(null);
            await WaitAsync(() => SafetyDialog.Current is not null, 5);
            var dialog = SafetyDialog.Current;
            var texts = Dispatcher.UIThread.Invoke(() => dialog is null ? "" : string.Join(" ", dialog.Panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
            Check("【导出错误报告…】先给锁定的隐私预览：写明有什么、去掉了什么", texts.Contains("最后 2000 行") && texts.Contains("已经替你去掉") && dialog?.AlternateButton is not null, texts);
            await Task.Delay(300);
            Dispatcher.UIThread.Invoke(() => (ControlAutomationPeer.CreatePeerForElement(dialog!.KeepButton) as IInvokeProvider)?.Invoke());
            await export;
            var report = instance.BuildErrorReport();
            Check("报告里没有电脑用户名，有模组列表和日志", report.Length > 0 && !report.Contains(home, StringComparison.OrdinalIgnoreCase)
                && report.Contains("Mixin apply failed") && report.Contains("1.20.1"), report.Length.ToString());

            window.Width = 900;
            window.Height = 760;
            await Task.Delay(600);
            Check("中等宽度：运行记录变成顶部下拉", Dispatcher.UIThread.Invoke(() => !page.LogRunsPane.IsVisible && page.LogRunPicker.IsEffectivelyVisible));
            await ShotAsync("logs-900");
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            await ShotAsync("logs-600");
            L.SetLanguage("en");
            await Task.Delay(400);
            await ShotAsync("logs-600-en");
            L.SetLanguage("zh-Hans");
        }
        finally
        {
            L.SetLanguage("zh-Hans");
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
