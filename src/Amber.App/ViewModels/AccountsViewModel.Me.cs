using System.ComponentModel;
using Amber.Core.Identity;
using Amber.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// The rest of the "Me" page: my friend code, my devices and the quick appearance switches. The multiplayer page owns
/// the identity and the friend code; this page shows them and links to where they are managed.
/// </summary>
public sealed partial class AccountsViewModel
{
    /// <summary>Set by the main view model: the identity behind the friend code and the device card.</summary>
    public MultiplayerViewModel? Multiplayer
    {
        get;
        set
        {
            if (field is not null) field.PropertyChanged -= OnMultiplayerChanged;
            field = value;
            if (value is not null) value.PropertyChanged += OnMultiplayerChanged;
            OnPropertyChanged((string?)null);
        }
    }

    /// <summary>Light/dark and the language (set by the main view model).</summary>
    public QuickAppearanceViewModel? Appearance { get; set; }

    /// <summary>Go to a route (set by the main view model).</summary>
    public Action<string>? Navigate { get; set; }

    public string MyFriendCode => Multiplayer?.MyContactCode ?? "";

    /// <summary>"For a child" turns the friend code off together with the public networks.</summary>
    public bool ShowFriendCode => Multiplayer?.ShowPublicNetworks ?? false;

    public string ThisDeviceName => JayAccountStore.ThisDeviceName();

    /// <summary>"Safety code: …" of this device's multiplayer identity.</summary>
    public string ThisDeviceCode => Multiplayer?.MyFingerprint ?? "";

    public string DevicesNote => L.T(HasJayAccounts ? "me.devices.with_jay" : "me.devices.no_jay");

    private void OnMultiplayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MultiplayerViewModel.MyFingerprint) or nameof(MultiplayerViewModel.MyContactCode) or null)
        {
            OnPropertyChanged(nameof(MyFriendCode));
            OnPropertyChanged(nameof(ThisDeviceCode));
        }
        if (e.PropertyName is nameof(MultiplayerViewModel.ShowPublicNetworks) or null) OnPropertyChanged(nameof(ShowFriendCode));
    }

    partial void OnJayAccountCountChanged(int value) => OnPropertyChanged(nameof(DevicesNote));

    [RelayCommand]
    private async Task CopyFriendCodeAsync()
    {
        if (MyFriendCode.Length > 0 && await Services.SensitiveClipboard.Shared.CopyAsync(MyFriendCode))
            SetStatus(StatusMessage.Success("multiplayer.contacts.code_copied"));
    }

    [RelayCommand]
    private void OpenPeople() => Navigate?.Invoke("people");

    /// <summary>The safety code and "reset identity" live in the multiplayer settings.</summary>
    [RelayCommand]
    private void OpenDeviceSettings() => Navigate?.Invoke("play/settings#IdentityCard");

    [RelayCommand]
    private void OpenThemes() => Navigate?.Invoke("settings#ThemePack");

    [RelayCommand]
    private void OpenAbout() => Navigate?.Invoke("about");
}
