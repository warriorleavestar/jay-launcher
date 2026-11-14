using Amber.Core.Localization;
using Amber.Mobile.ViewModels;
using Amber.Mobile.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Amber.Mobile;

public sealed class App : Application
{
    private MobileMainViewModel? _viewModel;
    public static IMobilePlatformServices PlatformServices { get; set; } = NoMobilePlatformServices.Instance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The mobile edition ships for Chinese-speaking players first. Keep desktop's saved
        // preference independent, while making a fresh Android/iOS install immediately usable.
        L.SetLanguage("zh-Hans");
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = CreateMainView;
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = CreateMainView();
        base.OnFrameworkInitializationCompleted();
    }

    private MainView CreateMainView()
    {
        _viewModel ??= new MobileMainViewModel(platformServices: PlatformServices);
        return new MainView { DataContext = _viewModel };
    }
}
