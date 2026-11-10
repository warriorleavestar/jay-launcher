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
    /// Their Jay account (jay1…), when they sent a certificate the account signed for this device key while adding.
    /// A later add-contact request from another device of the same account is recognized as the same person.
    /// </summary>
    public string? JayAccount { get; init; }
}

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

    /// <summary>Their Jay account, from a certificate that checked out for <see cref="PublicKey"/>.</summary>
    public string? JayAccount { get; internal set; }

    /// <summary>
    /// The Jay account belongs to someone already verified in the list: this is a friend on another device. Their request is
    /// accepted without asking, and the emoji can be skipped because the account itself vouches for the device.
    /// </summary>
    public bool KnownFriend { get; internal set; }

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
    private readonly Dictionary<string, PendingContact> _pending = new(StringComparer.Ordinal);

    public event Action? Changed;

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
        lock (_gate) return Load().FirstOrDefault(c => c.Verified && c.JayAccount is not null && string.Equals(c.JayAccount, account, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This device's Jay account certificate, sent in add-contact mail so the other person can recognize me on my other devices.
    /// Null when no Jay account is bound. Set by the owner, like <see cref="Me"/>.
    /// </summary>
    public Identity.DeviceCertificate? MyCertificate { get; set; }

    private void AddMyCertificate(JsonObject body)
    {
        if (MyCertificate is { } cert && cert.DeviceKey.AsSpan().SequenceEqual(Me)) body["jay"] = cert.ToJson();
    }

    private string? KnownAccountLocked(MailMessage message, ReadOnlySpan<byte> sender, out bool knownFriend)
    {
        var account = Identity.DeviceCertificate.VerifiedAccount(message.Body["jay"], sender);
        knownFriend = account is not null && Load().Any(c => c.Verified && c.JayAccount == account);
        return account;
    }

    /// <summary>
    /// Starts adding someone from their contact code: returns the hello to mail them. Adding myself or someone
    /// already verified returns null.
    /// </summary>
    public MailMessage? StartAdd(ReadOnlySpan<byte> me, ReadOnlySpan<byte> theirs, string myName) => Start(me, theirs, myName, relink: false);

    /// <summary>
    /// After restoring a friend list backup on a new device: says hello again to a verified friend's device so they learn
    /// this device's key. Needs my Jay account certificate: the friend's side recognizes the account and accepts without
    /// asking, and both sides confirm without the emoji. Returns null without a certificate or for someone not in the list.
    /// </summary>
    public MailMessage? StartRelink(ReadOnlySpan<byte> me, ReadOnlySpan<byte> theirs, string myName) =>
        MyCertificate is not null && IsVerified(theirs) ? Start(me, theirs, myName, relink: true) : null;

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
        AddMyCertificate(body);
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
                    var account = KnownAccountLocked(message, sender, out var knownFriend);
                    if (pending is null && !knownFriend && _pending.Values.Count(p => !p.Initiator) >= MaxRequests) return null;
                    var asking = new PendingContact(sender.ToArray(), initiator: false, _now())
                    {
                        Name = Name(message),
                        Step = ContactStep.AskingMe,
                        TheirCommit = commit,
                        JayAccount = account,
                        KnownFriend = knownFriend,
                    };
                    _pending[key] = asking;
                    // A friend on another device (their account vouches for it): no need to ask me first.
                    if (crossed || knownFriend) reply = AcceptLocked(asking, myName);
                    break;
                }
                case MailKind.HelloReply when pending is { Initiator: true, Step: ContactStep.WaitingForThem }:
                {
                    if (MailMessage.Hex(message.Body["n"], ContactHandshake.NonceLength) is not { } nonce) return null;
                    pending.Name = Name(message);
                    pending.JayAccount = KnownAccountLocked(message, sender, out var knownFriend);
                    pending.KnownFriend = knownFriend;
                    pending.TheirNonce = nonce;
                    pending.Sas = ContactHandshake.Sas(Me, sender, pending.MyNonce!, nonce);
                    pending.Step = ContactStep.Compare;
                    reply = MailMessage.Create(MailKind.HelloNonce, sender, new JsonObject { ["n"] = Convert.ToHexStringLower(pending.MyNonce!) }, _now());
                    if (pending.KnownFriend) ConfirmLocked(pending);
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
                    if (pending.KnownFriend) ConfirmLocked(pending);
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

    private MailMessage AcceptLocked(PendingContact pending, string myName)
    {
        pending.MyNonce = ContactHandshake.NewNonce();
        pending.Step = ContactStep.WaitingForNonce;
        var body = new JsonObject
        {
            ["name"] = KnockProtocol.SanitizeName(myName),
            ["n"] = Convert.ToHexStringLower(pending.MyNonce),
        };
        AddMyCertificate(body);
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

    /// <summary>
    /// Saves a finished exchange as a verified contact. A known friend's new device is confirmed this way without the emoji:
    /// the Jay account (verified earlier) signed the device key, which says more than four emoji would.
    /// </summary>
    private Contact ConfirmLocked(PendingContact pending)
    {
        _pending.Remove(Convert.ToHexStringLower(pending.PublicKey));
        var list = Load();
        var old = list.FirstOrDefault(c => c.PublicKey.AsSpan().SequenceEqual(pending.PublicKey));
        list.RemoveAll(c => c.PublicKey.AsSpan().SequenceEqual(pending.PublicKey));
        // A known friend keeps the name I gave them, wherever the device came from.
        var name = pending.KnownFriend && list.FirstOrDefault(c => c.Verified && c.JayAccount == pending.JayAccount) is { } friend ? friend.Name
            : old?.Name is { Length: > 0 } kept && pending.KnownFriend ? kept : pending.Name;
        var contact = new Contact(pending.PublicKey, name, Verified: true, old?.Added ?? _now()) { JayAccount = pending.JayAccount ?? old?.JayAccount };
        list.Add(contact);
        Save(list);
        return contact;
    }

    /// <summary>
    /// Adds contacts from a friend list backup made by my own Jay account. Ones already in the list stay as they are.
    /// Returns how many were added.
    /// </summary>
    public int Import(IEnumerable<Contact> contacts)
    {
        var added = 0;
        lock (_gate)
        {
            var list = Load();
            foreach (var c in contacts)
            {
                if (c.PublicKey.Length != X25519KeyPair.Length || c.PublicKey.AsSpan().SequenceEqual(Me) || list.Any(x => x.PublicKey.AsSpan().SequenceEqual(c.PublicKey))) continue;
                list.Add(c with { Name = KnockProtocol.SanitizeName(c.Name) });
                added++;
            }
            if (added > 0) Save(list);
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

    public void Remove(ReadOnlySpan<byte> theirs)
    {
        lock (_gate)
        {
            var list = Load();
            var key = theirs.ToArray();
            if (list.RemoveAll(c => c.PublicKey.AsSpan().SequenceEqual(key)) == 0) return;
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

    private List<Contact> Load()
    {
        if (_contacts is not null) return _contacts;
        _contacts = [];
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array) return _contacts;
            foreach (var item in array.OfType<JsonObject>())
            {
                if (MailMessage.Hex(item["pub"], X25519KeyPair.Length) is not { } pub) continue;
                var added = item["added"]?.GetValue<long>() is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.UnixEpoch;
                var jay = item["jay"]?.GetValue<string>();
                _contacts.Add(new Contact(pub, KnockProtocol.SanitizeName(item["name"]?.GetValue<string>()), item["verified"]?.GetValue<bool>() == true, added)
                {
                    JayAccount = Identity.JayAddress.TryDecode(jay, out var account) ? Identity.JayAddress.Encode(account) : null,
                });
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            _contacts.Clear();
        }
        return _contacts;
    }

    private void Save(List<Contact> list)
    {
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
            array.Add((JsonNode)item);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        MemberIdentity.WritePrivate(temp, System.Text.Encoding.UTF8.GetBytes(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        File.Move(temp, path, true);
    }
}
