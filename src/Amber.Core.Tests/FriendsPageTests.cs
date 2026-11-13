using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>
/// The Friends page (redesign batch 6): friends and people I've met, the "let them straight in" switch that replaced
/// the trusted list, what is known about a friend right now, and the conversation with its delivery states.
/// </summary>
public sealed class FriendsPageTests
{
    internal static void WithMain(Action<LauncherService, MainViewModel, PeopleViewModel, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "jay-friends-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "" });
                var main = new MainViewModel(service);
                // Friends and the "straight in" list are kept under the data folder: this test keeps its own.
                main.Multiplayer.ContactsDir = root;
                main.Multiplayer.TrustedFile = Path.Combine(root, "trusted.json");
                test(service, main, main.Pages.OfType<PeopleViewModel>().Single(), root);
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    internal static byte[] Key(byte n)
    {
        var key = new byte[32];
        key[0] = n;
        return key;
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

    private static void AddFriends(MultiplayerViewModel mp, params Contact[] friends)
    {
        mp.Contacts.Import(friends);
        mp.RefreshContacts();
        Dispatcher.UIThread.RunJobs();
    }

    private static Contact Friend(byte n, string name, string? account = null) => new(Key(n), name, true, DateTimeOffset.UtcNow) { JayAccount = account };

    private static RoomCode HostRoom(MultiplayerViewModel mp) =>
        RoomCredentials.Create(mp.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), "Me");

    private static void Enter(MultiplayerViewModel mp, string name, byte[] key, bool member = true)
    {
        if (member) mp.Members.Add(new MemberItem(name, key, false, true));
        mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(key), key, name)));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The "straight in" list, read from the file again.</summary>
    private static IReadOnlyList<TrustedMember> StraightIn(string root) => new TrustedMembers(Path.Combine(root, "trusted.json")).All;

    [Fact]
    public void PeopleIveMetAreTheRoomAndThePeopleLetStraightIn() => WithMain((_, main, people, root) =>
    {
        var mp = main.Multiplayer;
        new TrustedMembers(Path.Combine(root, "trusted.json")).Add(Key(9), "Dana");
        mp.TrustedFile = Path.Combine(root, "trusted.json");
        // Starting Jay does not go through the list: it is read when the page is first opened.
        Assert.Empty(mp.MetPeople);
        Assert.False(people.HasMet);
        main.Shell.Navigate("people");
        Dispatcher.UIThread.RunJobs();
        var dana = Assert.Single(people.MetPeople);
        Assert.Equal("Dana", dana.Name);
        Assert.True(dana.AutoAdmit);
        Assert.False(dana.InRoom);
        Assert.Equal(L.T("people.met.before"), dana.StateText);
        Assert.True(people.HasList);
        Assert.False(people.HasFriends);
        Assert.True(people.ShowPickHint);
        Assert.False(people.ShowEmpty);

        AddFriends(mp, Friend(1, "Robin"));
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Robin", Key(1));
        Enter(mp, "Eve", Key(5));
        // The room's roster also lists me, and a member the roster has not caught up with yet.
        Enter(mp, "Me", mp.Identity.PublicKey, member: false);
        mp.Members.Add(new MemberItem("Finn", Key(6), false));
        Dispatcher.UIThread.RunJobs();
        // People in the room come first; a friend is in the friends list, not here; I am not someone I've met.
        Assert.Equal(["Eve", "Dana", "Finn"], people.MetPeople.Select(p => p.Name));
        var eve = people.MetPeople[0];
        Assert.True(eve.InRoom);
        Assert.False(eve.AutoAdmit);
        Assert.Equal(L.T("people.met.in_room"), eve.StateText);
        Assert.Equal(MemberIdentity.FingerprintOf(Key(5)), eve.Fingerprint);
        Assert.False(people.MetPeople[2].InRoom);

        // Selecting one of them is not selecting a friend, and the other way round.
        people.SelectedMet = eve;
        Assert.True(people.HasSelectedMet);
        Assert.True(people.HasPicked);
        Assert.False(people.HasSelected);
        people.Selected = people.Friends.Single();
        Assert.Null(people.SelectedMet);
        Assert.True(people.HasSelected);
        people.SelectedMet = people.MetPeople[0];
        Assert.Null(people.Selected);

        // The room closes: only the person I let straight in is still remembered, and Eve is not selected any more.
        mp.LeaveTestRoom();
        mp.Members.Clear();
        mp.OnlinePeers.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Dana", Assert.Single(people.MetPeople).Name);
        Assert.Null(people.SelectedMet);
        Assert.False(people.HasPicked);
        Assert.True(people.ShowPickHint);
    });

    [Fact]
    public void TheSelectedPersonStaysSelectedWhenTheListsAreRebuilt() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        main.Shell.Navigate("people");
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Eve", Key(5));
        people.SelectedMet = people.MetPeople.Single();
        // Someone else comes in: the list is refilled with new rows, Eve is still the one on the right.
        Enter(mp, "Finn", Key(6));
        Assert.Equal(2, people.MetPeople.Count);
        Assert.Equal("Eve", people.SelectedMet?.Name);
        Assert.Contains(people.SelectedMet, people.MetPeople);

        people.BackToListCommand.Execute(null);
        Assert.Null(people.SelectedMet);
        Enter(mp, "Gus", Key(7));
        Assert.Null(people.SelectedMet);

        AddFriends(mp, Friend(1, "Robin"), Friend(2, "Sam"));
        people.Selected = people.Friends.First(f => f.Name == "Sam");
        people.ShowSafety = true;
        people.Draft = "half a sentence";
        mp.RefreshContacts();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sam", people.Selected?.Name);
        // The same person: what I had open and what I was typing stay.
        Assert.True(people.ShowSafety);
        Assert.Equal("half a sentence", people.Draft);
        // Someone else: their codes start folded and the draft is not carried over.
        people.Selected = people.Friends.First(f => f.Name == "Robin");
        Assert.False(people.ShowSafety);
        Assert.Equal("", people.Draft);

        // Removing the friend asks first; [Keep] keeps them.
        people.RemoveFriendCommand.Execute(null);
        Assert.True(people.IsConfirmingRemove);
        Assert.Contains("Robin", people.RemoveQuestion);
        Assert.Equal(2, people.Friends.Count);
        people.KeepFriendCommand.Execute(null);
        Assert.False(people.IsConfirmingRemove);
        Assert.Equal(2, people.Friends.Count);
        // A question left open is not carried to someone else.
        people.RemoveFriendCommand.Execute(null);
        people.Selected = people.Friends.First(f => f.Name == "Sam");
        Assert.False(people.IsConfirmingRemove);
        people.Selected = people.Friends.First(f => f.Name == "Robin");

        // Removing the friend leaves nobody selected.
        people.RemoveFriendCommand.Execute(null);
        people.ConfirmRemoveFriendCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sam", Assert.Single(people.Friends).Name);
        Assert.Null(people.Selected);
        mp.LeaveTestRoom();
    });

    [Fact]
    public void LetThemStraightInIsASwitchOnTheFriend() => WithMain((_, main, people, root) =>
    {
        var mp = main.Multiplayer;
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        AddFriends(mp, Friend(1, "Bob", bob.Address), Friend(2, "Bob", bob.Address), Friend(3, "Sam"));
        Assert.Equal(2, people.Friends.Count);
        people.Selected = people.Friends.First(f => f.Name == "Bob");
        Assert.False(people.AutoAdmit);

        var raised = 0;
        people.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PeopleViewModel.AutoAdmit)) raised++; };
        people.AutoAdmit = true;
        Assert.True(people.AutoAdmit);
        Assert.True(raised > 0);
        // Both of their computers, each by its own key and not under their account; nobody else.
        var list = StraightIn(root);
        Assert.Equal(2, list.Count);
        Assert.All(list, m => Assert.Null(m.JayAccount));
        Assert.Equal(2, mp.TrustedPeople.Count);
        people.Selected = people.Friends.First(f => f.Name == "Sam");
        Assert.False(people.AutoAdmit);

        people.Selected = people.Friends.First(f => f.Name == "Bob");
        people.AutoAdmit = false;
        Assert.False(people.AutoAdmit);
        Assert.Empty(StraightIn(root));
        Assert.Empty(mp.TrustedPeople);

        // Let in under their account from a computer that is not in my friend list (it knocked before we were friends, maybe
        // in an older Jay): the account lets none of a friend's devices in, so the switch shows off, and turning it on works
        // in one click and puts their devices on the list by key.
        new TrustedMembers(Path.Combine(root, "trusted.json")).Add(Key(8), "Bob", bob.Address);
        mp.TrustedFile = Path.Combine(root, "trusted.json");
        Assert.False(people.AutoAdmit);
        main.Shell.Navigate("people");
        Dispatcher.UIThread.RunJobs();
        // Their account is a friend's: that computer is not "someone I've met".
        Assert.Empty(people.MetPeople);
        people.AutoAdmit = true;
        Assert.True(people.AutoAdmit);
        Assert.True(mp.Trusted.IsTrusted(Key(1)) && mp.Trusted.IsTrusted(Key(2)));
        // Turning it off takes the computer trusted under their account off too.
        people.AutoAdmit = false;
        Assert.Empty(StraightIn(root));
    });

    /// <summary>
    /// "Let them straight in" trusts a friend's devices one by one: a new device signed by their Jay account still knocks
    /// and asks until I confirm it, and removing the device or the friend takes them off the list.
    /// </summary>
    [Fact]
    public void AFriendsNewDeviceAsksUntilIConfirmItEvenWhenTheyAreLetStraightIn() => WithMain((_, main, people, root) =>
    {
        var mp = main.Multiplayer;
        var me = mp.Identity.PublicKey;
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        using var stranger = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        AddFriends(mp, Friend(1, "Bob", bob.Address));
        people.Selected = people.Friends.Single();
        people.AutoAdmit = true;
        Assert.Null(Assert.Single(StraightIn(root)).JayAccount);

        // Someone who is not my friend, trusted with their account from a knock: their other devices are let in.
        mp.Trusted.Add(Key(9), "Sam", stranger.Address);
        Assert.True(mp.AccountMayAutoAdmit(stranger.Address));

        // Bob's new phone, signed by his account. Even with his account on the list (he knocked from another computer before
        // we were friends), the account doesn't let the phone in: only the devices I confirmed are on the list.
        mp.Trusted.Add(Key(8), "Bob", bob.Address);
        Assert.False(mp.AccountMayAutoAdmit(bob.Address));
        // It proves his account in a live handshake (a knock or a room), which is how a new device arrives.
        using var phone = DeviceKeys.Generate();
        mp.Contacts.SeenLive(phone.Dh.PublicKey, bob.CertifyDevice(phone, "Phone", 0, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        var row = people.Selected!;
        Assert.True(row.DeviceItems.Single(d => d.Device.PublicKey.AsSpan().SequenceEqual(phone.Dh.PublicKey)).IsNew);
        Assert.False(mp.Trusted.IsTrusted(phone.Dh.PublicKey));
        Assert.False(mp.AccountMayAutoAdmit(bob.Address));

        // I look at it and it is his: the switch is on, so it joins his other computer.
        people.ConfirmDeviceCommand.Execute(row.DeviceItems.Single(d => d.IsNew));
        Dispatcher.UIThread.RunJobs();
        Assert.True(mp.Trusted.IsTrusted(phone.Dh.PublicKey));
        Assert.True(mp.Trusted.IsTrusted(Key(1)));

        // Removing the phone takes only the phone off.
        row = people.Selected!;
        mp.RemoveContactDeviceCommand.Execute(row.DeviceItems.Single(d => d.Device.PublicKey.AsSpan().SequenceEqual(phone.Dh.PublicKey)));
        Dispatcher.UIThread.RunJobs();
        Assert.False(mp.Trusted.IsTrusted(phone.Dh.PublicKey));
        Assert.True(mp.Trusted.IsTrusted(Key(1)));
        // The newest device gave the row its name; Bob stays open all the same.
        Assert.Equal("Bob", people.Selected?.Name);

        // "Let them in from now on" on a knock from a friend's device: that device. Their account is kept with it so that
        // removing them finds it, and still lets no other device of theirs in.
        mp.TrustKnocker(Key(7), "Bob", bob.Address);
        Assert.True(mp.Trusted.IsTrusted(Key(7)));
        Assert.False(mp.AccountMayAutoAdmit(bob.Address));

        // Removing the friend takes everything of theirs off the list; Sam stays.
        people.RemoveFriendCommand.Execute(null);
        people.ConfirmRemoveFriendCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Sam", Assert.Single(StraightIn(root)).Name);
    });

    [Fact]
    public void LetThemStraightInWorksForSomeoneIveMetAndAChildCanOnlyTurnItOff() => WithMain((service, main, people, root) =>
    {
        var mp = main.Multiplayer;
        main.Shell.Navigate("people");
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Eve", Key(5));
        people.SelectedMet = people.MetPeople.Single();
        Assert.False(people.MetAutoAdmit);
        Assert.True(people.CanChangeMetAutoAdmit);
        Assert.False(people.ShowKnocksAlwaysAsk);
        Assert.True(people.CanAddMet);

        people.MetAutoAdmit = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(people.MetAutoAdmit);
        // Only this device of theirs: no Jay account is kept with it.
        Assert.Null(Assert.Single(StraightIn(root), m => m.Name == "Eve").JayAccount);
        // The room console shows the same fact on the member.
        Assert.True(mp.Members.Single(m => m.Name == "Eve").Trusted);
        Assert.Equal("Eve", people.SelectedMet?.Name);

        // "For a child" lists nobody, people I've met included (every knock asks then anyway).
        service.Settings.ChildMode.SetEnabled(true, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.False(people.HasMet);
        Assert.False(people.HasList);
        Assert.False(people.HasSelectedMet);
        Assert.Null(people.SelectedMet);
        Assert.False(people.CanAddMet);
        Assert.True(people.ShowChildNote);

        service.Settings.ChildMode.SetEnabled(false, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.True(people.HasMet);
        people.SelectedMet = people.MetPeople.Single();
        Assert.True(people.CanChangeMetAutoAdmit);
        people.MetAutoAdmit = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(StraightIn(root));
        Assert.False(mp.Members.Single(m => m.Name == "Eve").Trusted);
        Assert.False(people.MetAutoAdmit);
        mp.LeaveTestRoom();
    });

    [Fact]
    public void WhatIsKnownAboutAFriendComesFromTheRoomTheMailAndTheContactFile() => WithMain((_, main, people, root) =>
    {
        var mp = main.Multiplayer;
        var me = mp.Identity.PublicKey;
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        AddFriends(mp, Friend(1, "Bob", bob.Address));
        people.Selected = people.Friends.Single();
        var row = people.Selected!;
        Assert.Equal(FriendState.Verified, row.State);
        Assert.True(row.IsQuiet);
        Assert.Equal(L.T("people.state.verified"), row.StateText);
        Assert.Equal(L.T("people.safety.verified"), people.SafetyText);
        Assert.True(people.SafetyIsFine);
        Assert.False(people.CanMessage);
        Assert.True(people.ShowMessageHint);
        Assert.False(people.CanJoinRoom);
        Assert.Equal("", people.JoinRoomText);

        // They mailed me an invite to their room.
        using var host = X25519KeyPair.Generate();
        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, TunnelSettings.Default, boundTo: me).Invite;
        var mailed = new MailedInvite(row.Friend.Contact, invite, host.PublicKey, "Bob's world", DateTimeOffset.UtcNow.AddMinutes(30));
        mp.ShowMailedInvites([mailed]);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(row, people.Selected);
        Assert.Equal(FriendState.InTheirRoom, row.State);
        Assert.True(row.IsInRoom);
        Assert.Equal(L.F("people.state.in_room", "Bob's world"), row.StateText);
        Assert.True(people.CanJoinRoom);
        Assert.Equal(L.F("people.chat.join", "Bob's world"), people.JoinRoomText);
        var note = Assert.Single(people.Lines!);
        Assert.True(note.IsNote);
        Assert.Equal(L.F("people.line.invited_me", "Bob's world"), note.Text);
        Assert.False(note.HasDelivery);
        // The same invite is shown again after a restart of the mail check: still one line.
        mp.ShowMailedInvites([mailed]);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(people.Lines!);

        // Being in the same room says more than an invite.
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Bob", Key(1));
        Assert.Equal(FriendState.SameRoom, row.State);
        Assert.True(row.IsHere);
        Assert.Equal(L.T("people.state.same_room"), row.StateText);
        Assert.True(people.CanMessage);
        Assert.False(people.ShowMessageHint);
        Assert.True(people.CanInvite);

        // A new computer signed by their account: that is what the row says until I have looked at it.
        // It proves his account in a live handshake (a knock or a room), which is how a new device arrives.
        using var phone = DeviceKeys.Generate();
        mp.Contacts.SeenLive(phone.Dh.PublicKey, bob.CertifyDevice(phone, "Phone", 0, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        row = people.Selected!;
        Assert.Equal("Bob", row.Name);
        Assert.Equal(2, row.DeviceItems.Count);
        Assert.Equal(FriendState.NewDevice, row.State);
        Assert.True(row.NeedsLook);
        Assert.True(people.SafetyNeedsLook);
        Assert.Equal(L.T("people.state.new_device"), people.SafetyText);
        Assert.Equal(L.T("people.state.new_device"), people.SafetyShortText);
        // The switch lets in the computers I have looked at, not the new one.
        people.AutoAdmit = true;
        Assert.Equal(Key(1), Assert.Single(StraightIn(root)).PublicKey);
        people.AutoAdmit = false;

        people.ConfirmDeviceCommand.Execute(row.DeviceItems.Single(d => d.IsNew));
        Dispatcher.UIThread.RunJobs();
        row = people.Selected!;
        Assert.Equal(FriendState.SameRoom, row.State);
        Assert.False(people.SafetyNeedsLook);
        Assert.DoesNotContain(row.DeviceItems, d => d.IsNew);

        mp.LeaveTestRoom();
        mp.Members.Clear();
        mp.OnlinePeers.Clear();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FriendState.InTheirRoom, people.Selected!.State);
    });

    [Fact]
    public void TheConversationKeepsMessagesAndSaysWhereAnInviteIs() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        AddFriends(mp, Friend(1, "Robin"), Friend(2, "Sam"));
        var robin = MemberIdentity.FingerprintOf(Key(1));
        people.Selected = people.Friends.First(f => f.Name == "Robin");
        Assert.True(people.HasNoLines);
        Assert.Empty(people.Lines!);
        var added = 0;
        people.LineAdded += () => added++;

        mp.FriendMessageForTest(Key(1), "ready?", mine: false);
        mp.FriendMessageForTest(Key(1), "two minutes", mine: true);
        // A private message with someone who is not a friend stays in the room.
        mp.FriendMessageForTest(Key(7), "psst", mine: false);
        Assert.Equal(2, people.Lines!.Count);
        Assert.Equal(2, added);
        Assert.False(people.HasNoLines);
        Assert.True(people.Lines[0].IsTheirs);
        Assert.Equal("ready?", people.Lines[0].Text);
        Assert.True(people.Lines[1].IsMine);
        Assert.False(people.Lines[1].HasDelivery);

        // An invite I mailed: handed over, then no receipt for a while, then the receipt.
        mp.SentInviteForTest("mail-1", people.Selected!.Friend);
        var line = people.Lines[2];
        Assert.True(line.IsNote);
        Assert.Equal(L.T("people.line.invited"), line.Text);
        Assert.Equal(DeliveryState.Sent, line.Delivery);
        Assert.Equal(L.T("people.line.sent"), line.DeliveryText);
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        mp.ShowHeldBack([new SentMail(Key(1), "mail-1", MailKind.Invite, DateTimeOffset.UtcNow)]);
        Assert.Equal(DeliveryState.Held, line.Delivery);
        Assert.True(line.IsHeld);
        Assert.Equal(L.T("people.line.held"), line.DeliveryText);
        Assert.Contains(nameof(FriendLine.DeliveryText), changed);
        mp.InviteReceiptForTest("mail-1");
        Assert.Equal(DeliveryState.Delivered, line.Delivery);
        Assert.True(line.IsDelivered);
        Assert.Contains(L.T("people.line.delivered"), line.Spoken);
        // A late "held back" does not take the receipt away.
        mp.ShowHeldBack([new SentMail(Key(1), "mail-1", MailKind.Invite, DateTimeOffset.UtcNow)]);
        Assert.Equal(DeliveryState.Delivered, line.Delivery);

        // The conversation belongs to the person: it is the same after the list is rebuilt, and the other friend's is empty.
        var lines = people.Lines;
        mp.RefreshContacts();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(lines, people.Lines);
        people.Selected = people.Friends.First(f => f.Name == "Sam");
        Assert.Empty(people.Lines!);
        Assert.True(people.HasNoLines);
        people.BackToListCommand.Execute(null);
        Assert.Null(people.Lines);

        // Old lines fall off the top, and an invite note that falls off stops waiting for its receipt.
        mp.SentInviteForTest("mail-2", mp.ContactList.First(f => f.Name == "Robin"));
        Assert.Equal(1, mp.InviteNotesWaiting);
        for (var i = 0; i < MultiplayerViewModel.MaxFriendLines + 5; i++) mp.FriendMessageForTest(Key(1), "line " + i, mine: true);
        Assert.Equal(MultiplayerViewModel.MaxFriendLines, lines.Count);
        Assert.Equal("line " + (MultiplayerViewModel.MaxFriendLines + 4), lines[^1].Text);
        Assert.Equal(0, mp.InviteNotesWaiting);
    });

    [Fact]
    public void AMessageNeedsTheFriendInTheSameRoomAndSaysSo() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        AddFriends(mp, Friend(1, "Robin"));
        people.Selected = people.Friends.Single();
        Assert.Equal(L.F("people.chat.placeholder", "Robin"), people.DraftPlaceholder);
        Assert.False(people.CanMessage);
        Assert.True(people.ShowMessageHint);

        people.Draft = "are you there?";
        Wait(people.SendCommand.ExecuteAsync(null));
        // Nothing was sent: the words go back into the box and nothing is added to the conversation.
        Assert.Equal("are you there?", people.Draft);
        Assert.Empty(people.Lines!);
        Assert.Equal(L.T("people.chat.needs_room"), mp.Status);

        people.Draft = "   ";
        Wait(people.SendCommand.ExecuteAsync(null));
        Assert.Empty(people.Lines!);
    });

    /// <summary>A friend is known by their device key: a name or a safety code someone else shows doesn't make them the friend.</summary>
    [Fact]
    public void OnlyTheFriendsOwnKeyReachesTheirConversation() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        AddFriends(mp, Friend(1, "Robin"));
        people.Selected = people.Friends.Single();
        mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(1)), Key(7), "Robin")));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(mp.PeerOf(people.Selected!.Friend));
        mp.FriendMessageForTest(Key(7), "it's me", mine: false);
        Assert.Empty(people.Lines!);
        mp.FriendMessageForTest(Key(1), "the real one", mine: false);
        Assert.Equal("the real one", Assert.Single(people.Lines!).Text);
        mp.OnlinePeers.Clear();
        Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void ARosterEntryWithAFriendsKeyButAnotherFingerprintCannotBeMessaged() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        AddFriends(mp, Friend(1, "Robin"));
        people.Selected = people.Friends.Single();
        // The friend's key, with someone else's fingerprint (the one messages would be addressed to).
        var forged = new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(7)), Key(1), "Robin"));
        mp.OnlinePeers.Add(forged);
        Dispatcher.UIThread.RunJobs();
        Assert.False(MultiplayerViewModel.KeyMatches(forged.Entry));
        Assert.Null(mp.PeerOf(people.Selected!.Friend));
        Assert.False(people.CanMessage);
        Assert.False(mp.SendPrivateToAsync(forged, "hello").GetAwaiter().GetResult());
        Assert.Empty(people.Lines!);
        Assert.DoesNotContain(mp.MetPeople, p => p.PublicKey.AsSpan().SequenceEqual(Key(1)));

        // The real entry for the same friend can be.
        mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(1)), Key(1), "Robin")));
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(mp.PeerOf(people.Selected!.Friend));
        Assert.True(people.CanMessage);
        mp.OnlinePeers.Clear();
        Dispatcher.UIThread.RunJobs();
    });

    [Fact]
    public void RemovingAFriendDeviceByDeviceLeavesNothingOfTheirAccountTrusted() => WithMain((service, main, people, root) =>
    {
        var mp = main.Multiplayer;
        var me = mp.Identity.PublicKey;
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        AddFriends(mp, Friend(1, "Bob", bob.Address));
        people.Selected = people.Friends.Single();
        people.AutoAdmit = true;
        // His account went on the list before we were friends (he knocked from another computer).
        mp.Trusted.Add(Key(7), "Bob", bob.Address);
        Assert.False(mp.AccountMayAutoAdmit(bob.Address));

        // A phone signed by his account, waiting for me to look at it.
        // It proves his account in a live handshake (a knock or a room), which is how a new device arrives.
        using var phone = DeviceKeys.Generate();
        mp.Contacts.SeenLive(phone.Dh.PublicKey, bob.CertifyDevice(phone, "Phone", 0, DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        var row = people.Selected!;
        Assert.Equal(2, row.DeviceItems.Count);

        // I remove his computer, then say the phone is not him.
        mp.RemoveContactDeviceCommand.Execute(row.DeviceItems.Single(d => d.Device.PublicKey.AsSpan().SequenceEqual(Key(1))));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(people.Friends.Single().DeviceItems);
        // The last device can't be removed on its own: that is removing the friend, which asks first.
        mp.RemoveContactDeviceCommand.Execute(people.Friends.Single().DeviceItems.Single());
        Assert.Single(people.Friends);
        service.Requests.Resolve(service.Requests.Pending.Single(r => r.Spec.Kind == "contact-new-device"), "remove", RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(people.Friends);

        // Nothing under his account lets anyone in, the rejected phone least of all.
        Assert.False(mp.AccountMayAutoAdmit(bob.Address));
        Assert.DoesNotContain(StraightIn(root), m => string.Equals(m.JayAccount, bob.Address, StringComparison.OrdinalIgnoreCase));
        Assert.False(mp.Trusted.IsTrusted(phone.Dh.PublicKey));
    });

    [Fact]
    public void InSomeoneElsesRoomTheHostsListIsOnlyTheHostsWord() => WithMain((_, main, people, root) =>
    {
        var mp = main.Multiplayer;
        main.Shell.Navigate("people");
        AddFriends(mp, Friend(1, "Robin"));
        mp.EnterTestRoom(HostRoom(mp), host: false);
        mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(1)), Key(1), "Robin")));
        mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(Key(9)), Key(9), "Dana")));
        Dispatcher.UIThread.RunJobs();

        // My friend is there by the host's word: messages can go (to their key alone), and the row says whose word it is.
        var row = people.Friends.Single();
        Assert.Equal(FriendState.ListedInRoom, row.State);
        Assert.True(row.IsHere);
        Assert.Equal(L.T("people.state.listed_in_room"), row.StateText);
        people.Selected = row;
        Assert.True(people.CanMessage);

        // Someone only the host lists can't be let straight in from here.
        people.SelectedMet = people.MetPeople.Single();
        Assert.False(people.SelectedMet!.Checked);
        Assert.False(people.CanChangeMetAutoAdmit);
        Assert.True(people.ShowMetUnchecked);
        people.MetAutoAdmit = true;
        mp.SetAutoAdmit(people.SelectedMet!, true);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(StraightIn(root));
        mp.OnlinePeers.Clear();
        mp.LeaveTestRoom();
    });

    [Fact]
    public void ARefreshKeepsTheRemoveQuestionOpen() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        AddFriends(mp, Friend(1, "Robin"), Friend(2, "Sam"));
        people.Selected = people.Friends.First(f => f.Name == "Robin");
        people.RemoveFriendCommand.Execute(null);
        Assert.True(people.IsConfirmingRemove);
        // Something else about the contacts changes and the list is rebuilt in the background.
        mp.RefreshContacts();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Robin", people.Selected!.Name);
        Assert.True(people.IsConfirmingRemove);
        people.KeepFriendCommand.Execute(null);
    });

    [Fact]
    public void RequestsShowOnTheEntryInOneLineAndTheEmojiOpenTheAddPanel() => WithMain((service, main, people, root) =>
    {
        var mp = main.Multiplayer;
        var me = mp.Identity.PublicKey;
        Assert.Equal(0, people.Badge);
        Assert.False(people.ShowRequestsLine);

        // Someone asks to add me.
        using var alex = X25519KeyPair.Generate();
        var alexBook = new ContactBook(Path.Combine(root, "alex.json")) { Me = alex.PublicKey };
        mp.Contacts.Receive(alex.PublicKey, alexBook.StartAdd(alex.PublicKey, me, "Alex")!, "Me");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, people.Badge);
        Assert.True(people.HasRequests);
        Assert.True(people.ShowRequestsLine);
        Assert.Equal(L.F("people.requests", 1), people.RequestsText);
        people.OpenAddCommand.Execute(null);
        Assert.True(people.ShowAddPanel);
        Assert.False(people.ShowRequestsLine);
        people.CloseAddCommand.Execute(null);

        // I add someone: no red dot for my own request. When they accept, the request bar's [Compare] opens the panel.
        using var sam = X25519KeyPair.Generate();
        var samBook = new ContactBook(Path.Combine(root, "sam.json")) { Me = sam.PublicKey };
        var hello = mp.Contacts.StartAdd(me, sam.PublicKey, "Me")!;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, people.RequestCount);
        Assert.Equal(1, people.Badge);
        Assert.Null(samBook.Receive(me, hello, "Sam"));
        mp.Contacts.Receive(sam.PublicKey, samBook.Accept(me, "Sam")!, "Me");
        Dispatcher.UIThread.RunJobs();
        var compare = service.Requests.Pending.Single(r => r.Spec.Kind == "contact-compare");
        main.Shell.Navigate("library");
        service.Requests.Resolve(compare, "open", RequestSource.Bar);
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<PeopleViewModel>(main.CurrentPage);
        Assert.True(people.ShowAddPanel);
    });

    [Fact]
    public void AFriendCodePastedAnywhereOpensTheAddPanelAndStaysThere() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        main.FirstRun.IsOpen = false;
        var code = ContactCode.Format(Key(4));
        main.Shell.Navigate("library");
        Assert.True(main.PasteCode(code));
        Dispatcher.UIThread.RunJobs();
        // Not the lobby: the code belongs on the Friends page, in the add panel, and nothing was sent.
        Assert.Equal("people/add", main.Shell.Router.Current.ToString());
        Assert.IsType<PeopleViewModel>(main.CurrentPage);
        Assert.True(people.ShowAddPanel);
        Assert.Equal(code, people.CodeInput);
        Assert.Empty(mp.ContactRequests);

        // An invite code still goes to the lobby's paste box; other text is left alone.
        Assert.True(main.PasteCode("JAYS2-0123456789ABCDEFGHJK"));
        Assert.Equal("play", main.Shell.Router.Current.ToString());
        Assert.False(main.PasteCode("hello"));
        Assert.Equal("play", main.Shell.Router.Current.ToString());

        // The old friends card in multiplayer settings (saved in help links or history) opens the add panel too.
        main.Shell.Navigate("play/settings#ContactsCard");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("people/add", main.Shell.Router.Current.ToString());
        Assert.True(people.ShowAddPanel);
    });

    [Fact]
    public void AskingSomeoneIveMetToBeFriendsNeedsNoPastedCode() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        main.Shell.Navigate("people");
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Eve", Key(5));
        people.SelectedMet = people.MetPeople.Single();
        // A code I pasted for someone else waits in the add panel.
        var pasted = ContactCode.Format(Key(6));
        mp.ContactCodeInput = pasted;
        Wait(people.AddMetCommand.ExecuteAsync(null));
        // Their friend code is the key the room already showed me, so the code box is left alone. No public network here,
        // so nothing was mailed.
        Assert.Equal(pasted, mp.ContactCodeInput);
        Assert.Empty(mp.ContactRequests);
        Assert.False(people.IsAdding);
        mp.LeaveTestRoom();
    });

    [Fact]
    public void ForAChildThePageKeepsOnlyTheNote() => WithMain((service, main, people, root) =>
    {
        var mp = main.Multiplayer;
        var me = mp.Identity.PublicKey;
        AddFriends(mp, Friend(1, "Robin"));
        using var alex = X25519KeyPair.Generate();
        var alexBook = new ContactBook(Path.Combine(root, "alex.json")) { Me = alex.PublicKey };
        mp.Contacts.Receive(alex.PublicKey, alexBook.StartAdd(alex.PublicKey, me, "Alex")!, "Me");
        Dispatcher.UIThread.RunJobs();
        people.Selected = people.Friends.Single();
        people.OpenAddCommand.Execute(null);
        Assert.True(people.ShowTopRow);
        Assert.Equal(1, people.Badge);

        service.Settings.ChildMode.SetEnabled(true, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.True(people.IsChildMode);
        Assert.True(people.ShowChildNote);
        Assert.False(people.ShowTopRow);
        Assert.False(people.HasFriends);
        Assert.False(people.HasSelected);
        Assert.False(people.ShowAddPanel);
        Assert.False(people.HasRequests);
        Assert.False(people.ShowRequestsLine);
        Assert.False(people.ShowConnect);
        Assert.False(people.ShowEmpty);
        Assert.False(people.ShowPickHint);
        Assert.Equal(0, people.Badge);
        Assert.Null(people.Lines);
        // Neither the button nor the route opens the panel.
        people.OpenAddCommand.Execute(null);
        main.Shell.Navigate("people/add");
        Assert.False(people.IsAdding);

        service.Settings.ChildMode.SetEnabled(false, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.False(people.IsChildMode);
        Assert.True(people.HasFriends);
        Assert.Equal(1, people.Badge);
        Assert.True(people.ShowPickHint);
    });
}

/// <summary>What the Friends page writes follows the language while it is open.</summary>
[Collection("GlobalLanguage")]
public sealed class FriendsPageLanguageTests
{
    [Fact]
    public void RowsAndConversationNotesFollowTheLanguage() => FriendsPageTests.WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        var language = L.Language;
        try
        {
            L.SetLanguage("zh-Hans");
            mp.Contacts.Import([new Contact(FriendsPageTests.Key(1), "Robin", true, DateTimeOffset.UtcNow)]);
            mp.RefreshContacts();
            Dispatcher.UIThread.RunJobs();
            people.Selected = people.Friends.Single();
            mp.SentInviteForTest("mail-1", people.Selected!.Friend);
            var row = people.Selected;
            var line = people.Lines!.Single();
            var chinese = (row.StateText, line.Text, line.DeliveryText, people.SafetyText, people.DraftPlaceholder);

            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            line.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            L.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, raised.Count(name => name == string.Empty));
            Assert.Equal(L.T("people.state.verified"), row.StateText);
            Assert.Equal(L.T("people.line.invited"), line.Text);
            Assert.Equal(L.T("people.line.sent"), line.DeliveryText);
            Assert.Equal(L.F("people.chat.placeholder", "Robin"), people.DraftPlaceholder);
            Assert.NotEqual(chinese, (row.StateText, line.Text, line.DeliveryText, people.SafetyText, people.DraftPlaceholder));
        }
        finally
        {
            L.SetLanguage(language);
        }
    });
}
