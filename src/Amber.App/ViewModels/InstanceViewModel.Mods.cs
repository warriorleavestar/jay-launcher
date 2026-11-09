using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Content;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Instance › Mods (official design §6.5): search and filter chips, an inspector for the clicked mod, ticks for bulk
/// actions with a bulk bar, and "Update all" only after a check found updates.
/// </summary>
public sealed partial class InstanceViewModel
{
    public OptionItem[] ModFilterOptions { get; } =
        OptionItem.Keys("instance.mods.filter.all", "instance.mods.filter.disabled", "instance.mods.filter.updates", "instance.mods.filter.problems");

    [ObservableProperty] public partial string ModSearch { get; set; } = "";
    [ObservableProperty] public partial int ModFilterIndex { get; set; }

    private ObservableCollection<ModItem>? _filteredMods;

    /// <summary>The table's rows: the full list when nothing is filtered, otherwise the matching rows.</summary>
    public IEnumerable<ModItem> ModsView => _filteredMods ?? (IEnumerable<ModItem>)Mods;

    public bool IsModFiltered => _filteredMods is not null;
    [ObservableProperty] public partial bool ModsNoMatches { get; set; }
    [ObservableProperty] public partial bool HasMods { get; set; }

    /// <summary>The mod shown in the inspector.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInspected))]
    public partial ModItem? InspectedMod { get; set; }

    public bool HasInspected => InspectedMod is not null;

    [ObservableProperty] public partial int SelectedModCount { get; set; }
    [ObservableProperty] public partial bool HasModSelection { get; set; }
    [ObservableProperty] public partial string ModSelectionText { get; set; } = "";
    [ObservableProperty] public partial int ModUpdateCount { get; set; }
    [ObservableProperty] public partial bool HasModUpdates { get; set; }
    [ObservableProperty] public partial string ModFooter { get; set; } = "";
    [ObservableProperty] public partial bool GameRunningHere { get; set; }

    private bool _modsHooked;

    private void HookMods()
    {
        if (_modsHooked) return;
        _modsHooked = true;
        Mods.CollectionChanged += OnModsCollectionChanged;
    }

    private void OnModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ModItem item in e.NewItems) item.PropertyChanged += OnModItemChanged;
        if (e.OldItems is not null)
            foreach (ModItem item in e.OldItems) item.PropertyChanged -= OnModItemChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset) InspectedMod = null;
        else if (InspectedMod is { } inspected && !Mods.Contains(inspected)) InspectedMod = null;
        ApplyModQuery();
        UpdateModCounts();
    }

    private void OnModItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModItem.Selected) or nameof(ModItem.Enabled) or nameof(ModItem.Update) or nameof(ModItem.Compat)) UpdateModCounts();
        // A row that stops matching its filter (disabled while "Disabled" is shown) stays until the filter changes, so it does not jump away under the pointer.
    }

    partial void OnModSearchChanged(string value) => ApplyModQuery();
    partial void OnModFilterIndexChanged(int value) => ApplyModQuery();

    private void ApplyModQuery()
    {
        var filter = (ModFilter)Math.Clamp(ModFilterIndex, 0, 3);
        if (filter == ModFilter.All && string.IsNullOrWhiteSpace(ModSearch))
        {
            _filteredMods = null;
        }
        else
        {
            _filteredMods = new ObservableCollection<ModItem>(Mods.Where(m => ModQuery.Matches(m.Facts, ModSearch, filter)));
        }
        OnPropertyChanged(nameof(ModsView));
        OnPropertyChanged(nameof(IsModFiltered));
        ModsNoMatches = _filteredMods is { Count: 0 } && Mods.Count > 0;
    }

    private void UpdateModCounts()
    {
        HasMods = Mods.Count > 0;
        SelectedModCount = Mods.Count(m => m.Selected);
        HasModSelection = SelectedModCount > 0;
        ModSelectionText = L.P("instance.mods.selected", SelectedModCount, SelectedModCount);
        ModUpdateCount = Mods.Count(m => m.HasUpdate);
        HasModUpdates = ModUpdateCount > 0;
        var enabled = Mods.Count(m => m.Enabled);
        ModFooter = ModUpdateCount > 0
            ? L.F("instance.mods.footer_updates", L.P("instance.mods.summary", Mods.Count, Mods.Count, enabled), L.P("instance.mods.updatable", ModUpdateCount, ModUpdateCount))
            : L.P("instance.mods.summary", Mods.Count, Mods.Count, enabled);
    }

    /// <summary>The game is running from this instance: changes apply next start.</summary>
    internal void UpdateGameRunning() => GameRunningHere = Current is { } c && _home?.SessionOf(c.Id)?.IsActive == true;

    [RelayCommand]
    private void ClearModFilters()
    {
        ModSearch = "";
        ModFilterIndex = 0;
    }

    [RelayCommand]
    private void CloseInspector() => InspectedMod = null;

    [RelayCommand]
    private void SelectAllMods()
    {
        foreach (var m in ModsView) m.Selected = true;
    }

    [RelayCommand]
    private void ClearModSelection()
    {
        foreach (var m in Mods) m.Selected = false;
    }

    private List<ModItem> SelectedMods => Mods.Where(m => m.Selected).ToList();

    [RelayCommand]
    private void EnableSelectedMods() => SetSelectedEnabled(true);

    [RelayCommand]
    private void DisableSelectedMods() => SetSelectedEnabled(false);

    private void SetSelectedEnabled(bool enabled)
    {
        foreach (var m in SelectedMods) m.Enabled = enabled;
        RecheckCompat();
        UpdateModCounts();
    }

    [RelayCommand]
    private async Task UpdateSelectedModsAsync()
    {
        var pending = SelectedMods.Where(m => m.Update is not null).ToList();
        if (pending.Count == 0)
        {
            SetStatus(StatusMessage.Info("instance.mods.check_first"));
            return;
        }
        await RunWorkAsync(async ct =>
        {
            var done = 0;
            foreach (var item in pending)
            {
                SetStatus(StatusMessage.Info("instance.mods.updating", item.Name));
                await ModManager.ApplyUpdateAsync(item.Update!, _service.Downloader, ct);
                Progress = 100.0 * ++done / pending.Count;
            }
            SetStatus(new StatusMessage("instance.mods.updated_count", [done], StatusKind.Success) { Count = done });
        });
        await LoadModsAsync();
    }

    /// <summary>Deletes the ticked mods into the recycle folder, with one undo for all of them (also Ctrl+Z).</summary>
    [RelayCommand]
    private void DeleteSelectedMods()
    {
        var items = SelectedMods;
        if (items.Count == 0) return;
        DeleteModsWithUndo(items);
    }

    /// <summary>Delete key on one row or the ticked rows.</summary>
    internal void DeleteModsWithUndo(IReadOnlyList<ModItem> items)
    {
        var entries = new List<TrashEntry>();
        foreach (var item in items)
        {
            try { entries.Add(TrashService.MoveToTrash(item.Mod.Path, _service.Directory.Root)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetStatus(StatusMessage.Failure("instance.status.delete_failed", e, ErrorText.Of(e)));
                break;
            }
            Mods.Remove(item);
        }
        if (entries.Count == 0) return;
        RecheckCompat();
        UpdateModCounts();
        var name = entries.Count == 1 ? items[0].Name : L.P("library.mods", entries.Count, entries.Count);
        Toasts.Removed(name, () =>
        {
            foreach (var entry in entries) TrashService.Restore(entry);
            _ = LoadModsAsync();
        });
    }

    /// <summary>E on a row, or the inspector's enable / disable button.</summary>
    [RelayCommand]
    private void ToggleMod(ModItem? item)
    {
        if (item is null) return;
        item.Enabled = !item.Enabled;
    }

    [RelayCommand]
    private void RecheckCompatNow() => RecheckCompat();
}
