using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Launch;
using Amber.Core.Multiplayer.Public;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>Privacy plan items 2 and 3: releases need 2 of 3 trustees with both algorithms; list changes are signed by the old list.</summary>
public sealed class ReleaseSignatureTests : IDisposable
{
    private static readonly byte[][] Seeds = [.. Enumerable.Range(0, 5).Select(_ => RandomNumberGenerator.GetBytes(32))];
    private static readonly TrustSigner[] T = [.. Seeds.Select(s => TrustSigner.FromSeed(Convert.ToBase64String(s)))];
    /// <summary>Trustees 0, 1, 2; 3 and 4 are outsiders (or the next list).</summary>
    private static readonly TrustList Own = new([T[0].PublicKey, T[1].PublicKey, T[2].PublicKey], 2);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-relsig-" + Guid.NewGuid().ToString("N"));

    public ReleaseSignatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static readonly byte[] SumsBytes = Encoding.UTF8.GetBytes(new string('a', 64) + "  Jay-win-x64.zip\n");

    private static ReleaseStatement Statement(string tag = "v0.9.0", long seq = 5, byte[]? sums = null, string? trust = null) =>
        new(tag, seq, new string('c', 40), Convert.ToHexStringLower(SHA256.HashData(sums ?? SumsBytes)), trust ?? Own.Hash, ReleaseStatement.NoLog);

    private static string Signed(ReleaseStatement statement, params TrustSigner[] signers) =>
        signers.Aggregate(ReleaseSignature.Create(statement), (json, s) => ReleaseSignature.Cosign(json, s));

    private static ReleaseVerdict Check(string manifest, byte[]? sums = null, string? chain = null, string? tag = "v0.9.0", long highest = 0) =>
        ReleaseSignature.Verify(manifest, sums ?? SumsBytes, chain, tag, Own, highest).Verdict;

    [Fact]
    public void TwoOfThreeTrusteesPass()
    {
        Assert.Equal(ReleaseVerdict.Valid, Check(Signed(Statement(), T[0], T[2])));
        Assert.Equal(ReleaseVerdict.Valid, Check(Signed(Statement(), T[1], T[2], T[0])));
    }

    [Fact]
    public void OneVoteIsNotEnough()
    {
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(), T[1])));
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement())));
    }

    [Fact]
    public void OutsidersDoNotCount()
    {
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(), T[0], T[3])));
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(), T[3], T[4])));
    }

    [Fact]
    public void SameKeyTwiceCountsOnce()
    {
        var message = Statement().Bytes();
        var vote = QuorumSignature.Of(T[0].Sign(message));
        var again = QuorumSignature.Of(T[0].Sign(message)); // ML-DSA is randomized: different bytes, same signer
        Assert.Equal(1, Own.Votes(message, [vote, again]));
        Assert.Throws<InvalidDataException>(() => ReleaseSignature.Cosign(Signed(Statement(), T[0]), T[0]));

        // A file carrying the same key twice is refused outright.
        var json = JsonNode.Parse(Signed(Statement(), T[0]))!.AsObject();
        json["sigs"]!.AsArray().Add(again.ToJson());
        Assert.Equal(ReleaseVerdict.Malformed, Check(json.ToJsonString()));
    }

    [Fact]
    public void BothAlgorithmsMustBeRight()
    {
        var message = Statement().Bytes();
        var good = QuorumSignature.Of(T[1].Sign(message));
        var badMl = good with { MlDsaSignature = QuorumSignature.Of(T[1].Sign(Statement(seq: 6).Bytes())).MlDsaSignature };
        var badEd = good with { EdSignature = QuorumSignature.Of(T[1].Sign(Statement(seq: 6).Bytes())).EdSignature };
        Assert.Equal(1, Own.Votes(message, [good]));
        Assert.Equal(0, Own.Votes(message, [badMl]));
        Assert.Equal(0, Own.Votes(message, [badEd]));

        var manifest = new ReleaseManifest(Statement(), [QuorumSignature.Of(T[0].Sign(message)), badMl]).ToJson();
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(manifest));
    }

    [Theory]
    [InlineData("tag", "v0.9.1")]
    [InlineData("seq", 6)]
    [InlineData("commit", "dddddddddddddddddddddddddddddddddddddddd")]
    [InlineData("trust", "1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("log", "2222222222222222222222222222222222222222222222222222222222222222")]
    public void ChangingAnySignedFieldFails(string field, object value)
    {
        var json = JsonNode.Parse(Signed(Statement(), T[0], T[1]))!.AsObject();
        json[field] = value is int i ? JsonValue.Create((long)i) : JsonValue.Create((string)value);
        var verdict = Check(json.ToJsonString(), tag: null);
        Assert.NotEqual(ReleaseVerdict.Valid, verdict);
    }

    [Fact]
    public void ChangingTheFileListFails()
    {
        var manifest = Signed(Statement(), T[0], T[1]);
        var other = Encoding.UTF8.GetBytes(new string('b', 64) + "  Jay-win-x64.zip\n");
        Assert.Equal(ReleaseVerdict.SumsMismatch, Check(manifest, other));

        // Even with "sums" edited to match, the signatures are over the old value.
        var json = JsonNode.Parse(manifest)!.AsObject();
        json["sums"] = Convert.ToHexStringLower(SHA256.HashData(other));
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(json.ToJsonString(), other));
    }

    [Fact]
    public void OlderThanAcceptedIsRefused()
    {
        var manifest = Signed(Statement(seq: 5), T[0], T[1]);
        Assert.Equal(ReleaseVerdict.Valid, Check(manifest, highest: 4));
        Assert.Equal(ReleaseVerdict.Valid, Check(manifest, highest: 5)); // checking the same release again
        Assert.Equal(ReleaseVerdict.Rollback, Check(manifest, highest: 6));
    }

    [Fact]
    public void WrongTagAndNoKeysAndUnknownFields()
    {
        var manifest = Signed(Statement(), T[0], T[1]);
        Assert.Equal(ReleaseVerdict.WrongTag, Check(manifest, tag: "0.9.1"));
        Assert.Equal(ReleaseVerdict.Valid, Check(manifest, tag: "0.9.0"));
        Assert.Equal(ReleaseVerdict.NoTrustedKey, ReleaseSignature.Verify(manifest, SumsBytes, null, null, new TrustList([], 1)).Verdict);

        var json = JsonNode.Parse(manifest)!.AsObject();
        json["note"] = "trust me";
        Assert.Equal(ReleaseVerdict.Malformed, Check(json.ToJsonString()));
        Assert.Equal(ReleaseVerdict.Malformed, Check("not json"));
    }

    [Fact]
    public void BuiltInKeysAreWellFormed()
    {
        // Each list: full hybrid public keys, no duplicates, enough of them for its quorum, and the two lists disjoint.
        foreach (var (keys, need) in new[] { (JayTrust.Keys, JayTrust.Need), (JayTrust.ReleaseKeys, JayTrust.ReleaseNeed) })
        {
            foreach (var key in keys) Assert.Equal(TrustSigner.PublicKeySize, Convert.FromBase64String(key).Length);
            Assert.Equal(keys.Length, keys.Distinct().Count());
            Assert.True(keys.Length == 0 || keys.Length >= need);
        }
        Assert.Empty(JayTrust.ReleaseKeys.Intersect(JayTrust.Keys));

        // The fingerprints printed when the keys were made (and written next to them in JayTrust): a swapped key fails here.
        string[] Prints(string[] keys) => [.. keys.Select(k => TrustCommands.Fingerprint(TrustSigner.IdOfPublicKey(Convert.FromBase64String(k))))];
        Assert.Equal(["9D47 BBDA 24BF 2956", "75CD 82DB E5DF 8E4D"], Prints(JayTrust.Keys));
        Assert.Equal([], Prints(JayTrust.ReleaseKeys));
        Assert.Equal(JayTrust.Keys.Length, JayTrust.Quorum.Keys.Count);
        Assert.True(JayTrust.IsNetworkKey(JayTrust.Quorum.Keys[0]));
        Assert.False(JayTrust.IsReleaseKey(JayTrust.Quorum.Keys[0]));
    }

    [Fact]
    public void ReleaseAndNetworkKeysAreSeparate()
    {
        // The release list is JayTrust.ReleaseKeys, not the keys that review networks.
        Assert.Equal(new TrustList([.. JayTrust.ReleaseKeys.Select(Convert.FromBase64String)], JayTrust.ReleaseNeed).Hash, TrustList.Jay.Hash);
        Assert.Empty(JayTrust.ReleaseKeys.Intersect(JayTrust.Keys));

        bool IsNetwork(byte[] id) => id.AsSpan().SequenceEqual(T[0].Id);
        bool IsRelease(byte[] id) => id.AsSpan().SequenceEqual(T[1].Id);
        Assert.Throws<InvalidOperationException>(() => TrustCommands.ForUse(T[0], release: true, IsNetwork, IsRelease));
        Assert.Throws<InvalidOperationException>(() => TrustCommands.ForUse(T[1], release: false, IsNetwork, IsRelease));
        Assert.Same(T[0], TrustCommands.ForUse(T[0], release: false, IsNetwork, IsRelease));
        Assert.Same(T[1], TrustCommands.ForUse(T[1], release: true, IsNetwork, IsRelease));
    }

    // ---- item 3: changing the trustee list ----

    private static readonly TrustList Next = new([T[0].PublicKey, T[1].PublicKey, T[3].PublicKey], 2);

    [Fact]
    public void NewListIsReachedThroughTheChain()
    {
        var chain = ReleaseSignature_Chain(Own, Next, 6, T[0], T[2]);
        var manifest = Signed(Statement(seq: 7, trust: Next.Hash), T[3], T[1]);
        Assert.Equal(ReleaseVerdict.Valid, Check(manifest, chain: chain, highest: 5));

        // Without the chain, or with the new list's own signers only, an old Jay can't follow.
        Assert.Equal(ReleaseVerdict.BrokenChain, Check(manifest));
        Assert.Equal(ReleaseVerdict.BrokenChain, Check(manifest, chain: ReleaseSignature_Chain(Own, Next, 6, T[0])));
        Assert.Equal(ReleaseVerdict.BrokenChain, Check(manifest, chain: ReleaseSignature_Chain(Own, Next, 6, T[0], T[3]))); // T3 isn't on the old list
        // Signed by trustees no longer on the new list: not enough.
        Assert.Equal(ReleaseVerdict.NotEnoughSignatures, Check(Signed(Statement(seq: 7, trust: Next.Hash), T[2], T[1]), chain: chain, highest: 5));
    }

    [Fact]
    public void ChainSteps()
    {
        var third = new TrustList([T[1].PublicKey, T[3].PublicKey, T[4].PublicKey], 2);
        var chain = ReleaseSignature_Chain(Own, Next, 6, T[0], T[1]);
        chain = ReleaseSignature_Chain(Next, third, 8, T[3], T[0], chain);
        Assert.Equal(third.Hash, TrustChain.Resolve(Own, third.Hash, chain)!.Hash);
        Assert.Equal(third.Hash, TrustChain.Resolve(Next, third.Hash, chain)!.Hash);
        // A change older than a release already accepted can't be replayed.
        Assert.Null(TrustChain.Resolve(Own, third.Hash, chain, highestSeq: 7));
        // Tampering with a link's keys breaks it.
        var json = JsonNode.Parse(chain)!.AsArray();
        json[0]!["keys"]![2] = Convert.ToBase64String(T[4].PublicKey);
        Assert.Null(TrustChain.Resolve(Own, Next.Hash, json.ToJsonString()));
        Assert.Null(TrustChain.Resolve(Own, Next.Hash, "[{}]"));
    }

    private static string ReleaseSignature_Chain(TrustList from, TrustList to, long seq, TrustSigner a, TrustSigner? b = null, string? chain = null)
    {
        Exception? refused = null;
        try { chain = TrustChain.Cosign(chain, from, to.Keys, to.Need, seq, a); }
        catch (InvalidDataException e) { refused = e; }
        if (b is not null)
        {
            try { chain = TrustChain.Cosign(chain, from, to.Keys, to.Need, seq, b); }
            catch (InvalidDataException e) { refused = e; }
        }
        // An outsider is refused when signing; build the bad link by hand so Resolve gets to judge it.
        if (refused is not null && b is not null && !from.Ids.Any(id => id.AsSpan().SequenceEqual(b.Id)))
        {
            var array = JsonNode.Parse(chain!)!.AsArray();
            array[^1]!["sigs"]!.AsArray().Add(QuorumSignature.Of(b.Sign(TrustChain.ChangeBytes(from.Hash, to.Hash, seq))).ToJson());
            chain = array.ToJsonString();
        }
        return chain!;
    }

    // ---- sums file, packages ----

    [Fact]
    public void SumsParsingRejectsTricks()
    {
        var text = new string('a', 64) + "  Jay.zip\n" + new string('b', 64) + " *other.zip\r\n"
                   + new string('c', 64) + "  dir/evil.zip\n" + new string('d', 64) + "  twice.zip\n" + new string('e', 64) + "  twice.zip\n" + "short  x\n";
        var sums = ReleaseSignature.ParseSums(text);
        Assert.Equal(new string('a', 64), sums["Jay.zip"]);
        Assert.Equal(new string('b', 64), sums["other.zip"]);
        Assert.False(sums.ContainsKey("dir/evil.zip"));
        Assert.False(sums.ContainsKey("twice.zip"));
    }

    private string Release(string dir, ReleaseStatement? statement = null, params TrustSigner[] signers)
    {
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "Jay-win-x64.zip");
        File.WriteAllText(zip, "pretend package");
        File.WriteAllText(Path.Combine(dir, "Jay-linux-x64.zip"), "other package");
        var sums = ReleaseSignature.BuildSums(Directory.GetFiles(dir));
        File.WriteAllText(Path.Combine(dir, ReleaseSignature.SumsFileName), sums);
        var s = statement ?? Statement(sums: Encoding.UTF8.GetBytes(sums));
        s = s with { SumsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sums))) };
        File.WriteAllText(Path.Combine(dir, ReleaseSignature.SignatureFileName), Signed(s, signers.Length > 0 ? signers : [T[0], T[1]]));
        return zip;
    }

    [Fact]
    public async Task PackageChecks()
    {
        var zip = Release(Path.Combine(_dir, "ok"));
        Assert.Equal(ReleaseVerdict.Valid, (await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", own: Own)).Verdict);

        File.WriteAllText(zip, "swapped");
        Assert.Equal(ReleaseVerdict.HashMismatch, (await ReleaseSignature.VerifyPackageAsync(zip, "v0.9.0", own: Own)).Verdict);

        var extra = Path.Combine(_dir, "ok", "Jay-extra.zip");
        File.WriteAllText(extra, "not listed");
        Assert.Equal(ReleaseVerdict.NotListed, (await ReleaseSignature.VerifyPackageAsync(extra, "v0.9.0", own: Own)).Verdict);

        var bare = Path.Combine(_dir, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "Jay.zip"), "x");
        Assert.Equal(ReleaseVerdict.Unsigned, (await ReleaseSignature.VerifyPackageAsync(Path.Combine(bare, "Jay.zip"), own: Own)).Verdict);
    }

    // ---- the app's update check against a local release feed ----

    private const string Feed = "https://releases.example.test/latest";

    private static (FakeHttp Http, string Json) FeedFor(string tag, Dictionary<string, string> files)
    {
        var http = new FakeHttp();
        var assets = new JsonArray();
        foreach (var (name, body) in files)
        {
            var url = $"https://releases.example.test/download/{tag}/{name}";
            http.On(url, body);
            assets.Add(new JsonObject { ["name"] = name, ["browser_download_url"] = url });
        }
        var json = new JsonObject { ["tag_name"] = tag, ["html_url"] = $"https://releases.example.test/{tag}", ["body"] = "notes", ["assets"] = assets }.ToJsonString();
        http.On(Feed, json);
        return (http, json);
    }

    private static Dictionary<string, string> Files(ReleaseStatement? statement = null, TrustSigner[]? signers = null, string? sumsOverride = null, string? chain = null, string package = "package bytes")
    {
        var sums = $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("package bytes")))}  Jay-win-x64.zip\n";
        var s = (statement ?? Statement(tag: "v1.0.0")) with { SumsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sums))) };
        var files = new Dictionary<string, string>
        {
            ["Jay-win-x64.zip"] = package,
            [ReleaseSignature.SumsFileName] = sumsOverride ?? sums,
            [ReleaseSignature.SignatureFileName] = Signed(s, signers ?? [T[0], T[1]]),
        };
        if (chain is not null) files[ReleaseSignature.ChainFileName] = chain;
        return files;
    }

    [Fact]
    public async Task UpdateCheckOffersOnlyTheGoodRelease()
    {
        var bad = new Dictionary<string, Dictionary<string, string>>
        {
            ["one signature"] = Files(signers: [T[0]]),
            ["outsider"] = Files(signers: [T[0], T[3]]),
            ["edited list"] = Files(sumsOverride: new string('f', 64) + "  Jay-win-x64.zip\n"),
            ["other version"] = Files(Statement(tag: "v0.9.9")),
            ["unknown trustee list"] = Files(Statement(tag: "v1.0.0", trust: Next.Hash), [T[0], T[1]]),
            ["no signature file"] = Files().Where(f => f.Key != ReleaseSignature.SignatureFileName).ToDictionary(),
        };
        foreach (var (why, files) in bad)
        {
            var dataDir = Path.Combine(_dir, "data-" + why.Replace(' ', '-'));
            var (http, _) = FeedFor("v1.0.0", files);
            var update = await new UpdateCheck(new HttpClient(http), dataDir, Own).CheckAsync(Feed, "0.9.0");
            Assert.NotNull(update);
            Assert.False(update!.IsVerified, why);
            Assert.Empty(update.Sums);
            Assert.False(File.Exists(Path.Combine(dataDir, UpdateCheck.StateFileName)), why);
        }

        var goodDir = Path.Combine(_dir, "data-good");
        var (good, _) = FeedFor("v1.0.0", Files());
        var checker = new UpdateCheck(new HttpClient(good), goodDir, Own);
        var ok = await checker.CheckAsync(Feed, "0.9.0");
        Assert.True(ok!.IsVerified);
        Assert.Equal("1.0.0", ok.Info.Version);
        Assert.Equal(5, checker.HighestSeq);

        // The package is kept only when its hash matches the signed list.
        var path = await checker.DownloadAsync(ok, "Jay-win-x64.zip", Path.Combine(_dir, "dl"));
        Assert.Equal("package bytes", File.ReadAllText(path));

        var (swapped, _) = FeedFor("v1.0.0", Files(package: "evil bytes"));
        var swappedCheck = new UpdateCheck(new HttpClient(swapped), Path.Combine(_dir, "data-swap"), Own);
        var swappedUpdate = await swappedCheck.CheckAsync(Feed, "0.9.0");
        Assert.True(swappedUpdate!.IsVerified); // the signed files are fine…
        await Assert.ThrowsAsync<InvalidDataException>(() => swappedCheck.DownloadAsync(swappedUpdate, "Jay-win-x64.zip", Path.Combine(_dir, "dl2")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "dl2"))); // …but the swapped package is not kept

        // Once seq 5 is accepted, an older signed release is a rollback.
        var (older, _) = FeedFor("v1.0.0", Files(Statement(tag: "v1.0.0", seq: 4)));
        var rollback = await new UpdateCheck(new HttpClient(older), goodDir, Own).CheckAsync(Feed, "0.9.0");
        Assert.Equal(ReleaseVerdict.Rollback, rollback!.Check.Verdict);
    }

    [Fact]
    public async Task UpdateCheckFollowsTheTrustChain()
    {
        var chain = ReleaseSignature_Chain(Own, Next, 6, T[1], T[2]);
        var (http, _) = FeedFor("v1.0.0", Files(Statement(tag: "v1.0.0", seq: 7, trust: Next.Hash), [T[3], T[0]], chain: chain));
        var update = await new UpdateCheck(new HttpClient(http), Path.Combine(_dir, "data-chain"), Own).CheckAsync(Feed, "0.9.0");
        Assert.True(update!.IsVerified);
    }

    [Fact]
    public async Task UpdateCheckWithoutFeedOrNewerVersionStaysQuiet()
    {
        var (http, _) = FeedFor("v1.0.0", Files());
        var checker = new UpdateCheck(new HttpClient(http), Path.Combine(_dir, "data-quiet"), Own);
        Assert.Null(await checker.CheckAsync(null, "0.9.0"));
        Assert.Empty(http.Requests);
        Assert.Null(await checker.CheckAsync(Feed, "1.0.0"));
        Assert.Single(http.Requests); // only the feed, no signature downloads
    }

    // ---- jay-server release commands ----

    private static string KeyFile(string dir, int i)
    {
        var path = Path.Combine(dir, $"t{i}.key");
        File.WriteAllText(path, Convert.ToBase64String(Seeds[i]) + "\n");
        return path;
    }

    [Fact]
    public void ServerCommandsPrepareCosignVerify()
    {
        var pkgDir = Path.Combine(_dir, "pkg");
        Directory.CreateDirectory(pkgDir);
        var zip = Path.Combine(pkgDir, "Jay-win-x64.zip");
        File.WriteAllText(zip, "package");
        var list = Path.Combine(_dir, "trustees.txt");
        File.WriteAllLines(list, ["# Jay trustees", .. Own.Keys.Select(Convert.ToBase64String)]);
        var sums = Path.Combine(pkgDir, ReleaseSignature.SumsFileName);
        var commit = new string('a', 40);

        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "sums", zip, "--out", sums]), new StringWriter()));
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "prepare", "--tag", "0.9.0", "--seq", "3", "--commit", commit, "--sums", sums, "--trust", list]), new StringWriter()));

        // The signer sees what they sign and must type the tag; refusing writes nothing.
        IReadOnlyList<string>? shown = null;
        string? code = null;
        Assert.Equal(3, Program.ReleaseCommand(Options.Parse(["release", "cosign", "--key", KeyFile(_dir, 0), "--sums", sums, "--trust", list]), new StringWriter(),
            (lines, _, expected) => { shown = lines; code = expected; return false; }));
        Assert.Equal("v0.9.0", code);
        var text = string.Join("\n", shown!);
        Assert.Contains(commit, text);
        Assert.Contains("Jay-win-x64.zip", text);
        Assert.Contains("还没有人", text);

        foreach (var i in new[] { 0, 2 })
            Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "cosign", "--key", KeyFile(_dir, i), "--sums", sums, "--trust", list]), new StringWriter(), (_, _, _) => true));

        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--tag", "v0.9.0", "--trust", list]), new StringWriter()));
        File.WriteAllText(zip, "swapped");
        Assert.Equal(2, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--tag", "v0.9.0", "--trust", list]), new StringWriter()));

        // A list that doesn't match the SHA256SUMS.txt on hand is refused before anything is shown.
        File.WriteAllText(sums, new string('0', 64) + "  Jay-win-x64.zip\n");
        Assert.Throws<InvalidDataException>(() => Program.ReleaseCommand(Options.Parse(["release", "cosign", "--key", KeyFile(_dir, 1), "--sums", sums, "--trust", list]), new StringWriter(), (_, _, _) => true));
    }

    [Fact]
    public void ServerCommandsSignAnEasyTierBuildAsAComponent()
    {
        var pkgDir = Path.Combine(_dir, "et");
        Directory.CreateDirectory(pkgDir);
        var zip = Path.Combine(pkgDir, "easytier-linux-x86_64-v2.6.4-jay-89e36e45d7e2.zip");
        File.WriteAllText(zip, "package");
        var list = Path.Combine(_dir, "trustees.txt");
        File.WriteAllLines(list, [.. Own.Keys.Select(Convert.ToBase64String)]);
        var sums = Path.Combine(pkgDir, ReleaseSignature.SumsFileName);
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "sums", zip, "--out", sums]), new StringWriter()));
        Assert.Throws<ArgumentException>(() => Program.ReleaseCommand(Options.Parse(["release", "prepare", "--component", "other", "--tag", "jay-v2.6.4-1",
            "--seq", "1", "--commit", new string('a', 40), "--sums", sums, "--trust", list]), new StringWriter()));
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "prepare", "--component", "easytier", "--tag", "jay-v2.6.4-1",
            "--seq", "1", "--commit", new string('a', 40), "--sums", sums, "--trust", list]), new StringWriter()));

        // The trustee is told this is not a Jay release, and types the component's own tag.
        IReadOnlyList<string>? shown = null;
        string? code = null;
        Program.ReleaseCommand(Options.Parse(["release", "cosign", "--key", KeyFile(_dir, 1), "--sums", sums, "--trust", list]), new StringWriter(),
            (lines, _, expected) => { shown = lines; code = expected; return false; });
        Assert.Equal("jay-v2.6.4-1", code);
        Assert.Contains("easytier", shown![0], StringComparison.Ordinal);
        foreach (var i in new[] { 1, 2 })
            Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "cosign", "--key", KeyFile(_dir, i), "--sums", sums, "--trust", list]), new StringWriter(), (_, _, _) => true));

        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--component", "easytier", "--tag", "jay-v2.6.4-1", "--trust", list]), new StringWriter()));
        // Checked as a Jay release it is refused, whatever the tag.
        var output = new StringWriter();
        Assert.Equal(2, Program.ReleaseCommand(Options.Parse(["release", "verify", zip, "--trust", list]), output));
        Assert.Contains("--component easytier", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ServerCommandTrustChange()
    {
        var from = Path.Combine(_dir, "old.txt");
        var to = Path.Combine(_dir, "new.txt");
        File.WriteAllLines(from, Own.Keys.Select(Convert.ToBase64String));
        File.WriteAllLines(to, Next.Keys.Select(Convert.ToBase64String));
        var chain = Path.Combine(_dir, ReleaseSignature.ChainFileName);
        string[] Args(int i) => ["release", "trust-change", "--key", KeyFile(_dir, i), "--from", from, "--to", to, "--need", "2", "--seq", "6", "--chain", chain];

        IReadOnlyList<string>? shown = null;
        Assert.Equal(3, Program.ReleaseCommand(Options.Parse(Args(0)), new StringWriter(), (lines, _, _) => { shown = lines; return false; }));
        var text = string.Join("\n", shown!);
        Assert.Contains("移除：" + TrustCommands.Fingerprint(T[2].Id), text);
        Assert.Contains("新加：" + TrustCommands.Fingerprint(T[3].Id), text);
        Assert.False(File.Exists(chain));

        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(Args(0)), new StringWriter(), (_, _, _) => true));
        Assert.Null(TrustChain.Resolve(Own, Next.Hash, File.ReadAllText(chain)));
        Assert.Equal(0, Program.ReleaseCommand(Options.Parse(Args(2)), new StringWriter(), (_, _, _) => true));
        Assert.Equal(Next.Hash, TrustChain.Resolve(Own, Next.Hash, File.ReadAllText(chain))!.Hash);
        // Someone not on the old list can't sign the change.
        Assert.Throws<InvalidDataException>(() => Program.ReleaseCommand(Options.Parse(Args(3)), new StringWriter(), (_, _, _) => true));
    }
}
