using System.Text;

namespace Amber.Core.Identity;

/// <summary>Bech32m (BIP350) encoding: lowercase, a checksum that catches typos, and no 0/O or 1/l look-alikes.</summary>
public static class Bech32m
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Constant = 0x2bc830a3;
    public const int MaxLength = 90;

    /// <summary>Encodes 5-bit groups (each 0..31) under a human-readable part.</summary>
    public static string Encode(string hrp, ReadOnlySpan<byte> data5)
    {
        var checksum = Checksum(hrp, data5);
        var text = new StringBuilder(hrp.Length + 1 + data5.Length + 6);
        text.Append(hrp).Append('1');
        foreach (var d in data5) text.Append(Charset[d]);
        foreach (var d in checksum) text.Append(Charset[d]);
        return text.ToString();
    }

    /// <summary>Decodes into the human-readable part and 5-bit groups; false on bad characters, mixed case or a wrong checksum.</summary>
    public static bool TryDecode(string? text, out string hrp, out byte[] data5)
    {
        hrp = "";
        data5 = [];
        if (text is null || text.Length is < 8 or > MaxLength) return false;
        if (text.Any(c => c is < (char)33 or > (char)126)) return false;
        if (text.Any(char.IsLower) && text.Any(char.IsUpper)) return false;
        text = text.ToLowerInvariant();
        var separator = text.LastIndexOf('1');
        if (separator < 1 || separator + 7 > text.Length) return false;
        hrp = text[..separator];
        var values = new byte[text.Length - separator - 1];
        for (var i = 0; i < values.Length; i++)
        {
            var at = Charset.IndexOf(text[separator + 1 + i]);
            if (at < 0) return false;
            values[i] = (byte)at;
        }
        if (Polymod(Expand(hrp), values) != Constant) return false;
        data5 = values[..^6];
        return true;
    }

    /// <summary>Regroups bits (8→5 with padding, or 5→8 without).</summary>
    public static byte[]? ConvertBits(ReadOnlySpan<byte> data, int from, int to, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>(data.Length * from / to + 1);
        var max = (1 << to) - 1;
        foreach (var value in data)
        {
            if (value >> from != 0) return null;
            acc = (acc << from) | value;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte)((acc >> bits) & max));
            }
        }
        if (pad)
        {
            if (bits > 0) result.Add((byte)((acc << (to - bits)) & max));
        }
        else if (bits >= from || ((acc << (to - bits)) & max) != 0)
        {
            return null;
        }
        return [.. result];
    }

    private static byte[] Checksum(string hrp, ReadOnlySpan<byte> data5)
    {
        var mod = Polymod(Expand(hrp), [.. data5, 0, 0, 0, 0, 0, 0]) ^ Constant;
        var result = new byte[6];
        for (var i = 0; i < 6; i++) result[i] = (byte)((mod >> (5 * (5 - i))) & 31);
        return result;
    }

    private static byte[] Expand(string hrp)
    {
        var result = new byte[hrp.Length * 2 + 1];
        for (var i = 0; i < hrp.Length; i++)
        {
            result[i] = (byte)(hrp[i] >> 5);
            result[hrp.Length + 1 + i] = (byte)(hrp[i] & 31);
        }
        return result;
    }

    private static uint Polymod(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint chk = 1;
        Step(a, ref chk);
        Step(b, ref chk);
        return chk;

        static void Step(ReadOnlySpan<byte> values, ref uint chk)
        {
            uint[] gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffff) << 5) ^ v;
                for (var i = 0; i < 5; i++)
                {
                    if (((top >> i) & 1) != 0) chk ^= gen[i];
                }
            }
        }
    }
}
