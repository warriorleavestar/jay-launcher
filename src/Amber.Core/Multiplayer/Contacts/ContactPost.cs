using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Multiplayer.Contacts;

/// <summary>A room invite that arrived as sealed mail from a verified contact.</summary>
/// <param name="Invite">The JAY2 invite. It never passed through a chat platform.</param>
/// <param name="HostKey">The host's identity key. Knocking only goes on if the room's host proves this key.</param>
public sealed record MailedInvite(Contact From, JayInvite Invite, byte[] HostKey, string Room, DateTimeOffset? Expires)
{
    public override string ToString() => $"MailedInvite({From.Fingerprint})";
}

/// <summary>Mail I sent that should be answered with a receipt.</summary>
public sealed record SentMail(byte[] To, string Id, string Kind, DateTimeOffset Sent)
{
    public bool Confirmed { get; internal set; }
}

/// <summary>
/// Sends and picks up sealed mail through the room directories (<see cref="SealedMail"/>), and turns what arrives into
/// contact-list steps and invites.
/// <list type="bullet">
/// <item>Every piece is posted to every reachable directory; picking up merges them all. A directory that drops mail
/// only delays it if another one is honest.</item>
/// <item>Invites are answered with a receipt (sealed, so it proves the recipient opened it). An invite without a receipt
/// after <see cref="ReceiptGrace"/> shows up in <see cref="Unconfirmed"/> so the UI can say it may have been held back and
/// offer another way (a direct link or the LAN).</item>
/// <item>Each message id is accepted once; mail older than the directory keeps it, or dated in the future, is ignored.</item>
/// </list>
/// </summary>
public sealed class ContactPost(Func<X25519KeyPair> me, ContactBook book, RoomDirectoryClient directory, string seenPath, Func<DateTimeOffset>? clock = null)
{
    public const string SeenFileName = "mail-seen.json";
    public static readonly TimeSpan ReceiptGrace = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);
    /// <summary>Remembered sent mail (for receipts) is forgotten after this long.</summary>
    public static readonly TimeSpan OutboxLifetime = TimeSpan.FromHours(48);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private readonly List<SentMail> _outbox = [];
    private Dictionary<string, long>? _seen;

    /// <summary>A receipt came in for mail I sent.</summary>
    public event Action<SentMail>? Confirmed;

    public async Task SendAsync(ReadOnlyMemory<byte> to, MailMessage message, CancellationToken ct)
    {
        var blob = SealedMail.Seal(me(), to.Span, message);
        await directory.PostMailAsync(SealedMail.MailboxId(to.Span, SealedMail.Day(_now())), blob, SealedMail.DropHash(message.DropToken), ct);
        if (message.Kind is MailKind.Invite)
            lock (_gate) _outbox.Add(new SentMail(to.ToArray(), message.IdHex, message.Kind, _now()));
    }

    /// <summary>Starts adding a contact from their code and mails the hello.</summary>
    public async Task<bool> AddAsync(byte[] theirs, string myName, CancellationToken ct)
    {
        book.Me = me().PublicKey;
        if (book.StartAdd(book.Me, theirs, myName) is not { } hello) return false;
        await SendAsync(theirs, hello, ct);
        return true;
    }

    public async Task AcceptAsync(byte[] theirs, string myName, CancellationToken ct)
    {
        if (book.Accept(theirs, myName) is not { } reply) return;
        try { await SendAsync(theirs, reply, ct); }
        catch
        {
            // Maybe not sent (the network held it back, or the answer was lost): the request stays there to accept
            // again later, which sends this same reply; if it did arrive, their answer is still taken.
            book.Unaccept(theirs);
            throw;
        }
    }

    /// <summary>
    /// Mails a room invite to a verified contact. The invite should be one the host created bound to this contact's key
    /// (<see cref="InviteBook.Create"/> with boundTo), so even a copy that leaks somehow can't be used by anyone else.
    /// </summary>
    public async Task<string> SendInviteAsync(Contact to, JayInvite invite, ReadOnlyMemory<byte> hostKey, string room, DateTimeOffset? expires, CancellationToken ct)
    {
        if (!to.Verified) throw new InvalidOperationException("Invites go only to verified contacts.");
        var body = new JsonObject
        {
            ["code"] = invite.ToString(),
            ["host"] = Convert.ToHexStringLower(hostKey.Span),
            ["room"] = KnockProtocol.SanitizeName(room),
        };
        if (expires is { } e) body["exp"] = e.ToUnixTimeSeconds();
        var message = MailMessage.Create(MailKind.Invite, to.PublicKey, body, _now());
        await SendAsync(to.PublicKey, message, ct);
        return message.IdHex;
    }

    /// <summary>Invites sent more than <see cref="ReceiptGrace"/> ago with no receipt yet.</summary>
    public IReadOnlyList<SentMail> Unconfirmed()
    {
        var now = _now();
        lock (_gate)
        {
            _outbox.RemoveAll(m => m.Sent < now - OutboxLifetime);
            return [.. _outbox.Where(m => !m.Confirmed && m.Sent + ReceiptGrace <= now)];
        }
    }

    /// <summary>
    /// Picks up my mail from every directory, handles it, and returns the invites that came in. Contact-list changes show
    /// up through <see cref="ContactBook.Changed"/>. Every opened piece is deleted from the directories.
    /// </summary>
    public async Task<IReadOnlyList<MailedInvite>> CheckAsync(string myName, CancellationToken ct)
    {
        var identity = me();
        book.Me = identity.PublicKey;
        var boxes = SealedMail.MailboxesToCheck(identity.PublicKey, _now());
        var items = await directory.FetchMailAsync(boxes, ct);
        var invites = new List<MailedInvite>();
        foreach (var (box, blob) in items)
        {
            if (SealedMail.Open(identity, blob) is not { } opened) continue;
            var (sender, message) = opened;
            // Delete it whether or not it's new: a replayed copy shouldn't sit there either.
            try { await directory.DropMailAsync(box, message.DropToken, ct); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
            if (!FirstTime(message)) continue;
            switch (message.Kind)
            {
                case MailKind.Hello or MailKind.HelloReply or MailKind.HelloNonce:
                    if (book.Receive(sender, message, myName) is { } reply) await TrySendAsync(sender, reply, ct);
                    break;
                case MailKind.Invite:
                    if (ReadInvite(sender, message) is { } invite)
                    {
                        invites.Add(invite);
                        await TrySendAsync(sender, MailMessage.Create(MailKind.Receipt, sender, new JsonObject { ["for"] = message.IdHex }, _now()), ct);
                    }
                    break;
                case MailKind.Receipt:
                    Receipt(sender, message);
                    break;
            }
        }
        return invites;
    }

    private async Task TrySendAsync(byte[] to, MailMessage message, CancellationToken ct)
    {
        try { await SendAsync(to, message, ct); }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
    }

    private MailedInvite? ReadInvite(byte[] sender, MailMessage message)
    {
        // Only verified contacts can put an invite on my screen: mail from anyone else is dropped.
        if (book.Find(sender) is not { Verified: true } contact) return null;
        try
        {
            if (!JayInvite.TryParse(message.Body["code"]?.GetValue<string>(), out var invite)) return null;
            if (MailMessage.Hex(message.Body["host"], X25519KeyPair.Length) is not { } host) return null;
            DateTimeOffset? expires = message.Body["exp"]?.GetValue<long>() is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
            if (expires is { } e && e <= _now()) return null;
            return new MailedInvite(contact, invite, host, KnockProtocol.SanitizeName(message.Body["room"]?.GetValue<string>()), expires);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentOutOfRangeException) { return null; }
    }

    private void Receipt(byte[] sender, MailMessage message)
    {
        string? id;
        try { id = message.Body["for"]?.GetValue<string>(); }
        catch (InvalidOperationException) { return; }
        SentMail? done = null;
        lock (_gate)
        {
            done = _outbox.FirstOrDefault(m => !m.Confirmed && m.Id == id && CryptographicOperations.FixedTimeEquals(m.To, sender));
            if (done is not null) done.Confirmed = true;
        }
        if (done is not null) Confirmed?.Invoke(done);
    }

    /// <summary>Accepts each message id once, and only while the directories would still hold it.</summary>
    private bool FirstTime(MailMessage message)
    {
        var now = _now();
        if (message.Sent < now - RoomDirectoryProtocol.MailTtl || message.Sent > now + ClockSkew) return false;
        lock (_gate)
        {
            var seen = LoadSeen();
            var cutoff = (now - RoomDirectoryProtocol.MailTtl - ClockSkew).ToUnixTimeSeconds();
            foreach (var old in seen.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) seen.Remove(old);
            if (!seen.TryAdd(message.IdHex, message.Sent.ToUnixTimeSeconds())) return false;
            SaveSeen(seen);
            return true;
        }
    }

    private Dictionary<string, long> LoadSeen()
    {
        if (_seen is not null) return _seen;
        _seen = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(seenPath) && JsonNode.Parse(File.ReadAllText(seenPath)) is JsonObject o)
                foreach (var (id, ts) in o)
                    if (id.Length == MailMessage.IdLength * 2 && ts is not null) _seen[id] = ts.GetValue<long>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return _seen;
    }

    private void SaveSeen(Dictionary<string, long> seen)
    {
        try
        {
            var o = new JsonObject();
            foreach (var (id, ts) in seen) o[id] = ts;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(seenPath))!);
            var temp = seenPath + ".tmp";
            File.WriteAllText(temp, o.ToJsonString());
            File.Move(temp, seenPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
