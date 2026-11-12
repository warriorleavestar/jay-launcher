using System.Collections.ObjectModel;
using Amber.App.Services;
using Amber.Core.Activity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Public;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amber.App.ViewModels;

/// <summary>One of the four emoji, with its name for screen readers.</summary>
public sealed record EmojiItem(string Emoji, string Name);

/// <summary>A verified contact in the list.</summary>
/// <remarks>
/// One person: every verified device that shares a Jay account is listed under one name; a contact without a Jay account
/// is a person with one device. <see cref="Contact"/> is the newest device (the name comes from it).
/// </remarks>
public sealed class ContactItem(Contact contact)
{
    public Contact Contact { get; } = contact;
    public string Name => Contact.Name.Length > 0 ? Contact.Name : L.T("multiplayer.contacts.no_name");
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    public string Fingerprint => Contact.Fingerprint;
    public string Label => Name + " · " + Fingerprint;

    /// <summary>All of this person's devices (at least <see cref="Contact"/>).</summary>
    public IReadOnlyList<Contact> Devices { get; init; } = [contact];
    public IReadOnlyList<ContactDeviceItem> DeviceItems => [.. Devices.Select(d => new ContactDeviceItem(d, this))];
    public bool HasSeveralDevices => Devices.Count > 1;
    public bool HasOneDevice => Devices.Count <= 1;
    public string DevicesText => L.F("multiplayer.contacts.devices", Devices.Count);

    public string? JayAccount => Contact.JayAccount;
    public bool HasJay => JayAccount is not null;
    public string JayText => MultiplayerViewModel.JayLine(JayAccount) ?? "";
}

/// <summary>One device of a person in the contact list (its safety code), with [Remove this device].</summary>
public sealed class ContactDeviceItem(Contact device, ContactItem person)
{
    public Contact Device { get; } = device;
    public ContactItem Person { get; } = person;

    /// <summary>Their account vouched for this device and I have not looked at it yet.</summary>
    public bool IsNew => Device.NewDevice;
    public string Fingerprint => (Device.DeviceName.Length > 0 ? Device.DeviceName + " · " : "") + Device.Fingerprint
        + (Device.NewDevice ? " · " + L.T("multiplayer.contacts.new_device_tag") : "");
}

/// <summary>An add-contact exchange under way: waiting, asking me, or the emoji to compare.</summary>
public sealed class ContactRequestItem(PendingContact pending)
{
    public PendingContact Pending { get; } = pending;
    public ContactStep Step { get; } = pending.Step;
    public string Who => (Pending.Name.Length > 0 ? Pending.Name + " · " + Pending.Fingerprint : Pending.Fingerprint)
        + (MultiplayerViewModel.JayLine(Pending.JayAccount) is { } jay ? " · " + jay : "");

    public string Title => Pending.KnownFriend ? L.F("multiplayer.contacts.step.known", Who) : Step switch
    {
        ContactStep.WaitingForThem => L.F("multiplayer.contacts.step.waiting_them", Who),
        ContactStep.AskingMe => L.F("multiplayer.contacts.step.asking_me", Who),
        ContactStep.WaitingForNonce => L.F("multiplayer.contacts.step.waiting_nonce", Who),
        _ => L.F("multiplayer.contacts.step.compare", Who),
    };

    public bool IsAskingMe => Step == ContactStep.AskingMe;
    public bool IsComparing => Step == ContactStep.Compare && Pending.Sas is not null;
    public bool CanCancel => Step is ContactStep.WaitingForThem or ContactStep.WaitingForNonce;

    public IReadOnlyList<EmojiItem> Emoji => Pending.Sas is { } sas ? [.. sas.Select(i => new EmojiItem(ContactHandshake.Emoji[i], MultiplayerViewModel.EmojiName(i)))] : [];

    /// <summary>The four names in a row, read out for the whole card.</summary>
    public string EmojiSpoken => string.Join(", ", Emoji.Select(e => e.Name));
}

/// <summary>
/// Contacts (docs/security/zero-leak-codes.md): add someone from their contact code (JAYC1), compare four emoji over voice
/// or in person, then send them room invites as sealed mail through the public network's directories. Nothing that grants
/// entry passes through a chat app. Mail is picked up every 30 seconds while the public network is connected.
/// </summary>
public sealed partial class MultiplayerViewModel
{
    public static readonly TimeSpan MailInterval = TimeSpan.FromSeconds(30);
    /// <summary>How long an invite mailed to a contact stays usable.</summary>
    public static readonly TimeSpan ContactInviteLifetime = TimeSpan.FromMinutes(30);

    private ContactBook? _contacts;
    private ContactPost? _post;
    private IPublicNetwork? _postNetwork;
    private DirectoryKeyMemory? _directoryKeys;
    private DispatcherTimer? _mailTimer;
    private bool _checkingMail;
    private readonly Dictionary<string, MailedInvite> _mailedInvites = [];
    private readonly Dictionary<string, string> _sentInvites = [];
    private readonly HashSet<string> _heldBackShown = [];

    /// <summary>Where contacts.json and mail-seen.json live. Tests point it at a temporary folder.</summary>
    internal string ContactsDir
    {
        get => _contactsDir;
        set
        {
            _contactsDir = value;
            _contacts = null;
            _post = null;
            RefreshContacts();
        }
    }

    private string _contactsDir = AppSettings.DataDir;

    internal ContactBook Contacts => _contacts ??= CreateContacts();

    private ContactBook CreateContacts()
    {
        var book = new ContactBook(Path.Combine(ContactsDir, ContactBook.FileName)) { Me = Identity.PublicKey };
        book.Changed += () => Dispatcher.UIThread.Post(RefreshContacts);
        return book;
    }

    /// <summary>Directory keys seen first, for networks that pin none (trust on first use).</summary>
    private DirectoryKeyMemory DirectoryKeys => _directoryKeys ??= new DirectoryKeyMemory(Path.Combine(AppSettings.DataDir, DirectoryKeyMemory.FileName));

    /// <summary>Every directory call goes through here so first-use pinning applies everywhere.</summary>
    internal RoomDirectoryClient DirectoryClient(IPublicNetwork network) => new(network) { KeyMemory = DirectoryKeys };

    /// <summary>The mail service on the network that is connected now (null when none is).</summary>
    private ContactPost? Post
    {
        get
        {
            if (_public is not { } network) return null;
            // Add-contact mail carries this device's Jay account certificate, so friends recognize me on my other devices.
            Contacts.MyCertificate = JayCertificate;
            if (_post is null || !ReferenceEquals(_postNetwork, network))
            {
                _postNetwork = network;
                _post = new ContactPost(() => Identity.KeyPair, Contacts, DirectoryClient(network), Path.Combine(ContactsDir, ContactPost.SeenFileName));
                _post.Confirmed += sent => Dispatcher.UIThread.Post(() => OnInviteReceipt(sent));
            }
            return _post;
        }
    }

    public ObservableCollection<ContactItem> ContactList { get; } = [];
    public ObservableCollection<ContactRequestItem> ContactRequests { get; } = [];
    public bool HasContacts => ContactList.Count > 0;
    public bool HasContactRequests => ContactRequests.Count > 0;

    /// <summary>The room's [Invite a contact]: hosting, with a contact to invite, public networks allowed.</summary>
    public bool CanInviteContacts => IsHosting && HasContacts && ShowPublicNetworks;

    /// <summary>My contact code (my multiplayer identity's public key). Safe to post anywhere: it grants nothing.</summary>
    public string MyContactCode => ContactCode.Format(Identity.PublicKey);

    [ObservableProperty] public partial string ContactCodeInput { get; set; } = "";

    /// <summary>"Invite may have been held back" banner in the room (empty: hidden).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHeldBack))] public partial string HeldBackText { get; set; } = "";
    public bool HasHeldBack => HeldBackText.Length > 0;

    /// <summary>Localized name of emoji <paramref name="index"/>; English when the language has no list.</summary>
    internal static string EmojiName(int index)
    {
        var names = L.T("multiplayer.contacts.emoji_names").Split('|');
        return names.Length == ContactHandshake.Emoji.Length ? names[index].Trim() : ContactHandshake.EmojiNames[index];
    }

    private void InitContacts()
    {
        ContactList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasContacts));
            OnPropertyChanged(nameof(CanInviteContacts));
        };
        ContactRequests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasContactRequests));
        _service.Requests.Resolved += handle =>
        {
            if (handle.ResolvedBy == RequestSource.Program || handle.Spec.Kind is not ("contact" or "contact-compare" or "contact-invite" or "contact-new-device")) return;
            Dispatcher.UIThread.Post(() => AnswerContactRequest(handle));
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsHosting) or nameof(ShowPublicNetworks)) OnPropertyChanged(nameof(CanInviteContacts));
            if (e.PropertyName == nameof(IsPublicConnected)) OnPublicConnectionChanged();
        };
        RefreshContacts();
    }

    /// <summary>Rebuilds both lists from the book and keeps the activity centre's requests in step with it.</summary>
    internal void RefreshContacts()
    {
        var book = Contacts;
        ContactList.Clear();
        // One row per person: devices sharing a Jay account are grouped, the newest one names the person.
        var people = book.All.Where(c => c.Verified)
            .GroupBy(c => c.JayAccount ?? Convert.ToHexStringLower(c.PublicKey))
            .Select(g => g.OrderByDescending(c => c.Added).ToList())
            .Select(devices => new ContactItem(devices[0]) { Devices = devices });
        foreach (var person in people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) ContactList.Add(person);
        DropRoomsOfRemovedFriends();
        ContactRequests.Clear();
        var pending = book.Pending;
        foreach (var p in pending.OrderByDescending(p => p.Step == ContactStep.Compare).ThenByDescending(p => p.Step == ContactStep.AskingMe))
            ContactRequests.Add(new ContactRequestItem(p));

        // Someone asks to add me, or the emoji are ready: one request each in the activity centre.
        var wanted = new HashSet<string>();
        foreach (var p in pending)
        {
            var hex = Convert.ToHexStringLower(p.PublicKey);
            var who = p.Name.Length > 0 ? p.Name : p.Fingerprint;
            if (p.Step == ContactStep.AskingMe)
            {
                wanted.Add("contact:" + hex);
                _service.Requests.Post(new RequestSpec("contact", L.F("multiplayer.contacts.request.title", who),
                    L.F("multiplayer.contacts.request.body", p.Fingerprint),
                    [new("ignore", L.T("multiplayer.contacts.ignore"), IsDefault: true), new("accept", L.T("multiplayer.contacts.accept"))],
                    DedupeKey: "contact:" + hex));
            }
            else if (p.Step == ContactStep.Compare)
            {
                wanted.Add("contact-compare:" + hex);
                _service.Requests.Post(new RequestSpec("contact-compare", L.F("multiplayer.contacts.compare.request", who),
                    L.T("multiplayer.contacts.compare.request_body"), [new("open", L.T("multiplayer.contacts.compare.open"), IsDefault: true)],
                    DedupeKey: "contact-compare:" + hex));
            }
        }
        // Privacy plan item 7: a friend's new device comes in on their account's signature, but never silently. The notice
        // stays (in the request bar, and in the contact file across restarts) until I say it's fine or remove the device.
        foreach (var d in book.All.Where(c => c.Verified && c.NewDevice))
        {
            var dedupe = "contact-new-device:" + Convert.ToHexStringLower(d.PublicKey);
            wanted.Add(dedupe);
            var who = new ContactItem(d).Name;
            var device = d.DeviceName.Length > 0 ? d.DeviceName : L.T("multiplayer.contacts.new_device_unnamed");
            _service.Requests.Post(new RequestSpec("contact-new-device", L.F("multiplayer.contacts.new_device", who),
                L.F("multiplayer.contacts.new_device_body", device, d.Fingerprint),
                [new("ok", L.T("multiplayer.contacts.new_device_ok"), IsDefault: true), new("remove", L.T("multiplayer.contacts.new_device_remove"), IsDanger: true)],
                IsSafety: true, DedupeKey: dedupe));
        }
        foreach (var r in _service.Requests.Pending)
            if (r.Spec.Kind is "contact" or "contact-compare" or "contact-new-device" && r.Spec.DedupeKey is { } key && !wanted.Contains(key))
                _service.Requests.Withdraw(key);
        // Both lists are whole again: the Friends page rebuilds its rows once, not once per row added.
        FriendsRebuilt?.Invoke();
        RefreshMetPeople();
    }

    [RelayCommand]
    private async Task CopyContactCodeAsync()
    {
        if (await SecretClipboard.CopyAsync(MyContactCode)) SetStatus(StatusMessage.Success("multiplayer.contacts.code_copied"));
    }

    /// <summary>Adds someone from their contact code: mails them a hello; the emoji come once they accept.</summary>
    [RelayCommand]
    private async Task AddContactAsync()
    {
        var text = ContactCodeInput.Trim();
        if (!ContactCode.TryParse(text, out var key))
        {
            if (RouteMisplacedCode(text, PastedCodeKind.ContactCode, () => ContactCodeInput = "")) return;
            SetStatus(StatusMessage.Warning("multiplayer.contacts.bad_code"));
            return;
        }
        if (key.AsSpan().SequenceEqual(Identity.PublicKey))
        {
            SetStatus(StatusMessage.Warning("multiplayer.contacts.is_me"));
            return;
        }
        if (Contacts.IsVerified(key))
        {
            SetStatus(StatusMessage.Info("multiplayer.contacts.already"));
            return;
        }
        if (Post is not { } post)
        {
            SetStatus(StatusMessage.Warning("multiplayer.contacts.needs_public"));
            return;
        }
        try
        {
            if (await post.AddAsync(key, ChatName, default))
            {
                ContactCodeInput = "";
                SetStatus(StatusMessage.Success("multiplayer.contacts.hello_sent"));
            }
        }
        catch (Exception e) when (IsMailError(e))
        {
            Contacts.Dismiss(key);
            SetStatus(StatusMessage.Failure("multiplayer.contacts.mail_failed", e, ErrorText.Of(e)));
        }
    }

    [RelayCommand]
    private async Task AcceptContactAsync(ContactRequestItem? item)
    {
        if (item is null) return;
        if (Post is not { } post)
        {
            SetStatus(StatusMessage.Warning("multiplayer.contacts.needs_public"));
            return;
        }
        try { await post.AcceptAsync(item.Pending.PublicKey, ChatName, default); }
        catch (Exception e) when (IsMailError(e))
        {
            SetStatus(StatusMessage.Failure("multiplayer.contacts.mail_failed", e, ErrorText.Of(e)));
        }
    }

    /// <summary>Ignore a request or cancel my own.</summary>
    [RelayCommand]
    private void DismissContact(ContactRequestItem? item)
    {
        if (item is not null) Contacts.Dismiss(item.Pending.PublicKey);
    }

    /// <summary>The emoji match what they see: saved as a verified contact.</summary>
    [RelayCommand]
    private void ConfirmContact(ContactRequestItem? item)
    {
        if (item is null || Contacts.Confirm(item.Pending.PublicKey) is not { } contact) return;
        SetStatus(StatusMessage.Success("multiplayer.contacts.added", new ContactItem(contact).Name));
    }

    /// <summary>The emoji don't match: someone may be in the middle. Nothing is saved.</summary>
    [RelayCommand]
    private void RejectContact(ContactRequestItem? item)
    {
        if (item is null) return;
        Contacts.Dismiss(item.Pending.PublicKey);
        SetStatus(StatusMessage.Warning("multiplayer.contacts.mismatch"));
    }

    /// <summary>Removes the person: all of their devices.</summary>
    [RelayCommand]
    private void RemoveContact(ContactItem? item)
    {
        if (item is null) return;
        Contacts.RemovePerson(item.Contact.PublicKey);
        SetStatus(StatusMessage.Info("multiplayer.contacts.removed", item.Name));
    }

    /// <summary>Removes one device (say, a lost laptop); the person stays with their other devices.</summary>
    [RelayCommand]
    private void RemoveContactDevice(ContactDeviceItem? item)
    {
        if (item is null) return;
        Contacts.Remove(item.Device.PublicKey);
        SetStatus(StatusMessage.Info("multiplayer.contacts.device_removed", item.Person.Name));
    }

    // ---- mail pick-up ----

    private void OnPublicConnectionChanged()
    {
        if (IsPublicConnected)
        {
            _mailTimer ??= new DispatcherTimer(MailInterval, DispatcherPriority.Background, async (_, _) => await CheckMailAsync());
            _mailTimer.Start();
            _ = CheckMailAsync();
            _ = RelinkFriendsAsync();
        }
        else _mailTimer?.Stop();
    }

    /// <summary>Picks up mail: contact steps update the book; invites go to the activity centre; missing receipts warn.</summary>
    internal async Task CheckMailAsync()
    {
        if (_checkingMail || Post is not { } post) return;
        _checkingMail = true;
        try
        {
            var name = ChatName;
            var invites = await Task.Run(() => post.CheckAsync(name, default));
            ShowMailedInvites(invites);
            ShowHeldBack(post.Unconfirmed());
        }
        catch (Exception e) when (IsMailError(e))
        {
            _diag.Add(L.T("multiplayer.public.diagnostics_category"), e.Message);
        }
        finally { _checkingMail = false; }
    }

    private static bool IsMailError(Exception e) => e is IOException or System.Net.Sockets.SocketException or InvalidDataException
        or OperationCanceledException or TimeoutException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
        or InvalidOperationException;

    /// <summary>Invites from verified contacts: one request each in the activity centre, until they expire.</summary>
    internal void ShowMailedInvites(IEnumerable<MailedInvite> invites)
    {
        foreach (var m in invites)
        {
            var key = "contact-invite:" + Convert.ToHexStringLower(m.Invite.Token);
            _mailedInvites[key] = m;
            var who = new ContactItem(m.From).Name;
            var room = m.Room.Length > 0 ? m.Room : L.T("multiplayer.room.friend_room");
            ListFriendRoom(key, m, who, room);
            NoteInvitedBy(m, key, room);
            _service.Requests.Post(new RequestSpec("contact-invite", L.F("multiplayer.contacts.invite.title", who, room),
                L.F("multiplayer.contacts.invite.body", m.From.Fingerprint),
                [new("ignore", L.T("multiplayer.contacts.ignore"), IsDefault: true), new("join", L.T("multiplayer.contacts.invite.join"))],
                Timeout: m.Expires is { } e ? e - DateTimeOffset.Now : null, DedupeKey: key, TimeoutChoice: "ignore"));
        }
    }

    private void AnswerContactRequest(RequestHandle handle)
    {
        var key = handle.Spec.DedupeKey ?? "";
        switch (handle.Spec.Kind)
        {
            case "contact":
            {
                var item = ContactRequests.FirstOrDefault(r => "contact:" + Convert.ToHexStringLower(r.Pending.PublicKey) == key);
                if (handle.Choice == "accept") _ = AcceptContactCommand.ExecuteAsync(item);
                else DismissContact(item);
                break;
            }
            case "contact-compare":
                // The emoji are on the Friends page, in the add panel.
                OpenAddFriend();
                break;
            case "contact-invite":
                // "Ignore" only closes the request: the room stays under "where to play" until the invite runs out.
                if (handle.Choice == "join" && _mailedInvites.Remove(key, out var invite))
                {
                    UnlistFriendRoom(key);
                    _ = JoinMailedInviteAsync(invite);
                }
                break;
            case "contact-new-device" when key.StartsWith("contact-new-device:", StringComparison.Ordinal):
            {
                var device = Convert.FromHexString(key["contact-new-device:".Length..]);
                if (handle.Choice == "remove")
                {
                    Contacts.Remove(device);
                    SetStatus(StatusMessage.Info("multiplayer.contacts.new_device_removed"));
                }
                else if (handle.Choice == "ok") Contacts.AcknowledgeDevice(device);
                break;
            }
        }
    }

    /// <summary>Joins from a mailed invite. The knock stops before saying anything if the host isn't the one the invite names.</summary>
    internal async Task JoinMailedInviteAsync(MailedInvite invite)
    {
        if (!IsIdle || IsKnocking)
        {
            SetStatus(StatusMessage.Warning("multiplayer.contacts.invite.busy"));
            return;
        }
        Tab = MultiplayerTab.Play;
        Navigate?.Invoke("play");
        await KnockAndJoinAsync(invite.Invite, expectedHost: invite.HostKey);
    }

    // ---- host: invite a contact ----

    /// <summary>
    /// Mails a contact an invite bound to their key: one use, 30 minutes, let in without asking (it can only be them).
    /// </summary>
    [RelayCommand]
    private Task InviteContactAsync(ContactItem? item) => MailInviteAsync(item, 1, ContactInviteLifetime, askFirst: false, quiet: false);

    /// <summary>
    /// Mails one invite per device of a friend, each bound to that device's key: whichever computer they are at, they
    /// can come in, and nobody else can use it.
    /// </summary>
    /// <param name="askFirst">The host approves the knock even for a friend who could be let straight in.</param>
    /// <param name="quiet">No status line per friend (the caller sums up).</param>
    private async Task<bool> MailInviteAsync(ContactItem? item, int uses, TimeSpan lifetime, bool askFirst, bool quiet)
    {
        if (item is null || _lobby is not { } lobby || _credentials is not { } credentials) return false;
        if (Post is not { } post)
        {
            if (!quiet) SetStatus(StatusMessage.Warning("multiplayer.contacts.needs_public"));
            return false;
        }
        lobby.Book.Game = await HostGameAsync();
        if (_lobby != lobby) return false;
        IReadOnlyList<Contact> devices = askFirst ? [.. item.Devices.Where(d => d.Verified)] : item.Devices;
        if (devices.Count == 0) return false;
        var pendings = devices.Select(d => (Device: d, Invite: lobby.Book.Create(uses, lifetime, !askFirst && Child.MayAutoAdmit(d.MayBeAutoAdmitted), credentials.Tunnel, boundTo: d.PublicKey))).ToList();
        RefreshInvites();
        try
        {
            await lobby.UpdateAsync();
            var room = PublishRoomName.Trim().Length > 0 ? PublishRoomName.Trim() : L.F("multiplayer.public.default_name", ChatName);
            foreach (var (device, pending) in pendings)
            {
                var id = await post.SendInviteAsync(device, pending.Invite, Identity.PublicKey, room, DateTimeOffset.UtcNow + lifetime, default);
                // Only the newest device's receipt counts: an older computer that's switched off shouldn't raise "held back".
                if (!ReferenceEquals(device, devices[0])) continue;
                if (!quiet) _sentInvites[id] = item.Name;
                NoteInviteSent(item, id);
            }
            if (!quiet) SetStatus(StatusMessage.Success("multiplayer.contacts.invite.sent", item.Name));
            return true;
        }
        catch (Exception e) when (IsMailError(e) || e is System.ComponentModel.Win32Exception)
        {
            foreach (var (_, pending) in pendings) lobby.Book.Revoke(pending);
            RefreshInvites();
            if (!quiet) SetStatus(StatusMessage.Failure("multiplayer.contacts.mail_failed", e, ErrorText.Of(e)));
            return false;
        }
    }

    private void OnInviteReceipt(SentMail sent)
    {
        NoteInviteDelivered(sent.Id);
        if (!_sentInvites.Remove(sent.Id, out var name)) return;
        if (_heldBackShown.Remove(sent.Id)) HeldBackText = "";
        SetStatus(StatusMessage.Success("multiplayer.contacts.invite.received", name));
    }

    /// <summary>No receipt after a few minutes: the mail may have been held back. Offer the other ways in.</summary>
    internal void ShowHeldBack(IEnumerable<SentMail> unconfirmed)
    {
        foreach (var sent in unconfirmed)
        {
            NoteInviteHeld(sent.Id);
            if (_sentInvites.TryGetValue(sent.Id, out var name) && _heldBackShown.Add(sent.Id))
                HeldBackText = L.F("multiplayer.contacts.held_back", name);
        }
    }

    /// <summary>Test seam: as if a contact invite with this id had been sent to <paramref name="name"/>.</summary>
    internal void SentInviteForTest(string id, string name) => _sentInvites[id] = name;

    /// <summary>Test seam: as if an invite with this mail id had been mailed to a friend in the list.</summary>
    internal void SentInviteForTest(string id, ContactItem friend)
    {
        _sentInvites[id] = friend.Name;
        NoteInviteSent(friend, id);
    }

    [RelayCommand]
    private void DismissHeldBack() => HeldBackText = "";

    /// <summary>The fallback: the invite dialog (direct link, or a code for the LAN).</summary>
    [RelayCommand]
    private void HeldBackOtherWay()
    {
        HeldBackText = "";
        InviteFriendsCommand.Execute(null);
    }
}
