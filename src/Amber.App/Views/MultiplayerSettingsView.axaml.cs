using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

/// <summary>Settings › Multiplayer: invites, what a new room starts with, the multiplayer component and hotspot sharing.</summary>
public partial class MultiplayerSettingsView : UserControl
{
    public MultiplayerSettingsView() => InitializeComponent();

    private async void ImportComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MultiplayerViewModel vm) await ImportComponentAsync(this, vm);
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
