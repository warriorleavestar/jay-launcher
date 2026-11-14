using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>Someone in the contact list. <see cref="Verified"/>: both people compared the four emoji and they matched.</summary>
public sealed record Contact(byte[] PublicKey, string Name, bool Verified, DateTimeOffset Added)
{
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);
    public string Code => ContactCode.Format(PublicKey);

    /// <summary>
    /// Their Jay account (jay1…), proven live for this device key in a knock (<see cref="ContactBook.SeenLive"/>).
    /// Other devices of the same account are recognized as the same person once I've confirmed the account.
    /// </summary>
    public string? JayAccount { get; init; }

    /// <summary>
    /// <see cref="JayAccount"/> was linked to this (emoji-verified) device in a knock and I haven't confirmed it yet. Until
    /// then the account vouches for no other device: the link itself rests only on this device's X25519 key.
    /// </summary>
    public bool AccountPending { get; init; }

    /// <summary>An account I said isn't theirs for this device; a knock proving it again doesn't ask again.</summary>
    public string? RefusedAccount { get; init; }

    /// <summary>The device name and epoch from the certificate this device sent (empty and 0 without one).</summary>
    public string DeviceName { get; init; } = "";
    public int Epoch { get; init; }

    /// <summary>
    /// Added on the strength of their Jay account alone (a known friend's new device, no emoji) and I haven't said it's
    /// fine yet. Kept in the file so the notice survives a restart until I answer it.
    /// </summary>
    public bool NewDevice { get; init; }

    /// <summary>
    /// A room invite mailed to this device may let it straight in. Not while it's a new device I haven't confirmed:
    /// until then the host is asked when it knocks, like for anyone else.
    /// </summary>
    public bool MayBeAutoAdmitted => Verified && !NewDevice;

    /// <summary>This device stands for its account: verified, not a new device awaiting my OK, and the account confirmed.</summary>
    internal bool VouchesForAccount => Verified && !NewDevice && !AccountPending && JayAccount is not null;
}

/// <summary>A device I removed from someone's devices. It has to go through the emoji again to come back.</summary>
public sealed record RemovedDevice(byte[] PublicKey, string? JayAccount);

/// <summary>Where an add-contact exchange stands, as the UI shows it.</summary>
public enum ContactStep
{
    /// <summary>I sent hello and wait for them to accept.</summary>
    WaitingForThem,
    /// <summary>They sent hello; waiting for me to accept or ignore.</summary>
    AskingMe,
    /// <summary>I accepted; waiting for their nonce.</summary>
    WaitingForNonce,
    /// <summary>Both nonces are in: compare <see cref="PendingContact.Sas"/> and confirm or reject.</summary>
    Compare,
}

public sealed class PendingContact
{
    internal PendingContact(byte[] publicKey, bool initiator, DateTimeOffset started)
    {
        PublicKey = publicKey;
        Initiator = initiator;
        Started = started;
    }

    public byte[] PublicKey { get; }
    public string Name { get; internal set; } = "";
    public bool Initiator { get; }
    public DateTimeOffset Started { get; }
    public ContactStep Step { get; internal set; }
    /// <summary>The four emoji to compare (indexes into <see cref="ContactHandshake.Emoji"/>), once <see cref="Step"/> is Compare.</summary>
    public int[]? Sas { get; internal set; }
    public string Fingerprint => MemberIdentity.FingerprintOf(PublicKey);


    internal byte[]? MyNonce;
    internal byte[]? TheirCommit;
    internal byte[]? TheirNonce;
}

/// <summary>
/// The contact list (contacts.json in the data folder: public keys, names, whether verified) and the add-contact
/// exchanges in progress (in memory only; after a restart they start over).
/// <para>
/// Adding goes through <see cref="ContactHandshake"/>: hello (my commitment) → hello-r (their nonce) → hello-n (my nonce),
/// then both people compare emoji. Only a verified contact can be sent room invites by mail, and only mail from a
/// verified contact turns into an invite on screen.
/// </para>
/// </summary>
public sealed class ContactBook(string path, Func<DateTimeOffset>? clock = null)
{
    public const string FileName = "contacts.json";
    /// <summary>An exchange nobody finished in this long is dropped.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);
    /// <summary>At most this many strangers asking at once; more hello mail from unknown keys is ignored.</summary>
    public const int MaxRequests = 20;

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private List<Contact>? _contacts;
    /// <summary>Devices I removed, by key (hex), with their account. Loaded and saved with the contacts.</summary>
    private readonly Dictionary<string, string?> _removed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingContact> _pending = new(StringComparer.Ordinal);

    public event Action? Changed;

    /// <summary>
    /// The file was there but could not be read, so the list is empty for now. Callers must not take that as
    /// "everyone was removed".
    /// </summary>
    public bool Unreadable
    {
        get { lock (_gate) { Load(); return _unreadable; } }
    }

    private bool _unreadable;

    /// <summary>
    /// A known friend's device I hadn't seen before was added without the emoji (their Jay account signed it).
    /// The owner tells the user, so a device they didn't expect doesn't slip in unnoticed.
    /// </summary>
    public event Action<Contact>? DeviceAdded;

    public IReadOnlyList<Contact> All
    {
        get { lock (_gate) return [.. Load()]; }
    }

    public IReadOnlyList<PendingContact> Pending
    {
        get { lock (_gate) { PruneLocked(); return [.. _pending.Values]; } }
    }

    public Contact? Find(ReadOnlySpan<byte> publicKey)
    {
        lock (_gate)
        {
            foreach (var c in Load())
                if (CryptographicOperations.FixedTimeEquals(c.PublicKey, publicKey)) return c;
            return null;
        }
    }

    public bool IsVerified(ReadOnlySpan<byte> publicKey) => Find(publicKey)?.Verified == true;

    /// <summary>A verified contact with this Jay account (any of their devices), or null.</summary>
    public Contact? FindVerifiedByJayAccount(string? account)
    {
        if (account is null) return null;
        lock (_gate) return Load().FirstOrDefault(c => c.VouchesForAccount && string.Equals(c.JayAccount, account, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A live handshake (a knock either way) proved <paramref name="certificate"/> for <paramref name="deviceKey"/>: both
    /// account signatures and a fresh device signature checked out (privacy plan item 7). Add-contact mail never carries
    /// the certificate (it is about 7 KB and mail is 4 KiB), so this is where devices learn which person they belong to.
    /// <list type="bullet">
    /// <item>A verified contact without an account gets this one, waiting for my OK (<see cref="Contact.AccountPending"/>):
    /// the link rests only on the device's X25519 key, so it vouches for nothing until I confirm it. One with this account
    /// keeps the newest epoch and device name. A contact under another account, or one I refused, is left alone.</item>
    /// <item>A device I don't have, certified by a confirmed account of someone verified in the list, joins them as a new device
    /// waiting for my OK (<see cref="Contact.NewDevice"/>, <see cref="DeviceAdded"/>), unless I removed that device or the
    /// certificate is older (lower epoch) than one I've seen from that account.</item>
    /// </list>
    /// Returns the new device, or null.
    /// <para>Precondition: <paramref name="certificate"/> comes from <see cref="Identity.JayProof.Verified"/> for this
    /// handshake. A certificate alone isn't tied to a session and can be replayed, so never pass one from mail, a backup or
    /// any other stored copy.</para>
    /// </summary>
    public Contact? SeenLive(ReadOnlySpan<byte> deviceKey, Identity.DeviceCertificate certificate)
    {
        if (deviceKey.Length != X25519KeyPair.Length || deviceKey.SequenceEqual(Me) || !certificate.Verify(deviceKey)) return null;
        var account = certificate.Account;
        var key = deviceKey.ToArray();
        Contact? added = null;
        var changed = false;
        lock (_gate)
        {
            var list = Load();
            var i = list.FindIndex(c => c.PublicKey.AsSpan().SequenceEqual(key));
            if (i >= 0)
            {
                var known = list[i];
                // Nothing older than the newest certificate seen from this account changes anything, the name included.
                var newest = list.Where(c => c.Verified && c.JayAccount == account).Select(c => c.Epoch).DefaultIfEmpty(0).Max();
                if (!known.Verified || (known.JayAccount is not null && known.JayAccount != account) || certificate.Epoch < newest
                    || known.RefusedAccount == account
                    || (known.JayAccount == account && known.Epoch == certificate.Epoch && known.DeviceName == certificate.DeviceName)) return null;
                list[i] = known with
                {
                    JayAccount = account,
                    AccountPending = known.JayAccount is null || known.AccountPending,
                    DeviceName = certificate.DeviceName,
                    Epoch = certificate.Epoch,
                };
                changed = true;
            }
            else
            {
                var devices = list.Where(c => c.VouchesForAccount && c.JayAccount == account).ToList();
                // The epoch floor counts every certificate seen from this account, including devices still waiting for my OK.
                var newest = list.Where(c => c.Verified && c.JayAccount == account).Max(c => (int?)c.Epoch) ?? 0;
                if (devices.Count == 0 || _removed.ContainsKey(Convert.ToHexStringLower(deviceKey)) || certificate.Epoch < newest) return null;
                // The person keeps the name I gave them, wherever the device came from.
                added = new Contact(key, devices[0].Name, Verified: true, _now())
                {
                    JayAccount = account,
                    DeviceName = certificate.DeviceName,
                    Epoch = certificate.Epoch,
                    NewDevice = true,
                };
                list.Add(added);
                _pending.Remove(Convert.ToHexStringLower(deviceKey));
                changed = true;
            }
            if (changed) Save(list);
        }
        if (changed) Changed?.Invoke();
        if (added is not null) DeviceAdded?.Invoke(added);
        return added;
    }

    /// <summary>
    /// Starts adding someone from their contact code: returns the hello to mail them. Adding myself or someone
    /// already verified returns null.
    /// </summary>
    public MailMessage? StartAdd(ReadOnlySpan<byte> me, ReadOnlySpan<byte> theirs, string myName) => Start(me, theirs, myName, relink: false);

    /// <summary>
    /// After restoring a friend list backup on a new device: says hello again to a verified friend's device so they learn
    /// this device's key. It is an ordinary add-contact request with the emoji on both sides: mail can't carry the
    /// post-quantum account certificate, so the friend checks this device like any other (privacy plan item 7). Returns
    /// null for someone not in the list.
    /// </summary>
    public MailMessage? StartRelink(ReadOnlySpan<byte> me, ReadOnlySpan<byte> theirs, string myName) =>
        IsVerified(theirs) ? Start(me, theirs, myName, relink: true) : null;

    private MailMessage? Start(ReadOnlySpan<byte> me, ReadOnlySpan<byte> theirs, string myName, bool relink)
    {
        if (theirs.Length != X25519KeyPair.Length || theirs.SequenceEqual(me) || (!relink && IsVerified(theirs))) return null;
        var key = Convert.ToHexStringLower(theirs);
        var pending = new PendingContact(theirs.ToArray(), initiator: true, _now()) { Step = ContactStep.WaitingForThem };
        pending.MyNonce = ContactHandshake.NewNonce();
        lock (_gate) _pending[key] = pending;
        Changed?.Invoke();
        var body = new JsonObject
        {
            ["name"] = KnockProtocol.SanitizeName(myName),
            ["c"] = Convert.ToHexStringLower(ContactHandshake.Commit(pending.MyNonce)),
        };
        return MailMessage.Create(MailKind.Hello, theirs, body, _now());
    }

    /// <summary>
    /// Feeds one opened piece of add-contact mail in. Returns the mail to send back, if any.
    /// Mail that doesn't fit the exchange's current step is ignored.
    /// </summary>
    public MailMessage? Receive(ReadOnlySpan<byte> sender, MailMessage message, string myName)
    {
        if (Me.Length != X25519KeyPair.Length || sender.Length != X25519KeyPair.Length) return null;
        var key = Convert.ToHexStringLower(sender);
        MailMessage? reply = null;
        lock (_gate)
        {
            PruneLocked();
            _pending.TryGetValue(key, out var pending);
            switch (message.Kind)
            {
                case MailKind.Hello:
                {
                    if (MailMessage.Hex(message.Body["c"], 32) is not { } commit) return null;
                    // Both added each other at the same time: the smaller key goes on as the initiator and ignores this
                    // hello; the larger one answers it as if it had been asked (it wanted the contact anyway).
                    var crossed = pending is { Initiator: true, Step: ContactStep.WaitingForThem };
                    if (crossed && string.CompareOrdinal(key, Convert.ToHexStringLower(Me)) > 0) return null;
                    var asking = new PendingContact(sender.ToArray(), initiator: false, _now())
                    {
                        Name = Name(message),
                        Step = ContactStep.AskingMe,
                        TheirCommit = commit,
                    };
                    if (pending is null && _pending.Values.Count(p => !p.Initiator) >= MaxRequests) return null;
                    _pending[key] = asking;
                    if (crossed) reply = AcceptLocked(asking, myName);
                    break;
                }
                case MailKind.HelloReply when pending is { Initiator: true, Step: ContactStep.WaitingForThem }:
                {
                    if (MailMessage.Hex(message.Body["n"], ContactHandshake.NonceLength) is not { } nonce) return null;
                    pending.Name = Name(message);
                    pending.TheirNonce = nonce;
                    pending.Sas = ContactHandshake.Sas(Me, sender, pending.MyNonce!, nonce);
                    pending.Step = ContactStep.Compare;
                    reply = MailMessage.Create(MailKind.HelloNonce, sender, new JsonObject { ["n"] = Convert.ToHexStringLower(pending.MyNonce!) }, _now());
                    break;
                }
                case MailKind.HelloNonce when pending is { Initiator: false, Step: ContactStep.WaitingForNonce }:
                {
                    if (MailMessage.Hex(message.Body["n"], ContactHandshake.NonceLength) is not { } nonce) return null;
                    // A nonce that doesn't open the commitment: the other side changed its mind after seeing mine. Drop it.
                    if (!ContactHandshake.Opens(pending.TheirCommit!, nonce))
                    {
                        _pending.Remove(key);
                        break;
                    }
                    pending.TheirNonce = nonce;
                    pending.Sas = ContactHandshake.Sas(sender, Me, nonce, pending.MyNonce!);
                    pending.Step = ContactStep.Compare;
                    break;
                }
                default:
                    return null;
            }
        }
        Changed?.Invoke();
        return reply;
    }

    /// <summary>My public key, for working out the emoji. Set once by the owner (the identity can be reset).</summary>
    public byte[] Me { get; set; } = [];

    /// <summary>I accept someone's request: returns the hello-r to mail them.</summary>
    public MailMessage? Accept(ReadOnlySpan<byte> theirs, string myName)
    {
        MailMessage reply;
        lock (_gate)
        {
            if (!_pending.TryGetValue(Convert.ToHexStringLower(theirs), out var pending) || pending.Step != ContactStep.AskingMe) return null;
            reply = AcceptLocked(pending, myName);
        }
        Changed?.Invoke();
        return reply;
    }

    /// <summary>
    /// The hello-r for a request I accepted could not be mailed: the request goes back to asking me, so accepting again
    /// sends a fresh one (otherwise it would sit waiting for a nonce the other side can never send).
    /// </summary>
    internal void UndoAccept(ReadOnlySpan<byte> theirs)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(Convert.ToHexStringLower(theirs), out var pending) || pending.Initiator || pending.Step != ContactStep.WaitingForNonce) return;
            pending.MyNonce = null;
            pending.Step = ContactStep.AskingMe;
        }
        Changed?.Invoke();
    }

    private MailMessage AcceptLocked(PendingContact pending, string myName)
    {
        pending.MyNonce = ContactHandshake.NewNonce();
        pending.Step = ContactStep.WaitingForNonce;
        var body = new JsonObject
        {
            ["name"] = KnockProtocol.SanitizeName(myName),
            ["n"] = Convert.ToHexStringLower(pending.MyNonce),
        };
        return MailMessage.Create(MailKind.HelloReply, pending.PublicKey, body, _now());
    }

    /// <summary>The emoji matched: the contact is saved as verified.</summary>
    public Contact? Confirm(ReadOnlySpan<byte> theirs)
    {
        Contact contact;
        lock (_gate)
        {
            if (!_pending.TryGetValue(Convert.ToHexStringLower(theirs), out var pending) || pending.Step != ContactStep.Compare) return null;
            contact = ConfirmLocked(pending);
        }
        Changed?.Invoke();
        return contact;
    }

    /// <summary>Saves a finished exchange (the emoji matched) as a verified contact.</summary>
    private Contact ConfirmLocked(PendingContact pending)
    {
        _pending.Remove(Convert.ToHexStringLower(pending.PublicKey));
        var list = Load();
        var old = list.FirstOrDefault(c => c.PublicKey.AsSpan().SequenceEqual(pending.PublicKey));
        list.RemoveAll(c => c.PublicKey.AsSpan().SequenceEqual(pending.PublicKey));
        // A friend I already had (a relink after restoring a backup) keeps the name I gave them.
        var name = old is { Verified: true, Name.Length: > 0 } ? old.Name : pending.Name;
        // The emoji checks this device, not the Jay account it once claimed: an account link still waiting for my OK keeps
        // waiting, one I refused stays refused, and nothing is carried over from a contact that wasn't verified.
        var kept = old is { Verified: true } ? old : null;
        var contact = new Contact(pending.PublicKey, name, Verified: true, old?.Added ?? _now())
        {
            JayAccount = kept?.JayAccount,
            AccountPending = kept is { JayAccount: not null, AccountPending: true },
            RefusedAccount = old?.RefusedAccount,
            DeviceName = kept?.DeviceName ?? "",
            Epoch = kept?.Epoch ?? 0,
        };
        list.Add(contact);
        // Through the emoji again: a device I once removed is back because I checked it myself.
        _removed.Remove(Convert.ToHexStringLower(pending.PublicKey));
        Save(list);
        return contact;
    }

    /// <summary>
    /// Adds contacts from a friend list backup made by my own Jay account. Ones already in the list stay as they are.
    /// Returns how many were added.
    /// </summary>
    public int Import(IEnumerable<Contact> contacts, IEnumerable<RemovedDevice>? removed = null)
    {
        var added = 0;
        lock (_gate)
        {
            var list = Load();
            var removedAny = false;
            // Devices I removed on the old computer stay removed here: a backup can't hand them the no-emoji path back.
            foreach (var r in removed ?? [])
            {
                if (r.PublicKey.Length != X25519KeyPair.Length || list.Any(x => x.Verified && x.PublicKey.AsSpan().SequenceEqual(r.PublicKey))) continue;
                removedAny |= _removed.TryAdd(Convert.ToHexStringLower(r.PublicKey), r.JayAccount);
            }
            foreach (var c in contacts)
            {
                if (c.PublicKey.Length != X25519KeyPair.Length || c.PublicKey.AsSpan().SequenceEqual(Me) || list.Any(x => x.PublicKey.AsSpan().SequenceEqual(c.PublicKey))
                    || _removed.ContainsKey(Convert.ToHexStringLower(c.PublicKey))) continue;
                // An account link I hadn't confirmed, or a new device I hadn't acknowledged, comes back still waiting for me.
                list.Add(c with
                {
                    Name = KnockProtocol.SanitizeName(c.Name),
                    DeviceName = Identity.DeviceCertificate.CleanName(c.DeviceName),
                    AccountPending = c.JayAccount is not null && c.AccountPending,
                    NewDevice = c.Verified && c.JayAccount is not null && c.NewDevice,
                });
                added++;
            }
            if (added > 0 || removedAny) Save(list);
        }
        if (added > 0) Changed?.Invoke();
        return added;
    }

    /// <summary>One person in the list: everyone sharing a Jay account is one person; a contact without one is a person of its own.</summary>
    public IReadOnlyList<Contact> DevicesOf(Contact contact)
    {
        lock (_gate)
            return contact.JayAccount is { } account
                ? [.. Load().Where(c => c.Verified && c.JayAccount == account)]
                : [.. Load().Where(c => c.PublicKey.AsSpan().SequenceEqual(contact.PublicKey))];
    }

    /// <summary>Removes the person: every device of theirs under the same Jay account (or just this one without an account).</summary>
    public void RemovePerson(ReadOnlySpan<byte> anyDevice)
    {
        lock (_gate)
        {
            var list = Load();
            var key = anyDevice.ToArray();
            var account = list.FirstOrDefault(c => c.PublicKey.AsSpan().SequenceEqual(key))?.JayAccount;
            if (list.RemoveAll(c => c.PublicKey.AsSpan().SequenceEqual(key) || (account is not null && c.JayAccount == account)) == 0) return;
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Ignore a request, cancel my own, or reject after the emoji didn't match.</summary>
    public void Dismiss(ReadOnlySpan<byte> theirs)
    {
        lock (_gate)
            if (!_pending.Remove(Convert.ToHexStringLower(theirs))) return;
        Changed?.Invoke();
    }

    public void Rename(ReadOnlySpan<byte> theirs, string name)
    {
        lock (_gate)
        {
            var list = Load();
            var key = theirs.ToArray();
            var i = list.FindIndex(c => c.PublicKey.AsSpan().SequenceEqual(key));
            if (i < 0) return;
            list[i] = list[i] with { Name = KnockProtocol.SanitizeName(name) };
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes one device. A device that came with a Jay account is remembered as removed: its certificate stays valid, so
    /// without this it would be let straight back in as the same person. To come back it has to go through the emoji.
    /// </summary>
    public void Remove(ReadOnlySpan<byte> theirs)
    {
        lock (_gate)
        {
            var list = Load();
            var key = theirs.ToArray();
            var gone = list.FirstOrDefault(c => c.PublicKey.AsSpan().SequenceEqual(key));
            if (gone is null) return;
            list.Remove(gone);
            if (gone.JayAccount is not null) _removed[Convert.ToHexStringLower(key)] = gone.JayAccount;
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>Devices I removed that need the emoji to come back (for the friend list backup).</summary>
    public IReadOnlyList<RemovedDevice> RemovedDevices
    {
        get
        {
            lock (_gate)
            {
                Load();
                return [.. _removed.Select(r => new RemovedDevice(Convert.FromHexString(r.Key), r.Value))];
            }
        }
    }

    /// <summary>
    /// My answer about the Jay account a knock linked to this device: yes, it's theirs (from now on it vouches for their
    /// other devices), or no (the link is dropped, and the same account doesn't ask again for this device).
    /// </summary>
    /// <param name="account">The account the question showed. When given, an answer about a different account does nothing.</param>
    public void ConfirmAccount(ReadOnlySpan<byte> theirs, bool isTheirs, string? account = null)
    {
        lock (_gate)
        {
            var list = Load();
            var key = theirs.ToArray();
            var i = list.FindIndex(c => c.PublicKey.AsSpan().SequenceEqual(key));
            if (i < 0 || !list[i].AccountPending || (account is not null && list[i].JayAccount != account)) return;
            list[i] = isTheirs
                ? list[i] with { AccountPending = false }
                : list[i] with { AccountPending = false, RefusedAccount = list[i].JayAccount, JayAccount = null, DeviceName = "", Epoch = 0 };
            Save(list);
        }
        Changed?.Invoke();
    }

    /// <summary>I've seen the notice about a friend's new device and it's fine: stop asking.</summary>
    public void AcknowledgeDevice(ReadOnlySpan<byte> theirs)
    {
        lock (_gate)
        {
            var list = Load();
            var key = theirs.ToArray();
            var i = list.FindIndex(c => c.PublicKey.AsSpan().SequenceEqual(key));
            if (i < 0 || !list[i].NewDevice) return;
            list[i] = list[i] with { NewDevice = false };
            Save(list);
        }
        Changed?.Invoke();
    }

    private static string Name(MailMessage message)
    {
        try { return KnockProtocol.SanitizeName(message.Body["name"]?.GetValue<string>()); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return KnockProtocol.SanitizeName(null); }
    }

    private void PruneLocked()
    {
        var cutoff = _now() - PendingLifetime;
        foreach (var (key, p) in _pending.Where(p => p.Value.Started < cutoff).ToList()) _pending.Remove(key);
    }

    /// <summary>The last read failed (the file was locked or not readable): nothing is cached, and nothing is written over it.</summary>
    private bool _readFailed;
    /// <summary>The file could not be understood: a copy is kept as .broken before it is first replaced.</summary>
    private bool _broken;

    private List<Contact> Load()
    {
        if (_contacts is not null) return _contacts;
        var contacts = new List<Contact>();
        _removed.Clear();
        _unreadable = false;
        _readFailed = false;
        string? text;
        try { text = ListFile.Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not readable right now: an empty list for this call only. The next call reads again, and Save leaves the file alone.
            _unreadable = true;
            _readFailed = true;
            return contacts;
        }
        JsonArray? array = null;
        if (text is not null)
        {
            try { array = JsonNode.Parse(text) as JsonArray ?? throw new JsonException(); }
            catch (JsonException)
            {
                _broken = true;
                _unreadable = true;
            }
        }
        foreach (var item in array?.OfType<JsonObject>() ?? [])
        {
            // One damaged entry is skipped; the others stay.
            try
            {
                if (MailMessage.Hex(item["pub"], X25519KeyPair.Length) is not { } pub) continue;
                var jay = item["jay"]?.GetValue<string>();
                var account = Identity.JayAddress.TryDecode(jay, out var accountKey) ? Identity.JayAddress.Encode(accountKey) : null;
                // A removed device: not a contact, only remembered so its certificate alone can't bring it back.
                if (item["removed"]?.GetValue<bool>() == true)
                {
                    _removed[Convert.ToHexStringLower(pub)] = account;
                    continue;
                }
                var added = item["added"]?.GetValue<long>() is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.UnixEpoch;
                contacts.Add(new Contact(pub, KnockProtocol.SanitizeName(item["name"]?.GetValue<string>()), item["verified"]?.GetValue<bool>() == true, added)
                {
                    JayAccount = account,
                    DeviceName = Identity.DeviceCertificate.CleanName(item["device"]?.GetValue<string>()),
                    Epoch = Math.Max(0, item["epoch"]?.GetValue<int>() ?? 0),
                    NewDevice = item["new"]?.GetValue<bool>() == true,
                    AccountPending = account is not null && item["acctPending"]?.GetValue<bool>() == true,
                    RefusedAccount = Identity.JayAddress.Canonical(item["refused"]?.GetValue<string>()),
                });
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentOutOfRangeException or JsonException)
            {
                // Keep the file as .broken before the entry is dropped on the next save, and stay careful until then:
                // the skipped entry may be a friend (or a removed device) that Unreadable callers must not miss.
                _broken = true;
                _unreadable = true;
            }
        }
        _contacts = contacts;
        return contacts;
    }

    private void Save(List<Contact> list)
    {
        // Never write over a file that couldn't be read: it may hold every contact.
        if (_readFailed) return;
        if (_broken)
        {
            ListFile.KeepBroken(path);
            _broken = false;
        }
        _contacts = list;
        var array = new JsonArray();
        foreach (var c in list)
        {
            var item = new JsonObject
            {
                ["pub"] = Convert.ToHexStringLower(c.PublicKey),
                ["name"] = c.Name,
                ["verified"] = c.Verified,
                ["added"] = c.Added.ToUnixTimeSeconds(),
            };
            if (c.JayAccount is not null) item["jay"] = c.JayAccount;
            if (c.DeviceName.Length > 0) item["device"] = c.DeviceName;
            if (c.Epoch > 0) item["epoch"] = c.Epoch;
            if (c.NewDevice) item["new"] = true;
            if (c.AccountPending) item["acctPending"] = true;
            if (c.RefusedAccount is not null) item["refused"] = c.RefusedAccount;
            array.Add((JsonNode)item);
        }
        foreach (var (pub, account) in _removed)
        {
            var item = new JsonObject { ["pub"] = pub, ["removed"] = true };
            if (account is not null) item["jay"] = account;
            array.Add((JsonNode)item);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        MemberIdentity.WritePrivate(temp, System.Text.Encoding.UTF8.GetBytes(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        File.Move(temp, path, true);
        _unreadable = false;
    }
}
