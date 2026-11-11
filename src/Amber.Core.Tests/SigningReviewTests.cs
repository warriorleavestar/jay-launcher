using System.Text;
using Amber.Core.Multiplayer.Public;
using Amber.Core.Multiplayer.Transport;
using Amber.Server;

namespace Amber.Core.Tests;

/// <summary>
/// Privacy plan item 1: a trustee sees every field of what they sign, and has to type a code from it, before
/// <c>jay-server network cosign</c> or <c>dir-cert</c> signs anything.
/// </summary>
public sealed class SigningReviewTests
{
    private static readonly TrustSigner Owner = TrustSigner.Generate(out _);
    private static readonly TrustSigner Trustee = TrustSigner.Generate(out _);

    private static NetworkInfo FullInfo()
    {
        using var dir = X25519KeyPair.Generate();
        return new NetworkInfo("nz-friends", "NZ friends", "jay-nz-friends", "nz-friends-public-v1",
            ["tcp://node1.example.org:11010", "udp://node2.example.org:11010"], 7, DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            "https://example.org/jay", new NetworkConfigRef("https://example.org/jay/config.json", new string('b', 64)),
            [dir.PublicKey], [Trustee.Id], 1);
    }

    [Fact]
    public void ReviewShowsEveryField()
    {
        var info = FullInfo();
        var text = NetworkString.Create(info, Owner);
        var shown = string.Join("\n", TrustCommands.DescribeNetwork(text, null));
        foreach (var value in new[] { info.Id, info.Name, info.NetworkName, info.Secret, info.Nodes[0], info.Nodes[1], "7", "2030-03-17",
                     info.Home!, info.Config!.Url, info.Config.Sha256, Convert.ToBase64String(info.DirectoryKeys![0]), Convert.ToBase64String(info.DirectoryRoots![0]),
                     TrustCommands.Fingerprint(Owner.Id) })
            Assert.Contains(value, shown);
        Assert.Contains("SHA-256", shown);
    }

    [Fact]
    public void ReviewAgainstThePreviousVersionListsOnlyChanges()
    {
        var before = NetworkString.Create(FullInfo() with { Seq = 1 }, Owner);
        var after = NetworkString.Create(FullInfo() with { Seq = 2, Nodes = ["tcp://node1.example.org:11010", "tcp://evil.example.org:11010"] }, Owner);
        var shown = string.Join("\n", TrustCommands.DescribeNetwork(after, before));
        Assert.Contains("+ 节点：tcp://evil.example.org:11010", shown);
        Assert.Contains("- 节点：udp://node2.example.org:11010", shown);
        Assert.DoesNotContain("nz-friends-public-v1", shown);
    }

    [Fact]
    public void UnknownFieldsAreRefused()
    {
        var json = FullInfo().ToJson();
        json["surprise"] = "trust me";
        var payload = Encoding.UTF8.GetBytes(json.ToJsonString());
        var entry = Owner.Sign([.. "jay-network-v2\n"u8.ToArray(), .. payload]);
        var text = NetworkString.Current + NetworkString.Base64Url(payload) + "." + NetworkString.Base64Url(entry.Encode());
        var e = Assert.Throws<InvalidDataException>(() => TrustCommands.DescribeNetwork(text, null));
        Assert.Contains("surprise", e.Message);
    }

    [Fact]
    public void SigningNeedsTheTypedCode()
    {
        var lines = new[] { "网络编号：nz-friends" };
        Assert.Equal("riends", TrustCommands.CodeOf("nz-friends"));
        Assert.Equal("jay", TrustCommands.CodeOf("jay"));

        Assert.True(TrustCommands.Confirm(lines, "网络编号", "riends", TextWriter.Null, new StringReader("riends\n"), interactive: true));
        Assert.False(TrustCommands.Confirm(lines, "网络编号", "riends", TextWriter.Null, new StringReader("\n"), interactive: true));
        Assert.False(TrustCommands.Confirm(lines, "网络编号", "riends", TextWriter.Null, new StringReader("y\n"), interactive: true));
        // Not a terminal: never signs, whatever arrives on input.
        Assert.False(TrustCommands.Confirm(lines, "网络编号", "riends", TextWriter.Null, new StringReader("riends\n"), interactive: false));

        var output = new StringWriter();
        TrustCommands.Confirm(lines, "网络编号", "riends", output, new StringReader("riends\n"), interactive: true);
        Assert.Contains("网络编号：nz-friends", output.ToString());
    }

    [Fact]
    public void CertificateReviewShowsWhatIsCertified()
    {
        using var node = X25519KeyPair.Generate();
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var cert = DirectoryCertificate.Issue(Trustee, node.PublicKey, "jay-public-v1", expires);
        var shown = string.Join("\n", TrustCommands.DescribeCertificate(cert));
        Assert.Contains(Convert.ToBase64String(node.PublicKey), shown);
        Assert.Contains("jay-public-v1", shown);
        Assert.Contains("2030-03-17", shown);
        Assert.Contains(TrustCommands.Fingerprint(Trustee.Id), shown);
    }
}
