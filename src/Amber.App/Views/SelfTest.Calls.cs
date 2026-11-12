using Amber.App.ViewModels;
using Amber.Core.Calls;
using Amber.Core.Identity;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.Navigation;
using Amber.App.Views.Controls;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// Calls and screen sharing (redesign batch 11, reserved) with a stand-in room and friend: no media, no connection.
    /// With calls off nothing shows; switched on in-process: the friend's three buttons, the panel in the conversation,
    /// the top bar chip, the incoming call request, the locked control confirmation, the red bar and frame,
    /// Ctrl+Shift+F12, room voice and [Share my screen with them], "for a child", and the settings group.
    /// </summary>
    private async Task CallsAsync()
    {
        var shell = main.Shell;
        var call = main.Call;
        var mp = Page<MultiplayerViewModel>();
        var people = Page<PeopleViewModel>();
        var settings = Page<SettingsViewModel>();
        var requests = main.Service.Requests;
        T? Find<T>(Control root, string name) where T : Control => Dispatcher.UIThread.Invoke(() =>
            root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible));
        bool InWindow(string name) => Find<Control>(window, name) is not null;
        // A toggle answers to the toggle pattern, not invoke: run its command the way a click would.
        void Flip(ToggleButton? toggle) => Dispatcher.UIThread.Invoke(() =>
        {
            if (toggle is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true }) toggle.Command?.Execute(toggle.CommandParameter);
        });
        window.Width = 1440;
        window.Height = 900;

        var robinKey = new byte[32];
        robinKey[0] = 7;
        var robinFingerprint = MemberIdentity.FingerprintOf(robinKey);
        var dir = Path.Combine(reportDir, "calls-contacts");
        Directory.CreateDirectory(dir);
        var savedDir = mp.ContactsDir;
        var hostRoom = RoomCredentials.Create(mp.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), mp.ChatName);
        Dispatcher.UIThread.Invoke(() =>
        {
            mp.ContactsDir = dir;
            mp.Contacts.Import([new Contact(robinKey, "Robin", true, DateTimeOffset.UtcNow)]);
            mp.RefreshContacts();
            mp.EnterTestRoom(hostRoom, host: true, lobby: true);
            mp.Members.Add(new MemberItem("Robin", robinKey, false, true));
            mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(robinFingerprint, robinKey, "Robin")));
        });
        shell.Navigate("people");
        await Task.Delay(500);
        var page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible);
        if (page is null) { Check("通话：朋友页显示", false); return; }
        Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin"));
        await Task.Delay(300);

        // ---- switched off: nothing of calls anywhere ----
        Check("通话关着（默认）：朋友对话里没有【语音】【视频】【共享屏幕】", !CallFeature.Enabled && !InWindow("CallVoiceButton") && !InWindow("CallVideoButton")
            && !InWindow("CallShareButton") && Find<Button>(page, "InviteFriendButton") is not null);
        Check("通话关着：有人打来也不响，设置里找不到通话的设置", call.Ring("Robin", true, true) is null && SettingsCatalog.Find("CallsWho") is null
            && !AdvancedSettings.Groups.Contains(AdvancedSettings.Calls));

        // ---- switched on ----
        CallFeature.Enabled = true;
        try
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                call.Refresh();
                settings.RefreshCalls();
            });
            await Task.Delay(300);
            Check("通话打开：朋友对话里有【语音】【视频】【共享屏幕】，在同一个房间时能点", Find<Button>(page, "CallVoiceButton") is { IsEffectivelyEnabled: true }
                && Find<Button>(page, "CallVideoButton") is { IsEffectivelyEnabled: true } && Find<Button>(page, "CallShareButton") is { IsEffectivelyEnabled: true });
            await ShotAsync("calls-buttons");

            Press(Find<Button>(page, "CallVoiceButton"));
            await Task.Delay(400);
            Check("【语音】：通话面板出现在对话最上面，有我和 Robin 两块", call.IsInCall && Find<CallPanel>(page, "FriendCallPanel") is not null
                && call.Tiles.Select(t => t.Name).SequenceEqual([main.Service.Settings.PlayerName is { Length: > 0 } me ? me : L.T("call.me"), "Robin"]));
            Check("麦克风默认关，面板写明打开之前谁都听不到", !call.MicOn && InWindow("MicOffNote") && Find<TextBlock>(window, "CallRoute")?.Text == L.T("call.route.relay"));
            Check("顶栏有“通话中 · Robin”", Find<Button>(window, "CallChip") is { } chip && call.ChipText == L.F("call.chip", "Robin"));
            Check("一次一个通话：通话中三个按钮灰掉", Find<Button>(page, "CallVoiceButton") is { IsEffectivelyEnabled: false });
            Flip(Find<ToggleButton>(page, "MicButton"));
            Dispatcher.UIThread.Invoke(() => call.MarkSpeaking("Robin", true));
            await Task.Delay(300);
            var tiles = Dispatcher.UIThread.Invoke(() => page.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("call-tile") && b.IsEffectivelyVisible).ToList());
            Check("打开麦克风后提示收起；说话的人那一块是绿框（名字一直在）", call.MicOn && !InWindow("MicOffNote") && tiles.Count == 2
                && Dispatcher.UIThread.Invoke(() => tiles.Count(t => t.Classes.Contains("speaking"))) == 1);
            await ShotAsync("calls-voice");

            // Sharing, then control: the locked question first, focus on [Don't allow].
            Check("没共享时【让 TA 控制】灰掉", Find<Button>(page, "LetControlButton") is { IsEffectivelyEnabled: false });
            Flip(Find<ToggleButton>(page, "ShareButton"));
            await Task.Delay(300);
            Check("共享屏幕：大块是我的屏幕，窗口四周有红框（点不到它）", call.HasStage && InWindow("StageTile") && Find<Border>(window, "CallShareFrame") is { IsHitTestVisible: false });
            Press(Find<Button>(page, "LetControlButton"));
            await Task.Delay(400);
            Check("【让 TA 控制】：先是锁定的确认，焦点在【不允许】", call.ConfirmingControl && InWindow("ControlConfirm")
                && Dispatcher.UIThread.Invoke(() => page.FindControl<CallPanel>("FriendCallPanel")!.ControlRefuse.IsFocused));
            await ShotAsync("calls-control-ask");
            Press(Find<Button>(page, "ControlRefuse"));
            await Task.Delay(200);
            Check("【不允许】：什么都不开", !call.ConfirmingControl && !call.IsControlled && !InWindow("CallControlBar"));
            Press(Find<Button>(page, "LetControlButton"));
            await Task.Delay(300);
            Press(Find<Button>(page, "ControlAllow"));
            await Task.Delay(300);
            Check("【只这一次，允许】：顶上红条写谁在控制，带【停止】", call.IsControlled && Find<TextBlock>(window, "CallControlText")?.Text == L.F("call.control.bar", "Robin")
                && InWindow("CallControlStop"));
            await ShotAsync("calls-controlled");
            Press(Key.F12, KeyModifiers.Control | KeyModifiers.Shift);
            await Task.Delay(300);
            Check("Ctrl+Shift+F12：立刻停止控制，红条收起，还在共享", !call.IsControlled && !InWindow("CallControlBar") && call.Sharing);
            Press(Find<Button>(page, "LetControlButton"));
            await Task.Delay(200);
            Press(Find<Button>(page, "ControlAllow"));
            await Task.Delay(200);
            Flip(Find<ToggleButton>(page, "ShareButton"));
            await Task.Delay(300);
            Check("停止共享：控制跟着停，红框收起", !call.Sharing && !call.IsControlled && !InWindow("CallShareFrame"));

            // "For a child": no camera, no control, at once.
            Flip(Find<ToggleButton>(page, "CameraButton"));
            await Task.Delay(200);
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = true);
            await Task.Delay(300);
            // The friends page itself shrinks to a note for a child; the call loses the camera and control at once.
            Check("“给孩子用”：摄像头立刻关掉，视频和控制都不再提供，语音和共享屏幕还在", call is { CameraOn: false, OffersVideo: false, OffersControl: false, IsInCall: true }
                && !people.ShowVideoButton);
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = false);
            await Task.Delay(300);
            page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible) ?? page;
            Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin"));
            await Task.Delay(300);

            Press(Find<Button>(page, "HangUpButton"));
            await Task.Delay(300);
            Check("【挂断】：面板收起，写一句通话结束了，顶栏标记不见", !call.IsInCall && !InWindow("FriendCallPanel")
                && Find<TextBlock>(page, "FriendCallEnded")?.Text == L.T("call.ended") && !InWindow("CallChip"));

            // ---- someone calls ----
            Check("只见过的人打不进来", call.Ring("Dana", verifiedFriend: false, sameRoom: false) is null);
            var ring = Dispatcher.UIThread.Invoke(() => call.Ring("Robin", verifiedFriend: true, sameRoom: true));
            shell.Navigate("library");
            await Task.Delay(400);
            Check("来电：请求条写谁打来、是核对过的朋友，默认是【拒绝】", ring is { Spec: { Kind: "call" } spec } && spec.Choices.Single(c => c.IsDefault).Id == "decline"
                && requests.Pending.Contains(ring) && window.RequestBar.IsEffectivelyVisible
                && window.RequestBar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == L.F("call.ring.title", "Robin")));
            await ShotAsync("calls-ring");
            requests.Resolve(ring!, "answer", Amber.Core.Activity.RequestSource.Bar);
            await Task.Delay(500);
            Check("【接听】：回到朋友页的通话面板，麦克风还是关的", call.IsInCall && !call.MicOn && main.CurrentPage is PeopleViewModel
                && await WaitAsync(() => InWindow("FriendCallPanel"), 3));
            Press(Find<Button>(window, "CallChip"));
            Dispatcher.UIThread.Invoke(() => call.End("call.ended"));
            await Task.Delay(200);

            // ---- the room: voice on the banner, sharing with one member ----
            shell.Navigate("play");
            await Task.Delay(500);
            var room = await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible);
            Check("房间横幅上有【语音】", room is not null && Find<Button>(room, "RoomVoiceButton") is { IsEffectivelyEnabled: true });
            Press(room is null ? null : Find<Button>(room, "RoomVoiceButton"));
            await Task.Delay(400);
            Check("【语音】：横幅下面是房间的通话面板，标题写房间名，麦克风关着", call.IsInCall && call.IsRoomCall && room is not null
                && Find<CallPanel>(room, "RoomCallPanel") is not null && !call.MicOn && InWindow("CallChip"));
            await ShotAsync("calls-room-voice");
            Dispatcher.UIThread.Invoke(mp.LeaveTestRoom);
            await Task.Delay(300);
            Check("离开房间：房间的通话跟着结束", !call.IsInCall && call.EndedText == L.T("call.ended_room"));
            Dispatcher.UIThread.Invoke(() =>
            {
                mp.EnterTestRoom(hostRoom, host: true, lobby: true);
                if (!mp.Members.Any(m => m.Name == "Robin")) mp.Members.Add(new MemberItem("Robin", robinKey, false, true));
                if (!mp.OnlinePeers.Any(p => p.Entry.Fingerprint == robinFingerprint))
                    mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(robinFingerprint, robinKey, "Robin")));
            });
            await Task.Delay(400);
            Dispatcher.UIThread.Invoke(() => mp.SelectedPerson = mp.People.FirstOrDefault(p => !p.IsMe));
            await Task.Delay(300);
            room = await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible);
            Check("成员详情里有【共享屏幕给 TA】，远程协助并进了共享（不再单独一行）", room is not null && Find<Button>(room, "ShareScreenWithButton") is { IsEffectivelyEnabled: true }
                && !InWindow("ShareRemoteRow"));
            Press(room is null ? null : Find<Button>(room, "ShareScreenWithButton"));
            await Task.Delay(400);
            Check("【共享屏幕给 TA】：只有我们两个人的通话，一开始就在共享", call.IsInCall && call.Sharing && call.Peer == "Robin" && call.Tiles.Count == 2);
            await ShotAsync("calls-share-with");
            Dispatcher.UIThread.Invoke(() => call.End("call.ended"));

            // ---- Settings › Multiplayer › Calls and screen ----
            shell.Navigate("settings#CallsWho");
            await Task.Delay(600);
            var panel = await FindAsync<MultiplayerSettingsView>(p => p.IsEffectivelyVisible);
            Check("设置 › 联机多了“通话和屏幕（预留）”一组，settings#CallsWho 打开它", panel is not null && Dispatcher.UIThread.Invoke(() =>
                panel.MpGroupCalls.IsExpanded && panel.Groups.Count == AdvancedSettings.Groups.Count && panel.Groups[^1] == panel.MpGroupCalls));
            Check("通话设置：默认值和说明（只见过的人打不进来、控制每次都要问）", settings.CallsWhoIndex == 0 && settings.CallsNoise && !settings.CallsPushToTalk
                && settings.CallsShareGameOnly && settings.CallsAllowControl && !settings.CallsCustomized);
            Dispatcher.UIThread.Invoke(() => settings.CallsWhoIndex = 2);
            await Task.Delay(200);
            Check("改成“没人”：组头标“已自定义”，谁打来都不响", settings.CallsCustomized && call.Ring("Robin", true, true) is null);
            await ShotAsync("calls-settings");
            Dispatcher.UIThread.Invoke(() => settings.ChildMode = true);
            await Task.Delay(300);
            Check("“给孩子用”：设置里藏起摄像头、画质和允许控制", panel is not null && Find<ComboBox>(panel, "CallsCameraBox") is null && Find<ComboBox>(panel, "CallsQualityBox") is null
                && Find<CheckBox>(panel, "CallsAllowControlBox") is null && Find<ComboBox>(panel, "CallsMicBox") is not null);
            Dispatcher.UIThread.Invoke(() =>
            {
                settings.ChildMode = false;
                settings.CallsWhoIndex = 0;
            });
        }
        finally
        {
            CallFeature.Enabled = false;
            Dispatcher.UIThread.Invoke(() =>
            {
                call.End("call.ended");
                call.Refresh();
                settings.RefreshCalls();
                try { mp.LeaveTestRoom(); }
                catch (ArgumentException e) { Check("通话：离开模拟房间", false, e.Message); }
                mp.ContactsDir = savedDir;
            });
        }
    }
}
