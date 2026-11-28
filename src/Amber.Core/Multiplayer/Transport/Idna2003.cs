using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// IDNA2003 ToASCII (RFC 3490) with Nameprep (RFC 3491) and Punycode (RFC 3492), with the flags the game uses:
/// unassigned code points not allowed, STD3 ASCII rules not applied. This is what Java's <c>java.net.IDN.toASCII(host)</c>
/// returns, including how it treats dots, empty labels and ASCII labels, checked against a real Java runtime.
/// .NET's IdnMapping can't be used: it follows UTS #46 with current Unicode data, which maps some names to a
/// different domain than the game does.
/// </summary>
internal static class Idna2003
{
    private const string AcePrefix = "xn--";
    private const int MaxLabelLength = 63;

    /// <summary>
    /// The ASCII form of <paramref name="host"/> exactly as Java's IDN.toASCII gives it, or null where Java throws.
    /// Labels are separated by U+002E and its ideographic (U+3002), full-width (U+FF0E) and half-width (U+FF61) forms,
    /// and joined with U+002E. One trailing dot is kept; any other empty label fails. An all-ASCII label is kept as it
    /// is (case and all), but like every label it must be 1 to 63 characters long.
    /// </summary>
    internal static string? ToAscii(string host)
    {
        if (host.Length == 0) return "";
        if (host.Length == 1 && IsDot(host[0])) return ".";
        var result = new StringBuilder(host.Length + 8);
        var start = 0;
        while (true)
        {
            var end = start;
            while (end < host.Length && !IsDot(host[end])) end++;
            if (end == host.Length && start == end && start > 0) break; // the root label after a trailing dot
            if (LabelToAscii(host[start..end]) is not { } label) return null;
            result.Append(label);
            if (end == host.Length) break;
            result.Append('.');
            start = end + 1;
        }
        return result.ToString();
    }

    private static bool IsDot(char c) => c is '.' or '\u3002' or '\uFF0E' or '\uFF61';

    /// <summary>RFC 3490 section 4.1 ToASCII for one label (AllowUnassigned and UseSTD3ASCIIRules both off).</summary>
    private static string? LabelToAscii(string label)
    {
        // Step 1: an all-ASCII label skips Nameprep (and so isn't lowercased).
        var output = label;
        if (!Ascii.IsValid(label))
        {
            // Step 2: Nameprep.
            if (Nameprep.Prepare(label) is not { } prepared) return null;
            output = prepared;
            // Step 4: if that left only ASCII, it goes out as it is; otherwise steps 5 to 7.
            if (!Ascii.IsValid(output))
            {
                if (output.StartsWith(AcePrefix, StringComparison.OrdinalIgnoreCase)) return null;
                if (PunycodeEncode(output) is not { } encoded) return null;
                output = AcePrefix + encoded;
            }
        }
        // Step 8.
        return output.Length is >= 1 and <= MaxLabelLength ? output : null;
    }

    /// <summary>The Punycode encoder of RFC 3492 section 6.3, with its parameters from section 5; null on overflow.</summary>
    internal static string? PunycodeEncode(string input)
    {
        const int Base = 36, TMin = 1, TMax = 26, InitialBias = 72, InitialN = 0x80;
        var codePoints = new List<int>(input.Length);
        for (var i = 0; i < input.Length; i++) codePoints.Add(Nameprep.ReadCodePoint(input, ref i));

        var output = new StringBuilder(input.Length + 8);
        foreach (var cp in codePoints)
            if (cp < 0x80) output.Append((char)cp);
        int basic = output.Length, handled = basic;
        if (basic > 0) output.Append('-');

        long n = InitialN, delta = 0;
        var bias = InitialBias;
        while (handled < codePoints.Count)
        {
            // The smallest code point not handled yet.
            var m = int.MaxValue;
            foreach (var cp in codePoints)
                if (cp >= n && cp < m) m = cp;
            delta += (m - n) * (handled + 1);
            if (delta > int.MaxValue) return null;
            n = m;
            foreach (var cp in codePoints)
            {
                if (cp < n && ++delta > int.MaxValue) return null;
                if (cp != n) continue;
                // Write delta as a generalized variable-length integer.
                var q = delta;
                for (var k = Base; ; k += Base)
                {
                    var t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                    if (q < t) break;
                    output.Append(Digit(t + (q - t) % (Base - t)));
                    q = (q - t) / (Base - t);
                }
                output.Append(Digit(q));
                bias = Adapt(delta, handled + 1, handled == basic);
                delta = 0;
                handled++;
            }
            delta++;
            n++;
        }
        return output.ToString();

        static char Digit(long d) => (char)(d < 26 ? 'a' + d : '0' + d - 26);

        // Bias adaptation, RFC 3492 section 6.1.
        static int Adapt(long delta, int numPoints, bool firstTime)
        {
            const int Damp = 700, Skew = 38;
            delta = firstTime ? delta / Damp : delta / 2;
            delta += delta / numPoints;
            var k = 0;
            while (delta > (Base - TMin) * TMax / 2)
            {
                delta /= Base - TMin;
                k += Base;
            }
            return (int)(k + (Base - TMin + 1) * delta / (delta + Skew));
        }
    }
}
