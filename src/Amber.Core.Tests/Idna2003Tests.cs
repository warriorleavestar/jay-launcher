using System.Diagnostics;
using System.Text;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Join names are converted exactly as the game converts them (Java's IDN.toASCII: IDNA2003 with Unicode 3.2 data),
/// so a relay connects to the same domain the game would. Every expected value here was produced by a real Java runtime;
/// null stands for "Java throws".
/// </summary>
public sealed class Idna2003Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amber-idna-" + Guid.NewGuid().ToString("N"));

    [Theory]
    // Unicode 3.2 case folding, not today's: U+04C0 folds to itself (U+04CF didn't exist yet) and Georgian
    // Asomtavruli and Cherokee capitals had no lowercase.
    [InlineData("\u04C0.example.org", "xn--d5a.example.org")]
    [InlineData("\u04CF.example.org", null)]
    [InlineData("\u10A0.example.org", "xn--7md.example.org")]
    [InlineData("\u13A0.example.org", "xn--58d.example.org")]
    [InlineData("\u13F5", null)]
    // ß and final sigma fold, ZWJ is mapped to nothing.
    [InlineData("stra\u00DFe.example.org", "strasse.example.org")]
    [InlineData("\u03C2\u03BF\u03C6\u03CC\u03C2.example.org", "xn--0xahbl4a.example.org")]
    [InlineData("a\u200Db.example.org", "ab.example.org")]
    [InlineData("b\u00FC\u200Dcher.example.org", "xn--bcher-kva.example.org")]
    [InlineData("\u0130stanbul.example.org", "xn--istanbul-o0e.example.org")]
    // Bidi: R/AL must start and end the label and can't be mixed with L.
    [InlineData("\u0627\u0031.example.org", null)]
    [InlineData("\u05D0a\u05D1.example.org", null)]
    [InlineData("\u0627\u0031\u0628.example.org", "xn--1-ymce.example.org")]
    // Unassigned in Unicode 3.2 (U+0221 came in 4.0, capital sharp s in 5.1).
    [InlineData("a\u0221b.example.org", null)]
    [InlineData("\u1E9E.example.org", null)]
    // Prohibited: private use, line separator. A no-break space is NFKC-mapped to an ASCII space first and passes.
    [InlineData("a\uE000b.example.org", null)]
    [InlineData("b\u00FC\u2028cher.example.org", null)]
    [InlineData("a\u00A0b.example.org", "a b.example.org")]
    // The ACE prefix is refused on a label that still needs encoding, but an ASCII label passes as it is.
    [InlineData("xn--b\u00FCcher.example.org", null)]
    [InlineData("XN--b\u00FCcher.example.org", null)]
    [InlineData("xn--abc.example.org", "xn--abc.example.org")]
    // Every label, ASCII ones too, must be 1 to 63 characters, the encoded form included.
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.org", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.org")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.org", null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u00FC", "xn--aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-8yf")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u00FC", null)]
    // Ideographic, full-width and half-width dots separate labels; one trailing dot stays, other empty labels fail.
    [InlineData("b\u00FCcher\uFF0Eexample\uFF61org\u3002", "xn--bcher-kva.example.org.")]
    [InlineData("B\u00FCcher.EXAMPLE.org", "xn--bcher-kva.EXAMPLE.org")]
    [InlineData("b\u00FCcher..example.org", null)]
    [InlineData("\u00FC.", "xn--tda.")]
    [InlineData("\u200D.example.org", null)]
    [InlineData(".", ".")]
    [InlineData("", "")]
    // Beyond U+FFFF: Deseret folds, mathematical bold A becomes "a", and the CJK compatibility ideographs that
    // Unicode 4.0 corrected keep their Unicode 3.2 decomposition (U+2F868 to U+2136A, U+2F874 to U+5F33).
    [InlineData("\U00010400.example.org", "xn--hj8c.example.org")]
    [InlineData("\U0001D400bc.example.org", "abc.example.org")]
    [InlineData("\U0002F868.example.org", "xn--j74i.example.org")]
    [InlineData("\U0002F874.example.org", "xn--x1t.example.org")]
    public void ConvertsLikeJava(string host, string? expected)
    {
        Assert.Equal(expected, Idna2003.ToAscii(host));
        Assert.Equal(expected, JoinTarget.ToAsciiLikeTheGame(host));
    }

    [Fact]
    public void LoneSurrogatesAreProhibited()
    {
        Assert.Null(Idna2003.ToAscii("a\uD800b.example.org"));
        Assert.Null(Idna2003.ToAscii("a\uDC00b"));
    }

    [Theory]
    // RFC 3492 section 7.1, samples (A) and (B), and one code point beyond U+FFFF.
    [InlineData("\u0644\u064A\u0647\u0645\u0627\u0628\u062A\u0643\u0644\u0645\u0648\u0634\u0639\u0631\u0628\u064A\u061F", "egbpdaj6bu4bxfgehfvwxn")]
    [InlineData("\u4ED6\u4EEC\u4E3A\u4EC0\u4E48\u4E0D\u8BF4\u4E2D\u6587", "ihqwcrb4cv8a8dqg056pqjye")]
    [InlineData("\U0001F600", "e28h")]
    public void PunycodeMatchesTheRfcSamples(string input, string expected) => Assert.Equal(expected, Idna2003.PunycodeEncode(input));

    /// <summary>
    /// When a Java runtime is installed, compares a few hundred single code points and mixed-script labels with
    /// java.net.IDN.toASCII itself. Without Java (or with one too old to run a source file) there is nothing to compare.
    /// </summary>
    [Fact]
    public void MatchesJavaWhenInstalled()
    {
        var java = FindJava();
        if (java is null) return;
        var inputs = SampleInputs();
        Directory.CreateDirectory(_dir);
        var inputFile = Path.Combine(_dir, "inputs.txt");
        File.WriteAllLines(inputFile, inputs.Select(s => string.Join(' ', CodeUnitsOrPoints(s).Select(c => c.ToString("X")))));
        var source = Path.Combine(_dir, "ToAscii.java");
        File.WriteAllText(source, """
            import java.io.*;
            import java.net.IDN;
            import java.nio.charset.StandardCharsets;
            import java.nio.file.*;
            public class ToAscii {
                public static void main(String[] args) throws Exception {
                    PrintStream out = new PrintStream(System.out, true, "UTF-8");
                    for (String line : Files.readAllLines(Paths.get(args[0]), StandardCharsets.UTF_8)) {
                        StringBuilder s = new StringBuilder();
                        if (!line.isEmpty())
                            for (String hex : line.split(" ")) {
                                int cp = Integer.parseInt(hex, 16);
                                if (cp > 0xFFFF) s.appendCodePoint(cp); else s.append((char) cp);
                            }
                        String result;
                        try { result = IDN.toASCII(s.toString()); } catch (IllegalArgumentException e) { result = null; }
                        out.println(result == null ? "!" : "=" + result.replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "\\r"));
                    }
                }
            }
            """);
        var info = new ProcessStartInfo(java) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(source);
        info.ArgumentList.Add(inputFile);
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "java did not finish");
        // A stub launcher without a runtime, or a Java before 11 that can't run a source file: nothing to compare with.
        if (process.ExitCode != 0 && !output.Contains('!') && !output.Contains('=')) return;
        Assert.True(process.ExitCode == 0, stderr.Result);

        var results = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        Assert.Equal(inputs.Count, results.Length);
        var differences = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var expected = results[i] == "!" ? null : results[i][1..].Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\\\", "\\");
            var actual = Idna2003.ToAscii(inputs[i]);
            if (expected != actual)
                differences.Add($"{string.Join(' ', CodeUnitsOrPoints(inputs[i]).Select(c => "U+" + c.ToString("X4")))}: Java {expected ?? "throws"}, here {actual ?? "null"}");
        }
        Assert.True(differences.Count == 0, string.Join('\n', differences.Take(20)));
    }

    /// <summary>
    /// Code points that are easy to get wrong (case folding that changed after Unicode 3.2, mapped-to-nothing,
    /// prohibited, bidi, corrected decompositions, beyond U+FFFF), a seeded random spread over planes 0 to 2,
    /// each alone and inside a label, and random labels drawn from several scripts.
    /// </summary>
    private static List<string> SampleInputs()
    {
        int[] interesting =
        [
            0x00A0, 0x00AD, 0x00DF, 0x0130, 0x0221, 0x0340, 0x03C2, 0x03F4, 0x04C0, 0x04CF, 0x05BE, 0x0627, 0x06DD,
            0x10A0, 0x10C5, 0x13A0, 0x13F4, 0x1806, 0x1E9E, 0x200B, 0x200C, 0x200D, 0x200E, 0x2028, 0x2060, 0x2121,
            0x2160, 0x3000, 0x3002, 0xE000, 0xF951, 0xFB01, 0xFEFF, 0xFF21, 0xFFFD, 0xFFFF, 0x10400, 0x1D400, 0x1D7FF,
            0x2F868, 0x2F874, 0x2F91F, 0x2F95F, 0x2F9BF, 0xE0001, 0xE0041, 0xF0000,
        ];
        int[][] scripts =
        [
            [0x30, 0x3A], [0x41, 0x5B], [0x61, 0x7B], [0xC0, 0x250], [0x370, 0x400], [0x400, 0x530], [0x531, 0x590],
            [0x591, 0x600], [0x600, 0x700], [0x900, 0x980], [0x10A0, 0x1100], [0x1100, 0x1200], [0x13A0, 0x13F6],
            [0x1E00, 0x2000], [0x2000, 0x2070], [0x2100, 0x2200], [0x3000, 0x3100], [0x4E00, 0x4E80], [0xAC00, 0xAD00],
            [0xF900, 0xFB00], [0xFB00, 0xFE00], [0xFF00, 0xFFF0], [0x300, 0x370], [0x1D400, 0x1D800], [0x2F800, 0x2FA20],
        ];
        var random = new Random(3490);
        var points = interesting.Concat(Enumerable.Range(0, 250).Select(_ => random.Next(0, 0x30000))).ToList();
        var inputs = new List<string>();
        foreach (var cp in points)
        {
            var s = cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
            inputs.Add(s);
            inputs.Add("a" + s + "b.example.org");
        }
        for (var i = 0; i < 300; i++)
        {
            var picked = Enumerable.Range(0, random.Next(1, 4)).Select(_ => scripts[random.Next(scripts.Length)]).ToArray();
            var label = new StringBuilder();
            for (var n = random.Next(1, 12); n > 0; n--)
            {
                var range = picked[random.Next(picked.Length)];
                label.Append(char.ConvertFromUtf32(random.Next(range[0], range[1])));
            }
            inputs.Add(label + (i % 3 == 0 ? ".example.org" : ""));
        }
        return inputs;
    }

    /// <summary>The string as code points, with a lone surrogate standing for itself.</summary>
    private static IEnumerable<int> CodeUnitsOrPoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) yield return char.ConvertToUtf32(s[i], s[++i]);
            else yield return s[i];
        }
    }

    private static string? FindJava()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", name)))
            return Path.Combine(home, "bin", name);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
