using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>A settings group in the left list (wide), the picker (medium) or the group index (compact).</summary>
public sealed class SettingsGroupItem(SettingsGroup group) : ObservableObject
{
    public SettingsGroup Group { get; } = group;
    public string Id => Group.Id;
    public string Title => L.T(Group.TitleKey);
    public string Summary => L.T(Group.SummaryKey);
    public string Note => L.T(Group.NoteKey);

    internal void Relabel()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Note));
    }
}

/// <summary>A settings search hit: "Group › Setting", with the hint under it.</summary>
public sealed record SettingsSearchHit(SettingsField Field, string Title, string Path, string Hint)
{
    public bool HasHint => Hint.Length > 0;
}

/// <summary>
/// Settings in ten groups (official design §6.20): a group list on wide windows, a picker on medium ones, and an index
/// with one-line summaries on compact ones. Search lists "Group › Setting" as you type; Enter jumps to the field.
/// <c>settings#Field</c> opens the field's group first.
/// </summary>
public sealed partial class SettingsViewModel
{
    public ObservableCollection<SettingsGroupItem> Groups { get; } = [.. SettingsCatalog.Groups.Select(g => new SettingsGroupItem(g))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGroup), nameof(SelectedGroupId), nameof(SelectedGroupNote), nameof(HeaderTitle))]
    public partial int SelectedGroupIndex { get; set; }

    public SettingsGroupItem SelectedGroup => Groups[Math.Clamp(SelectedGroupIndex, 0, Groups.Count - 1)];
    public string SelectedGroupId => SelectedGroup.Id;
    public string SelectedGroupNote => SelectedGroup.Note;

    /// <summary>Compact windows: true once a group was opened from the index (false shows the index).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    public partial bool GroupEntered { get; set; }

    /// <summary>Compact windows show the group name as the page title once inside a group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    public partial bool IsCompact { get; set; }

    public string HeaderTitle => IsCompact && GroupEntered ? SelectedGroup.Title : L.T("instance.settings.title");

    partial void OnSelectedGroupIndexChanged(int value)
    {
        if (value < 0) SelectedGroupIndex = 0;
    }

    public void SelectGroup(string id)
    {
        var index = Groups.ToList().FindIndex(g => g.Id == id);
        if (index < 0) return;
        SelectedGroupIndex = index;
        GroupEntered = true;
    }

    /// <summary>Compact: back from a group to the index.</summary>
    [RelayCommand]
    private void BackToGroups() => GroupEntered = false;

    [RelayCommand]
    private void OpenGroup(SettingsGroupItem? group)
    {
        if (group is not null) SelectGroup(group.Id);
    }

    /// <summary>Before <c>settings#Field</c> is revealed: open the group that holds it and clear the search.</summary>
    public void ShowField(string? field)
    {
        if (SettingsCatalog.Find(field) is not { } f) return;
        SettingsSearch = "";
        SelectGroup(f.Group);
    }

    // ---- search ----

    [ObservableProperty] public partial string SettingsSearch { get; set; } = "";
    public ObservableCollection<SettingsSearchHit> SearchHits { get; } = [];
    [ObservableProperty] public partial bool HasSearch { get; private set; }
    [ObservableProperty] public partial bool SearchEmpty { get; private set; }
    [ObservableProperty] public partial int SelectedHitIndex { get; set; } = -1;

    partial void OnSettingsSearchChanged(string value)
    {
        SearchHits.Clear();
        var text = value?.Trim() ?? "";
        HasSearch = text.Length > 0;
        if (HasSearch)
        {
            var items = SettingsCatalog.Fields.Select(f => (Field: f, Item: ItemFor(f))).ToList();
            foreach (var match in CommandIndex.Search(text, items.Select(i => i.Item), int.MaxValue))
            {
                var field = items.First(i => ReferenceEquals(i.Item, match.Item)).Field;
                var group = SettingsCatalog.Group(field.Group)!;
                SearchHits.Add(new SettingsSearchHit(field, match.Item.Title, L.T(group.TitleKey) + " › " + match.Item.Title,
                    field.HintKey is { } h ? L.T(h) : ""));
            }
        }
        SearchEmpty = HasSearch && SearchHits.Count == 0;
        SelectedHitIndex = SearchHits.Count > 0 ? 0 : -1;
    }

    /// <summary>A setting as a palette / search item: its label, its group as the subtitle, other languages and the hint as keywords.</summary>
    public static CommandItem ItemFor(SettingsField field)
    {
        var group = SettingsCatalog.Group(field.Group)!;
        List<string> keys = [field.LabelKey, .. field.HintKey is { } h ? [h] : Array.Empty<string>()];
        return new CommandItem("setting:" + field.Field, CommandKind.Setting, L.T(field.LabelKey), L.T(group.TitleKey),
            CommandPaletteViewModel.NamesOf([.. keys, group.TitleKey]));
    }

    /// <summary>Raised to reveal a field (the window scrolls to it, focuses it and flashes it).</summary>
    public Action<string>? RevealField { get; set; }

    /// <summary>Navigates elsewhere (the multiplayer and tools cards).</summary>
    public Action<string>? Navigate { get; set; }

    [RelayCommand]
    private void OpenHit(SettingsSearchHit? hit)
    {
        hit ??= SelectedHitIndex >= 0 && SelectedHitIndex < SearchHits.Count ? SearchHits[SelectedHitIndex] : null;
        if (hit is null) return;
        RevealField?.Invoke(hit.Field.Field);
    }

    [RelayCommand] private void GoMultiplayerSettings() => Navigate?.Invoke("play");
    [RelayCommand] private void GoAbout() => Navigate?.Invoke("about");
    [RelayCommand] private void GoGroup(string? id) { if (id is not null) SelectGroup(id); }

    // ---- General › after launching a game ----

    public OptionItem[] AfterLaunchOptions { get; } = OptionItem.Keys("settings.after_launch.minimize", "settings.after_launch.keep");
    internal static readonly string[] AfterLaunchKeys = ["minimize", "keep"];

    [ObservableProperty] public partial int AfterLaunchIndex { get; set; }

    partial void OnAfterLaunchIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _service.Settings.AfterLaunch = AfterLaunchKeys[Math.Min(value, AfterLaunchKeys.Length - 1)];
        Persist();
    }

    public string VersionText => L.F("main.version", Amber.Core.Launch.UpdateChecker.CurrentVersion);
    public string DataFolder => Path.GetDirectoryName(AppSettings.FilePath) ?? "";

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataFolder) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private void InitGroups()
    {
        AfterLaunchIndex = Math.Max(0, Array.IndexOf(AfterLaunchKeys, _service.Settings.AfterLaunch));
        L.Changed += () =>
        {
            foreach (var g in Groups) g.Relabel();
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(SelectedGroupNote));
            OnPropertyChanged(nameof(VersionText));
            if (HasSearch) OnSettingsSearchChanged(SettingsSearch);
        };
    }
}
