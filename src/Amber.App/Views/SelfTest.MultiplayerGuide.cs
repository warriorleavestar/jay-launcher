using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Navigation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// The multiplayer lobby (UI restructure: people, rooms, networks): no component download and no connection. Checks
    /// the real controls: no tab strip, join on top, the open-a-room card with its four choices, "where to play", the
    /// paste box that recognises every code, one amber button, Ctrl+V, codes pasted in the wrong box going to the right
    /// place, the sub-views with their back row, relay servers and the server wizard's first step.
    /// </summary>
    private async Task MultiplayerGuideAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        main.Shell.Navigate("play");
        var page = (await FindAsync<MultiplayerPage>(_ => true))!;
        if (page is null)
        {
            Check("联机引导：页面显示", false);
            return;
        }
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        // 本范围只检查界面；即使测试目录已有组件，也不建立任何连接。
        vm.IsComponentReady = false;
        bool HasText(string key) => page.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.IsEffectivelyVisible && t.Text == L.T(key));
        Control? Shown(string name) => page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        Button? Visible(string name) => page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);

        Border? Card(string name) => page.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        Check("联机页：没有页签，页面就是大厅", vm.IsPlayTab && !vm.IsSubView && Shown("MpSubViewRow") is null
            && !page.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "MpTabs" or "PlayTabButton"));
        Check("大厅：加入、开房间、去哪玩三块", HasText("multiplayer.lobby.join_title") && HasText("multiplayer.lobby.host_title") && HasText("multiplayer.where.title"));
        var (joinCard, hostCard, whereCard) = (Card("JoinCard"), Card("HostCard"), Card("WhereCard"));
        Check("大厅：加入在最上面占满宽度，开房间和去哪玩在下面并排", page.LobbyLayout == MultiplayerPage.LobbyArrangement.SideBySide
            && joinCard is not null && hostCard is not null && whereCard is not null
            && joinCard.Bounds.Bottom <= hostCard.Bounds.Top && Math.Abs(hostCard.Bounds.Top - whereCard.Bounds.Top) < 1 && hostCard.Bounds.Right <= whereCard.Bounds.Left
            && joinCard.Bounds.Width >= hostCard.Bounds.Width + whereCard.Bounds.Width,
            $"{joinCard?.Bounds} {hostCard?.Bounds} {whereCard?.Bounds}");
        Check("大厅：自动复制邀请码的勾选框看得见、默认开", vm.AutoCopyInvite
            && page.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Name == "AutoCopyInviteBox" && c.IsEffectivelyVisible));
        Check("大厅：组件没装时底部状态行是注意色", vm.LobbyStatusWarns && vm.LobbyStatusText.Contains(L.T("multiplayer.lobby.status_component_missing")));

        // One lamp: no code → open-a-room is amber; a recognised code → join is amber.
        var host = Visible("LobbyHostButton");
        var join = Visible("LobbyJoinButton");
        Check("一盏灯：没贴码时开房间是琥珀，加入是蓝色", host?.Classes.Contains("play") == true && join?.Classes.Contains("play") == false && join?.Classes.Contains("primary") == true);
        // The component is missing: no card for it; installing is the first step of the open-a-room button.
        TextBlock? Text(string name) => page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible);
        Check("缺组件：开房间按钮写“先装联机组件 N MB”，下面有说明和【导入】，没有单独的卡片", vm.ShowComponentStep
            && Text("LobbyHostButtonText")?.Text == vm.ComponentStepText && vm.ComponentStepText.Contains("MB")
            && Shown("ComponentStep") is not null && Text("ComponentStepHint") is not null && Visible("ImportComponentButton") is not null
            && page.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "ComponentAbout") is { IsEffectivelyVisible: true, IsExpanded: false },
            Text("LobbyHostButtonText")?.Text ?? "none");
        Check("缺组件：没贴码时加入按钮还是【加入】", Text("LobbyJoinButtonText")?.Text == L.T("multiplayer.lobby.join_button"), Text("LobbyJoinButtonText")?.Text ?? "none");
        vm.IsComponentReady = true;
        await Task.Delay(150);
        Check("组件装好后：这一步收起，按钮回到开房间", !vm.ShowComponentStep && Shown("ComponentStep") is null
            && Text("LobbyHostButtonText")?.Text == (vm.StartsGameToHost ? L.T("multiplayer.lobby.play_and_host") : L.T("multiplayer.lobby.host_button")));
        if (vm.StartsGameToHost)
            Check("Lobby: with no LAN world the button is [Play and open a room] and names the game it starts",
                HasText("multiplayer.lobby.play_and_host") && Shown("PlayAndHostHint") is not null, vm.PlayAndHostHint);
        await OpenChoicesAsync(vm, page);
        vm.IsComponentReady = false;
        await Task.Delay(150);
        var relayInvite = JayInvite.Create(new byte[16], Amber.Core.Multiplayer.Transport.TunnelSettings.Default).ToString();
        var roomCode = await FindAsync<TextBox>(t => t.Name == "JoinCodeBox");
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = relayInvite);
        await Task.Delay(200);
        Check("一盏灯：贴进邀请后琥珀移到加入", host?.Classes.Contains("play") == false && join?.Classes.Contains("play") == true);
        Check("万能粘贴框：认出房间邀请", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.invite") && !vm.JoinCodeRecognitionWarns && HasText("multiplayer.lobby.code.invite"));
        Check("万能粘贴框：邀请码默认打码", roomCode?.PasswordChar == '•' && !vm.JoinCodeShown);
        Check("缺组件：贴进邀请后加入按钮写“先装联机组件 N MB”", Text("LobbyJoinButtonText")?.Text == vm.ComponentStepText);

        // Invite v3: the host's game is in the code; Jay picks the closest instance or offers to install one.
        var hostMods = new ModManifest([new ModManifestEntry("a.jar", new string('a', 40), 1), new ModManifestEntry("b.jar", new string('b', 40), 1)]);
        var v3 = JayInvite.Create(new byte[16], Amber.Core.Multiplayer.Transport.TunnelSettings.Default, hostMods.ToInviteGame("1.20.1", "fabric")).ToString();
        vm.TestCandidates = () => [new("vanilla", "1.20.1", null, 0), new("close", "1.20.1", "fabric", 5), new("closer", "1.20.1", "fabric", 3)];
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = v3);
        await WaitAsync(() => vm.JoinMatch is not null, 5);
        Check("邀请 v3：写出房主的游戏（版本 · 加载器 · 模组数）", vm.JoinGameText == "1.20.1 · Fabric · " + L.P("multiplayer.lobby.game.mods", 2, 2)
            && Shown("JoinGameText") is not null, vm.JoinGameText);
        Check("邀请 v3：自动挑最接近的实例，不说“和房主一样”", vm.JoinMatch is { Kind: InstanceMatchKind.SameGame, Instance.Id: "closer" }
            && vm.JoinMatchText == L.T("multiplayer.lobby.game.pick_close") && Shown("JoinMatchText") is not null);
        vm.TestCandidates = () => [new("other", "1.20.1", "fabric", 9), new("exact", "1.20.1", "fabric", 2, hostMods.Fingerprint)];
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = v3 + " ");
        await WaitAsync(() => vm.JoinMatch?.Instance.Id == "exact", 5);
        Check("邀请 v3：模组清单一样的实例排第一", vm.JoinMatch is { Kind: InstanceMatchKind.Exact } && vm.JoinMatchText == L.T("multiplayer.lobby.game.pick_exact"));
        await ShotAsync("邀请v3-挑实例");
        vm.TestCandidates = () => [new("old", "1.19.2", "fabric", 2)];
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = v3);
        await WaitAsync(() => vm.JoinNeedsInstall, 5);
        await Task.Delay(100);
        Check("邀请 v3：没有合适的实例时给【装一个 1.20.1 Fabric 再加入】", vm.JoinNeedsInstall && Visible("InstallForInviteButton") is not null && Shown("JoinNoInstanceText") is not null
            && vm.InstallForInviteText == L.F("multiplayer.lobby.game.install", "1.20.1", "Fabric"));
        await ShotAsync("邀请v3-没有实例");
        vm.TestCandidates = null;
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = relayInvite);
        await Task.Delay(100);
        Check("邀请 v2：没有游戏信息时不显示这一块", !vm.HasJoinGame && Shown("JoinGameText") is null);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565");
        await Task.Delay(100);
        Check("万能粘贴框：旧 AMB- 码说请朋友更新", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.old") && vm.JoinCodeRecognitionWarns);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "hello");
        await Task.Delay(100);
        Check("万能粘贴框：认不出时写邀请码长什么样", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.unknown") && host?.Classes.Contains("play") == true);
        // A server invite, a friend's code and a network address are told apart, and the button says what it will do.
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "JAYS2-0123456789ABCDEFGHJK");
        await Task.Delay(100);
        Check("万能粘贴框：服务器邀请写“一个一直在线的服务器的邀请”，按钮是【加入并存下】", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.server")
            && Text("LobbyJoinButtonText")?.Text == L.T("multiplayer.lobby.join.server") && !vm.JoinNeedsPassphrase, Text("LobbyJoinButtonText")?.Text ?? "none");
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "JAYSS1-QUJDREVGR0hJSktMTU5PUA");
        await Task.Delay(100);
        Check("万能粘贴框：加密的服务器邀请出现口令框", vm.JoinNeedsPassphrase && Shown("JoinPassphraseBox") is not null);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "JAYC1-ABCD-EFGH-JKMN-PQRS-TVWX");
        await Task.Delay(100);
        Check("万能粘贴框：朋友的码，按钮是【加朋友】", Text("LobbyJoinButtonText")?.Text == L.T("multiplayer.lobby.join.friend"), Text("LobbyJoinButtonText")?.Text ?? "none");
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "JAYNET2-ABCDEFGH");
        await Task.Delay(100);
        Check("万能粘贴框：网络地址，按钮是【添加网络】", vm.JoinCodeKind == PastedCodeKind.PublicNetwork && Text("LobbyJoinButtonText")?.Text == L.T("multiplayer.lobby.join.network"),
            Text("LobbyJoinButtonText")?.Text ?? "none");
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "");
        // "Open like last time": hidden until a room was opened once; says what it repeats.
        Check("和上次一样开：没开过房间时不显示", !vm.HasLastRoom && Shown("LastRoomRow") is null);
        main.Service.Settings.MultiplayerLastRoom = new Amber.App.Services.LastRoom { World = "新世界", Visibility = "Public" };
        vm.NotifyLastRoom();
        await Task.Delay(200);
        Check("和上次一样开：写上次的世界、要我同意、谁能找到", Shown("LastRoomRow") is not null && Visible("RepeatLastRoomButton") is not null
            && vm.LastRoomText == L.F("multiplayer.lobby.last_room", "新世界 · " + L.T("multiplayer.invite.terms_ask") + " · " + L.T("multiplayer.public.visibility.public")), vm.LastRoomText);
        main.Service.Settings.MultiplayerLastRoom = new Amber.App.Services.LastRoom
        {
            World = "新世界", Access = nameof(RoomAccess.Invited), Route = nameof(RoomRoute.RelayOnly), Protection = nameof(RoomProtection.Standard), Scope = nameof(RoomScope.Game),
        };
        vm.NotifyLastRoom();
        Check("和上次一样开：新开的房间写四个选择里和默认不一样的", vm.LastRoomText == L.F("multiplayer.lobby.last_room",
            "新世界 · " + L.T("multiplayer.invite.terms_ask") + " · " + L.T("multiplayer.open.who.invited") + " · " + L.T("multiplayer.open.route.relay")), vm.LastRoomText);
        await ShotAsync("和上次一样开");
        Press(Visible("RepeatLastRoomButton"));
        await Task.Delay(300);
        Check("和上次一样开：组件没装时先说要下载，不出错", vm.IsIdle && vm.Status == L.T("multiplayer.component.download_required"), vm.Status);
        main.Service.Settings.MultiplayerLastRoom = null;
        vm.NotifyLastRoom();
        // The repeat picked last time's choices: back to the defaults for the rest of the checks.
        vm.PickOpenChoices(nameof(RoomAccess.Invited), nameof(RoomRoute.Auto), null, null);
        vm.SetStatus(null);
        await Task.Delay(150);
        await ShotAsync("联机大厅-1440");
        await LayoutHereAsync("联机大厅");

        // Relay servers live in multiplayer settings, reached from [Advanced settings] under the open-a-room card; built-in
        // ones are on out of the box. A sub-view has a row to go back and its name.
        Check("开房间卡下面的【高级设置】打开联机设置", Press(Visible("OpenAdvancedButton")) && vm.IsSettingsTab);
        await Task.Delay(300);
        Check("子页面：顶上有【返回】和这一页的名字，大厅收起", vm.IsSubView && Visible("MpBackButton") is not null
            && Text("MpSubViewTitle")?.Text == L.T("multiplayer.tab.settings") && Card("JoinCard") is null, Text("MpSubViewTitle")?.Text ?? "none");
        Check("子页面：【返回】回到大厅", Press(Visible("MpBackButton")) && vm.IsPlayTab && !vm.IsSubView);
        vm.ShowTabCommand.Execute("settings");
        await Task.Delay(300);
        Check("联机设置：分组都在", HasText("multiplayer.settings.identity") && HasText("multiplayer.settings.invites") && HasText("multiplayer.settings.component")
            && HasText("multiplayer.settings.diagnostics"));

        // Long-lived invites for regular friends: made here, 30 days, masked, revoke.
        Check("长期邀请：开始时没有", !vm.HasLongInvites);
        Dispatcher.UIThread.Invoke(() => vm.LongInviteLabel = "Bob");
        Press(Visible("CreateLongInviteButton"));
        await WaitAsync(() => vm.HasLongInvites, 3);
        await Task.Delay(200);
        var longItem = vm.LongInviteItems.FirstOrDefault();
        Check("长期邀请：做好一张给 Bob 的，30 天、默认打码、能被认出是邀请", longItem is { Label: "Bob" } && longItem.Left == L.P("multiplayer.longinvite.days_left", 30, 30)
            && longItem.CodeDisplay.Contains('•') && JayInvite.TryParse(longItem.Code, out _) && vm.LongInviteLabel.Length == 0
            && page.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.Name == "LongInviteList" && i.IsEffectivelyVisible), longItem?.Left);
        await ShotAsync("长期邀请");
        Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("long-revoke") && b.IsEffectivelyVisible));
        await Task.Delay(200);
        Check("长期邀请：作废后列表清空", !vm.HasLongInvites);
        await ContactsAsync(vm, page);
        var relayHeader = page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RelayHeader");
        // "On by default" is about a fresh install, not about this test profile: a reused profile may have it switched off
        // (earlier runs, or a tester who turned it off). Check the default itself, then show the switch on for the UI checks
        // and put the profile's own choice back at the end.
        var profileUsesBuiltIn = vm.UseBuiltInNodes;
        vm.UseBuiltInNodes = true;
        await Task.Delay(100);
        Check("联机设置：默认开着内置中转服务器，开箱就有", new AppSettings().UseBuiltInNodes && vm.HasNodes && HasText("multiplayer.nodes.builtin_toggle")
            && relayHeader?.Text == vm.NodesHeaderText,
            $"default={new AppSettings().UseBuiltInNodes} profile={profileUsesBuiltIn} has={vm.HasNodes} header={vm.NodesHeaderText} count={RelayNodes.BuiltIn.Count}");
        var advanced = page.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "AdvancedExpander");
        Check("联机设置：高级选项默认收起", advanced is { IsExpanded: false });
        vm.UseBuiltInNodes = false;
        await Task.Delay(200);
        Check("联机设置：没有中转服务器时标题写出来", !vm.HasNodes && relayHeader?.Text == L.T("multiplayer.nodes.header_empty"));
        await ShotAsync("联机设置-1440");

        // A relay share code pasted into the join box goes to settings and is not imported silently.
        vm.ShowTabCommand.Execute("play");
        await Task.Delay(200);
        var nodeCode = RelayNodes.Encode(null, ["tcp://example.invalid:11010"]);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = nodeCode);
        Check("加入框里的中转分享码：点加入", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand))));
        await Task.Delay(300);
        var importBox = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.nodes.share_placeholder"));
        Check("中转分享码转到联机设置，不静默导入", vm.IsSettingsTab && vm.Nodes.Count == 0 && roomCode!.Text == "" && importBox?.Text == nodeCode && vm.IsIdle);
        Check("用户点导入才添加", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.ImportNodesCommand)))
            && await WaitAsync(() => Dispatcher.UIThread.Invoke(() => vm.Nodes.Count == 1), 5) && vm.Nodes[0].Url == "tcp://example.invalid:11010");
        Check("中转服务器数量同步到标题和大厅状态行", relayHeader?.Text == L.F("multiplayer.nodes.header_count", 1)
            && vm.LobbyStatusText.Contains(L.F("multiplayer.lobby.status_relays", 1)));
        vm.RemoveNodeCommand.Execute(vm.Nodes.Single());

        // A relay that goes by an IP: the share code is sealed, the passphrase box shows, and import needs the passphrase.
        var (sealedNodes, nodePassphrase) = RelayNodes.Share(null, ["tcp://203.0.113.5:11010"]);
        Dispatcher.UIThread.Invoke(() => vm.ShareCodeText = sealedNodes);
        await Task.Delay(200);
        var nodePassphraseBox = await FindAsync<TextBox>(t => t.Name == "NodeSharePassphraseBox");
        Check("加密中转分享码：不含 IP，显示口令框", !sealedNodes.Contains("203.0.113.5") && nodePassphraseBox?.IsEffectivelyVisible == true);
        Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.ImportNodesCommand)));
        await Task.Delay(300);
        Check("加密中转分享码：没口令不导入", vm.Nodes.Count == 0 && vm.Status == L.T("multiplayer.sealed.enter_passphrase"), vm.Status);
        Dispatcher.UIThread.Invoke(() => vm.SharePassphrase = nodePassphrase!);
        Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.ImportNodesCommand)));
        Check("加密中转分享码：输入口令后导入", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => vm.Nodes.Count == 1), 10)
            && vm.Nodes[0].Url == "tcp://203.0.113.5:11010" && vm.SharePassphrase == "");
        if (vm.Nodes.Count > 0) vm.RemoveNodeCommand.Execute(vm.Nodes.Single());
        vm.UseBuiltInNodes = true;

        // Open a room: game status follows the port typed under "more ways".
        vm.ShowTabCommand.Execute("play");
        vm.MoreWaysOpen = true;
        await Task.Delay(300);
        var port = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.lan.port_placeholder"));
        Dispatcher.UIThread.Invoke(() => port!.Text = "25565");
        await Task.Delay(200);
        Check("开房间：填端口后卡片上写出来", vm.HasHostGame
            && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == L.F("multiplayer.guide.port_ready", 25565)));

        // Old direct codes (JAY2D / JAY2R) carried public IPs: the join box refuses them and asks for a plain invite.
        foreach (var oldDirect in new[] { "JAY2D-eyJ2IjoxfQ", "JAY2R-eyJ2IjoxfQ" })
        {
            Dispatcher.UIThread.Invoke(() => roomCode!.Text = oldDirect);
            Check($"加入框里的旧直连码（{oldDirect[..5]}）：点加入", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand))));
            await Task.Delay(300);
            Check($"旧直连码（{oldDirect[..5]}）提示改发普通邀请", vm.Status == L.T("multiplayer.direct.old_code") && vm.IsIdle && !vm.IsKnocking, vm.Status);
        }
        // A sealed direct code (no server, no readable IP): the passphrase box shows, and Join without it does nothing.
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "JAYDS1-QUJDREVGR0hJSktMTU5PUA");
        await Task.Delay(200);
        var passphraseBox = await FindAsync<TextBox>(t => t.Name == "JoinPassphraseBox");
        Check("加密直连码：出现口令框", passphraseBox is { IsEffectivelyVisible: true });
        Check("加密直连码：没填口令点加入只提示", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand))));
        await Task.Delay(200);
        Check("加密直连码：提示输入口令、不敲门", vm.Status == L.T("multiplayer.sealed.enter_passphrase") && vm.IsIdle && !vm.IsKnocking, vm.Status);
        Dispatcher.UIThread.Invoke(() => { roomCode!.Text = ""; port!.Text = ""; });
        vm.MoreWaysOpen = false;
        vm.SetStatus(null);

        // Ctrl+V anywhere: a code on the clipboard opens the lobby with it filled in (no knocking yet).
        main.Shell.Navigate("library");
        await Task.Delay(200);
        Check("Ctrl+V 贴邀请：打开大厅并填好，不自动敲门", main.PasteCode(relayInvite) && main.Shell.CurrentSection == Section.Play && vm.IsPlayTab
            && vm.JoinCode == relayInvite && !vm.IsKnocking && vm.IsIdle);
        Check("Ctrl+V 贴普通文字：不做事", !main.PasteCode("hello world"));
        main.Shell.Navigate("library");
        Check("Ctrl+V 贴好友码：到网络页的查找框", main.PasteCode("JAYF1-ABCD-EFGH-JKMN-PQRS-TVWX") && vm.IsPublicTab && vm.FindCodeInput == "JAYF1-ABCD-EFGH-JKMN-PQRS-TVWX");
        vm.FindCodeInput = "";
        vm.JoinCode = "";

        // Routes: play/public, play/servers and play/settings are sub-views now, each named in the back row.
        main.Shell.Navigate("play/servers");
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Check("路由 play/servers 打开我的服务器，只有开服这一块", vm.IsServersTab && Text("MpSubViewTitle")?.Text == L.T("multiplayer.subview.server")
            && Shown("MyServerPanel") is not null && !page.GetVisualDescendants().OfType<RadioButton>().Any(r => r.Name is "JayServersSegment" or "MyServerSegment"));
        main.Shell.Navigate("play/public");
        await Task.Delay(300);
        Check("路由 play/public 打开网络页", vm.IsPublicTab && HasText("multiplayer.public.title") && Text("MpSubViewTitle")?.Text == L.T("multiplayer.subview.network"));
        Check("网络页：没有公共网络时说明从哪来、给【添加公共网络…】", vm.ShowPublicSetup && Visible("AddPublicNetworkButton") is not null
            && HasText("multiplayer.public.setup_body"));
        Check("网络页：房间列表不在这里了（在大厅的去哪玩）", !page.GetVisualDescendants().OfType<Control>().Any(c => c.Name is "PublicRoomList" or "RoomSearchBox" && c.IsEffectivelyVisible));
        await ShotAsync("网络页-1440");

        // Where to play: one list with filter chips. (Leaving the page rebuilt its view: find it again.)
        main.Shell.Navigate("play");
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        await WhereToPlayAsync(vm, page);
        await PublicListAsync(vm);

        // Width tiers: medium and compact put the three cards in one column, join first.
        foreach (var (w, h, expected) in new[] { (900, 700, MultiplayerPage.LobbyArrangement.Stacked), (600, 420, MultiplayerPage.LobbyArrangement.Compact) })
        {
            window.Width = w;
            window.Height = h;
            await Task.Delay(500);
            var (j, o, wh) = (Card("JoinCard"), Card("HostCard"), Card("WhereCard"));
            Check($"大厅 {w}：加入、开房间、去哪玩从上到下一列", page.LobbyLayout == expected && j is not null && o is not null && wh is not null
                && j.Bounds.Bottom <= o.Bounds.Top && o.Bounds.Bottom <= wh.Bounds.Top && wh.Bounds.Right <= page.Bounds.Width + 1, $"{page.LobbyLayout} {j?.Bounds} {o?.Bounds} {wh?.Bounds}");
            await ShotAsync($"联机大厅-{w}");
        }
        L.SetLanguage("en");
        await Task.Delay(400);
        await ShotAsync("联机大厅-600-en");
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        await ShotAsync("联机大厅-1440-en");
        L.SetLanguage("zh-Hans");

        // The server wizard: before set-up only the address step shows; a bad address is explained, nothing is created.
        var server = vm.HostServer;
        Dispatcher.UIThread.Invoke(() => Visible("OpenServerButton")?.BringIntoView());
        Check("开房间卡下面的【开一个一直在线的服务器…】打开我的服务器", Press(Visible("OpenServerButton")) && vm.IsServersTab);
        await Task.Delay(300);
        if (!server.IsInitialized)
        {
            Check("开服：未初始化时显示填地址这一步", HasText("multiplayer.hostserver.step_address") && !HasText("multiplayer.hostserver.step_setup"));
            Check("开服：步骤条写第一步（地址 ●）、四步都在", server.WizardStep == 1 && server.WizardSteps.Count == 4 && server.WizardSteps[0].IsCurrent
                && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == server.WizardSteps[0].Label));
            Dispatcher.UIThread.Invoke(() => server.PublicHost = "http://bad host");
            Check("开服：地址填错时提示", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, server.InitCommand)))
                && server.Status == L.T("multiplayer.hostserver.bad_host") && !server.IsInitialized);
            await ShotAsync("开服-1440");
            Dispatcher.UIThread.Invoke(() =>
            {
                server.PublicHost = "";
                server.Status = "";
            });
        }
        else Check("开服：测试配置里已经初始化过，跳过第一步检查", true);
        vm.ShowTabCommand.Execute("play");
        vm.UseBuiltInNodes = profileUsesBuiltIn;

        // "Connection problems?" drawer (§6.18): from the lobby footer, Esc closes, Ctrl+Shift+D toggles, 560 wide (full width when compact).
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Check("连接有问题？：大厅底部有入口", Press(Visible("LobbyProblemsButton") ?? page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "LobbyProblemsButton" && b.IsEffectivelyVisible)) && vm.DiagnosticsOpen);
        await Task.Delay(300);
        var drawer = page.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "DiagnosticsDrawer");
        Check("连接有问题？：抽屉宽 560，焦点在【检测我的网络】", drawer is { IsEffectivelyVisible: true } && Math.Abs(drawer.Bounds.Width - 560) < 1
            && page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DiagnosticsCheckButton")?.IsFocused == true, drawer?.Bounds.Width.ToString() ?? "none");
        Check("连接有问题？：有建议按钮和折叠的技术细节", page.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SwitchRelayButton" && b.IsEffectivelyVisible)
            && page.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "DiagnosticsDetails") is { IsExpanded: false });
        // The toolbox's "download a file" lives here now: folded away, a download test when the network looks wrong.
        var downloadTest = page.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "DownloadTest");
        Check("连接有问题？：最下面有折叠的【下载文件】（原来在工具箱）", downloadTest is { IsEffectivelyVisible: true, IsExpanded: false } && vm.Tools is not null);
        if (downloadTest is not null)
        {
            Dispatcher.UIThread.Invoke(() => downloadTest.IsExpanded = true);
            await Task.Delay(300);
            Check("下载文件：展开后有网址框和【开始下载】，没在下载时没有进度行", Dispatcher.UIThread.Invoke(() =>
                downloadTest.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "DownloadUrlBox" && t.IsEffectivelyVisible)
                && downloadTest.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "DownloadTestButton" && b.IsEffectivelyVisible)
                && !downloadTest.GetVisualDescendants().OfType<Controls.ProgressLine>().Any(l => l.IsVisible)));
            Dispatcher.UIThread.Invoke(() => downloadTest.IsExpanded = false);
        }
        await ShotAsync("连接有问题-1440");
        Dispatcher.UIThread.Invoke(() => PressKey(page.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DiagnosticsCheckButton"), Avalonia.Input.Key.Escape));
        Check("连接有问题？：Esc 关掉", !vm.DiagnosticsOpen);
        Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.D, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift));
        Check("连接有问题？：Ctrl+Shift+D 打开", vm.DiagnosticsOpen);
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        Check("连接有问题？：紧凑窗口铺满", drawer is not null && drawer.Bounds.Width >= page.Bounds.Width - 1, $"{drawer?.Bounds.Width}/{page.Bounds.Width}");
        await ShotAsync("连接有问题-600");
        Check("连接有问题？：【换中转服务器】去联机设置", Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SwitchRelayButton")) && !vm.DiagnosticsOpen && vm.IsSettingsTab);
        window.Width = 1440;
        window.Height = 900;
        vm.ShowTabCommand.Execute("play");
        await DrillAsync(vm);
        await LessonsAsync(vm);
    }

    /// <summary>The two lessons (official §9.5): five cards each with a picture, ← →, Esc, and each lesson's last step.</summary>
    private async Task LessonsAsync(MultiplayerViewModel vm)
    {
        await Task.Delay(300);
        var page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Button? Named(string name) => page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        Check("小课堂：大厅底部【第一次用？】打开第一张", Press(Named("LobbyLessonButton")) && vm.LessonOpen && vm.LessonKind == Amber.Core.Multiplayer.LessonKind.Multiplayer
            && vm.LessonStep == L.F("lesson.step", 1, 5) && vm.LessonTitle == L.T("lesson.mp.room.title"));
        await Task.Delay(300);
        var art = page.GetVisualDescendants().OfType<Amber.App.Views.Controls.LineArt>().First(a => a.Name == "LessonArt");
        Check("小课堂：有插画（≤160 高）、焦点在【下一张】", art.IsEffectivelyVisible && art.Picture == "room" && art.Bounds.Height is > 40 and <= 160
            && Named("LessonNextButton")?.IsFocused == true && Named("LessonPreviousButton") is null, $"{art.Bounds.Width}x{art.Bounds.Height}");
        await ShotAsync("小课堂-1-1440");
        Dispatcher.UIThread.Invoke(() => PressKey(Named("LessonNextButton")!, Avalonia.Input.Key.Right));
        Check("小课堂：→ 翻到第二张", vm.LessonIndex == 1 && vm.LessonHasPrevious);
        Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.Left));
        Check("小课堂：← 翻回第一张", vm.LessonIndex == 0);
        for (var i = 0; i < 4; i++)
        {
            Press(Named("LessonNextButton"));
            await Task.Delay(150);
            await ShotAsync($"小课堂-{i + 2}-1440");
        }
        Check("小课堂：最后一张给【连接有问题？】和【看完了】", vm.LessonIsLast && Named("LessonDiagnosticsButton") is not null && Named("LessonDoneButton") is not null
            && Named("LessonNextButton") is null && Named("LessonDrillButton") is null && art.Picture == "trouble");
        Check("小课堂：【连接有问题？】换成诊断抽屉", Press(Named("LessonDiagnosticsButton")) && !vm.LessonOpen && vm.DiagnosticsOpen);
        vm.DiagnosticsOpen = false;
        await Task.Delay(200);

        Check("安全小课：大厅底部【安全小课】打开第一张", Press(Named("LobbySafetyButton")) && vm.LessonOpen && vm.LessonKind == Amber.Core.Multiplayer.LessonKind.Safety
            && vm.LessonIndex == 0 && vm.LessonHeading == L.T("lesson.safety.heading"));
        await Task.Delay(200);
        await ShotAsync("安全小课-1-1440");
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.Right));
            await Task.Delay(150);
            await ShotAsync($"安全小课-{i + 2}-1440");
        }
        Check("安全小课：最后一张给【演练一遍】", vm.LessonIsLast && Named("LessonDrillButton") is not null && Named("LessonDiagnosticsButton") is null && art.Picture == "public");
        Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.Right));
        Check("安全小课：最后一张再 → 不越界、不关", vm.LessonIsLast && vm.LessonOpen);
        Check("安全小课：【演练一遍】打开演练", Press(Named("LessonDrillButton")) && !vm.LessonOpen && vm.DrillOpen && vm.DrillAsking);
        vm.DrillOpen = false;

        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        Dispatcher.UIThread.Invoke(() => vm.OpenLessonCommand.Execute("multiplayer"));
        await Task.Delay(300);
        var drawer = page.GetVisualDescendants().OfType<Border>().First(b => b.Name == "LessonDrawer");
        Check("小课堂：矮窗口铺满、插画藏起来、文字还在", drawer.Bounds.Width >= page.Bounds.Width - 1 && !art.IsEffectivelyVisible
            && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "LessonBodyText" && t.IsEffectivelyVisible), $"{drawer.Bounds.Width}/{page.Bounds.Width}");
        await ShotAsync("小课堂-600");
        Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.Escape));
        Check("小课堂：Esc 关掉", !vm.LessonOpen);
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(300);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.OpenLessonCommand.Execute("safety");
            while (vm.LessonHasNext) vm.LessonNextCommand.Execute(null);
        });
        await Task.Delay(200);
        Check("安全小课：【看完整文章】去帮助", Press(Named("LessonArticleButton")) && !vm.LessonOpen);
        await Task.Delay(500);
        Check("安全小课：帮助打开“给孩子用”", main.Shell.Router.Current.ToString().StartsWith("help/child-mode", StringComparison.Ordinal), main.Shell.Router.Current.ToString());
        main.Shell.Navigate("play");
        await Task.Delay(300);
    }

    /// <summary>Drill mode (official §9.5): lobby link, four steps with "No" focused, verdict + lesson, score, restart, Esc, no network.</summary>
    private async Task DrillAsync(MultiplayerViewModel vm)
    {
        await Task.Delay(300);
        var page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Button? Named(string name) => page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        var requests = 0;
        void Count(Amber.Core.Net.NetworkCategory? c, Uri? u) => Interlocked.Increment(ref requests);
        Amber.Core.Net.NetworkPolicy.Requested += Count;
        try
        {
            Check("演练：大厅底部有【演练一遍】", Press(Named("LobbyDrillButton")) && vm.DrillOpen && vm.DrillAsking);
            await Task.Delay(300);
            Check("演练：第一步写两个码、焦点在【不让进】", Named("DrillNoButton")?.IsFocused == true
                && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "DrillShownCodeText" && t.IsEffectivelyVisible && t.Text == "7Q4M-2KXP")
                && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "DrillSaidCodeText" && t.IsEffectivelyVisible && t.Text == "7Q4M-2KXB"));
            await ShotAsync("演练-1440");
            Check("演练：码对不上还让进，写“不对”和这一课", Press(Named("DrillYesButton")) && vm.DrillExplaining && !vm.DrillRight
                && vm.DrillLesson == L.T("drill.lesson.code_mismatch") && Named("DrillYesButton") is null);
            await Task.Delay(200);
            Check("演练：讲解时焦点在【下一步】", Named("DrillNextButton")?.IsFocused == true);
            await ShotAsync("演练-讲解-1440");
            Press(Named("DrillNextButton"));
            await Task.Delay(200);
            Check("演练：码一样时让进是对的", vm.DrillProgress == L.F("drill.step", 2, 4) && Press(Named("DrillYesButton")) && vm.DrillRight);
            Press(Named("DrillNextButton"));
            await Task.Delay(200);
            Check("演练：程序文件写文件名，不收是对的", vm.DrillPrompt.Contains("free-skins.exe") && !vm.DrillShowsCodes && Press(Named("DrillNoButton")) && vm.DrillRight);
            Press(Named("DrillNextButton"));
            await Task.Delay(200);
            Check("演练：远程控制，拒绝是对的", vm.DrillYesText == L.T("drill.yes.remote") && Press(Named("DrillNoButton")) && vm.DrillRight);
            Press(Named("DrillNextButton"));
            await Task.Delay(300);
            Check("演练：结束写成绩（3 / 4）", vm.DrillDone && vm.DrillScore == L.F("drill.done", 3, 4)
                && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "DrillScoreText" && t.IsEffectivelyVisible));
            await ShotAsync("演练-成绩-1440");
            Check("演练：【再来一次】回到第一步", Press(Named("DrillRestartButton")) && vm.DrillAsking && vm.DrillProgress == L.F("drill.step", 1, 4));
            window.Width = 600;
            window.Height = 420;
            await Task.Delay(500);
            var drawer = page.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "DrillDrawer");
            Check("演练：紧凑窗口铺满", drawer is not null && drawer.Bounds.Width >= page.Bounds.Width - 1, $"{drawer?.Bounds.Width}/{page.Bounds.Width}");
            await ShotAsync("演练-600");
            Dispatcher.UIThread.Invoke(() => PressKey(page, Avalonia.Input.Key.Escape));
            Check("演练：Esc 关掉", !vm.DrillOpen);
            Check("演练：全程没有网络请求", requests == 0, requests.ToString());
        }
        finally
        {
            Amber.Core.Net.NetworkPolicy.Requested -= Count;
            window.Width = 1440;
            window.Height = 900;
            vm.DrillOpen = false;
            vm.ShowTabCommand.Execute("play");
        }
    }

    /// <summary>The public room list with stand-in rooms (no network): search, version, walk-right-in, sort, empty states, keys.</summary>
    private async Task PublicListAsync(MultiplayerViewModel vm)
    {
        var page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Amber.Core.Multiplayer.Public.RoomListing Room(string name, string host, string game, int players, Amber.Core.Multiplayer.Public.RoomVisibility mode, bool approval) => new()
        {
            Id = name, Name = name, Host = host, Game = game, Players = players, Mode = mode, Approval = approval, HostKey = new byte[32], Updated = DateTimeOffset.Now,
        };
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("play");
            vm.Where = WhereFilter.Public;
            vm.IsPublicConnected = true;
            vm.PublicRooms.Clear();
        });
        await Task.Delay(300);
        Check("公开房间：连上但没有房间时写出来并给【刷新】", vm.ShowNoPublicRooms && !vm.ShowPublicSetup && vm.ShowPublicRoomList
            && page.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "WherePublicRooms" && c.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.PublicRooms.Add(new PublicRoomItem(Room("Sky island", "Alex", "1.20.1 Fabric", 5, Amber.Core.Multiplayer.Public.RoomVisibility.Public, true)));
            vm.PublicRooms.Add(new PublicRoomItem(Room("Build contest", "Bo", "1.21.4", 2, Amber.Core.Multiplayer.Public.RoomVisibility.Password, true)));
            vm.PublicRooms.Add(new PublicRoomItem(Room("Open farm", "Cy", "1.20.1 Fabric", 9, Amber.Core.Multiplayer.Public.RoomVisibility.Public, false)));
        });
        await Task.Delay(300);
        Check("公开房间：人多的在前", vm.VisibleRooms.Select(r => r.Listing.Name).SequenceEqual(["Open farm", "Sky island", "Build contest"]));
        Check("公开房间：列表里有带锁的房间时才出现密码框", vm.ShowPublicRoomPassword
            && page.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "PublicRoomPasswordBox" && t.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() => vm.Where = WhereFilter.All);
        await Task.Delay(200);
        Check("去哪玩 › 全部：有公开房间时也列出来", vm.ShowPublicRoomList && !vm.ShowWhereEmpty
            && page.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.Name == "PublicRoomList" && i.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() => vm.Where = WhereFilter.Public);
        await Task.Delay(200);
        Check("公开房间：版本筛选列出全部版本和每个版本", vm.RoomGameChoices.Count == 3 && vm.SelectedRoomGame?.Game == "");
        await ShotAsync("公开房间-列表-1440");
        var search = page.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "RoomSearchBox");
        Dispatcher.UIThread.Invoke(() => PressKey(page.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "PublicRoomList"), Avalonia.Input.Key.Oem2));
        await Task.Delay(200);
        Check("公开房间：/ 跳到搜索框", search?.IsFocused == true);
        Dispatcher.UIThread.Invoke(() => search!.Text = "alex");
        await Task.Delay(200);
        Check("公开房间：搜索房名或房主", vm.VisibleRooms.Count == 1 && vm.VisibleRooms[0].Listing.Name == "Sky island");
        Dispatcher.UIThread.Invoke(() => search!.Text = "zzz");
        await Task.Delay(200);
        Check("公开房间：没有符合的写出来", vm.ShowNoMatchingRooms && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "NoMatchingRoomsText" && t.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() =>
        {
            search!.Text = "";
            vm.OpenRoomsOnly = true;
        });
        await Task.Delay(200);
        Check("公开房间：只看能直接进的", vm.VisibleRooms.Count == 1 && vm.VisibleRooms[0].Listing.Name == "Open farm");
        Check("公开房间：列表里没有带锁的房间时不出现密码框", !vm.ShowPublicRoomPassword
            && !page.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "PublicRoomPasswordBox" && t.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.OpenRoomsOnly = false;
            vm.SelectedRoomGame = vm.RoomGameChoices.First(g => g.Game == "1.21.4");
        });
        await Task.Delay(200);
        Check("公开房间：按版本筛选", vm.VisibleRooms.Count == 1 && vm.VisibleRooms[0].Listing.Name == "Build contest");
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        await ShotAsync("公开房间-列表-600");
        window.Width = 1440;
        window.Height = 900;
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.SelectedRoomGame = vm.RoomGameChoices[0];
            vm.PublicRooms.Clear();
            vm.IsPublicConnected = false;
            vm.Where = WhereFilter.All;
        });
        await Task.Delay(300);
    }

    /// <summary>
    /// The open-a-room card's four choices: defaults, each option with what it means, greyed-out options with the reason,
    /// "no server" changing the button, protection following the transport, and the public options.
    /// </summary>
    private async Task OpenChoicesAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        ComboBox? Box(string name) => page.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        Control? Shown(string name) => page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        TextBlock? Text(string name) => page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == name && t.IsEffectivelyVisible);
        var (who, route, protection, scope) = (Box("OpenWhoBox"), Box("OpenRouteBox"), Box("OpenProtectionBox"), Box("OpenScopeBox"));
        Check("开房间：四个选择都在（谁能进、怎么连、保护、范围）", who is not null && route is not null && protection is not null && scope is not null
            && who.ItemCount == 3 && route.ItemCount == 3 && protection.ItemCount == 3 && scope.ItemCount == 2);
        Check("开房间：默认是只有我邀请的人、自动、标准、只联这个游戏", vm.OpenAccess == RoomAccess.Invited && vm.OpenRoute == RoomRoute.Auto
            && vm.SelectedProtection?.Id == nameof(RoomProtection.Standard) && vm.SelectedScope.Id == nameof(RoomScope.Game)
            && ReferenceEquals(who?.SelectedItem, vm.WhoChoices[0]) && ReferenceEquals(route?.SelectedItem, vm.RouteChoices[0]),
            $"{vm.OpenAccess} {vm.OpenRoute} {vm.SelectedProtection?.Id} {vm.SelectedScope.Id}");
        // No network is connected in this scope: friends and public are greyed out, and say why.
        Check("开房间：没连网络时“朋友可以敲门”和“公开”是灰的，写原因", !vm.WhoChoices[1].IsEnabled && !vm.WhoChoices[2].IsEnabled
            && vm.WhoChoices[2].Line == L.T("multiplayer.open.blocked.network") && vm.WhoChoices[0].Line == L.T("multiplayer.open.who.invited_detail"),
            vm.WhoChoices[1].Line + " / " + vm.WhoChoices[2].Line);
        if (who is not null)
        {
            Dispatcher.UIThread.Invoke(() => who.IsDropDownOpen = true);
            await Task.Delay(300);
            var items = Dispatcher.UIThread.Invoke(() => Enumerable.Range(0, who.ItemCount).Select(i => who.ContainerFromIndex(i) as ComboBoxItem).ToList());
            Check("开房间：下拉里每项写它的意思，灰的那项点不了", items.Count == 3 && items[0] is { IsEnabled: true } && items[2] is { IsEnabled: false }
                && items[0]!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("multiplayer.open.who.invited_detail"))
                && items[2]!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("multiplayer.open.blocked.network")),
                string.Join(",", items.Select(i => i?.IsEnabled.ToString() ?? "none")));
            await ShotAsync("开房间-谁能进");
            Dispatcher.UIThread.Invoke(() => who.IsDropDownOpen = false);
            Dispatcher.UIThread.Invoke(() => who.SelectedItem = vm.WhoChoices[2]);
            await Task.Delay(150);
            Check("开房间：选灰的那项不生效", vm.OpenAccess == RoomAccess.Invited && ReferenceEquals(who.SelectedItem, vm.WhoChoices[0]), vm.OpenAccess.ToString());
        }
        // Connected with a verified friend: both can be picked; "public" shows the room name and the optional password.
        Dispatcher.UIThread.Invoke(() => vm.IsPublicConnected = true);
        await Task.Delay(150);
        Check("开房间：连上网络后“公开”能选，没有朋友时“朋友可以敲门”写原因", vm.WhoChoices[2].IsEnabled
            && (vm.WhoChoices[1].IsEnabled || vm.WhoChoices[1].Line == L.T("multiplayer.open.blocked.no_friends")), vm.WhoChoices[1].Line);
        Dispatcher.UIThread.Invoke(() => who!.SelectedItem = vm.WhoChoices[2]);
        await Task.Delay(200);
        Check("开房间：选“公开”后出现房间名和可选的密码", vm.ShowPublicOptions && Shown("OpenRoomNameBox") is not null && Shown("OpenRoomPasswordBox") is not null);
        await ShotAsync("开房间-公开");

        // Relays only: just a choice. No server: the button makes a sealed direct code, protection is not asked, and
        // friends / public cannot be picked (they need a network).
        Dispatcher.UIThread.Invoke(() => route!.SelectedItem = vm.RouteChoices[1]);
        await Task.Delay(150);
        Check("开房间：只走中转能选，按钮不变", vm.OpenRoute == RoomRoute.RelayOnly && Text("LobbyHostButtonText")?.Text != L.T("multiplayer.open.make_sealed") && vm.OpenAccess == RoomAccess.Public);
        Dispatcher.UIThread.Invoke(() => route!.SelectedItem = vm.RouteChoices[2]);
        await Task.Delay(200);
        Check("开房间：不经服务器时按钮变成【生成加密直连码】，下面写怎么用", vm.IsServerlessChosen && Text("LobbyHostButtonText")?.Text == L.T("multiplayer.open.make_sealed")
            && Shown("OpenServerlessNote") is not null && Shown("PlayAndHostHint") is null, Text("LobbyHostButtonText")?.Text ?? "none");
        Check("开房间：不经服务器时回到“只有我邀请的人”，保护那一格不能选", vm.OpenAccess == RoomAccess.Invited && !vm.WhoChoices[2].IsEnabled
            && vm.WhoChoices[2].Line == L.T("multiplayer.open.blocked.serverless") && !vm.ProtectionEnabled && protection is { IsEnabled: false }, vm.WhoChoices[2].Line);
        await ShotAsync("开房间-不经服务器");
        Dispatcher.UIThread.Invoke(() =>
        {
            route!.SelectedItem = vm.RouteChoices[0];
            vm.IsPublicConnected = false;
        });
        await Task.Delay(150);

        // Protection is a view over the transport: picking a level sets it, a hand-set transport shows as customised.
        var transport = vm.SelectedTransport;
        var disguise = vm.DisguiseAsMinecraft;
        Dispatcher.UIThread.Invoke(() => protection!.SelectedItem = vm.ProtectionChoices[1]);
        await Task.Delay(150);
        Check("开房间：选“加强”换成对应的传输方式", vm.SelectedTransport.Mode == Amber.Core.Multiplayer.Transport.TransportMode.High && !vm.IsProtectionCustom);
        Dispatcher.UIThread.Invoke(() => vm.SelectedTransport = vm.TransportChoices.First(c => c.Mode == Amber.Core.Multiplayer.Transport.TransportMode.General));
        await Task.Delay(200);
        Check("开房间：手动改过传输方式时写“已自定义”，给【恢复标准】", vm.IsProtectionCustom && Shown("OpenProtectionCustomRow") is not null
            && page.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ResetProtectionButton" && b.IsEffectivelyVisible) && protection?.SelectedItem is null);
        Check("开房间：【恢复标准】回到标准", Press(page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ResetProtectionButton" && b.IsEffectivelyVisible))
            && vm.SelectedProtection?.Id == nameof(RoomProtection.Standard) && Shown("OpenProtectionCustomRow") is null);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.SelectedTransport = transport;
            vm.DisguiseAsMinecraft = disguise;
        });
        if (!vm.CanUseFullLan)
            Check("开房间：这台电脑用不了整个局域网时那一项是灰的，写原因", !vm.ScopeChoices[1].IsEnabled && vm.ScopeChoices[1].Line.Length > 0, vm.ScopeChoices[1].Line);
        await Task.Delay(100);
    }

    /// <summary>"Where to play": the filter chips, the empty lines, a room a friend invited me to, and where saved servers go.</summary>
    private async Task WhereToPlayAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        Control? Shown(string name) => page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        RadioButton? Chip(string name) => page.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.Name == name && r.IsEffectivelyVisible);
        Check("去哪玩：四个筛选（全部、朋友、公开、服务器），默认全部", Chip("WhereAllButton") is { IsChecked: true } && Chip("WhereFriendsButton") is not null
            && Chip("WherePublicButton") is not null && Chip("WhereServersButton") is not null && vm.Where == WhereFilter.All);
        if (vm.JayServers.Servers.Count == 0)
            Check("去哪玩：什么都没有时写这里会出现什么", vm.ShowWhereEmpty && Shown("WhereEmptyText") is not null);
        Dispatcher.UIThread.Invoke(() => Chip("WhereFriendsButton")!.IsChecked = true);
        await Task.Delay(200);
        Check("去哪玩 › 朋友：没人邀请时写出来", vm.Where == WhereFilter.Friends && Shown("FriendRoomsEmptyText") is not null && Shown("WhereEmptyText") is null);
        Dispatcher.UIThread.Invoke(() => Chip("WherePublicButton")!.IsChecked = true);
        await Task.Delay(200);
        Check("去哪玩 › 公开：没有公共网络时说明从哪来、给【添加公共网络…】", vm.ShowPublicSetup ? Shown("WherePublicSetup") is not null && Shown("WhereAddNetworkButton") is not null
            : Shown("WherePublicConnect") is not null && Shown("WhereConnectButton") is not null);
        await ShotAsync("去哪玩-公开-没有网络");
        Dispatcher.UIThread.Invoke(() => Chip("WhereServersButton")!.IsChecked = true);
        await Task.Delay(200);
        Check("去哪玩 › 服务器：没存过时写怎么加入", vm.JayServers.Servers.Count > 0 ? Shown("SavedServerList") is not null
            : Shown("SavedServersEmpty") is not null && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "NoJayServersText" && t.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() => Chip("WhereAllButton")!.IsChecked = true);
        await Task.Delay(200);

        // A friend's room: a mailed invite is one row with who it is from, until it runs out.
        using var friend = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        using var hostKey = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        var contact = new Amber.Core.Multiplayer.Contacts.Contact(friend.PublicKey, "Robin", true, DateTimeOffset.UtcNow);
        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, Amber.Core.Multiplayer.Transport.TunnelSettings.Default, boundTo: vm.Identity.PublicKey).Invite;
        Dispatcher.UIThread.Invoke(() => vm.ShowMailedInvites([new Amber.Core.Multiplayer.Contacts.MailedInvite(contact, invite, hostKey.PublicKey, "Robin's world", DateTimeOffset.UtcNow.AddMinutes(30))]));
        await Task.Delay(300);
        var row = vm.FriendRooms.FirstOrDefault();
        Check("去哪玩：朋友邀请我的房间排进列表，写是谁的", row is { Title: "Robin's world" } && row.Meta.Contains(L.F("multiplayer.where.friend_room.from", "Robin"))
            && Shown("FriendRoomList") is not null && !vm.ShowWhereEmpty, row?.Meta ?? "none");
        await ShotAsync("去哪玩-朋友的房间");
        var request = main.Service.Requests.Pending.FirstOrDefault(r => r.Spec.Kind == "contact-invite");
        if (request is not null) main.Service.Requests.Resolve(request, "ignore", Amber.Core.Activity.RequestSource.Bar);
        await Task.Delay(200);
        Check("去哪玩：在请求条里点“先不”后这一行和请求一起收掉，同一张邀请再来也不出现", Dispatcher.UIThread.Invoke(() =>
        {
            var gone = vm.FriendRooms.Count == 0 && vm.HeldFriendInvites == 0 && !main.Service.Requests.Pending.Any(r => r.Spec.Kind == "contact-invite");
            vm.ShowMailedInvites([new Amber.Core.Multiplayer.Contacts.MailedInvite(contact, invite, hostKey.PublicKey, "Robin's world", DateTimeOffset.UtcNow.AddMinutes(30))]);
            return gone && vm.FriendRooms.Count == 0;
        }));
        Dispatcher.UIThread.Invoke(() => vm.FriendRooms.Clear());
        await Task.Delay(100);
    }

    /// <summary>
    /// Contacts (zero-leak codes): my contact code, a pasted JAYC1 lands in the card, a request from someone, the four-emoji
    /// card with names for screen readers, confirming, and a mailed invite in the activity centre. The other side is a
    /// second contact book fed by hand, so nothing goes over the network.
    /// </summary>
    private async Task ContactsAsync(MultiplayerViewModel vm, MultiplayerPage page)
    {
        Control? Shown(string name) => page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        Button? Visible(string name) => page.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        var dir = Path.Combine(reportDir, "contacts");
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { Amber.Core.Multiplayer.Contacts.ContactBook.FileName, "peer.json" })
            if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));
        Dispatcher.UIThread.Invoke(() => vm.ContactsDir = dir);
        await Task.Delay(200);
        Check("Contacts: card with my JAYC1 contact code and the add box", Shown("ContactsCard") is not null && Shown("MyContactCodeText") is not null
            && vm.MyContactCode.StartsWith("JAYC1-") && Shown("ContactCodeBox") is not null && Shown("ContactsEmpty") is not null);

        using var peer = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        var code = Amber.Core.Multiplayer.Contacts.ContactCode.Format(peer.PublicKey);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("play");
            vm.JoinCode = code;
        });
        await Task.Delay(200);
        Check("Contacts: the paste box recognises a contact code", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.contact") && !vm.JoinCodeRecognitionWarns);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.JoinCode = "";
            vm.PasteCode(code);
        });
        await Task.Delay(300);
        Check("Contacts: Ctrl+V of a contact code fills the contacts box, nothing sent", vm.IsSettingsTab && vm.ContactCodeInput == code && !vm.HasContactRequests);
        Dispatcher.UIThread.Invoke(() => vm.ContactCodeInput = "");

        // They add me: request in the card and in the activity centre; I accept, they send their nonce, the emoji appear.
        var peerBook = new Amber.Core.Multiplayer.Contacts.ContactBook(Path.Combine(dir, "peer.json")) { Me = peer.PublicKey };
        var me = vm.Identity.PublicKey;
        Dispatcher.UIThread.Invoke(() => vm.Contacts.Receive(peer.PublicKey, peerBook.StartAdd(peer.PublicKey, me, "Robin")!, "Me"));
        await Task.Delay(300);
        Check("Contacts: someone asking shows Accept / Ignore and an activity request", vm.ContactRequests.FirstOrDefault()?.IsAskingMe == true
            && main.Service.Requests.Pending.Any(r => r.Spec.Kind == "contact" && r.Spec.Title.Contains("Robin")));
        Dispatcher.UIThread.Invoke(() =>
        {
            var reply = vm.Contacts.Accept(peer.PublicKey, "Me")!;
            var nonce = peerBook.Receive(me, reply, "Robin")!;
            vm.Contacts.Receive(peer.PublicKey, nonce, "Me");
        });
        await Task.Delay(400);
        var item = vm.ContactRequests.FirstOrDefault();
        var emoji = page.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "ContactEmoji" && i.IsEffectivelyVisible);
        Check("Contacts: the four emoji, big, each named for screen readers, same as the other side", item is { IsComparing: true } && emoji is not null
            && Avalonia.Automation.AutomationProperties.GetName(emoji) == item.EmojiSpoken && item.Emoji.Count == 4
            && item.Emoji.Select(e => e.Emoji).SequenceEqual(peerBook.Pending.Single().Sas!.Select(i => Amber.Core.Multiplayer.Contacts.ContactHandshake.Emoji[i]))
            && Visible("ConfirmContactButton") is not null && Visible("RejectContactButton") is not null, item?.EmojiSpoken);
        Dispatcher.UIThread.Invoke(() => emoji?.BringIntoView());
        await Task.Delay(300);
        await ShotAsync("contacts-emoji");
        Press(Visible("ConfirmContactButton"));
        await Task.Delay(300);
        Check("Contacts: confirming saves Robin as a contact", vm.ContactList.SingleOrDefault()?.Name == "Robin" && !vm.HasContactRequests && Shown("ContactsEmpty") is null);

        // A mailed invite from Robin: a request in the activity centre with [Join], timing out to Ignore.
        using var host = Amber.Core.Multiplayer.Transport.X25519KeyPair.Generate();
        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, Amber.Core.Multiplayer.Transport.TunnelSettings.Default, boundTo: me).Invite;
        Dispatcher.UIThread.Invoke(() => vm.ShowMailedInvites([new Amber.Core.Multiplayer.Contacts.MailedInvite(vm.ContactList[0].Contact, invite, host.PublicKey, "Robin's world", DateTimeOffset.UtcNow.AddMinutes(30))]));
        var mailed = main.Service.Requests.Pending.FirstOrDefault(r => r.Spec.Kind == "contact-invite");
        Check("Contacts: a mailed invite is an activity request with Join, defaulting to Ignore", mailed is not null && mailed.Spec.Title.Contains("Robin's world")
            && mailed.Spec.Choices.Any(c => c.Id == "join") && mailed.Spec.TimeoutChoice == "ignore");
        if (mailed is not null) main.Service.Requests.Resolve(mailed, "ignore", Amber.Core.Activity.RequestSource.Bar);
        Dispatcher.UIThread.Invoke(() => Shown("ContactListView")?.BringIntoView());
        await Task.Delay(300);
        await ShotAsync("contacts-list");
        Dispatcher.UIThread.Invoke(() => vm.RemoveContactCommand.Execute(vm.ContactList[0]));
        await Task.Delay(200);
        Check("Contacts: removing empties the list", !vm.HasContacts);
        Check("Contacts: removing a friend takes the room they invited me to off \"where to play\"", vm.FriendRooms.Count == 0);
    }
}
