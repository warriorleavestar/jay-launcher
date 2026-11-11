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
        // Since the four-tab lobby (official §6.14) the public network lives under Public rooms.
        Dispatcher.UIThread.Invoke(() => host.ShowTabCommand.Execute("public"));
        await Task.Delay(400);
        bool HasText(string key) => page!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == L.T(key));
        // No public network added yet: the setup card explains where public rooms come from; with one: the card and its hint.
        Check("公共网络：联机页有公共网络卡片和说明", page is not null && host.IsPublicTab && HasText("multiplayer.public.title")
            && (host.ShowPublicSetup ? HasText("multiplayer.public.setup_body") : HasText("multiplayer.public.hint")));
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

        // Volunteer relay settings: the card is there, a home-network address is refused, a public one is saved, then off again
        // (the self-check must not listen on a public port).
        Check("Volunteer relay: offered on this network", host.VolunteersAllowed);
        Dispatcher.UIThread.Invoke(() => { host.VolunteerEnabled = true; host.VolunteerAddress = "192.168.1.20"; });
        host.SaveVolunteerCommand.Execute(null);
        Check("Volunteer relay: home-network address refused", !service.Settings.VolunteerNode, host.Status);
        Dispatcher.UIThread.Invoke(() => host.VolunteerAddress = "jay.example.org");
        host.SaveVolunteerCommand.Execute(null);
        Check("Volunteer relay: public address saved", service.Settings.VolunteerNode && service.Settings.VolunteerAddress == "jay.example.org", host.Status);
        await ShotAsync("Volunteer relay settings");
        Dispatcher.UIThread.Invoke(() => host.VolunteerEnabled = false);
        host.SaveVolunteerCommand.Execute(null);
        Check("Volunteer relay: turned off again", !service.Settings.VolunteerNode, host.Status);
        // Detect and Test port need the public network; before connecting they say so and do nothing else.
        // The buttons sit in the collapsed "Volunteer relay" expander on the settings tab: open both first.
        Dispatcher.UIThread.Invoke(() => host.ShowTabCommand.Execute("settings"));
        var volunteerExpander = await FindAsync<Expander>(e => Equals(e.Header, L.T("multiplayer.volunteer.title")));
        if (volunteerExpander is not null) Dispatcher.UIThread.Invoke(() => volunteerExpander.IsExpanded = true);
        await Task.Delay(300);
        Check("Volunteer relay: Detect and Test port buttons shown",
            await FindAsync<Button>(b => ReferenceEquals(b.Command, host.DetectVolunteerAddressCommand)) is not null
            && await FindAsync<Button>(b => ReferenceEquals(b.Command, host.TestVolunteerPortCommand)) is not null);
        await host.DetectVolunteerAddressCommand.ExecuteAsync(null);
        Check("Volunteer relay: Detect asks to connect first", host.Status == L.T("multiplayer.volunteer.connect_first") && host.VolunteerAddress == "jay.example.org", host.Status);
        await host.TestVolunteerPortCommand.ExecuteAsync(null);
        Check("Volunteer relay: Test port asks to connect first", host.Status == L.T("multiplayer.volunteer.connect_first") && !host.IsCheckingVolunteer, host.Status);
        service.Settings.PublicNetworkNoticeAccepted = false;
        Dispatcher.UIThread.Invoke(() => host.ShowTabCommand.Execute("public"));
        await Task.Delay(300);
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
            await PasswordGuessLimitAsync(host, guest, locked);
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
        await NetworkSwitchAsync(host, directory);
    }

    /// <summary>
    /// 换公共网络：注册表签的连接串直接加、自动选上，再连进这个网络（本机另开一个这个网络的种子节点）；
    /// 别人签的先提醒、确认后才加；同编号换钥匙的拒绝；删掉后回到 Jay 公共网络。
    /// </summary>
    private async Task NetworkSwitchAsync(MultiplayerViewModel host, RoomDirectoryServer directory)
    {
        var service = host.TestService;
        var registry = TrustSigner.Generate(out _);
        var someone = TrustSigner.Generate(out _);
        service.TrustedNetworkKeys = [new Amber.Core.Multiplayer.Public.TrustQuorum([registry.Id], 1)];
        service.ReloadNetworks();
        Dispatcher.UIThread.Invoke(host.LoadNetworks);
        Check("换公共网络：默认是 Jay 公共网络", host.SelectedNetworkChoice is { IsBuiltIn: true } && host.NetworkChoices.Count == 1,
            string.Join(" | ", host.NetworkChoices.Select(c => c.Label)));

        // Test registry key switch (AMBER_TEST_REGISTRY_KEY): only valid 32-byte keys count, and such networks are labelled as test-reviewed.
        var testKey = Convert.ToBase64String(TrustSigner.Generate(out _).Id);
        var parsedKeys = Amber.App.Services.LauncherService.ParseTestKeys($"{testKey}, not-base64, AAAA");
        var testChoice = new NetworkChoice("t", "测试网络", true, 1, null, TestReviewed: true);
        Check("测试注册表钥匙：只认合法公钥，网络标成测试审核", parsedKeys.Count == 1 && testChoice.Label.Contains("🧪")
            && testChoice.Detail.Contains(L.T("multiplayer.network.test_reviewed")), testChoice.Detail);
        var node = $"tcp://127.0.0.1:{EasyTierArgs.FreeTcpPort()}";
        // 网络节点的账号登录（和 jay-server directory --accounts 一样）：一个开了两步验证的账号。
        var accounts = new AccountStore(Path.Combine(reportDir, "node-accounts.json"));
        accounts.Set("steve", "selftest-pass");
        var totp = TotpFactor.NewSecret();
        accounts.SetTotp("steve", totp);
        var (loginKey, loginSigner) = LoginAuthority.LoadOrCreateKeys(Path.Combine(reportDir, "node-login.key"));
        directory.Login = new LoginAuthority(accounts, loginKey, loginSigner, "selftest-net") { Factors = [new TotpFactor(accounts)] };
        var loginJson = $$"""{ "required": true, "key": "{{Convert.ToBase64String(loginKey.PublicKey)}}", "signKey": "{{Convert.ToBase64String(loginSigner.PublicKey)}}", "factors": ["totp"] }""";
        // 网络配置文件：先放进缓存（按哈希命名），地址故意用连不上的，证明是按哈希从缓存里拿的。
        var configJson = System.Text.Encoding.UTF8.GetBytes($$"""
            { "v": 1, "network": "selftest-net", "notice": "自检网络公告：只允许公开房间",
              "links": [{ "name": "网络规则", "url": "https://example.invalid/rules" }],
              "policy": { "modes": ["public"], "requireDevice": true, "maxFileMB": 1,
                "security": { "attestation": "required", "breaker": "on", "restartDelayMin": 20 } },
              "login": {{loginJson}},
              "ui": { "scope": "global", "inline": { "seed": { "accent": "#22AA66" } } } }
            """);
        var configSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(configJson));
        host.NetworkConfigDir = Path.Combine(reportDir, "network-configs");
        host.NetworkThemeDir = Path.Combine(reportDir, "network-themes");
        Directory.CreateDirectory(host.NetworkConfigDir);
        await File.WriteAllBytesAsync(Path.Combine(host.NetworkConfigDir, configSha + ".json"), configJson);
        var info = new NetworkInfo("selftest-net", "自检公共网络", "jay-selftest-" + Guid.NewGuid().ToString("N")[..8], "selftest-secret", [node],
            Config: new NetworkConfigRef("https://example.invalid/network.json", configSha));
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
            Check("网络配置：显示公告和链接", host.HasNetworkNotice && host.NetworkNotice.Contains("只允许公开房间")
                && host.NetworkLinks.SingleOrDefault()?.Name == "网络规则", host.NetworkNotice);
            Check("网络配置：策略生效（只允许公开房间、要求设备指纹、文件限 1 MB）", !host.NetworkPolicy.Allows(RoomVisibility.Password)
                && host.HostDevicePolicy.Require && !host.NetworkPolicy.AllowsFile(2 * 1024 * 1024));
            Check("网络主题：连上后换上网络的主题，global 先问用户", await WaitAsync(() => host.NetworkThemePack is not null && host.NetworkThemeAskGlobal, 10)
                && ReferenceEquals(Amber.App.Services.ThemeService.NetworkPack, host.NetworkThemePack) && !Amber.App.Services.ThemeService.NetworkGlobalAccepted);
            Check("网络配置：最低安全要求生效（设备验证必须、熔断开、倒计时至少 20 秒）",
                host.Security is { Attestation: Amber.Core.Multiplayer.Transport.AttestationMode.Required, Breaker: true } && host.Security.EffectiveRestartDelay >= 20);
            await ShotAsync("网络配置公告");
            host.AcceptNetworkThemeGlobalCommand.Execute(null);
            Check("网络主题：用户同意后整个 Jay 都用", Amber.App.Services.ThemeService.NetworkGlobalAccepted && !host.NetworkThemeAskGlobal);
            await NetworkLoginAsync(host, totp);
            host.DisconnectPublicCommand.Execute(null);
            Check("网络配置：断开后公告和策略清掉", !host.HasNetworkNotice && !host.HostDevicePolicy.Require && host.NetworkPolicy.Allows(RoomVisibility.Password));
            Check("网络配置：断开后安全要求回到本机设置", host.NetworkSecurityPolicy is null);
            Check("网络主题：断开后换回自己的主题", Amber.App.Services.ThemeService.NetworkPack is null && host.NetworkThemePack is null && !host.NetworkThemeAskGlobal);
            Check("网络账号：断开后登录框和凭证清掉", !host.HasNetworkLogin && !host.IsAccountLoggedIn && host.AccountToken is null && host.HostAccountPolicy is null);
            directory.Login = null;
            // 缓存被改过、地址又连不上：不连。
            await File.WriteAllTextAsync(Path.Combine(host.NetworkConfigDir, configSha + ".json"), "{}");
            await host.ConnectPublicCommand.ExecuteAsync(null);
            Check("网络配置：取不到配置文件就不连", !host.IsPublicConnected && !host.IsPublicConnecting && host.Status.Contains("自检公共网络"), host.Status);
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
        Check("换公共网络：同编号换了钥匙的拒绝", service.Networks.Find("selftest-net")!.SignerKey.AsSpan().SequenceEqual(registry.Id)
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
    /// <summary>网络账号：登录框按网络配置显示，密码错、缺两步验证码、码对了登录成功，房主要求登录。</summary>
    private async Task NetworkLoginAsync(MultiplayerViewModel host, byte[] totp)
    {
        Dispatcher.UIThread.Invoke(() => host.ShowTabCommand.Execute("public"));
        await Task.Delay(300);
        Check("网络账号：网络要求登录时显示登录框和验证码框", host.HasNetworkLogin && host.IsNetworkLoginRequired && host.ShowAccountLogin
            && host.AccountFactors.SingleOrDefault()?.Name == "totp"
            && (await FindAsync<MultiplayerPage>(_ => true))!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == L.T("multiplayer.account.title")));
        Check("网络账号：要求登录时房主只让登录过的人进", host.HostAccountPolicy is { Network: "selftest-net" });
        Dispatcher.UIThread.Invoke(() =>
        {
            host.AccountUser = "steve";
            host.AccountPassword = "wrong-password";
        });
        Press(await FindAsync<Button>(b => ReferenceEquals(b.Command, host.LoginAccountCommand)));
        Check("网络账号：密码错登录不了", await WaitAsync(() => !host.IsAccountLoggingIn && host.Status.Contains(L.T("core.mp.account.wrong")), 20) && !host.IsAccountLoggedIn, host.Status);
        Dispatcher.UIThread.Invoke(() => host.AccountPassword = "selftest-pass");
        await host.LoginAccountCommand.ExecuteAsync(null);
        Check("网络账号：密码对了还要两步验证码", !host.IsAccountLoggedIn && host.Status == L.T("core.mp.factor.needed") && host.HasAccountFactors, host.Status);
        await ShotAsync("网络账号两步验证");
        Dispatcher.UIThread.Invoke(() => host.AccountFactors[0].Value = TotpFactor.Code(totp, DateTimeOffset.UtcNow));
        await host.LoginAccountCommand.ExecuteAsync(null);
        Check("网络账号：验证码对了登录成功，拿到凭证", host.IsAccountLoggedIn && host.AccountLoggedInAs == "steve" && host.AccountToken is not null
            && host.AccountPassword.Length == 0, host.Status);
        await ShotAsync("网络账号已登录");
        host.LogoutAccountCommand.Execute(null);
        Check("网络账号：退出登录后凭证作废", !host.IsAccountLoggedIn && host.AccountToken is null && host.ShowAccountLogin);
    }

    /// <summary>
    /// Password guessing (KnockLimits.MaxUnknownPerMinute, 20 a minute): enough wrong passwords make the password invite go
    /// quiet for a minute, so even the right password is refused the same way as a wrong one, and the host's diagnostics say
    /// someone may be guessing. Random-token invites are not affected (the next step publishes a hidden room).
    /// </summary>
    private async Task PasswordGuessLimitAsync(MultiplayerViewModel host, MultiplayerViewModel guest, PublicRoomItem locked)
    {
        var limit = new Amber.Core.Multiplayer.KnockLimits().MaxUnknownPerMinute;
        var refused = 0;
        for (var i = 0; i < limit; i++)
        {
            guest.PublicRoomPassword = $"guess-{i:D2}";
            await guest.JoinPublicRoomCommand.ExecuteAsync(locked);
            if (guest.IsIdle && !guest.IsJoined) refused++;
            if (guest.IsJoined) guest.StopCommand.Execute(null);
        }
        Check($"公共网络：连猜 {limit} 次错密码都进不去", refused == limit, $"{refused}/{limit}，{guest.Status}");
        var warned = await WaitAsync(() => host.DebugLines.Any(l => l.Contains(L.T("core.mp.knock.guessing"))), 10);
        Check("公共网络：房主诊断里写“可能有人在猜房间密码”", warned, host.DebugLines.LastOrDefault() ?? "没有诊断");
        guest.PublicRoomPassword = "creeper-123";
        await guest.JoinPublicRoomCommand.ExecuteAsync(locked);
        Check("公共网络：猜密码被限后，对的密码也暂时进不去（和错密码一样的提示）",
            !guest.IsJoined && guest.Status == L.T("multiplayer.public.reason.password"), guest.Status);
        if (guest.IsJoined) guest.StopCommand.Execute(null);
    }

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
