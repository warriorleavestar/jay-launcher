using Amber.App.Services;
using Amber.App.ViewModels;

namespace Amber.Core.Tests;

/// <summary>Settings › Multiplayer and › Network edit the multiplayer page's state, also when that page was never opened.</summary>
public sealed class SettingsMultiplayerTests
{
    private const string Saved = "tcp://relay-a.example.org:11010";
    private const string Added = "tcp://relay-b.example.org:11010";

    private static void WithMain(Action<LauncherService, MainViewModel> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "amber-settings-mp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            UiTestThread.Run(() =>
            {
                using var service = new LauncherService(new AppSettings { GameDirectory = root, PlayerName = "", MultiplayerPeers = [Saved] });
                var main = new MainViewModel(service);
                main.Multiplayer.ContactsDir = root;
                main.Multiplayer.TrustedFile = Path.Combine(root, "trusted.json");
                test(service, main);
            });
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ARelayAddedInSettingsFirstKeepsTheSavedOnes()
    {
        WithMain((service, main) =>
        {
            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            settings.Activate();
            Assert.Contains(main.Multiplayer.Nodes, n => n.Url == Saved);

            main.Multiplayer.NewNodeUrl = Added;
            main.Multiplayer.AddNodeCommand.Execute(null);
            Assert.Equal(new[] { Saved, Added }, service.Settings.MultiplayerPeers!);
        });
    }

    [Fact]
    public void ARelayAddedBeforeAnyPageWasOpenedKeepsTheSavedOnes()
    {
        WithMain((service, main) =>
        {
            main.Multiplayer.NewNodeUrl = Added;
            main.Multiplayer.AddNodeCommand.Execute(null);
            Assert.Equal(new[] { Saved, Added }, service.Settings.MultiplayerPeers!);
        });
    }

    [Fact]
    public void TheMePageLoadsTheMultiplayerState()
    {
        WithMain((_, main) =>
        {
            main.Pages.OfType<AccountsViewModel>().Single().Activate();
            Assert.Contains(main.Multiplayer.Nodes, n => n.Url == Saved);
        });
    }

    [Fact]
    public void WhatAMultiplayerCommandSaysShowsOnSettingsAndTheMePageWhileOnScreen()
    {
        WithMain((_, main) =>
        {
            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            var me = main.Pages.OfType<AccountsViewModel>().Single();
            settings.Activate();

            settings.IsCurrent = true;
            main.Multiplayer.NewNodeUrl = "not a relay";
            main.Multiplayer.AddNodeCommand.Execute(null);
            Assert.Equal("multiplayer.nodes.invalid_address", settings.StatusInfo?.Key);
            Assert.NotEqual("multiplayer.nodes.invalid_address", me.StatusInfo?.Key);

            settings.IsCurrent = false;
            me.IsCurrent = true;
            main.Multiplayer.NewNodeUrl = Added;
            main.Multiplayer.AddNodeCommand.Execute(null);
            Assert.Equal("multiplayer.nodes.added", me.StatusInfo?.Key);
            Assert.Equal("multiplayer.nodes.invalid_address", settings.StatusInfo?.Key);
        });
    }

    [Fact]
    public void TheRoomHeadcountStaysOnTheMultiplayerPage()
    {
        WithMain((_, main) =>
        {
            var settings = main.Pages.OfType<SettingsViewModel>().Single();
            settings.IsCurrent = true;
            var said = 0;
            main.Multiplayer.ActionStatus += _ => said++;
            main.Multiplayer.SetRoomStatusForTest(MultiplayerViewModel.RoomCountStatus(3));
            Assert.Equal(0, said);
            Assert.NotEqual("multiplayer.room.player_count", settings.StatusInfo?.Key);
        });
    }
}
