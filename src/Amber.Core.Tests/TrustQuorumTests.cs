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
    private static readonly NetworkSigner A = NetworkSigner.Generate(out _);
    private static readonly NetworkSigner B = NetworkSigner.Generate(out _);
    private static readonly NetworkSigner C = NetworkSigner.Generate(out _);
    private static readonly NetworkSigner Owner = NetworkSigner.Generate(out _);
    private static readonly NetworkSigner Outsider = NetworkSigner.Generate(out _);
    private static readonly TrustQuorum TwoOfThree = new([A.PublicKey, B.PublicKey, C.PublicKey], 2);

    private static NetworkInfo Info() =>
        new("nz-friends", "NZ friends", "jay-nz-friends", "nz-friends-public-v1", ["tcp://node1.example.org:11010"]);

    [Fact]
    public void QuorumCountsDistinctListedKeysOnly()
    {
        Assert.Equal(0, TwoOfThree.Count([Outsider.PublicKey]));
        Assert.Equal(1, TwoOfThree.Count([A.PublicKey, A.PublicKey, Outsider.PublicKey]));
        Assert.False(TwoOfThree.IsMet([A.PublicKey, A.PublicKey]));
        Assert.True(TwoOfThree.IsMet([C.PublicKey, A.PublicKey]));
        Assert.True(TwoOfThree.IsMet([A.PublicKey, B.PublicKey, C.PublicKey]));
        // A quorum asking for nothing, more than it has, or with no keys is never met.
        Assert.False(new TrustQuorum([A.PublicKey], 0).IsMet([A.PublicKey]));
        Assert.False(new TrustQuorum([A.PublicKey], 2).IsMet([A.PublicKey]));
        Assert.False(TrustQuorum.None.IsMet([A.PublicKey]));
        // A key listed twice still counts once.
        Assert.False(new TrustQuorum([A.PublicKey, A.PublicKey], 2).IsMet([A.PublicKey]));
    }

    [Fact]
    public void NetworkIsReviewedOnlyWithTwoTrustees()
    {
        var owned = NetworkString.Create(Info(), Owner);
        Assert.False(NetworkString.Parse(owned, [TwoOfThree]).Reviewed);

        var one = NetworkString.Cosign(owned, A);
        var parsedOne = NetworkString.Parse(one, [TwoOfThree]);
        Assert.False(parsedOne.Reviewed);
        Assert.Equal(Owner.PublicKey, parsedOne.SignerKey);

        var two = NetworkString.Cosign(one, C);
        var parsed = NetworkString.Parse(two, [TwoOfThree]);
        Assert.True(parsed.Reviewed);
        Assert.Equal(3, parsed.Signers.Count);
        // The owner stays the network's identity, so updates from the same owner still replace it.
        Assert.Equal(Owner.PublicKey, parsed.SignerKey);
        Assert.Equal(Info().Nodes, parsed.Info.Nodes);

        // An outsider's extra signature doesn't help, and the trustees' keys are still empty in Jay itself.
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
        var signers = Enumerable.Range(0, NetworkString.MaxSignatures).Select(_ => NetworkSigner.Generate(out byte[] _)).ToArray();
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
        var s = Convert.FromBase64String(sigs[1]!["s"]!.GetValue<string>());
        s[0] ^= 1;
        sigs[1]!["s"] = Convert.ToBase64String(s);
        Assert.Null(DirectoryCertificate.Parse(Encoding.UTF8.GetBytes(wire.ToJsonString())));

        // Still bound to the network and the directory key.
        Assert.False(trust.Accepts("10.235.0.1", X25519KeyPair.Generate().PublicKey, two));
        Assert.False(new DirectoryTrust { Network = "other", Roots = TwoOfThree.Keys, RootsNeed = 2 }.Accepts("10.235.0.1", identity.PublicKey, two));
    }

    [Fact]
    public void NetworkStringCarriesHowManyRootsItNeeds()
    {
        var info = Info() with { DirectoryRoots = [A.PublicKey, B.PublicKey, C.PublicKey], DirectoryRootsNeed = 2 };
        var parsed = NetworkString.Parse(NetworkString.Create(info, Owner), []);
        Assert.Equal(2, parsed.ToProfile().DirectoryRootsNeed);
        Assert.Equal(3, parsed.ToProfile().DirectoryRoots.Count);
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(info with { DirectoryRootsNeed = 4 }, Owner));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(info with { DirectoryRootsNeed = 0 }, Owner));
    }

    [Fact]
    public void BuiltInNetworkUsesTheJayTrustees()
    {
        var jay = PublicNetworkProfile.Jay();
        Assert.Equal(JayTrust.Need, jay.DirectoryRootsNeed);
        Assert.Equal(JayTrust.Keys.Length, jay.DirectoryRoots.Count);
        Assert.Equal(2, JayTrust.Need);
    }
}
