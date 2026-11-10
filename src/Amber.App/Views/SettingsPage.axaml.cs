using System.ComponentModel;
using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// Settings page (official design §6.20): shows one group at a time, picks the layout for the width, and handles the
/// keys: Ctrl+F searches, ↑↓ / Enter in the search list jump to a field, Esc clears the search, Enter or → in the
/// group list moves into the form.
/// </summary>
public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _vm;

    /// <summary>The width tier the page is laid out for (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Wide;
    private bool _tierSet;

    public SettingsPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        GroupList.KeyDown += GroupList_KeyDown;
    }

    private void Attach(SettingsViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        vm.RevealField = field => (TopLevel.GetTopLevel(this) as MainWindow)?.RevealField(field);
        ShowGroup();
        ApplyLayout();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedGroupIndex)) ShowGroup();
        if (e.PropertyName is nameof(SettingsViewModel.GroupEntered) or nameof(SettingsViewModel.SelectedGroupIndex)) ApplyLayout();
    }

    /// <summary>Only the selected group's panel is shown; the form starts at the top.</summary>
    private void ShowGroup()
    {
        if (_vm is null) return;
        var id = "Group_" + _vm.SelectedGroupId;
        foreach (var panel in Form.Children.OfType<Control>())
            panel.IsVisible = panel.Name == id;
        FormScroll.Offset = default;
    }

    internal void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = _tierSet ? LayoutTiers.ForWidth(width, Tier) : LayoutTiers.ForWidth(width);
        _tierSet = true;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        var wide = Tier == WidthTier.Wide;
        var compact = Tier == WidthTier.Compact;
        var entered = _vm?.GroupEntered == true;
        if (_vm is not null) _vm.IsCompact = compact;
        Body.ColumnDefinitions[0].Width = wide ? new GridLength(200) : new GridLength(0);
        Body.ColumnDefinitions[2].Width = wide ? new GridLength(240) : new GridLength(0);
        Body.ColumnSpacing = wide ? 16 : 0;
        GroupList.IsVisible = wide;
        NotePane.IsVisible = wide;
        GroupPicker.IsVisible = Tier == WidthTier.Medium;
        GroupIndex.IsVisible = compact && !entered;
        FormScroll.IsVisible = !compact || entered;
        GroupBackButton.IsVisible = compact && entered;
        SettingsSearchBox.Width = compact ? 150 : 260;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (!SettingsSearchBox.IsFocused || !_vm.HasSearch) return;
        switch (e.Key)
        {
            case Key.Down or Key.Up when _vm.SearchHits.Count > 0:
                _vm.SelectedHitIndex = Math.Clamp(_vm.SelectedHitIndex + (e.Key == Key.Down ? 1 : -1), 0, _vm.SearchHits.Count - 1);
                SearchHitList.ScrollIntoView(_vm.SelectedHitIndex);
                break;
            case Key.Enter:
                _vm.OpenHitCommand.Execute(null);
                break;
            case Key.Escape:
                _vm.SettingsSearch = "";
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Enter or → in the group list: into the form, on the first control of the group.</summary>
    private void GroupList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Right)) return;
        e.Handled = FocusFirstField();
    }

    private bool FocusFirstField()
    {
        var first = Form.Children.OfType<Control>().FirstOrDefault(p => p.IsVisible)?
            .GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c is not ScrollViewer and not TextBlock);
        return first?.Focus(NavigationMethod.Tab) == true;
    }

    private void GroupIndex_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is SettingsGroupItem group) _vm?.OpenGroupCommand.Execute(group);
    }

    private void GroupIndex_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Right) || GroupIndex.SelectedItem is not SettingsGroupItem group) return;
        _vm?.OpenGroupCommand.Execute(group);
        e.Handled = true;
    }

    private void SearchHit_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is SettingsSearchHit hit) _vm?.OpenHitCommand.Execute(hit);
    }

    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async void ImportTheme_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("settings.themepack.import"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("settings.themepack.filetype")) { Patterns = ["*.jaytheme", "*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _vm.ImportThemePack(path);
    }

    private async void InstallBakeLoader_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("settings.bake.install"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("settings.bake.install_filetype")) { Patterns = ["*.zip"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await _vm.InstallBakeLoaderAsync(path);
    }

    private async void ExportTheme_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("settings.themepack.export"),
            SuggestedFileName = "my-theme.jaytheme",
            DefaultExtension = "jaytheme",
            FileTypeChoices = [new FilePickerFileType(L.T("settings.themepack.filetype")) { Patterns = ["*.jaytheme"] }],
        });
        if (file?.TryGetLocalPath() is { } path) _vm.ExportThemePack(path);
    }

    private async void BrowseGameDir_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Storage is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("settings.directory.picker") });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) _vm.GameDirectory = path;
    }
}
