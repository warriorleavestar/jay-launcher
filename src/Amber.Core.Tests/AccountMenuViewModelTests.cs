using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Navigation;

namespace Amber.Core.Tests;

/// <summary>The account menu (§6.13): one-click switching, theme and language shared with the settings page.</summary>
public sealed class AccountMenuViewModelTests
{
    [Fact]
    public void SwitchesDefaultAndSharesThemeAndLanguageWithSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-amenu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var main = new MainViewModel(service);
                var menu = main.Shell.AccountMenu;
                var settings = main.Pages.OfType<SettingsViewModel>().First();

                // Accounts live in the shared test data folder: other tests may add their own, so look only at ours.
                var alex = service.Accounts.AddOffline("MenuAlex");
                var steve = service.Accounts.AddOffline("MenuSteve");
                service.NotifyAccountsChanged();
                menu.Refresh();
                Assert.True(menu.HasAccounts);
                Assert.Contains(menu.Accounts, a => a.Account.Id == alex.Id);
                Assert.Single(menu.Accounts, a => a.IsDefault);

                var other = menu.Accounts.First(a => (a.Account.Id == alex.Id || a.Account.Id == steve.Id) && !a.IsDefault);
                var closed = 0;
                menu.Done += () => closed++;
                menu.SwitchCommand.Execute(other);
                Assert.Equal(1, closed);
                Assert.Equal(other.Account.Id, service.Accounts.Store.SelectedId);
                Assert.Equal(other.Account.Id, menu.Accounts.Single(a => a.IsDefault).Account.Id);

                menu.IsDarkTheme = true;
                Assert.Equal(2, settings.SelectedThemeIndex);
                Assert.Equal("dark", service.Settings.Theme);
                settings.SelectedThemeIndex = 1;
                Assert.True(menu.IsLightTheme);
                Assert.False(menu.IsDarkTheme);

                Assert.Equal(settings.LanguageOptions.Length, menu.LanguageOptions.Length);
                Assert.Equal(settings.SelectedLanguageIndex, menu.LanguageIndex);

                menu.AboutCommand.Execute(null);
                Assert.Equal(2, closed);
                Assert.Equal(Section.About, main.Shell.Router.CurrentSection);
                service.Accounts.Store.Remove(alex);
                service.Accounts.Store.Remove(steve);
                service.Accounts.Store.Save();
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
