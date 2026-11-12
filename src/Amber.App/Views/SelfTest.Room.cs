using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// The room (UI restructure: people, rooms, networks) with a stand-in room: no component, no connection. The page is
    /// put in host mode with two people, a chat line and a knock, and the real controls are checked: the banner (cover,
    /// faces with the friend tick and the link line, the connection mark, the one amber button), "who can see what" with
    /// the "relays only" switch, people | chat | details at three widths, a person's facts and "Share with them", the
    /// invite panel (two choices, the invite as a card, three lines, send to a friend, thirty days), the locked knock
    /// panel ("No" focused, Alt+N / Alt+Y, let in next time), the room settings drawer and Ctrl+Shift+I; then a guest's view.
    /// </summary>
    private async Task RoomConsoleAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        main.Shell.Navigate("play");
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        var page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        if (page is null)
        {
            // Say why: a page that fails to build (a bad resource or binding in its markup) throws here too.
            string why;
            try
            {
                Dispatcher.UIThread.Invoke(() => new MultiplayerPage());
                why = "the page builds, but it never became visible";
            }
            catch (Exception e)
            {
                why = e.ToString();
            }
            Check("房间：页面显示", false, why);
            return;
        }
        bool HasText(string text) => page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        T? Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        var alexKey = new byte[32];
        alexKey[0] = 1;
        var boKey = new byte[32];
        boKey[0] = 2;
        var alexFingerprint = MemberIdentity.FingerprintOf(alexKey);
        var hostRoom = RoomCredentials.Create(vm.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), vm.ChatName);
        var confirms = new List<string>();
        var answer = false;
        var savedConfirm = vm.Confirm;
        var savedTarget = vm.TargetInstance;
        var savedContactsDir = vm.ContactsDir;
        Dispatcher.UIThread.Invoke(() =>
        {
            // Alex is a friend I compared emoji with: the tick on the face comes from the contact book (a test one).
            vm.ContactsDir = Path.Combine(reportDir, "room-contacts");
            vm.Contacts.Import([new Amber.Core.Multiplayer.Contacts.Contact(alexKey, "Alex", true, DateTimeOffset.UtcNow)]);
            vm.RefreshContacts();
            vm.Confirm = (title, _, _) =>
            {
                confirms.Add(title);
                return Task.FromResult(answer);
            };
            vm.EnterTestRoom(hostRoom, host: true, lobby: true);
            vm.Members.Add(new MemberItem("Alex", alexKey, false, true));
            vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(alexFingerprint, alexKey, "Alex")));
            vm.BannedPeople.Add(new BannedItem(new BannedMember(boKey, "Bo")));
            vm.ChatLines.Add(new ChatLine(new ChatMessage(ChatKind.Message, alexFingerprint, "Alex", "hi!", DateTimeOffset.Now), false));
            vm.RememberInviteTerms(1, TimeSpan.FromMinutes(10), false);
        });
        await Task.Delay(400);
        void SetPeers(params PeerInfo[] peers) => Dispatcher.UIThread.Invoke(() =>
        {
            vm.Peers.Clear();
            foreach (var peer in peers) vm.Peers.Add(new PeerItem(peer));
        });
        void Click(CheckBox? box, bool on) => Dispatcher.UIThread.Invoke(() =>
        {
            if (box is not { IsEffectivelyEnabled: true, IsEffectivelyVisible: true }) return;
            box.IsChecked = on;
            box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        });
        int AmberButtons() => page.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("play"));

        Check("房间：顶栏写我的房间、人数", vm.ShowRoomHeader && Named<StackPanel>("RoomHeaderArea")?.IsEffectivelyVisible == true
            && vm.RoomHeaderText.StartsWith(L.T("multiplayer.room.my_room")) && vm.RoomHeaderText.Contains(L.P("multiplayer.room.people", 2, 2)), vm.RoomHeaderText);
        Check("房间：顶栏不随内容滚动（在内容区外面）", Named<StackPanel>("RoomHeaderArea") is { } area && !page.FindControl<ScrollViewer>("MpBody")!.IsVisualAncestorOf(area));
        Check("房间：顶栏有【邀请】、⋯、【结束房间】", (Named<Button>("HeaderInviteButton")?.Content as string) == L.T("multiplayer.room.invite")
            && Named<Button>("HeaderInviteButton")!.IsEffectivelyVisible && Named<Button>("RoomMenuButton")?.IsEffectivelyVisible == true
            && (Named<Button>("EndRoomButton")?.Content as string) == L.T("multiplayer.room.end"));
        Check("横幅：封面压暗，上面写房间名、人数、保护", Named<Border>("RoomHeader") is { Background: not null } banner && banner.Classes.Contains("scene")
            && Named<Border>("RoomCoverShade")?.IsEffectivelyVisible == true && Named<TextBlock>("RoomTitleText")?.Text == L.T("multiplayer.room.my_room")
            && vm.PeopleCountText == L.P("multiplayer.room.people", 2, 2) && Named<Border>("PeopleCountChip")?.IsEffectivelyVisible == true
            && vm.RoomProtectionText == L.F("multiplayer.room.protection", L.T("multiplayer.open.protection.standard")) && Named<Border>("ProtectionChip")?.IsEffectivelyVisible == true,
            vm.PeopleCountText + " / " + vm.RoomProtectionText);
        Check("横幅：没人连上时连接标记写“等朋友进来”", vm.IsMarkWaiting && Named<TextBlock>("ConnectionMarkText")?.Text == L.T("multiplayer.room.waiting_friends"), vm.ConnectionMarkText);
        Check("横幅：房主的游戏已经开着时没有琥珀按钮", !vm.ShowEnterGame && AmberButtons() == 0, AmberButtons().ToString());
        var faces = Named<ItemsControl>("RoomFaces");
        Check("横幅：一排头像，一人一个，核对过的朋友有对勾", faces is not null && faces.GetVisualDescendants().OfType<Border>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("face")) == 2
            && faces.GetVisualDescendants().OfType<Border>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("friend-tick")) == 1
            && vm.People[1].IsFriend && !vm.People[0].IsFriend);
        Check("房间：宽窗口三栏（人 | 聊天 | 详情）", page.RoomLayout == MultiplayerPage.RoomArrangement.ThreeColumns
            && Named<Border>("PeopleColumn")!.IsEffectivelyVisible && Named<Border>("ChatColumn")!.IsEffectivelyVisible && Named<StackPanel>("DetailsColumn")!.IsEffectivelyVisible,
            page.RoomLayout + " " + page.Bounds.Width);
        Check("房间：大厅收起来了", Named<Grid>("LobbyGrid")?.IsEffectivelyVisible == false);
        Check("人：我排第一（房主），Alex 在线", vm.People.Count == 2 && vm.People[0].IsMe && vm.People[0].DisplayName == L.F("multiplayer.people.me_host", vm.ChatName)
            && vm.People[1].Name == "Alex" && vm.People[1].IsOnline && vm.People[1].Status == L.T("multiplayer.people.online"));

        // The connection mark and the line under each face follow the peer list.
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "p2p", 18, "udp"));
        await Task.Delay(300);
        bool Line(string kind) => faces!.GetVisualDescendants().OfType<Border>().Any(b => b.IsEffectivelyVisible && b.Classes.Contains("link-line") && b.Classes.Contains(kind));
        Check("连接标记：直连时写“直连 · 18 ms”，头像下面是直连的线", vm.IsMarkDirect && Named<TextBlock>("ConnectionMarkText")?.Text == L.T("multiplayer.room.direct") + " · 18 ms"
            && Line("direct") && !Line("relayed") && vm.People[1].Status == vm.ConnectionMarkText, vm.ConnectionMarkText);
        Check("横幅：两种线的意思写在旁边（不只靠颜色）", Named<StackPanel>("RoomFacesLegend")?.IsEffectivelyVisible == true);
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "relay(2)", 82, "tcp"));
        await Task.Delay(300);
        Check("连接标记：经中转时写“经中转 · 82 ms”，线换成中转的", vm.IsMarkRelayed && Named<TextBlock>("ConnectionMarkText")?.Text == L.T("multiplayer.room.relayed") + " · 82 ms"
            && Line("relayed") && !Line("direct"), vm.ConnectionMarkText);
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "p2p", 18, "udp"), new PeerInfo("amber-guest", "10.144.144.3", "relay(2)", 82, "tcp"));
        await Task.Delay(300);
        Check("连接标记：有直连有中转时写各几个；分不清是谁的线就不画", vm.IsMarkMixed && vm.ConnectionMarkText == L.F("multiplayer.room.mark_mixed", 1, 1)
            && !Line("direct") && !Line("relayed") && !vm.People[1].HasLink, vm.ConnectionMarkText);
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "p2p", 18, "udp"));
        await Task.Delay(200);

        // The mark opens "who can see what": the fixed table, the rows that apply now, and the "relays only" switch.
        Check("谁能看到什么：点连接标记打开", Press(Named<Button>("ConnectionMarkButton")) && vm.WhoSeesOpen);
        await Task.Delay(400);
        var seesRows = Named<ItemsControl>("WhoSeesRows");
        bool Now(string row) => vm.WhoSeesLines.Single(l => l.Row.Id == row).Now;
        int NowTags() => seesRows!.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("sees-now") && b.IsEffectivelyVisible);
        Check("谁能看到什么：固定的五行，写是 / 否，不写保证", Named<Border>("WhoSeesCard")?.IsEffectivelyVisible == true && vm.WhoSeesLines.Count == 5
            && seesRows!.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("sees-row") && b.IsEffectivelyVisible) == 5
            && HasText(L.T("multiplayer.sees.row.operator")) && HasText(L.T("multiplayer.sees.only_them"))
            && vm.WhoSeesLines.All(l => !(l.Who + l.Ip + l.Content).Contains("安全") && !(l.Who + l.Ip + l.Content).Contains("放心")));
        Check("谁能看到什么：标出现在的情况（没开只走中转的那一行）", Now("direct") && !Now("relayed")
            && seesRows!.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("sees-row") && b.Classes.Contains("now")) == 1
            && Named<TextBlock>("WhoSeesNowHint")?.IsEffectivelyVisible == true);
        var nowLine = vm.WhoSeesLines.Single(l => l.Now);
        Check("谁能看到什么：“现在”写成字（不只靠底色），读屏也读得到", NowTags() == 1 && HasText(L.T("multiplayer.sees.now"))
            && nowLine.SpokenName == L.F("multiplayer.sees.row_name_now", nowLine.Who, nowLine.Ip, nowLine.Content)
            && seesRows!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("sees-row")).All(b => !string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(b))), nowLine.SpokenName);
        Check("谁能看到什么：开了只走中转那一行写“否”", vm.WhoSeesLines.Single(l => l.Row.Id == "relayed").Ip == L.T("multiplayer.sees.no"));
        var relayBox = Named<CheckBox>("RelayOnlyBox");
        Check("只走中转：表下面的开关，默认关，写清楚换来什么", relayBox is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, IsChecked: false }
            && HasText(L.T("multiplayer.sees.relay_only")) && !vm.MyRelayOnly);
        await ShotAsync("房间-谁能看到什么-1440");
        RoomCode? switchedTo = null;
        vm.TestSwitchNetwork = next =>
        {
            switchedTo = next;
            return true;
        };
        Click(relayBox, true);
        Check("只走中转：打开后我这边改成不直连（联机组件按新设置重启）", await WaitAsync(() => vm.MyRelayOnly && vm.CanChangeRelayOnly, 3) && switchedTo is { RelayOnly: true }
            && relayBox!.IsChecked == true && vm.Status == L.T("multiplayer.sees.relay_only.turned_on")
            && vm.RoomFacts.Any(f => f.Value == L.T("multiplayer.open.route.relay")), vm.Status);
        Check("只走中转：开关写明房间里的人看不到我的 IP、延迟会高一些，打开后也这么说", L.T("multiplayer.sees.relay_only").Contains("看不到我的 IP")
            && L.T("multiplayer.sees.relay_only.turned_on").Contains("看不到你的 IP") && HasText(L.T("multiplayer.sees.relay_only")));
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "relay(2)", 82, "tcp"));
        await Task.Delay(300);
        Check("谁能看到什么：开着只走中转、连接也确实经中转时，“现在”换到那一行", Now("relayed") && !Now("direct") && NowTags() == 1);
        vm.TestSwitchNetwork = _ => false;
        Click(relayBox, false);
        await Task.Delay(400);
        Check("只走中转：没换成时开关回到真实状态，并说一句没换成", vm.MyRelayOnly && relayBox!.IsChecked == true && vm.Status == L.T("multiplayer.sees.relay_only.failed"), vm.Status);
        vm.TestSwitchNetwork = _ => true;
        Click(relayBox, false);
        Check("只走中转：能关掉", await WaitAsync(() => !vm.MyRelayOnly && vm.CanChangeRelayOnly, 3) && relayBox!.IsChecked == false);
        Check("谁能看到什么：关掉后就算这会儿还经中转，“现在”也回到没开只走中转的那一行（Jay 会接着试直连）", vm.IsMarkRelayed && Now("direct") && !Now("relayed"));
        SetPeers(new PeerInfo("amber-guest", "10.144.144.2", "p2p", 18, "udp"));
        await Task.Delay(300);
        Check("谁能看到什么：✕ 收起", Press(Named<Button>("WhoSeesCloseButton")) && !vm.WhoSeesOpen);
        await Task.Delay(200);

        // Pick Alex (from the banner): safety code, friend or not, the link, actions; "Message" opens private chat with them.
        var alexFace = faces!.GetVisualDescendants().OfType<Button>().LastOrDefault(b => b.IsEffectivelyVisible && b.DataContext is PersonItem { Name: "Alex" });
        Check("人：点横幅上的头像就选中这个人", Press(alexFace) && vm.SelectedPerson?.Name == "Alex");
        await Task.Delay(300);
        Check("人：选中后显示安全码和按钮", Named<SelectableTextBlock>("PersonSafetyCode")?.Text == alexFingerprint
            && Named<Button>("PersonChatButton")?.IsEffectivelyVisible == true && Named<Button>("PersonTrustButton")?.IsEffectivelyVisible == true
            && Named<Button>("PersonRemoveButton")?.IsEffectivelyVisible == true);
        Check("人：写是不是朋友、怎么连的", Named<Border>("PersonFriendChip") is { IsEffectivelyVisible: true } friendChip && friendChip.Classes.Contains("ok")
            && vm.SelectedPerson!.FriendText == L.T("multiplayer.people.friend") && Named<Border>("PersonLinkChip")?.IsEffectivelyVisible == true
            && vm.SelectedPerson.LinkText == L.T("multiplayer.room.direct") + " · 18 ms", vm.SelectedPerson?.Summary);

        // "Share with them": three things in one place, each behind the locked confirmation.
        Check("和 TA 共享：远程协助、转发游戏、经我上网三样在一起", Named<StackPanel>("ShareGroup")?.IsEffectivelyVisible == true && Named<StackPanel>("ShareRemoteRow")?.IsEffectivelyVisible == true
            && Named<StackPanel>("ShareProxyRow")?.IsEffectivelyVisible == true && Named<StackPanel>("ShareExitRow")?.IsEffectivelyVisible == true
            && Named<TextBlock>("ShareExitText")?.Text == L.T("multiplayer.share.exit.off"));
        confirms.Clear();
        answer = false;
        if (vm.CanAskRemoteHelp)
        {
            Press(Named<Button>("ShareRemoteButton"));
            await Task.Delay(300);
            Check("和 TA 共享：远程协助先走确认，写明是谁", confirms.Count == 1 && confirms[0] == L.F("multiplayer.share.remote.confirm_title", "Alex") && !vm.RemoteWaiting, string.Join("|", confirms));
        }
        else Check("和 TA 共享：这台电脑用不了远程协助时按钮灰掉并写原因", Named<Button>("ShareRemoteButton")?.IsEffectivelyEnabled == false && vm.HasRemoteHelpNote, vm.RemoteHelpNote);
        var proxyBox = Named<CheckBox>("ShareProxyBox");
        EasyTierOptions? restarted = null;
        vm.TestRestartForProxy = next =>
        {
            restarted = next;
            return true;
        };
        Dispatcher.UIThread.Invoke(() => vm.UseTestOptions(new EasyTierOptions(hostRoom, true, [], 0)));
        await Task.Delay(200);
        confirms.Clear();
        Click(proxyBox, true);
        await Task.Delay(400);
        Check("和 TA 共享：转发游戏先走确认；点取消就不开", confirms.Count == 1 && confirms[0] == L.T("multiplayer.share.proxy.confirm_title") && !vm.SharingGameProxy
            && restarted is null && proxyBox!.IsChecked == false, string.Join("|", confirms));
        answer = true;
        Click(proxyBox, true);
        Check("和 TA 共享：确认后打开，并写明对房间里每个人生效", await WaitAsync(() => vm.SharingGameProxy && vm.CanShareGameProxy, 3) && restarted is { AllowProxy: true }
            && proxyBox!.IsChecked == true && HasText(L.T("multiplayer.share.proxy.line")) && vm.Status == L.T("multiplayer.share.proxy.turned_on"), vm.Status);
        await ShotAsync("房间-和TA共享-1440");
        confirms.Clear();
        Click(proxyBox, false);
        Check("和 TA 共享：关掉不用确认", await WaitAsync(() => !vm.SharingGameProxy && vm.CanShareGameProxy, 3) && confirms.Count == 0 && proxyBox!.IsChecked == false);
        Dispatcher.UIThread.Invoke(() => vm.UseTestOptions(null));
        Check("人：私聊转到私聊页签并选好人", Press(Named<Button>("PersonChatButton")) && vm.PrivateChatMode && vm.SelectedPeer?.Entry.Fingerprint == alexFingerprint);
        await Task.Delay(200);
        Check("聊天：私聊和房间聊天切换", Named<StackPanel>("PrivateChat")?.IsEffectivelyVisible == true && Named<StackPanel>("RoomChat")?.IsEffectivelyVisible == false);
        vm.RoomChatMode = true;
        await Task.Delay(200);
        Check("聊天：回到房间聊天", Named<StackPanel>("RoomChat")?.IsEffectivelyVisible == true && HasText("hi!"));

        // Invite panel: two choices, three lines that are always there, the invite as a card.
        var createButton = Named<Button>("CreateFirstInviteButton");
        Check("邀请：还没邀请时【生成邀请】是这张卡的主按钮", createButton is { IsEffectivelyVisible: true } && createButton.Classes.Contains("primary")
            && Named<Border>("InviteCodeCard")?.IsEffectivelyVisible == false);
        Check("邀请：条款写成一句话", vm.InviteTerms == L.F("multiplayer.invite.terms", L.P("multiplayer.invite.terms_uses", 1, 1),
            L.F("multiplayer.invite.terms_life", L.P("multiplayer.invite.minutes", 10, 10)), L.T("multiplayer.invite.terms_ask")), vm.InviteTerms);
        var lifeBox = Named<ComboBox>("InviteLifeBox");
        var useBox = Named<ComboBox>("InviteUseBox");
        Check("邀请：两个下拉框，10 分钟 / 1 天 / 30 天，1 次 / 不限；默认 10 分钟、1 次", lifeBox is { IsEffectivelyVisible: true, ItemCount: 3 } && useBox is { IsEffectivelyVisible: true, ItemCount: 2 }
            && lifeBox.SelectedItem is InviteLifeChoice { Life: InviteLife.TenMinutes } && useBox.SelectedItem is InviteUseChoice { Use: InviteUse.Once }
            && vm.InviteLifeChoices.Select(c => c.Label).SequenceEqual(new[] { L.T("multiplayer.invite.life.ten_minutes"), L.T("multiplayer.invite.life.day"), L.T("multiplayer.invite.life.month") }));
        Check("邀请：常驻三行（码里有什么、拿到的人能做什么、什么时候失效）", Named<TextBlock>("InviteFactHolds") is { IsEffectivelyVisible: true } holds && holds.Text == L.T("multiplayer.invite.fact.holds")
            && Named<TextBlock>("InviteFactKnock")?.Text == L.T("multiplayer.invite.fact.knock") && Named<TextBlock>("InviteTermsText") is { IsEffectivelyVisible: true, Text.Length: > 0 });
        var autoBox = Named<CheckBox>("InviteAutoBox");
        Dispatcher.UIThread.Invoke(() => vm.InviteAutoApprove = true);
        await Task.Delay(200);
        Check("邀请：勾了自动批准，第二行跟着改", autoBox?.IsEffectivelyVisible == true && Named<TextBlock>("InviteFactKnock")?.Text == L.T("multiplayer.invite.fact.auto"));
        Dispatcher.UIThread.Invoke(() => vm.InviteAutoApprove = false);
        Check("邀请：【生成邀请】做出一张 10 分钟、1 次的", Press(createButton) && await WaitAsync(() => vm.HasInvite && vm.PendingInvites.Count == 1, 5), vm.Status);
        await Task.Delay(300);
        Check("邀请：是一张小卡片，写还剩多久、几次，不直接亮出码", Named<Border>("InviteCodeCard")?.IsEffectivelyVisible == true
            && Named<TextBlock>("InviteSummaryText")?.Text == L.F("multiplayer.invite.summary", L.F("multiplayer.invite.left", L.P("multiplayer.invite.minutes", 10, 10)), L.P("multiplayer.invite.uses_left", 1, 1))
            && Named<TextBox>("InviteCodeBox")?.IsEffectivelyVisible == false && Named<Button>("CopyInviteButton")?.IsEffectivelyVisible == true
            && !createButton!.Classes.Contains("primary"), vm.InviteSummary);
        Check("邀请：点【显示】才看到码（还是打码前的完整码）", Press(Named<Button>("ShowInviteButton")) && vm.InviteShown && Named<TextBox>("InviteCodeBox") is { IsEffectivelyVisible: true } codeBox
            && codeBox.Text == vm.TestInviteText);
        vm.InviteShown = false;
        Check("邀请：【发给朋友】打开朋友列表；没连网络时写原因", Press(Named<Button>("SendInviteButton")) && vm.InviteFriendsOpen && Named<StackPanel>("InviteFriendsPanel")?.IsEffectivelyVisible == true
            && (vm.CanMailInvites || Named<TextBlock>("InviteFriendsNote") is { IsEffectivelyVisible: true, Text.Length: > 0 }), vm.InviteFriendsNote);
        await Task.Delay(200);
        await ShotAsync("房间-1440");
        vm.InviteFriendsOpen = false;
        Dispatcher.UIThread.Invoke(() =>
        {
            lifeBox!.SelectedItem = vm.InviteLifeChoices[1];
            useBox!.SelectedItem = vm.InviteUseChoices[1];
            vm.InviteAutoApprove = true;
        });
        await Task.Delay(300);
        Check("邀请：不限次数时没有“自动批准”，原处写每次都先问你", autoBox?.IsEffectivelyVisible == false && !vm.CanInviteAutoApprove
            && Named<TextBlock>("InviteAlwaysAsksNote") is { IsEffectivelyVisible: true } asks && asks.Text == L.T("multiplayer.invite.always_asks")
            && Named<TextBlock>("InviteFactKnock")?.Text == L.T("multiplayer.invite.fact.knock"));
        Check("邀请：1 天、不限次数的一张", Press(createButton) && await WaitAsync(() => vm.PendingInvites.Count == 2, 5)
            && vm.InviteSummary == L.F("multiplayer.invite.summary", L.F("multiplayer.invite.left", L.P("multiplayer.invite.hours", 24, 24)), L.T("multiplayer.invite.use.unlimited"))
            && vm.PendingInvites.Any(i => i.Invite.Unlimited && i.Label.Contains(L.F("multiplayer.invite.pending_item_unlimited", "", "", "").Split(" · ")[1])), vm.InviteSummary);

        Check("邀请：就算之前勾过，不限次数的这张也照样先问你", vm.PendingInvites.All(i => !i.Invite.AutoApprove) && vm.InviteTerms.Contains(L.T("multiplayer.invite.terms_ask")), vm.InviteTerms);

        // Thirty days is the long-lived invite: no use count, a name to recognise it by, and a lobby key of its own.
        Dispatcher.UIThread.Invoke(() => lifeBox!.SelectedItem = vm.InviteLifeChoices[2]);
        await Task.Delay(300);
        Check("30 天邀请：次数固定成不限，多一行说明和“给谁的”", useBox is { IsEffectivelyEnabled: false, SelectedItem: InviteUseChoice { Use: InviteUse.Unlimited } }
            && Named<TextBlock>("InviteMonthNote")?.IsEffectivelyVisible == true && Named<TextBox>("LongInviteLabelBox")?.IsEffectivelyVisible == true);
        Check("30 天邀请：没有“自动批准”（说明里已经写每次敲门仍要你同意）", autoBox?.IsEffectivelyVisible == false && Named<TextBlock>("InviteAlwaysAsksNote")?.IsEffectivelyVisible == false
            && Named<TextBlock>("InviteFactKnock")?.Text == L.T("multiplayer.invite.fact.knock"));
        Dispatcher.UIThread.Invoke(() => vm.InviteAutoApprove = false);
        Check("30 天邀请：开始时没有", !vm.HasLongInvites);
        Dispatcher.UIThread.Invoke(() => vm.LongInviteLabel = "Bob");
        Press(createButton);
        await WaitAsync(() => vm.HasLongInvites, 5);
        await Task.Delay(300);
        var longItem = vm.LongInviteItems.FirstOrDefault();
        Check("30 天邀请：做好一张给 Bob 的，卡片写还剩 30 天、不限", longItem is { Label: "Bob" } && longItem.Left == L.P("multiplayer.longinvite.days_left", 30, 30)
            && JayInvite.TryParse(longItem.Code, out _) && vm.LongInviteLabel.Length == 0
            && vm.InviteSummary == L.F("multiplayer.invite.summary", longItem.Left, L.T("multiplayer.invite.use.unlimited")), vm.InviteSummary);
        Check("30 天邀请：不借用房间现在的大厅钥匙；房间里已有别的邀请时，卡片写明下次开房间起才能用", vm.ShowInviteNextRoomNote && vm.Status == L.T("multiplayer.longinvite.created_next_room")
            && Named<TextBlock>("InviteNextRoomNote")?.IsEffectivelyVisible == true
            && JayInvite.TryParse(vm.TestInviteText ?? "", out var monthCode) && vm.PendingInvites.Count == 2
            && JayInvite.TryParse(vm.PendingInvites[0].Invite.Invite.ToString(), out var roomCode) && !monthCode.LobbyKey.AsSpan().SequenceEqual(roomCode.LobbyKey), vm.Status);
        Check("30 天邀请：在卡片下面单独列着，能复制、作废", Named<ItemsControl>("RoomLongInviteList")?.IsEffectivelyVisible == true);
        await ShotAsync("房间-30天邀请-1440");
        Dispatcher.UIThread.Invoke(() => vm.RevokeLongInviteCommand.Execute(longItem));
        await Task.Delay(300);
        Check("30 天邀请：作废后列表清空，卡片也收起", !vm.HasLongInvites && !vm.HasInvite && Named<StackPanel>("RoomLongInvites")?.IsEffectivelyVisible == false);
        // With no other invite out, the room takes the thirty-day invite's own key and accepts it at once.
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var item in vm.PendingInvites.ToList()) vm.RevokeInviteCommand.Execute(item);
        });
        await Task.Delay(300);
        Press(createButton);
        await WaitAsync(() => vm.HasLongInvites, 5);
        await Task.Delay(300);
        var secondLong = vm.LongInviteItems.FirstOrDefault();
        Check("30 天邀请：房间里没有别的邀请时马上能用；作废再做的这张换了新钥匙", secondLong is not null && !vm.ShowInviteNextRoomNote && vm.Status == L.T("multiplayer.longinvite.created")
            && JayInvite.TryParse(secondLong.Code, out var secondCode) && JayInvite.TryParse(longItem!.Code, out var firstCode)
            && !secondCode.LobbyKey.AsSpan().SequenceEqual(firstCode.LobbyKey), vm.Status);
        Dispatcher.UIThread.Invoke(() =>
        {
            if (secondLong is not null) vm.RevokeLongInviteCommand.Execute(secondLong);
            lifeBox!.SelectedItem = vm.InviteLifeChoices[0];
            useBox!.SelectedItem = vm.InviteUseChoices[0];
            foreach (var item in vm.PendingInvites.ToList()) vm.RevokeInviteCommand.Execute(item);
        });
        await Task.Delay(300);

        // Room opened before the game: the invite card says what it waits for and takes a port; nothing is connected yet.
        Check("先开房间：有游戏时不显示等游戏", Named<Border>("GameLinkPanel")?.IsEffectivelyVisible == false);
        var savedPort = vm.ManualPort;
        var savedGame = vm.SelectedLanGame;
        vm.ManualPort = "";
        vm.SelectedLanGame = null;
        var relayReady = await vm.PrepareGameRelayForTest();
        await Task.Delay(300);
        Check("先开房间：邀请卡写在等游戏，给端口框和【查找】", relayReady && vm.IsWaitingForGame && Named<Border>("GameLinkPanel")?.IsEffectivelyVisible == true
            && Named<TextBox>("GameLinkPortBox")?.IsEffectivelyVisible == true && Named<Button>("GameLinkFindButton")?.IsEffectivelyVisible == true
            && HasText(L.T("multiplayer.relay.waiting")));
        await ShotAsync("房间-先开房间等游戏");
        vm.StopGameRelayForTest();
        vm.ManualPort = savedPort;
        vm.SelectedLanGame = savedGame;
        await Task.Delay(200);

        // A knock: the locked panel at the top, "No" focused, the countdown; Alt+N says no.
        var book = new InviteBook();
        KnockRequest Knock(string name, byte[] key) => new(name, key, "Jay 0.8", false, book.Create(1, null, false, TunnelSettings.Default));
        var first = vm.AskApprovalAsync(Knock("Casey", new byte[32] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), CancellationToken.None);
        var shown = await WaitAsync(() => vm.HasKnocks && page.FirstKnockDeny is { IsEffectivelyVisible: true }, 5);
        await Task.Delay(300);
        var panel = page.GetVisualDescendants().OfType<SafetyPanel>().FirstOrDefault(p => p.IsEffectivelyVisible);
        Check("敲门：锁定的安全面板，写谁想进来", shown && panel?.Title == L.F("multiplayer.knock.wants_in", "Casey"));
        Check("敲门：默认焦点在【拒绝】", page.FirstKnockDeny?.IsFocused == true, window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none");
        Check("敲门：写安全码和倒计时（默认 10 分钟）", vm.ApprovalCards[0].SafetyCode.Length > 0 && vm.ApprovalCards[0].Deadline is { } d && d - DateTimeOffset.Now > TimeSpan.FromMinutes(9)
            && vm.ApprovalCards[0].RemainingText.StartsWith(L.T("multiplayer.knock.remaining")[..2]), vm.ApprovalCards[0].RemainingText);
        Check("敲门：在内容区外面，不随滚动", page.FirstKnockDeny is { } deny && !page.FindControl<ScrollViewer>("MpBody")!.IsVisualAncestorOf(deny));
        await ShotAsync("房间-敲门-1440");
        Dispatcher.UIThread.Invoke(() => PressKey(page.FirstKnockDeny!, Key.N, KeyModifiers.Alt));
        Check("敲门：Alt+N 拒绝", await WaitAsync(() => first.IsCompleted, 3) && !first.Result && await WaitAsync(() => !vm.HasKnocks, 3));

        // Alt+Y with "let in next time" ticked: in, and on the trusted list.
        var danaKey = new byte[32];
        danaKey[0] = 4;
        var second = vm.AskApprovalAsync(Knock("Dana", danaKey), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        await Task.Delay(300);
        var trustBox = page.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.IsEffectivelyVisible && c.DataContext is ApprovalCard);
        Dispatcher.UIThread.Invoke(() => { if (trustBox is not null) trustBox.IsChecked = true; });
        Check("敲门：有“以后直接让 TA 进来”", trustBox is not null && vm.ApprovalCards[0].TrustAfter);
        Dispatcher.UIThread.Invoke(() => PressKey(page.FirstKnockDeny ?? (Control)page, Key.Y, KeyModifiers.Alt));
        Check("敲门：Alt+Y 让进来并加为信任的人", await WaitAsync(() => second.IsCompleted, 3) && second.Result
            && vm.TrustedPeople.Any(t => t.Member.Name == "Dana"));
        if (vm.TrustedPeople.FirstOrDefault(t => t.Member.Name == "Dana") is { } dana) vm.RemoveTrustedCommand.Execute(dana);

        // Esc while the panel has the focus says no too.
        var third = vm.AskApprovalAsync(Knock("Eli", new byte[32] { 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), CancellationToken.None);
        await WaitAsync(() => page.FirstKnockDeny is { IsEffectivelyVisible: true }, 5);
        await Task.Delay(300);
        Dispatcher.UIThread.Invoke(() => PressKey(page.FirstKnockDeny!, Key.Escape));
        Check("敲门：焦点在面板里时 Esc 拒绝", await WaitAsync(() => third.IsCompleted, 3) && !third.Result);

        // Room settings drawer (⋯ › Room settings): who can join / find it, knock wait, removed people.
        vm.ToggleRoomSettingsCommand.Execute(null);
        await Task.Delay(300);
        Check("房间设置：抽屉打开，写谁能进来、谁能找到", Named<Border>("RoomSettingsCard")?.IsEffectivelyVisible == true
            && HasText(L.T("multiplayer.room.who_join")) && HasText(L.T("multiplayer.room.who_find")));
        Check("房间设置：移出的人可以解除", HasText(L.T("multiplayer.kick.banned_title")) && page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("Bo · ") == true));
        var waitBox = Named<ComboBox>("RoomKnockWaitBox");
        Check("房间设置：等多久默认 10 分钟", waitBox?.SelectedItem is KnockWaitChoice { Wait: KnockWait.TenMinutes });
        Dispatcher.UIThread.Invoke(() => waitBox!.SelectedItem = vm.KnockWaitChoices.First(c => c.Wait == KnockWait.TwoMinutes));
        var fourth = vm.AskApprovalAsync(Knock("Fay", new byte[32] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), CancellationToken.None);
        await WaitAsync(() => vm.HasKnocks, 5);
        Check("房间设置：改成 2 分钟后新的敲门按 2 分钟倒数", vm.ApprovalCards.FirstOrDefault()?.Deadline is { } d2 && d2 - DateTimeOffset.Now <= TimeSpan.FromMinutes(2)
            && main.Service.Settings.MultiplayerKnockWait == "10m");
        Dispatcher.UIThread.Invoke(() => waitBox!.SelectedItem = vm.KnockWaitChoices.First(c => c.Wait == KnockWait.Forever));
        var fifth = vm.AskApprovalAsync(Knock("Gus", new byte[32] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }), CancellationToken.None);
        await WaitAsync(() => vm.ApprovalCards.Count == 2, 5);
        Check("房间设置：一直等时卡片不倒数", vm.ApprovalCards.LastOrDefault() is { Deadline: null } last && last.RemainingText == L.T("multiplayer.knock.waiting_forever"));
        Check("敲门：一次只放一个，其余写还有几人", page.GetVisualDescendants().OfType<SafetyPanel>().Count(p => p.IsEffectivelyVisible) == 1
            && HasText(L.P("multiplayer.knock.more", 1, 1)));
        vm.DenyFirstKnockCommand.Execute(null);
        await Task.Delay(300);
        Check("敲门：第一个处理完，下一个接上且焦点在【拒绝】", vm.FirstKnock?.Request.Name == "Gus" && page.FirstKnockDeny?.IsFocused == true);
        vm.DenyFirstKnockCommand.Execute(null);
        await WaitAsync(() => fourth.IsCompleted && fifth.IsCompleted && !vm.HasKnocks, 3);
        await ShotAsync("房间-设置-1440");
        vm.RoomKnockWait = KnockWait.TenMinutes;
        vm.RoomSettingsOpen = false;

        // Widths: medium = two columns with details below; compact = one section at a time, header buttons wrap under.
        window.Width = 900;
        window.Height = 700;
        await Task.Delay(500);
        Check("房间 900：两栏，详情在下面", page.RoomLayout == MultiplayerPage.RoomArrangement.TwoColumns, page.RoomLayout + " " + page.Bounds.Width);
        Check("房间 900：横幅还有头像", Named<StackPanel>("RoomFacesRow")?.IsEffectivelyVisible == true);
        await ShotAsync("房间-900");
        vm.CompactSection = 0;
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        Check("房间 600：分段，一次一块", page.RoomLayout == MultiplayerPage.RoomArrangement.Sections && Named<StackPanel>("RoomSections")?.IsEffectivelyVisible == true
            && Named<Border>("PeopleColumn")!.IsEffectivelyVisible && !Named<Border>("ChatColumn")!.IsEffectivelyVisible, page.RoomLayout.ToString());
        Check("房间 600：横幅收掉头像一排（人在“人”那一段里），按钮排到下面", Named<StackPanel>("RoomFacesRow")?.IsVisible == false
            && Named<Button>("EndRoomButton")?.IsEffectivelyVisible == true && Named<Button>("ConnectionMarkButton")?.IsEffectivelyVisible == true);
        vm.CompactSection = 1;
        await Task.Delay(200);
        Check("房间 600：切到聊天", Named<Border>("ChatColumn")!.IsEffectivelyVisible && !Named<Border>("PeopleColumn")!.IsEffectivelyVisible);
        await ShotAsync("房间-600");
        Dispatcher.UIThread.Invoke(() => PressKey(page, Key.I, KeyModifiers.Control | KeyModifiers.Shift));
        await Task.Delay(400);
        Check("Ctrl+Shift+I：转到邀请", vm.CompactSection == 2 && Named<Border>("InviteCard")?.IsEffectivelyVisible == true);
        await ShotAsync("房间-600-邀请");
        L.SetLanguage("en");
        await Task.Delay(400);
        Check("房间（英文）：单复数", vm.RoomHeaderText.Contains("2 people") && L.P("multiplayer.room.people", 1, 1) == "1 person", vm.RoomHeaderText);
        await ShotAsync("房间-600-en");
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        Check("房间（英文）：邀请的三行跟着换语言", Named<TextBlock>("InviteTermsText")?.Text is { } enTerms && enTerms == vm.InviteTerms && enTerms.Contains("10 minutes")
            && Named<TextBlock>("InviteSummaryText")?.Text == vm.InviteSummary, Named<TextBlock>("InviteTermsText")?.Text ?? "");
        await ShotAsync("房间-1440-en");
        L.SetLanguage("zh-Hans");

        // Settings › Multiplayer: the default wait for new rooms, and the room-defaults card says a room is open.
        main.Shell.Navigate(Amber.Core.Navigation.LegacyRoutes.MultiplayerSettings);
        await Task.Delay(300);
        T? Anywhere<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Check("设置 › 联机：新房间的默认等待时长", Anywhere<ComboBox>("DefaultKnockWaitBox")?.IsEffectivelyVisible == true && vm.SelectedDefaultKnockWait.Wait == KnockWait.TenMinutes);
        Check("设置 › 联机：能把“只走中转”设成默认；没有 30 天邀请时不单独占一块", Anywhere<CheckBox>("DefaultRelayOnlyBox") is { IsEffectivelyVisible: true, IsChecked: false }
            && !vm.DefaultRelayOnly && Anywhere<TextBlock>("LongInviteTitle")?.IsVisible == false && Anywhere<Button>("CreateLongInviteButton") is null);
        Check("设置 › 联机：在房间里时写明改动从下一个房间生效，完整局域网等不能改", Anywhere<TextBlock>("RoomDefaultsBusyText")?.IsEffectivelyVisible == true
            && Anywhere<CheckBox>("FullLanModeBox")?.IsEffectivelyEnabled == false && Anywhere<CheckBox>("DefaultRelayOnlyBox")?.IsEffectivelyEnabled == true);
        await ShotAsync("设置-联机-在房间里");
        main.Shell.Navigate("play/servers");
        await Task.Delay(300);
        page = (await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible))!;
        Check("房间：不在一起玩页签时顶栏收起", vm.IsServersTab && !vm.ShowRoomHeader);

        // A guest's view: the one amber button is [Enter the game]; the host is reached through a relay; remote help is asked from the host.
        var hostKey = new byte[32];
        hostKey[0] = 9;
        var hostFingerprint = MemberIdentity.FingerprintOf(hostKey);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("play");
            vm.Members.Clear();
            vm.OnlinePeers.Clear();
            vm.BannedPeople.Clear();
            vm.ChatLines.Clear();
            vm.Peers.Clear();
            vm.LeaveTestRoom();
            vm.EnterTestRoom(hostRoom with { RelayOnly = true }, host: false, relayRule: true);
            vm.HostFingerprint = hostFingerprint;
            vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(hostFingerprint, hostKey, "Robin")));
            vm.Peers.Add(new PeerItem(new PeerInfo("amber-host", "10.144.144.1", "relay(2)", 82, "tcp")));
            vm.JoinAddress = "127.0.0.1:25565";
            vm.TargetInstance = null;
        });
        await Task.Delay(500);
        var enter = Named<Button>("EnterGameButton");
        Check("加入方：横幅上【进入游戏】是这一屏唯一的琥珀按钮", vm.IsJoined && vm.ShowEnterGame && enter is { IsEffectivelyVisible: true } && enter.Classes.Contains("play")
            && (enter.Content as string) == L.T("multiplayer.room.enter_game") && AmberButtons() == 1 && (Named<Button>("EndRoomButton")?.Content as string) == L.T("multiplayer.room.leave"),
            AmberButtons().ToString());
        Check("加入方：还没选用哪个游戏进时，【进入游戏】先提示去选（不启动游戏）", Press(enter) && vm.Status == L.T("multiplayer.room.enter_game_pick")
            && Named<ComboBox>("RoomInstanceBox")?.IsEffectivelyVisible == true, vm.Status);
        Check("加入方：房主经中转，标记和房主那一行都写“经中转 · 82 ms”", vm.IsMarkRelayed && vm.People.Count == 2 && vm.People[1] is { IsHost: true, IsRelayed: true }
            && vm.People[1].LinkText == L.T("multiplayer.room.relayed") + " · 82 ms" && vm.WhoSeesLines.Single(l => l.Row.Id == "relayed").Now, vm.ConnectionMarkText);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.SelectedPerson = vm.People[1];
            if (!vm.WhoSeesOpen) vm.ToggleWhoSeesCommand.Execute(null);
        });
        await Task.Delay(400);
        Check("加入方：房间开的时候就定了只走中转，开关固定开着并写原因", Named<CheckBox>("RelayOnlyBox") is { IsEffectivelyVisible: true, IsEffectivelyEnabled: false, IsChecked: true }
            && Named<TextBlock>("RelayOnlyNote")?.Text == L.T("multiplayer.sees.relay_only.room_rule"), vm.RelayOnlyNote);
        Check("加入方：远程协助向房主请求；没有邀请面板", vm.ShowRemoteHelp && (vm.CanAskRemoteHelp || vm.HasRemoteHelpNote) && Named<Border>("InviteCard")?.IsEffectivelyVisible == false
            && Named<Button>("HeaderInviteButton")?.IsEffectivelyVisible == false && Named<Border>("RoomFactsCard")?.IsEffectivelyVisible == true);
        await ShotAsync("房间-加入方-1440");

        Dispatcher.UIThread.Invoke(() =>
        {
            vm.OnlinePeers.Clear();
            vm.Peers.Clear();
            vm.HostFingerprint = "";
            vm.JoinAddress = "";
            vm.TargetInstance = savedTarget;
            vm.LeaveTestRoom();
            vm.TestSwitchNetwork = null;
            vm.TestRestartForProxy = null;
            vm.Confirm = savedConfirm;
            foreach (var friend in vm.ContactList.ToList()) foreach (var device in friend.Devices) vm.Contacts.Remove(device.PublicKey);
            // Back to the contact book the scope started with: later scopes must not read this test one.
            vm.ContactsDir = savedContactsDir;
        });
        await Task.Delay(300);
        Check("房间：回到大厅", vm.IsIdle && !vm.ShowRoomHeader && Named<Grid>("LobbyGrid")?.IsEffectivelyVisible == true);
    }
}
