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
/// drop zones offer the four slots). The preview sits above the slots.
/// </summary>
public partial class ImageThemeEditor : UserControl
{
    /// <summary>The preview's own width; in a narrower editor it is scaled down to fit.</summary>
    private const double PreviewWidth = 360;

    public ImageThemeEditor()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        ActualThemeVariantChanged += (_, _) => Model?.UpdatePreview();
    }

    private ImageThemeViewModel? Model => DataContext as ImageThemeViewModel;

    private void Arrange(double width)
    {
        var scale = Math.Min(1, width / PreviewWidth);
        PreviewScale.LayoutTransform = scale >= 1 ? null : new Avalonia.Media.ScaleTransform(scale, scale);
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
