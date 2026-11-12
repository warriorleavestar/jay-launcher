using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Navigation;

namespace Amber.Core.Tests;

/// <summary>
/// The shell's fourth entry and what moved around it: "Friends", the "Me" page behind the avatar (which took over the
/// account menu's light/dark and language switches) and the network status in the top bar.
/// </summary>
public sealed class ShellPeopleViewModelTests
{
    private static void WithMain(Action<LauncherService, MainViewModel, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-shell-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var main = new MainViewModel(service);
                // Friends are kept under the data folder: this test keeps its own.
                main.Multiplayer.ContactsDir = root;
                test(service, main, root);
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static byte[] Key(byte n)
    {
        var key = new byte[32];
        key[0] = n;
        return key;
    }

    [Fact]
    public void ShellHasFourEntriesAndTheAvatarOpensMe()
    {
        WithMain((_, main, _) =>
        {
            var shell = main.Shell;
            Assert.Equal(["library", "discover", "multiplayer", "people"], shell.Sections.Select(s => s.Id));
            Assert.Equal("Ctrl+4", shell.AllSections.Single(s => s.Id == "people").Shortcut);

            shell.GoSectionCommand.Execute("people");
            Assert.IsType<PeopleViewModel>(main.CurrentPage);
            Assert.Equal("people", shell.Router.Current.ToString());
            Assert.Equal("people", shell.SelectedSection?.Id);
            Assert.Equal(L.T("shell.nav.people"), shell.Heading);

            shell.OpenMeCommand.Execute(null);
            Assert.IsType<AccountsViewModel>(main.CurrentPage);
            Assert.Equal("me", shell.Router.Current.ToString());
            Assert.True(shell.IsMeActive);
            Assert.Null(shell.SelectedSection);
            Assert.Equal(L.T("me.title"), shell.Heading);

            // The route saved before the page was renamed still lands there.
            shell.Navigate("library");
            shell.Navigate("accounts");
            Assert.IsType<AccountsViewModel>(main.CurrentPage);
            Assert.Equal("me", shell.Router.Current.ToString());
            Assert.Equal(shell.Router.Current, shell.RouteOf(main.CurrentPage));
        });
    }

    [Fact]
    public void MePageSharesAppearanceWithSettingsAndShowsTheFriendCode()
    {
        WithMain((service, main, _) =>
        {
            var me = main.Pages.OfType<AccountsViewModel>().Single();
            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            var appearance = me.Appearance!;
            var theme = settings.SelectedThemeIndex;
            try
            {
                appearance.IsDarkTheme = true;
                Assert.Equal(2, settings.SelectedThemeIndex);
                Assert.Equal("dark", service.Settings.Theme);
                settings.SelectedThemeIndex = 1;
                Assert.True(appearance.IsLightTheme);
                Assert.False(appearance.IsDarkTheme);
                Assert.Equal(settings.LanguageOptions.Length, appearance.LanguageOptions.Length);
                Assert.Equal(settings.SelectedLanguageIndex, appearance.LanguageIndex);
            }
            finally
            {
                settings.SelectedThemeIndex = theme;
            }

            // The friend code and this device's safety code are the multiplayer identity's, not copies.
            Assert.True(me.ShowFriendCode);
            Assert.StartsWith("JAYC1-", me.MyFriendCode);
            Assert.Equal(main.Multiplayer.MyContactCode, me.MyFriendCode);
            Assert.Equal(main.Multiplayer.MyFingerprint, me.ThisDeviceCode);
            Assert.Equal(L.T(me.HasJayAccounts ? "me.devices.with_jay" : "me.devices.no_jay"), me.DevicesNote);

            me.OpenPeopleCommand.Execute(null);
            Assert.Equal(Section.People, main.Shell.Router.CurrentSection);
            me.OpenAboutCommand.Execute(null);
            Assert.Equal(Section.About, main.Shell.Router.CurrentSection);
            me.OpenDeviceSettingsCommand.Execute(null);
            Assert.Equal("play/settings#IdentityCard", main.Shell.Router.Current.ToString());
            me.OpenThemesCommand.Execute(null);
            Assert.Equal("settings#ThemePack", main.Shell.Router.Current.ToString());
        });
    }

    [Fact]
    public void FriendsPageListsPeopleKeepsTheSelectionAndLinksToWhereTheyAreManaged()
    {
        WithMain((_, main, _) =>
        {
            var mp = main.Multiplayer;
            var people = main.Pages.OfType<PeopleViewModel>().Single();
            Assert.True(people.IsEmpty);
            Assert.False(people.HasFriends);
            Assert.False(people.CanDeliver);
            Assert.True(people.ShowConnect);
            Assert.Equal(L.T("people.delivery.none"), people.DeliveryText);

            mp.Contacts.Import([
                new Contact(Key(1), "Bob", true, DateTimeOffset.UtcNow),
                new Contact(Key(2), "Sam", true, DateTimeOffset.UtcNow),
            ]);
            mp.RefreshContacts();
            Assert.True(people.HasFriends);
            Assert.False(people.IsEmpty);
            Assert.True(people.ShowPickHint);
            Assert.Equal(2, people.Friends.Count);

            people.Selected = people.Friends.First(f => f.Name == "Sam");
            Assert.True(people.HasSelected);
            Assert.False(people.ShowPickHint);
            Assert.True(people.SelectedHasNoJay);
            // The multiplayer page rebuilds its list from scratch: the same person stays selected.
            mp.RefreshContacts();
            Assert.Equal("Sam", people.Selected?.Name);
            Assert.Contains(people.Selected, people.Friends);

            // No room open: nothing to invite to.
            Assert.False(people.CanInvite);
            Assert.True(people.ShowInviteHint);
            mp.Mode = "host";
            Assert.True(people.CanInvite);
            mp.Mode = "";
            Assert.False(people.CanInvite);

            people.ManageCommand.Execute(null);
            Assert.Equal("play/settings#ContactsCard", main.Shell.Router.Current.ToString());
            people.ConnectCommand.Execute(null);
            Assert.Equal("play/public", main.Shell.Router.Current.ToString());
        });
    }

    [Fact]
    public void NetworkStatusListsBasicRelaysAndEachPublicNetwork()
    {
        WithMain((_, main, _) =>
        {
            var mp = main.Multiplayer;
            var network = main.Shell.Network;
            // Jay ships no public network servers: only the basic relays, off until a room needs them.
            var relays = Assert.Single(network.Rows);
            Assert.True(relays.IsBaseRelays);
            Assert.False(relays.CanSwitch);
            Assert.True(relays.IsOff);
            Assert.Equal(L.T("shell.network.can.invites"), relays.CanText);
            Assert.Equal(L.T("shell.network.none"), network.StatusText);
            Assert.True(network.IsOff);
            Assert.True(network.ShowEmptyNote);
            Assert.True(network.CanAddNetworks);

            mp.Mode = "host";
            Assert.Equal(L.T("shell.network.relays"), network.StatusText);
            Assert.True(network.IsOn);
            Assert.Same(relays, network.Rows[0]); // the row object stays (keyboard focus in the panel)
            Assert.True(relays.IsOn);
            Assert.Equal(L.T("shell.network.relays.in_use"), relays.Detail);
            mp.Mode = "";
            Assert.True(network.IsOff);

            // A network the player added, not reviewed, with servers: a row with a switch, off.
            mp.NetworkChoices.Add(new NetworkChoice("friends-net", "Friends net", Reviewed: false, NodeCount: 2, Signer: "ab12"));
            Assert.Equal(2, network.Rows.Count);
            var row = network.Rows[1];
            Assert.Equal("Friends net", row.Name);
            Assert.True(row.IsUnreviewed);
            Assert.Equal(L.T("shell.network.trust.unreviewed"), row.TrustText);
            Assert.True(row.CanSwitch);
            Assert.False(row.Wanted);
            Assert.Contains(L.T("shell.network.can.public_rooms"), row.CanText);
            Assert.Contains(L.T("shell.network.can.messages"), row.CanText);
            Assert.False(network.ShowEmptyNote);
            Assert.Equal(L.T("shell.network.none"), network.StatusText);

            var closed = 0;
            network.Done += () => closed++;
            network.AddNetworkCommand.Execute(null);
            Assert.Equal(1, closed);
            Assert.Equal("play/settings#NetworksCard", main.Shell.Router.Current.ToString());
            network.OpenSettingsCommand.Execute(null);
            Assert.Equal(2, closed);
            Assert.Equal("play/settings", main.Shell.Router.Current.ToString());
        });
    }
}
