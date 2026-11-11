using System.Net;
using System.Net.Sockets;
using Amber.App.Services;
using Amber.App.ViewModels;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Avalonia.Threading;

namespace Amber.Core.Tests;

/// <summary>Open the room before the game: the relay waits, then passes connections on to the game's port once it is known.</summary>
public sealed class GamePortRelayTests
{
    /// <summary>A stand-in game: echoes back whatever it gets, prefixed with a tag so tests can tell two games apart.</summary>
    private sealed class EchoGame : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public EchoGame(byte tag)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var s = c.GetStream();
                        var buffer = new byte[256];
                        try
                        {
                            int n;
                            while ((n = await s.ReadAsync(buffer, _cts.Token)) > 0)
                            {
                                await s.WriteAsync(new[] { tag }, _cts.Token);
                                await s.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                            }
                        }
                        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
                    });
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static async Task<byte[]?> RoundTripAsync(int port, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        s.ReadTimeout = 5000;
        await s.WriteAsync(payload);
        var reply = new byte[payload.Length + 1];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (read < reply.Length)
            {
                var n = await s.ReadAsync(reply.AsMemory(read), timeout.Token);
                if (n == 0) return null;
                read += n;
            }
        }
        catch (Exception e) when (e is IOException or SocketException) { return null; }
        return reply;
    }

    [Fact]
    public async Task ClosesConnectionsUntilTheGameIsKnown()
    {
        await using var relay = new GamePortRelay();
        Assert.Null(relay.Target);
        Assert.Null(await RoundTripAsync(relay.Port, [1, 2, 3]));
    }

    [Fact]
    public async Task PassesConnectionsOnAndFollowsANewPort()
    {
        using var first = new EchoGame(0xA1);
        using var second = new EchoGame(0xB2);
        await using var relay = new GamePortRelay();

        relay.SetTarget(first.Port);
        Assert.Equal(new byte[] { 0xA1, 7, 8 }, await RoundTripAsync(relay.Port, [7, 8]));

        // The world was opened to LAN again on another port: new connections go there.
        relay.SetTarget(second.Port);
        Assert.Equal(new byte[] { 0xB2, 9 }, await RoundTripAsync(relay.Port, [9]));

        relay.ClearTarget();
        Assert.Null(await RoundTripAsync(relay.Port, [1]));
    }

    [Fact]
    public async Task RejectsBadTargetsAndListensOnLoopbackOnly()
    {
        await using var relay = new GamePortRelay();
        Assert.Throws<ArgumentOutOfRangeException>(() => relay.SetTarget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => relay.SetTarget(70000));
        Assert.Throws<ArgumentException>(() => relay.SetTarget(relay.Port));
        Assert.Null(relay.Target);
    }

    [Fact]
    public async Task StopsListeningWhenDisposed()
    {
        var relay = new GamePortRelay();
        var port = relay.Port;
        await relay.DisposeAsync();
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public void RoomWaitsForTheGameThenLinksATypedPort() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var game = new EchoGame(0xC3);
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        Assert.False(vm.HasHostGame);

        // No LAN world and no port: the room can still be opened, on a relay that waits.
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        Assert.True(prepared.Result);
        Assert.True(vm.HasGameRelay);
        Assert.True(vm.IsWaitingForGame);
        Assert.Equal(L.T("multiplayer.relay.waiting"), vm.GameLinkText);
        var relay = vm.GameRelayForTest!;

        // A typed port (any TCP game) is linked once something answers on it.
        vm.ManualPort = game.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        Assert.Equal(game.Port, relay.Target);
        Assert.Equal("multiplayer.relay.connected", vm.StatusInfo?.Key);
        Assert.Contains(game.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.GameLinkText);
        var reply = Task.Run(() => RoundTripAsync(relay.Port, [5]));
        Pump(() => reply.IsCompleted);
        Assert.Equal(new byte[] { 0xC3, 5 }, reply.Result);

        // Leaving the room stops the relay.
        vm.StopCommand.Execute(null);
        Pump(() => !vm.HasGameRelay);
        Assert.Null(vm.GameRelayForTest);
    });

    [Fact]
    public void AClosedTypedPortKeepsTheRoomWaiting() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        int closed;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); closed = ((IPEndPoint)l.LocalEndpoint).Port; }
        vm.ManualPort = closed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => vm.StatusInfo?.Key == "multiplayer.lan.port_unavailable");
        Assert.True(vm.IsWaitingForGame);
        Assert.Null(vm.GameRelayForTest!.Target);
        vm.StopCommand.Execute(null);
    });

    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }
}
