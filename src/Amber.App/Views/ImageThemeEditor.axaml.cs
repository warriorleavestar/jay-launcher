using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

/// <summary>
/// The "make a theme from pictures" panel. Pictures come from a file picker, or from dragging them onto the window (its
/// drop zones offer the four slots). In a narrow settings column the preview moves above the slots.
/// </summary>
public partial class ImageThemeEditor : UserControl
{
    /// <summary>Below this width the preview sits above the slots instead of beside them.</summary>
    public const double StackBelow = 760;

    public ImageThemeEditor()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        ActualThemeVariantChanged += (_, _) => Model?.UpdatePreview();
    }

    private ImageThemeViewModel? Model => DataContext as ImageThemeViewModel;

    internal bool IsStacked { get; private set; }

    private void Arrange(double width)
    {
        var stacked = width < StackBelow;
        IsStacked = stacked;
        Grid.SetColumn(PreviewColumn, stacked ? 0 : 1);
        Grid.SetRow(PreviewColumn, 0);
        Grid.SetRow(EditorColumn, stacked ? 1 : 0);
        Grid.SetColumnSpan(EditorColumn, stacked ? 2 : 1);
    }

    private async void Pick_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } vm || (sender as Control)?.DataContext is not ImageSlotItem slot) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = slot.Title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.T("theme.image.filetype")) { Patterns = ThemeImages.Extensions.Select(x => "*" + x).ToArray() }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.SetPicture(slot.Slot, path);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("theme.image.save"),
            SuggestedFileName = Amber.Core.Themes.ImageTheme.IdFor(vm.Name).Split('-')[0] + Amber.Core.Themes.ThemePackReader.Extension,
            DefaultExtension = "jaytheme",
            FileTypeChoices = [new FilePickerFileType(L.T("settings.themepack.filetype")) { Patterns = ["*.jaytheme"] }],
        });
        if (file?.TryGetLocalPath() is { } path) vm.SaveAs(path);
    }
}
