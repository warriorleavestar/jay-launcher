using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Public;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

internal sealed partial class SelfTest
{
    /// <summary>
    /// 公共网络（AMBER_SELFTEST_SCOPE=public-network）：在本机开一个种子节点 + 目录，房主连公共网络、开房间、公开发布；
    /// 加入方连公共网络、在列表里看到房间、点“申请加入”进房间；再换成密码房间，密码错进不去、对了能进。
    /// 要先下载好联机组件；所有连接都在本机回环上。
    /// </summary>
    /// <summary>加入方的页面不在主窗口里：单独开一个窗口显示它的联机页再截图。</summary>
    /// <param name="scrollToText">截图前把显示这段文字的控件滚到可见处。</param>
    private async Task ShotPageAsync(MultiplayerViewModel vm, string name, string? scrollToText = null)
    {
        var shot = Dispatcher.UIThread.Invoke(() =>
        {
            var w = new Window { Content = new MultiplayerPage { DataContext = vm, Margin = new Avalonia.Thickness(16) }, Width = 900, Height = 760 };
            w.Show();
            return w;
        });
        await Task.Delay(800);
        if (scrollToText is not null)
        {
            Dispatcher.UIThread.Invoke(() => shot.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == scrollToText)?.BringIntoView());
            await Task.Delay(400);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)shot.Bounds.Width, (int)shot.Bounds.Height));
            bitmap.Render(shot);
            bitmap.Save(Path.Combine(reportDir, $"{++_shot:D2}-{name}.png"));
            shot.Close();
        });
    }

    private async Task PublicNetworkAsync()
    {
        var host = Page<MultiplayerViewModel>();
        main.CurrentPage = host;
        await Task.Delay(500);
        var page = await FindAsync<MultiplayerPage>(_ => true);
        bool HasText(string key) => page!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == L.T(key));
        Check("公共网络：联机页有公共网络卡片和说明", page is not null && HasText("multiplayer.public.title") && HasText("multiplayer.public.hint"));
        if (!host.IsComponentReady)
        {
            Log("SKIP 公共网络：联机组件没有下载");
            return;
        }
        var service = host.TestService;
        // 收到的文件放进报告目录（私聊在进房间时就建好了，要在开房间之前设）。
        host.DownloadsDir = Path.Combine(reportDir, "downloads-host");

        // 本机的种子节点 + 目录（和 jay-server directory 一样）。
        var nodePort = EasyTierArgs.FreeTcpPort();
        var node = $"tcp://127.0.0.1:{nodePort}";
        await using var directory = new RoomDirectoryServer(System.Net.IPAddress.Loopback, PublicNetworkProfile.DirectoryPort);
        using var seed = PublicNetworkSession.Start(service.EasyTier, new PublicNetworkOptions(PublicNetworkProfile.Jay() with { Nodes = [] },
            EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort(), Directory: true, Listeners: [node]), Path.Combine(reportDir, "public-seed"));

        Dispatcher.UIThread.Invoke(() => host.PublicNodesText = node);
        host.SavePublicNodesCommand.Execute(null);
        Check("公共网络：保存种子节点", host.HasPublicNodes, host.Status);
        service.Settings.PublicNetworkNoticeAccepted = false;
        Check("公共网络：第一次连接先显示隐私说明", Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, host.ConnectPublicCommand))) && host.PublicNoticeOpen && !host.IsPublicConnecting);
        await ShotAsync("公共网络隐私说明");
        await host.AcceptPublicNoticeCommand.ExecuteAsync(null);
        Check("公共网络：同意后连上", await WaitAsync(() => host.IsPublicConnected, 60), host.Status);

        // 房主开房间（房间自己的网络经本机节点互连），默认“隐藏”登记，然后公开发布。
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var relayPort = EasyTierArgs.FreeTcpPort();
        host.TestListeners = [$"tcp://127.0.0.1:{relayPort}"];
        host.TestPeers = [];
        host.ManualPort = ((System.Net.IPEndPoint)server.LocalEndpoint).Port.ToString();
        await host.CreateRoomCommand.ExecuteAsync(null);
        Check("公共网络：开房间", host.IsHosting, host.Status);
        host.SelectedVisibility = host.VisibilityChoices.Single(c => c.Mode == RoomVisibility.Public);
        host.PublishRoomName = "自检房间";
        host.PublishAutoApprove = true;
        await host.PublishRoomCommand.ExecuteAsync(null);
        Check("公共网络：公开发布", await WaitAsync(() => host.PublishStatus == L.T("multiplayer.public.published"), 20), host.PublishStatus + " / " + host.Status);
        await ShotAsync("公共网络房主发布");

        var guest = host.CreateTestPeer();
        guest.Activate();
        guest.TestPeers = [$"tcp://127.0.0.1:{relayPort}"];
        guest.EasyTierWorkRoot = Path.Combine(reportDir, "easytier-guest");
        // 加入方要有自己的联机身份（同一个进程里默认和房主共用一个）。
        guest.UseTestIdentity(Path.Combine(reportDir, "identity-guest"));
        // 收到的文件放进报告目录（私聊在进房间时就建好了，要在那之前设）。
        guest.DownloadsDir = Path.Combine(reportDir, "downloads-guest");
        await guest.ConnectPublicCommand.ExecuteAsync(null);
        Check("公共网络：加入方连上（隐私说明只显示一次）", guest.IsPublicConnected && !guest.PublicNoticeOpen, guest.Status);
        await guest.RefreshRoomsCommand.ExecuteAsync(null);
        var listed = guest.PublicRooms.FirstOrDefault(r => r.Listing.Name == "自检房间");
        Check("公共网络：列表里看到房间（名字、房主指纹）", listed is not null && listed.Meta.Contains(host.Identity.Fingerprint), string.Join(" | ", guest.PublicRooms.Select(r => r.Title)));
        await ShotPageAsync(guest, "公共网络房间列表");
        if (listed is not null)
        {
            await guest.JoinPublicRoomCommand.ExecuteAsync(listed);
            Check("公共网络：从列表申请加入后进了房间", guest.IsJoined && guest.HostFingerprint == host.Identity.Fingerprint, guest.Status);
            Check("公共网络：房主成员列表里有他", await WaitAsync(() => host.Members.Count == 1, 15));
            await PrivateChatAsync(host, guest);
            guest.StopCommand.Execute(null);
        }

        // 密码房间：列表带锁，密码错进不去，对了能进。
        host.SelectedVisibility = host.VisibilityChoices.Single(c => c.Mode == RoomVisibility.Password);
        host.PublishPassword = "creeper-123";
        await host.PublishRoomCommand.ExecuteAsync(null);
        await Task.Delay(1500);
        await guest.RefreshRoomsCommand.ExecuteAsync(null);
        var locked = guest.PublicRooms.FirstOrDefault(r => r.Listing.Name == "自检房间");
        Check("公共网络：密码房间带锁、列表里没有邀请", locked is { NeedsPassword: true } && locked.Listing.Invite is null && locked.Title.Contains("🔒"));
        if (locked is not null)
        {
            guest.PublicRoomPassword = "wrong-pass";
            await guest.JoinPublicRoomCommand.ExecuteAsync(locked);
            Check("公共网络：密码错进不去（提示密码不对）", guest.IsIdle && !guest.IsKnocking && guest.Status == L.T("multiplayer.public.reason.password"), guest.Status);
            guest.PublicRoomPassword = "creeper-123";
            await guest.JoinPublicRoomCommand.ExecuteAsync(locked);
            Check("公共网络：密码对了能进", guest.IsJoined, guest.Status);
            guest.StopCommand.Execute(null);
        }

        await DeviceFingerprintAsync(host, guest);

        // 撤下：列表里没了；关房间、断开后目录里也没了。
        host.UnpublishRoomCommand.Execute(null);
        await Task.Delay(1500);
        await guest.RefreshRoomsCommand.ExecuteAsync(null);
        Check("公共网络：撤下后列表里没有", guest.PublicRooms.All(r => r.Listing.Name != "自检房间"));
        host.StopCommand.Execute(null);
        host.DisconnectPublicCommand.Execute(null);
        guest.DisconnectPublicCommand.Execute(null);
        server.Stop();
        Check("公共网络：断开后状态复原", !host.IsPublicConnected && !guest.IsPublicConnected && host.PublicRooms.Count == 0);

        seed.Dispose();
        await NetworkSwitchAsync(host);
    }

    /// <summary>
    /// 换公共网络：注册表签的连接串直接加、自动选上，再连进这个网络（本机另开一个这个网络的种子节点）；
    /// 别人签的先提醒、确认后才加；同编号换钥匙的拒绝；删掉后回到 Jay 公共网络。
    /// </summary>
    private async Task NetworkSwitchAsync(MultiplayerViewModel host)
    {
        var service = host.TestService;
        var registry = NetworkSigner.Generate(out _);
        var someone = NetworkSigner.Generate(out _);
        service.TrustedNetworkKeys = [registry.PublicKey];
        service.ReloadNetworks();
        Dispatcher.UIThread.Invoke(host.LoadNetworks);
        Check("换公共网络：默认是 Jay 公共网络", host.SelectedNetworkChoice is { IsBuiltIn: true } && host.NetworkChoices.Count == 1,
            string.Join(" | ", host.NetworkChoices.Select(c => c.Label)));

        var node = $"tcp://127.0.0.1:{EasyTierArgs.FreeTcpPort()}";
        var info = new NetworkInfo("selftest-net", "自检公共网络", "jay-selftest-" + Guid.NewGuid().ToString("N")[..8], "selftest-secret", [node]);
        Dispatcher.UIThread.Invoke(() =>
        {
            host.NetworkInput = NetworkString.Create(info, registry);
            host.AddNetworkCommand.Execute(null);
        });
        Check("换公共网络：审核过的连接串直接加上并选中", host.SelectedNetworkChoice is { Id: "selftest-net", Reviewed: true }
            && service.Settings.PublicNetworkId == "selftest-net" && host.PublicStateText.Contains("自检公共网络"), host.Status + " / " + host.PublicStateText);

        // 这个网络的种子节点（和 jay-server directory --network 一样；目录服务沿用前面开的那个）。
        using (var seed = PublicNetworkSession.Start(service.EasyTier, new PublicNetworkOptions(service.Networks.Find("selftest-net")!.ToProfile() with { Nodes = [] },
            EasyTierArgs.FreeTcpPort(), EasyTierArgs.FreeTcpPort(), Directory: true, Listeners: [node]), Path.Combine(reportDir, "public-seed-2")))
        {
            await host.ConnectPublicCommand.ExecuteAsync(null);
            Check("换公共网络：连进新网络（找得到它的目录）", await WaitAsync(() => host.IsPublicConnected, 60) && host.PublicStateText.Contains("自检公共网络"), host.Status);
            host.DisconnectPublicCommand.Execute(null);
        }

        // 别人签的：先提醒（显示签名钥匙），确认后才加。
        var other = new NetworkInfo("selftest-other", "没审核的网络", "jay-selftest-other", "other-secret", ["tcp://203.0.113.5:11010"]);
        Dispatcher.UIThread.Invoke(() =>
        {
            host.NetworkInput = NetworkString.Create(other, someone);
            host.AddNetworkCommand.Execute(null);
        });
        Check("换公共网络：没审核的先提醒、还没加", host.NetworkConfirmOpen && host.NetworkConfirmText.Contains("没审核的网络") && service.Networks.Find("selftest-other") is null, host.Status);
        var page = await FindAsync<MultiplayerPage>(_ => true);
        if (page?.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => Equals(e.Header, L.T("multiplayer.network.title"))) is { } expander)
        {
            Dispatcher.UIThread.Invoke(() => expander.IsExpanded = true);
            await Task.Delay(300);
            Dispatcher.UIThread.Invoke(() => expander.BringIntoView());
        }
        await ShotAsync("换公共网络提醒");
        Dispatcher.UIThread.Invoke(() => host.ConfirmAddNetworkCommand.Execute(null));
        Check("换公共网络：确认后加上（标着未审核）", host.SelectedNetworkChoice is { Id: "selftest-other", Reviewed: false } && host.SelectedNetworkChoice.Label.Contains('⚠'), host.Status);

        // 别的钥匙冒用同一个编号：拒绝。
        Dispatcher.UIThread.Invoke(() =>
        {
            host.NetworkInput = NetworkString.Create(info with { Seq = 5 }, someone);
            host.AddNetworkCommand.Execute(null);
        });
        Check("换公共网络：同编号换了钥匙的拒绝", service.Networks.Find("selftest-net")!.SignerKey.AsSpan().SequenceEqual(registry.PublicKey)
            && host.Status == L.F("multiplayer.network.conflict", "selftest-net"), host.Status);

        // 删掉：回到 Jay 公共网络。
        Dispatcher.UIThread.Invoke(() =>
        {
            host.RemoveNetworkCommand.Execute(null);
            host.SelectedNetworkChoice = host.NetworkChoices.First(c => c.Id == "selftest-net");
            host.RemoveNetworkCommand.Execute(null);
        });
        Check("换公共网络：删掉后回到 Jay 公共网络", host.SelectedNetworkChoice is { IsBuiltIn: true } && service.Settings.PublicNetworkId is null
            && (service.Settings.PublicNetworks ?? []).Count == 0, host.Status);
    }

    /// <summary>设备指纹：默认关着；打开后生成的码每次不同；房主隐藏房间 + 指纹查找 + 要求设备指纹；加入方按码找到房间，没开指纹进不去，开了、在名单里、房主批准后能进；换新后旧码找不到。</summary>
    private async Task DeviceFingerprintAsync(MultiplayerViewModel host, MultiplayerViewModel guest)
    {
        host.UseTestDevice(Path.Combine(reportDir, "device-host"));
        guest.UseTestDevice(Path.Combine(reportDir, "device-guest"));
        Check("设备指纹：默认关着", !host.DeviceEnabled && !guest.DeviceEnabled && host.MyFingerprintCode.Length == 0);
        host.EnableDeviceCommand.Execute(null);
        var first = host.MyFingerprintCode;
        host.NewFingerprintCodeCommand.Execute(null);
        var code = host.MyFingerprintCode;
        Check("设备指纹：打开后生成指纹码，每次都不一样", host.DeviceEnabled && first.StartsWith("JAYF1-") && code.StartsWith("JAYF1-") && first != code, first + " / " + code);
        var page = await FindAsync<MultiplayerPage>(_ => true);
        if (page?.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => Equals(e.Header, L.T("multiplayer.device.title"))) is { } expander)
        {
            Dispatcher.UIThread.Invoke(() => expander.IsExpanded = true);
            await Task.Delay(300);
            Dispatcher.UIThread.Invoke(() => expander.BringIntoView());
        }
        await ShotAsync("设备指纹");

        host.SelectedVisibility = host.VisibilityChoices.Single(c => c.Mode == RoomVisibility.Hidden);
        host.AllowFingerprintSearch = true;
        host.RequireDevice = true;
        await host.PublishRoomCommand.ExecuteAsync(null);
        await Task.Delay(1500);
        await guest.RefreshRoomsCommand.ExecuteAsync(null);
        Check("设备指纹：隐藏房间不在列表里", guest.PublicRooms.All(r => r.Listing.Name != "自检房间"));
        guest.FindCodeInput = first;
        await guest.FindByFingerprintCommand.ExecuteAsync(null);
        Check("设备指纹：按指纹码找到房主的隐藏房间", guest.FoundRoom?.Listing.Name == "自检房间", guest.Status);
        await guest.JoinFoundRoomCommand.ExecuteAsync(null);
        Check("设备指纹：房主要求设备指纹，没打开进不去", guest.IsIdle && guest.Status == L.T("multiplayer.device.reason"), guest.Status);

        guest.EnableDeviceCommand.Execute(null);
        host.AllowedDevicesText = guest.MyFingerprintCode;
        var joining = guest.JoinFoundRoomCommand.ExecuteAsync(null);
        var asked = await WaitAsync(() => host.ApprovalCards.Count == 1, 30);
        Check("设备指纹：房主的敲门卡片带设备编号", asked && host.ApprovalCards[0].Detail.Contains(guest.Device!.DeviceId), asked ? host.ApprovalCards[0].Detail : guest.Status);
        if (asked) host.ApproveKnockCommand.Execute(host.ApprovalCards[0]);
        await joining;
        Check("设备指纹：在名单里、房主批准后进了房间", guest.IsJoined, guest.Status);
        guest.StopCommand.Execute(null);

        // 换新：以前的码全部作废。
        host.RotateDeviceCommand.Execute(null);
        await Task.Delay(1500);
        guest.FindCodeInput = code;
        await guest.FindByFingerprintCommand.ExecuteAsync(null);
        Check("设备指纹：换新后旧码找不到", guest.FoundRoom is null && guest.Status == L.T("multiplayer.device.not_found"), guest.Status);
        guest.FindCodeInput = host.MyFingerprintCode;
        await guest.FindByFingerprintCommand.ExecuteAsync(null);
        Check("设备指纹：新码找得到", guest.FoundRoom is not null, guest.Status);
        host.RequireDevice = false;
        host.AllowedDevicesText = "";
        host.AllowFingerprintSearch = false;
    }

    /// <summary>私聊和传文件：双方在线名单里看到对方；私聊只到对方；发文件对方接收后完整收到。</summary>
    private async Task PrivateChatAsync(MultiplayerViewModel host, MultiplayerViewModel guest)
    {
        var seen = await WaitAsync(() => guest.OnlinePeers.Any(p => p.Entry.Fingerprint == host.Identity.Fingerprint)
            && host.OnlinePeers.Any(p => p.Entry.Fingerprint == guest.Identity.Fingerprint), 20);
        Check("私聊：双方在线名单里看到对方", seen, string.Join(" | ", guest.OnlinePeers.Select(p => p.Label)) + " / " + string.Join(" | ", host.OnlinePeers.Select(p => p.Label))
            + $" / chat {guest.ChatReady}:{host.ChatReady} invite {guest.IsInviteRoom}:{host.IsInviteRoom} {guest.PrivateDebug} {host.PrivateDebug} / {guest.ChatStatus} / {host.ChatStatus}");
        if (!seen) return;
        guest.ChoosePeerCommand.Execute(guest.OnlinePeers.First(p => p.Entry.Fingerprint == host.Identity.Fingerprint));
        guest.PrivateInput = "只给房主看的悄悄话";
        await guest.SendPrivateCommand.ExecuteAsync(null);
        Check("私聊：对方收到私聊", await WaitAsync(() => host.PrivateLines.Any(l => l.Text == "只给房主看的悄悄话" && !l.Mine), 10)
            && guest.PrivateLines.Any(l => l.Mine), string.Join(" | ", host.PrivateLines.Select(l => l.Display)));
        Check("私聊：没进公共聊天", host.ChatLines.All(l => l.Message.Text != "只给房主看的悄悄话"));

        var source = Path.Combine(reportDir, "send-test.bin");
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(source, data);
        var sending = guest.SendFileToAsync(guest.SelectedPeer!, source);
        var offered = await WaitAsync(() => host.FileOffers.Count == 1, 15);
        Check("传文件：对方看到“想发给你”", offered && host.FileOffers[0].Label.Contains("send-test.bin"), offered ? host.FileOffers[0].Label : "");
        if (offered) await host.AcceptFileCommand.ExecuteAsync(host.FileOffers[0]);
        var sent = await sending.WaitAsync(TimeSpan.FromSeconds(60));
        var saved = Path.Combine(host.DownloadsDir, "send-test.bin");
        var received = await WaitAsync(() => File.Exists(saved), 10);
        Check("传文件：接收后完整收到（校验一致）", sent && received && File.ReadAllBytes(saved).AsSpan().SequenceEqual(data),
            string.Join(" | ", host.Transfers.Select(t => t.Text)));
        await ShotPageAsync(guest, "私聊和传文件", L.T("multiplayer.private.title"));
    }
}
