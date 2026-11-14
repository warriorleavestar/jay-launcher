using System.Reflection;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>A room listed on a public network carries a standing invite; leaving the network must revoke it.</summary>
public sealed class PublicStandingInviteTests
{
    [Fact]
    public void DisconnectingFromThePublicNetworkRevokesTheStandingInvite() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var lobby = new HostLobby(_ => throw new InvalidOperationException("not started in this test"), () => [],
            _ => throw new InvalidOperationException("not started in this test"));
        try
        {
            var standing = lobby.Book.CreateStanding(autoApprove: true, new TunnelSettings(TransportMode.General, false));
            var vm = new MultiplayerViewModel(service);
            SetField(vm, "_lobby", lobby);
            SetField(vm, "_standing", standing);

            vm.DisconnectPublicCommand.Execute(null);

            Assert.True(standing.Revoked);
            Assert.False(standing.IsUsable(DateTimeOffset.UtcNow));
            Assert.Null(GetField(vm, "_standing"));
        }
        finally { lobby.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static void SetField(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static object? GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
}
