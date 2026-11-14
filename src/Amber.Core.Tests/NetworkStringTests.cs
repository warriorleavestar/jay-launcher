using System.Text;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>公共网络连接串：签名、改动检测、审核钥匙、版本、过期，以及本机保存的网络列表（替换、冲突、保留编号）。</summary>
public sealed class NetworkStringTests
{
    private static readonly TrustSigner Registry = TrustSigner.Generate(out _);
    private static readonly TrustSigner Someone = TrustSigner.Generate(out _);
    private static readonly TrustQuorum[] Trusted = [new([Registry.Id], 1)];

    private static NetworkInfo Info(string id = "nz-friends", long seq = 1) =>
        new(id, "新西兰朋友服", "jay-" + id, id + "-public-v1", ["tcp://node1.example.org:11010", "udp://node1.example.org:11010"], seq,
            Config: new NetworkConfigRef("https://example.org/jay/network.json", new string('a', 64)));

    [Fact]
    public void RoundTripsAndKnowsWhoSignedIt()
    {
        var text = NetworkString.Create(Info(), Registry);
        Assert.StartsWith("JAYNET2-", text);
        var reviewed = NetworkString.Parse(text, Trusted);
        Assert.True(reviewed.Reviewed);
        Assert.Equal(Info().Nodes, reviewed.Info.Nodes);
        Assert.Equal("新西兰朋友服", reviewed.Info.Name);
        Assert.Equal(new string('a', 64), reviewed.Info.Config!.Sha256);
        Assert.Equal(Registry.Id, reviewed.SignerKey);
        // 别人签的：签名对，但不算审核过。
        Assert.False(NetworkString.Parse(NetworkString.Create(Info(), Someone), Trusted).Reviewed);
        // The Jay trustees have no keys yet: nothing counts as reviewed.
        Assert.False(NetworkString.Parse(text).Reviewed);
    }

    [Fact]
    public void AnyChangeBreaksTheSignature()
    {
        var text = NetworkString.Create(Info(), Registry);
        var body = text["JAYNET2-".Length..].Split('.');
        var payload = NetworkString.FromBase64Url(body[0])!;
        var json = Encoding.UTF8.GetString(payload).Replace("node1.example.org", "evil.example.org");
        var forged = "JAYNET2-" + NetworkString.Base64Url(Encoding.UTF8.GetBytes(json)) + "." + body[1];
        Assert.Throws<InvalidDataException>(() => NetworkString.Parse(forged, Trusted));
        // 把注册表的签名换成自己的钥匙重签：签名对了，但不再是“审核过”。
        Assert.False(NetworkString.Parse(NetworkString.Create(NetworkString.Parse(text, Trusted).Info, Someone), Trusted).Reviewed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("JAYNET2-")]
    [InlineData("JAYNET2-abc")]
    [InlineData("JAYNET2-abc.def")]
    [InlineData("JAYNETx-abc.def")]
    public void RejectsGarbage(string text) => Assert.Null(NetworkString.TryParse(text, Trusted));

    [Fact]
    public void NewerFormatsAskForAnUpdate()
    {
        var text = NetworkString.Create(Info(), Registry).Replace("JAYNET2-", "JAYNET3-");
        var e = Assert.Throws<InvalidDataException>(() => NetworkString.Parse(text, Trusted));
        Assert.Contains("3", e.Message);
        // Version 1 strings are no longer read.
        var old = Assert.Throws<InvalidDataException>(() => NetworkString.Parse(NetworkString.Create(Info(), Registry).Replace("JAYNET2-", "JAYNET1-"), Trusted));
        Assert.Contains("1", old.Message);
    }

    [Fact]
    public void ExpiredStringsAreRefused()
    {
        var now = DateTimeOffset.UtcNow;
        var text = NetworkString.Create(Info() with { Expires = now.AddDays(1) }, Registry);
        Assert.NotNull(NetworkString.Parse(text, Trusted, now));
        Assert.Throws<InvalidDataException>(() => NetworkString.Parse(text, Trusted, now.AddDays(2)));
    }

    [Fact]
    public void InvalidContentCannotBeSigned()
    {
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { Id = "Bad Id" }, Registry));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { Nodes = [] }, Registry));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { Nodes = ["http://x"] }, Registry));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { Home = "http://example.org" }, Registry));
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { Config = new NetworkConfigRef("https://e.org/c.json", "xyz") }, Registry));
        // 冒用内置 Jay 网络的 EasyTier 网络名。
        Assert.Throws<InvalidDataException>(() => NetworkString.Create(Info() with { NetworkName = PublicNetworkProfile.JayNetworkName }, Registry));
    }

    [Fact]
    public void SignerKeysComeFromSeeds()
    {
        var signer = NetworkSigner.Generate(out var seed);
        var again = NetworkSigner.FromSeed(Convert.ToBase64String(seed));
        Assert.Equal(signer.PublicKey, again.PublicKey);
        Assert.Throws<InvalidDataException>(() => NetworkSigner.FromSeed("not base64!"));
        Assert.Throws<InvalidDataException>(() => NetworkSigner.FromSeed(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void ListAsksBeforeAddingUnreviewedNetworks()
    {
        var list = new PublicNetworkList(null, Trusted);
        var unreviewed = NetworkString.Create(Info("someone"), Someone);
        Assert.Equal(NetworkAddResult.NeedsConfirm, list.Add(unreviewed, false, out _));
        Assert.Empty(list.Networks);
        Assert.Equal(NetworkAddResult.Added, list.Add(unreviewed, true, out _));
        Assert.Equal(NetworkAddResult.Added, list.Add(NetworkString.Create(Info(), Registry), false, out _));
        Assert.Equal(2, list.Networks.Count);
    }

    /// <summary>An update that only its owner signed does not silently replace a reviewed network.</summary>
    [Fact]
    public void AnUpdateThatLostTheReviewIsConfirmedFirst()
    {
        var list = new PublicNetworkList(null, Trusted);
        Assert.Equal(NetworkAddResult.Added, list.Add(NetworkString.Create(Info("someone", 1), Someone, Registry), false, out _));
        Assert.True(list.Find("someone")!.Reviewed);
        var ownerOnly = NetworkString.Create(Info("someone", 2), Someone);
        Assert.Equal(NetworkAddResult.NeedsConfirm, list.Add(ownerOnly, false, out _));
        Assert.Equal(1, list.Find("someone")!.Info.Seq);
        Assert.True(list.Find("someone")!.Reviewed);
        Assert.Equal(NetworkAddResult.Updated, list.Add(ownerOnly, true, out _));
        Assert.False(list.Find("someone")!.Reviewed);
        // A reviewed update needs no confirmation.
        Assert.Equal(NetworkAddResult.Updated, list.Add(NetworkString.Create(Info("someone", 3), Someone, Registry), false, out _));
        Assert.True(list.Find("someone")!.Reviewed);
    }

    [Fact]
    public void ListReplacesOnlyNewerVersionsFromTheSameKey()
    {
        var list = new PublicNetworkList(null, Trusted);
        Assert.Equal(NetworkAddResult.Added, list.Add(NetworkString.Create(Info(seq: 2), Registry), false, out _));
        Assert.Equal(NetworkAddResult.AlreadyHave, list.Add(NetworkString.Create(Info(seq: 1), Registry), false, out _));
        Assert.Equal(NetworkAddResult.Updated, list.Add(NetworkString.Create(Info(seq: 3) with { Nodes = ["tcp://node2.example.org:11010"] }, Registry), false, out _));
        Assert.Equal(["tcp://node2.example.org:11010"], list.Find("nz-friends")!.Info.Nodes);
        // 别的钥匙用同一个编号：不替换，要用户先删。
        Assert.Equal(NetworkAddResult.Conflict, list.Add(NetworkString.Create(Info(seq: 9), Someone), true, out _));
        Assert.Equal(Registry.Id, list.Find("nz-friends")!.SignerKey);
        Assert.True(list.Remove("nz-friends"));
        Assert.Equal(NetworkAddResult.Added, list.Add(NetworkString.Create(Info(seq: 9), Someone), true, out _));
    }

    [Fact]
    public void JayIdIsReservedForTheRegistry()
    {
        var list = new PublicNetworkList(null, Trusted);
        var fake = Info("jay") with { NetworkName = PublicNetworkProfile.JayNetworkName };
        Assert.Equal(NetworkAddResult.Reserved, list.Add(NetworkString.Create(fake, Someone), true, out _));
        // 注册表签的 jay：补充内置网络的节点（以后不用发新版本也能加节点）。
        Assert.Equal(NetworkAddResult.Added, list.Add(NetworkString.Create(fake, Registry), false, out _));
        var current = list.Current(null, ["tcp://mine.example.org:11010"]);
        Assert.Equal(PublicNetworkProfile.JayNetworkName, current.NetworkName);
        Assert.Contains("tcp://mine.example.org:11010", current.Nodes);
        Assert.Contains("tcp://node1.example.org:11010", current.Nodes);
    }

    [Fact]
    public void SavedStringsAreCheckedAgainOnLoad()
    {
        var good = NetworkString.Create(Info(), Registry);
        var expired = NetworkString.Create(Info("old") with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) }, Registry);
        var list = new PublicNetworkList([good, "JAYNET2-garbage.x", expired, good], Trusted);
        Assert.Single(list.Networks);
        Assert.Equal([good], list.ToSaved());
        var profile = list.Current("nz-friends", null);
        Assert.Equal("jay-nz-friends", profile.NetworkName);
        Assert.True(profile.Reviewed);
        Assert.False(profile.IsBuiltIn);
        // 选的网络不在了：回到内置的 Jay 网络。
        Assert.True(list.Current("gone", null).IsBuiltIn);
    }
}
