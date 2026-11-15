using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Amber.App.Views;

/// <summary>
/// Settings › Multiplayer as the multiplayer advanced settings panel (redesign batch 10, settings-layout scope): six
/// groups that open one at a time, settings#Field opens the field's group, "Customised" tags, greyed items with a
/// reason, the links into Settings › Network, and [Reset all to defaults] behind a confirmation with focus on [Cancel].
/// </summary>
internal sealed partial class SelfTest
{
    private async Task MultiplayerAdvancedAsync(SettingsPage page, SettingsViewModel settings)
    {
        var mp = settings.Multiplayer!;
        var panel = Dispatcher.UIThread.Invoke(() => page.GetVisualDescendants().OfType<MultiplayerSettingsView>().First());
        string[] Open() => Dispatcher.UIThread.Invoke(() => panel.Groups.Where(g => g.IsExpanded).Select(g => g.Name ?? "").ToArray());
        bool Tag(string name) => Dispatcher.UIThread.Invoke(() => panel.FindControl<Border>(name)?.IsEffectivelyVisible == true);

        Check("联机高级设置：六组（保护、连接、邀请和敲门、房间里能做什么、网络和中转、排查），默认只展开“保护”",
            Dispatcher.UIThread.Invoke(() => panel.Groups.Count) == AdvancedSettings.Groups.Count && Open() is ["MpGroupProtection"], string.Join(",", Open()));
        Check("联机高级设置：都是默认值时没有“已自定义”，【全部恢复默认】不能点并写明",
            !settings.AnyAdvancedCustomized && !Tag("MpProtectionTag") && Dispatcher.UIThread.Invoke(() => !panel.MultiplayerReset.IsEffectivelyEnabled
                && panel.MultiplayerAllDefault.IsEffectivelyVisible), string.Join(",", settings.AdvancedCustomized));

        Dispatcher.UIThread.Invoke(() => panel.MpGroupInvites.IsExpanded = true);
        await Task.Delay(200);
        Check("联机高级设置：打开一组，别的组收起", Open() is ["MpGroupInvites"], string.Join(",", Open()));

        main.Shell.Navigate("settings#FullLanMode");
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "FullLanModeBox"), 3);
        Check("settings#FullLanMode：打开“连接”组并定位到字段，别的组收起", Open() is ["MpGroupConnection"]
            && Dispatcher.UIThread.Invoke(() => window.RevealedField?.IsEffectivelyVisible == true), string.Join(",", Open()));

        // Greyed items say why.
        main.Shell.Navigate("settings#AllowExitNode");
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "AllowExitNodeBox"), 3);
        Check("房间里能做什么：没开整个局域网时“经我上网”灰掉并写原因", Open() is ["MpGroupRoom"] && Dispatcher.UIThread.Invoke(() =>
            !panel.AllowExitNodeBox.IsEffectivelyEnabled && panel.AllowExitNodeReason.IsEffectivelyVisible), string.Join(",", Open()));
        var general = mp.TransportChoices.First(c => c.Mode == TransportMode.General);
        mp.SelectedTransport = general;
        main.Shell.Navigate("settings#Transport");
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "TransportBox"), 3);
        await Task.Delay(200);
        Check("保护：通用模式时“看起来像登录 MC”灰掉并写原因，档位写“已自定义”和【恢复标准】，组头标“已自定义”",
            Dispatcher.UIThread.Invoke(() => !panel.DisguiseBox.IsEffectivelyEnabled && panel.DisguiseReason.IsEffectivelyVisible
                && panel.ProtectionCustomLine.IsEffectivelyVisible) && Tag("MpProtectionTag") && settings.ProtectionCustomized);
        await ShotAsync("settings-mp-protection-custom-1440");
        Dispatcher.UIThread.Invoke(() => panel.ProtectionResetButton.Command?.Execute(null));
        await Task.Delay(200);
        Check("保护：【恢复标准】后不再标“已自定义”", mp.SelectedTransport.Mode == AdvancedSettings.Defaults.Transport && !settings.ProtectionCustomized && !Tag("MpProtectionTag"));

        // A change in one group tags that group only.
        mp.DefaultRelayOnly = true;
        mp.SelectedDefaultKnockWait = mp.KnockWaitChoices.First(c => c.Wait == KnockWait.Forever);
        await Task.Delay(200);
        Check("改了“只走中转”和“敲门等多久”：只有连接、邀请和敲门两组标“已自定义”，【全部恢复默认】能点",
            settings.AdvancedCustomized.SequenceEqual([AdvancedSettings.Connection, AdvancedSettings.Invites]) && Tag("MpConnectionTag") && Tag("MpInvitesTag")
                && !Tag("MpRoomTag") && Dispatcher.UIThread.Invoke(() => panel.MultiplayerReset.IsEffectivelyEnabled), string.Join(",", settings.AdvancedCustomized));

        // Networks and relays point into Settings › Network.
        main.Shell.Navigate("settings#Troubleshoot");
        await Task.Delay(300);
        Check("排查：【连接有问题？】在“排查”组里", Open() is ["MpGroupTroubleshoot"] && Dispatcher.UIThread.Invoke(() => panel.Troubleshoot.IsEffectivelyVisible));
        Dispatcher.UIThread.Invoke(() => panel.MpGroupNetwork.IsExpanded = true);
        await Task.Delay(200);
        await ShotAsync("settings-mp-network-1440");
        Dispatcher.UIThread.Invoke(() => panel.MpRelaysLink.Command?.Execute(panel.MpRelaysLink.CommandParameter));
        await WaitAsync(() => settings.SelectedGroupId == Amber.Core.Navigation.SettingsCatalog.Network, 3);
        Check("网络和中转：【中转】跳到设置 › 网络的中转卡片", settings.SelectedGroupId == Amber.Core.Navigation.SettingsCatalog.Network
            && Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "RelayGroup"), settings.SelectedGroupId);

        // Reset all: asks first with focus on [Cancel]; [Cancel] changes nothing; [Reset] puts everything back.
        main.Shell.Navigate("settings#MultiplayerReset");
        await WaitAsync(() => Dispatcher.UIThread.Invoke(() => window.RevealedField?.Name == "MultiplayerReset"), 3);
        Dispatcher.UIThread.Invoke(() => panel.MultiplayerReset.Command?.Execute(null));
        await Task.Delay(400);
        Check("【全部恢复默认】先问一句（写明网络和中转不变），焦点在【取消】", Dispatcher.UIThread.Invoke(() => panel.MultiplayerResetConfirm.IsEffectivelyVisible
            && panel.MultiplayerResetCancel.IsFocused && !panel.MultiplayerReset.IsVisible));
        await ShotAsync("settings-mp-reset-confirm-1440");
        Dispatcher.UIThread.Invoke(() => panel.MultiplayerResetCancel.Command?.Execute(null));
        await Task.Delay(200);
        Check("【取消】：什么都不改", settings.AnyAdvancedCustomized && mp.DefaultRelayOnly && Dispatcher.UIThread.Invoke(() => !panel.MultiplayerResetConfirm.IsVisible));
        Dispatcher.UIThread.Invoke(() => panel.MultiplayerReset.Command?.Execute(null));
        await Task.Delay(200);
        Dispatcher.UIThread.Invoke(() => panel.MultiplayerResetDo.Command?.Execute(null));
        await Task.Delay(300);
        Check("【恢复默认】：各组回到默认，没有“已自定义”，写明已恢复", !settings.AnyAdvancedCustomized && !mp.DefaultRelayOnly
            && mp.SelectedDefaultKnockWait.Wait == KnockWaits.Default && !Tag("MpConnectionTag") && !Tag("MpInvitesTag")
            && Dispatcher.UIThread.Invoke(() => panel.MultiplayerResetStatus.IsEffectivelyVisible), settings.AdvancedResetStatus);
        await ShotAsync("settings-mp-reset-done-1440");
        Dispatcher.UIThread.Invoke(() => panel.MpGroupProtection.IsExpanded = true);
    }
}
