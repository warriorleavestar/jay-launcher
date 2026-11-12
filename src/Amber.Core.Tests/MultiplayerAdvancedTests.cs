using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Amber.Core.Navigation;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>The multiplayer advanced settings panel (redesign batch 10): defaults, "Customised" groups and the reset.</summary>
public sealed class MultiplayerAdvancedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amber-mp-adv-" + Guid.NewGuid().ToString("N"));

    public MultiplayerAdvancedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly AdvancedValues D = AdvancedSettings.Defaults;

    [Fact]
    public void TheDefaultsAreTheStandardLevelAndAFreshInstall()
    {
        Assert.Equal(RoomOpening.Tunnel(RoomProtection.Standard), new TunnelSettings(D.Transport, D.Disguise));
        var fresh = new AppSettings();
        Assert.Equal(fresh.SecurityAttestation, D.Attestation);
        Assert.Equal(fresh.SecurityBreaker, D.Breaker);
        Assert.Equal(fresh.BreakerRestartDelay, D.BreakerDelay);
        Assert.Equal(fresh.BreakerSkipDelay, D.BreakerSkip);
        Assert.Equal(fresh.MultiplayerRelayOnly, D.RelayOnly);
        Assert.Equal(fresh.MultiplayerAutoCopyInvite, D.AutoCopyInvite);
        Assert.Equal(fresh.MultiplayerKnockWait, D.KnockWait);
        Assert.Equal(fresh.MultiplayerRequestPopup, D.RequestPopup);
        Assert.Equal(fresh.MultiplayerAllowModSync, D.ModSync);
        Assert.Empty(AdvancedSettings.Customized(D));
    }

    [Fact]
    public void EachChangeMarksItsOwnGroup()
    {
        Assert.Equal([AdvancedSettings.Protection], AdvancedSettings.Customized(D with { Transport = TransportMode.High }));
        Assert.Equal([AdvancedSettings.Protection], AdvancedSettings.Customized(D with { Attestation = "required" }));
        Assert.Equal([AdvancedSettings.Protection], AdvancedSettings.Customized(D with { BreakerDelay = 30 }));
        Assert.Equal([AdvancedSettings.Connection], AdvancedSettings.Customized(D with { RelayOnly = true }));
        Assert.Equal([AdvancedSettings.Invites], AdvancedSettings.Customized(D with { KnockWait = "forever" }));
        Assert.Equal([AdvancedSettings.Room], AdvancedSettings.Customized(D with { ModSync = false }));
        Assert.Equal([AdvancedSettings.Protection, AdvancedSettings.Invites, AdvancedSettings.Room],
            AdvancedSettings.Customized(D with { Disguise = true, RequestPopup = false, GameProxy = true }));
        // Same wait, other spelling: not a change. Routing through me without the whole LAN does nothing: not a change.
        Assert.Empty(AdvancedSettings.Customized(D with { KnockWait = " 10M " }));
        Assert.Empty(AdvancedSettings.Customized(D with { NetworkExit = true }));
        Assert.Equal([AdvancedSettings.Connection, AdvancedSettings.Room], AdvancedSettings.Customized(D with { FullLan = true, NetworkExit = true }));
    }

    [Fact]
    public void EveryPanelControlIsAFindableSetting()
    {
        string[] fields = ["ProtectionLevel", "Transport", "Disguise", "Attestation", "Breaker", "DefaultRelayOnly", "FullLanMode", "InviteAutoCopy",
            "DefaultKnockWait", "RequestPopup", "ModSyncDefault", "AllowGameProxy", "AllowExitNode", "Troubleshoot", "MultiplayerReset"];
        foreach (var field in fields) Assert.Equal(SettingsCatalog.Multiplayer, SettingsCatalog.Find(field)?.Group);
        Assert.Equal(SettingsCatalog.Network, SettingsCatalog.Find("Volunteer")?.Group);
    }

    [Fact]
    public void ThePanelMarksChangesAndResetsAfterAsking()
    {
        UiTestThread.Run(() =>
        {
            using var service = new LauncherService(new AppSettings { GameDirectory = _root, PlayerName = "" });
            var settings = new SettingsViewModel(service);
            var mp = new MultiplayerViewModel(service) { ContactsDir = _root, TrustedFile = Path.Combine(_root, "trusted.json") };
            settings.Multiplayer = mp;
            Assert.False(settings.AnyAdvancedCustomized);

            mp.SelectedTransport = mp.TransportChoices.First(c => c.Mode == TransportMode.High);
            mp.DefaultRelayOnly = true;
            mp.SelectedDefaultKnockWait = mp.KnockWaitChoices.First(c => c.Wait == KnockWait.Forever);
            mp.AllowModSync = false;
            settings.SecurityBreaker = false;
            Assert.True(settings.ProtectionCustomized);
            Assert.True(settings.ConnectionCustomized);
            Assert.True(settings.InvitesCustomized);
            Assert.True(settings.RoomCustomized);

            // The first press only asks; cancelling changes nothing.
            settings.AskAdvancedResetCommand.Execute(null);
            Assert.True(settings.ConfirmingAdvancedReset);
            settings.CancelAdvancedResetCommand.Execute(null);
            Assert.False(settings.ConfirmingAdvancedReset);
            Assert.True(service.Settings.MultiplayerRelayOnly);

            settings.AskAdvancedResetCommand.Execute(null);
            settings.ResetAdvancedCommand.Execute(null);
            Assert.False(settings.AnyAdvancedCustomized);
            Assert.False(settings.ConfirmingAdvancedReset);
            Assert.Equal(L.T("settings.mp.reset_done"), settings.AdvancedResetStatus);
            Assert.Equal(TransportMode.Minecraft, mp.SelectedTransport.Mode);
            Assert.False(service.Settings.MultiplayerRelayOnly);
            Assert.Equal("10m", service.Settings.MultiplayerKnockWait);
            Assert.True(service.Settings.MultiplayerAllowModSync);
            Assert.True(service.Settings.SecurityBreaker);

            // Nothing to reset: the button does not even ask.
            settings.AskAdvancedResetCommand.Execute(null);
            Assert.False(settings.ConfirmingAdvancedReset);
        });
    }
}
