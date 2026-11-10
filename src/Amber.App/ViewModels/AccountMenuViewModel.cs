using System.Collections.ObjectModel;
using System.ComponentModel;
using Amber.Core.Auth;
using Amber.Core.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One row of the account menu.</summary>
public sealed class AccountMenuItem(Account account, bool isDefault)
{
    public Account Account { get; } = account;
    public string Name => Account.Name;
    public string Initial => Account.Name.Length > 0 ? Account.Name[..1].ToUpperInvariant() : "?";
    public bool IsDefault { get; } = isDefault;
    public string Detail => IsDefault ? L.F("shell.account_menu.default", Account.KindText) : Account.KindText;
}

/// <summary>
/// The account menu behind the sidebar avatar (official design §6.13): switch the default account in one
/// click, add or manage accounts, and change light/dark and the language without leaving the page.
/// Theme and language go through the settings page so both places stay in sync and are saved the same way.
/// </summary>
public sealed partial class AccountMenuViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ShellViewModel _shell;
    private SettingsViewModel? _settings;

    public AccountMenuViewModel(MainViewModel main, ShellViewModel shell)
    {
        _main = main;
        _shell = shell;
        L.Changed += () => Dispatcher.UIThread.Post(Refresh);
    }

    private HomeViewModel? Home => _main.Pages.OfType<HomeViewModel>().FirstOrDefault();

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

    public ObservableCollection<AccountMenuItem> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Raised when a menu action is done and the menu should close.</summary>
    public event Action? Done;

    /// <summary>Rebuild the account rows (called when the menu opens and when the language changes).</summary>
    public void Refresh()
    {
        Accounts.Clear();
        if (Home is { } home)
            foreach (var a in home.Accounts) Accounts.Add(new AccountMenuItem(a, ReferenceEquals(a, home.SelectedAccount)));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(LanguageIndex));
        OnThemeChanged();
    }

    /// <summary>One click on a row makes it the default account.</summary>
    [RelayCommand]
    private void Switch(AccountMenuItem? item)
    {
        if (item is not null && Home is { } home) home.SelectedAccount = item.Account;
        Refresh();
        Done?.Invoke();
    }

    [RelayCommand]
    private void AddAccount()
    {
        Done?.Invoke();
        _shell.OpenAccountsCommand.Execute(null);
    }

    [RelayCommand]
    private void Manage()
    {
        Done?.Invoke();
        _shell.OpenAccountsCommand.Execute(null);
    }

    [RelayCommand]
    private void About()
    {
        Done?.Invoke();
        _shell.Navigate("about");
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
        if (e.PropertyName is nameof(SettingsViewModel.SelectedThemeIndex) or null) OnThemeChanged();
        if (e.PropertyName is nameof(SettingsViewModel.SelectedLanguageIndex) or null) OnPropertyChanged(nameof(LanguageIndex));
    }

    private void OnThemeChanged()
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }
}
