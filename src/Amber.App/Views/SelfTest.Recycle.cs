using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Content;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=recycle (UI batch 6e): Settings › Recently deleted with a throwaway game directory in the report
/// folder. Rows for a deleted mod, world and instance with their kind and owner, [Restore] back in place (an instance
/// keeps its name), [Delete forever] and [Empty…] behind the locked confirm, and the retention choice. No network.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task RecycleAsync()
    {
        var service = main.Service;
        var originalDir = service.Settings.GameDirectory;
        var originalKeep = service.Settings.TrashRetentionDays;
        var game = Path.Combine(reportDir, "rc-game");
        var settings = Page<SettingsViewModel>();
        var recycle = settings.Recycle;
        try
        {
            WriteLibraryVersion(game, "1.20.1", "[]");
            WriteLibraryVersion(game, "1.19.4", "[]");
            var dir = Path.Combine(game, "versions", "1.20.1");
            Directory.CreateDirectory(Path.Combine(dir, "mods"));
            Directory.CreateDirectory(Path.Combine(dir, "saves", "Castle"));
            File.WriteAllText(Path.Combine(dir, "mods", "iris.jar"), "jar");
            File.WriteAllText(Path.Combine(dir, "saves", "Castle", "level.dat"), "x");
            service.Settings.GameDirectory = game;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), game, DateTimeOffset.Now.AddDays(-2));
            TrashService.MoveToTrash(Path.Combine(dir, "saves", "Castle"), game, DateTimeOffset.Now.AddHours(-1));
            TrashService.MoveToTrash(Path.Combine(game, "versions", "1.19.4"), game);
            service.NotifyInstalledChanged();

            window.Width = 1440;
            window.Height = 900;
            main.Shell.Navigate("settings#RecycleKeep");
            await WaitAsync(() => main.CurrentPage is SettingsViewModel, 5);
            var card = await FindAsync<Border>(b => b.Name == "RecycleCard");
            Dispatcher.UIThread.Invoke(() => card?.BringIntoView());
            Check("设置里有“最近删除”：实例、世界、模组三行，新的在前", await WaitAsync(() => recycle.Rows.Count == 3, 5)
                && recycle.Rows[0].Item.Kind == RecycledKind.Instance && recycle.Rows[1].Item.Kind == RecycledKind.World && recycle.Rows[2].Item.Kind == RecycledKind.Mod,
                string.Join(",", recycle.Rows.Select(r => r.Name)));
            Check("每行写类型、来自哪个实例、剩几天", recycle.Rows[2].Detail.Contains("1.20.1") && recycle.Rows[2].When.Contains('5'), recycle.Rows[2].Detail + " / " + recycle.Rows[2].When);
            Check("【永久删除】和【清空…】都走锁定确认", Dispatcher.UIThread.Invoke(() =>
                card!.GetVisualDescendants().OfType<ConfirmButton>().Count(b => b.IsEffectivelyVisible) == 4));
            await Task.Delay(300);
            await ShotAsync("recycle-1440");

            // [Restore]: the mod goes back; the instance keeps its name.
            await recycle.RestoreCommand.ExecuteAsync(recycle.Rows.First(r => r.Item.Kind == RecycledKind.Mod));
            Check("【恢复】模组：回到原来的 mods，行没了", File.Exists(Path.Combine(dir, "mods", "iris.jar")) && recycle.Rows.Count == 2);
            await recycle.RestoreCommand.ExecuteAsync(recycle.Rows.First(r => r.Item.Kind == RecycledKind.Instance));
            Check("【恢复】实例：名字不变，又出现在游戏库", Directory.Exists(Path.Combine(game, "versions", "1.19.4")) && service.InstalledVersions().Contains("1.19.4"));

            // Retention.
            Dispatcher.UIThread.Invoke(() => recycle.KeepIndex = Array.IndexOf(RecycleViewModel.KeepDayChoices, 30));
            Check("保留天数改成 30 天：存进设置，剩余天数跟着变", service.Settings.TrashRetentionDays == 30 && recycle.Rows[0].When.Contains("30"), recycle.Rows[0].When);

            // [Delete forever] (confirmed) and [Empty].
            await recycle.DeleteForeverCommand.ExecuteAsync(recycle.Rows[0]);
            Check("【永久删除】：从最近删除里清掉", recycle.IsEmpty);
            TrashService.MoveToTrash(Path.Combine(dir, "mods", "iris.jar"), game);
            recycle.Refresh();
            await recycle.EmptyAllCommand.ExecuteAsync(null);
            Check("【清空】：全部清掉，显示空状态", recycle.IsEmpty && recycle.EmptyText.Contains("30"), recycle.EmptyText);
            await Task.Delay(300);
            window.Width = 600;
            window.Height = 640;
            await Task.Delay(500);
            await ShotAsync("recycle-600-empty");
        }
        finally
        {
            service.Settings.TrashRetentionDays = originalKeep;
            service.Settings.GameDirectory = originalDir;
            service.SaveAndReload();
            service.NotifyInstalledChanged();
            window.Width = 1440;
            window.Height = 900;
        }
    }
}
