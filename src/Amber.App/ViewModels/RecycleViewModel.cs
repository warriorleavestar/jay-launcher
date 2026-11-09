using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One row of "Recently deleted".</summary>
public sealed class RecycledRow(RecycledItem item, int keepDays, DateTimeOffset now)
{
    public RecycledItem Item { get; } = item;
    public string Name => Item.Name;

    /// <summary>"Mod · from fabric-1.20.1 · 12 MB".</summary>
    public string Detail => string.Join(" · ", new[]
    {
        L.T(KindKey(Item.Kind)),
        Item.InstanceId is { } id && Item.Kind != RecycledKind.Instance ? L.F("recycle.from", id) : null,
        Fmt.Size(Item.Entry.SizeBytes),
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string When => L.F("recycle.when", Item.Entry.DeletedAt.ToLocalTime().ToString("g", L.Culture), L.P("recycle.days_left", Item.DaysLeft(keepDays, now), Item.DaysLeft(keepDays, now)));

    internal static string KindKey(RecycledKind kind) => kind switch
    {
        RecycledKind.Instance => "recycle.kind.instance",
        RecycledKind.World => "recycle.kind.world",
        RecycledKind.Mod => "recycle.kind.mod",
        RecycledKind.ResourcePack => "recycle.kind.resourcepack",
        RecycledKind.Shader => "recycle.kind.shader",
        RecycledKind.Screenshot => "recycle.kind.screenshot",
        RecycledKind.Datapack => "recycle.kind.datapack",
        _ => "recycle.kind.other",
    };
}

/// <summary>
/// Settings › Storage › Recently deleted (official design §6.20): what was deleted in the last N days, with [Restore] on
/// every row. [Delete forever] and [Empty] go through the locked safety confirm (the view's ConfirmButton).
/// </summary>
public sealed partial class RecycleViewModel(LauncherService service) : ObservableObject
{
    public ObservableCollection<RecycledRow> Rows { get; } = [];

    public static readonly int[] KeepDayChoices = [1, 3, 7, 14, 30];

    public OptionItem[] KeepOptions { get; } = KeepDayChoices.Select(d => OptionItem.Plural("recycle.keep.days", d)).ToArray();

    public int KeepIndex
    {
        get => Math.Max(0, Array.IndexOf(KeepDayChoices, service.Settings.TrashRetentionDays) is var i && i >= 0 ? i : Array.IndexOf(KeepDayChoices, TrashService.DefaultKeepDays));
        set
        {
            if (value < 0 || value >= KeepDayChoices.Length || KeepDayChoices[value] == service.Settings.TrashRetentionDays) return;
            service.Settings.TrashRetentionDays = KeepDayChoices[value];
            try { service.Settings.Save(); } catch (IOException) { }
            OnPropertyChanged();
            Refresh();
        }
    }

    public bool IsEmpty => Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;

    public string EmptyText => L.P("recycle.empty", service.Settings.TrashRetentionDays, service.Settings.TrashRetentionDays);

    public string Summary => L.P("recycle.summary", Rows.Count, Rows.Count, Fmt.Size(Rows.Sum(r => r.Item.Entry.SizeBytes)));

    [ObservableProperty]
    public partial StatusMessage? Message { get; set; }

    public void Refresh()
    {
        var root = service.Directory.Root;
        IReadOnlyList<RecycledItem> items;
        try
        {
            items = RecycleIndex.Collect(root, service.Directory.ListInstalled().Select(service.Directory.VersionDir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            items = [];
        }
        var now = DateTimeOffset.Now;
        Rows.Clear();
        foreach (var item in items) Rows.Add(new RecycledRow(item, service.Settings.TrashRetentionDays, now));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Summary));
        EmptyAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>[Restore]: back where it was. An instance keeps its name (its files must match the folder).</summary>
    [RelayCommand]
    private void Restore(RecycledRow? row)
    {
        if (row is null) return;
        try
        {
            var path = TrashService.Restore(row.Item.Entry, keepName: row.Item.Kind == RecycledKind.Instance);
            Message = StatusMessage.Success("recycle.restored", Path.GetFileName(path));
            if (row.Item.Kind == RecycledKind.Instance) service.NotifyInstalledChanged();
            else service.NotifyContentChanged();
        }
        catch (TrashException e)
        {
            Message = StatusMessage.Warning(e.Reason switch
            {
                TrashFailure.TargetExists => "recycle.restore.exists",
                TrashFailure.ParentMissing => "recycle.restore.no_parent",
                _ => "recycle.restore.missing",
            }, row.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = StatusMessage.Failure("recycle.failed", e, ErrorText.Of(e));
        }
        Refresh();
    }

    /// <summary>[Delete forever] (already confirmed in the locked dialog).</summary>
    [RelayCommand]
    private void DeleteForever(RecycledRow? row)
    {
        if (row is null) return;
        try
        {
            RecycleIndex.DeleteForever(row.Item.Entry);
            Message = StatusMessage.Success("recycle.deleted", row.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = StatusMessage.Failure("recycle.failed", e, ErrorText.Of(e));
        }
        Refresh();
    }

    private bool CanEmptyAll() => Rows.Count > 0;

    /// <summary>[Empty] (already confirmed in the locked dialog).</summary>
    [RelayCommand(CanExecute = nameof(CanEmptyAll))]
    private void EmptyAll()
    {
        var count = Rows.Count;
        var removed = RecycleIndex.Empty(Rows.Select(r => r.Item).ToList());
        Message = removed == count
            ? new StatusMessage("recycle.emptied", [removed], StatusKind.Success) { Count = removed }
            : StatusMessage.Warning("recycle.emptied_partly", removed, count - removed);
        Refresh();
    }
}
