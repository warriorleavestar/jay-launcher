using System.ComponentModel;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;

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
        // "No server" means no relay either: a direct room is given none to fall back to, whatever relays are set up.
        vm.TestPeers = ["tcp://relay.example:11010"];
        Assert.Empty(vm.MakeDirectOptions().FallbackNodes);

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
        // A room remembered with terms the panel never makes together says what "open like last time" will really do.
        service.Settings.MultiplayerLastRoom = new LastRoom { World = "World", Uses = InviteBook.UnlimitedUses, LifetimeMinutes = 60, AutoApprove = true, Access = nameof(RoomAccess.Invited) };
        vm.NotifyLastRoom();
        Assert.Contains(L.T("multiplayer.invite.terms_ask"), vm.LastRoomText);
        Assert.DoesNotContain(L.T("multiplayer.invite.terms_auto"), vm.LastRoomText);
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

    /// <summary>The name of a guest turned away from a "relays only" room is theirs to pick: one line a minute per name.</summary>
    [Fact]
    public void AGuestTurnedAwayForRelaysOnlyIsMentionedAtMostOnceAMinute() => UiTestThread.Run(() =>
    {
        var (_, vm, _) = Create();
        vm.OnRelayOnlyRefused("Alex");
        Assert.Equal("multiplayer.room.relay_only_old", vm.StatusInfo?.Key);
        vm.SetStatus(StatusMessage.Info("common.raw", "other"));
        vm.OnRelayOnlyRefused("Alex");
        Assert.Equal("common.raw", vm.StatusInfo?.Key);
        vm.OnRelayOnlyRefused("Sam");
        Assert.Equal("multiplayer.room.relay_only_old", vm.StatusInfo?.Key);
        // A flood of made-up names stops showing once 32 are waiting out their minute.
        for (var i = 0; i < 40; i++) vm.OnRelayOnlyRefused("guest" + i);
        vm.SetStatus(StatusMessage.Info("common.raw", "other"));
        vm.OnRelayOnlyRefused("one more");
        Assert.Equal("common.raw", vm.StatusInfo?.Key);
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

        // "Not now" in the request bar lets the room go with its invite; the same invite arriving again stays away.
        var request = Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");
        service.Requests.Resolve(request, "ignore", Amber.Core.Activity.RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.FriendRooms);
        Assert.Equal(0, vm.HeldFriendInvites);
        vm.ShowMailedInvites([row.Invite]);
        Assert.Empty(vm.FriendRooms);
        vm.ShowMailedInvites([Mail(TimeSpan.FromMinutes(30), "Robin's world")]);
        Assert.Single(vm.FriendRooms);

        // An invite that has already run out is not listed, and asks nothing.
        vm.ShowMailedInvites([Mail(TimeSpan.FromSeconds(-1), "Old room")]);
        vm.PruneFriendRooms();
        Assert.Equal(["Robin's world"], vm.FriendRooms.Select(r => r.Title));
        Assert.Contains("Robin's world", Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite").Spec.Title);

        // "For a child" has no networks: the friends and public filters fall back to "all".
        vm.Where = WhereFilter.Friends;
        Assert.Equal(WhereFilter.Friends, vm.Where);
        service.Settings.ChildMode.SetEnabled(true, DateTimeOffset.UtcNow);
        vm.ApplyChildMode();
        vm.Where = WhereFilter.Friends;
        Assert.Equal(WhereFilter.All, vm.Where);
        vm.Where = WhereFilter.Public;
        Assert.Equal(WhereFilter.All, vm.Where);
        foreach (var pending in service.Requests.Pending.ToList()) service.Requests.Withdraw(pending.Spec.DedupeKey ?? "");
    });

    /// <summary>
    /// A friend can mail any number of invites (new ids and tokens, no end date or a far one): the list, the invites held
    /// and the requests stay within the per-device and overall limits, and every row leaves by itself at the latest
    /// <see cref="MultiplayerViewModel.MaxFriendRoomAge"/> after it arrived.
    /// </summary>
    [Fact]
    public void AFloodOfInvitesFromFriendsStaysBoundedAndLeavesByItself() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var now = DateTimeOffset.UtcNow;
        vm.FriendRoomClock = () => now;
        using var host = X25519KeyPair.Generate();
        var friends = Enumerable.Range(0, 8).Select(_ => X25519KeyPair.Generate()).ToList();
        MailedInvite Mail(X25519KeyPair from, DateTimeOffset? expires, string room) => new(
            new Contact(from.PublicKey, "Friend", true, now),
            new InviteBook().Create(20, null, false, TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite,
            host.PublicKey, room, expires);
        int Requests() => service.Requests.Pending.Count(r => r.Spec.Kind == "contact-invite");
        void AssertBounded()
        {
            Assert.InRange(vm.FriendRooms.Count, 0, MultiplayerViewModel.MaxFriendRooms);
            Assert.Equal(vm.FriendRooms.Count, vm.HeldFriendInvites);
            Assert.Equal(vm.FriendRooms.Count, Requests());
            foreach (var group in vm.FriendRooms.GroupBy(r => r.Sender))
                Assert.InRange(group.Count(), 1, MultiplayerViewModel.MaxFriendRoomsPerDevice);
        }

        try
        {
            // One friend, 200 distinct invites with no end, one at a time and in batches: at most three rows, newest kept.
            var robin = friends[0];
            for (var i = 0; i < 100; i++) vm.ShowMailedInvites([Mail(robin, null, $"room {i}")]);
            AssertBounded();
            vm.ShowMailedInvites(Enumerable.Range(100, 100).Select(i => Mail(robin, now.AddYears(50), $"room {i}")).ToList());
            AssertBounded();
            Assert.Equal(["room 199", "room 198", "room 197"], vm.FriendRooms.Select(r => r.Title));

            // The same invite again is still one row.
            vm.ShowMailedInvites([vm.FriendRooms[0].Invite]);
            AssertBounded();
            Assert.Equal(3, vm.FriendRooms.Count);

            // Eight friends flooding together: never more than the overall limit.
            foreach (var friend in friends)
                vm.ShowMailedInvites(Enumerable.Range(0, 10).Select(i => Mail(friend, null, $"{i}")).ToList());
            AssertBounded();
            Assert.Equal(MultiplayerViewModel.MaxFriendRooms, vm.FriendRooms.Count);
            Assert.True(vm.WatchingFriendRoomExpiry);

            // A far end date doesn't keep a row: it goes at the latest MaxFriendRoomAge after it arrived.
            Assert.All(vm.FriendRooms, r => Assert.True(r.Until <= now + MultiplayerViewModel.MaxFriendRoomAge));
            now += MultiplayerViewModel.MaxFriendRoomAge;
            vm.PruneFriendRooms();
            Assert.Empty(vm.FriendRooms);
            Assert.Equal(0, vm.HeldFriendInvites);
            Assert.Equal(0, Requests());
            Assert.False(vm.WatchingFriendRoomExpiry);
        }
        finally
        {
            foreach (var friend in friends) friend.Dispose();
            foreach (var pending in service.Requests.Pending.ToList()) service.Requests.Withdraw(pending.Spec.DedupeKey ?? "");
        }
    });

    [Fact]
    public void AFriendsRoomStaysListedUntilTheHostHasAnswered() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        using var friend = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        var from = new Contact(friend.PublicKey, "Robin", true, DateTimeOffset.UtcNow);
        MailedInvite Mail(TimeSpan lifetime, string room) => new(from,
            new InviteBook().Create(20, TimeSpan.FromHours(12), false, TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite,
            host.PublicKey, room, DateTimeOffset.UtcNow + lifetime);
        var end = MultiplayerViewModel.KnockEnd.NotAnswered;
        var knocks = 0;
        vm.TestKnock = _ =>
        {
            knocks++;
            return Task.FromResult(end);
        };
        vm.ShowMailedInvites([Mail(TimeSpan.FromHours(12), "Robin's world")]);
        var row = Assert.Single(vm.FriendRooms);

        // The knock never got through (no connection, the host busy): the room is still there to try again.
        Wait(vm.JoinFriendRoomCommand.ExecuteAsync(row));
        Assert.Equal(1, knocks);
        Assert.Same(row, Assert.Single(vm.FriendRooms));
        Assert.DoesNotContain(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");

        // In the room: the row goes.
        end = MultiplayerViewModel.KnockEnd.Joined;
        Wait(vm.JoinFriendRoomCommand.ExecuteAsync(row));
        Assert.Equal(2, knocks);
        Assert.Empty(vm.FriendRooms);

        // "Join" on the request goes the same way, and the host saying the invite is over drops the row too.
        vm.ShowMailedInvites([Mail(TimeSpan.FromHours(12), "Second world")]);
        end = MultiplayerViewModel.KnockEnd.InviteSpent;
        var request = Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");
        service.Requests.Resolve(request, "join", Amber.Core.Activity.RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, knocks);
        Assert.Empty(vm.FriendRooms);

        // A request whose invite ran out in the meantime says so instead of doing nothing.
        vm.ShowMailedInvites([Mail(TimeSpan.FromMilliseconds(30), "Short world")]);
        request = Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");
        Thread.Sleep(60);
        service.Requests.Resolve(request, "join", Amber.Core.Activity.RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, knocks);
        Assert.Equal("multiplayer.where.friend_room.expired", vm.StatusInfo?.Key);
    });

    [Fact]
    public void FriendsWhoMayKnockEachGetTheirOwnInviteThatStillNeedsMyYes() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var root = Path.Combine(Path.GetTempPath(), "jay-lobby-friends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            vm.ContactsDir = root;
            byte[] Key(byte n) { var key = new byte[32]; key[0] = n; return key; }
            var now = DateTimeOffset.UtcNow;
            // Robin has two computers on one Jay account, Sam one; Kim's emoji were never compared.
            vm.Contacts.Import([
                new Contact(Key(1), "Robin", true, now) { JayAccount = "jay1robin" },
                new Contact(Key(2), "Robin's laptop", true, now.AddMinutes(-5)) { JayAccount = "jay1robin" },
                new Contact(Key(3), "Sam", true, now),
                new Contact(Key(4), "Kim", false, now),
            ]);
            vm.RefreshContacts();
            var credentials = RoomCredentials.Create(vm.Identity.PublicKey, 25565, TunnelSettings.Default);
            var lobby = new HostLobby(_ => new NoLobby(), () => [],
                book => new KnockServer(book, vm.Identity.KeyPair, () => credentials, _ => false, (_, _) => Task.FromResult(false), System.Net.IPAddress.Loopback, 0));
            try
            {
                vm.HostLobbyForTest(lobby, credentials);
                var mailed = new List<(Contact To, JayInvite Invite, DateTimeOffset Expires)>();
                vm.TestSendInvite = (to, invite, expires) =>
                {
                    mailed.Add((to, invite, expires));
                    return Task.FromResult(Guid.NewGuid().ToString("N"));
                };

                var told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                // Two friends were told; each verified device got its own invite, and Kim got none.
                Assert.Equal(2, told.Result);
                Assert.Equal([1, 2, 3], mailed.Select(m => (int)m.To.PublicKey[0]).Order());
                Assert.All(mailed, m => Assert.InRange(m.Expires - DateTimeOffset.UtcNow, TimeSpan.FromHours(11.9), TimeSpan.FromHours(12)));
                var made = lobby.Book.Pending;
                Assert.Equal(3, made.Count);
                Assert.All(made, invite =>
                {
                    // 20 knocks in 12 hours, only from that device, and I still say yes to each.
                    Assert.Equal(20, invite.Uses);
                    Assert.False(invite.AutoApprove);
                    Assert.True(invite.AskEachTime);
                    Assert.NotNull(invite.BoundTo);
                    Assert.Contains(mailed, m => m.To.PublicKey.AsSpan().SequenceEqual(invite.BoundTo) && m.Invite == invite.Invite);
                });
                Assert.Equal("multiplayer.open.friends_told", vm.StatusInfo?.Key);

                // Mail that can't be sent takes back the invites it was going to carry.
                vm.TestSendInvite = (_, _, _) => throw new IOException("offline");
                told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                Assert.Equal(0, told.Result);
                Assert.Equal(3, lobby.Book.Pending.Count);
                Assert.Equal("multiplayer.open.friends_not_told", vm.StatusInfo?.Key);

                // Taking one of Robin's computers off, then Sam altogether, ends the invites that were mailed to them.
                vm.Contacts.Remove(Key(2));
                vm.RefreshContacts();
                Assert.Equal([1, 3], lobby.Book.Pending.Select(i => (int)i.BoundTo![0]).Order());
                vm.Contacts.RemovePerson(Key(3));
                vm.RefreshContacts();
                Assert.Equal(1, Assert.Single(lobby.Book.Pending).BoundTo![0]);
                Assert.Single(vm.PendingInvites);

                // An invite bound to a key that I did not mail to a friend is not theirs to end.
                var other = lobby.Book.Create(1, TimeSpan.FromMinutes(10), false, credentials.Tunnel, boundTo: Key(9));
                vm.RefreshContacts();
                Assert.Contains(other, lobby.Book.Pending);

                // A contacts file that can't be read is not "everyone was removed": Robin keeps the invite.
                var broken = Path.Combine(root, "broken");
                Directory.CreateDirectory(broken);
                File.WriteAllText(Path.Combine(broken, ContactBook.FileName), "{ not json");
                vm.ContactsDir = broken;
                Assert.True(vm.Contacts.Unreadable);
                Assert.Contains(lobby.Book.Pending, i => i.BoundTo is [1, ..]);
                Assert.Equal(2, lobby.Book.Pending.Count);

                // The invites mailed for a room end with it: opening and closing rooms again and again keeps none.
                Assert.True(vm.FriendInvitesHeld > 0);
                vm.StopHostLobbyForTest();
                Assert.Equal(0, vm.FriendInvitesHeld);
                vm.ContactsDir = root;
                vm.TestSendInvite = (_, _, _) => Task.FromResult(Guid.NewGuid().ToString("N"));
                for (var round = 0; round < 5; round++)
                {
                    var again = new HostLobby(_ => new NoLobby(), () => [],
                        book => new KnockServer(book, vm.Identity.KeyPair, () => credentials, _ => false, (_, _) => Task.FromResult(false), System.Net.IPAddress.Loopback, 0));
                    vm.HostLobbyForTest(again, credentials);
                    Wait(vm.InviteFriendsToKnockAsync());
                    Assert.InRange(vm.FriendInvitesHeld, 1, 3);
                    vm.StopHostLobbyForTest();
                    Assert.Equal(0, vm.FriendInvitesHeld);
                }
            }
            finally
            {
                Wait(lobby.DisposeAsync().AsTask());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void FriendsCanKnockStaysPickedWhenTheContactListIsRebuilt() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var root = Path.Combine(Path.GetTempPath(), "jay-lobby-keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            vm.ContactsDir = root;
            byte[] Key(byte n) { var key = new byte[32]; key[0] = n; return key; }
            vm.Contacts.Import([new Contact(Key(1), "Robin", true, DateTimeOffset.UtcNow)]);
            vm.RefreshContacts();
            vm.IsPublicConnected = true;
            vm.SelectedWho = vm.WhoChoices[1];
            Assert.Equal(RoomAccess.Friends, vm.OpenAccess);

            // Adding a friend (or renaming one) rebuilds the list, which is empty for a moment: the choice stays.
            vm.Contacts.Import([new Contact(Key(2), "Sam", true, DateTimeOffset.UtcNow)]);
            vm.RefreshContacts();
            Assert.Equal(RoomAccess.Friends, vm.OpenAccess);

            // With no friends left it can't be kept.
            vm.Contacts.RemovePerson(Key(1));
            vm.Contacts.RemovePerson(Key(2));
            vm.RefreshContacts();
            Assert.Empty(vm.ContactList);
            Assert.Equal(RoomAccess.Invited, vm.OpenAccess);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ADirectJoinThatCannotConnectIsNotAJoin() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var dir = Path.Combine(Path.GetTempPath(), "jay-lobby-direct-" + Guid.NewGuid().ToString("N"));
        // No relays to fall back to ("No server"), and nothing to punch towards: the connection fails.
        var direct = new DirectSession(null!, dir, null, false, new DirectOptions([], false, [], false, false, false, true), new DiagnosticLog());
        var room = new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 25565);
        var joined = vm.CompleteDirectJoinAsync(direct, room, new KnockOutcome(null, null, new byte[32]));
        Wait(joined);
        Assert.False(joined.Result);
        Assert.False(vm.IsDirect);
        Assert.Equal("", vm.Mode);
        Assert.Equal("multiplayer.direct.connect_failed", vm.StatusInfo?.Key);
    });

    [Fact]
    public void ANoServerRoomsKnockLobbyReachesNoRelay() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        vm.TestPeers = ["tcp://relay.example.org:11010"];
        var credentials = RoomCredentials.Create(vm.Identity.PublicKey, 25565, TunnelSettings.Default);
        var members = new MemberTable();

        // An ordinary invite room: guests knock through the relays.
        vm.StartHostLobbyForTest(credentials, members);
        Assert.Equal(["tcp://relay.example.org:11010"], vm.HostLobbyPeersForTest);
        vm.StopHostLobbyForTest();
        Assert.Null(vm.HostLobbyPeersForTest);

        // "No server": the knock lobby takes no relay at all, only what the direct bridge hands it on loopback.
        var dir = Path.Combine(Path.GetTempPath(), "jay-lobby-serverless-" + Guid.NewGuid().ToString("N"));
        var direct = new DirectSession(null!, dir, credentials.ToHostRoom(members, "Host"), true,
            new DirectOptions([], false, [], false, false, false, true), new DiagnosticLog());
        try
        {
            vm.StartHostLobbyForTest(credentials, members, new InviteBook { KeepLobbyKey = true }, direct);
            Assert.Empty(vm.HostLobbyPeersForTest!);
            Assert.Empty(vm.MakeDirectOptions().FallbackNodes);
            // Nor is it registered with a public network, while an ordinary hosted room may be.
            vm.Mode = "host";
            Assert.True(vm.MayAnnounce);
            Assert.True(vm.ShowPublishRoom);
            vm.IsDirect = true;
            Assert.False(vm.MayAnnounce);
            // [Publish…] is not offered, and publishing anyway says why instead of "published".
            Assert.False(vm.ShowPublishRoom);
            Wait(vm.PublishRoomCommand.ExecuteAsync(null));
            Assert.Equal("multiplayer.direct.use_sealed", vm.StatusInfo?.Key);
            vm.IsDirect = false;
            vm.Mode = "";
        }
        finally
        {
            vm.StopHostLobbyForTest();
            Wait(direct.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public void AFriendRemovedWhileInvitesGoOutGetsNone() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var root = Path.Combine(Path.GetTempPath(), "jay-lobby-removed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            vm.ContactsDir = root;
            byte[] Key(byte n) { var key = new byte[32]; key[0] = n; return key; }
            var now = DateTimeOffset.UtcNow;
            vm.Contacts.Import([
                new Contact(Key(1), "Robin", true, now),
                new Contact(Key(2), "Sam", true, now),
            ]);
            vm.RefreshContacts();
            var credentials = RoomCredentials.Create(vm.Identity.PublicKey, 25565, TunnelSettings.Default);
            var lobby = new HostLobby(_ => new NoLobby(), () => [],
                book => new KnockServer(book, vm.Identity.KeyPair, () => credentials, _ => false, (_, _) => Task.FromResult(false), System.Net.IPAddress.Loopback, 0));
            try
            {
                vm.HostLobbyForTest(lobby, credentials);
                var mailed = new List<string>();
                // Sam is taken off the list while the first invite (Robin's, the lists are sorted by name) is on its way.
                vm.TestSendInvite = (to, _, _) =>
                {
                    mailed.Add(to.Name);
                    if (to.Name == "Robin")
                    {
                        vm.Contacts.RemovePerson(Key(2));
                        vm.RefreshContacts();
                    }
                    return Task.FromResult(Guid.NewGuid().ToString("N"));
                };
                var told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                Assert.Equal(1, told.Result);
                Assert.Equal(["Robin"], mailed);
                Assert.Equal(1, Assert.Single(lobby.Book.Pending).BoundTo![0]);

                // Removed right after the mail went: the invite it carried is taken back.
                vm.TestSendInvite = (to, _, _) =>
                {
                    vm.Contacts.RemovePerson(Key(1));
                    return Task.FromResult(Guid.NewGuid().ToString("N"));
                };
                told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                Assert.Equal(0, told.Result);
                // Robin is no longer a friend: neither invite is left.
                Assert.Empty(lobby.Book.Pending);
            }
            finally
            {
                Wait(lobby.DisposeAsync().AsTask());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void ANoServerRoomHandsOutNoPlainInvite() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var root = Path.Combine(Path.GetTempPath(), "jay-lobby-noserver-invite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            vm.ContactsDir = root;
            var key = new byte[32];
            key[0] = 1;
            vm.Contacts.Import([new Contact(key, "Robin", true, DateTimeOffset.UtcNow)]);
            vm.RefreshContacts();
            var credentials = RoomCredentials.Create(vm.Identity.PublicKey, 25565, TunnelSettings.Default);
            var lobby = new HostLobby(_ => new NoLobby(), () => [],
                book => new KnockServer(book, vm.Identity.KeyPair, () => credentials, _ => false, (_, _) => Task.FromResult(false), System.Net.IPAddress.Loopback, 0));
            try
            {
                vm.HostLobbyForTest(lobby, credentials);
                vm.Mode = "host";
                var mailed = 0;
                vm.TestSendInvite = (_, _, _) =>
                {
                    mailed++;
                    return Task.FromResult(Guid.NewGuid().ToString("N"));
                };

                // An ordinary room offers a new invite on its card.
                Assert.True(vm.ShowCreateInvite);

                // "No server": a plain invite would knock through a relay this room's lobby never reaches.
                vm.IsDirect = true;
                Assert.False(vm.ShowCreateInvite);
                Wait(vm.CreateInviteCommand.ExecuteAsync(null));
                Assert.False(vm.HasInvite);
                Assert.Empty(lobby.Book.Pending);
                Assert.Equal("multiplayer.direct.use_sealed", vm.StatusInfo?.Key);

                // "Invite friends" and Ctrl+Shift+I go to the sealed code instead (none can be made here: no addresses).
                Wait(vm.InviteFriendsCommand.ExecuteAsync(null));
                Assert.False(vm.HasInvite);
                Assert.Empty(lobby.Book.Pending);

                // Nor is a friend mailed a plain invite.
                var told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                Assert.Equal(0, told.Result);
                Assert.Equal(0, mailed);
                Assert.Empty(lobby.Book.Pending);

                // Back in an ordinary room, the same friend is mailed one.
                vm.IsDirect = false;
                told = vm.InviteFriendsToKnockAsync();
                Wait(told);
                Assert.Equal(1, told.Result);
                Assert.Equal(1, mailed);
                vm.Mode = "";
            }
            finally
            {
                Wait(lobby.DisposeAsync().AsTask());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void RelaysOnlyWithAStrictSplitDoesNotOpen() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        // A strict split needs a direct main path; "Relays only" turns direct connections off.
        Assert.True(MultiplayerViewModel.SplitNeedsDirect(SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Off)));
        Assert.True(MultiplayerViewModel.SplitNeedsDirect(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Strict)));
        Assert.False(MultiplayerViewModel.SplitNeedsDirect(SplitSettings.Create(SplitPolicy.Auto, SplitPolicy.Auto)));
        Assert.False(MultiplayerViewModel.SplitNeedsDirect(SplitSettings.None));

        vm.TestPeers = ["tcp://relay.example.org:11010"];
        vm.TestSplit = SplitSettings.Create(SplitPolicy.Strict, SplitPolicy.Strict);
        vm.SelectedRoute = vm.RouteChoices.First(c => c.Id == nameof(RoomRoute.RelayOnly));
        Wait(vm.CreateRoomCommand.ExecuteAsync(null));
        Assert.True(vm.IsIdle);
        Assert.Equal("multiplayer.split.relay_only_strict", vm.StatusInfo?.Key);
    });

    [Fact]
    public void AFriendsRoomLeavesTheListWhenItRunsOut() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        using var friend = X25519KeyPair.Generate();
        using var host = X25519KeyPair.Generate();
        MailedInvite Mail(TimeSpan lifetime, string room) => new(new Contact(friend.PublicKey, "Robin", true, DateTimeOffset.UtcNow),
            new InviteBook().Create(1, TimeSpan.FromHours(12), false, TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite,
            host.PublicKey, room, DateTimeOffset.UtcNow + lifetime);

        // Listed rooms are checked on a timer (every one runs out), so nobody has to touch the list.
        Assert.False(vm.WatchingFriendRoomExpiry);
        vm.ShowMailedInvites([Mail(TimeSpan.FromHours(12), "Robin's world")]);
        Assert.True(vm.WatchingFriendRoomExpiry);

        // The request timing out takes the room away, and so does the user's own "Not now".
        vm.ShowMailedInvites([Mail(TimeSpan.FromHours(12), "Second world")]);
        var request = service.Requests.Pending.Single(r => r.Spec.Kind == "contact-invite" && r.Spec.Title.Contains("Second world"));
        service.Requests.Resolve(request, "ignore", Amber.Core.Activity.RequestSource.Timeout);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Robin's world"], vm.FriendRooms.Select(r => r.Title));
        request = Assert.Single(service.Requests.Pending, r => r.Spec.Kind == "contact-invite");
        service.Requests.Resolve(request, "ignore", Amber.Core.Activity.RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.FriendRooms);

        // Once nothing is listed, the timer stops.
        Assert.False(vm.WatchingFriendRoomExpiry);
    });

    [Fact]
    public void JoiningTheSameServerAgainCountsAsJoined() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        var serverKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        vm.JayServers.EnrollForTest = (invite, _) => Task.FromResult(
            new Amber.Core.Multiplayer.Gate.GateMembership(invite.Host, invite.Port, serverKey, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), "Steve"));
        string NewInvite() => new Amber.Core.Multiplayer.Gate.GateInvite("mc.example.com", 47670,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).Encode();
        try
        {
            foreach (var round in new[] { 1, 2 })
            {
                vm.Where = WhereFilter.All;
                Assert.True(vm.PasteCode(NewInvite()));
                var joined = vm.JoinCommand.ExecuteAsync(null);
                Wait(joined);
                // The second time the saved server is replaced (same server, same name), and that is still a join.
                Assert.Equal("", vm.JoinCode);
                Assert.Equal(WhereFilter.Servers, vm.Where);
                Assert.Single(vm.JayServers.Servers, server => server.Entry.Host == "mc.example.com");
            }
        }
        finally
        {
            foreach (var server in vm.JayServers.Servers.Where(server => server.Entry.Host == "mc.example.com").ToList())
                Wait(vm.JayServers.RemoveCommand.ExecuteAsync(server));
        }
    });

    private sealed class NoLobby : IDisposable
    {
        public void Dispose() { }
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "the task did not finish");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

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
