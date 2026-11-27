using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amber.Core.Localization;

namespace Amber.Core.Multiplayer.Gate;

/// <summary>
/// A Jay server invite. The admin makes one for one player (<c>jay-server invite</c>, or the host server page).
/// <para>
/// Plain form <c>JAYS2-</c> + Crockford Base32: the gate's <b>domain name</b> and port, a hash of the gate's public key
/// (pins the server), a one-use 128-bit token, and a 4-byte checksum. It names no player: the server keeps which name an
/// invite is for, and tells the player after enrollment. No long-term key: the token only opens the first handshake.
/// Version 2 adds the entry's port rotation rule and seed.
/// </para>
/// <para>
/// A raw IP address never goes into a plain code. A server with no domain gets a sealed code <c>JAYSS1-</c> instead:
/// the same bytes encrypted with a 16-digit passphrase that the admin tells the player another way
/// (<see cref="SealedDirect.SealBlob"/>). Old <c>JAYS1-</c> codes (which carried an IP and the player's name) are refused.
/// </para>
/// <para>
/// Version 3: a server with no domain that publishes a signed record on a public network's directories. The code
/// carries a <see cref="GateLocator"/> (network id, a random id, the record key's hash) instead of a host; Jay looks
/// the address up at enrollment (<see cref="GateRecord.ResolveAsync"/>). No address and no passphrase in the code.
/// </para>
/// </summary>
public sealed record GateInvite(string Host, int Port, byte[] ServerKeyHash, byte[] Token, GateEntryRotation? Rotation = null, GateLocator? Locator = null)
{
    public const string Prefix = "JAYS2-";
    public const string SealedPrefix = "JAYSS1-";
    /// <summary>The old format: refused, the admin makes a new invite.</summary>
    public const string OldPrefix = "JAYS1-";
    public const int KeyHashLength = 16;
    public const int TokenLength = 16;
    private const byte Version = 1;
    private const byte VersionRotating = 2;
    private const byte VersionLocator = 3;
    private const int ChecksumLength = 4;
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string SealInfo = "jay-sealed-server-invite-v1";

    /// <summary>The host is an IP address: only a sealed code may carry it (<see cref="Seal"/>).</summary>
    public bool NeedsSeal => Locator is null && IsIpLiteral(Host);

    /// <summary>For a server with a record (<see cref="Locator"/>): the address and rotation filled in from the record.</summary>
    public static GateInvite Located(GateLocator locator, byte[] serverKeyHash, byte[] token) => new("", 0, serverKeyHash, token, null, locator);

    /// <summary>The plain code. Throws <see cref="InvalidOperationException"/> when the host is an IP address.</summary>
    public string Encode()
    {
        if (NeedsSeal) throw new InvalidOperationException(L.T("core.gate.invite.ip_needs_seal"));
        var bytes = Body();
        return Prefix + Base32Encode([.. bytes, .. Checksum(bytes)]);
    }

    /// <summary>The sealed code and the passphrase that opens it (a new one unless given). Works for any host.</summary>
    public (string Code, string Passphrase) Seal(string? passphrase = null)
    {
        passphrase ??= SealedDirect.NewPassphrase();
        var bytes = Body();
        try { return (SealedDirect.SealBlob(SealedPrefix, SealInfo, bytes, passphrase), passphrase); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>The code to hand out: plain for a domain, sealed (with a passphrase) for an IP address.</summary>
    public (string Code, string? Passphrase) ToCode() => NeedsSeal ? Seal() : (Encode(), null);

    private byte[] Body()
    {
        if (Locator is { } locator)
        {
            if (!IsValidNetworkId(locator.NetworkId) || locator.Id.Length != GateLocator.IdLength || locator.SignerHash.Length != GateLocator.SignerHashLength
                || ServerKeyHash.Length != KeyHashLength || Token.Length != TokenLength)
                throw new FormatException(L.T("core.gate.invite.corrupt"));
            var network = Encoding.ASCII.GetBytes(locator.NetworkId);
            return [VersionLocator, (byte)network.Length, .. network, .. locator.Id, .. locator.SignerHash, .. ServerKeyHash, .. Token];
        }
        Validate(Host, Port, ServerKeyHash, Token);
        var host = Encoding.ASCII.GetBytes(Host);
        var body = new List<byte> { Rotation is null ? Version : VersionRotating, (byte)host.Length };
        body.AddRange(host);
        Span<byte> port = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)Port);
        body.AddRange(port.ToArray());
        body.AddRange(ServerKeyHash);
        body.AddRange(Token);
        if (Rotation is not null)
        {
            Rotation.Rotation.Write(body);
            body.AddRange(Rotation.Seed);
        }
        return body.ToArray();
    }

    /// <summary>The text holds a sealed invite: ask for the passphrase and use <see cref="TryOpen"/>.</summary>
    public static bool IsSealed(string? text) => text?.Contains(SealedPrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Finds a plain invite in some text (words around it, spaces or line breaks inside are fine). Null with a reason when
    /// there is none, it is damaged, it is sealed (needs the passphrase), or it is an old JAYS1 code.
    /// </summary>
    public static GateInvite? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = L.T("core.gate.invite.missing"); return null; }
        var start = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            error = IsSealed(text) ? L.T("core.gate.invite.sealed")
                : text.Contains(OldPrefix, StringComparison.OrdinalIgnoreCase) ? L.T("core.gate.invite.old")
                : L.T("core.gate.invite.missing");
            return null;
        }
        var chars = new StringBuilder();
        foreach (var c in text.AsSpan(start + Prefix.Length))
        {
            if (char.IsWhiteSpace(c) || c == '-') continue;
            if (!char.IsAsciiLetterOrDigit(c)) break;
            chars.Append(c);
        }
        // Words in Latin letters after the code would otherwise run into it: stop at the length the code itself gives.
        if (ExpectedCharCount(chars) is { } expected && chars.Length > expected) chars.Length = expected;
        var raw = Base32Decode(chars.ToString());
        if (raw is null || raw.Length < 1 + 1 + KeyHashLength + TokenLength + ChecksumLength)
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
        var bytes = raw.AsSpan(0, raw.Length - ChecksumLength);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(bytes), raw.AsSpan(raw.Length - ChecksumLength)))
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
        var invite = Read(bytes, out error);
        if (invite is { NeedsSeal: true })
        {
            // Jay never makes this; a hand-made plain code with an IP in it is refused like any other bad code.
            error = L.T("core.gate.invite.ip_needs_seal");
            return null;
        }
        return invite;
    }

    /// <summary>
    /// How many Base32 characters a plain code has, read from its first two bytes (version and host or network-id length).
    /// Null for an unknown version or too short a text: then the text is decoded as it is and reported as it would be.
    /// </summary>
    private static int? ExpectedCharCount(StringBuilder chars)
    {
        if (chars.Length < 4) return null;
        var bits = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = char.ToUpperInvariant(chars[i]) switch { 'I' or 'L' => '1', 'O' => '0', var x => x };
            var v = Crockford.IndexOf(c);
            if (v < 0) return null;
            bits = (bits << 5) | v;
        }
        var version = (byte)(bits >> 12);
        var length = (bits >> 4) & 0xFF;
        int? body = version switch
        {
            Version => 1 + 1 + length + 2 + KeyHashLength + TokenLength,
            VersionRotating => 1 + 1 + length + 2 + KeyHashLength + TokenLength + GateRotation.EncodedLength + GateRotation.SeedLength,
            VersionLocator => 1 + 1 + length + GateLocator.IdLength + GateLocator.SignerHashLength + KeyHashLength + TokenLength,
            _ => null,
        };
        return body is { } b ? ((b + ChecksumLength) * 8 + 4) / 5 : null;
    }

    /// <summary>Opens a sealed invite with its passphrase. Null with a reason for the person (wrong passphrase, damaged).</summary>
    public static GateInvite? TryOpen(string? text, string? passphrase, out string? error)
    {
        error = null;
        if (!IsSealed(text)) return TryParse(text, out error);
        byte[] bytes;
        try
        {
            bytes = SealedDirect.OpenBlob(text, SealedPrefix, SealInfo, passphrase);
        }
        catch (InvalidDataException e)
        {
            error = e.Message;
            return null;
        }
        try { return Read(bytes, out error); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static GateInvite? Read(ReadOnlySpan<byte> bytes, out string? error)
    {
        error = null;
        if (bytes.Length == 0 || bytes[0] is not (Version or VersionRotating or VersionLocator))
        {
            error = L.T("core.gate.invite.version");
            return null;
        }
        try
        {
            var p = 1;
            if (bytes[0] == VersionLocator)
            {
                int networkLength = bytes[p++];
                var network = Encoding.ASCII.GetString(bytes.Slice(p, networkLength)); p += networkLength;
                var id = bytes.Slice(p, GateLocator.IdLength).ToArray(); p += GateLocator.IdLength;
                var signer = bytes.Slice(p, GateLocator.SignerHashLength).ToArray(); p += GateLocator.SignerHashLength;
                var serverKey = bytes.Slice(p, KeyHashLength).ToArray(); p += KeyHashLength;
                var secret = bytes.Slice(p, TokenLength).ToArray(); p += TokenLength;
                if (p != bytes.Length || !IsValidNetworkId(network)) throw new FormatException();
                return Located(new GateLocator(network, id, signer), serverKey, secret);
            }
            int hostLength = bytes[p++];
            var host = Encoding.ASCII.GetString(bytes.Slice(p, hostLength)); p += hostLength;
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(p, 2)); p += 2;
            var keyHash = bytes.Slice(p, KeyHashLength).ToArray(); p += KeyHashLength;
            var token = bytes.Slice(p, TokenLength).ToArray(); p += TokenLength;
            GateEntryRotation? rotation = null;
            if (bytes[0] == VersionRotating)
            {
                var rule = GateRotation.Read(bytes[p..]) ?? throw new FormatException();
                p += GateRotation.EncodedLength;
                rotation = new GateEntryRotation(rule, bytes.Slice(p, GateRotation.SeedLength).ToArray());
                p += GateRotation.SeedLength;
            }
            if (p != bytes.Length) throw new FormatException();
            Validate(host, port, keyHash, token);
            return new GateInvite(host, port, keyHash, token, rotation);
        }
        // A length byte that points past the end makes bytes[p++] throw IndexOutOfRangeException: a crafted code is just corrupt.
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            error = L.T("core.gate.invite.corrupt");
            return null;
        }
    }

    /// <summary>A public network id as <see cref="Public.NetworkString.IsValidId"/> allows (or "jay").</summary>
    private static bool IsValidNetworkId(string id) => Public.NetworkString.IsValidId(id);

    /// <summary>An IPv4 or IPv6 address rather than a name.</summary>
    public static bool IsIpLiteral(string? host) => host is not null && System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>Minecraft 用户名：3–16 个字母、数字或下划线。离线账号也按这个规则。</summary>
    public static bool IsValidUsername(string? name) =>
        name is { Length: >= 3 and <= 16 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>主机名或 IP：只允许字母、数字、点、横杠和冒号（IPv6），最长 253。</summary>
    public static bool IsValidHost(string? host) =>
        host is { Length: >= 1 and <= 253 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':')
        && !host.StartsWith('-') && !host.StartsWith('.');

    private static void Validate(string host, int port, byte[] keyHash, byte[] token)
    {
        if (!IsValidHost(host)) throw new FormatException(L.T("core.gate.invite.badhost"));
        if (port is < 1 or > 65535) throw new FormatException(L.T("core.gate.invite.badport"));
        if (keyHash.Length != KeyHashLength || token.Length != TokenLength) throw new FormatException(L.T("core.gate.invite.corrupt"));
    }

    private static byte[] Checksum(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData([.. "jay-invite-v2"u8, .. bytes], digest);
        return digest[..ChecksumLength].ToArray();
    }

    internal static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Crockford[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) text.Append(Crockford[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>手抄容错：I、L 当 1，O 当 0，不分大小写。多出来的位必须是 0，否则当作被改过。</summary>
    internal static byte[]? Base32Decode(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            var c = char.ToUpperInvariant(raw) switch { 'I' or 'L' => '1', 'O' => '0', var x => x };
            var v = Crockford.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        if (bits >= 5 || buffer != 0) return null;
        return [.. output];
    }

    /// <summary>For logs: no token, no address.</summary>
    public override string ToString() => Locator is { } l ? $"{Prefix}... (record on {l.NetworkId})" : $"{(NeedsSeal ? SealedPrefix : Prefix)}...";
}
