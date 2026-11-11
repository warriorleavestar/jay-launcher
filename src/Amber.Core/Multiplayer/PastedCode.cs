namespace Amber.Core.Multiplayer;

/// <summary>联机页上粘贴的码是哪一种。</summary>
public enum PastedCodeKind
{
    Unknown,
    /// <summary>旧版本的房间码 AMB-…：不能再用来加入，提示对方更新。</summary>
    Room,
    /// <summary>邀请码 JAY2-…：经联机节点的房间（先敲门、房主批准）。</summary>
    Invite,
    /// <summary>
    /// Removed direct-link codes: JAY2D- / JAY2R- (both sides' public IP addresses in plain text) and the older
    /// AMBDIRECT1- / AMBREPLY1-. Refused; the person is told to ask for a current invite (JAY2-).
    /// </summary>
    OldDirect,
    /// <summary>节点分享码 AMBNODE-…。</summary>
    NodeShare,
    /// <summary>Jay 服务器邀请码 JAYS2- / JAYSS1-（加密）…：贴到“Jay 服务器”卡片。</summary>
    JayServer,
    /// <summary>好友码 JAYF1-…（设备指纹码）：到“公开房间”里找朋友的隐藏房间。</summary>
    FriendCode,
    /// <summary>公共网络地址 JAYNET…：到联机设置里先看内容再确认添加。</summary>
    PublicNetwork,
    /// <summary>A contact code JAYC1-… (someone's identity public key): add them as a contact and compare emoji.</summary>
    ContactCode,
    /// <summary>A sealed direct offer JAYDS1-… (host's addresses encrypted with a passphrase told by another way).</summary>
    SealedOffer,
    /// <summary>A sealed direct reply JAYDR1-… (the friend's addresses, sealed with the same passphrase): goes to the host.</summary>
    SealedReply,
}

/// <summary>
/// 只按前缀认出码的种类，好把贴错输入框的码转到对的地方，或者告诉用户该贴在哪。
/// 不校验内容，真正解析仍由各自的 TryParse 负责。容忍大小写和前后多余的文字。
/// </summary>
public static class PastedCode
{
    public static PastedCodeKind Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return PastedCodeKind.Unknown;
        var upper = text.ToUpperInvariant();
        // 先认长前缀：base64url 正文里可能碰巧出现 “AMB-”。
        var first = PastedCodeKind.Unknown;
        var firstAt = int.MaxValue;
        foreach (var (prefix, kind) in Prefixes)
        {
            var at = upper.IndexOf(prefix, StringComparison.Ordinal);
            if (at >= 0 && at < firstAt)
            {
                first = kind;
                firstAt = at;
            }
        }
        if (first != PastedCodeKind.Unknown) return first;
        return RoomCode.TryParse(text, out _) ? PastedCodeKind.Room : PastedCodeKind.Unknown;
    }

    private static readonly (string Prefix, PastedCodeKind Kind)[] Prefixes =
    [
        (SealedDirect.OfferPrefix, PastedCodeKind.SealedOffer),
        (SealedDirect.ReplyPrefix, PastedCodeKind.SealedReply),
        ("JAY2D-", PastedCodeKind.OldDirect),
        ("JAY2R-", PastedCodeKind.OldDirect),
        ("AMBDIRECT", PastedCodeKind.OldDirect),
        ("AMBREPLY", PastedCodeKind.OldDirect),
        ("AMBNODE-", PastedCodeKind.NodeShare),
        (RelayNodes.SealedPrefix, PastedCodeKind.NodeShare),
        ("JAY2-", PastedCodeKind.Invite),
        (Gate.GateInvite.SealedPrefix, PastedCodeKind.JayServer),
        (Gate.GateInvite.Prefix, PastedCodeKind.JayServer),
        // Old server invites go to the server card too, which explains they must be remade.
        (Gate.GateInvite.OldPrefix, PastedCodeKind.JayServer),
        ("JAYF1-", PastedCodeKind.FriendCode),
        ("JAYC1-", PastedCodeKind.ContactCode),
        ("JAYNET", PastedCodeKind.PublicNetwork),
    ];
}
