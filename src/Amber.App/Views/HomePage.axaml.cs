using Amber.App.ViewModels;
using Amber.Core.Localization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amber.App.Views;

public partial class HomePage : UserControl
{
    internal Amber.App.Services.ThemeLayoutHost ThemeLayout { get; }

    public HomePage()
    {
        InitializeComponent();
        // 主题包可以重新排这一页的区块（docs/themes.md 的“页面布局”）。
        ThemeLayout = Amber.App.Services.ThemeLayoutHost.Attach(this, "home");
        // 进入启动页时焦点放在启动按钮上，按 Enter 或空格就能启动（改版规格 7.2）；新手卡在的时候焦点交给卡。
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(FocusPrimary, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    internal void FocusPrimary()
    {
        if (DataContext is HomeViewModel { Onboarding.IsVisible: true }) return;
        if (PrimaryButton.IsEffectivelyVisible && PrimaryButton.IsEffectivelyEnabled) PrimaryButton.Focus(Avalonia.Input.NavigationMethod.Tab);
    }

    private async void ExportCrash_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("home.crash.export_title"),
            SuggestedFileName = $"jay-error-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportCrashBundle(path);
    }

    private async void ExportFailure_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("home.crash.export_title"),
            SuggestedFileName = $"jay-error-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExtension = "zip",
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportFailureReport(path);
    }

    private void CloseForceKillFlyout(object? sender, RoutedEventArgs e) => ForceKillButton.Flyout?.Hide();
}
