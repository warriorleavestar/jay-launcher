using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Launch;

/// <summary>
/// Jay's release signatures (privacy plan, items 2 and 3). A release counts only when enough of the Jay trustees signed it
/// (2 of 3), each with both Ed25519 and ML-DSA-65, using their release keys (<see cref="JayTrust.ReleaseKeys"/>), which are
/// separate from the keys that review networks.
/// <para>
/// Next to the packages a release carries <c>SHA256SUMS.txt</c>, <c>SHA256SUMS.txt.sigs</c> (JSON, see
/// <see cref="ReleaseManifest"/>) and, once the trustee list has ever changed, <c>trust-chain.json</c>
/// (<see cref="TrustChain"/>). Trustees sign offline with <c>jay-server release cosign</c>.
/// </para>
/// <para>
/// What is signed is the lines <c>jay-release-v2</c>, tag, seq, commit, SHA-256 of SHA256SUMS.txt, hash of the trustee
/// list built into that release, and the release log head, each followed by "\n". The JSON is not signed directly, so
/// one release has exactly one byte string.
/// </para>
/// This doesn't replace Windows/macOS code signing (it can't help with a first download or OS warnings); it stops a
/// stolen GitHub account, a compromised CI run, a swapped mirror file or a single trustee from passing as an update.
/// </summary>
public static class ReleaseSignature
{
    public const string SumsFileName = "SHA256SUMS.txt";
    public const string SignatureFileName = "SHA256SUMS.txt.sigs";
    public const string ChainFileName = "trust-chain.json";
    public const int Version = 2;
    public const int MaxSignatures = 8;

    /// <summary>Makes an unsigned manifest; trustees then add signatures with <see cref="Cosign"/>.</summary>
    public static string Create(ReleaseStatement statement) => new ReleaseManifest(statement, []).ToJson();

    /// <summary>Adds <paramref name="signer"/>'s signature. Refuses a signer who already signed; <c>jay-server release cosign</c> shows whether the existing ones verify.</summary>
    public static string Cosign(string manifestJson, TrustSigner signer)
    {
        var manifest = ReleaseManifest.Parse(manifestJson) ?? throw new InvalidDataException("not a release signature file");
        if (manifest.Signatures.Any(s => s.KeyId.AsSpan().SequenceEqual(signer.Id))) throw new InvalidDataException("this key already signed");
        if (manifest.Signatures.Count >= MaxSignatures) throw new InvalidDataException("too many signatures");
        return (manifest with { Signatures = [.. manifest.Signatures, QuorumSignature.Of(signer.Sign(manifest.Statement.Bytes()))] }).ToJson();
    }

    /// <summary>
    /// Checks a release. <paramref name="sums"/> is the SHA256SUMS.txt that was downloaded with it,
    /// <paramref name="chainJson"/> the release's trust-chain.json (null when there is none).
    /// </summary>
    /// <param name="own">The trustee list this Jay was built with (normally <see cref="TrustList.Jay"/>).</param>
    /// <param name="highestSeq">The highest release seq this Jay has accepted before: an older one is refused (no rollback).</param>
    /// <param name="component">Null for a Jay release; a <see cref="ReleaseStatement.Components"/> name (for example
    /// <c>easytier</c>) for a component build. A signature for anything else is refused, so one can never pass as the other.</param>
    /// <param name="chainSeq">The seq of the newest trustee change this Jay has followed from <paramref name="own"/> (0 when
    /// none): a release signed by a list that change retired is refused (<see cref="ReleaseCheck.ChainSeq"/> gives the new value).</param>
    /// <param name="chainHash">The hash of the list that change introduced (null when none): the chain to the release's list
    /// must pass through it, so a retired list can't sign a change of its own that goes around it
    /// (<see cref="ReleaseCheck.ChainHash"/> gives the new value).</param>
    public static ReleaseCheck Verify(string manifestJson, ReadOnlySpan<byte> sums, string? chainJson, string? expectedTag,
        TrustList? own = null, long highestSeq = 0, string? component = null, long chainSeq = 0, string? chainHash = null)
    {
        own ??= TrustList.Jay;
        if (own.Keys.Count == 0) return new(ReleaseVerdict.NoTrustedKey, null, 0);
        if (ReleaseManifest.Parse(manifestJson) is not { } manifest) return new(ReleaseVerdict.Malformed, null, 0);
        var s = manifest.Statement;
        if (s.Component != component) return new(ReleaseVerdict.WrongComponent, s.Tag, s.Seq);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(s.SumsHash), SHA256.HashData(sums)))
            return new(ReleaseVerdict.SumsMismatch, s.Tag, s.Seq);
        if (s.Seq < highestSeq) return new(ReleaseVerdict.Rollback, s.Tag, s.Seq);
        if (TrustChain.Walk(own, s.TrustHash, chainJson) is not { } reached || reached.Seq < chainSeq
            || chainHash is not null && !reached.Lists.Contains(chainHash, StringComparer.Ordinal))
            return new(ReleaseVerdict.BrokenChain, s.Tag, s.Seq);
        var list = reached.List;
        if (list.Votes(s.Bytes(), manifest.Signatures) < list.Need) return new(ReleaseVerdict.NotEnoughSignatures, s.Tag, s.Seq);
        if (expectedTag is not null && (component is null ? NormalizeTag(expectedTag) : expectedTag) != s.Tag) return new(ReleaseVerdict.WrongTag, s.Tag, s.Seq);
        return new(ReleaseVerdict.Valid, s.Tag, s.Seq) { ChainSeq = reached.Seq, ChainHash = reached.Seq > 0 ? list.Hash : null };
    }

    /// <summary>
    /// Full check of a downloaded package: SHA256SUMS.txt and SHA256SUMS.txt.sigs (and trust-chain.json, if the release
    /// has one) next to it or given, a valid release, and the package's name and hash listed.
    /// </summary>
    public static async Task<ReleaseCheck> VerifyPackageAsync(string packagePath, string? expectedTag = null, string? sumsPath = null,
        string? signaturePath = null, string? chainPath = null, TrustList? own = null, long highestSeq = 0, string? component = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? ".";
        sumsPath ??= Path.Combine(dir, SumsFileName);
        signaturePath ??= Path.Combine(dir, SignatureFileName);
        chainPath ??= Path.Combine(dir, ChainFileName);
        if (!File.Exists(sumsPath) || !File.Exists(signaturePath)) return new(ReleaseVerdict.Unsigned, null, 0);

        var sums = await File.ReadAllBytesAsync(sumsPath, ct);
        var chain = File.Exists(chainPath) ? await File.ReadAllTextAsync(chainPath, ct) : null;
        var check = Verify(await File.ReadAllTextAsync(signaturePath, ct), sums, chain, expectedTag, own, highestSeq, component);
        if (check.Verdict != ReleaseVerdict.Valid) return check;
        return await CheckListedAsync(packagePath, Path.GetFileName(packagePath), sums, ct) ? check
            : check with { Verdict = ParseSums(Encoding.UTF8.GetString(sums)).ContainsKey(Path.GetFileName(packagePath)) ? ReleaseVerdict.HashMismatch : ReleaseVerdict.NotListed };
    }

    /// <summary>Whether the file at <paramref name="path"/> has the SHA-256 that <paramref name="sums"/> lists for <paramref name="name"/>.</summary>
    public static async Task<bool> CheckListedAsync(string path, string name, ReadOnlyMemory<byte> sums, CancellationToken ct = default)
    {
        if (!ParseSums(Encoding.UTF8.GetString(sums.Span)).TryGetValue(name, out var expected)) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) == expected;
    }

    /// <summary>Writes SHA256SUMS.txt text for files (sorted by name, "hash  name" per line, like sha256sum).</summary>
    public static string BuildSums(IEnumerable<string> files)
    {
        var lines = files
            .Select(f => (Name: Path.GetFileName(f), Path: f))
            .Where(f => f.Name is not SumsFileName and not SignatureFileName and not ChainFileName)
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

    internal static bool IsHex(string? s, int length) => s is not null && s.Length == length && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>What a release signature vouches for.</summary>
/// <param name="Tag">Version tag, for example v0.9.0.</param>
/// <param name="Seq">Release sequence number, one more for every release.</param>
/// <param name="Commit">Full 40-character commit id the release was built from.</param>
/// <param name="SumsHash">SHA-256 (hex) of SHA256SUMS.txt.</param>
/// <param name="TrustHash">Hash of the trustee list built into this release (<see cref="TrustList.Hash"/>).</param>
/// <param name="LogHead">Head hash of the release log (privacy plan item 5); 64 zeros until the log exists.</param>
/// <param name="Component">
/// Null for a Jay release. For a build of a component Jay ships from its own fork (see <see cref="Components"/>), its
/// name: the signed bytes then start with <c>jay-component-v1</c> and the name, so a component signature and a Jay
/// release signature can never be swapped for each other. The tag is the component's own (for example <c>jay-v2.6.4-1</c>).
/// </param>
public sealed record ReleaseStatement(string Tag, long Seq, string Commit, string SumsHash, string TrustHash, string LogHead, string? Component = null)
{
    public static readonly string NoLog = new('0', 64);

    /// <summary>Components whose builds the trustees sign: EasyTier, from Jay's hardened fork.</summary>
    public static readonly IReadOnlySet<string> Components = new HashSet<string>(StringComparer.Ordinal) { "easytier" };

    public byte[] Bytes() => Encoding.UTF8.GetBytes(string.Concat(
        Component is null ? "jay-release-v2\n" : "jay-component-v1\n" + Component + "\n",
        Tag, "\n", Seq.ToString(CultureInfo.InvariantCulture), "\n", Commit, "\n", SumsHash, "\n", TrustHash, "\n", LogHead, "\n"));

    public bool IsValid => Tag.Length is > 1 and <= 64 && Tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
        && (Component is null ? Tag == ReleaseSignature.NormalizeTag(Tag) : Components.Contains(Component))
        && Seq >= 1 && ReleaseSignature.IsHex(Commit, 40) && ReleaseSignature.IsHex(SumsHash, 64) && ReleaseSignature.IsHex(TrustHash, 64) && ReleaseSignature.IsHex(LogHead, 64);
}

/// <summary>One trustee's vote: their key id and both signatures (the full public key comes from the trustee list).</summary>
public sealed record QuorumSignature(byte[] KeyId, byte[] EdSignature, byte[] MlDsaSignature)
{
    public static QuorumSignature Of(TrustSignature s) => new(s.Key, s.EdSignature, s.MlDsaSignature);

    public JsonObject ToJson() => new()
    {
        ["k"] = Convert.ToBase64String(KeyId),
        ["ed"] = Convert.ToBase64String(EdSignature),
        ["ml"] = Convert.ToBase64String(MlDsaSignature),
    };

    public static QuorumSignature? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var k = Convert.FromBase64String(o["k"]?.GetValue<string>() ?? "");
        var ed = Convert.FromBase64String(o["ed"]?.GetValue<string>() ?? "");
        var ml = Convert.FromBase64String(o["ml"]?.GetValue<string>() ?? "");
        return k.Length == 32 && ed.Length == TrustSignature.EdSignatureSize && ml.Length == TrustSignature.MlDsaSignatureSize ? new(k, ed, ml) : null;
    }
}

/// <summary>
/// SHA256SUMS.txt.sigs: <c>{"v":2,"tag","seq","commit","sums","trust","log","sigs":[{"k","ed","ml"}…]}</c>, plus
/// <c>"component"</c> for a component build (absent for a Jay release).
/// </summary>
public sealed record ReleaseManifest(ReleaseStatement Statement, IReadOnlyList<QuorumSignature> Signatures)
{
    private static readonly HashSet<string> Fields = ["v", "component", "tag", "seq", "commit", "sums", "trust", "log", "sigs"];

    public string ToJson()
    {
        var o = new JsonObject { ["v"] = ReleaseSignature.Version };
        if (Statement.Component is { } component) o["component"] = component;
        o["tag"] = Statement.Tag;
        o["seq"] = Statement.Seq;
        o["commit"] = Statement.Commit;
        o["sums"] = Statement.SumsHash;
        o["trust"] = Statement.TrustHash;
        o["log"] = Statement.LogHead;
        o["sigs"] = new JsonArray([.. Signatures.Select(s => (JsonNode)s.ToJson())]);
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }


    /// <summary>Reads the file. Unknown fields, a wrong version or a malformed entry: null. Doesn't verify signatures.</summary>
    public static ReleaseManifest? Parse(string json)
    {
        try
        {
            if (json.Length > 128 * 1024 || JsonNode.Parse(json) is not JsonObject o || o.Any(p => !Fields.Contains(p.Key))) return null;
            if (o["v"]?.GetValue<int>() != ReleaseSignature.Version) return null;
            var statement = new ReleaseStatement(o["tag"]?.GetValue<string>() ?? "", o["seq"]?.GetValue<long>() ?? 0, o["commit"]?.GetValue<string>() ?? "",
                o["sums"]?.GetValue<string>() ?? "", o["trust"]?.GetValue<string>() ?? "", o["log"]?.GetValue<string>() ?? "",
                o["component"] is { } c ? c.GetValue<string>() : null);
            if (!statement.IsValid || o["sigs"] is not JsonArray sigs || sigs.Count > ReleaseSignature.MaxSignatures) return null;
            var list = new List<QuorumSignature>();
            foreach (var entry in sigs)
            {
                if (QuorumSignature.FromJson(entry) is not { } s || list.Any(x => x.KeyId.AsSpan().SequenceEqual(s.KeyId))) return null;
                list.Add(s);
            }
            return new ReleaseManifest(statement, list);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return null; }
    }
}

/// <summary>
/// A trustee list: the trustees' full public keys (Ed25519 ‖ ML-DSA-65) and how many must sign. Its
/// <see cref="Hash"/> names it in release signatures and in the trust chain.
/// </summary>
public sealed class TrustList
{
    public TrustList(IReadOnlyList<byte[]> keys, int need)
    {
        Keys = keys;
        Need = need;
        Ids = [.. keys.Select(k => TrustSigner.IdOfPublicKey(k))];
        var lines = new StringBuilder("jay-trust-list-v1\n").Append(need.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var k in keys.OrderBy(k => Convert.ToHexStringLower(TrustSigner.IdOfPublicKey(k)), StringComparer.Ordinal))
            lines.Append(Convert.ToBase64String(k)).Append('\n');
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(lines.ToString())));
    }

    /// <summary>The release list built into this Jay (<see cref="JayTrust.ReleaseKeys"/>, <see cref="JayTrust.ReleaseNeed"/>).</summary>
    public static TrustList Jay => new([.. JayTrust.ReleaseKeys.Select(Convert.FromBase64String)], JayTrust.ReleaseNeed);

    public IReadOnlyList<byte[]> Keys { get; }
    public IReadOnlyList<byte[]> Ids { get; }
    public int Need { get; }

    /// <summary>SHA-256 (hex) of "jay-trust-list-v1\n", the need, and the keys in id order, one base64 key per line.</summary>
    public string Hash { get; }

    /// <summary>
    /// How many distinct listed trustees signed <paramref name="message"/> with both algorithms. A signature from a key
    /// not on the list, or with one algorithm wrong, is not a vote; a key signing twice counts once.
    /// </summary>
    public int Votes(byte[] message, IEnumerable<QuorumSignature> signatures)
    {
        var counted = new HashSet<int>();
        foreach (var s in signatures)
        {
            var index = -1;
            for (var i = 0; i < Ids.Count; i++) if (CryptographicOperations.FixedTimeEquals(Ids[i], s.KeyId)) index = i;
            if (index < 0 || counted.Contains(index)) continue;
            var key = Keys[index];
            var full = new TrustSignature(key[..TrustSignature.EdKeySize], key[TrustSignature.EdKeySize..], s.EdSignature, s.MlDsaSignature);
            if (full.Verify(message)) counted.Add(index);
        }
        return counted.Count;
    }
}

/// <summary>
/// trust-chain.json: every change of the trustee list, each signed by enough of the list it replaces, so a Jay built
/// with an old list can follow it to the list a new release names (privacy plan item 3).
/// <para>
/// A JSON array of <c>{"from": old list hash, "keys": [new full public keys, base64], "need", "seq", "sigs": [{"k","ed","ml"}…]}</c>.
/// Signed bytes: <c>jay-trust-change-v1</c>, from, new list hash, seq, each followed by "\n".
/// Losing two of three keys can't be repaired this way: old installs then have to reinstall.
/// </para>
/// </summary>
public static class TrustChain
{
    public const int MaxEntries = 32;

    public static byte[] ChangeBytes(string from, string to, long seq) =>
        Encoding.UTF8.GetBytes($"jay-trust-change-v1\n{from}\n{to}\n{seq.ToString(CultureInfo.InvariantCulture)}\n");

    /// <summary>
    /// The list named <paramref name="targetHash"/>, reached from <paramref name="own"/> through the chain. Null when the
    /// chain is missing a link, a link isn't signed by enough of the list before it, its seq goes backwards along the
    /// chain, or the list reached was introduced before <paramref name="minChainSeq"/> or not through the list named
    /// <paramref name="throughHash"/> (a change this Jay already followed retired it).
    /// </summary>
    public static TrustList? Resolve(TrustList own, string targetHash, string? chainJson, long minChainSeq = 0, string? throughHash = null) =>
        Walk(own, targetHash, chainJson) is { } reached && reached.Seq >= minChainSeq
        && (throughHash is null || reached.Lists.Contains(throughHash, StringComparer.Ordinal)) ? reached.List : null;

    /// <summary>
    /// Follows the chain from <paramref name="own"/> to the list named <paramref name="targetHash"/>. Returns that list,
    /// the seq of the last change followed (0 when it is <paramref name="own"/> itself) and the hashes of every list on the
    /// way (<paramref name="own"/> first, the list reached last), or null when the chain doesn't get there. Seqs only have to grow along the chain; they are not compared with release seqs, so a release made after a
    /// change still verifies on every later check (rollback of releases is <see cref="ReleaseSignature.Verify"/>'s job).
    /// </summary>
    public static (TrustList List, long Seq, IReadOnlyList<string> Lists)? Walk(TrustList own, string targetHash, string? chainJson)
    {
        if (own.Hash == targetHash) return (own, 0, [own.Hash]);
        if (chainJson is null || ParseEntries(chainJson) is not { } entries) return null;
        var current = own;
        var lists = new List<string> { own.Hash };
        long lastSeq = 0, reached = 0;
        for (var step = 0; step < entries.Count && current.Hash != targetHash; step++)
        {
            var link = entries.FirstOrDefault(e => e.From == current.Hash);
            if (link is null || link.Seq < lastSeq) return null;
            var next = new TrustList(link.Keys, link.Need);
            if (current.Votes(ChangeBytes(link.From, next.Hash, link.Seq), link.Signatures) < current.Need) return null;
            current = next;
            lists.Add(next.Hash);
            reached = link.Seq;
            lastSeq = link.Seq + 1;
        }
        return current.Hash == targetHash ? (current, reached, lists) : null;
    }

    /// <summary>Adds a signature to the change from <paramref name="from"/> to the given list, creating the entry if needed.</summary>
    public static string Cosign(string? chainJson, TrustList from, IReadOnlyList<byte[]> keys, int need, long seq, TrustSigner signer)
    {
        var entries = chainJson is null ? [] : ParseEntries(chainJson) ?? throw new InvalidDataException("trust-chain.json is not valid");
        var next = new TrustList(keys, need);
        if (need < 1 || need > keys.Count) throw new InvalidDataException("need must be between 1 and the number of keys");
        var index = entries.FindIndex(e => e.From == from.Hash);
        var entry = index >= 0 ? entries[index] : new Entry(from.Hash, keys, need, seq, []);
        if (index >= 0 && (new TrustList(entry.Keys, entry.Need).Hash != next.Hash || entry.Seq != seq))
            throw new InvalidDataException("the chain already has a different change from this list");
        if (entry.Signatures.Any(s => s.KeyId.AsSpan().SequenceEqual(signer.Id))) throw new InvalidDataException("this key already signed");
        if (!from.Ids.Any(id => id.AsSpan().SequenceEqual(signer.Id))) throw new InvalidDataException("only a trustee on the old list can sign this change");
        entry = entry with { Signatures = [.. entry.Signatures, QuorumSignature.Of(signer.Sign(ChangeBytes(from.Hash, next.Hash, seq)))] };
        if (index >= 0) entries[index] = entry; else entries.Add(entry);
        return new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["from"] = e.From,
            ["keys"] = new JsonArray([.. e.Keys.Select(k => (JsonNode)JsonValue.Create(Convert.ToBase64String(k)))]),
            ["need"] = e.Need,
            ["seq"] = e.Seq,
            ["sigs"] = new JsonArray([.. e.Signatures.Select(s => (JsonNode)s.ToJson())]),
        })]).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    internal sealed record Entry(string From, IReadOnlyList<byte[]> Keys, int Need, long Seq, IReadOnlyList<QuorumSignature> Signatures);

    internal static List<Entry>? ParseEntries(string json)
    {
        try
        {
            if (json.Length > 1024 * 1024 || JsonNode.Parse(json) is not JsonArray array || array.Count > MaxEntries) return null;
            var entries = new List<Entry>();
            foreach (var node in array)
            {
                if (node is not JsonObject o || o["keys"] is not JsonArray keys || o["sigs"] is not JsonArray sigs) return null;
                var from = o["from"]?.GetValue<string>();
                var need = o["need"]?.GetValue<int>() ?? 0;
                var seq = o["seq"]?.GetValue<long>() ?? 0;
                var list = keys.Select(k => Convert.FromBase64String(k?.GetValue<string>() ?? "")).ToList();
                if (!ReleaseSignature.IsHex(from, 64) || list.Count is 0 or > 8 || list.Any(k => k.Length != TrustSigner.PublicKeySize)
                    || need < 1 || need > list.Count || seq < 1) return null;
                var signatures = sigs.Select(QuorumSignature.FromJson).ToList();
                if (signatures.Any(s => s is null)) return null;
                entries.Add(new Entry(from!, list, need, seq, signatures!));
            }
            return entries;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return null; }
    }
}

public enum ReleaseVerdict
{
    /// <summary>Signed by enough trustees with both algorithms, for the expected tag, and the package hash matches.</summary>
    Valid,
    /// <summary>No SHA256SUMS.txt / .sigs next to the package (older releases, or not signed yet).</summary>
    Unsigned,
    /// <summary>This Jay has no trustee keys built in yet, so it can't check anything.</summary>
    NoTrustedKey,
    Malformed,
    /// <summary>The signature file is for a different SHA256SUMS.txt.</summary>
    SumsMismatch,
    /// <summary>Older than a release this Jay already accepted.</summary>
    Rollback,
    /// <summary>The release names a trustee list this Jay can't reach through a valid trust chain.</summary>
    BrokenChain,
    /// <summary>Fewer valid trustee votes than the list needs.</summary>
    NotEnoughSignatures,
    /// <summary>A valid signature for a different version (e.g. an old release passed off as new).</summary>
    WrongTag,
    NotListed,
    HashMismatch,
    /// <summary>A valid signature for something else: a component build offered as a Jay release, or the other way round.</summary>
    WrongComponent,
}

public sealed record ReleaseCheck(ReleaseVerdict Verdict, string? Tag, long Seq)
{
    public bool IsValid => Verdict == ReleaseVerdict.Valid;

    /// <summary>For a valid release: the seq of the last trustee change followed to reach its list (0 for the built-in list).</summary>
    public long ChainSeq { get; init; }

    /// <summary>For a valid release reached through the chain: the hash of the list that last change introduced (null for the built-in list).</summary>
    public string? ChainHash { get; init; }
}
