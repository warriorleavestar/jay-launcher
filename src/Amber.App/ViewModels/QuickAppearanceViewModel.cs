using System.ComponentModel;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>
/// Light/dark and the language on the "Me" page, without opening the settings. Both go through the settings page, so
/// the two places stay in sync and are saved the same way.
/// </summary>
public sealed partial class QuickAppearanceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private SettingsViewModel? _settings;

    public QuickAppearanceViewModel(MainViewModel main)
    {
        _main = main;
        L.Changed += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LanguageIndex)));
    }

    private SettingsViewModel? Settings
    {
        get
        {
            if (_settings is null && _main.Pages.OfType<SettingsViewModel>().FirstOrDefault() is { } s)
            {
                _settings = s;
                s.PropertyChanged += OnSettingsChanged;
            }
            return _settings;
        }
    }

    // ---- light / dark: system | light | dark (same order as SettingsViewModel.ThemeOptions) ----

    public bool IsSystemTheme { get => ThemeIndex == 0; set { if (value) ThemeIndex = 0; } }
    public bool IsLightTheme { get => ThemeIndex == 1; set { if (value) ThemeIndex = 1; } }
    public bool IsDarkTheme { get => ThemeIndex == 2; set { if (value) ThemeIndex = 2; } }

    private int ThemeIndex
    {
        get => Settings?.SelectedThemeIndex ?? 0;
        set { if (Settings is { } s && s.SelectedThemeIndex != value) s.SelectedThemeIndex = value; }
    }

    // ---- language: the same options as Settings › General ----

    public Amber.App.Localization.OptionItem[] LanguageOptions => Settings?.LanguageOptions ?? [];

    public int LanguageIndex
    {
        get => Settings?.SelectedLanguageIndex ?? 0;
        set { if (Settings is { } s && value >= 0 && s.SelectedLanguageIndex != value) s.SelectedLanguageIndex = value; }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedThemeIndex) or null)
        {
            OnPropertyChanged(nameof(IsSystemTheme));
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
        }
        if (e.PropertyName is nameof(SettingsViewModel.SelectedLanguageIndex) or null) OnPropertyChanged(nameof(LanguageIndex));
    }
}
