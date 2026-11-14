using System.IO.Compression;
using Amber.App.ViewModels;
using Amber.App.Views.Controls;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Contacts;
using Amber.Core.Multiplayer.Files;
using Amber.Core.Multiplayer.Transport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>Two ends of a stand-in private channel: frames go straight to the other end, nothing leaves the process.</summary>
    private sealed class LoopLink(string me, byte[] myKey, string name) : IPrivateLink
    {
        public LoopLink? Peer;
        public event Action<PrivateFrame>? PrivateReceived;

        public Task<bool> SendPrivateAsync(string to, byte[] sealedData, CancellationToken ct)
        {
            if (Peer is null) return Task.FromResult(false);
            Peer.PrivateReceived?.Invoke(new PrivateFrame(me, myKey, name, sealedData));
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Files in conversations (redesign batch 12) with a stand-in room and a friend whose side is a real
    /// <see cref="PrivateMessenger"/> over a loop link: no connection, no network. The paperclip and its six items,
    /// a dropped file, a world and a mod list through the send panel, receiving through the request bar (Alt+Y / Alt+N),
    /// a program's warning, importing a received world, comparing a received mod list, auto-accept for a checked friend
    /// (programs still ask), the queue for a friend who is away, [Send again] when queueing is off, a send that breaks
    /// and carries on by itself, the room member's [Send a file], the world row's [Send to a friend] and the Files group.
    /// </summary>
    private async Task FilesAsync()
    {
        var shell = main.Shell;
        var service = main.Service;
        var s = service.Settings;
        var mp = Page<MultiplayerViewModel>();
        var people = Page<PeopleViewModel>();
        var settings = Page<SettingsViewModel>();
        T? Find<T>(Control root, string name) where T : Control => Dispatcher.UIThread.Invoke(() =>
            root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible));
        window.Width = 1280;
        window.Height = 820;

        var root = Path.Combine(reportDir, "files");
        var game = Path.Combine(root, "game");
        var mine = Path.Combine(root, "received");
        var robinDownloads = Path.Combine(root, "robin");
        var sources = Path.Combine(root, "sources");
        foreach (var d in new[] { mine, robinDownloads, sources }) Directory.CreateDirectory(d);
        var saved = (s.GameDirectory, s.FilesQueueOffline, s.FilesAutoAcceptFriends, s.FilesSaveFolder, s.FilesSpeed, mp.ContactsDir, mp.DownloadsDir, mp.OutgoingDir, people.PickPath);

        // A Fabric instance with two mods and a world.
        WriteVersion(game, "files-fabric", """[{"name":"net.fabricmc:fabric-loader:0.16.0"}]""");
        s.GameDirectory = game;
        s.FilesSaveFolder = "";
        service.SaveAndReload();
        await WaitAsync(() => service.InstalledVersions().Contains("files-fabric"), 10);
        var instance = service.GetInstance("files-fabric");
        var worldDir = Path.Combine(instance.SavesDir, "Sunny Hills");
        Directory.CreateDirectory(Path.Combine(worldDir, "region"));
        File.WriteAllBytes(Path.Combine(worldDir, "level.dat"), [10, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(worldDir, "region", "r.0.0.mca"), new byte[8192]);
        File.WriteAllText(Path.Combine(worldDir, "session.lock"), "lock");
        Directory.CreateDirectory(instance.ModsDir);
        void Jar(string file, string id, string version)
        {
            using var zip = ZipFile.Open(Path.Combine(instance.ModsDir, file), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write($$"""{"schemaVersion":1,"id":"{{id}}","version":"{{version}}","name":"{{id}}"}""");
        }
        Jar("sodium.jar", "sodium", "0.6.0");
        Jar("lithium.jar", "lithium", "0.14.0");

        // Robin: a checked friend in my room, whose end is a real messenger.
        using var robin = X25519KeyPair.Generate();
        var robinFingerprint = MemberIdentity.FingerprintOf(robin.PublicKey);
        var meFingerprint = MemberIdentity.FingerprintOf(mp.Identity.PublicKey);
        var myLink = new LoopLink(meFingerprint, mp.Identity.PublicKey, mp.ChatName);
        var robinLink = new LoopLink(robinFingerprint, robin.PublicKey, "Robin");
        myLink.Peer = robinLink;
        robinLink.Peer = myLink;
        var robinAccepts = true;
        var robinGot = new System.Collections.Concurrent.ConcurrentQueue<FileProgress>();
        PrivateMessenger RobinSide()
        {
            var side = new PrivateMessenger(robinLink, robin, fp => fp == meFingerprint ? mp.Identity.PublicKey : null, robinDownloads);
            side.FileOffered += offer => _ = robinAccepts ? side.AcceptAsync(offer.Id) : side.DeclineAsync(offer.Id);
            side.Progress += robinGot.Enqueue;
            return side;
        }
        var robinSide = RobinSide();
        var contacts = Path.Combine(root, "contacts");
        Directory.CreateDirectory(contacts);
        var hostRoom = RoomCredentials.Create(mp.Identity.PublicKey, 25565, RoomOpening.Tunnel(RoomProtection.Standard)!).ToHostRoom(new MemberTable(), mp.ChatName);
        void RobinHere(bool here) => Dispatcher.UIThread.Invoke(() =>
        {
            var peer = mp.OnlinePeers.FirstOrDefault(p => p.Entry.Fingerprint == robinFingerprint);
            if (here && peer is null) mp.OnlinePeers.Add(new OnlinePeerItem(new RosterEntry(robinFingerprint, robin.PublicKey, "Robin")));
            if (!here && peer is not null) mp.OnlinePeers.Remove(peer);
            mp.RosterChangedForTest();
        });
        Dispatcher.UIThread.Invoke(() =>
        {
            mp.ContactsDir = contacts;
            mp.DownloadsDir = mine;
            mp.OutgoingDir = Path.Combine(root, "outgoing");
            mp.Contacts.Import([new Contact(robin.PublicKey, "Robin", true, DateTimeOffset.UtcNow)]);
            mp.RefreshContacts();
            mp.EnterTestRoom(hostRoom, host: true, lobby: true);
            mp.Members.Add(new MemberItem("Robin", robin.PublicKey, false, true));
            mp.UsePrivateLinkForTest(myLink);
        });
        RobinHere(true);

        string Source(string name, int bytes)
        {
            var path = Path.Combine(sources, name);
            File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));
            return path;
        }
        TransferItem? Newest(bool outgoing) => Dispatcher.UIThread.Invoke(() => mp.Transfers.FirstOrDefault(t => t.Outgoing == outgoing));
        async Task<bool> Ends(TransferItem? card, FileCardState state, int seconds = 15) => card is not null && await WaitAsync(() => card.State == state, seconds);
        bool RobinHas(string name) => File.Exists(Path.Combine(robinDownloads, name));

        try
        {
            shell.Navigate("people");
            await Task.Delay(500);
            var page = await FindAsync<PeoplePage>(p => p.IsEffectivelyVisible);
            if (page is null) { Check("文件：朋友页显示", false); return; }
            Dispatcher.UIThread.Invoke(() => page.FriendList.SelectedItem = people.Friends.First(f => f.Name == "Robin"));
            await Task.Delay(300);

            // ---- the paperclip ----
            var attach = Find<Button>(page, "AttachButton");
            var items = Dispatcher.UIThread.Invoke(() => attach?.Flyout is MenuFlyout menu ? menu.Items.OfType<MenuItem>().Select(i => i.Name).ToList() : []);
            Check("对话输入框左边有回形针，菜单六项（文件、世界、模组列表、截图、日志、文件夹）", attach is { IsEffectivelyEnabled: true }
                && items.SequenceEqual(["AttachFileItem", "AttachWorldItem", "AttachModsItem", "AttachShotItem", "AttachLogItem", "AttachFolderItem"]), string.Join(",", items));
            var notes = Source("notes.txt", 20_000);
            people.PickPath = _ => Task.FromResult<string?>(notes);
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("file"));
            var card = Newest(true);
            Check("【选一个文件…】：卡片进对话，送达后写“已送达”，Robin 那边收到了", await Ends(card, FileCardState.Delivered) && RobinHas("notes.txt")
                && card!.StatusLine == L.T("files.card.delivered")
                && await WaitAsync(() => Dispatcher.UIThread.Invoke(() => page.GetVisualDescendants().OfType<FileCard>().Any(c => c.IsEffectivelyVisible)), 3), card?.StatusLine ?? "");
            Check("活动里有这次传输，送达后结束", service.Tasks.All.Any(t => t.Kind == "file" && t.State == Amber.Core.Activity.TaskState.Succeeded));

            // ---- drop on the conversation ----
            var dropped = Source("map.png", 9_000);
            var data = new DataTransfer();
            if (await window.StorageProvider.TryGetFileFromPathAsync(new Uri(dropped)) is { } storage) data.Add(DataTransferItem.CreateFile(storage));
            Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DragEnterEvent, data, window, new Point(10, 10), KeyModifiers.None)));
            await Task.Delay(300);
            var zones = Dispatcher.UIThread.Invoke(() => window.DropTargets.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("drop-target")).ToList());
            Check("把文件拖到对话上：只有一个落点“发给 Robin”", zones.Count == 1 && main.Drop.Targets is [{ Kind: Amber.Core.Content.DropKind.Send } only]
                && only.Label == L.F("drop.send", "Robin"));
            await ShotAsync("files-drop");
            var at = Dispatcher.UIThread.Invoke(() =>
            {
                var origin = zones[0].TranslatePoint(default, window) ?? default;
                return new Point(origin.X + zones[0].Bounds.Width / 2, origin.Y + zones[0].Bounds.Height / 2);
            });
            Dispatcher.UIThread.Invoke(() => window.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, at, KeyModifiers.None)));
            card = await WaitAsync(() => Newest(true)?.Name == "map.png", 5) ? Newest(true) : null;
            Check("放下：发给 Robin，截图按截图标出来", await Ends(card, FileCardState.Delivered) && card!.Kind == FileKind.Screenshot && RobinHas("map.png"));

            // ---- a world through the panel ----
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("world"));
            await Task.Delay(400);
            var panel = mp.SendPanel;
            Check("【世界…】：“发文件”面板打开，类型是世界，列出 Sunny Hills，收件人是 Robin", panel is { IsOpen: true, Kind: FileKind.World }
                && panel.Candidate?.Label == "Sunny Hills" && panel.Recipient?.Name == "Robin" && panel.CanSend && Find<Border>(window, "SendFileDrawerPart") is not null
                && panel.Note.Length > 0);
            await ShotAsync("files-panel-world");
            Press(Find<Button>(window, "SendFileSendPart"));
            await Task.Delay(100);
            card = Newest(true);
            Check("【发送】：面板收起，世界先打包成 zip 再发，送达", !panel.IsOpen && card?.Name == "Sunny Hills.zip" && await Ends(card, FileCardState.Delivered)
                && RobinHas("Sunny Hills.zip"));
            using (var zip = ZipFile.OpenRead(Path.Combine(robinDownloads, "Sunny Hills.zip")))
                Check("打包的世界：顶层是世界文件夹，有 level.dat，没有 session.lock", zip.Entries.Any(e => e.FullName == "Sunny Hills/level.dat")
                    && !zip.Entries.Any(e => e.Name == "session.lock"));
            Check("送达后打包的临时文件删掉了", !Directory.EnumerateFiles(mp.OutgoingDir).Any(f => f.EndsWith(".zip", StringComparison.Ordinal)));

            // ---- a mod list ----
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("mods"));
            await Task.Delay(300);
            Check("【模组列表…】：列出带加载器的实例和模组数", panel is { IsOpen: true, Kind: FileKind.ModList } && panel.Candidate?.Detail == L.F("files.panel.mod_count", 2));
            Press(Find<Button>(window, "SendFileSendPart"));
            card = await WaitAsync(() => Newest(true)?.Kind == FileKind.ModList, 5) ? Newest(true) : null;
            Check("模组列表作为 .jaymods 送达", await Ends(card, FileCardState.Delivered) && Directory.EnumerateFiles(robinDownloads, "*" + FileKinds.ModListExtension).Any());

            // ---- receiving: the request bar ----
            var robinWorld = Path.Combine(sources, "Robin Island.zip");
            using (var zip = ZipFile.Open(robinWorld, ZipArchiveMode.Create))
            {
                using (var w = new BinaryWriter(zip.CreateEntry("Robin Island/level.dat").Open())) w.Write([10, 0, 0, 0]);
                using (var w = new BinaryWriter(zip.CreateEntry("Robin Island/region/r.0.0.mca").Open())) w.Write(new byte[4096]);
            }
            var send = robinSide.SendFileAsync(meFingerprint, robinWorld);
            var request = await WaitAsync(() => main.Activity.FirstRequest is { Handle.Spec.Kind: "file" }, 5) ? main.Activity.FirstRequest : null;
            Check("Robin 发来世界：请求条写是朋友发来的、文件类型，五分钟后算没接收，默认【拒绝】", request is { Handle.Spec: { } spec }
                && spec.Body?.Contains(L.T("files.offer.friend"), StringComparison.Ordinal) == true && spec.Timeout == TimeSpan.FromMinutes(5)
                && spec.Choices.Single(c => c.IsDefault).Id == "decline" && window.RequestBar.IsEffectivelyVisible, request?.Body ?? "");
            await ShotAsync("files-request");
            Press(Key.Y, KeyModifiers.Alt);
            card = await WaitAsync(() => Newest(false) is not null, 5) ? Newest(false) : null;
            Check("Alt+Y 接收：卡片写“已收到”，存进收文件的文件夹，认出是世界", await Ends(card, FileCardState.Received) && await send
                && card!.Kind == FileKind.World && card.SavedPath?.StartsWith(mine, StringComparison.Ordinal) == true && card.CanImportWorld && card.CanOpen);
            await ShotAsync("files-received-world");
            var target = card?.ImportTargets.FirstOrDefault(t => t.Name.Length > 0);
            if (target is not null) await Dispatcher.UIThread.InvokeAsync(() => target.GoCommand.ExecuteAsync(null));
            Check("【导入到实例…】：世界进了实例的存档", target is not null && await WaitAsync(() => Directory.Exists(Path.Combine(instance.SavesDir, "Robin Island")), 5));

            // ---- a program: the warning, then Alt+N ----
            var tool = Source("tool.exe", 2_000);
            send = robinSide.SendFileAsync(meFingerprint, tool);
            request = await WaitAsync(() => main.Activity.FirstRequest is { Handle.Spec.Kind: "file" }, 5) ? main.Activity.FirstRequest : null;
            Check("发来程序：请求条里写着程序文件的警告", request?.Body?.Contains(L.T("multiplayer.file.program_warning"), StringComparison.Ordinal) == true, request?.Body ?? "");
            await ShotAsync("files-request-program");
            Press(Key.N, KeyModifiers.Alt);
            Check("Alt+N 拒绝：Robin 那边写“对方没接收”，我这边没有文件", !await send && !File.Exists(Path.Combine(mine, "tool.exe"))
                && robinGot.Any(p => p.Name == "tool.exe" && p.Finished && p.End == FileEnd.NotAccepted));

            // ---- a mod list from Robin: compare ----
            var theirs = new ModManifest([.. ModManifest.Read(instance.ModsDir).Mods.Where(m => m.FileName != "lithium.jar")]);
            var list = FilePacker.WriteModList(theirs, "Robin", sources);
            send = robinSide.SendFileAsync(meFingerprint, list);
            await WaitAsync(() => main.Activity.FirstRequest is { Handle.Spec.Kind: "file" }, 5);
            Press(Key.Y, KeyModifiers.Alt);
            card = await WaitAsync(() => Newest(false)?.Kind == FileKind.ModList, 5) ? Newest(false) : null;
            var compare = card?.CompareTargets.FirstOrDefault();
            if (compare is not null) await Dispatcher.UIThread.InvokeAsync(() => compare.GoCommand.ExecuteAsync(null));
            Check("收到模组列表：【和实例比一比…】写出差多少", card is { CanCompareMods: true } && compare is not null && await WaitAsync(() => card.HasCompare, 5)
                && card.CompareText != L.T("files.compare.same"), card?.CompareText ?? "");
            await ShotAsync("files-compare");

            // ---- auto-accept for a checked friend, programs still ask ----
            Dispatcher.UIThread.Invoke(() => settings.FilesAutoAccept = true);
            var photo = Source("photo.png", 5_000);
            Check("打开“核对过的朋友发来的文件直接收下”：不弹请求，直接收下", await robinSide.SendFileAsync(meFingerprint, photo)
                && await WaitAsync(() => File.Exists(Path.Combine(mine, "photo.png")), 5) && main.Activity.FirstRequest is not { Handle.Spec.Kind: "file" });
            send = robinSide.SendFileAsync(meFingerprint, Source("setup.exe", 1_000));
            Check("程序还是要问", await WaitAsync(() => main.Activity.FirstRequest is { Handle.Spec.Kind: "file" }, 5));
            Press(Key.N, KeyModifiers.Alt);
            await send;
            Dispatcher.UIThread.Invoke(() => settings.FilesAutoAccept = false);

            // ---- Robin away: the file waits, then goes ----
            RobinHere(false);
            people.PickPath = _ => Task.FromResult<string?>(Source("later.txt", 4_000));
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("file"));
            card = Newest(true);
            Check("Robin 不在房间里：卡片写“等 Robin 上线”，文件在本机排队", card is { State: FileCardState.Queued, CanCancel: true } && mp.QueuedFiles == 1
                && card.StatusLine == L.F("files.card.queued", "Robin"), card?.StatusLine ?? "");
            await ShotAsync("files-queued");
            RobinHere(true);
            Check("Robin 回来：排着的文件自己发出去", await Ends(card, FileCardState.Delivered) && mp.QueuedFiles == 0 && RobinHas("later.txt"));

            // ---- queueing off: [Send again] ----
            Dispatcher.UIThread.Invoke(() => settings.FilesQueue = false);
            RobinHere(false);
            people.PickPath = _ => Task.FromResult<string?>(Source("now.txt", 4_000));
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("file"));
            card = Newest(true);
            Check("关掉排队：Robin 不在时卡片写“对方不在”，给【再发一次】", card is { State: FileCardState.Unreachable, CanSendAgain: true } && mp.QueuedFiles == 0);
            Dispatcher.UIThread.Invoke(() => settings.FilesQueue = true);
            RobinHere(true);
            Dispatcher.UIThread.Invoke(() => card?.SendAgainCommand.Execute(null));
            Check("【再发一次】：送达", await Ends(card, FileCardState.Delivered) && RobinHas("now.txt"));

            // ---- a send that breaks part way and carries on by itself ----
            Dispatcher.UIThread.Invoke(() => settings.FilesSpeedIndex = Array.IndexOf(TransferPace.All, TransferPace.SaveData));
            var big = Source("big.bin", 600_000);
            people.PickPath = _ => Task.FromResult<string?>(big);
            await Dispatcher.UIThread.InvokeAsync(() => people.AttachCommand.ExecuteAsync("file"));
            card = Newest(true);
            Check("“省流量”限速：发送中写进度、经什么连过去和速度", card is not null && await WaitAsync(() => card.State == FileCardState.Sending && card.Done > 30_000, 10)
                && card.StatusLine.Contains('%'), card?.StatusLine ?? "");
            await ShotAsync("files-sending");
            Dispatcher.UIThread.Invoke(mp.ClosePrivateForTest);
            await robinSide.DisposeAsync();
            Check("房间断开：卡片写“连接断了”，文件留着等 Robin", await Ends(card, FileCardState.Interrupted, 5) && card!.CanResume && mp.QueuedFiles == 1);
            Dispatcher.UIThread.Invoke(() => settings.FilesSpeedIndex = 0);
            robinGot.Clear();
            robinSide = RobinSide();
            Dispatcher.UIThread.Invoke(() => mp.UsePrivateLinkForTest(myLink));
            RobinHere(true);
            Check("再见到 Robin：从断开的地方接着发，送达且内容一样", await Ends(card, FileCardState.Delivered, 20) && RobinHas("big.bin")
                && robinGot.FirstOrDefault(p => p.Name == "big.bin")?.Done > 0
                && File.ReadAllBytes(Path.Combine(robinDownloads, "big.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(big)));

            // ---- the room: the cards and [Send a file] on a member ----
            shell.Navigate("play");
            await Task.Delay(500);
            var room = await FindAsync<MultiplayerPage>(p => p.IsEffectivelyVisible);
            Dispatcher.UIThread.Invoke(() => mp.SelectedPerson = mp.People.FirstOrDefault(p => !p.IsMe));
            await Task.Delay(300);
            Check("房间成员详情有【发文件】", room is not null && Find<Button>(room, "PersonSendFileButton") is { IsEffectivelyEnabled: true });
            Press(room is null ? null : Find<Button>(room, "PersonSendFileButton"));
            await Task.Delay(300);
            Check("【发文件】：面板打开，收件人是 Robin", panel is { IsOpen: true } && panel.Recipient?.Name == "Robin");
            Press(Key.Escape, KeyModifiers.None);
            await Task.Delay(200);
            Check("Esc 关掉面板", !panel.IsOpen);

            // ---- the world row ----
            var inst = main.Pages.OfType<InstanceViewModel>().First();
            main.CurrentPage = inst;
            inst.SelectedId = "files-fabric";
            await WaitAsync(() => inst.Current?.Id == "files-fabric", 10);
            inst.TabIndex = (int)InstanceTab.Worlds;
            await WaitAsync(() => inst.Worlds.Any(w => w.World.FolderName == "Sunny Hills"), 5);
            var row = inst.Worlds.FirstOrDefault(w => w.World.FolderName == "Sunny Hills");
            Dispatcher.UIThread.Invoke(() => inst.SendWorldToFriendCommand.Execute(row));
            await Task.Delay(300);
            Check("世界那一行的【发给朋友】：面板打开，选好了这个世界", panel is { IsOpen: true, Kind: FileKind.World } && panel.Candidate?.Label == "Sunny Hills");
            await ShotAsync("files-panel-from-world");
            Dispatcher.UIThread.Invoke(() => panel.CloseCommand.Execute(null));

            // ---- Settings › Multiplayer › Files ----
            shell.Navigate("settings#FilesQueue");
            await Task.Delay(600);
            var group = await FindAsync<MultiplayerSettingsView>(p => p.IsEffectivelyVisible);
            Check("设置 › 联机有“文件”一组，settings#FilesQueue 打开它，默认值", group is not null && Dispatcher.UIThread.Invoke(() => group.MpGroupFiles.IsExpanded)
                && settings is { FilesQueue: true, FilesAutoAccept: false, FilesNotify: true, FilesSpeedIndex: 0, FilesCustomized: false } && !settings.FilesFolderIsCustom);
            Dispatcher.UIThread.Invoke(() => settings.FilesSpeedIndex = 1);
            await Task.Delay(200);
            Check("改了速度：组头标“已自定义”", settings.FilesCustomized && group is not null && Find<Control>(group, "MpFilesTag") is not null);
            await ShotAsync("files-settings");
            Dispatcher.UIThread.Invoke(() => settings.FilesSpeedIndex = 0);
        }
        finally
        {
            await robinSide.DisposeAsync();
            Dispatcher.UIThread.Invoke(() =>
            {
                mp.SendPanel.IsOpen = false;
                mp.ClosePrivateForTest();
                try { mp.LeaveTestRoom(); }
                catch (ArgumentException e) { Check("文件：离开模拟房间", false, e.Message); }
                (s.GameDirectory, s.FilesQueueOffline, s.FilesAutoAcceptFriends, s.FilesSaveFolder, s.FilesSpeed, mp.ContactsDir, mp.DownloadsDir, mp.OutgoingDir, people.PickPath) = saved;
                service.SaveAndReload();
            });
        }
    }
}
