using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amber.App.ViewModels;

/// <summary>联机页的引导：现在缺什么、高级选项开了哪些、贴错输入框的码转到对的地方。</summary>
public sealed partial class MultiplayerViewModel
{
    private void InitGuide()
    {
        Nodes.CollectionChanged += (_, _) => NotifyNodesChanged();
    }

    private void NotifyNodesChanged()
    {
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(NodesStatusText));
        OnPropertyChanged(nameof(NodesHeaderText));
        NotifyLobbyStatus();
    }

    /// <summary>实际会用的节点数（自己的 + 开着时的内置节点）。</summary>
    private int EffectiveNodeCount => RelayNodes.Effective(Nodes.Select(n => n.Url), UseBuiltInNodes).Count;

    public bool HasNodes => EffectiveNodeCount > 0;

    /// <summary>联机节点折叠区是否展开：载入时没有节点就展开，之后由用户决定。</summary>
    [ObservableProperty] public partial bool NodesExpanded { get; set; }

    /// <summary>引导卡里的节点状态：房间码方式需要节点，直连不需要。</summary>
    public string NodesStatusText => HasNodes
        ? L.F("multiplayer.guide.nodes_ready", EffectiveNodeCount)
        : L.T("multiplayer.guide.nodes_missing");

    /// <summary>联机节点折叠区的标题，收起时也能看到有几个节点。</summary>
    public string NodesHeaderText => HasNodes
        ? L.F("multiplayer.nodes.header_count", EffectiveNodeCount)
        : L.T("multiplayer.nodes.header_empty");

    /// <summary>开房间用的游戏端口：手动填的优先，其次是选中的游戏。和创建房间时的取法一致。</summary>
    private int? ManualPortValue => int.TryParse(ManualPort.Trim(), out var manual) && manual is > 0 and < 65536 ? manual : null;

    public bool HasHostGame => ManualPortValue is not null || SelectedLanGame is not null;

    public string GameStatusText => ManualPortValue is { } port
        ? L.F("multiplayer.guide.port_ready", port)
        : SelectedLanGame is { } game
            ? L.F("multiplayer.guide.game_ready", game.Label)
            : L.T("multiplayer.guide.game_missing");

    partial void OnSelectedLanGameChanged(LanGameItem? value)
    {
        NotifyGameStatus();
        _ = RetargetGameRelayAsync(typed: false);
    }

    partial void OnManualPortChanged(string value)
    {
        NotifyGameStatus();
        _ = RetargetGameRelayAsync(typed: true);
    }

    private void NotifyGameStatus()
    {
        OnPropertyChanged(nameof(HasHostGame));
        OnPropertyChanged(nameof(GameStatusText));
        NotifyPlayAndHost();
    }

    /// <summary>高级选项折叠区的标题：收起时也能看出哪些不是默认值。</summary>
    public string AdvancedHeaderText
    {
        get
        {
            var on = new List<string>();
            if (FullLanMode) on.Add(L.T("multiplayer.advanced.item.full_lan"));
            if (FullLanMode && AllowExitNode) on.Add(L.T("multiplayer.advanced.item.exit"));
            if (AllowGameProxy) on.Add(L.T("multiplayer.advanced.item.proxy"));
            if (SelectedTransport.Mode != DefaultTransport) on.Add(L.F("multiplayer.advanced.item.transport", SelectedTransport.Label));
            return on.Count == 0
                ? L.T("multiplayer.advanced.title")
                : L.F("multiplayer.advanced.enabled", string.Join(L.T("multiplayer.advanced.separator"), on));
        }
    }

    partial void OnFullLanModeChanged(bool value)
    {
        if (value && !CanUseFullLan)
        {
            FullLanMode = false;
            return;
        }
        if (!value) AllowExitNode = false;
        OnPropertyChanged(nameof(AdvancedHeaderText));
    }

    partial void OnAllowExitNodeChanged(bool value)
    {
        if (value && (!CanUseFullLan || !FullLanMode || Child.HideNetworkExit))
        {
            AllowExitNode = false;
            return;
        }
        OnPropertyChanged(nameof(AdvancedHeaderText));
    }
    partial void OnAllowGameProxyChanged(bool value)
    {
        if (value && Child.HideNetworkExit)
        {
            AllowGameProxy = false;
            return;
        }
        OnPropertyChanged(nameof(AdvancedHeaderText));
    }
    partial void OnSelectedTransportChanged(TransportChoice value) => OnPropertyChanged(nameof(AdvancedHeaderText));

    /// <summary>
    /// 输入框里贴的不是这里要的码时，转到对应的地方处理，返回 true 表示已经处理了。
    /// 认不出来的码交回原来的流程，由它报“格式不对”。
    /// </summary>
    private bool RouteMisplacedCode(string text, PastedCodeKind expected, Action clearSource)
    {
        var kind = PastedCode.Detect(text);
        if (kind == expected || kind == PastedCodeKind.Unknown) return false;
        switch (kind)
        {
            case PastedCodeKind.Invite:
                clearSource();
                Tab = MultiplayerTab.Play;
                JoinCode = text;
                JoinCommand.Execute(null);
                return true;
            case PastedCodeKind.Room:
                // 旧版本的 AMB- 房间码不能再用来加入：请房主更新后发邀请码。
                SetStatus(StatusMessage.Warning("multiplayer.invite.old_code"));
                return true;
            case PastedCodeKind.NodeShare:
                // 节点会被保存、以后每次联机都用：不在“加入”框里静默导入，放进节点导入框让用户确认。
                clearSource();
                ShareCodeText = text;
                NodesExpanded = true;
                Navigate?.Invoke(LegacyRoutes.RelaySettings);
                SetStatus(StatusMessage.Warning("multiplayer.code.node_misplaced"));
                return true;
            case PastedCodeKind.OldDirect:
                SetStatus(StatusMessage.Warning("multiplayer.direct.old_code"));
                return true;
            case PastedCodeKind.SealedOffer:
                // A sealed direct code: into the join box, which then asks for the passphrase.
                clearSource();
                Tab = MultiplayerTab.Play;
                JoinCode = text.Trim();
                SetStatus(StatusMessage.Info("multiplayer.sealed.enter_passphrase"));
                return true;
            case PastedCodeKind.SealedReply:
                // A friend's sealed reply belongs to the host's direct room card.
                clearSource();
                if (IsDirectHost && HasSealedOffer)
                {
                    SealedReplyInput = text.Trim();
                    AddSealedReplyCommand.Execute(null);
                }
                else SetStatus(StatusMessage.Warning("multiplayer.sealed.reply_misplaced"));
                return true;
            case PastedCodeKind.JayServer:
                // Jay 服务器邀请：交给“Jay 服务器”卡片加入（只能用一次，所以直接用掉，不留在原来的框里）。
                clearSource();
                JayServers.InviteText = text;
                JayServers.JoinCommand.Execute(null);
                return true;
            case PastedCodeKind.FriendCode when ShowPublicNetworks:
                // 好友码：到“公开房间”里找朋友的隐藏房间（要先连上公共网络）。
                clearSource();
                FindCodeInput = text.Trim();
                Tab = MultiplayerTab.Public;
                SetStatus(StatusMessage.Info("multiplayer.code.friend_misplaced"));
                return true;
            case PastedCodeKind.ContactCode when ShowPublicNetworks:
                // A friend code: into the add panel of the Friends page, added only when the player clicks [Add].
                clearSource();
                ContactCodeInput = text.Trim();
                OpenAddFriend();
                SetStatus(StatusMessage.Info("multiplayer.code.contact_misplaced"));
                return true;
            case PastedCodeKind.PublicNetwork:
                // A public network address: into the box in Settings › Network, added only after the player has seen it.
                clearSource();
                NetworkInput = text.Trim();
                Navigate?.Invoke(LegacyRoutes.PublicNetworks);
                SetStatus(StatusMessage.Info("multiplayer.code.network_misplaced"));
                return true;
            default:
                return false;
        }
    }
}
