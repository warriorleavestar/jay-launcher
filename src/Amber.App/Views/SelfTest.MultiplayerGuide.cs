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
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "AMB-K7Q2M9XD-4HZP8RW2N6TC-25565");
        await Task.Delay(100);
        Check("万能粘贴框：旧 AMB- 码说请朋友更新", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.old") && vm.JoinCodeRecognitionWarns);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "hello");
        await Task.Delay(100);
        Check("万能粘贴框：认不出时写邀请码长什么样", vm.JoinCodeRecognition == L.T("multiplayer.lobby.code.unknown") && host?.Classes.Contains("play") == true);
        Dispatcher.UIThread.Invoke(() => roomCode!.Text = "");
        await ShotAsync("联机大厅-1440");
        await LayoutHereAsync("联机大厅");

        // Relay servers live in multiplayer settings; built-in ones are on out of the box.
        vm.ShowTabCommand.Execute("settings");
        await Task.Delay(300);
        Check("联机设置：分组都在", HasText("multiplayer.settings.identity") && HasText("multiplayer.settings.invites") && HasText("multiplayer.settings.component")
            && HasText("multiplayer.settings.diagnostics"));
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
        await ShotAsync("公开房间-1440");

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
        vm.ShowTabCommand.Execute("play");
        vm.UseBuiltInNodes = profileUsesBuiltIn;
    }
}
