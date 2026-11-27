using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer.Gate;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// AMBER_SELFTEST_SCOPE=host-server-advanced: the server's advanced settings panel, offline. Sets up a server identity in
/// the test data folder (no MC server download, nothing started), then drives the panel in the real window: privacy level
/// refused without enough relays, add relays and their one-time codes, Privacy+ / Privacy++, entry port rotation (bad
/// input refused, on, off), two-step revoke, the address-record switch. Every change is checked against the data folder.
/// </summary>
internal sealed partial class SelfTest
{
    private async Task HostServerAdvancedAsync()
    {
        var vm = Page<MultiplayerViewModel>();
        var page = await OpenMultiplayerAsync(vm);
        if (page is null)
        {
            Check("开服高级：联机页显示", false);
            return;
        }
        var host = vm.HostServer;
        Dispatcher.UIThread.Invoke(() =>
        {
            vm.ShowTabCommand.Execute("servers");
            vm.ShowMyServer = true;
        });
        await Task.Delay(300);
        Log("数据目录：" + host.DataDirectory);
        GateState Saved() => new GateStore(JayServerRunner.GateDirectory(host.DataDirectory)).Read();

        Check("开服高级：没初始化时不显示高级设置", Dispatcher.UIThread.Invoke(() => host.IsInitialized)
            || await FindAsync<Expander>(e => e.Name == "HostServerAdvancedExpander", 1) is null);
        if (!Dispatcher.UIThread.Invoke(() => host.IsInitialized))
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                host.PublicHost = "127.0.0.1";
                host.PublicPort = "47670";
            });
            Check("开服高级：点“下一步”生成服务器身份", Press(await ButtonFor(host.InitCommand)));
            Check("开服高级：初始化完成", await WaitAsync(() => Dispatcher.UIThread.Invoke(() => host.IsInitialized), 10), host.Status);
        }

        var expander = await FindAsync<Expander>(e => e.Name == "HostServerAdvancedExpander");
        Check("开服高级：高级设置可见、默认收起", expander is { IsExpanded: false });
        if (expander is null) return;
        Dispatcher.UIThread.Invoke(() =>
        {
            expander.IsExpanded = true;
            expander.BringIntoView();
        });
        await Task.Delay(400);
        await ShotAsync("开服高级-展开");

        bool PrivacyRadiosMatch() => window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "HostPrivacy")
            .All(r => r.IsChecked == (r.Name == (host.PrivacyChoice switch { 1 => "HostPrivacyPlus", 2 => "HostPrivacyPlusPlus", _ => "HostPrivacyBasic" })));

        async Task<bool> Choose(string name)
        {
            var radio = await FindAsync<RadioButton>(r => r.Name == name);
            if (radio is null) return false;
            // Like a click (keeps the binding); setting IsChecked directly would replace it.
            Dispatcher.UIThread.Invoke(() =>
            {
                var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(radio);
                if (peer.GetProvider<Avalonia.Automation.Provider.ISelectionItemProvider>() is { } select) select.Select();
                else peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>()?.Toggle();
            });
            await Task.Delay(200);
            return true;
        }

        // Privacy+ needs two relays: refused, the radio buttons go back, the status says why.
        Check("开服高级：默认是基础档", Dispatcher.UIThread.Invoke(() => host.IsPrivacyBasic) && Saved().Privacy == PrivacyLevel.Basic);
        await Choose("HostPrivacyPlus");
        Check("开服高级：没有中转时选隐私+被拒并说明原因、单选框退回基础档", Dispatcher.UIThread.Invoke(() => host.IsPrivacyBasic && PrivacyRadiosMatch() && host.AdvancedStatus == L.T("multiplayer.hostserver.adv.plus_needs_relays"))
            && Saved().Privacy == PrivacyLevel.Basic, host.AdvancedStatus);

        // A bad relay address is refused; a good one is added and its code shows once.
        Dispatcher.UIThread.Invoke(() => host.NewRelayAddress = "not an address");
        Check("开服高级：中转地址写错时提示", Press(await ButtonFor(host.AddRelayCommand)) && host.AdvancedStatus == L.T("multiplayer.hostserver.adv.bad_relay") && Saved().Relays.Count == 0);
        Dispatcher.UIThread.Invoke(() => host.NewRelayAddress = "127.0.0.1:47681");
        Check("开服高级：加第一个中转", Press(await ButtonFor(host.AddRelayCommand)) && Saved().Relays.Count == 1 && Dispatcher.UIThread.Invoke(() => host.Relays.Count) == 1);
        var code = Dispatcher.UIThread.Invoke(() => host.RelayCode);
        Check("开服高级：加中转后给出一次性中转码", GateRelayCode.TryParse(code) is not null && await FindAsync<Border>(b => b.Name == "HostRelayCodePanel") is not null);
        Check("开服高级：中转码里没有地址", !code.Contains("127.0.0.1") && !code.Contains("47681"));
        Check("开服高级：写出在中转电脑上运行的命令（不含码）", Dispatcher.UIThread.Invoke(() => host.RelayCommandLine.Contains("--listen 47681") && !host.RelayCommandLine.Contains(code)));
        await ShotAsync("开服高级-中转码");
        Check("开服高级：【收起】藏起中转码", Press(await ButtonFor(host.HideRelayCodeCommand)) && Dispatcher.UIThread.Invoke(() => !host.HasRelayCode));

        await Choose("HostPrivacyPlus");
        Check("开服高级：一个中转时隐私+仍然被拒", Saved().Privacy == PrivacyLevel.Basic && Dispatcher.UIThread.Invoke(() => host.IsPrivacyBasic && PrivacyRadiosMatch()));
        Dispatcher.UIThread.Invoke(() => host.NewRelayAddress = "127.0.0.1:47682");
        Check("开服高级：加第二个中转", Press(await ButtonFor(host.AddRelayCommand)) && Saved().Relays.Count == 2);
        var item = Dispatcher.UIThread.Invoke(() => host.Relays.FirstOrDefault(r => r.Address == "127.0.0.1:47682"));
        var firstCode = Dispatcher.UIThread.Invoke(() => host.RelayCode);
        Check("开服高级：【新的码】换一个码", item is not null && Press(await ButtonFor(host.NewRelayCodeCommand, item))
            && Dispatcher.UIThread.Invoke(() => host.RelayCode) is { Length: > 0 } fresh && fresh != firstCode);
        Dispatcher.UIThread.Invoke(() => host.HideRelayCodeCommand.Execute(null));

        await Choose("HostPrivacyPlus");
        Check("开服高级：两个中转后选得上隐私+", Saved().Privacy == PrivacyLevel.Plus && Dispatcher.UIThread.Invoke(() => host.IsPrivacyPlus), host.AdvancedStatus);
        await Choose("HostPrivacyPlusPlus");
        Check("开服高级：两个中转都有码时选得上隐私++", Saved().Privacy == PrivacyLevel.PlusPlus && Dispatcher.UIThread.Invoke(() => host.IsPrivacyPlusPlus), host.AdvancedStatus);
        await ShotAsync("开服高级-隐私加加");

        // Port rotation: a bad range is refused, a good one turns it on and shows the current ports, then off again.
        Dispatcher.UIThread.Invoke(() =>
        {
            host.RotationMinutes = "10";
            host.RotationMinPort = "80";
            host.RotationMaxPort = "90";
        });
        Check("开服高级：端口范围不对时提示", Press(await ButtonFor(host.ApplyRotationCommand)) && host.AdvancedStatus == L.T("multiplayer.hostserver.adv.bad_rotation")
            && Saved().Rotation is not { Enabled: true });
        Dispatcher.UIThread.Invoke(() =>
        {
            host.RotationMinPort = "20000";
            host.RotationMaxPort = "45000";
        });
        Check("开服高级：打开端口轮换", Press(await ButtonFor(host.ApplyRotationCommand)) && Saved().Rotation is { Enabled: true } r && r.WindowSeconds == 600
            && Dispatcher.UIThread.Invoke(() => host.RotationEnabled));
        Check("开服高级：写出每个中转现在的端口", Dispatcher.UIThread.Invoke(() => host.RotationNow.Contains("127.0.0.1:47681") && host.RotationNow.Contains("127.0.0.1:47682")), host.RotationNow);
        await ShotAsync("开服高级-端口轮换");
        Check("开服高级：关掉端口轮换", Press(await ButtonFor(host.TurnOffRotationCommand)) && Saved().Rotation is not { Enabled: true } && Dispatcher.UIThread.Invoke(() => !host.RotationEnabled));

        // Revoke asks first; cancel leaves it; the second press revokes (epoch goes up, the relay is gone).
        await Choose("HostPrivacyBasic");
        Check("开服高级：改回基础档", Saved().Privacy == PrivacyLevel.Basic);
        item = Dispatcher.UIThread.Invoke(() => host.Relays.FirstOrDefault(r => r.Address == "127.0.0.1:47682"));
        var epoch = Saved().Epoch;
        Check("开服高级：第一次按【吊销…】只是问", item is not null && Press(await ButtonFor(host.RevokeRelayCommand, item)) && item.ConfirmRevoke && Saved().Relays.Count == 2 && Saved().Epoch == epoch);
        await ShotAsync("开服高级-确认吊销");
        Check("开服高级：【取消】不吊销", item is not null && Press(await ButtonFor(host.CancelRevokeCommand, item)) && !item.ConfirmRevoke && Saved().Relays.Count == 2);
        Press(await ButtonFor(host.RevokeRelayCommand, item));
        Check("开服高级：再按一次才吊销（大家换新钥匙）", Press(await ButtonFor(host.RevokeRelayCommand, item)) && Saved().Relays.Count == 1 && Saved().Epoch == epoch + 1
            && Dispatcher.UIThread.Invoke(() => host.Relays.Count) == 1, host.AdvancedStatus);
        item = Dispatcher.UIThread.Invoke(() => host.Relays.FirstOrDefault());
        Check("开服高级：删除中转", item is not null && Press(await ButtonFor(host.RemoveRelayCommand, item)) && Saved().Relays.Count == 0 && Saved().Epoch == epoch + 1);

        // The signed address record switch writes to the data folder and back.
        var records = await FindAsync<CheckBox>(c => c.Name == "HostRecordsSwitch");
        Dispatcher.UIThread.Invoke(() => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(records!).GetProvider<Avalonia.Automation.Provider.IToggleProvider>()?.Toggle());
        await Task.Delay(200);
        Check("开服高级：打开地址记录", Saved().RecordNetwork is not null && Dispatcher.UIThread.Invoke(() => host.RecordsEnabled), host.AdvancedStatus);
        Dispatcher.UIThread.Invoke(() => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(records!).GetProvider<Avalonia.Automation.Provider.IToggleProvider>()?.Toggle());
        await Task.Delay(200);
        Check("开服高级：关掉地址记录", Saved().RecordNetwork is null);

        foreach (var (w, h) in new[] { (1440, 900), (600, 420) })
        {
            window.Width = w;
            window.Height = h;
            await Task.Delay(400);
            Dispatcher.UIThread.Invoke(() => expander.BringIntoView());
            await ShotAsync($"开服高级-{w}");
        }
        L.SetLanguage("en");
        await Task.Delay(400);
        Dispatcher.UIThread.Invoke(() => expander.BringIntoView());
        await ShotAsync("开服高级-600-en");
        L.SetLanguage("zh-Hans");
        Check("开服高级：没有琥珀色主按钮", Dispatcher.UIThread.Invoke(() => !expander.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("primary") || b.Classes.Contains("accent"))));
    }
}
