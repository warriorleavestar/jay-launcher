using Amber.App.Localization;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Files;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// Settings › Multiplayer › Files (redesign batch 12): where received files go, taking checked friends' files without
/// asking (off; programs always ask), queueing for friends who are offline, the speed, and saying when a file arrived.
/// </summary>
public sealed partial class SettingsViewModel
{
    public OptionItem[] FilesSpeedOptions { get; } = OptionItem.Keys("settings.files.speed.unlimited", "settings.files.speed.give_way", "settings.files.speed.save_data");

    /// <summary>The folder chooser (the view sets it; self-checks give a path directly).</summary>
    internal Func<Task<string?>>? PickFilesFolder { get; set; }

    public bool FilesFolderIsCustom => _service.Settings.FilesSaveFolder.Length > 0;

    /// <summary>"Downloads/Jay", or the folder chosen here.</summary>
    public string FilesFolderText => FilesFolderIsCustom ? _service.Settings.FilesSaveFolder : L.T("files.downloads_jay");

    public bool FilesAutoAccept
    {
        get => _service.Settings.FilesAutoAcceptFriends;
        set => SaveFiles(s => s.FilesAutoAcceptFriends = value);
    }

    public bool FilesQueue
    {
        get => _service.Settings.FilesQueueOffline;
        set => SaveFiles(s => s.FilesQueueOffline = value);
    }

    public int FilesSpeedIndex
    {
        get => Math.Max(0, Array.IndexOf(TransferPace.All, TransferPace.Normalize(_service.Settings.FilesSpeed)));
        set => SaveFiles(s => s.FilesSpeed = TransferPace.All[Math.Clamp(value, 0, TransferPace.All.Length - 1)]);
    }

    public bool FilesNotify
    {
        get => _service.Settings.FilesNotifyDone;
        set => SaveFiles(s => s.FilesNotifyDone = value);
    }

    public bool FilesCustomized => AdvancedCustomized.Contains(AdvancedSettings.Files);

    [RelayCommand]
    private async Task ChooseFilesFolderAsync()
    {
        if (PickFilesFolder is not { } pick || await pick() is not { Length: > 0 } folder) return;
        SetFilesFolder(folder);
    }

    [RelayCommand]
    private void ResetFilesFolder() => SetFilesFolder("");

    internal void SetFilesFolder(string folder) => SaveFiles(s => s.FilesSaveFolder = folder);

    private void SaveFiles(Action<Amber.App.Services.AppSettings> change)
    {
        change(_service.Settings);
        _service.TrySaveSettings();
        Multiplayer?.ApplyFileSettings();
        NotifyFiles();
        RefreshAdvanced();
    }

    private void NotifyFiles()
    {
        foreach (var name in (string[])[nameof(FilesFolderIsCustom), nameof(FilesFolderText), nameof(FilesAutoAccept), nameof(FilesQueue), nameof(FilesSpeedIndex), nameof(FilesNotify)])
            OnPropertyChanged(name);
    }

    /// <summary>[Reset all to defaults] puts the file settings back too.</summary>
    private void ResetFiles(AdvancedValues d)
    {
        var s = _service.Settings;
        if (!d.FilesCustomFolder) s.FilesSaveFolder = "";
        s.FilesAutoAcceptFriends = d.FilesAutoAccept;
        s.FilesQueueOffline = d.FilesQueue;
        s.FilesSpeed = d.FilesSpeed;
        s.FilesNotifyDone = d.FilesNotify;
        _service.TrySaveSettings();
        Multiplayer?.ApplyFileSettings();
        NotifyFiles();
    }
}
