using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>
/// The room as a scene (UI restructure: people, rooms, networks): the connection mark and the line under each face,
/// the fixed "who can see what" table with the relays-only switch, the invite panel's two choices, "share with them"
/// under a person, and the one amber action.
/// </summary>
[Collection("GlobalLanguage")]
public sealed class RoomSceneTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jay-room-scene-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static PeerInfo Peer(string name, string cost, double? ms = null) => new(name, "10.144.144.2", cost, ms, "udp");

    // ---- pure logic ----

    [Fact]
    public void RelayServersAndMyOwnEntryAreNotPeople()
    {
        Assert.False(RoomLinks.IsPlayer(Peer("me", "Local")));
        Assert.False(RoomLinks.IsPlayer(Peer("", "p2p")));
        Assert.True(RoomLinks.IsPlayer(Peer("amber-guest", "p2p")));
        Assert.Equal(RoomLink.None, RoomLinks.Room([Peer("me", "Local"), Peer("", "relay(1)", 40)]));
        // A relay that tells its name is still not a person: it has no address in the room.
        var namedRelay = new PeerInfo("relay-one", "", "p2p", 12, "tcp");
        Assert.False(RoomLinks.IsPlayer(namedRelay));
        Assert.Equal(RoomLink.None, RoomLinks.Room([Peer("me", "Local"), namedRelay]));
        Assert.Equal(new RoomLink(LinkKind.Relay, 82), RoomLinks.Room([namedRelay, Peer("amber-guest", "relay(2)", 82)]));
        Assert.Equal(RoomLink.None, RoomLinks.ToGuest([namedRelay]));
    }

    [Fact]
    public void TheRoomIsOneKindOnlyWhenEveryoneIsReachedTheSameWay()
    {
        Assert.Equal(new RoomLink(LinkKind.Direct, 30), RoomLinks.Room([Peer("amber-guest", "p2p", 18), Peer("amber-guest", "P2P", 30)]));
        Assert.Equal(new RoomLink(LinkKind.Relay, 82), RoomLinks.Room([Peer("amber-host", "relay(2)", 82)]));
        Assert.Equal(LinkKind.Mixed, RoomLinks.Room([Peer("amber-guest", "p2p", 18), Peer("amber-guest", "relay(2)", 82)]).Kind);
        // No round trip measured yet: the kind is still told.
        Assert.Equal(new RoomLink(LinkKind.Direct, null), RoomLinks.Room([Peer("amber-guest", "p2p")]));
    }

    [Fact]
    public void AGuestsLinkIsOnlyToldWhenItCannotBeSomebodyElses()
    {
        var host = Peer(RoomLinks.HostName, "relay(2)", 82);
        Assert.Equal(new RoomLink(LinkKind.Relay, 82), RoomLinks.ToHost([host, Peer("amber-guest", "p2p", 18)]));
        Assert.Equal(RoomLink.None, RoomLinks.ToHost([Peer("amber-guest", "p2p", 18)]));

        Assert.Equal(new RoomLink(LinkKind.Direct, 18), RoomLinks.ToGuest([host, Peer("amber-guest", "p2p", 18)]));
        // Two guests reached the same way: the kind, without a round trip that might be the other one's.
        Assert.Equal(new RoomLink(LinkKind.Direct, null), RoomLinks.ToGuest([Peer("amber-guest", "p2p", 18), Peer("amber-guest", "p2p", 40)]));
        // Two guests reached differently: nothing is claimed.
        Assert.Equal(RoomLink.None, RoomLinks.ToGuest([Peer("amber-guest", "p2p", 18), Peer("amber-guest", "relay(2)", 82)]));
        Assert.Equal(RoomLink.None, RoomLinks.ToGuest([host]));
    }

    [Fact]
    public void WhoSeesWhatIsTheSameFiveRowsOfFacts()
    {
        Assert.Equal(["code", "direct", "relayed", "operator", "private"], WhoSeesWhat.Rows.Select(r => r.Id));
        var language = L.Language;
        try
        {
            foreach (var lang in L.Languages.Select(l => l.Code))
            {
                L.SetLanguage(lang);
                foreach (var key in WhoSeesWhat.Rows.SelectMany(r => new[] { r.WhoKey, r.IpKey, r.ContentKey }))
                    Assert.False(L.T(key).StartsWith("multiplayer.", StringComparison.Ordinal), lang + " " + key);
            }
        }
        finally
        {
            L.SetLanguage(language);
        }
        // A code carries no address; without "relays only" the room can see mine; with it, nobody in the room can (my
        // component shares no address list, refuses hole punching and asks no STUN server); a relay sees it itself.
        Assert.Equal("multiplayer.sees.no", WhoSeesWhat.Rows[0].IpKey);
        Assert.Equal("multiplayer.sees.yes", WhoSeesWhat.Rows[1].IpKey);
        Assert.Equal("multiplayer.sees.no", WhoSeesWhat.Rows[2].IpKey);
        Assert.Equal("multiplayer.sees.yes", WhoSeesWhat.Rows[3].IpKey);
        Assert.Equal("multiplayer.sees.no", WhoSeesWhat.Rows[3].ContentKey);
    }

    [Fact]
    public void TheRowThatAppliesNowFollowsMySwitchNotJustTheLinkOfTheMoment()
    {
        Assert.Empty(WhoSeesWhat.AppliesNow(LinkKind.None, relayOnly: false));
        Assert.Empty(WhoSeesWhat.AppliesNow(LinkKind.None, relayOnly: true));
        Assert.Equal(["direct"], WhoSeesWhat.AppliesNow(LinkKind.Direct, relayOnly: false));
        // Through a relay at the moment, but Jay keeps trying to connect directly: still the row that says "yes".
        Assert.Equal(["direct"], WhoSeesWhat.AppliesNow(LinkKind.Relay, relayOnly: false));
        Assert.Equal(["direct"], WhoSeesWhat.AppliesNow(LinkKind.Mixed, relayOnly: false));
        Assert.Equal(["relayed"], WhoSeesWhat.AppliesNow(LinkKind.Relay, relayOnly: true));
        // A direct link in spite of the switch is reported as what it is.
        Assert.Equal(["direct"], WhoSeesWhat.AppliesNow(LinkKind.Direct, relayOnly: true));
        Assert.Equal(["direct"], WhoSeesWhat.AppliesNow(LinkKind.Mixed, relayOnly: true));
    }

    [Fact]
    public void TheInvitePanelsTwoChoicesMapOntoTheInviteBook()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), InviteOffer.Lifetime(InviteLife.TenMinutes));
        Assert.Equal(TimeSpan.FromDays(1), InviteOffer.Lifetime(InviteLife.OneDay));
        Assert.Equal(LongLivedInvites.DefaultLifetime, InviteOffer.Lifetime(InviteLife.ThirtyDays));
        Assert.True(InviteOffer.IsLongLived(InviteLife.ThirtyDays));
        Assert.False(InviteOffer.IsLongLived(InviteLife.OneDay));
        // Thirty days has no use count.
        Assert.False(InviteOffer.CanPickUses(InviteLife.ThirtyDays));
        Assert.Equal(InviteUse.Unlimited, InviteOffer.Normalize(InviteLife.ThirtyDays, InviteUse.Once));
        Assert.Equal(InviteUse.Once, InviteOffer.Normalize(InviteLife.OneDay, InviteUse.Once));
        Assert.Equal(1, InviteOffer.Uses(InviteUse.Once));
        Assert.Equal(InviteBook.UnlimitedUses, InviteOffer.Uses(InviteUse.Unlimited));
        // Only an invite for one person can skip the host's yes.
        Assert.True(InviteOffer.CanAutoApprove(InviteLife.TenMinutes, InviteUse.Once));
        Assert.True(InviteOffer.CanAutoApprove(InviteLife.OneDay, InviteUse.Once));
        Assert.False(InviteOffer.CanAutoApprove(InviteLife.OneDay, InviteUse.Unlimited));
        Assert.False(InviteOffer.CanAutoApprove(InviteLife.ThirtyDays, InviteUse.Once));
    }

    [Fact]
    public void RememberedTermsNeverMapOntoAWiderChoice()
    {
        Assert.Equal(InviteLife.TenMinutes, InviteOffer.LifeFromMinutes(10));
        // An hour is not on the panel: ten minutes, not a day.
        Assert.Equal(InviteLife.TenMinutes, InviteOffer.LifeFromMinutes(60));
        Assert.Equal(InviteLife.TenMinutes, InviteOffer.LifeFromMinutes(24 * 60 - 1));
        Assert.Equal(InviteLife.OneDay, InviteOffer.LifeFromMinutes(24 * 60));
        // "While the room is open" and thirty days: a day, never a long-lived invite.
        Assert.Equal(InviteLife.OneDay, InviteOffer.LifeFromMinutes(null));
        Assert.Equal(InviteLife.OneDay, InviteOffer.LifeFromMinutes(30 * 24 * 60));
        Assert.Equal(InviteUse.Once, InviteOffer.UseFromCount(1));
        // Five uses is not on the panel: once, not unlimited.
        Assert.Equal(InviteUse.Once, InviteOffer.UseFromCount(5));
        Assert.Equal(InviteUse.Once, InviteOffer.UseFromCount(20));
        Assert.Equal(InviteUse.Unlimited, InviteOffer.UseFromCount(InviteBook.UnlimitedUses));

        // "Open like last time": the first invite is one of the panel's own, and unlimited never comes with auto-approve.
        Assert.Equal((InviteLife.TenMinutes, InviteUse.Once, true),
            MultiplayerViewModel.LastInviteTerms(new LastRoom { Uses = 5, LifetimeMinutes = 60, AutoApprove = true }));
        Assert.Equal((InviteLife.OneDay, InviteUse.Unlimited, false),
            MultiplayerViewModel.LastInviteTerms(new LastRoom { Uses = InviteBook.UnlimitedUses, LifetimeMinutes = 24 * 60, AutoApprove = true }));
        Assert.Equal((InviteLife.OneDay, InviteUse.Once, false),
            MultiplayerViewModel.LastInviteTerms(new LastRoom { Uses = 1, LifetimeMinutes = null, AutoApprove = false }));
    }

    [Fact]
    public void AnUnlimitedInviteDoesNotRunOut()
    {
        var now = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        var book = new InviteBook(() => now);
        var invite = book.Create(InviteBook.UnlimitedUses, TimeSpan.FromDays(1), false, TunnelSettings.Default);
        Assert.True(invite.Unlimited);
        for (var i = 0; i < 50; i++) Assert.True(book.TryConsume(invite));
        Assert.True(invite.Unlimited);
        Assert.True(invite.IsUsable(now));
        // It still ends with its time.
        now += TimeSpan.FromDays(2);
        Assert.False(book.TryConsume(invite));
        Assert.False(book.Create(1, null, false, TunnelSettings.Default).Unlimited);
    }

    [Fact]
    public void AnUnlimitedInviteAlwaysAsksTheHost()
    {
        var book = new InviteBook();
        Assert.Throws<ArgumentException>(() => book.Create(InviteBook.UnlimitedUses, TimeSpan.FromDays(1), true, TunnelSettings.Default));
        Assert.False(book.HasPending);
        // A counted invite can still let its people in unasked.
        Assert.True(book.Create(1, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).AutoApprove);
        Assert.True(book.Create(20, TimeSpan.FromMinutes(10), true, TunnelSettings.Default).AutoApprove);
    }

    // ---- the view model ----

    private (LauncherService Service, MultiplayerViewModel Vm, RoomCode Room) Create()
    {
        var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service)
        {
            IsComponentReady = true,
            ContactsDir = Path.Combine(_root, "contacts"),
            LongInvitesFile = Path.Combine(_root, "long-invites.json"),
        };
        var room = RoomCredentials.Create(vm.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), "Robin");
        return (service, vm, room);
    }

    private static byte[] Key(byte first)
    {
        var key = new byte[32];
        key[0] = first;
        return key;
    }

    private static void AddGuest(MultiplayerViewModel vm, string name, byte[] key)
    {
        vm.Members.Add(new MemberItem(name, key, false, true));
        vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(key), key, name)));
    }

    private static void SetPeers(MultiplayerViewModel vm, params PeerInfo[] peers)
    {
        vm.Peers.Clear();
        foreach (var peer in peers) vm.Peers.Add(new PeerItem(peer));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the dispatcher until the task is done (continuations come back to this thread).</summary>
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

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }

    [Fact]
    public void TheConnectionMarkAndTheLinesFollowThePeerList() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(L.P("multiplayer.room.people", 2, 2), vm.PeopleCountText);
        Assert.Equal(L.F("multiplayer.room.protection", L.T("multiplayer.open.protection.standard")), vm.RoomProtectionText);

        // Nobody connected yet.
        Assert.True(vm.IsMarkWaiting);
        Assert.Equal(L.T("multiplayer.room.waiting_friends"), vm.ConnectionMarkText);
        Assert.DoesNotContain(vm.WhoSeesLines, l => l.Now);

        SetPeers(vm, Peer("amber-guest", "p2p", 18));
        Assert.True(vm.IsMarkDirect);
        Assert.Equal(L.T("multiplayer.room.direct") + " · 18 ms", vm.ConnectionMarkText);
        var alex = vm.People.Single(p => p.Name == "Alex");
        Assert.True(alex.IsDirect);
        Assert.Equal(vm.ConnectionMarkText, alex.LinkText);
        Assert.Equal(["direct"], vm.WhoSeesLines.Where(l => l.Now).Select(l => l.Row.Id));
        // My own face never gets a line.
        Assert.False(vm.People.Single(p => p.IsMe).HasLink);

        SetPeers(vm, Peer("amber-guest", "relay(2)", 82));
        Assert.True(vm.IsMarkRelayed);
        Assert.Equal(L.T("multiplayer.room.relayed") + " · 82 ms", vm.ConnectionMarkText);
        Assert.True(vm.People.Single(p => p.Name == "Alex").IsRelayed);
        // Relayed at the moment, with "relays only" off: Jay keeps trying direct, so the row that applies says so.
        Assert.Equal(["direct"], vm.WhoSeesLines.Where(l => l.Now).Select(l => l.Row.Id));
        var now = vm.WhoSeesLines.Single(l => l.Now);
        Assert.Equal(L.F("multiplayer.sees.row_name_now", now.Who, now.Ip, now.Content), now.SpokenName);
        var other = vm.WhoSeesLines.First(l => !l.Now);
        Assert.Equal(L.F("multiplayer.sees.row_name", other.Who, other.Ip, other.Content), other.SpokenName);

        // A relay that tells its name is nobody: the mark waits, and "connected to others" agrees with it.
        SetPeers(vm, new PeerInfo("relay-one", "", "p2p", 12, "tcp"));
        Assert.True(vm.IsMarkWaiting);
        Assert.False(vm.IsConnectedToOthers);
        Assert.DoesNotContain(vm.WhoSeesLines, l => l.Now);
        SetPeers(vm, new PeerInfo("relay-one", "", "p2p", 12, "tcp"), Peer("amber-guest", "relay(2)", 82));
        Assert.True(vm.IsMarkRelayed);
        Assert.True(vm.IsConnectedToOthers);

        // One of each: the mark counts them, and no line is drawn because a peer cannot be matched to a person.
        AddGuest(vm, "Bo", Key(2));
        SetPeers(vm, Peer("amber-guest", "p2p", 18), Peer("amber-guest", "relay(2)", 82));
        Assert.True(vm.IsMarkMixed);
        Assert.Equal(L.F("multiplayer.room.mark_mixed", 1, 1), vm.ConnectionMarkText);
        Assert.All(vm.People, p => Assert.False(p.HasLink));
        Assert.Equal(["direct"], vm.WhoSeesLines.Where(l => l.Now).Select(l => l.Row.Id));
        vm.LeaveTestRoom();
    });

    [Fact]
    public void VerifiedFriendsAreMarkedAndTheBannerShowsEveryone() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.Contacts.Import([new Contact(Key(1), "Alex", true, DateTimeOffset.UtcNow)]);
        vm.RefreshContacts();
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        AddGuest(vm, "Bo", Key(2));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.People.Single(p => p.Name == "Alex").IsFriend);
        Assert.False(vm.People.Single(p => p.Name == "Bo").IsFriend);
        Assert.Equal(L.T("multiplayer.people.friend"), vm.People.Single(p => p.Name == "Alex").FriendText);
        Assert.Equal(L.T("multiplayer.people.met"), vm.People.Single(p => p.Name == "Bo").FriendText);
        Assert.Equal("", vm.People.Single(p => p.IsMe).FriendText);
        Assert.Equal(3, vm.BannerPeople.Count);
        Assert.False(vm.HasExtraPeople);

        // Picking a face selects the person.
        vm.SelectPersonCommand.Execute(vm.People.Single(p => p.Name == "Bo"));
        Assert.Equal("Bo", vm.SelectedPerson?.Name);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void RelaysOnlyRestartsMySideAndStaysHonestWhenThatFails() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true);
        Assert.False(vm.MyRelayOnly);
        Assert.True(vm.CanChangeRelayOnly);
        Assert.Equal("", vm.RelayOnlyNote);
        Assert.Contains(vm.RoomFacts, f => f.Value == L.T("multiplayer.open.route.auto"));

        RoomCode? switchedTo = null;
        vm.TestSwitchNetwork = next =>
        {
            switchedTo = next;
            return true;
        };
        Wait(vm.SetRelayOnlyAsync(true));
        Assert.True(switchedTo is { RelayOnly: true });
        Assert.True(vm.MyRelayOnly);
        Assert.True(vm.CanChangeRelayOnly);
        Assert.Equal(L.T("multiplayer.sees.relay_only.turned_on"), vm.Status);
        Assert.Contains(vm.RoomFacts, f => f.Value == L.T("multiplayer.open.route.relay"));
        // With the switch on and the link through a relay, the "relays only" row is the one that applies.
        AddGuest(vm, "Alex", Key(1));
        SetPeers(vm, Peer("amber-guest", "relay(2)", 82));
        Assert.Equal(["relayed"], vm.WhoSeesLines.Where(l => l.Now).Select(l => l.Row.Id));

        // The component would not restart: the switch stays where it really is, and says it did not take.
        vm.TestSwitchNetwork = _ => false;
        Wait(vm.SetRelayOnlyAsync(false));
        Assert.True(vm.MyRelayOnly);
        Assert.True(vm.CanChangeRelayOnly);
        Assert.Equal(L.T("multiplayer.sees.relay_only.failed"), vm.Status);

        // A restart that throws is reported, and the switch is usable again.
        vm.TestSwitchNetwork = _ => throw new IOException("no component");
        Wait(vm.SetRelayOnlyAsync(false));
        Assert.True(vm.MyRelayOnly);
        Assert.True(vm.CanChangeRelayOnly);
        Assert.Contains("no component", vm.Status);

        vm.TestSwitchNetwork = _ => true;
        Wait(vm.SetRelayOnlyAsync(false));
        Assert.False(vm.MyRelayOnly);
        Assert.Equal(L.T("multiplayer.sees.relay_only.turned_off"), vm.Status);
        // Off again while the link is still relayed: back to the row that says the room can see my address.
        Assert.Equal(["direct"], vm.WhoSeesLines.Where(l => l.Now).Select(l => l.Row.Id));
        vm.LeaveTestRoom();
    });

    [Fact]
    public void TheTwoSwitchesThatRestartTheComponentNeverRunTogether() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        var confirm = new TaskCompletionSource<bool>();
        vm.Confirm = (_, _, _) => confirm.Task;
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedPerson = vm.People.Single(p => p.Name == "Alex");
        vm.UseTestOptions(new EasyTierOptions(room, true, [], 0));
        Assert.True(vm.CanShareGameProxy);
        var relaySwitches = 0;
        bool? proxyFreeDuringRelaySwitch = null;
        vm.TestSwitchNetwork = _ =>
        {
            relaySwitches++;
            proxyFreeDuringRelaySwitch = vm.CanShareGameProxy;
            return true;
        };
        EasyTierOptions? restarted = null;
        vm.TestRestartForProxy = next =>
        {
            restarted = next;
            return true;
        };

        // Forwarding is waiting at its confirmation: "relays only" cannot start a restart of its own.
        var proxy = vm.SetGameProxySharingAsync(true);
        Dispatcher.UIThread.RunJobs();
        Assert.False(proxy.IsCompleted);
        Assert.False(vm.CanChangeRelayOnly);
        Assert.False(vm.CanShareGameProxy);
        Wait(vm.SetRelayOnlyAsync(true));
        Assert.Equal(0, relaySwitches);
        Assert.False(vm.MyRelayOnly);

        confirm.SetResult(true);
        Wait(proxy);
        Assert.True(restarted is { AllowProxy: true });
        Assert.True(vm.SharingGameProxy);
        Assert.True(vm.CanChangeRelayOnly);
        Assert.True(vm.CanShareGameProxy);

        // And the other way round: while "relays only" restarts, forwarding cannot be switched.
        Wait(vm.SetRelayOnlyAsync(true));
        Assert.Equal(1, relaySwitches);
        Assert.False(proxyFreeDuringRelaySwitch);
        Assert.True(vm.CanShareGameProxy);
        vm.UseTestOptions(null);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void ForwardingThatDoesNotTakeSaysSoAndFreesTheSwitches() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.Confirm = (_, _, _) => Task.FromResult(true);
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedPerson = vm.People.Single(p => p.Name == "Alex");
        vm.UseTestOptions(new EasyTierOptions(room, true, [], 0));

        vm.TestRestartForProxy = _ => false;
        Wait(vm.SetGameProxySharingAsync(true));
        Assert.False(vm.SharingGameProxy);
        Assert.Equal(L.T("multiplayer.share.proxy.failed"), vm.Status);
        Assert.True(vm.CanShareGameProxy);
        Assert.True(vm.CanChangeRelayOnly);

        vm.TestRestartForProxy = _ => throw new InvalidOperationException("no component");
        Wait(vm.SetGameProxySharingAsync(true));
        Assert.False(vm.SharingGameProxy);
        Assert.Contains("no component", vm.Status);
        Assert.True(vm.CanShareGameProxy);
        Assert.True(vm.CanChangeRelayOnly);
        vm.UseTestOptions(null);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void ARoomOpenedAsRelaysOnlyFixesTheSwitchForEveryone() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        var switched = false;
        vm.TestSwitchNetwork = _ => switched = true;
        vm.EnterTestRoom(room with { RelayOnly = true }, host: false, relayRule: true);
        Assert.True(vm.RoomRuleRelayOnly);
        Assert.True(vm.MyRelayOnly);
        Assert.False(vm.CanChangeRelayOnly);
        Assert.Equal(L.T("multiplayer.sees.relay_only.room_rule"), vm.RelayOnlyNote);
        Wait(vm.SetRelayOnlyAsync(false));
        Assert.True(vm.MyRelayOnly);
        Assert.False(switched);
        vm.LeaveTestRoom();
        Assert.Equal("", vm.RelayOnlyNote);
    });

    [Fact]
    public void RelaysOnlyCanBeMyDefaultForEveryRoom() => UiTestThread.Run(() =>
    {
        var (service, vm, _) = Create();
        using var s = service;
        Assert.False(vm.DefaultRelayOnly);
        vm.DefaultRelayOnly = true;
        Assert.True(service.Settings.MultiplayerRelayOnly);
        vm.DefaultRelayOnly = false;
        Assert.False(service.Settings.MultiplayerRelayOnly);
    });

    [Fact]
    public void WhoSeesWhatOpensFromTheMarkAndClosesAgain() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true);
        var focused = 0;
        vm.WhoSeesFocusRequested = () => focused++;
        Assert.Equal(5, vm.WhoSeesLines.Count);
        vm.ToggleWhoSeesCommand.Execute(null);
        Assert.True(vm.WhoSeesOpen);
        Assert.Equal(1, focused);
        // In the compact layout the table is in the details section.
        Assert.Equal(2, vm.CompactSection);
        vm.ToggleWhoSeesCommand.Execute(null);
        Assert.False(vm.WhoSeesOpen);
        Assert.Equal(1, focused);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void TheInvitePanelMakesWhatItsTwoChoicesSay() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true, lobby: true);
        Assert.Equal(3, vm.InviteLifeChoices.Count);
        Assert.Equal(2, vm.InviteUseChoices.Count);
        Assert.False(vm.HasInvite);
        // Without an invite the third line says how invites end.
        Assert.Equal(L.T("multiplayer.invite.fact.ends"), vm.InviteTermsFact);
        Assert.Equal(L.T("multiplayer.invite.fact.knock"), vm.InviteKnockFact);

        vm.PickInviteChoices(InviteLife.TenMinutes, InviteUse.Once);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.True(vm.HasInvite);
        var first = Assert.Single(vm.PendingInvites);
        Assert.False(first.Invite.Unlimited);
        Assert.Equal(1, first.Invite.Uses);
        Assert.Equal(L.F("multiplayer.invite.summary", L.F("multiplayer.invite.left", L.P("multiplayer.invite.minutes", 10, 10)), L.P("multiplayer.invite.uses_left", 1, 1)), vm.InviteSummary);
        Assert.Equal(vm.InviteTerms, vm.InviteTermsFact);
        Assert.Contains(L.P("multiplayer.invite.minutes", 10, 10), vm.InviteTerms);
        // The card shows no code until asked to.
        Assert.False(vm.InviteShown);

        // The three lines follow the language.
        var language = L.Language;
        try
        {
            L.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("10 minutes", vm.InviteTerms);
            Assert.Equal(vm.InviteTerms, vm.InviteTermsFact);
            L.SetLanguage("zh-Hans");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("10 分钟", vm.InviteTerms);
        }
        finally
        {
            L.SetLanguage(language);
            Dispatcher.UIThread.RunJobs();
        }

        vm.PickInviteChoices(InviteLife.OneDay, InviteUse.Unlimited);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.Equal(2, vm.PendingInvites.Count);
        Assert.Contains(vm.PendingInvites, i => i.Invite.Unlimited);
        Assert.Equal(L.F("multiplayer.invite.summary", L.F("multiplayer.invite.left", L.P("multiplayer.invite.hours", 24, 24)), L.T("multiplayer.invite.use.unlimited")), vm.InviteSummary);
        Assert.Contains(L.P("multiplayer.invite.days", 1, 1), vm.InviteTerms);
        Assert.Contains(L.T("multiplayer.invite.use.unlimited"), vm.InviteTerms);
        vm.LeaveTestRoom();
        Assert.False(vm.HasInvite);
    });

    [Fact]
    public void OnlyAnInviteForOnePersonCanSkipMyYes() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true, lobby: true);
        vm.PickInviteChoices(InviteLife.TenMinutes, InviteUse.Once);
        Assert.True(vm.CanInviteAutoApprove);
        Assert.False(vm.ShowInviteAlwaysAsksNote);
        vm.InviteAutoApprove = true;
        Assert.Equal(L.T("multiplayer.invite.fact.auto"), vm.InviteKnockFact);

        // Unlimited: the box is gone, a line says why, and the tick left over from before does not count.
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.PickInviteChoices(InviteLife.OneDay, InviteUse.Unlimited);
        Assert.Contains(nameof(vm.CanInviteAutoApprove), changed);
        Assert.Contains(nameof(vm.InviteKnockFact), changed);
        Assert.False(vm.CanInviteAutoApprove);
        Assert.True(vm.ShowInviteAlwaysAsksNote);
        Assert.Equal(L.T("multiplayer.invite.fact.knock"), vm.InviteKnockFact);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        var unlimited = Assert.Single(vm.PendingInvites);
        Assert.True(unlimited.Invite.Unlimited);
        Assert.False(unlimited.Invite.AutoApprove);
        Assert.Contains(L.T("multiplayer.invite.terms_ask"), vm.InviteTerms);
        // Whatever a caller remembered, the room never makes an unlimited invite that lets people in unasked.
        Assert.True(Wait(vm.CreateInviteForTestAsync(InviteBook.UnlimitedUses, TimeSpan.FromDays(1), true)));
        Assert.All(vm.PendingInvites, i => Assert.False(i.Invite.AutoApprove));

        // Thirty days has no use count either; its own note already says every knock still asks.
        vm.PickInviteChoices(InviteLife.ThirtyDays, InviteUse.Once);
        Assert.False(vm.CanInviteAutoApprove);
        Assert.False(vm.ShowInviteAlwaysAsksNote);
        Assert.Equal(L.T("multiplayer.invite.fact.knock"), vm.InviteKnockFact);

        // Back to one person: the tick counts again.
        vm.PickInviteChoices(InviteLife.TenMinutes, InviteUse.Once);
        Assert.True(vm.CanInviteAutoApprove);
        Assert.Equal(L.T("multiplayer.invite.fact.auto"), vm.InviteKnockFact);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.Contains(vm.PendingInvites, i => i.Invite.AutoApprove && i.Invite.Uses == 1);

        // "For a child": no box and no line where it would be.
        service.Settings.ChildMode.SetEnabled(true, DateTimeOffset.UtcNow);
        vm.ApplyChildMode();
        Assert.False(vm.CanInviteAutoApprove);
        Assert.False(vm.ShowInviteAlwaysAsksNote);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void ThirtyDaysIsTheLongLivedInviteAndARoomWithNoOtherInviteAcceptsItAtOnce() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true, lobby: true);
        Assert.False(vm.HasLongInvites);

        vm.PickInviteChoices(InviteLife.ThirtyDays, InviteUse.Once);
        Assert.False(vm.InviteUsesEnabled);
        Assert.True(vm.ShowInviteMonthNote);
        Assert.Equal(InviteUse.Unlimited, vm.SelectedInviteUse.Use);
        vm.LongInviteLabel = "Bob";
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));

        var item = Assert.Single(vm.LongInviteItems);
        Assert.Equal("Bob", item.Label);
        Assert.Equal("", vm.LongInviteLabel);
        Assert.True(File.Exists(vm.LongInvitesFile));
        // The room had no invite out, so it runs on the thirty-day invite's own key and answers it, and says so.
        Assert.False(vm.ShowInviteNextRoomNote);
        Assert.Equal(L.T("multiplayer.longinvite.created"), vm.Status);
        Assert.True(JayInvite.TryParse(vm.TestInviteText ?? "", out var monthCode));
        Assert.Equal(item.Invite.Token, monthCode.Token);
        Assert.Equal(L.F("multiplayer.invite.summary", item.Left, L.T("multiplayer.invite.use.unlimited")), vm.InviteSummary);
        Assert.Contains(L.P("multiplayer.invite.days", 30, 30), vm.InviteTerms);
        Assert.Contains(L.T("multiplayer.invite.terms_ask"), vm.InviteTerms);

        // Revoking it takes it off the card and out of the open room.
        vm.RevokeLongInviteCommand.Execute(item);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasLongInvites);
        Assert.False(vm.HasInvite);
        Assert.False(File.Exists(vm.LongInvitesFile));

        // Made again after the last one was revoked: a new key, not the one the revoked code carries.
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.True(JayInvite.TryParse(vm.TestInviteText ?? "", out var secondCode));
        Assert.NotEqual(monthCode.LobbyKey, secondCode.LobbyKey);
        Assert.NotEqual(monthCode.Token, secondCode.Token);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void AThirtyDayInviteNeverTakesTheKeyOfARoomThatHasInvitesOut() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        vm.EnterTestRoom(room, host: true, lobby: true);
        // An ordinary invite first: the room has a lobby key, and whoever holds that invite knows it.
        vm.PickInviteChoices(InviteLife.TenMinutes, InviteUse.Once);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        var roomKey = vm.PendingInvites.Single().Invite.Invite.LobbyKey;

        vm.PickInviteChoices(InviteLife.ThirtyDays, InviteUse.Unlimited);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.Single(vm.LongInviteItems);
        Assert.True(JayInvite.TryParse(vm.TestInviteText ?? "", out var monthCode));
        Assert.NotEqual(roomKey, monthCode.LobbyKey);
        // So this room cannot answer it, and the card says from when it works.
        Assert.True(vm.ShowInviteNextRoomNote);
        Assert.Equal(L.T("multiplayer.longinvite.created_next_room"), vm.Status);
        Assert.Equal(roomKey, vm.PendingInvites.Single().Invite.Invite.LobbyKey);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void AThirtyDayInviteWithAnotherKeySaysItWorksFromTheNextRoom() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        // An older thirty-day invite exists, made while no room was open: it has its own key.
        new LongLivedInvites(vm.LongInvitesFile).Create("Old");
        vm.LongInvitesFile = vm.LongInvitesFile;
        Assert.True(vm.HasLongInvites);

        // This room started on a key of its own (a stand-in for a room that could not take the stored one).
        vm.EnterTestRoom(room, host: true, lobby: true);
        vm.PickInviteChoices(InviteLife.TenMinutes, InviteUse.Once);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));

        vm.PickInviteChoices(InviteLife.ThirtyDays, InviteUse.Unlimited);
        Wait(vm.CreateInviteCommand.ExecuteAsync(null));
        Assert.Equal(2, vm.LongInviteItems.Count);
        Assert.True(vm.HasInvite);
        Assert.True(vm.ShowInviteNextRoomNote);
        Assert.Equal(L.T("multiplayer.longinvite.created_next_room"), vm.Status);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void EnterTheGameIsTheGuestsOneAction() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        // A host whose world is already open has nothing to enter.
        vm.EnterTestRoom(room, host: true);
        Assert.False(vm.ShowEnterGame);
        vm.LeaveTestRoom();

        vm.EnterTestRoom(room, host: false);
        Assert.False(vm.ShowEnterGame);
        vm.JoinAddress = "127.0.0.1:25565";
        Assert.True(vm.ShowEnterGame);
        // No game picked yet: it asks for one and starts nothing.
        vm.TargetInstance = null;
        vm.EnterGameCommand.Execute(null);
        Assert.Equal(L.T("multiplayer.room.enter_game_pick"), vm.Status);
        Assert.Equal(2, vm.CompactSection);
        vm.JoinAddress = "";
        vm.LeaveTestRoom();
    });

    [Fact]
    public void ShareWithThemAsksFirstAndSaysWhoItReaches() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        var asked = new List<string>();
        var answer = false;
        vm.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(answer);
        };
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        Dispatcher.UIThread.RunJobs();

        // Nothing under my own name.
        vm.SelectedPerson = vm.People.Single(p => p.IsMe);
        Assert.False(vm.ShowShareGroup);
        vm.SelectedPerson = vm.People.Single(p => p.Name == "Alex");
        Assert.True(vm.ShowShareGroup);
        Assert.True(vm.ShowShareNetwork);
        Assert.Equal(L.T("multiplayer.share.exit.off"), vm.ExitShareText);

        // Forwarding their game: no component running means nothing to switch.
        Assert.False(vm.CanShareGameProxy);
        EasyTierOptions? restarted = null;
        vm.TestRestartForProxy = next =>
        {
            restarted = next;
            return true;
        };
        vm.UseTestOptions(new EasyTierOptions(room, true, [], 0));
        Assert.True(vm.CanShareGameProxy);
        Assert.False(vm.SharingGameProxy);

        // Cancelled at the confirmation: nothing changes.
        Wait(vm.SetGameProxySharingAsync(true));
        Assert.Equal([L.T("multiplayer.share.proxy.confirm_title")], asked);
        Assert.Null(restarted);
        Assert.False(vm.SharingGameProxy);

        answer = true;
        Wait(vm.SetGameProxySharingAsync(true));
        Assert.True(restarted is { AllowProxy: true });
        Assert.True(vm.SharingGameProxy);
        Assert.Equal(L.T("multiplayer.share.proxy.turned_on"), vm.Status);

        // Turning it off needs no question.
        asked.Clear();
        Wait(vm.SetGameProxySharingAsync(false));
        Assert.Empty(asked);
        Assert.False(vm.SharingGameProxy);

        // Whole traffic through my computer is decided when my side starts: a state, not a switch.
        vm.UseTestOptions(new EasyTierOptions(room, true, [], 0, Tun: true, AllowExit: true));
        Assert.True(vm.SharingExit);
        Assert.Equal(L.T("multiplayer.share.exit.on"), vm.ExitShareText);
        vm.LeaveTestRoom();
    });

    [Fact]
    public void RemoteHelpIsBetweenTheHostAndOneMember() => UiTestThread.Run(() =>
    {
        var (service, vm, room) = Create();
        using var s = service;
        var asked = new List<string>();
        vm.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(false);
        };
        vm.EnterTestRoom(room, host: true);
        AddGuest(vm, "Alex", Key(1));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedPerson = vm.People.Single(p => p.Name == "Alex");
        Assert.True(vm.ShowRemoteHelp);
        if (vm.CanAskRemoteHelp)
        {
            // It asks on this side first, by name; "No" sends nothing.
            Wait(vm.AskRemoteHelpCommand.ExecuteAsync(null));
            Assert.Equal([L.F("multiplayer.share.remote.confirm_title", "Alex")], asked);
            Assert.False(vm.RemoteWaiting);
        }
        else Assert.True(vm.HasRemoteHelpNote);
        vm.LeaveTestRoom();

        // A guest can only ask the host: under another guest the button is off and says why.
        vm.Members.Clear();
        vm.OnlinePeers.Clear();
        vm.EnterTestRoom(room, host: false);
        var host = Key(9);
        vm.HostFingerprint = MemberIdentity.FingerprintOf(host);
        vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(vm.HostFingerprint, host, "Robin")));
        vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(2)), Key(2), "Bo")));
        Dispatcher.UIThread.RunJobs();
        vm.SelectedPerson = vm.People.Single(p => p.Name == "Bo");
        Assert.False(vm.CanAskRemoteHelp);
        Assert.Equal(L.T("multiplayer.share.remote.host_only"), vm.RemoteHelpNote);
        vm.SelectedPerson = vm.People.Single(p => p.IsHost);
        Assert.NotEqual(L.T("multiplayer.share.remote.host_only"), vm.RemoteHelpNote);
        vm.HostFingerprint = "";
        vm.OnlinePeers.Clear();
        vm.LeaveTestRoom();
    });
}
