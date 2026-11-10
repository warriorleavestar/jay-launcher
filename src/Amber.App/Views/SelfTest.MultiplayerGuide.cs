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
    /// The multiplayer lobby and tabs (official §6.14): no component download and no connection. Checks the real
    /// controls: four tabs, the two cards, the paste box that recognises every code, one amber button, Ctrl+V, codes
    /// pasted in the wrong box going to the right place, relay servers and the server wizard's first step.
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

        Check("联机页：四个页签", HasText("multiplayer.tab.play") && HasText("multiplayer.tab.public") && HasText("multiplayer.tab.servers") && HasText("multiplayer.tab.settings"));
        Check("大厅：开房间和加入朋友两张卡", vm.IsPlayTab && HasText("multiplayer.lobby.host_title") && HasText("multiplayer.lobby.join_title"));
        Check("大厅：宽窗口两张卡并排", page.LobbyLayout == MultiplayerPage.LobbyArrangement.SideBySide);
        Check("大厅：自动复制邀请码的勾选框看得见、默认开", vm.AutoCopyInvite
            && page.GetVisualDescendants().OfType<CheckBox>().Any(c => c.Name == "AutoCopyInviteBox" && c.IsEffectivelyVisible));
        Check("大厅：组件没装时底部状态行是注意色", vm.LobbyStatusWarns && vm.LobbyStatusText.Contains(L.T("multiplayer.lobby.status_component_missing")));

        // One lamp: no code → open-a-room is amber; a recognised code → join is amber.
        var host = Visible("LobbyHostButton");
        var join = Visible("LobbyJoinButton");
        Check("一盏灯：没贴码时开房间是琥珀，加入是蓝色", host?.Classes.Contains("play") == true && join?.Classes.Contains("play") == false && join?.Classes.Contains("primary") == true);
        var relayInvite = JayInvite.Create(new byte[16], Amber.Core.Multiplayer.Transport.TunnelSettings.Default).ToString();
        var roomCode = await FindAsync<TextBox>(t => t.Name == "JoinCodeBox");
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = relayInvite);
        await Task.Delay(200);
        Check("一盏灯：贴进邀请后琥珀移到加入", host?.Classes.Contains("play") == false && join?.Classes.Contains("play") == true);
        Check("万能粘贴框：认出房间邀请", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.invite") && !vm.JoinCodeRecognitionWarns && HasText("multiplayer.lobby.code.invite"));
        Check("万能粘贴框：邀请码默认打码", roomCode?.PasswordChar == '•' && !vm.JoinCodeShown);

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
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "");
        // "Open like last time": hidden until a room was opened once; says what it repeats.
        Check("和上次一样开：没开过房间时不显示", !vm.HasLastRoom && Shown("LastRoomRow") is null);
        main.Service.Settings.MultiplayerLastRoom = new Amber.App.Services.LastRoom { World = "新世界", Visibility = "Public" };
        vm.NotifyLastRoom();
        await Task.Delay(200);
        Check("和上次一样开：写上次的世界、要我同意、谁能找到", Shown("LastRoomRow") is not null && Visible("RepeatLastRoomButton") is not null
            && vm.LastRoomText == L.F("multiplayer.lobby.last_room", "新世界 · " + L.T("multiplayer.invite.terms_ask") + " · " + L.T("multiplayer.public.visibility.public")), vm.LastRoomText);
        await ShotAsync("和上次一样开");
        Press(Visible("RepeatLastRoomButton"));
        await Task.Delay(300);
        Check("和上次一样开：组件没装时先说要下载，不出错", vm.IsIdle && vm.Status == L.T("multiplayer.component.download_required"), vm.Status);
        main.Service.Settings.MultiplayerLastRoom = null;
        vm.NotifyLastRoom();
        await ShotAsync("联机大厅-1440");
        await LayoutHereAsync("联机大厅");

        // Relay servers live in multiplayer settings; built-in ones are on out of the box.
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
        var relayHeader = page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "RelayHeader");
        Check("联机设置：默认开着内置中转服务器，开箱就有", vm.UseBuiltInNodes && vm.HasNodes && HasText("multiplayer.nodes.builtin_toggle")
            && relayHeader?.Text == vm.NodesHeaderText,
            $"builtin={vm.UseBuiltInNodes} has={vm.HasNodes} header={vm.NodesHeaderText} count={RelayNodes.BuiltIn.Count}");
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
            && vm.Nodes.Count == 1 && vm.Nodes[0].Url == "tcp://example.invalid:11010");
        Check("中转服务器数量同步到标题和大厅状态行", relayHeader?.Text == L.F("multiplayer.nodes.header_count", 1)
            && vm.LobbyStatusText.Contains(L.F("multiplayer.lobby.status_relays", 1)));
        vm.RemoveNodeCommand.Execute(vm.Nodes.Single());
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

        // A direct invite in the join box moves to the direct box under "more ways".
        vm.MoreWaysOpen = false;
        const string directInvite = "JAY2D-eyJ2IjoxfQ";
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = directInvite);
        Check("加入框里的直连邀请：点加入", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinCommand))));
        await Task.Delay(300);
        var directCode = await FindAsync<TextBox>(t => t.PlaceholderText == L.T("multiplayer.direct.invite_placeholder"));
        Check("直连邀请转到“更多方式”的直连框", vm.MoreWaysOpen && roomCode!.Text == "" && directCode?.Text == directInvite && vm.IsIdle);
        Dispatcher.UIThread.Invoke(() => directCode!.Text = relayInvite);
        Check("直连框的邀请码转到加入框", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinDirectCommand)))
            && directCode!.Text == "" && roomCode!.Text == relayInvite);
        await Task.Delay(200);
        vm.CancelKnockCommand.Execute(null);
        Dispatcher.UIThread.Invoke(() => { roomCode!.Text = ""; directCode!.Text = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565"; });
        Check("直连框的旧房间码提示对方更新", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinDirectCommand)))
            && vm.Status == L.T("multiplayer.invite.old_code") && vm.IsIdle, vm.Status);
        Dispatcher.UIThread.Invoke(() => directCode!.Text = "JAY2R-eyJ2IjoxfQ");
        Check("直连框：点击加入识别回复码", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, vm.JoinDirectCommand))));
        await Task.Delay(200);
        Check("回复码误贴时提示交给房主", vm.Status == L.T("multiplayer.code.reply_misplaced") && vm.IsIdle);
        Dispatcher.UIThread.Invoke(() => { directCode!.Text = ""; port!.Text = ""; });
        vm.MoreWaysOpen = false;
        vm.SetStatus(null);

        // Ctrl+V anywhere: a code on the clipboard opens the lobby with it filled in (no knocking yet).
        main.Shell.Navigate("library");
        await Task.Delay(200);
        Check("Ctrl+V 贴邀请：打开大厅并填好，不自动敲门", main.PasteCode(relayInvite) && main.Shell.CurrentSection == Section.Play && vm.IsPlayTab
            && vm.JoinCode == relayInvite && !vm.IsKnocking && vm.IsIdle);
        Check("Ctrl+V 贴普通文字：不做事", !main.PasteCode("hello world"));
        main.Shell.Navigate("library");
        Check("Ctrl+V 贴好友码：到公开房间的查找框", main.PasteCode("JAYF1-ABCD-EFGH-JKMN-PQRS-TVWX") && vm.IsPublicTab && vm.FindCodeInput == "JAYF1-ABCD-EFGH-JKMN-PQRS-TVWX");
        vm.FindCodeInput = "";
        vm.JoinCode = "";

        // Routes: play/public, play/servers, play/settings.
        main.Shell.Navigate("play/servers");
        await Task.Delay(200);
        Check("路由 play/servers 打开服务器页签", vm.IsServersTab && HasText("multiplayer.servers.jay") && HasText("multiplayer.jayserver.title"));
        main.Shell.Navigate("play/public");
        await Task.Delay(200);
        Check("路由 play/public 打开公开房间", vm.IsPublicTab && HasText("multiplayer.public.title"));
        Check("公开房间：没有公共网络时说明从哪来、给【添加公共网络…】", vm.ShowPublicSetup && Visible("AddPublicNetworkButton") is not null
            && HasText("multiplayer.public.setup_body"));
        await ShotAsync("公开房间-1440");
        await PublicListAsync(vm);

        // Width tiers: medium stacks the cards, compact puts "join" first. (Leaving the page rebuilt its view: find it again.)
        main.Shell.Navigate("play");
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        foreach (var (w, h, expected) in new[] { (900, 700, MultiplayerPage.LobbyArrangement.Stacked), (600, 420, MultiplayerPage.LobbyArrangement.JoinFirst) })
        {
            window.Width = w;
            window.Height = h;
            await Task.Delay(500);
            Check($"大厅 {w}：卡片排法", page.LobbyLayout == expected, page.LobbyLayout.ToString());
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
        vm.ShowTabCommand.Execute("servers");
        vm.ShowMyServer = true;
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
        vm.ShowMyServer = false;
        vm.ShowJayServerList = true;
        await Task.Delay(200);
        Check("Jay 服务器：没加入过时写怎么做", vm.JayServers.Servers.Count > 0
            || page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "NoJayServersText" && t.IsEffectivelyVisible));
        vm.ShowTabCommand.Execute("play");

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
            vm.IsPublicConnected = true;
            vm.PublicRooms.Clear();
        });
        await Task.Delay(300);
        Check("公开房间：连上但没有房间时写出来并给【刷新】", vm.ShowNoPublicRooms && !vm.ShowPublicSetup);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.PublicRooms.Add(new PublicRoomItem(Room("Sky island", "Alex", "1.20.1 Fabric", 5, Amber.Core.Multiplayer.Public.RoomVisibility.Public, true)));
            vm.PublicRooms.Add(new PublicRoomItem(Room("Build contest", "Bo", "1.21.4", 2, Amber.Core.Multiplayer.Public.RoomVisibility.Password, true)));
            vm.PublicRooms.Add(new PublicRoomItem(Room("Open farm", "Cy", "1.20.1 Fabric", 9, Amber.Core.Multiplayer.Public.RoomVisibility.Public, false)));
        });
        await Task.Delay(300);
        Check("公开房间：人多的在前", vm.VisibleRooms.Select(r => r.Listing.Name).SequenceEqual(["Open farm", "Sky island", "Build contest"]));
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
        });
        await Task.Delay(300);
    }
}
