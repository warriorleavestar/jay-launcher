using Amber.App.ViewModels;
using Amber.Core.Content;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=fix (UI batch 7d): [Help me fix…] halving the mods, in a throwaway game directory inside the
/// report folder with seven placeholder mod files. One of them plays the crashing mod: the check answers "still crashes"
/// whenever it is on. The card shows the round and what to do, comes back after switching away (as after a restart),
/// finds the mod, [Turn off X] leaves only that one off, and [Restore everything] in the middle puts every mod back.
/// Screenshots wide and narrow. Nothing goes online and the game is never started.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task FixWizardAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "fix-game");
        const string id = "1.20.1";
        const string other = "1.19.4";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        try
        {
            WriteLibraryVersion(game, id, "[]");
            WriteLibraryVersion(game, other, "[]");
            service.Settings.Instances = new() { [id] = new() { DisplayName = "修复测试" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var mods = Directory.CreateDirectory(Path.Combine(service.WorkDirFor(id), "mods")).FullName;
            string[] names = ["alpha.jar", "bravo.jar", "charlie.jar", "delta.jar", "echo.jar", "foxtrot.jar", "golf.jar"];
            foreach (var name in names) File.WriteAllText(Path.Combine(mods, name), name);
            const string culprit = "echo.jar";
            bool Crashes() => File.Exists(Path.Combine(mods, culprit));

            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1440;
            window.Height = 900;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().First();
            Check("【帮我修…】在“上一局”面板里", Dispatcher.UIThread.Invoke(() => page.HelpMeFixButton.Command == instance.StartFixCommand));

            instance.StartFixCommand.Execute(null);
            Check("开始：先关掉一半（3 个），卡片写第几轮、怎么做", instance.HasBisect && instance.Bisect!.DisabledNow.Count == 3
                && instance.BisectTitle.Contains("第 1 轮") && instance.BisectBody.Contains("关掉了 3 个") && instance.BisectBody.Contains("还有 7 个") && instance.BisectAwaiting
                && Directory.EnumerateFiles(mods, "*.jar.disabled").Count() == 3, instance.BisectTitle + " / " + instance.BisectBody);
            await Task.Delay(300);
            await ShotAsync("fix-round1-1440");

            // As after a restart: switch to another instance and back, the card comes back from the instance's file.
            instance.SelectedId = other;
            await WaitAsync(() => instance.Current?.Id == other, 5);
            Check("换到别的实例：没有这张卡", !instance.HasBisect);
            instance.SelectedId = id;
            await WaitAsync(() => instance.Current?.Id == id, 5);
            Check("回来（等于重启后）：接着上次的进度", instance.HasBisect && instance.Bisect!.Round == 1 && instance.BisectAwaiting);

            var rounds = 0;
            while (!instance.BisectFound && rounds++ < 6)
                instance.BisectAnswerCommand.Execute(Crashes() ? "true" : "false");
            Check("一半一半：三轮内找到问题模组", instance.BisectFound && instance.Bisect!.Found == culprit && rounds <= 3,
                $"{rounds} · {instance.BisectFoundText}");
            Check("找到后：其他模组都开回来了", Directory.EnumerateFiles(mods, "*.jar").Count() == 7);
            await Task.Delay(300);
            await ShotAsync("fix-found-1440");
            instance.BisectDisableFoundCommand.Execute(null);
            Check("【关掉 X】：只关这一个，会话结束", !instance.HasBisect && !Crashes() && Directory.EnumerateFiles(mods, "*.jar").Count() == 6
                && !File.Exists(CrashBisect.PathFor(service.WorkDirFor(id))), instance.Status);

            // [Restore everything] in the middle of a round.
            File.Move(Path.Combine(mods, culprit + ".disabled"), Path.Combine(mods, culprit));
            instance.StartFixCommand.Execute(null);
            instance.BisectAnswerCommand.Execute("true");
            Check("第二轮也关着一部分", instance.BisectAwaiting && Directory.EnumerateFiles(mods, "*.jar.disabled").Any());
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            await ShotAsync("fix-round2-600");
            instance.BisectRestoreCommand.Execute(null);
            Check("【全部恢复原样】：7 个模组都开着，没有会话", !instance.HasBisect && Directory.EnumerateFiles(mods, "*.jar").Count() == 7
                && !Directory.EnumerateFiles(mods, "*.disabled").Any());
        }
        finally
        {
            service.Settings.Instances = originalMeta;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
