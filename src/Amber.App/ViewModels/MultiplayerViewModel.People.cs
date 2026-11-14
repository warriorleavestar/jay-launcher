using System.Collections.ObjectModel;
using Amber.App.Localization;
using Amber.App.Services;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>
/// What this computer knows about a friend right now. Jay has no presence service, so there is no "online" or
/// "offline": only what the room I am in, the mail I received and the contact file say.
/// </summary>
public enum FriendState
{
    /// <summary>Emoji compared; nothing else is known.</summary>
    Verified,
    /// <summary>In the room I am in right now.</summary>
    SameRoom,
    /// <summary>
    /// In someone else's room, by the host's list. Only the host vouches for it; messages still go to the friend's key alone.
    /// </summary>
    ListedInRoom,
    /// <summary>They mailed me an invite to their room and it still works.</summary>
    InTheirRoom,
    /// <summary>A device their Jay account vouched for that I have not looked at yet.</summary>
    NewDevice,
}

/// <summary>Where something I mailed to a friend is, as far as the receipts say.</summary>
public enum DeliveryState
{
    None,
    /// <summary>Handed to the network; no receipt yet.</summary>
    Sent,
    /// <summary>Their Jay picked it up and said so.</summary>
    Delivered,
    /// <summary>No receipt after a few minutes: it may have been held back.</summary>
    Held,
}

public enum FriendLineKind
{
    /// <summary>Something that happened ("You invited Alex to your room").</summary>
    Note,
    Mine,
    Theirs,
}

/// <summary>
/// One line of the conversation with a friend. Notes are written from their key when read, so they follow the language.
/// Lines live in memory for this run of Jay only: nothing of a conversation is written to disk.
/// </summary>
public sealed partial class FriendLine : ObservableObject
{
    private readonly Func<string> _text;

    internal FriendLine(FriendLineKind kind, Func<string> text, DateTimeOffset time)
    {
        Kind = kind;
        _text = text;
        Time = time;
        LanguageListeners.Add(this, static o => ((FriendLine)o).OnPropertyChanged(string.Empty));
    }

    public FriendLineKind Kind { get; }
    public DateTimeOffset Time { get; }
    public bool IsNote => Kind == FriendLineKind.Note;
    public bool IsMine => Kind == FriendLineKind.Mine;
    public bool IsTheirs => Kind == FriendLineKind.Theirs;
    public string Text => _text();
    public string TimeText => Time.ToLocalTime().ToString("HH:mm");

    /// <summary>The row under "where to play" this note is about (their invite): one note per invite.</summary>
    public string? RoomKey { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDelivery), nameof(DeliveryText), nameof(IsDelivered), nameof(IsHeld), nameof(Spoken))]
    public partial DeliveryState Delivery { get; set; }

    public bool HasDelivery => Delivery != DeliveryState.None;
    public bool IsDelivered => Delivery == DeliveryState.Delivered;
    public bool IsHeld => Delivery == DeliveryState.Held;

    public string DeliveryText => Delivery switch
    {
        DeliveryState.Sent => L.T("people.line.sent"),
        DeliveryState.Delivered => L.T("people.line.delivered"),
        DeliveryState.Held => L.T("people.line.held"),
        _ => "",
    };

    /// <summary>For screen readers: the time, the text and where it is.</summary>
    public string Spoken => string.Join(" · ", new[] { TimeText, Text, DeliveryText }.Where(s => s.Length > 0));
}

/// <summary>
/// Someone who is or was in a room with me and has not compared emoji with me ("someone I've met"): the people in the
/// room now, and the people I told Jay to let straight in. Only the second kind is remembered after the room closes.
/// </summary>
public sealed record MetPersonItem(byte[] PublicKey, string Name, string? JayAccount, bool AutoAdmit, bool InRoom) : LocalizedRecord
{
    /// <summary>
    /// This computer checked the key itself (it knocked on my room, or I let it in before). Someone only on another host's
    /// list is not: that host could have put any key under any name there, so they can't be let straight in from here.
    /// </summary>
    public bool Checked { get; init; } = true;

    public string DisplayName => Name.Length > 0 ? Name : L.T("multiplayer.peers.player");
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
    public string StateText => L.T(InRoom ? "people.met.in_room" : "people.met.before");
    public bool HasJay => JayAccount is not null;
    public string JayText => MultiplayerViewModel.JayLine(JayAccount) ?? "";
    public string Summary => DisplayName + " · " + StateText;
}

/// <summary>
/// What the Friends page needs from multiplayer: the people I have met, the "let them straight in" switch (the old
/// trusted list, now one switch per person), what is known about a friend right now, and the conversation with a friend.
/// </summary>
/// <remarks>
/// Messages reach a friend only while both are in the same room (the room's private channel, end to end). Mail between
/// friends carries invites and their receipts, nothing else; offline messages are not built yet.
/// </remarks>
public sealed partial class MultiplayerViewModel
{
    private readonly Dictionary<string, ObservableCollection<FriendLine>> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FriendLine> _inviteLines = new(StringComparer.Ordinal);
    private bool _metPeopleWanted;

    /// <summary>The most lines kept per friend.</summary>
    internal const int MaxFriendLines = 300;

    /// <summary>Invite notes still waiting for a receipt (tests).</summary>
    internal int InviteNotesWaiting => _inviteLines.Count;

    public ObservableCollection<MetPersonItem> MetPeople { get; } = [];

    /// <summary>Something the Friends page shows about people changed (who is in the room, who is let straight in, rooms I was invited to).</summary>
    internal event Action? PeopleFactsChanged;

    /// <summary>
    /// The friend list was rebuilt from the contact file and is whole again. (The list is cleared and refilled row by
    /// row; a page that followed every step would see it empty in between and forget who was selected.)
    /// </summary>
    internal event Action? FriendsRebuilt;

    /// <summary>Go to the Friends page with the add panel open.</summary>
    internal void OpenAddFriend() => Navigate?.Invoke("people/add");

    /// <summary>Multiplayer settings: friends moved to their own page.</summary>
    [RelayCommand]
    private void OpenFriends() => Navigate?.Invoke("people");

    private void InitPeople()
    {
        Members.CollectionChanged += (_, _) => QueueMetPeopleRefresh();
        OnlinePeers.CollectionChanged += (_, _) => QueueMetPeopleRefresh();
        TrustedPeople.CollectionChanged += (_, _) => QueueMetPeopleRefresh();
        FriendRooms.CollectionChanged += (_, _) => PeopleFactsChanged?.Invoke();
    }

    /// <summary>
    /// The Friends page is open: start listing the people I have met. Not before, so starting Jay does not read the
    /// trusted list.
    /// </summary>
    internal void ShowMetPeople()
    {
        _metPeopleWanted = true;
        RefreshMetPeople();
    }

    private bool _metPeopleQueued;

    /// <summary>
    /// A list the met people come from changed. Lists are refilled one row at a time, so the work (which reads the trusted
    /// list and the contact book) runs once, after the UI thread is done with this round of changes.
    /// </summary>
    private void QueueMetPeopleRefresh()
    {
        if (!_metPeopleWanted)
        {
            PeopleFactsChanged?.Invoke();
            return;
        }
        if (_metPeopleQueued) return;
        _metPeopleQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _metPeopleQueued = false;
            RefreshMetPeople();
        });
    }

    internal void RefreshMetPeople()
    {
        if (!_metPeopleWanted)
        {
            PeopleFactsChanged?.Invoke();
            return;
        }
        var verified = Contacts.All.Where(c => c.Verified).ToList();
        var friendKeys = verified.Select(c => Convert.ToHexString(c.PublicKey)).ToHashSet();
        var friendAccounts = verified.Where(c => c.JayAccount is not null).Select(c => c.JayAccount!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsFriend(byte[] key, string? account) => friendKeys.Contains(Convert.ToHexString(key)) || (account is not null && friendAccounts.Contains(account));

        var me = Identity.PublicKey;
        var online = OnlinePeers.Where(p => KeyMatches(p.Entry)).Select(p => p.Entry.Fingerprint).ToHashSet();
        var met = new Dictionary<string, MetPersonItem>();
        foreach (var t in Trusted.All)
            if (!IsFriend(t.PublicKey, t.JayAccount))
                met[Convert.ToHexString(t.PublicKey)] = new MetPersonItem(t.PublicKey, t.Name, t.JayAccount, true, online.Contains(t.Fingerprint));
        foreach (var m in Members)
        {
            if (IsFriend(m.PublicKey, m.JayAccount)) continue;
            var hex = Convert.ToHexString(m.PublicKey);
            var on = m.Trusted || Trusted.IsTrusted(m.PublicKey) || Trusted.IsTrustedJayAccount(m.JayAccount);
            met[hex] = new MetPersonItem(m.PublicKey, m.Name, m.JayAccount, on, online.Contains(m.Fingerprint));
        }
        foreach (var p in OnlinePeers)
        {
            if (!KeyMatches(p.Entry)) continue;
            var key = p.Entry.PublicKey;
            if (key.AsSpan().SequenceEqual(me) || IsFriend(key, null)) continue;
            var hex = Convert.ToHexString(key);
            if (met.TryGetValue(hex, out var known)) met[hex] = known with { InRoom = true };
            // My own room's list comes from the knocks I checked; another host's list is only that host's word.
            else met[hex] = new MetPersonItem(key, p.Entry.Name, null, Trusted.IsTrusted(key), true) { Checked = IsHosting };
        }

        var rows = met.Values.OrderByDescending(p => p.InRoom).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var same = rows.Count == MetPeople.Count && rows.Zip(MetPeople).All(pair =>
            pair.First.PublicKey.AsSpan().SequenceEqual(pair.Second.PublicKey) && pair.First.Name == pair.Second.Name && pair.First.JayAccount == pair.Second.JayAccount
            && pair.First.AutoAdmit == pair.Second.AutoAdmit && pair.First.InRoom == pair.Second.InRoom && pair.First.Checked == pair.Second.Checked);
        if (!same)
        {
            MetPeople.Clear();
            foreach (var row in rows) MetPeople.Add(row);
        }
        PeopleFactsChanged?.Invoke();
    }

    // ---- "let them straight in" ----

    /// <summary>"For a child" asks about every knock, whatever this switch says.</summary>
    internal bool KnocksAlwaysAsk => Child.AlwaysAskKnock;

    /// <summary>
    /// The switch is on when every device of theirs I have confirmed is on the list by its key. A Jay account on the list
    /// (trusted from a knock, possibly by an older Jay) does not count: it lets no device of a friend in by itself, see
    /// <see cref="AccountMayAutoAdmit"/>. A device I have not confirmed yet always asks.
    /// </summary>
    internal bool IsAutoAdmitted(ContactItem friend) =>
        friend.Devices.Where(d => d.MayBeAutoAdmitted).ToList() is { Count: > 0 } confirmed && confirmed.All(d => Trusted.IsTrusted(d.PublicKey));

    /// <summary>
    /// Turns "let them straight in" on or off for a friend: every device I have looked at goes on the list by its key alone
    /// (a new device waits until I confirm it), or every one of theirs comes off.
    /// </summary>
    internal void SetAutoAdmit(ContactItem friend, bool on)
    {
        if (on)
        {
            foreach (var d in friend.Devices.Where(d => d.MayBeAutoAdmitted)) Trusted.AddDevice(d.PublicKey, friend.Contact.Name);
        }
        else
        {
            foreach (var d in friend.Devices) Trusted.Remove(d.PublicKey);
            // Trusted under their account from a device that is not in my friend list (it knocked before we were friends).
            if (friend.JayAccount is { } account)
                foreach (var t in Trusted.All.Where(t => string.Equals(t.JayAccount, account, StringComparison.OrdinalIgnoreCase)))
                    Trusted.Remove(t.PublicKey);
        }
        RefreshTrusted();
    }

    internal void SetAutoAdmit(MetPersonItem person, bool on)
    {
        // Only this device, as the switch says: someone I met is not a friend whose other devices I have checked. And only a
        // key this computer checked itself; turning it off always works.
        if (on && !person.Checked) return;
        if (on) Trusted.AddDevice(person.PublicKey, person.Name);
        else Trusted.Remove(person.PublicKey);
        RefreshTrusted();
        // The host's member list carries the same fact for the room console.
        for (var i = 0; i < Members.Count; i++)
            if (Members[i].PublicKey.AsSpan().SequenceEqual(person.PublicKey) && Members[i].Trusted != on)
                Members[i] = Members[i] with { Trusted = on };
    }

    /// <summary>
    /// Asks someone I have met to be friends. Their friend code is their device's public key, which the room already
    /// showed me, so there is nothing to paste; the emoji check still follows. A code I pasted in the add panel stays there.
    /// </summary>
    internal Task AddMetPersonAsync(MetPersonItem person) => AddContactKeyAsync(person.PublicKey, fromInput: false);

    /// <summary>I looked at a friend's new device and it is theirs.</summary>
    internal void AcknowledgeDevice(ContactDeviceItem device) => AcknowledgeDevice(device.Device.PublicKey);

    /// <summary>
    /// Confirms a friend's new device. When I let that friend straight in, the confirmed device joins their others on the
    /// list; until now it had to knock.
    /// </summary>
    internal void AcknowledgeDevice(byte[] device)
    {
        var friend = FriendOf(device, null);
        Contacts.AcknowledgeDevice(device);
        if (friend is null || !IsAutoAdmitted(friend)) return;
        Trusted.AddDevice(device, friend.Contact.Name);
        RefreshTrusted();
    }

    /// <summary>The friend this device or Jay account belongs to (a verified contact), if any.</summary>
    internal ContactItem? FriendOf(byte[]? device, string? jayAccount) => ContactList.FirstOrDefault(f =>
        (device is not null && f.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(device)))
        || (jayAccount is not null && string.Equals(f.JayAccount, jayAccount, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// "Let them in from now on" on a knock or a room member: this device, kept with the Jay account it showed so that
    /// removing the person later finds it. For a friend it is also their switch. A friend's account lets no other device in
    /// (see <see cref="AccountMayAutoAdmit"/>); for anyone else it does.
    /// </summary>
    internal void TrustKnocker(byte[] device, string name, string? jayAccount)
    {
        if (FriendOf(device, jayAccount) is { } friend) SetAutoAdmit(friend, true);
        // The account is kept so that removing the friend finds this device. It lets nobody in while they are a friend, and
        // every way of ending the friendship clears it (see ForgetRemovedDevice and ForgetFormerFriends).
        Trusted.Add(device, name, jayAccount);
        RefreshTrusted();
    }

    /// <summary>
    /// A Jay account counts for a knock only for someone who is not my friend: a friend's devices are trusted one by one,
    /// so a friend's new device asks until I confirm it. Runs on the knock thread, so it reads the contact book itself.
    /// </summary>
    internal bool AccountMayAutoAdmit(string? account)
    {
        if (Child.AlwaysAskKnock || !Trusted.IsTrustedJayAccount(account)) return false;
        // Can't tell who is a friend: ask.
        if (Contacts.Unreadable) return false;
        return !IsFriendAccount(account);
    }

    /// <summary>
    /// The Jay account belongs to a verified friend. Called on the knock thread too: <see cref="ContactBook.All"/> reads a
    /// snapshot of the contact files under the book's own lock, and <see cref="Contacts"/> is created safely from any thread.
    /// </summary>
    internal bool IsFriendAccount(string? account) =>
        account is not null && Contacts.All.Any(c => c.Verified && string.Equals(c.JayAccount, account, StringComparison.OrdinalIgnoreCase));

    // ---- what is known about a friend ----

    /// <summary>The friend as a member of the room I am in, when they are in it.</summary>
    internal OnlinePeerItem? PeerOf(ContactItem friend) =>
        OnlinePeers.FirstOrDefault(p => KeyMatches(p.Entry) && friend.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(p.Entry.PublicKey)));

    /// <summary>
    /// A roster entry can be talked to only when its fingerprint is the one of its own key: messages are addressed by
    /// fingerprint, so an entry that pairs someone's key with another fingerprint must never stand for that person.
    /// </summary>
    internal static bool KeyMatches(RosterEntry entry) => MemberIdentity.FingerprintOf(entry.PublicKey) == entry.Fingerprint;

    /// <summary>The newest room this friend invited me to that I can still join.</summary>
    internal FriendRoomItem? RoomOf(ContactItem friend)
    {
        var now = DateTimeOffset.UtcNow;
        return FriendRooms.FirstOrDefault(r => !r.IsExpired(now) && friend.Devices.Any(d => d.PublicKey.AsSpan().SequenceEqual(r.Invite.From.PublicKey)));
    }

    internal FriendState StateOf(ContactItem friend) =>
        friend.Devices.Any(d => d.NewDevice) ? FriendState.NewDevice
        : PeerOf(friend) is not null ? (IsHosting ? FriendState.SameRoom : FriendState.ListedInRoom)
        : RoomOf(friend) is not null ? FriendState.InTheirRoom
        : FriendState.Verified;

    // ---- the conversation with a friend ----

    private static string PersonKey(Contact contact) => contact.JayAccount ?? Convert.ToHexStringLower(contact.PublicKey);

    internal ObservableCollection<FriendLine> ConversationWith(ContactItem friend) => Conversation(friend.Contact, friend.Devices);

    /// <summary>
    /// The conversation with the person <paramref name="contact"/> belongs to. A friend whose Jay account was linked after
    /// we talked had their lines kept under a device key: they move (the same collection, so a page showing it keeps
    /// it) or are put in front of the account's.
    /// </summary>
    private ObservableCollection<FriendLine> Conversation(Contact contact, IEnumerable<Contact>? devices = null)
    {
        var person = PersonKey(contact);
        _conversations.TryGetValue(person, out var lines);
        if (contact.JayAccount is not null)
            foreach (var device in devices ?? [contact])
            {
                var earlierKey = Convert.ToHexStringLower(device.PublicKey);
                if (earlierKey == person || !_conversations.Remove(earlierKey, out var earlier)) continue;
                if (lines is null) lines = earlier;
                else for (var i = 0; i < earlier.Count; i++) lines.Insert(i, earlier[i]);
            }
        lines ??= [];
        _conversations[person] = lines;
        TrimConversation(lines);
        return lines;
    }

    private FriendLine AddFriendLine(Contact friend, FriendLine line)
    {
        var lines = Conversation(friend);
        lines.Add(line);
        TrimConversation(lines);
        return line;
    }

    private void TrimConversation(ObservableCollection<FriendLine> lines)
    {
        while (lines.Count > MaxFriendLines)
        {
            var dropped = lines[0];
            lines.RemoveAt(0);
            // An invite note that scrolled out no longer waits for its receipt.
            if (dropped.Delivery != DeliveryState.None)
                foreach (var key in _inviteLines.Where(p => ReferenceEquals(p.Value, dropped)).Select(p => p.Key).ToList()) _inviteLines.Remove(key);
        }
    }

    /// <summary>The verified friend who owns this device key, if any.</summary>
    private Contact? FriendByKey(ReadOnlySpan<byte> publicKey) => Contacts.Find(publicKey) is { Verified: true } contact ? contact : null;

    /// <summary>I mailed this friend an invite to my room: a note that shows where the mail is.</summary>
    private void NoteInviteSent(ContactItem friend, string mailId)
    {
        var line = AddFriendLine(friend.Contact, new FriendLine(FriendLineKind.Note, () => L.T("people.line.invited"), DateTimeOffset.Now) { Delivery = DeliveryState.Sent });
        _inviteLines[mailId] = line;
    }

    private void NoteInviteDelivered(string mailId)
    {
        if (_inviteLines.Remove(mailId, out var line)) line.Delivery = DeliveryState.Delivered;
    }

    private void NoteInviteHeld(string mailId)
    {
        if (_inviteLines.TryGetValue(mailId, out var line) && line.Delivery == DeliveryState.Sent) line.Delivery = DeliveryState.Held;
    }

    /// <summary>A friend mailed me an invite to their room: a note in the conversation, with [Join] while the invite lasts.</summary>
    private void NoteInvitedBy(MailedInvite invite, string roomKey, string room)
    {
        if (FriendByKey(invite.From.PublicKey) is not { } friend) return;
        if (Conversation(friend).Any(l => l.RoomKey == roomKey)) return;
        AddFriendLine(friend, new FriendLine(FriendLineKind.Note, () => L.F("people.line.invited_me", room), DateTimeOffset.Now) { RoomKey = roomKey });
    }

    /// <summary>
    /// A private message in the room, to or from someone who is a friend: it also belongs to the conversation. Filed by the
    /// device key the message was sealed with (or sent to), never by a name or a fingerprint someone in the room wrote.
    /// </summary>
    private void NoteFriendMessage(byte[] publicKey, string text, DateTimeOffset time, bool mine)
    {
        if (FriendByKey(publicKey) is not { } friend) return;
        AddFriendLine(friend, new FriendLine(mine ? FriendLineKind.Mine : FriendLineKind.Theirs, () => text, time));
    }

    /// <summary>Sends a message to a friend who is in the room with me. False when they are not, or it could not be sent.</summary>
    /// <summary>Tests: stands in for sending a message to a friend.</summary>
    internal Func<ContactItem, string, Task<bool>>? SendToFriendForTest { get; set; }

    internal async Task<bool> SendToFriendAsync(ContactItem friend, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (SendToFriendForTest is { } send) return await send(friend, text);
        if (PeerOf(friend) is not { } peer)
        {
            SetStatus(StatusMessage.Warning("people.chat.needs_room"));
            return false;
        }
        return await SendPrivateToAsync(peer, text);
    }

    /// <summary>Test seam: as if a private message to or from this device had gone through the room.</summary>
    internal void FriendMessageForTest(byte[] publicKey, string text, bool mine) => NoteFriendMessage(publicKey, text, DateTimeOffset.Now, mine);

    /// <summary>Test seam: the receipt for the mail with this id came back.</summary>
    internal void InviteReceiptForTest(string id) => OnInviteReceipt(new SentMail([], id, MailKind.Invite, DateTimeOffset.UtcNow));

    /// <summary>Test seam: the trusted list file (tests keep their own).</summary>
    internal string TrustedFile
    {
        get => _trustedFile ?? Path.Combine(AppSettings.DataDir, "trusted.json");
        set
        {
            _trustedFile = value;
            Volatile.Write(ref _trusted, null);
            RefreshTrusted();
        }
    }

    private string? _trustedFile;
}
