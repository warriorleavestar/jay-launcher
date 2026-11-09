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
    private async Task ShotPageAsync(MultiplayerViewModel vm, string name)
    {
        var shot = Dispatcher.UIThread.Invoke(() =>
        {
            var w = new Window { Content = new MultiplayerPage { DataContext = vm, Margin = new Avalonia.Thickness(16) }, Width = 900, Height = 760 };
            w.Show();
            return w;
        });
        await Task.Delay(800);
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
}
