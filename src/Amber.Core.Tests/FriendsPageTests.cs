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

        // Removing the friend leaves nobody selected.
        people.RemoveFriendCommand.Execute(null);
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
        // Both of their computers, under their account; nobody else.
        var list = StraightIn(root);
        Assert.Equal(2, list.Count);
        Assert.All(list, m => Assert.Equal(bob.Address, m.JayAccount));
        Assert.Equal(2, mp.TrustedPeople.Count);
        people.Selected = people.Friends.First(f => f.Name == "Sam");
        Assert.False(people.AutoAdmit);

        people.Selected = people.Friends.First(f => f.Name == "Bob");
        people.AutoAdmit = false;
        Assert.False(people.AutoAdmit);
        Assert.Empty(StraightIn(root));
        Assert.Empty(mp.TrustedPeople);

        // Let in under their account from a computer that is not in my friend list (it knocked before we were friends):
        // the switch shows on, and turning it off takes that one off too.
        new TrustedMembers(Path.Combine(root, "trusted.json")).Add(Key(8), "Bob", bob.Address);
        mp.TrustedFile = Path.Combine(root, "trusted.json");
        Assert.True(people.AutoAdmit);
        main.Shell.Navigate("people");
        Dispatcher.UIThread.RunJobs();
        // Their account is a friend's: that computer is not "someone I've met".
        Assert.Empty(people.MetPeople);
        people.AutoAdmit = false;
        Assert.Empty(StraightIn(root));
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
        Assert.Equal("Eve", Assert.Single(StraightIn(root)).Name);
        // The room console shows the same fact on the member.
        Assert.True(mp.Members.Single(m => m.Name == "Eve").Trusted);
        Assert.Equal("Eve", people.SelectedMet?.Name);

        // "For a child" asks about every knock: the switch can still be turned off, and says why it does nothing.
        service.Settings.ChildMode.SetEnabled(true, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.True(people.ShowKnocksAlwaysAsk);
        Assert.True(people.CanChangeMetAutoAdmit);
        Assert.False(people.CanAddMet);
        people.MetAutoAdmit = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(StraightIn(root));
        Assert.False(mp.Members.Single(m => m.Name == "Eve").Trusted);
        Assert.Equal("Eve", people.SelectedMet?.Name);
        Assert.False(people.MetAutoAdmit);
        Assert.False(people.CanChangeMetAutoAdmit);

        service.Settings.ChildMode.SetEnabled(false, DateTimeOffset.UtcNow);
        mp.ApplyChildMode();
        Dispatcher.UIThread.RunJobs();
        Assert.True(people.CanChangeMetAutoAdmit);
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
        using var phone = X25519KeyPair.Generate();
        var phoneBook = new ContactBook(Path.Combine(root, "bob-phone.json"))
            { Me = phone.PublicKey, MyCertificate = bob.CertifyDevice(phone.PublicKey, "Phone", 0, DateTimeOffset.UtcNow) };
        var reply = mp.Contacts.Receive(phone.PublicKey, phoneBook.StartAdd(phone.PublicKey, me, "Bob")!, "Me");
        if (reply is not null && phoneBook.Receive(me, reply, "Bob") is { } nonce) mp.Contacts.Receive(phone.PublicKey, nonce, "Me");
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

        mp.FriendMessageForTest(robin, "ready?", mine: false);
        mp.FriendMessageForTest(robin, "two minutes", mine: true);
        // A private message with someone who is not a friend stays in the room.
        mp.FriendMessageForTest(MemberIdentity.FingerprintOf(Key(7)), "psst", mine: false);
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

        // Old lines fall off the top.
        for (var i = 0; i < MultiplayerViewModel.MaxFriendLines + 5; i++) mp.FriendMessageForTest(robin, "line " + i, mine: true);
        Assert.Equal(MultiplayerViewModel.MaxFriendLines, lines.Count);
        Assert.Equal("line " + (MultiplayerViewModel.MaxFriendLines + 4), lines[^1].Text);
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
    public void AskingSomeoneIveMetToBeFriendsNeedsNoPastedCode() => WithMain((_, main, people, _) =>
    {
        var mp = main.Multiplayer;
        main.Shell.Navigate("people");
        mp.EnterTestRoom(HostRoom(mp), host: true);
        Enter(mp, "Eve", Key(5));
        people.SelectedMet = people.MetPeople.Single();
        Wait(people.AddMetCommand.ExecuteAsync(null));
        // Their friend code is the key the room already showed me. No public network here, so nothing was mailed.
        Assert.Equal(ContactCode.Format(Key(5)), people.CodeInput);
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
