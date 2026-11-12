using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Amber.App.Views;

/// <summary>
/// Settings › Multiplayer, the multiplayer advanced settings panel: groups that open one at a time (opening one closes
/// the others, also when a settings#Field reveal opens it), and the reset confirmation with focus on [Cancel].
/// </summary>
public partial class MultiplayerSettingsView : UserControl
{
    private bool _closingOthers;

    public MultiplayerSettingsView()
    {
        InitializeComponent();
        foreach (var group in AllGroups) group.PropertyChanged += OnGroupChanged;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel settings) settings.PickFilesFolder ??= PickFolderAsync;
        };
        MultiplayerResetConfirm.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && MultiplayerResetConfirm.IsVisible)
                Dispatcher.UIThread.Post(() => MultiplayerResetCancel.Focus(Avalonia.Input.NavigationMethod.Tab), DispatcherPriority.Loaded);
        };
    }

    /// <summary>The groups in panel order.</summary>
    internal IReadOnlyList<Expander> Groups => [.. AllGroups.Where(g => g.IsVisible)];

    /// <summary>Every group, including the calls group that shows only while calls are switched on.</summary>
    private IReadOnlyList<Expander> AllGroups =>
        [MpGroupProtection, MpGroupConnection, MpGroupInvites, MpGroupRoom, MpGroupFiles, MpGroupNetwork, MpGroupTroubleshoot, MpGroupCalls];

    private void OnGroupChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Expander.IsExpandedProperty || sender is not Expander { IsExpanded: true } opened || _closingOthers) return;
        _closingOthers = true;
        try
        {
            foreach (var group in Groups)
                if (!ReferenceEquals(group, opened)) group.IsExpanded = false;
        }
        finally { _closingOthers = false; }
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("settings.files.folder_choose"), AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async void ImportComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { Multiplayer: { } vm }) await ImportComponentAsync(this, vm);
    }

    /// <summary>Asks for the component zip and imports it. Shared with the lobby's "Import from a file" button.</summary>
    internal static async Task ImportComponentAsync(Control owner, MultiplayerViewModel vm)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.F("multiplayer.component.import_picker", vm.ComponentFileName),
            FileTypeFilter = [new FilePickerFileType(L.T("multiplayer.component.import_filetype")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.ImportComponentAsync(path);
    }
}
