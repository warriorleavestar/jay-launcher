using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Game;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One backup row in the world panel: "yesterday 21:40 · 300 MB [Restore…]".</summary>
public sealed class WorldBackupItem(WorldBackup backup, string worldName)
{
    public WorldBackup Backup { get; } = backup;
    public string When => Backup.TakenAt.ToLocalTime().ToString("g", L.Culture);
    public string Size => Fmt.Size(Backup.SizeBytes);
    public string Meta => $"{When} · {Size}";

    /// <summary>The locked confirm's text: what gets replaced, and that the current world is backed up first.</summary>
    public string ConfirmMessage => L.F("instance.backups.restore_confirm", When, worldName);
}

/// <summary>
/// Instance › Worlds › backups (official design §6.6): [Back up] is a long task in the Activity center that can be
/// cancelled; the world panel lists its backups with [Restore…] behind the locked confirm. Restoring backs up the world
/// as it is now first, and is refused while the game is running in this instance.
/// </summary>
public sealed partial class InstanceViewModel
{
    public ObservableCollection<WorldBackupItem> WorldBackupsList { get; } = [];

    public bool HasWorldBackups => WorldBackupsList.Count > 0;
    public string BackupsTitle => L.F("instance.backups.title", WorldBackupsList.Count);

    private void LoadBackups()
    {
        WorldBackupsList.Clear();
        if (Current is { } c && DatapackWorld is { } w)
        {
            try
            {
                foreach (var b in WorldBackups.List(c.WorkDir, w.World.FolderName)) WorldBackupsList.Add(new WorldBackupItem(b, w.Name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        OnPropertyChanged(nameof(HasWorldBackups));
        OnPropertyChanged(nameof(BackupsTitle));
    }

    /// <summary>The backup that is being written (self-check waits on it).</summary>
    internal Task? BackupRun { get; private set; }

    [RelayCommand]
    private Task BackupWorld(WorldItem? item) => BackupRun = BackupWorldCoreAsync(item ?? DatapackWorld);

    private async Task BackupWorldCoreAsync(WorldItem? item)
    {
        if (Current is not { } c || item is null) return;
        using var cts = new CancellationTokenSource();
        var task = _service.Tasks.Track("backup", L.F("instance.backups.task", item.Name),
            () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }, "backup:" + item.World.Path);
        SetStatus(StatusMessage.Info("instance.worlds.backing_up", item.Name));
        try
        {
            var progress = new Progress<double>(p => task.Report(p));
            await Task.Run(() => WorldBackups.Create(item.World, c.WorkDir, progress, cts.Token));
            SetStatus(StatusMessage.Success("instance.backups.done", item.Name));
            task.Succeed(Status);
            if (DatapackWorld == item) LoadBackups();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            task.Cancelled();
            SetStatus(StatusMessage.Warning("common.cancelled"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            task.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("instance.backups.failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>[Restore…] (already confirmed in the locked dialog).</summary>
    [RelayCommand]
    private async Task RestoreBackupAsync(WorldBackupItem? item)
    {
        if (Current is not { } c || DatapackWorld is not { } world || item is null) return;
        if (_service.IsRunningIn(c.WorkDir))
        {
            SetStatus(StatusMessage.Warning("instance.backups.running"));
            return;
        }
        var task = _service.Tasks.Track("backup", L.F("instance.backups.restore_task", world.Name));
        SetStatus(StatusMessage.Info("instance.backups.restoring", world.Name));
        try
        {
            await Task.Run(() => WorldBackups.Restore(item.Backup, c.SavesDir, c.WorkDir));
            SetStatus(StatusMessage.Success("instance.backups.restored", world.Name, item.When));
            task.Succeed(Status);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            task.Fail(ErrorText.Describe(e));
            SetStatus(StatusMessage.Failure("instance.backups.restore_failed", e, ErrorText.Of(e)));
        }
        // The folder was swapped: read the worlds again and keep the panel on the same world.
        var folder = world.World.FolderName;
        LoadWorlds();
        if (Worlds.FirstOrDefault(w => w.World.FolderName == folder) is { } again) ShowDatapacks(again);
    }
}
