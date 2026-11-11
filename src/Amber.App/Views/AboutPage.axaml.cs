using Amber.App.ViewModels;
using Amber.Core.Layout;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

/// <summary>About and privacy (official design §6.21). Compact width moves each status under its item.</summary>
public partial class AboutPage : UserControl
{
    /// <summary>The width tier the page is laid out for (self-check).</summary>
    internal WidthTier Tier { get; private set; } = WidthTier.Wide;

    public AboutPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => ApplyWidth(Bounds.Width);
    }

    /// <summary>"Verify a Jay download": pick the downloaded package; its SHA256SUMS.txt and .sigs must be next to it.</summary>
    private async void Verify_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L.T("about.verify.pick"), AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.VerifyDownloadAsync(path);
    }

    private void ApplyWidth(double width)
    {
        if (width <= 0) return;
        Tier = LayoutTiers.ForWidth(width, Tier);
        if (DataContext is AboutViewModel vm) vm.List.IsCompact = Tier == WidthTier.Compact;
    }
}
