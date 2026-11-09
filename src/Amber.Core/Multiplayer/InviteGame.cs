using System.Numerics;
using System.Text;

namespace Amber.Core.Multiplayer;

/// <summary>
/// What game the host is playing, carried in an invite (invite v3): Minecraft version, mod loader, how many mods and a
/// short fingerprint of the mod list (<see cref="ModManifest.Fingerprint"/>). The joiner uses it to pick the closest
/// instance before knocking. It is only a hint: the real mod list arrives over the room's encrypted channel after the
/// host approves, and nothing here is trusted for safety decisions.
/// </summary>
/// <remarks>
/// Written after the invite's check symbol as <c>-G&lt;payload&gt;&lt;check&gt;</c> in Crockford Base32, so older Jay
/// versions (which accept any non-alphanumeric text after the check symbol) still read the rest of the code.
/// </remarks>
public sealed record InviteGame(string McVersion, string? Loader, int ModCount, byte[] ModsFingerprint)
{
    public const int FingerprintLength = 6;
    public const int MaxVersionBytes = 24;
    private const byte Format = 1;

    private static readonly string?[] Loaders = [null, "fabric", "forge", "neoforge", "quilt", "optifine"];

    public bool Equals(InviteGame? other) => other is not null && McVersion == other.McVersion && Loader == other.Loader
        && ModCount == other.ModCount && ModsFingerprint.AsSpan().SequenceEqual(other.ModsFingerprint);

    public override int GetHashCode() => HashCode.Combine(McVersion, Loader, ModCount);

    /// <summary>The bytes behind the segment: format, loader, mod count, fingerprint, version.</summary>
    internal byte[] ToBytes()
    {
        var version = Encoding.UTF8.GetBytes(McVersion);
        if (version.Length > MaxVersionBytes) version = version[..MaxVersionBytes];
        var loader = Array.IndexOf(Loaders, Loader?.ToLowerInvariant());
        var bytes = new byte[5 + FingerprintLength + version.Length];
        bytes[0] = Format;
        bytes[1] = (byte)(loader < 0 ? 0 : loader);
        var count = Math.Clamp(ModCount, 0, ushort.MaxValue);
        bytes[2] = (byte)(count >> 8);
        bytes[3] = (byte)count;
        ModsFingerprint.AsSpan(0, FingerprintLength).CopyTo(bytes.AsSpan(4));
        bytes[4 + FingerprintLength] = (byte)version.Length;
        version.CopyTo(bytes, 5 + FingerprintLength);
        return bytes;
    }

    internal static InviteGame? FromBytes(byte[] bytes)
    {
        if (bytes.Length < 5 + FingerprintLength || bytes[0] != Format || bytes[1] >= Loaders.Length) return null;
        var length = bytes[4 + FingerprintLength];
        if (length > MaxVersionBytes || bytes.Length != 5 + FingerprintLength + length) return null;
        string version;
        try
        {
            version = new UTF8Encoding(false, true).GetString(bytes, 5 + FingerprintLength, length);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (version.Length == 0 || version.Any(char.IsControl)) return null;
        return new InviteGame(version, Loaders[bytes[1]], bytes[2] << 8 | bytes[3], bytes[4..(4 + FingerprintLength)]);
    }

    /// <summary>The segment without its leading dash: <c>G</c> + Base32 payload + check symbol (covering both keys and the payload).</summary>
    internal string ToSegment(ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> token)
    {
        var bytes = ToBytes();
        return "G" + Base32.Encode(bytes) + CheckSymbol(lobbyKey, token, bytes);
    }

    /// <summary>Reads a segment written by <see cref="ToSegment"/>; null when it is damaged or was not made for these keys.</summary>
    internal static InviteGame? FromSegment(string segment, ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> token)
    {
        if (segment.Length < 3 || segment[0] != 'G') return null;
        if (Base32.Decode(segment[1..^1]) is not { } bytes || FromBytes(bytes) is not { } game) return null;
        return segment[^1] == CheckSymbol(lobbyKey, token, bytes) ? game : null;
    }

    private static char CheckSymbol(ReadOnlySpan<byte> lobbyKey, ReadOnlySpan<byte> token, byte[] payload)
    {
        var data = new byte[1 + lobbyKey.Length + token.Length + payload.Length];
        data[0] = 1;
        lobbyKey.CopyTo(data.AsSpan(1));
        token.CopyTo(data.AsSpan(1 + lobbyKey.Length));
        payload.CopyTo(data, 1 + lobbyKey.Length + token.Length);
        return Base32.CheckAlphabet[(int)(new BigInteger(data, isUnsigned: true, isBigEndian: true) % 37)];
    }

    /// <summary>Crockford Base32 as a plain bit stream (5 bits per symbol, the last one padded with zeros).</summary>
    internal static class Base32
    {
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const string CheckAlphabet = Alphabet + "*~$=U";

        public static string Encode(ReadOnlySpan<byte> bytes)
        {
            var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in bytes)
            {
                buffer = buffer << 8 | b;
                bits += 8;
                while (bits >= 5)
                {
                    builder.Append(Alphabet[buffer >> (bits - 5) & 31]);
                    bits -= 5;
                }
                buffer &= (1 << bits) - 1;
            }
            if (bits > 0) builder.Append(Alphabet[buffer << (5 - bits) & 31]);
            return builder.ToString();
        }

        public static byte[]? Decode(string text)
        {
            var output = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var raw in text)
            {
                var c = raw switch { 'I' or 'L' or 'i' or 'l' => '1', 'O' or 'o' => '0', _ => char.ToUpperInvariant(raw) };
                var digit = Alphabet.IndexOf(c);
                if (digit < 0) return null;
                buffer = buffer << 5 | digit;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                    buffer &= (1 << bits) - 1;
                }
            }
            // Leftover bits are padding: there must be fewer than 5 of them, all zero.
            return bits >= 5 || buffer != 0 ? null : [.. output];
        }
    }
}
