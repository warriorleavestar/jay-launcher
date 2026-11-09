using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Game;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=backups (UI batch 7b): Instance › Worlds in a throwaway game directory inside the report folder.
/// [Back up] runs as an Activity task, B backs up the selected world, the world panel lists backups newest first, and
/// [Restore…] goes through the locked confirm (text names the time and says the current world is backed up first),
/// then swaps the world and leaves the safety backup in the list. Screenshots wide and narrow. Nothing goes online.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task BackupsAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalMeta = service.Settings.Instances;
        var game = Path.Combine(reportDir, "backups-game");
        const string id = "1.20.1";
        var instance = main.Pages.OfType<InstanceViewModel>().First();
        try
        {
            WriteLibraryVersion(game, id, "[]");
            service.Settings.Instances = new() { [id] = new() { DisplayName = "备份测试" } };
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            var work = service.WorkDirFor(id);
            var world = Path.Combine(work, "saves", "Castle");
            Directory.CreateDirectory(Path.Combine(world, "region"));
            Nbt.WriteFile(Path.Combine(world, "level.dat"), new Dictionary<string, object>
            {
                ["Data"] = new Dictionary<string, object> { ["LevelName"] = "城堡", ["GameType"] = 0, ["LastPlayed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            });
            File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "before");
            WorldBackups.Create(new WorldInfo("Castle", world, "城堡", "", null, null), work, now: DateTime.Now.AddDays(-3));

            service.SetCurrentInstance(id);
            main.Shell.Navigate("library/" + id);
            window.Width = 1440;
            window.Height = 900;
            await WaitAsync(() => instance.Current?.Id == id, 10);
            instance.TabIndex = (int)InstanceTab.Worlds;
            await WaitAsync(() => instance.Worlds.Count == 1, 10);
            var page = window.PageHost.GetVisualDescendants().OfType<InstancePage>().First();

            // B on the world list backs it up (an Activity task).
            Dispatcher.UIThread.Invoke(() =>
            {
                page.WorldsList.SelectedIndex = 0;
                page.WorldsList.Focus();
            });
            PressKey(page.WorldsList, Key.B);
            Check("B：备份选中的世界，进活动中心", service.Tasks.All.Any(t => t.Kind == "backup"), string.Join(",", service.Tasks.All.Select(t => t.Kind)));
            if (instance.BackupRun is { } run) await run;
            Check("备份完成：状态写明，没有留下半成品", instance.Status.Contains("备份好了") && service.Tasks.All.First(t => t.Kind == "backup").State == Amber.Core.Activity.TaskState.Succeeded
                && !Directory.EnumerateFiles(WorldBackups.Dir(work), "*.partial").Any(), instance.Status);

            // Enter opens the world panel with data packs and backups.
            PressKey(page.WorldsList, Key.Enter);
            await WaitAsync(() => instance.HasDatapackWorld, 5);
            Check("Enter：打开世界面板，备份按新到旧列出", instance.WorldBackupsList.Count == 2
                && instance.WorldBackupsList[0].Backup.TakenAt > instance.WorldBackupsList[1].Backup.TakenAt, instance.BackupsTitle);
            await Task.Delay(400);
            await ShotAsync("backups-1440");

            // [Restore…] → locked confirm → swap.
            File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "after");
            var oldest = instance.WorldBackupsList[^1];
            var restore = Dispatcher.UIThread.Invoke(() => page.BackupsList.GetVisualDescendants().OfType<ConfirmButton>().Last());
            Dispatcher.UIThread.Invoke(() => (ControlAutomationPeer.CreatePeerForElement(restore) as IInvokeProvider)?.Invoke());
            await WaitAsync(() => SafetyDialog.Current is not null, 5);
            var dialog = SafetyDialog.Current;
            var body = Dispatcher.UIThread.Invoke(() => dialog is null ? "" : string.Join(" ", dialog.Panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
            Check("【恢复…】走锁定确认：写明时间，并说会先备份现在的世界", body.Contains(oldest.When) && body.Contains("先自动备份"), body);
            Check("确认框默认焦点在“保留”", Dispatcher.UIThread.Invoke(() => dialog?.KeepButton.IsFocused == true || dialog?.FocusManager?.GetFocusedElement() == dialog?.KeepButton));
            Dispatcher.UIThread.Invoke(() => (ControlAutomationPeer.CreatePeerForElement(dialog!.ConfirmButton) as IInvokeProvider)?.Invoke());
            await WaitAsync(() => instance.Status.Contains("回到了"), 10);
            Check("恢复：世界回到旧备份的内容", File.ReadAllText(Path.Combine(world, "region", "r.0.0.mca")) == "before", instance.Status);
            await WaitAsync(() => instance.WorldBackupsList.Count == 3, 5);
            Check("恢复前的世界自动备份进列表，面板还在这个世界", instance.WorldBackupsList.Count == 3 && instance.DatapackWorld?.World.FolderName == "Castle", instance.BackupsTitle);
            Check("saves 里没有留下临时文件夹", Directory.EnumerateDirectories(Path.Combine(work, "saves")).Select(Path.GetFileName).SequenceEqual(["Castle"]));

            window.Width = 600;
            window.Height = 640;
            await Task.Delay(600);
            await ShotAsync("backups-600");
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
