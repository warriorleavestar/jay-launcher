using System.Text;

namespace Amber.Core.Multiplayer.Transport;

/// <summary>
/// Nameprep (RFC 3491): the stringprep (RFC 3454) profile IDNA2003 runs on every non-ASCII label before Punycode.
/// It is what Java's <c>java.net.IDN</c> (and so the game) does, with unassigned code points not allowed:
/// <list type="number">
/// <item>reject code points unassigned in Unicode 3.2 (table A.1);</item>
/// <item>map: drop table B.1, case fold with table B.2;</item>
/// <item>normalize with NFKC;</item>
/// <item>reject prohibited output (C.1.2, C.2.2, C.3 to C.9);</item>
/// <item>check bidi (section 6): with any R/AL (D.1) there may be no L (D.2), and the first and last must be R/AL.</item>
/// </list>
/// The tables are in Nameprep.Tables.cs, generated from the RFC text. Modern Unicode would give other answers
/// (its case folding turns U+04C0 into U+04CF, for example), so nothing here uses the runtime's Unicode data
/// except the NFKC step, which is stable for Unicode 3.2 characters apart from <see cref="NormalizationCorrections"/>.
/// </summary>
internal static partial class Nameprep
{
    /// <summary>
    /// Unicode 3.2 characters whose decomposition Unicode corrected later (Corrigendum #4, Unicode 4.0): these
    /// CJK compatibility ideographs decompose to their Unicode 3.2 value in IDNA2003, as they do in Java, and not to
    /// the corrected one the runtime's NFKC uses. Found by comparing every assigned code point with Java.
    /// </summary>
    private static readonly (int CodePoint, int Unicode32Decomposition)[] NormalizationCorrections =
    [
        (0x2F868, 0x2136A),
        (0x2F874, 0x5F33),
        (0x2F91F, 0x43AB),
        (0x2F95F, 0x7AAE),
        (0x2F9BF, 0x4D57),
    ];

    /// <summary>Prepares one label; null when Nameprep fails (unassigned, prohibited or bidi rules broken).</summary>
    internal static string? Prepare(string label)
    {
        // Steps 1 and 2: reject unassigned code points, then map. A lone surrogate is taken as its own code point:
        // it is assigned (as a surrogate) and prohibited below (C.5), as in Java.
        var mapped = new StringBuilder(label.Length + 8);
        for (var i = 0; i < label.Length; i++)
        {
            var cp = ReadCodePoint(label, ref i);
            if (InRanges(Unassigned, cp)) return null;
            if (MappedToNothing.BinarySearch(cp) >= 0) continue;
            AppendCaseFolded(mapped, cp);
        }

        // Step 3: NFKC. The runtime refuses to normalize ill-formed UTF-16 (lone surrogates); those are prohibited anyway.
        string normalized;
        try { normalized = Normalize(mapped.ToString()); }
        catch (ArgumentException) { return null; }

        // Steps 4 and 5: prohibited output and bidi.
        bool anyRandAL = false, anyL = false, firstRandAL = false, lastRandAL = false;
        for (var i = 0; i < normalized.Length; i++)
        {
            var first = i == 0;
            var cp = ReadCodePoint(normalized, ref i);
            if (InRanges(Prohibited, cp)) return null;
            var randAL = InRanges(RandAL, cp);
            anyRandAL |= randAL;
            anyL |= InRanges(LeftToRight, cp);
            if (first) firstRandAL = randAL;
            lastRandAL = randAL;
        }
        if (anyRandAL && (anyL || !firstRandAL || !lastRandAL)) return null;
        return normalized;
    }

    /// <summary>NFKC as in Unicode 3.2: the runtime's NFKC, with the few later decomposition corrections undone.</summary>
    private static string Normalize(string s)
    {
        foreach (var (cp, old) in NormalizationCorrections)
            if (s.Contains(char.ConvertFromUtf32(cp), StringComparison.Ordinal))
                s = s.Replace(char.ConvertFromUtf32(cp), char.ConvertFromUtf32(old), StringComparison.Ordinal);
        return s.Normalize(NormalizationForm.FormKC);
    }

    /// <summary>The code point at <paramref name="i"/>; moves <paramref name="i"/> to its low surrogate when it is a pair.</summary>
    internal static int ReadCodePoint(string s, ref int i)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            return char.ConvertToUtf32(s[i], s[++i]);
        return s[i];
    }

    private static void AppendCaseFolded(StringBuilder to, int cp)
    {
        var multi = CaseFoldMultiKeys.BinarySearch(cp);
        if (multi >= 0)
        {
            to.Append(CaseFoldMultiValues, CaseFoldMultiOffsets[multi], CaseFoldMultiOffsets[multi + 1] - CaseFoldMultiOffsets[multi]);
            return;
        }
        AppendCodePoint(to, cp + CaseFoldDelta(cp));
    }

    /// <summary>How far table B.2 moves a code point that maps to one code point (0 when it isn't in the table).</summary>
    private static int CaseFoldDelta(int cp)
    {
        // Runs are four numbers each (first, last, step, delta), sorted by first and not overlapping.
        var runs = CaseFoldRuns;
        int lo = 0, hi = runs.Length / 4 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var at = mid * 4;
            if (cp < runs[at]) hi = mid - 1;
            else if (cp > runs[at + 1]) lo = mid + 1;
            else return (cp - runs[at]) % runs[at + 2] == 0 ? runs[at + 3] : 0;
        }
        return 0;
    }

    /// <summary>Whether <paramref name="cp"/> falls in one of the inclusive (first, last) pairs of <paramref name="ranges"/>.</summary>
    private static bool InRanges(ReadOnlySpan<int> ranges, int cp)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (cp < ranges[2 * mid]) hi = mid - 1;
            else if (cp > ranges[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static void AppendCodePoint(StringBuilder to, int cp)
    {
        if (cp < 0x10000) to.Append((char)cp);
        else to.Append(char.ConvertFromUtf32(cp));
    }
}
