using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// The multiplayer lobby (UI restructure: people, rooms, networks): the four choices on the open-a-room card, the
/// component as a step on the buttons, the one paste box, "where to play" and the sub-views.
/// </summary>
public sealed class LobbyOpenRoomTests
{
    private static (LauncherService Service, MultiplayerViewModel Vm, List<string?> Changed) Create()
    {
        var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { IsComponentReady = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        return (service, vm, changed);
    }

    [Fact]
    public void DefaultsOpenARoomWithoutTouchingAnyChoice() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        Assert.Equal(RoomAccess.Invited, vm.OpenAccess);
        Assert.Equal(RoomRoute.Auto, vm.OpenRoute);
        Assert.Equal(nameof(RoomProtection.Standard), vm.SelectedProtection?.Id);
        Assert.Equal(nameof(RoomScope.Game), vm.SelectedScope.Id);
        Assert.Equal(3, vm.WhoChoices.Count);
        Assert.Equal(3, vm.RouteChoices.Count);
        Assert.Equal(3, vm.ProtectionChoices.Count);
        Assert.Equal(2, vm.ScopeChoices.Count);
        // Every option has a name and a line that says what it means.
        foreach (var choice in vm.WhoChoices.Concat(vm.RouteChoices).Concat(vm.ProtectionChoices).Concat(vm.ScopeChoices))
        {
            Assert.False(choice.Label.StartsWith("multiplayer.", StringComparison.Ordinal), choice.Id);
            Assert.False(choice.Detail.StartsWith("multiplayer.", StringComparison.Ordinal), choice.Id);
        }
        Assert.Equal(L.T("multiplayer.lobby.host_button"), vm.HostButtonText);
    });

    [Fact]
    public void FriendsAndPublicAreGreyedOutWithTheReasonUntilANetworkIsConnected() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        var (friends, open) = (vm.WhoChoices[1], vm.WhoChoices[2]);
        Assert.False(open.IsEnabled);
        Assert.Equal(L.T("multiplayer.open.blocked.network"), open.Line);
        Assert.False(friends.IsEnabled);

        // Picking a greyed-out option does nothing.
        vm.SelectedWho = open;
        Assert.Equal(RoomAccess.Invited, vm.OpenAccess);

        vm.IsPublicConnected = true;
        Assert.True(open.IsEnabled);
        Assert.Equal(L.T("multiplayer.open.who.public_detail"), open.Line);
        // No friends in this profile: said in its own words.
        Assert.False(friends.IsEnabled);
        Assert.Equal(L.T("multiplayer.open.blocked.no_friends"), friends.Line);

        vm.SelectedWho = open;
        Assert.Equal(RoomAccess.Public, vm.OpenAccess);
        Assert.True(vm.ShowPublicOptions);
        Assert.Contains(nameof(MultiplayerViewModel.ShowPublicOptions), changed);

        // The network drops: back to "only people I invite".
        vm.IsPublicConnected = false;
        Assert.Equal(RoomAccess.Invited, vm.OpenAccess);
        Assert.False(vm.ShowPublicOptions);
        Assert.Same(vm.WhoChoices[0], vm.SelectedWho);
    });

    [Fact]
    public void NoServerMakesASealedCodeAndAsksNeitherProtectionNorFriends() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        vm.Instances.Clear();
        vm.Instances.Add("1.21.4");
        vm.StartInstance = _ => { };
        vm.IsPublicConnected = true;
        vm.SelectedWho = vm.WhoChoices[2];
        Assert.True(vm.StartsGameToHost);

        vm.SelectedRoute = vm.RouteChoices[2];
        Assert.True(vm.IsServerlessChosen);
        Assert.Equal(L.T("multiplayer.open.make_sealed"), vm.HostButtonText);
        Assert.Contains(nameof(MultiplayerViewModel.HostButtonText), changed);
        // A serverless room is not "play and open": the sealed code is made from a game that is already open.
        Assert.False(vm.StartsGameToHost);
        Assert.False(vm.ProtectionEnabled);
        Assert.All(vm.ProtectionChoices, c => Assert.False(c.IsEnabled));
        Assert.Equal(RoomAccess.Invited, vm.OpenAccess);
        Assert.Equal(L.T("multiplayer.open.blocked.serverless"), vm.WhoChoices[2].Line);

        // Relays only is a plain choice: the button and the other rows stay as they are.
        vm.SelectedRoute = vm.RouteChoices[1];
        Assert.Equal(RoomRoute.RelayOnly, vm.OpenRoute);
        Assert.True(vm.ProtectionEnabled);
        Assert.True(vm.WhoChoices[2].IsEnabled);
        Assert.Equal(L.T("multiplayer.lobby.play_and_host"), vm.HostButtonText);
    });

    [Fact]
    public void ProtectionIsAViewOverTheTransportAndSaysWhenItWasSetByHand() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        vm.SelectedProtection = vm.ProtectionChoices[1];
        Assert.Equal(TransportMode.High, vm.SelectedTransport.Mode);
        Assert.False(vm.DisguiseAsMinecraft);
        Assert.False(vm.IsProtectionCustom);

        vm.SelectedTransport = vm.TransportChoices.First(c => c.Mode == TransportMode.General);
        Assert.True(vm.IsProtectionCustom);
        Assert.Null(vm.SelectedProtection);
        Assert.Equal(L.F("multiplayer.open.protection.custom", vm.SelectedTransport.Label), vm.ProtectionCustomText);
        Assert.Contains(nameof(MultiplayerViewModel.IsProtectionCustom), changed);

        vm.ResetProtectionCommand.Execute(null);
        Assert.Equal(nameof(RoomProtection.Standard), vm.SelectedProtection?.Id);
        Assert.Equal(TransportMode.Minecraft, vm.SelectedTransport.Mode);

        if (NoiseHandshake.HybridSupported)
        {
            vm.SelectedProtection = vm.ProtectionChoices[2];
            Assert.Equal(TransportMode.Extreme, vm.SelectedTransport.Mode);
            Assert.True(vm.DisguiseAsMinecraft);
            Assert.Equal(nameof(RoomProtection.Highest), vm.SelectedProtection?.Id);
        }
        else Assert.Equal(L.T("multiplayer.open.blocked.highest"), vm.ProtectionChoices[2].Line);
    });

    [Fact]
    public void ScopeFollowsWholeLanAndIsGreyedOutWhereItCannotWork() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        Assert.Equal(vm.CanUseFullLan, vm.ScopeChoices[1].IsEnabled);
        if (vm.CanUseFullLan)
        {
            vm.SelectedScope = vm.ScopeChoices[1];
            Assert.True(vm.FullLanMode);
            Assert.Same(vm.ScopeChoices[1], vm.SelectedScope);
            vm.SelectedScope = vm.ScopeChoices[0];
            Assert.False(vm.FullLanMode);
        }
        else
        {
            Assert.NotEqual("", vm.ScopeChoices[1].Line);
            vm.SelectedScope = vm.ScopeChoices[1];
            Assert.False(vm.FullLanMode);
        }
    });

    [Fact]
    public void LastRoomRemembersTheChoicesByName() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        vm.IsPublicConnected = true;
        vm.PickOpenChoices(nameof(RoomAccess.Public), nameof(RoomRoute.RelayOnly), nameof(RoomProtection.Stronger), nameof(RoomScope.Game));
        Assert.Equal(RoomAccess.Public, vm.OpenAccess);
        Assert.Equal(RoomRoute.RelayOnly, vm.OpenRoute);
        Assert.Equal(TransportMode.High, vm.SelectedTransport.Mode);

        // Names from an older or newer version, and options that cannot be picked today, leave things as they are.
        vm.PickOpenChoices("Everyone", "Tor", null, "Planet");
        Assert.Equal(RoomAccess.Public, vm.OpenAccess);
        Assert.Equal(RoomRoute.RelayOnly, vm.OpenRoute);
        vm.IsPublicConnected = false;
        vm.PickOpenChoices(nameof(RoomAccess.Public), nameof(RoomRoute.Auto), null, null);
        Assert.Equal(RoomAccess.Invited, vm.OpenAccess);
        Assert.Equal(RoomRoute.Auto, vm.OpenRoute);

        service.Settings.MultiplayerLastRoom = new LastRoom { World = "World", Access = nameof(RoomAccess.Invited), Route = nameof(RoomRoute.RelayOnly), Scope = nameof(RoomScope.Game) };
        vm.NotifyLastRoom();
        Assert.Equal(L.F("multiplayer.lobby.last_room", string.Join(" · ", "World", L.T("multiplayer.invite.terms_ask"),
            L.T("multiplayer.open.who.invited"), L.T("multiplayer.open.route.relay"))), vm.LastRoomText);
        service.Settings.MultiplayerLastRoom = null;
    });

    [Fact]
    public void TheMissingComponentIsAStepOnBothButtons() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        vm.IsComponentReady = false;
        Assert.True(vm.ShowComponentStep);
        Assert.Equal(vm.ComponentStepText, vm.HostButtonText);
        Assert.Contains("MB", vm.ComponentStepText);
        Assert.Contains(nameof(MultiplayerViewModel.HostButtonText), changed);
        // Nothing to join with yet: the join button stays "Join".
        Assert.Equal(L.T("multiplayer.lobby.join_button"), vm.JoinButtonText);

        var installs = 0;
        vm.TestInstallComponent = () =>
        {
            installs++;
            vm.IsComponentReady = true;
            return Task.CompletedTask;
        };
        vm.JoinCode = JayInvite.Create(new byte[16], TunnelSettings.Default).ToString();
        Assert.Equal(vm.ComponentStepText, vm.JoinButtonText);
        vm.JoinCommand.Execute(null);
        Assert.Equal(1, installs);
        // Installed: the same button now joins, and nobody was knocked on by the install click.
        Assert.False(vm.ShowComponentStep);
        Assert.False(vm.IsKnocking);
        Assert.Equal(L.T("multiplayer.lobby.join_button"), vm.JoinButtonText);
    });

    [Theory]
    [InlineData("JAYS2-0123456789ABCDEFGHJK", "multiplayer.lobby.join.server", false)]
    [InlineData("JAYSS1-QUJDREVGR0hJSktMTU5PUA", "multiplayer.lobby.join.server", true)]
    [InlineData("JAYC1-ABCD-EFGH-JKMN-PQRS-TVWX", "multiplayer.lobby.join.friend", false)]
    [InlineData("JAYNET2-ABCDEFGH", "multiplayer.lobby.join.network", false)]
    [InlineData("JAYDS1-QUJDREVGR0hJSktMTU5PUA", "multiplayer.lobby.join_button", true)]
    [InlineData("hello", "multiplayer.lobby.join_button", false)]
    public void ThePasteBoxSaysWhatItsButtonWillDo(string code, string button, bool passphrase) => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        vm.JoinCode = code;
        Assert.Equal(L.T(button), vm.JoinButtonText);
        Assert.Equal(passphrase, vm.JoinNeedsPassphrase);
        Assert.Contains(nameof(MultiplayerViewModel.JoinButtonText), changed);
        Assert.Contains(nameof(MultiplayerViewModel.JoinNeedsPassphrase), changed);
    });

    [Fact]
    public void AServerInviteStaysInThePasteBoxAndABadOneSavesNothing() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        Assert.True(vm.PasteCode("JAYS2-0123456789ABCDEFGHJK"));
        Assert.True(vm.IsPlayTab);
        Assert.Equal("JAYS2-0123456789ABCDEFGHJK", vm.JoinCode);
        Assert.Equal(L.T("multiplayer.lobby.code.server"), vm.JoinCodeRecognition);

        var before = vm.JayServers.Servers.Count;
        vm.JoinCommand.Execute(null);
        Assert.Equal(before, vm.JayServers.Servers.Count);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsKnocking);
        // The code is still there to fix, and the page did not go anywhere.
        Assert.True(vm.IsPlayTab);
        Assert.Equal("JAYS2-0123456789ABCDEFGHJK", vm.JoinCode);
    });

    [Fact]
    public void WhereToPlayFiltersOneList() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        Assert.Equal(WhereFilter.All, vm.Where);
        Assert.True(vm.IsWhereAll);
        Assert.Equal(vm.JayServers.Servers.Count == 0, vm.ShowWhereEmpty);
        Assert.False(vm.ShowFriendRoomsEmpty);
        Assert.False(vm.ShowPublicRoomList);

        vm.IsWhereFriends = true;
        Assert.Equal(WhereFilter.Friends, vm.Where);
        Assert.True(vm.ShowFriendRoomsEmpty);
        Assert.False(vm.ShowWhereEmpty);
        Assert.Contains(nameof(MultiplayerViewModel.ShowFriendRoomsEmpty), changed);

        // Public: says where public rooms come from, or offers to connect; the list shows once connected.
        vm.Where = WhereFilter.Public;
        Assert.True(vm.ShowWherePublicSetup || vm.ShowWherePublicConnect);
        Assert.False(vm.ShowPublicRoomList);
        vm.IsPublicConnected = true;
        Assert.True(vm.ShowPublicRoomList);
        Assert.False(vm.ShowWherePublicSetup);
        Assert.False(vm.ShowWherePublicConnect);
        // Under "all" the public part (search, filters, "no rooms right now") stays out of the way until there is a room.
        vm.Where = WhereFilter.All;
        Assert.False(vm.ShowPublicRoomList);
        Assert.False(vm.ShowPublicRoomPassword);
        vm.IsPublicConnected = false;

        vm.Where = WhereFilter.Servers;
        Assert.Equal(vm.JayServers.Servers.Count == 0, vm.ShowSavedServersEmpty);
        Assert.Equal(vm.JayServers.Servers.Count > 0, vm.ShowSavedServers);
        Assert.False(vm.ShowFriendRoomsEmpty);
    });

    [Fact]
    public void ARoomAFriendInvitedMeToIsListedUntilItRunsOut() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        using var friend = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        var from = new Contact(friend.PublicKey, "Robin", true, DateTimeOffset.UtcNow);
        MailedInvite Mail(TimeSpan lifetime, string room) => new(from,
            new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite,
            host.PublicKey, room, DateTimeOffset.UtcNow + lifetime);

        vm.ShowMailedInvites([Mail(TimeSpan.FromMinutes(30), "Robin's world")]);
        var row = Assert.Single(vm.FriendRooms);
        Assert.Equal("Robin's world", row.Title);
        Assert.Contains(L.F("multiplayer.where.friend_room.from", "Robin"), row.Meta);
        Assert.True(vm.ShowFriendRooms);
        Assert.False(vm.ShowWhereEmpty);
        // The same invite arriving again (mail is picked up on a timer) is one row.
        vm.ShowMailedInvites([row.Invite]);
        Assert.Single(vm.FriendRooms);

        // "Not now" in the request bar closes the request only.
        var request = Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");
        service.Requests.Resolve(request, "ignore", Amber.Core.Activity.RequestSource.Bar);
        Assert.Single(vm.FriendRooms);

        // An invite that has run out leaves the list the next time it is looked at.
        vm.ShowMailedInvites([Mail(TimeSpan.FromSeconds(-1), "Old room")]);
        vm.PruneFriendRooms();
        Assert.Equal(["Robin's world"], vm.FriendRooms.Select(r => r.Title));

        // "For a child" has no networks: the friends and public filters fall back to "all".
        vm.Where = WhereFilter.Friends;
        Assert.Equal(WhereFilter.Friends, vm.Where);
        foreach (var pending in service.Requests.Pending.ToList()) service.Requests.Withdraw(pending.Spec.DedupeKey ?? "");
    });

    [Fact]
    public void SubViewsHaveANameAndAWayBack() => UiTestThread.Run(() =>
    {
        var (service, vm, changed) = Create();
        using var s = service;
        Assert.False(vm.IsSubView);
        Assert.Equal("", vm.SubViewTitle);

        vm.OpenMyServerCommand.Execute(null);
        Assert.True(vm.IsServersTab);
        Assert.True(vm.IsSubView);
        Assert.Equal(L.T("multiplayer.subview.server"), vm.SubViewTitle);
        Assert.Contains(nameof(MultiplayerViewModel.IsSubView), changed);
        Assert.Contains(nameof(MultiplayerViewModel.SubViewTitle), changed);

        vm.OpenMultiplayerSettingsCommand.Execute(null);
        Assert.Equal(L.T("multiplayer.tab.settings"), vm.SubViewTitle);
        vm.ShowRoute("public");
        Assert.Equal(L.T("multiplayer.subview.network"), vm.SubViewTitle);

        vm.ShowTabCommand.Execute("play");
        Assert.False(vm.IsSubView);
    });
}
