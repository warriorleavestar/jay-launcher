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
    /// The room console (official §6.15) with a stand-in room: no component, no connection. The page is put in host mode
    /// with two people, a chat line and a knock, and the real controls are checked: the sticky header, people | chat |
    /// details at three widths, the locked knock panel ("No" focused, Alt+N / Alt+Y, let in next time), the invite terms,
    /// the room settings drawer with the knock wait, and Ctrl+Shift+I.
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
            Check("房间：页面显示", false);
            return;
        }
        bool HasText(string text) => page.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);
        T? Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        var alexKey = new byte[32];
        alexKey[0] = 1;
        var boKey = new byte[32];
        boKey[0] = 2;
        var alexFingerprint = MemberIdentity.FingerprintOf(alexKey);
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.Mode = "host";
            vm.Members.Add(new MemberItem("Alex", alexKey, false, true));
            vm.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(alexFingerprint, alexKey, "Alex")));
            vm.BannedPeople.Add(new BannedItem(new BannedMember(boKey, "Bo")));
            vm.ChatLines.Add(new ChatLine(new ChatMessage(ChatKind.Message, alexFingerprint, "Alex", "hi!", DateTimeOffset.Now), false));
            vm.RememberInviteTerms(1, TimeSpan.FromMinutes(10), false);
        });
        await Task.Delay(400);

        Check("房间：顶栏写我的房间、人数", vm.ShowRoomHeader && Named<StackPanel>("RoomHeaderArea")?.IsEffectivelyVisible == true
            && vm.RoomHeaderText.StartsWith(L.T("multiplayer.room.my_room")) && vm.RoomHeaderText.Contains(L.P("multiplayer.room.people", 2, 2)), vm.RoomHeaderText);
        Check("房间：顶栏不随内容滚动（在内容区外面）", Named<StackPanel>("RoomHeaderArea") is { } area && !page.FindControl<ScrollViewer>("MpBody")!.IsVisualAncestorOf(area));
        Check("房间：顶栏有邀请朋友、⋯、结束房间", Named<Button>("HeaderInviteButton")?.IsEffectivelyVisible == true && Named<Button>("RoomMenuButton")?.IsEffectivelyVisible == true
            && (Named<Button>("EndRoomButton")?.Content as string) == L.T("multiplayer.room.end"));
        Check("房间：宽窗口三栏（人 | 聊天 | 详情）", page.RoomLayout == MultiplayerPage.RoomArrangement.ThreeColumns
            && Named<Border>("PeopleColumn")!.IsEffectivelyVisible && Named<Border>("ChatColumn")!.IsEffectivelyVisible && Named<StackPanel>("DetailsColumn")!.IsEffectivelyVisible,
            page.RoomLayout + " " + page.Bounds.Width);
        Check("房间：大厅收起来了", Named<Grid>("LobbyGrid")?.IsEffectivelyVisible == false);
        Check("人：我排第一（房主），Alex 在线", vm.People.Count == 2 && vm.People[0].IsMe && vm.People[0].DisplayName == L.F("multiplayer.people.me_host", vm.ChatName)
            && vm.People[1].Name == "Alex" && vm.People[1].IsOnline && vm.People[1].Status == L.T("multiplayer.people.online"));

        // Pick Alex: safety code and actions; "Message" opens private chat with them.
        vm.SelectedPerson = vm.People[1];
        await Task.Delay(200);
        Check("人：选中后显示安全码和按钮", Named<SelectableTextBlock>("PersonSafetyCode")?.Text == alexFingerprint
            && Named<Button>("PersonChatButton")?.IsEffectivelyVisible == true && Named<Button>("PersonTrustButton")?.IsEffectivelyVisible == true
            && Named<Button>("PersonRemoveButton")?.IsEffectivelyVisible == true);
        Check("人：私聊转到私聊页签并选好人", Press(Named<Button>("PersonChatButton")) && vm.PrivateChatMode && vm.SelectedPeer?.Entry.Fingerprint == alexFingerprint);
        await Task.Delay(200);
        Check("聊天：私聊和房间聊天切换", Named<StackPanel>("PrivateChat")?.IsEffectivelyVisible == true && Named<StackPanel>("RoomChat")?.IsEffectivelyVisible == false);
        vm.RoomChatMode = true;
        await Task.Delay(200);
        Check("聊天：回到房间聊天", Named<StackPanel>("RoomChat")?.IsEffectivelyVisible == true && HasText("hi!"));

        // Invite card: no code yet → create; the terms read in words.
        Check("邀请卡：还没邀请时给【生成邀请】", Named<Button>("CreateFirstInviteButton")?.IsEffectivelyVisible == true);
        Check("邀请卡：条款写成一句话", vm.InviteTerms == L.F("multiplayer.invite.terms", L.P("multiplayer.invite.terms_uses", 1, 1),
            L.F("multiplayer.invite.terms_life", L.P("multiplayer.invite.minutes", 10, 10)), L.T("multiplayer.invite.terms_ask")), vm.InviteTerms);
        Check("邀请卡：【改…】展开选项", Press(Named<Button>("InviteChangeButton")) && vm.InviteOptionsOpen);
        await Task.Delay(200);
        Check("邀请卡：选项里有次数、有效期", Named<StackPanel>("InviteOptions")?.IsEffectivelyVisible == true);
        vm.InviteOptionsOpen = false;
        await ShotAsync("房间-1440");

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
        await ShotAsync("房间-900");
        vm.CompactSection = 0;
        window.Width = 600;
        window.Height = 420;
        await Task.Delay(500);
        Check("房间 600：分段，一次一块", page.RoomLayout == MultiplayerPage.RoomArrangement.Sections && Named<StackPanel>("RoomSections")?.IsEffectivelyVisible == true
            && Named<Border>("PeopleColumn")!.IsEffectivelyVisible && !Named<Border>("ChatColumn")!.IsEffectivelyVisible, page.RoomLayout.ToString());
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
        await ShotAsync("房间-1440-en");
        L.SetLanguage("zh-Hans");

        // Settings › invites and alerts: the default wait for new rooms.
        vm.ShowTabCommand.Execute("settings");
        await Task.Delay(300);
        Check("联机设置：新房间的默认等待时长", Named<ComboBox>("DefaultKnockWaitBox")?.IsEffectivelyVisible == true && vm.SelectedDefaultKnockWait.Wait == KnockWait.TenMinutes);
        Check("房间：不在一起玩页签时顶栏收起", !vm.ShowRoomHeader);

        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("play");
            vm.Members.Clear();
            vm.OnlinePeers.Clear();
            vm.BannedPeople.Clear();
            vm.ChatLines.Clear();
            vm.Mode = "";
        });
        await Task.Delay(300);
        Check("房间：回到大厅", vm.IsIdle && !vm.ShowRoomHeader && Named<Grid>("LobbyGrid")?.IsEffectivelyVisible == true);
    }
}
