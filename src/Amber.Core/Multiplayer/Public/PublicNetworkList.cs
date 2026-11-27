namespace Amber.Core.Multiplayer.Public;

/// <summary>添加连接串的结果。</summary>
public enum NetworkAddResult
{
    /// <summary>新加的网络。</summary>
    Added,
    /// <summary>同一个网络的新版本，替换了旧的。</summary>
    Updated,
    /// <summary>已经有这个版本（或者更新的）了，没变。</summary>
    AlreadyHave,
    /// <summary>
    /// 不是 Jay 审核的网络，要用户确认才加（再调用一次，带 confirmed）。Also returned when an update of a reviewed network
    /// is no longer reviewed.
    /// </summary>
    NeedsConfirm,
    /// <summary>同一个编号已经有另一把钥匙签的网络了：要先删掉旧的（防止冒用）。</summary>
    Conflict,
    /// <summary>编号 "jay" 留给内置的 Jay 公共网络（只认注册表签的）。</summary>
    Reserved,
}

/// <summary>
/// 本机保存的公共网络：内置的 Jay 网络 + 用户加的连接串。设置里只存连接串原文，每次读出来都重新验签，
/// 验不过的（被改过、过期了）直接丢掉不用。
/// </summary>
public sealed class PublicNetworkList
{
    private readonly List<SignedNetwork> _networks = [];
    private readonly IReadOnlyList<TrustQuorum>? _trusted;

    /// <param name="saved">设置里存的连接串。</param>
    /// <param name="trusted">Quorums that make a string reviewed (tests swap them; null = the Jay trustees, <see cref="JayTrust"/>).</param>
    public PublicNetworkList(IEnumerable<string>? saved, IReadOnlyList<TrustQuorum>? trusted = null, DateTimeOffset? now = null)
    {
        _trusted = trusted;
        foreach (var text in saved ?? [])
            if (NetworkString.TryParse(text, _trusted, now) is { } network && Admissible(network) && _networks.All(n => n.Info.Id != network.Info.Id))
                _networks.Add(network);
    }

    public IReadOnlyList<SignedNetwork> Networks => _networks;

    /// <summary>要存回设置的连接串。</summary>
    public List<string> ToSaved() => [.. _networks.Select(n => n.Text)];

    public SignedNetwork? Find(string? id) => _networks.FirstOrDefault(n => n.Info.Id == id);

    /// <summary>
    /// 加一个连接串。格式或签名不对抛 <see cref="InvalidDataException"/>（消息给用户看）。
    /// 没审核过的网络第一次返回 <see cref="NetworkAddResult.NeedsConfirm"/>，用户确认后带 <paramref name="confirmed"/> 再调一次。
    /// The same holds for a new version of a reviewed network that only its owner signed.
    /// </summary>
    public NetworkAddResult Add(string text, bool confirmed, out SignedNetwork network, DateTimeOffset? now = null)
    {
        network = NetworkString.Parse(text, _trusted, now);
        if (!Admissible(network)) return NetworkAddResult.Reserved;
        var id = network.Info.Id;
        var index = _networks.FindIndex(n => n.Info.Id == id);
        if (index >= 0)
        {
            var old = _networks[index];
            if (!old.SignerKey.AsSpan().SequenceEqual(network.SignerKey)) return NetworkAddResult.Conflict;
            if (!old.IsReplacedBy(network)) return NetworkAddResult.AlreadyHave;
            // A newer version signed by the same key needs no new confirmation, unless it lost the review the old version had.
            if (old.Reviewed && !network.Reviewed && !confirmed) return NetworkAddResult.NeedsConfirm;
            _networks[index] = network;
            return NetworkAddResult.Updated;
        }
        if (!network.Reviewed && !confirmed) return NetworkAddResult.NeedsConfirm;
        _networks.Add(network);
        return NetworkAddResult.Added;
    }

    public bool Remove(string id) => _networks.RemoveAll(n => n.Info.Id == id) > 0;

    /// <summary>"jay" 这个编号只给注册表签的（以后用它更新内置网络的节点）。</summary>
    private static bool Admissible(SignedNetwork network) => network.Info.Id != "jay" || network.Reviewed;

    /// <summary>
    /// 现在用的公共网络：选中的连接串网络；没选、选的不在了就用内置的 Jay 网络。
    /// 注册表签过的 "jay" 网络会补上内置 Jay 网络的节点。
    /// </summary>
    public PublicNetworkProfile Current(string? selectedId, IEnumerable<string>? extraJayNodes)
    {
        var jay = PublicNetworkProfile.Jay(extraJayNodes);
        if (string.IsNullOrEmpty(selectedId) || selectedId == "jay")
        {
            if (Find("jay") is { } update)
                return jay with { Nodes = [.. jay.Nodes.Concat(update.Info.Nodes).Distinct()], Config = update.Info.Config, Home = update.Info.Home };
            return jay;
        }
        return Find(selectedId)?.ToProfile() ?? jay;
    }
}
