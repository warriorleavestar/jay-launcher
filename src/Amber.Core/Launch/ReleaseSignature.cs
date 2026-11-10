using System.Security.Cryptography;
using System.Text;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Launch;

/// <summary>
/// Jay's own release signatures. The maintainer signs each release's SHA256SUMS.txt offline with an
/// Ed25519 release key and uploads SHA256SUMS.txt.sig next to it. Anyone who already has a genuine Jay
/// can then check a downloaded package: the signature must come from a key in <see cref="TrustedKeys"/>,
/// it must cover this exact SHA256SUMS.txt and the tag the user expects, and the package's SHA-256 must
/// be listed in it. This doesn't replace Windows/macOS code signing (it can't help with a first download
/// or OS warnings); it stops a stolen GitHub account or a swapped mirror file from passing as an update.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// Public release keys (base64 Ed25519). Empty until the maintainer runs <c>jay-server release key</c>
    /// and adds the printed public key here. More than one is allowed so a key can be rotated.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedKeys = [];

    public const string SumsFileName = "SHA256SUMS.txt";
    public const string SignatureFileName = "SHA256SUMS.txt.sig";
    private const string Header = "jay-release-signature-v1";
    private const string Domain = "jay-release-v1\n";

    /// <summary>What is signed: a domain string, the tag, and the SHA-256 of the sums file.</summary>
    public static byte[] Message(string tag, ReadOnlySpan<byte> sums) =>
        Encoding.UTF8.GetBytes(Domain + NormalizeTag(tag) + "\n" + Convert.ToHexStringLower(SHA256.HashData(sums)) + "\n");

    /// <summary>Signs the sums file for a tag and returns the text of SHA256SUMS.txt.sig.</summary>
    public static string Sign(NetworkSigner key, string tag, ReadOnlySpan<byte> sums)
    {
        var signature = key.Sign(Message(tag, sums));
        return $"{Header}\ntag: {NormalizeTag(tag)}\nkey: {Convert.ToBase64String(key.PublicKey)}\nsig: {Convert.ToBase64String(signature)}\n";
    }

    /// <summary>
    /// Checks a signature file against the sums file. <paramref name="expectedTag"/> is the version the
    /// caller asked for (null accepts whatever tag was signed, and the result reports it).
    /// </summary>
    public static ReleaseCheck Verify(string signatureText, ReadOnlySpan<byte> sums, string? expectedTag, IReadOnlyList<string>? trustedKeys = null)
    {
        trustedKeys ??= TrustedKeys;
        if (trustedKeys.Count == 0) return new(ReleaseVerdict.NoTrustedKey, null);
        if (!TryParse(signatureText, out var tag, out var key, out var signature)) return new(ReleaseVerdict.Malformed, null);
        if (!trustedKeys.Any(k => TryDecode(k, out var trusted) && CryptographicOperations.FixedTimeEquals(trusted, key)))
            return new(ReleaseVerdict.UnknownKey, tag);
        if (!RoomSigner.Verify(key, Message(tag, sums), signature)) return new(ReleaseVerdict.BadSignature, tag);
        if (expectedTag is not null && NormalizeTag(expectedTag) != tag) return new(ReleaseVerdict.WrongTag, tag);
        return new(ReleaseVerdict.Valid, tag);
    }

    /// <summary>
    /// Full check of a downloaded package: SHA256SUMS.txt and SHA256SUMS.txt.sig must sit next to it
    /// (or be given), the signature must be valid, and the package's name and hash must be listed.
    /// </summary>
    public static async Task<ReleaseCheck> VerifyPackageAsync(string packagePath, string? expectedTag = null,
        string? sumsPath = null, string? signaturePath = null, IReadOnlyList<string>? trustedKeys = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? ".";
        sumsPath ??= Path.Combine(dir, SumsFileName);
        signaturePath ??= Path.Combine(dir, SignatureFileName);
        if (!File.Exists(sumsPath) || !File.Exists(signaturePath)) return new(ReleaseVerdict.Unsigned, null);

        var sums = await File.ReadAllBytesAsync(sumsPath, ct);
        var check = Verify(await File.ReadAllTextAsync(signaturePath, ct), sums, expectedTag, trustedKeys);
        if (check.Verdict != ReleaseVerdict.Valid) return check;

        var listed = ParseSums(Encoding.UTF8.GetString(sums));
        if (!listed.TryGetValue(Path.GetFileName(packagePath), out var expected)) return check with { Verdict = ReleaseVerdict.NotListed };
        await using var stream = File.OpenRead(packagePath);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return actual == expected ? check : check with { Verdict = ReleaseVerdict.HashMismatch };
    }

    /// <summary>Writes SHA256SUMS.txt text for files (sorted by name, "hash  name" per line, like sha256sum).</summary>
    public static string BuildSums(IEnumerable<string> files)
    {
        var lines = files
            .Select(f => (Name: Path.GetFileName(f), Path: f))
            .Where(f => f.Name is not SumsFileName and not SignatureFileName)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f =>
            {
                using var stream = File.OpenRead(f.Path);
                return $"{Convert.ToHexStringLower(SHA256.HashData(stream))}  {f.Name}\n";
            });
        return string.Concat(lines);
    }

    /// <summary>Reads sha256sum-style lines ("hash  name" or "hash *name"). Names must be plain file names.</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 66 || line[64] != ' ') continue;
            var hash = line[..64].ToLowerInvariant();
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[65..].TrimStart(' ', '*');
            if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains("..")) continue;
            // A name listed twice with different hashes is ambiguous: drop it so it can't verify.
            if (result.TryGetValue(name, out var existing) && existing != hash) result[name] = "";
            else result[name] = hash;
        }
        foreach (var bad in result.Where(p => p.Value.Length == 0).Select(p => p.Key).ToList()) result.Remove(bad);
        return result;
    }

    public static string NormalizeTag(string tag)
    {
        var t = tag.Trim();
        return t.StartsWith('v') || t.StartsWith('V') ? "v" + t[1..] : "v" + t;
    }

    private static bool TryParse(string text, out string tag, out byte[] key, out byte[] signature)
    {
        tag = ""; key = []; signature = [];
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || lines[0] != Header) return false;
        string? t = null, k = null, s = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) return false;
            var (name, value) = (line[..colon], line[(colon + 2)..].Trim());
            switch (name)
            {
                case "tag" when t is null: t = value; break;
                case "key" when k is null: k = value; break;
                case "sig" when s is null: s = value; break;
                default: return false;
            }
        }
        if (t is null || k is null || s is null || t.Length == 0) return false;
        if (!TryDecode(k, out key) || key.Length != 32) return false;
        if (!TryDecode(s, out signature) || signature.Length != 64) return false;
        tag = NormalizeTag(t);
        return true;
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(base64.Trim()); return true; }
        catch (FormatException) { bytes = []; return false; }
    }
}

public enum ReleaseVerdict
{
    /// <summary>Signed by a trusted release key, for the expected tag, and the package hash matches.</summary>
    Valid,
    /// <summary>No SHA256SUMS.txt / .sig next to the package (older releases, or not signed yet).</summary>
    Unsigned,
    /// <summary>This Jay has no release key built in yet, so it can't check anything.</summary>
    NoTrustedKey,
    Malformed,
    /// <summary>Signed, but by a key this Jay doesn't trust.</summary>
    UnknownKey,
    BadSignature,
    /// <summary>A valid signature for a different version (e.g. an old release passed off as new).</summary>
    WrongTag,
    NotListed,
    HashMismatch,
}

public sealed record ReleaseCheck(ReleaseVerdict Verdict, string? Tag)
{
    public bool IsValid => Verdict == ReleaseVerdict.Valid;
}
