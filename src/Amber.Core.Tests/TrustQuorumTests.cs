using System.Text;
using System.Text.Json.Nodes;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// Trust shared by several people: a network string or a directory certificate counts only when enough distinct trustees
/// signed it (2 of 3 here), so one stolen trustee key can't vouch for anything.
/// </summary>
public sealed class TrustQuorumTests
{
    private static readonly TrustSigner A = TrustSigner.Generate(out _);
    private static readonly TrustSigner B = TrustSigner.Generate(out _);
    private static readonly TrustSigner C = TrustSigner.Generate(out _);
    private static readonly TrustSigner Owner = TrustSigner.Generate(out _);
    private static readonly TrustSigner Outsider = TrustSigner.Generate(out _);
    private static readonly TrustQuorum TwoOfThree = new([A.Id, B.Id, C.Id], 2);

    private static NetworkInfo Info() =>
        new("nz-friends", "NZ friends", "jay-nz-friends", "nz-friends-public-v1", ["tcp://node1.example.org:11010"]);

    [Fact]
    public void QuorumCountsDistinctListedKeysOnly()
    {
        Assert.Equal(0, TwoOfThree.Count([Outsider.Id]));
        Assert.Equal(1, TwoOfThree.Count([A.Id, A.Id, Outsider.Id]));
        Assert.False(TwoOfThree.IsMet([A.Id, A.Id]));
        Assert.True(TwoOfThree.IsMet([C.Id, A.Id]));
        Assert.True(TwoOfThree.IsMet([A.Id, B.Id, C.Id]));
        // A quorum asking for nothing, more than it has, or with no keys is never met.
        Assert.False(new TrustQuorum([A.Id], 0).IsMet([A.Id]));
        Assert.False(new TrustQuorum([A.Id], 2).IsMet([A.Id]));
        Assert.False(TrustQuorum.None.IsMet([A.Id]));
        // A key listed twice still counts once.
        Assert.False(new TrustQuorum([A.Id, A.Id], 2).IsMet([A.Id]));
    }

    [Fact]
    public void NetworkIsReviewedOnlyWithTwoTrustees()
    {
        var owned = NetworkString.Create(Info(), Owner);
        Assert.False(NetworkString.Parse(owned, [TwoOfThree]).Reviewed);

        var one = NetworkString.Cosign(owned, A);
        var parsedOne = NetworkString.Parse(one, [TwoOfThree]);
        Assert.False(parsedOne.Reviewed);
        Assert.Equal(Owner.Id, parsedOne.SignerKey);

        var two = NetworkString.Cosign(one, C);
        var parsed = NetworkString.Parse(two, [TwoOfThree]);
        Assert.True(parsed.Reviewed);
        Assert.Equal(3, parsed.Signers.Count);
        // The owner stays the network's identity, so updates from the same owner still replace it.
        Assert.Equal(Owner.Id, parsed.SignerKey);
        Assert.Equal(Info().Nodes, parsed.Info.Nodes);

        // An outsider's extra signature doesn't help, and A and C aren't Jay's own trust keys.
        Assert.False(NetworkString.Parse(NetworkString.Cosign(one, Outsider), [TwoOfThree]).Reviewed);
        Assert.False(NetworkString.Parse(two).Reviewed);
    }

    [Fact]
    public void OneTrusteeCannotCountTwice()
    {
        var one = NetworkString.Create(Info(), Owner, A);
        Assert.Throws<InvalidDataException>(() => NetworkString.Cosign(one, A));

        // Hand-made string carrying A's signature twice: refused outright.
        var body = one["JAYNET2-".Length..].Split('.');
        var tail = NetworkString.FromBase64Url(body[1])!;
        var forged = "JAYNET2-" + body[0] + "." + NetworkString.Base64Url([.. tail, .. tail[TrustSignature.Size..]]);
        Assert.Null(NetworkString.TryParse(forged, [TwoOfThree]));

        // A trustee who is also the owner still counts once.
        Assert.False(NetworkString.Parse(NetworkString.Create(Info(), A), [TwoOfThree]).Reviewed);
        Assert.True(NetworkString.Parse(NetworkString.Create(Info(), A, B), [TwoOfThree]).Reviewed);
    }

    [Fact]
    public void ABadCoSignatureRejectsTheString()
    {
        var two = NetworkString.Create(Info(), Owner, A, B);
        var body = two["JAYNET2-".Length..].Split('.');
        var tail = NetworkString.FromBase64Url(body[1])!;
        tail[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => NetworkString.Parse("JAYNET2-" + body[0] + "." + NetworkString.Base64Url(tail), [TwoOfThree]));
        // Changing the content breaks every signature.
        var json = Encoding.UTF8.GetString(NetworkString.FromBase64Url(body[0])!).Replace("node1", "evil");
        Assert.Throws<InvalidDataException>(() => NetworkString.Parse("JAYNET2-" + NetworkString.Base64Url(Encoding.UTF8.GetBytes(json)) + "." + body[1], [TwoOfThree]));
        // Too many signatures.
        var signers = Enumerable.Range(0, NetworkString.MaxSignatures).Select(_ => TrustSigner.Generate(out byte[] _)).ToArray();
        var full = NetworkString.Create(Info(), Owner, [.. signers[..(NetworkString.MaxSignatures - 1)]]);
        Assert.Throws<InvalidDataException>(() => NetworkString.Cosign(full, signers[^1]));
    }

    [Fact]
    public void DirectoryCertificateNeedsTwoRoots()
    {
        using var identity = X25519KeyPair.Generate();
        var trust = new DirectoryTrust { Network = "jay-public-v1", Roots = TwoOfThree.Keys, RootsNeed = 2 };
        var one = DirectoryCertificate.Issue(B, identity.PublicKey, "jay-public-v1", DateTimeOffset.UtcNow.AddDays(30));
        Assert.False(trust.Accepts("10.235.0.1", identity.PublicKey, one));

        var two = DirectoryCertificate.Cosign(one, C);
        Assert.True(trust.Accepts("10.235.0.1", identity.PublicKey, two));
        Assert.Equal(2, DirectoryCertificate.Parse(two)!.Signatures.Count);

        // An outsider's co-signature doesn't count; a root can't sign twice.
        Assert.False(trust.Accepts("10.235.0.1", identity.PublicKey, DirectoryCertificate.Cosign(one, Outsider)));
        Assert.Throws<InvalidDataException>(() => DirectoryCertificate.Cosign(two, C));

        // One tampered signature rejects the whole certificate.
        var wire = (JsonObject)JsonNode.Parse(two)!;
        var sigs = (JsonArray)wire["sigs"]!;
        var s = Convert.FromBase64String(sigs[1]!.GetValue<string>());
        s[TrustSignature.EdKeySize + TrustSignature.MlDsaKeySize] ^= 1; // first byte of the Ed25519 signature
        sigs[1] = Convert.ToBase64String(s);
        Assert.Null(DirectoryCertificate.Parse(Encoding.UTF8.GetBytes(wire.ToJsonString())));

        // Still bound to the network and the directory key.
        Assert.False(trust.Accepts("10.235.0.1", X25519KeyPair.Generate().PublicKey, two));
        Assert.False(new DirectoryTrust { Network = "other", Roots = TwoOfThree.Keys, RootsNeed = 2 }.Accepts("10.235.0.1", identity.PublicKey, two));
    }

    [Fact]
    public void NetworkStringCarriesHowManyRootsItNeeds()
    {
        var info = Info() with { DirectoryRoots = [A.Id, B.Id, C.Id], DirectoryRootsNeed = 2 };
        var parsed = NetworkString.Parse(NetworkString.Create(info, Owner), []);
        Assert.Equal(2, parsed.ToProfile().DirectoryRootsNeed);
        Assert.Equal(3, parsed.ToProfile().DirectoryRoots.Count);
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(info with { DirectoryRootsNeed = 4 }, Owner));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(info with { DirectoryRootsNeed = 0 }, Owner));
    }

    [Fact]
    public void OnlyTheSelfCheckCanPinABuiltInDirectory()
    {
        // Outside a GUI self-check process the built-in network pins no directory key and refuses to pin one.
        var before = Environment.GetEnvironmentVariable("AMBER_SELFTEST");
        try
        {
            Environment.SetEnvironmentVariable("AMBER_SELFTEST", null);
            Assert.Throws<InvalidOperationException>(() => PublicNetworkProfile.PinSelfTestDirectory(new byte[32]));
            Assert.Empty(PublicNetworkProfile.Jay().DirectoryKeys);
        }
        finally { Environment.SetEnvironmentVariable("AMBER_SELFTEST", before); }
    }

    [Fact]
    public void APinnedKeyPassesTheStrictBuiltInDirectoryAndNoOtherDoes()
    {
        // What the self-check's pin amounts to, without touching the static pin: the built-in roots plus one pinned key.
        var jay = PublicNetworkProfile.Jay();
        using var pinned = X25519KeyPair.Generate();
        using var other = X25519KeyPair.Generate();
        var trust = new DirectoryTrust { Network = jay.NetworkName, Pinned = [pinned.PublicKey], Roots = jay.DirectoryRoots, RootsNeed = jay.DirectoryRootsNeed };
        Assert.True(trust.Accepts("127.0.0.1", pinned.PublicKey, []));
        Assert.False(trust.Accepts("127.0.0.1", other.PublicKey, []));
    }

    [Fact]
    public void BuiltInNetworkUsesTheJayTrustees()
    {
        var jay = PublicNetworkProfile.Jay();
        Assert.Equal(JayTrust.Need, jay.DirectoryRootsNeed);
        Assert.Equal(JayTrust.Keys.Count, jay.DirectoryRoots.Count);
        Assert.Equal(2, JayTrust.Need);
    }

    [Fact]
    public void BuiltInNetworkDirectoryNeedsATrusteeCertificate()
    {
        // With trust keys built in, the built-in network's directory is no longer trusted on first use: it needs a
        // certificate both trustees signed. Without one, or with signatures from anyone else, it is refused, even
        // when this address was remembered under trust on first use before the upgrade.
        var jay = PublicNetworkProfile.Jay();
        using var identity = X25519KeyPair.Generate();
        var dir = Directory.CreateTempSubdirectory("jay-dirtrust-");
        try
        {
            var memory = new DirectoryKeyMemory(Path.Combine(dir.FullName, DirectoryKeyMemory.FileName));
            memory.Remember(jay.NetworkName, "10.235.0.1", identity.PublicKey);
            var trust = new DirectoryTrust { Network = jay.NetworkName, Roots = jay.DirectoryRoots, RootsNeed = jay.DirectoryRootsNeed, Memory = memory };

            // Control: without roots the same memory still accepts the remembered key, so the refusals below come from strict mode.
            Assert.True(new DirectoryTrust { Network = jay.NetworkName, Memory = memory }.Accepts("10.235.0.1", identity.PublicKey, []));
            Assert.True(trust.IsStrict);
            Assert.False(trust.Accepts("10.235.0.1", identity.PublicKey, []));
            Assert.False(trust.Accepts("10.235.0.2", identity.PublicKey, []));
            var one = DirectoryCertificate.Issue(A, identity.PublicKey, jay.NetworkName, DateTimeOffset.UtcNow.AddDays(30));
            Assert.False(trust.Accepts("10.235.0.1", identity.PublicKey, one));
            Assert.False(trust.Accepts("10.235.0.1", identity.PublicKey, DirectoryCertificate.Cosign(one, B)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void BothAlgorithmsMustVerify()
    {
        var message = "jay-test\n"u8.ToArray();
        var good = A.Sign(message);
        Assert.True(good.Verify(message));
        Assert.Equal(TrustSignature.Size, good.Encode().Length);
        Assert.Equal(A.Id, TrustSignature.Decode(good.Encode()).Key);

        // Ed25519 right, ML-DSA-65 wrong (and the other way round): not a signature.
        var badMlDsa = good with { MlDsaSignature = [.. good.MlDsaSignature[..^1], (byte)(good.MlDsaSignature[^1] ^ 1)] };
        Assert.False(badMlDsa.Verify(message));
        var badEd = good with { EdSignature = [(byte)(good.EdSignature[0] ^ 1), .. good.EdSignature[1..]] };
        Assert.False(badEd.Verify(message));
        // Someone else's ML-DSA half glued onto A's Ed25519 half: the id no longer matches A, and the halves don't verify.
        var mixed = good with { MlDsaKey = B.MlDsaKey };
        Assert.False(mixed.Verify(message));
        Assert.NotEqual(A.Id, mixed.Key);
    }

    [Fact]
    public void TrustKeysComeFromOneSeed()
    {
        var signer = TrustSigner.Generate(out var seed);
        var again = TrustSigner.FromSeed(Convert.ToBase64String(seed));
        Assert.Equal(signer.Id, again.Id);
        Assert.Equal(signer.PublicKey, again.PublicKey);
        Assert.Equal(TrustSigner.PublicKeySize, signer.PublicKey.Length);
        Assert.Equal(signer.Id, TrustSigner.IdOfPublicKey(signer.PublicKey));
        // The Ed25519 half is the same key a plain NetworkSigner makes from the seed.
        Assert.Equal(NetworkSigner.FromSeed(Convert.ToBase64String(seed)).PublicKey, signer.EdKey);
        Assert.Throws<InvalidDataException>(() => TrustSigner.FromSeed("not base64!"));
    }

    [Fact]
    public void KeyFilesAreSealedWithAPassphrase()
    {
        var cost = TrustKeyFile.Cost;
        TrustKeyFile.Cost = (1024, 1);
        try
        {
            TrustSigner.Generate(out var seed);
            var text = TrustKeyFile.Format(seed, "correct horse battery staple");
            Assert.True(TrustKeyFile.IsSealed(text));
            Assert.DoesNotContain(Convert.ToBase64String(seed), text);
            Assert.Equal(seed, TrustKeyFile.ReadSeed(text, () => "correct horse battery staple"));
            Assert.Throws<InvalidDataException>(() => TrustKeyFile.ReadSeed(text, () => "wrong"));
            // Damaged file: same refusal, nothing leaks.
            var damaged = text[..^6] + (text[^6] == 'A' ? 'B' : 'A') + text[^5..];
            Assert.Throws<InvalidDataException>(() => TrustKeyFile.ReadSeed(damaged, () => "correct horse battery staple"));
            // Two sealings of the same seed differ (fresh salt and nonce).
            Assert.NotEqual(text, TrustKeyFile.Format(seed, "correct horse battery staple"));

            // Unsealed files (test keys, older owner keys) still read, without asking.
            var plain = TrustKeyFile.Format(seed, null);
            Assert.False(TrustKeyFile.IsSealed(plain));
            Assert.Equal(seed, TrustKeyFile.ReadSeed(plain, () => throw new InvalidOperationException("asked")));
        }
        finally { TrustKeyFile.Cost = cost; }
    }

    [Fact]
    public void PaperBackupRoundTripsAndNamesTheBadLine()
    {
        TrustSigner.Generate(out var seed);
        var lines = PaperKey.ToLines(seed);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Matches("^([0-9A-HJKMNP-TV-Z]{4} ){6}[0-9A-HJKMNP-TV-Z]{2} - [0-9A-HJKMNP-TV-Z]{2}$", l));
        Assert.Equal(seed, PaperKey.FromLines(lines[0], lines[1]));
        // Case, spaces and the look-alikes O/I/L don't matter.
        Assert.Equal(seed, PaperKey.FromLines(lines[0].ToLowerInvariant().Replace(" ", ""), lines[1].Replace('0', 'O').Replace('1', 'I')));

        var typo = lines[1].ToCharArray();
        typo[0] = typo[0] == 'A' ? 'B' : 'A';
        var e = Assert.Throws<InvalidDataException>(() => PaperKey.FromLines(lines[0], new string(typo)));
        Assert.Contains("2", e.Message);
        Assert.Throws<InvalidDataException>(() => PaperKey.FromLines(lines[0][..^3], lines[1]));
    }

    [Fact]
    public void PaperFormatMatchesTheOfflineScripts()
    {
        // Same vector as paper.ps1 (seed bytes 0..31).
        var seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var lines = PaperKey.ToLines(seed);
        Assert.Equal(PaperVector, lines);
    }

    internal static readonly string[] PaperVector = ["000G 40R4 0M30 E209 185G R38E 1W - T7", "8124 GK2G AHC5 RR34 D1P7 0X3R FG - NK"];
}
