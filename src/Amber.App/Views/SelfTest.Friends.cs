using Amber.App.Controls.Safety;
using Amber.App.ViewModels;
using Amber.Core.Activity;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// The Friends page (UI restructure: people, rooms, networks) with stand-in people: nothing goes over a network. It
    /// checks the top row (which network delivers, add a friend, backup), the add panel (my code as a card, the box for
    /// theirs no wider than 420, a request, the emoji check as Jay's locked card), the two lists (friends, people I've
    /// met), the conversation with a friend (what is known about them, the encryption line that opens safety codes and
    /// devices, a new device to confirm, join their room, invite to mine, "let them straight in", lines with where an
    /// invite is, the composer that works only in the same room), someone I've met, list-then-person in narrow windows
    /// and what "for a child" leaves.
    /// </summary>
    private async Task FriendsPageAsync()
    {
        var shell = main.Shell;
        var mp = Page<MultiplayerViewModel>();
        var people = Page<PeopleViewModel>();
        var settings = Page<SettingsViewModel>();
        var dir = Path.Combine(reportDir, "friends");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.Invoke(() =>
        {
            mp.ContactsDir = dir;
            mp.TrustedFile = Path.Combine(dir, "trusted.json");
        });
        window.Width = 1440;
        window.Height = 900;
        shell.Navigate("people");
        await Task.Delay(400);
        var found = await FindAsync<PeoplePage>(_ => true);
        if (found is null)
        {
            // Say why: a page that fails to build (a bad resource or binding in its markup) throws here too.
            string why;
            try
            {
                Dispatcher.UIThread.Invoke(() => new PeoplePage());
                why = "the page builds, but it never became visible";
            }
            catch (Exception e) { why = e.GetBaseException().Message; }
            Check("朋友页显示出来", false, why);
            return;
        }
        var page = found;
        T? Named<T>(string name) where T : Control => Dispatcher.UIThread.Invoke(() =>
            page.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible));
        bool Shown(string name) => Named<Control>(name) is not null;
        T? Classed<T>(string cls) where T : Control => Dispatcher.UIThread.Invoke(() =>
            page.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Classes.Contains(cls) && c.IsEffectivelyVisible));
        int AmberButtons() => Dispatcher.UIThread.Invoke(() => page.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("play")));
        void Flip(string name, bool on) => Dispatcher.UIThread.Invoke(() =>
        {
            if (page.GetVisualDescendants().OfType<ToggleSwitch>().FirstOrDefault(c => c.Name == name) is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true } box)
                box.IsChecked = on;
        });
        async Task ReopenAsync()
        {
            shell.Navigate("people");
            await Task.Delay(300);
            page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible) ?? page;
        }
        byte[] Key(byte n)
        {
            var k = new byte[32];
            k[0] = n;
            return k;
        }

        // ---- nobody yet ----
        Check("朋友页：还没有人时给【复制我的好友码】和【贴上对方的好友码】，没有空列表", Shown("PeopleEmpty") && Shown("EmptyCopyButton") && Shown("EmptyAddButton")
            && !Shown("FriendListCard") && !Shown("FriendDetail"));
        Check("顶上一行：没有能送信的网络时写明，并在原地给【连接网络】", Named<TextBlock>("DeliveryText")?.Text == L.T("people.delivery.none")
            && Shown("ConnectNetworkButton") && Named<Border>("DeliveryChip") is { } chip && !chip.Classes.Contains("ok"), people.DeliveryText);
        Check("顶上一行：【加朋友】是主按钮，旁边是【⋯】", Named<Button>("AddFriendButton") is { } add && add.Classes.Contains("primary") && Shown("PeopleMoreButton"));
        Check("【⋯】里是备份和恢复朋友列表，并写明备份用 Jay 账户锁着", Dispatcher.UIThread.Invoke(() =>
            page.PeopleMoreButton.Flyout is Flyout { Content: StackPanel menu }
            && menu.Children.OfType<Button>().Select(b => b.Name).SequenceEqual(new[] { "BackupFriendsButton", "RestoreFriendsButton" })
            && menu.Children.OfType<TextBlock>().Any()));
        await ShotAsync("friends-empty");
        Press(Named<Button>("ConnectNetworkButton"));
        await Task.Delay(300);
        Check("【连接网络】去联机里选公共网络", shell.Router.Current.ToString() == "play/public", shell.Router.Current.ToString());
        await ReopenAsync();

        // ---- the add panel ----
        Press(Named<Button>("AddFriendButton"));
        await Task.Delay(300);
        Check("【加朋友】在原地打开面板：我的好友码是一张卡（带【复制】），下面贴对方的码", people.IsAdding && Shown("AddFriendCard") && Shown("MyCodeCard")
            && Shown("CopyMyCodeButton") && Shown("FriendCodeBox") && Shown("AddFriendCodeButton") && mp.MyContactCode.StartsWith("JAYC1-"));
        var box = Named<TextBox>("FriendCodeBox");
        Check("好友码输入框最宽 420（1440 宽的窗口里也不被拉长）", box is not null && box.Bounds.Width is > 200 and <= 420.5, $"{box?.Bounds.Width}");
        await ShotAsync("friends-add");
        Press(Named<Button>("CloseAddButton"));
        await Task.Delay(200);
        Check("面板右上角的 × 收起它", !people.IsAdding && !Shown("AddFriendCard"));

        using var robin = X25519KeyPair.Generate();
        var code = ContactCode.Format(robin.PublicKey);
        shell.Navigate("play");
        await Task.Delay(300);
        // Through the window's own Ctrl+V handler, the way a real paste arrives.
        Dispatcher.UIThread.Invoke(() => main.PasteCode(code));
        await Task.Delay(400);
        page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible) ?? page;
        Check("在联机页贴好友码：转到朋友页，码已填进面板，什么都没发", shell.Router.Current.ToString() == "people/add" && people.IsAdding
            && Named<TextBox>("FriendCodeBox")?.Text == code && !mp.HasContactRequests, shell.Router.Current.ToString());
        Press(Named<Button>("AddFriendCodeButton"));
        await Task.Delay(300);
        Check("没连能送信的网络时【添加】不发请求，码还在框里", !mp.HasContactRequests && people.CodeInput == code);
        Dispatcher.UIThread.Invoke(() => people.CodeInput = "");

        // Robin asks to add me (the other side is a second contact book fed by hand).
        var robinBook = new ContactBook(Path.Combine(dir, "robin.json")) { Me = robin.PublicKey };
        var me = mp.Identity.PublicKey;
        Dispatcher.UIThread.Invoke(() =>
        {
            people.IsAdding = false;
            mp.Contacts.Receive(robin.PublicKey, robinBook.StartAdd(robin.PublicKey, me, "Robin")!, "Me");
        });
        await Task.Delay(300);
        Check("有人请求加我：入口上有红点；面板收着时这一页留一行【打开】", people.Badge == 1 && Shown("RequestsLine") && Shown("OpenRequestsButton")
            && Named<TextBlock>("RequestsText")?.Text == L.F("people.requests", 1));
        Press(Named<Button>("OpenRequestsButton"));
        await Task.Delay(300);
        Check("面板里这条请求有【接受】【忽略】", people.IsAdding && Classed<Button>("request-accept") is not null && Classed<Button>("request-ignore") is not null && !Shown("RequestsLine"));
        Dispatcher.UIThread.Invoke(() =>
        {
            var reply = mp.Contacts.Accept(robin.PublicKey, "Me")!;
            var nonce = robinBook.Receive(me, reply, "Robin")!;
            mp.Contacts.Receive(robin.PublicKey, nonce, "Me");
        });
        await Task.Delay(400);
        var request = mp.ContactRequests.FirstOrDefault();
        var check = Classed<SafetyPanel>("emoji-check");
        var differ = Classed<SafetyButton>("emoji-differ");
        var same = Classed<SafetyButton>("emoji-same");
        Check("对表情是 Jay 自己的安全卡片（蓝框、盾牌、“Jay 安全提示”）", check is not null && check.BadgeText == L.T("safety.title") && check.Title == request?.Title, check?.BadgeText ?? "");
        Check("四个表情，每个有名字（读屏读得出），和对方看到的一样", request is { IsComparing: true, Emoji.Count: 4 } && request.Emoji.All(e => e.Name.Length > 0)
            && request.Emoji.Select(e => e.Emoji).SequenceEqual(robinBook.Pending.Single().Sas!.Select(i => ContactHandshake.Emoji[i]))
            && check is not null && Dispatcher.UIThread.Invoke(() => check.GetVisualDescendants().OfType<ItemsControl>()
                .Any(i => Avalonia.Automation.AutomationProperties.GetName(i) == request.EmojiSpoken)), request?.EmojiSpoken ?? "");
        Check("两个按钮：【不一样】是安全的那个，【一样，加为朋友】是确认", differ is { Kind: SafetyKind.Safe } && (differ.Content as string) == L.T("multiplayer.contacts.compare.no_match")
            && same is { Kind: SafetyKind.Confirm } && (same.Content as string) == L.T("multiplayer.contacts.compare.match"));
        await ShotAsync("friends-emoji");

        // A short window: the panel is taller than the page, so it stops at the bottom edge and scrolls inside.
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        Check("矮窗口：我的码下面那行说明收起（悬停还看得到），【添加】不用滚就整个在窗口里", !Shown("MyCodeHint") && Dispatcher.UIThread.Invoke(() =>
            page.FindControl<Button>("AddFriendCodeButton") is { IsEffectivelyVisible: true } add && add.TranslatePoint(new Point(0, 0), window) is { } at
            && at.Y >= 0 && at.Y + add.Bounds.Height <= window.Bounds.Height + 0.5));
        Check("矮窗口（600×420）里对表情：面板不超出窗口，× 还在，里面能滚到【一样，加为朋友】", Dispatcher.UIThread.Invoke(() =>
        {
            var card = page.AddFriendCard;
            if (card.TranslatePoint(new Point(0, 0), window) is not { } top || top.Y + card.Bounds.Height > window.Bounds.Height + 0.5) return false;
            if (page.CloseAddButton is not { IsEffectivelyVisible: true } close || close.TranslatePoint(new Point(0, 0), window) is not { Y: >= 0 }) return false;
            var scroll = page.AddFriendScroll;
            if (scroll.Extent.Height <= scroll.Viewport.Height) return false;
            same!.BringIntoView();
            return true;
        }), $"{Dispatcher.UIThread.Invoke(() => page.AddFriendCard.Bounds.Height)}");
        await Task.Delay(300);
        Check("滚过去以后【一样，加为朋友】整个在窗口里", Dispatcher.UIThread.Invoke(() => same is { IsEffectivelyVisible: true }
            && same.TranslatePoint(new Point(0, 0), window) is { } at && at.Y >= 0 && at.Y + same.Bounds.Height <= window.Bounds.Height + 0.5));
        await ShotAsync("friends-emoji-short");
        window.Width = 1440;
        window.Height = 900;
        await Task.Delay(400);
        Check("窗口高了，我的码下面那行说明回来", Shown("MyCodeHint"));
        Press(same);
        await Task.Delay(400);
        Check("点【一样，加为朋友】：Robin 进了朋友列表，请求没了", people.Friends.SingleOrDefault()?.Name == "Robin" && !mp.HasContactRequests && people.Badge == 0
            && Shown("FriendListCard") && Shown("FriendsCaption") && Shown("PeoplePickHint"), string.Join(",", people.Friends.Select(f => f.Name)));
        Dispatcher.UIThread.Invoke(() => people.IsAdding = false);

        // ---- a friend ----
        Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin"));
        await Task.Delay(300);
        Check("选中朋友：右边是和 TA 的对话，名字下面写现在知道的状态", Shown("FriendDetail") && Named<TextBlock>("FriendName")?.Text == "Robin"
            && Named<TextBlock>("FriendStateText")?.Text == L.T("people.state.verified") && !Shown("PeoplePickHint"), people.Selected?.StateText ?? "");
        Check("对话顶上常驻“端到端加密 · 已核对”", Named<TextBlock>("SafetyChipText")?.Text == L.T("people.safety.verified") && Shown("SafetyChipButton") && !Shown("FriendSafetyPanel"));
        Check("不在同一个房间时：输入框不能用，并写明原因；还没有消息", Named<TextBox>("DraftBox") is { IsEffectivelyEnabled: false } && Named<Button>("SendDraftButton") is { IsEffectivelyEnabled: false }
            && Named<TextBlock>("MessageHintText")?.Text == L.T("people.chat.needs_room") && Shown("NoLinesText"));
        Check("没开房间时【邀请进我的房间】不能点，并写明先开房间", Named<Button>("InviteFriendButton") is { IsEffectivelyEnabled: false } && Shown("InviteHintText"));
        Check("TA 没邀请我时没有【加入】，这一页没有琥珀按钮", !Shown("JoinFriendRoomButton") && AmberButtons() == 0, AmberButtons().ToString());
        Press(Named<Button>("SafetyChipButton"));
        await Task.Delay(300);
        Check("点那一行：打开安全码和 TA 的设备，能移除朋友", people.ShowSafety && Shown("FriendSafetyPanel") && Shown("RemoveFriendButton")
            && Named<ItemsControl>("FriendDevices") is { } one && Dispatcher.UIThread.Invoke(() => one.GetVisualDescendants().OfType<SelectableTextBlock>().Count()) == 1
            && Classed<Button>("device-remove") is null);
        await ShotAsync("friends-safety");
        Press(Named<Button>("SafetyChipButton"));

        // "Let them straight in": the old trusted list as a switch on the person.
        Check("“TA 敲门直接进”默认关", Named<ToggleSwitch>("AutoAdmitSwitch") is { IsChecked: false } && !people.AutoAdmit && mp.TrustedPeople.Count == 0);
        Flip("AutoAdmitSwitch", true);
        await Task.Delay(300);
        Check("打开开关：这个人的设备进了“敲门直接进”的名单", people.AutoAdmit && mp.TrustedPeople.Any(t => t.Member.PublicKey.AsSpan().SequenceEqual(robin.PublicKey))
            && Named<ToggleSwitch>("AutoAdmitSwitch") is { IsChecked: true });
        Flip("AutoAdmitSwitch", false);
        await Task.Delay(300);
        Check("关掉开关：从名单里拿掉", !people.AutoAdmit && mp.TrustedPeople.Count == 0);

        // Bob has a Jay account and two computers; a third shows up signed by his account.
        using var bob = JayAccount.FromMnemonic(Bip39.Generate(MnemonicLanguage.English), null);
        Dispatcher.UIThread.Invoke(() => mp.Contacts.Import([
            new Contact(Key(1), "Bob", true, DateTimeOffset.UtcNow.AddDays(-3)) { JayAccount = bob.Address },
            new Contact(Key(2), "Bob", true, DateTimeOffset.UtcNow) { JayAccount = bob.Address },
        ]));
        await WaitAsync(() => people.Friends.Count == 2, 3);
        Check("列表刷新后还选着同一个人", people.Selected?.Name == "Robin" && Shown("FriendDetail"), people.Selected?.Name ?? "");
        Dispatcher.UIThread.Invoke(() =>
        {
            page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Bob");
            people.ShowSafety = true;
        });
        await Task.Delay(300);
        Check("同一个 Jay 账户的两台设备算一个人：一行，设备各有安全码，能单独移除", people.Friends.Count == 2 && people.Selected is { HasJay: true, DeviceItems.Count: 2 }
            && Named<ItemsControl>("FriendDevices") is { } two && Dispatcher.UIThread.Invoke(() => two.GetVisualDescendants().OfType<SelectableTextBlock>().Count()) == 2
            && Dispatcher.UIThread.Invoke(() => page.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("device-remove") && b.IsEffectivelyVisible)) == 2);
        using (var bobNew = DeviceKeys.Generate())
        {
            // Bob's new laptop proves his Jay account in a live handshake (a knock or a room), which is how a new device arrives.
            Dispatcher.UIThread.Invoke(() => mp.Contacts.SeenLive(bobNew.Dh.PublicKey, bob.CertifyDevice(bobNew, "Linux", 0, DateTimeOffset.UtcNow)));
            var waiting = await WaitAsync(() => people.Friends.FirstOrDefault(f => f.Name == "Bob") is { NeedsLook: true }, 3);
            await Task.Delay(300);
            Check("朋友多了一台没见过的设备：状态写“新设备待确认”，顶上那一行也换成它", waiting && people.Selected is { Name: "Bob", NeedsLook: true }
                && Named<TextBlock>("FriendStateText")?.Text == L.T("people.state.new_device") && Named<TextBlock>("SafetyChipText")?.Text == L.T("people.state.new_device")
                && people.SafetyNeedsLook, people.Selected?.StateText ?? "");
            Dispatcher.UIThread.Invoke(() => people.ShowSafety = true);
            await Task.Delay(300);
            var confirm = Classed<Button>("device-confirm");
            Check("设备列表里这台标着待确认，有【是 TA】", confirm is not null && (confirm.Content as string) == L.T("multiplayer.contacts.new_device_ok")
                && people.Selected!.DeviceItems.Count(d => d.IsNew) == 1);
            Check("新设备的确认是锁定的安全面板：写 TA 的名字和设备的安全码，两个都是安全按钮，焦点在【不是 TA？移除这台设备】",
                confirm is Amber.App.Controls.Safety.SafetyButton { Kind: Amber.App.Controls.Safety.SafetyKind.Confirm }
                && Dispatcher.UIThread.Invoke(() => confirm.FindAncestorOfType<Amber.App.Controls.Safety.SafetyPanel>() is { } locked
                    && locked.Title == L.F("multiplayer.contacts.new_device", "Bob")
                    && page.NewDeviceSafeButton() is { Kind: Amber.App.Controls.Safety.SafetyKind.Safe } safe
                    && (safe.IsFocused || TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() == safe)));
            await ShotAsync("friends-new-device");
            Press(confirm);
            Check("点【是 TA】：状态回到“已核对”，请求条里的提示也收起", await WaitAsync(() => people.Selected is { Name: "Bob", NeedsLook: false }
                && main.Service.Requests.Pending.All(r => r.Spec.Kind != "contact-new-device"), 3), people.Selected?.StateText ?? "");
        }

        // ---- in a room together: messages, people I've met ----
        var robinFingerprint = MemberIdentity.FingerprintOf(robin.PublicKey);
        var danaKey = Key(9);
        var hostRoom = RoomCredentials.Create(mp.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), mp.ChatName);
        Dispatcher.UIThread.Invoke(() =>
        {
            page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin");
            mp.EnterTestRoom(hostRoom, host: true, lobby: true);
            mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(robinFingerprint, robin.PublicKey, "Robin")));
            mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(danaKey), danaKey, "Dana")));
        });
        await Task.Delay(400);
        Check("朋友进了我的房间：状态写“和你在同一个房间”，灯亮，输入框能用了", people.Selected is { IsHere: true } && Named<TextBlock>("FriendStateText")?.Text == L.T("people.state.same_room")
            && Named<TextBox>("DraftBox") is { IsEffectivelyEnabled: true } draft && draft.PlaceholderText == L.F("people.chat.placeholder", "Robin") && !Shown("MessageHintText"),
            people.Selected?.StateText ?? "");
        Check("开着房间：【邀请进我的房间】能点了", Named<Button>("InviteFriendButton") is { IsEffectivelyEnabled: true } && !Shown("InviteHintText"));
        Dispatcher.UIThread.Invoke(() =>
        {
            mp.FriendMessageForTest(robin.PublicKey, "hi! ready?", mine: false);
            mp.FriendMessageForTest(robin.PublicKey, "two minutes", mine: true);
            mp.SentInviteForTest("mail-1", people.Selected!.Friend);
        });
        await Task.Delay(300);
        var bubbles = Dispatcher.UIThread.Invoke(() => page.ConversationList.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("bubble") && b.IsEffectivelyVisible).ToList());
        Check("对话：TA 的话在左，我的在右", bubbles.Count == 2 && !bubbles[0].Classes.Contains("mine") && bubbles[1].Classes.Contains("mine")
            && bubbles[1].Bounds.Right > bubbles[0].Bounds.Right && !Shown("NoLinesText"), bubbles.Count.ToString());
        TextBlock? Delivery() => Dispatcher.UIThread.Invoke(() => page.ConversationList.GetVisualDescendants().OfType<TextBlock>().LastOrDefault(t => t.Classes.Contains("line-delivery") && t.IsEffectivelyVisible));
        Check("对话：邀请是一行记录，后面写“已寄出”", people.Lines is { Count: 3 } && people.Lines[2] is { IsNote: true } note && note.Text == L.T("people.line.invited")
            && Delivery()?.Text == L.T("people.line.sent"), Delivery()?.Text ?? "");
        Dispatcher.UIThread.Invoke(() => mp.ShowHeldBack([new SentMail(robin.PublicKey, "mail-1", MailKind.Invite, DateTimeOffset.UtcNow)]));
        await Task.Delay(200);
        Check("对话：几分钟没有回执，改写“还没送到”（黄字）", Delivery() is { } held && held.Text == L.T("people.line.held") && held.Classes.Contains("warn"), Delivery()?.Text ?? "");
        Dispatcher.UIThread.Invoke(() =>
        {
            mp.DismissHeldBackCommand.Execute(null);
            mp.InviteReceiptForTest("mail-1");
        });
        await Task.Delay(200);
        Check("对话：回执到了，写“已送达”", Delivery() is { } done && done.Text == L.T("people.line.delivered") && !done.Classes.Contains("warn"), Delivery()?.Text ?? "");
        await ShotAsync("friends-conversation");

        Check("房间里没对过表情的人列在“见过的人”下面", Shown("MetCaption") && people.MetPeople.SingleOrDefault() is { Name: "Dana", InRoom: true, AutoAdmit: false } dana
            && dana.StateText == L.T("people.met.in_room") && people.MetPeople.All(p => p.Name != "Robin"), string.Join(",", people.MetPeople.Select(p => p.Name)));
        Dispatcher.UIThread.Invoke(() => page.MetList.SelectedItem = people.MetPeople.First());
        await Task.Delay(300);
        Check("选中见过的人：没有对话，写 TA 的安全码，给【加为朋友】和同一个开关", Shown("MetDetail") && !Shown("FriendDetail") && people.Selected is null
            && Named<TextBlock>("MetName")?.Text == "Dana" && Named<SelectableTextBlock>("MetFingerprint")?.Text == MemberIdentity.FingerprintOf(danaKey)
            && Shown("AddMetButton") && Named<ToggleSwitch>("MetAutoAdmitSwitch") is { IsChecked: false, IsEffectivelyEnabled: true } && !Shown("KnocksAlwaysAskText"));
        await ShotAsync("friends-met");
        Flip("MetAutoAdmitSwitch", true);
        await Task.Delay(300);
        Check("给见过的人打开“TA 敲门直接进”：进名单", people.MetAutoAdmit && mp.TrustedPeople.Any(t => t.Member.Name == "Dana"));
        Dispatcher.UIThread.Invoke(mp.LeaveTestRoom);
        Dispatcher.UIThread.Invoke(() => mp.OnlinePeers.Clear());
        await Task.Delay(400);
        Check("房间关了：开着开关的人还在“见过的人”里，写“进过我的房间”，仍然选着", people.MetPeople.SingleOrDefault() is { Name: "Dana", InRoom: false, AutoAdmit: true } later
            && later.StateText == L.T("people.met.before") && people.SelectedMet?.Name == "Dana" && Shown("MetDetail"), string.Join(",", people.MetPeople.Select(p => p.Name)));
        Flip("MetAutoAdmitSwitch", false);
        await Task.Delay(400);
        Check("关掉开关：这个人不再被记着，右边回到“选一个人”", people.MetPeople.Count == 0 && people.SelectedMet is null && !Shown("MetCaption") && Shown("PeoplePickHint")
            && mp.TrustedPeople.Count == 0, string.Join(",", people.MetPeople.Select(p => p.Name)));

        // ---- someone else's room: their list is only the host's word ----
        Dispatcher.UIThread.Invoke(() =>
        {
            page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin");
            mp.EnterTestRoom(hostRoom, host: false);
            mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(robinFingerprint, robin.PublicKey, "Robin")));
            mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(MemberIdentity.FingerprintOf(danaKey), danaKey, "Dana")));
        });
        await Task.Delay(400);
        Check("在别人的房间里：朋友写“在这个房间里（房主的名单这样写）”，仍然能发消息", Named<TextBlock>("FriendStateText")?.Text == L.T("people.state.listed_in_room")
            && Named<TextBox>("DraftBox") is { IsEffectivelyEnabled: true }, Named<TextBlock>("FriendStateText")?.Text ?? "");
        Dispatcher.UIThread.Invoke(() => page.MetList.SelectedItem = people.MetPeople.FirstOrDefault(p => p.Name == "Dana"));
        await Task.Delay(300);
        Check("在别人的房间里：只有房主名单上的人，“TA 敲门直接进”灰掉并写原因", Shown("MetDetail") && Shown("MetUncheckedText")
            && Named<ToggleSwitch>("MetAutoAdmitSwitch") is { IsChecked: false, IsEffectivelyEnabled: false });
        Dispatcher.UIThread.Invoke(mp.LeaveTestRoom);
        Dispatcher.UIThread.Invoke(() => mp.OnlinePeers.Clear());
        await Task.Delay(300);

        // ---- a friend's room ----
        using var roomHost = X25519KeyPair.Generate();
        var invite = new InviteBook().Create(1, TimeSpan.FromMinutes(30), true, TunnelSettings.Default, boundTo: me).Invite;
        Dispatcher.UIThread.Invoke(() =>
        {
            page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin");
            mp.ShowMailedInvites([new MailedInvite(people.Selected!.Friend.Contact, invite, roomHost.PublicKey, "Robin's world", DateTimeOffset.UtcNow.AddMinutes(30))]);
        });
        await Task.Delay(400);
        Check("朋友邀请我进 TA 的房间：状态写在哪个房间里", people.Selected is { IsInRoom: true } && Named<TextBlock>("FriendStateText")?.Text == L.F("people.state.in_room", "Robin's world"),
            people.Selected?.StateText ?? "");
        Check("给【加入“…”】，它是这一页唯一的琥珀按钮；对话里记一行", Named<Button>("JoinFriendRoomButton") is { } join && join.Classes.Contains("play") && AmberButtons() == 1
            && people.JoinRoomText == L.F("people.chat.join", "Robin's world") && people.Lines!.Last() is { IsNote: true } invited
            && invited.Text == L.F("people.line.invited_me", "Robin's world"), people.JoinRoomText);
        await ShotAsync("friends-their-room");

        // ---- widths and languages ----
        foreach (var lang in new[] { "zh-Hans", "en" })
        {
            L.SetLanguage(lang);
            foreach (var (w, h) in new (double, double)[] { (600, 420), (1024, 700), (1440, 900) })
            {
                window.Width = w;
                window.Height = h;
                await Task.Delay(450);
                var bounds = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
                bool Inside(Control? c) => c is { IsEffectivelyVisible: true } && c.TranslatePoint(new Point(0, 0), window) is { } p
                    && bounds.Contains(new Rect(p, c.Bounds.Size).Deflate(0.5));
                var narrow = w < 700;
                Check($"朋友页（{lang} {w}×{h}）：{(narrow ? "窄窗口只显示这个人，带返回" : "列表在左、对话在右")}，都在窗口里", Dispatcher.UIThread.Invoke(() =>
                    Inside(page.FriendDetailCard) && Inside(page.PeopleStatusCard) && Inside(page.DraftBox) && Inside(page.SafetyChipButton)
                    && (narrow
                        ? !page.FriendListCard.IsVisible && page.BackToListButton.IsEffectivelyVisible && !page.PeopleTitle.IsVisible
                        : Inside(page.FriendListCard) && page.FriendDetailCard.Bounds.Left >= page.FriendListCard.Bounds.Right - 1 && !page.BackToListButton.IsVisible)),
                    $"list {page.FriendListCard.Bounds} {page.FriendListCard.IsVisible} detail {page.FriendDetailCard.Bounds}");
                await ShotAsync($"friends-{lang}-{w}x{h}");
            }
        }
        L.SetLanguage("zh-Hans");
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(450);
        Press(Named<Button>("BackToListButton"));
        await Task.Delay(300);
        Check("窄窗口：返回后只有列表", people.Selected is null && Shown("FriendListCard") && !Dispatcher.UIThread.Invoke(() => page.FriendDetailCard.IsVisible));
        await ShotAsync("friends-narrow-list");
        Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Bob"));
        await Task.Delay(300);
        Check("窄窗口：点一个人进到 TA 那里", people.Selected?.Name == "Bob" && Shown("FriendDetail") && !Dispatcher.UIThread.Invoke(() => page.FriendListCard.IsVisible));
        window.Width = 1024;
        window.Height = 700;
        await Task.Delay(400);

        // ---- removing ----
        Dispatcher.UIThread.Invoke(() =>
        {
            page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin");
            people.ShowSafety = true;
        });
        await Task.Delay(300);
        Press(Named<Button>("RemoveFriendButton"));
        await Task.Delay(400);
        Check("【从朋友里移除】先问一句，写 TA 的名字，焦点在【留着】", people.IsConfirmingRemove && Shown("RemoveFriendConfirm") && !Shown("RemoveFriendButton")
            && Named<Control>("RemoveFriendConfirm") is Amber.App.Controls.Safety.SafetyPanel && Named<Control>("ConfirmRemoveFriendButton") is Amber.App.Controls.Safety.SafetyButton
            && people.RemoveQuestion.Contains("Robin") && Named<Control>("KeepFriendButton") is { } keep && Dispatcher.UIThread.Invoke(() => keep.IsFocused)
            && people.Friends.Any(f => f.Name == "Robin"));
        Press(Named<Button>("KeepFriendButton"));
        await Task.Delay(300);
        Check("【留着】：TA 还在，问题收起", !people.IsConfirmingRemove && Shown("RemoveFriendButton") && people.Friends.Any(f => f.Name == "Robin"));
        Press(Named<Button>("RemoveFriendButton"));
        await Task.Delay(400);
        Press(Named<Button>("ConfirmRemoveFriendButton"));
        await Task.Delay(400);
        Check("【从朋友里移除】：人没了，TA 邀请我的房间和请求条里的邀请也跟着没了", people.Friends.All(f => f.Name != "Robin") && people.Selected is null && mp.FriendRooms.Count == 0
            && main.Service.Requests.Pending.All(r => r.Spec.Kind != "contact-invite"), string.Join(",", people.Friends.Select(f => f.Name)));

        // ---- "for a child" ----
        var child = settings.ChildMode;
        try
        {
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = true);
            await ReopenAsync();
            Check("“给孩子用”打开后：只留一句说明，没有朋友、没有见过的人、没有顶上那一行、没有红点", Shown("PeopleChildNote") && !Shown("FriendListCard") && !Shown("FriendDetail")
                && !Shown("MetList") && !Shown("MetDetail") && !people.HasMet
                && !Dispatcher.UIThread.Invoke(() => page.PeopleStatusCard.IsVisible) && people.Badge == 0 && !people.IsAdding);
            shell.Navigate("people/add");
            await Task.Delay(300);
            Check("“给孩子用”打开后：people/add 也不打开加朋友的面板", !people.IsAdding && !Shown("AddFriendCard"));
            await ShotAsync("friends-child");
        }
        finally
        {
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = child);
            await Task.Delay(200);
        }

        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var c in mp.ContactList.ToList()) mp.RemoveContactCommand.Execute(c);
        });
        foreach (var pending in main.Service.Requests.Pending.Where(r => r.Spec.Kind.StartsWith("contact", StringComparison.Ordinal)).ToList())
            main.Service.Requests.Resolve(pending, pending.Spec.Choices.First(c => c.IsDefault).Id, RequestSource.Program);
        shell.Navigate("library");
        await Task.Delay(200);
    }
}
