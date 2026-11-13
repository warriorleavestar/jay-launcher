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
        // A typed port is named once ("linked to port N"), not as "port N (port N)".
        Assert.Equal(L.F("multiplayer.relay.linked_port", game.Port), vm.GameLinkText);
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

    [Fact]
    public void ALinkedGameThatStopsAnsweringPutsTheRoomBackToWaiting() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var game = new EchoGame(0x7E);
        var answers = true;
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            GameTargetProbeForTest = _ => Task.FromResult(answers),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        var typed = game.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ManualPort = typed;
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;

        // Still answering: the link stays.
        Check(vm.WatchGameTargetAsync());
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(game.Port, relay.Target);

        // One miss could be a hiccup; the second drops the game and the room waits again, with the typed port cleared.
        answers = false;
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(game.Port, relay.Target);
        Check(vm.WatchGameTargetAsync());
        Assert.Null(relay.Target);
        Assert.True(vm.IsWaitingForGame);
        Assert.Equal(L.T("multiplayer.relay.waiting"), vm.GameLinkText);
        Assert.Equal("multiplayer.relay.game_gone", vm.StatusInfo?.Key);
        Assert.Equal("", vm.ManualPort);

        // The same port again (the game came back) links again.
        answers = true;
        vm.ManualPort = typed;
        Pump(() => !vm.IsWaitingForGame);
        Assert.Equal(game.Port, relay.Target);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void DroppingAClosedGameDoesNotStopASwitchInProgress() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var first = new EchoGame(0x11);
        using var second = new EchoGame(0x22);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            // The first game no longer answers; the second does.
            GameTargetProbeForTest = port => Task.FromResult(port != first.Port),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(first.Port, relay.Target);

        // The player types the second game's port; while that switch waits out the typing delay, the first is dropped.
        vm.RelayTypingDelay = TimeSpan.FromMilliseconds(300);
        vm.ManualPort = second.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Check(vm.WatchGameTargetAsync());
        Assert.Null(relay.Target);
        // The switch still goes through.
        Pump(() => relay.Target == second.Port);
        Assert.False(vm.IsWaitingForGame);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void MissesCountForOneLinkedPortOnly() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var first = new EchoGame(0x33);
        using var second = new EchoGame(0x44);
        var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            GameTargetProbeForTest = _ => Task.FromResult(false),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;
        // One miss on the first game, then the player switches to the second.
        Check(vm.WatchGameTargetAsync());
        vm.ManualPort = second.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => relay.Target == second.Port);
        // One miss on the second game is its first: it stays linked.
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(second.Port, relay.Target);

        // A check still running when the next refresh comes neither counts twice nor forgets the misses so far.
        vm.GameTargetProbeForTest = _ => probe.Task;
        var slow = vm.WatchGameTargetAsync();
        Check(vm.WatchGameTargetAsync());
        probe.SetResult(false);
        Check(slow);
        Assert.Null(relay.Target);
        Assert.True(vm.IsWaitingForGame);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void APortTypedDuringFindIsNotReplacedByWhatFindFound() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var first = new EchoGame(0x51);
        using var typed = new EchoGame(0x52);
        using var found = new EchoGame(0x53);
        var scan = new TaskCompletionSource<List<LanGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero, ScanForTest = () => scan.Task };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;

        // [Find] is listening when the player types another port, which links.
        vm.FindGameForRoomCommand.Execute(null);
        Pump(() => vm.IsScanning);
        var port = typed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ManualPort = port;
        Pump(() => relay.Target == typed.Port);

        // The scan then finds a new world: it is listed, but the typed port stays linked.
        scan.SetResult([new LanGame("World", found.Port)]);
        Pump(() => !vm.IsScanning);
        for (var i = 0; i < 20; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains(vm.LanGames, g => g.Game.Port == found.Port);
        Assert.Equal(typed.Port, relay.Target);
        Assert.Equal(port, vm.ManualPort);
        vm.StopCommand.Execute(null);
    });

    [Fact]
    public async Task APlayerStillConnectingToTheGameKeepsItLinked()
    {
        using var game = new EchoGame(0xB6);
        await using var relay = new GamePortRelay();
        relay.SetTarget(game.Port);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.BeforeConnectForTest = () => { held.TrySetResult(); return release.Task; };

        // A player is taken on, but the relay's own connection to the game isn't up yet.
        var trip = RoundTripAsync(relay.Port, [3]);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, relay.OpenConnectionsTo(game.Port));
        Assert.False(relay.ClearTargetIfIdle(game.Port));
        Assert.Equal(game.Port, relay.Target);

        release.SetResult();
        Assert.Equal(new byte[] { 0xB6, 3 }, await trip);
        relay.BeforeConnectForTest = null;
        // Once the player has left, an idle target can be cleared.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (relay.OpenConnectionsTo(game.Port) > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(relay.ClearTargetIfIdle(game.Port));
        Assert.Null(relay.Target);
    }

    [Fact]
    public void PlayingAGameForgetsTheMissesBefore() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var game = new EchoGame(0xC7);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            GameTargetProbeForTest = _ => Task.FromResult(false),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = game.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;
        // One miss, then a player plays and leaves.
        Check(vm.WatchGameTargetAsync());
        using (var player = new TcpClient())
        {
            player.Connect(IPAddress.Loopback, relay.Port);
            var stream = player.GetStream();
            stream.ReadTimeout = 5000;
            stream.Write([1]);
            Assert.Equal(2, stream.ReadAtLeast(new byte[2], 2));
            Pump(() => relay.OpenConnectionsTo(game.Port) == 1);
            Check(vm.WatchGameTargetAsync());
        }
        Pump(() => relay.OpenConnectionsTo(game.Port) == 0);

        // The next miss is the first since the game was in use: it stays linked.
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(game.Port, relay.Target);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void AGameThatTakesAPlayerDuringAFailingCheckStaysLinked() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var game = new EchoGame(0xA5);
        var slow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            GameTargetProbeForTest = _ => Task.FromResult(false),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = game.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;
        // One miss, then a second check that hasn't answered yet.
        Check(vm.WatchGameTargetAsync());
        vm.GameTargetProbeForTest = _ => slow.Task;
        var late = vm.WatchGameTargetAsync();

        // Meanwhile the game answers again on the same port and a player comes in through the relay.
        using var player = new TcpClient();
        player.Connect(IPAddress.Loopback, relay.Port);
        var stream = player.GetStream();
        stream.ReadTimeout = 5000;
        stream.Write([1]);
        Assert.Equal(2, stream.ReadAtLeast(new byte[2], 2));
        Pump(() => relay.OpenConnectionsTo(game.Port) == 1);

        // The second miss arrives late: the game is in use, so it stays linked.
        slow.SetResult(false);
        Check(late);
        Assert.Equal(game.Port, relay.Target);
        Assert.False(vm.IsWaitingForGame);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void ACheckFromBeforeASwitchAwayAndBackDoesNotDropTheNewLink() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var first = new EchoGame(0x91);
        using var second = new EchoGame(0x92);
        var slow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            GameTargetProbeForTest = _ => Task.FromResult(false),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        var a = first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        vm.ManualPort = a;
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;
        // One miss on A, then a second check that hasn't answered yet.
        Check(vm.WatchGameTargetAsync());
        vm.GameTargetProbeForTest = _ => slow.Task;
        var late = vm.WatchGameTargetAsync();

        // Meanwhile the player switches to B and back to A: a new link to A.
        vm.ManualPort = second.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => relay.Target == second.Port);
        vm.ManualPort = a;
        Pump(() => relay.Target == first.Port);

        // The old check now fails: it is about the earlier link, so the new one stays.
        slow.SetResult(false);
        Check(late);
        Assert.Equal(first.Port, relay.Target);
        Assert.False(vm.IsWaitingForGame);
        // The new link counts its own misses from none: one miss isn't enough.
        vm.GameTargetProbeForTest = _ => Task.FromResult(false);
        Check(vm.WatchGameTargetAsync());
        Assert.Equal(first.Port, relay.Target);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void ATypedGameThatClosesDoesNotFallBackToTheWorldPickedBefore() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var found = new EchoGame(0x71);
        using var typed = new EchoGame(0x72);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            ScanForTest = () => Task.FromResult(new List<LanGame> { new("World", found.Port) }),
            // The echo games don't answer a Minecraft status ping: found worlds only have to listen here.
            GamePortReadyForTest = (_, _) => Task.FromResult(true),
            // The typed game was closed; the found one still answers.
            GameTargetProbeForTest = p => Task.FromResult(p == found.Port),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        var relay = vm.GameRelayForTest!;
        // [Find] links the found world, then the player types another port.
        vm.FindGameForRoomCommand.Execute(null);
        Pump(() => relay.Target == found.Port && !vm.IsScanning);
        vm.ManualPort = typed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => relay.Target == typed.Port);

        // The typed game closes: the room waits instead of quietly linking the world picked before.
        Check(vm.WatchGameTargetAsync());
        Check(vm.WatchGameTargetAsync());
        Settle();
        Assert.Null(relay.Target);
        Assert.True(vm.IsWaitingForGame);
        Assert.Null(vm.SelectedLanGame);

        // Picking that world again is the player's choice, and links it.
        vm.SelectedLanGame = vm.LanGames.Single(g => g.Game.Port == found.Port);
        Pump(() => relay.Target == found.Port);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Fact]
    public void AGameThatClosesDuringFindDoesNotStopFindFromLinkingANewOne() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var closed = new EchoGame(0x81);
        using var reopened = new EchoGame(0x82);
        var scan = new TaskCompletionSource<List<LanGame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            ScanForTest = () => scan.Task,
            GameTargetProbeForTest = p => Task.FromResult(p != closed.Port),
            // The echo games don't answer a Minecraft status ping: found worlds only have to listen here.
            GamePortReadyForTest = (_, _) => Task.FromResult(true),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = closed.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;

        // [Find] is listening when the linked game is found closed and dropped.
        vm.FindGameForRoomCommand.Execute(null);
        Pump(() => vm.IsScanning);
        Check(vm.WatchGameTargetAsync());
        Check(vm.WatchGameTargetAsync());
        Assert.Null(relay.Target);

        // The world reopened on a new port is what [Find] then hears: it is linked.
        scan.SetResult([new LanGame("World", reopened.Port)]);
        Pump(() => relay.Target == reopened.Port);
        Assert.False(vm.IsWaitingForGame);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    private static void Settle()
    {
        for (var i = 0; i < 20; i++) { Thread.Sleep(10); Dispatcher.UIThread.RunJobs(); }
    }

    [Fact]
    public void AConnectionToTheWorldBeforeDoesNotHideThatTheLinkedOneClosed() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        using var first = new EchoGame(0x61);
        using var second = new EchoGame(0x62);
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            GameTargetCheckInterval = TimeSpan.Zero,
            // The first world still answers; the second was closed.
            GameTargetProbeForTest = p => Task.FromResult(p == first.Port),
        };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        vm.ManualPort = first.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => !vm.IsWaitingForGame);
        var relay = vm.GameRelayForTest!;

        // A player stays connected to the first world while the host switches to the second.
        using var player = new TcpClient();
        player.Connect(IPAddress.Loopback, relay.Port);
        var stream = player.GetStream();
        stream.Write([1]);
        var echo = new byte[2];
        stream.ReadTimeout = 5000;
        Assert.Equal(2, stream.ReadAtLeast(echo, 2));
        Pump(() => relay.OpenConnectionsTo(first.Port) == 1);
        vm.ManualPort = second.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Pump(() => relay.Target == second.Port);
        Assert.Equal(0, relay.OpenConnectionsTo(second.Port));

        // The second world closing is still noticed.
        Check(vm.WatchGameTargetAsync());
        Check(vm.WatchGameTargetAsync());
        Assert.Null(relay.Target);
        Assert.True(vm.IsWaitingForGame);
        Assert.Equal(1, relay.OpenConnections);
        vm.StopCommand.Execute(null);

        void Check(Task watch)
        {
            Pump(() => watch.IsCompleted);
            watch.GetAwaiter().GetResult();
        }
    });

    [Theory]
    // A world that wasn't listed before (reopened on a new port) wins, even over the linked one still answering.
    [InlineData(new[] { 100, 200 }, new[] { 200 }, 100, 300, 200)]
    // Nothing new and the linked world still answers: keep it.
    [InlineData(new[] { 100, 150 }, new int[0], 100, 300, null)]
    // The linked world is gone: take another found one.
    [InlineData(new[] { 150 }, new int[0], 100, 300, 150)]
    // Waiting for the game: the first found world.
    [InlineData(new[] { 150, 160 }, new int[0], null, 300, 150)]
    // The relay's own port is never a game.
    [InlineData(new[] { 300 }, new[] { 300 }, null, 300, null)]
    public void FindWhileLinkedPicksTheReopenedWorld(int[] found, int[] fresh, int? target, int relayPort, int? expected) =>
        Assert.Equal(expected, MultiplayerViewModel.RelayPickAfterScan(found, fresh, target, relayPort));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void OnlyUnwrappedWholeLanRoomsListenOnTheAdapter(bool fullLan, bool wraps, bool onAdapter) =>
        // Every room opens without a game on a relay; guests reach it on loopback except in an unwrapped whole-LAN room.
        Assert.Equal(onAdapter, MultiplayerViewModel.RelayOnAdapter(fullLan, wraps));

    [Fact]
    public async Task TheRelayAlsoListensOnTheAdapterOnceItsAddressAppears()
    {
        using var game = new EchoGame(0x5A);
        await using var relay = new GamePortRelay();
        relay.SetTarget(game.Port);
        // The adapter's address shows up once the test says so (a loopback listener stands in for it).
        var appears = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpListener? adapter = null;
        var bound = relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), IPAddress.IsLoopback, TimeSpan.FromSeconds(10), (_, _) =>
        {
            if (!appears.Task.IsCompleted) throw new SocketException((int)SocketError.AddressNotAvailable);
            adapter = new TcpListener(IPAddress.Loopback, 0);
            adapter.Start();
            return adapter;
        });
        Assert.False(relay.ListensOnAdapter);
        appears.SetResult();
        await bound.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(relay.ListensOnAdapter);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)adapter!.LocalEndpoint).Port);
        var s = client.GetStream();
        await s.WriteAsync(new byte[] { 7 });
        var reply = new byte[2];
        await s.ReadExactlyAsync(reply);
        Assert.Equal(new byte[] { 0x5A, 7 }, reply);
    }

    [Fact]
    public async Task TheAdapterListenerTurnsAwayCallersFromOutsideTheRoom()
    {
        // Packets for the virtual address can arrive on another adapter: only the room's subnet is passed on.
        using var game = new EchoGame(0x5A);
        await using var relay = new GamePortRelay();
        relay.SetTarget(game.Port);
        TcpListener? adapter = null;
        await relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), _ => false, TimeSpan.FromSeconds(5), (_, _) =>
        {
            adapter = new TcpListener(IPAddress.Loopback, 0);
            adapter.Start();
            return adapter;
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)adapter!.LocalEndpoint).Port);
        var s = client.GetStream();
        try { await s.WriteAsync(new byte[] { 7 }); } catch (IOException) { }
        var buffer = new byte[2];
        int read;
        try { read = await s.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { read = 0; }
        Assert.Equal(0, read);
        Assert.Equal(0, relay.OpenConnections);
    }

    [Theory]
    [InlineData("10.233.144.7", true)]
    [InlineData("10.233.144.1", false)]
    [InlineData("10.233.145.7", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("127.0.0.1", false)]
    public void OnlyTheRoomSubnetMayUseTheAdapterRelay(string source, bool allowed) =>
        Assert.Equal(allowed, MultiplayerViewModel.InRoomSubnet(IPAddress.Parse(source)));

    [Fact]
    public async Task AnAdapterThatGoesAwayClearsTheListenerAndSaysSo()
    {
        await using var relay = new GamePortRelay();
        TcpListener? adapter = null;
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), _ => true, TimeSpan.FromSeconds(5), (_, _) =>
        {
            adapter = new TcpListener(IPAddress.Loopback, 0);
            adapter.Start();
            return adapter;
        }, lost: e => lost.TrySetResult(e));
        Assert.True(relay.ListensOnAdapter);
        adapter!.Stop();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(relay.ListensOnAdapter);
    }

    [Fact]
    public async Task DisposingTheRelayIsNotALostAdapter()
    {
        var relay = new GamePortRelay();
        var lost = 0;
        await relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), _ => true, TimeSpan.FromSeconds(5), (_, _) =>
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return l;
        }, lost: _ => Interlocked.Increment(ref lost));
        await relay.DisposeAsync();
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    public async Task ACallerThatGivesUpBeforeAcceptDoesntStopTheAdapterListener(SocketError error)
    {
        using var game = new EchoGame(0x5A);
        await using var relay = new GamePortRelay();
        relay.SetTarget(game.Port);
        // The first accept fails the way a caller's early reset does; the next caller still gets through.
        var first = 1;
        relay.AcceptForTest = (l, ct) => Interlocked.Exchange(ref first, 0) == 1
            ? ValueTask.FromException<TcpClient>(new SocketException((int)error))
            : l.AcceptTcpClientAsync(ct);
        var lost = 0;
        TcpListener? adapter = null;
        await relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), IPAddress.IsLoopback, TimeSpan.FromSeconds(5), (_, _) =>
        {
            adapter = new TcpListener(IPAddress.Loopback, 0);
            adapter.Start();
            return adapter;
        }, lost: _ => Interlocked.Increment(ref lost));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)adapter!.LocalEndpoint).Port);
        var s = client.GetStream();
        await s.WriteAsync(new byte[] { 7 });
        var reply = new byte[2];
        await s.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x5A, 7 }, reply);
        Assert.True(relay.ListensOnAdapter);
        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task ARelayWhoseAdapterNeverGetsItsAddressSaysSo()
    {
        await using var relay = new GamePortRelay();
        var e = await Assert.ThrowsAsync<SocketException>(() => relay.ListenOnAdapterAsync(IPAddress.Parse("10.235.255.254"), _ => true, TimeSpan.FromMilliseconds(600),
            (_, _) => throw new SocketException((int)SocketError.AddressNotAvailable)));
        Assert.Equal(SocketError.AddressNotAvailable, e.SocketErrorCode);
        Assert.False(relay.ListensOnAdapter);
    }

    [Fact]
    public void AWholeLanRoomOpensWithoutAGame() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        vm.FullLanMode = true;
        // Where this platform has no whole-LAN scope the switch stays off; the rest needs it.
        Assert.Equal(vm.CanUseFullLan, vm.FullLanMode);
        if (!vm.FullLanMode) return;
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        // No "needs the game first": the room waits on a relay like any other.
        Assert.True(prepared.Result);
        Assert.True(vm.HasGameRelay);
        Assert.True(vm.IsWaitingForGame);
        vm.StopCommand.Execute(null);
    });

    [Fact]
    public void ARelayThatCantListenOnTheAdapterEndsTheRoomWithTheReason() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            AdapterWaitForTest = TimeSpan.FromMilliseconds(500),
            AdapterListenForTest = (_, _) => throw new SocketException((int)SocketError.AddressNotAvailable),
        };
        // An unwrapped whole-LAN room's relay must also listen on the virtual address; it never appears.
        var prepared = vm.PrepareGameRelayForTest(onAdapter: true);
        Pump(() => prepared.IsCompleted);
        Assert.True(vm.HasGameRelay);
        Pump(() => vm.StatusInfo?.Key == "multiplayer.room.closed_adapter_not_ready");
        Assert.False(vm.HasGameRelay);
    });

    [Fact]
    public void ARelayThatCantUseItsAdapterPortEndsTheRoom() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            AdapterListenForTest = (_, _) => throw new SocketException((int)SocketError.AddressAlreadyInUse),
        };
        var prepared = vm.PrepareGameRelayForTest(onAdapter: true);
        Pump(() => prepared.IsCompleted);
        // The relay's port is one the system picked: no "is another Jay running?" for it.
        Pump(() => vm.StatusInfo?.Key == "multiplayer.relay.adapter_failed");
        Assert.False(vm.HasGameRelay);
    });

    [Fact]
    public void ARelayThatFailsOddlyOnTheAdapterStillEndsTheRoom() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            AdapterListenForTest = (_, _) => throw new ArgumentException("odd"),
        };
        var prepared = vm.PrepareGameRelayForTest(onAdapter: true);
        Pump(() => prepared.IsCompleted);
        Pump(() => vm.StatusInfo?.Key == "multiplayer.relay.adapter_failed");
        Assert.False(vm.HasGameRelay);
    });

    [Fact]
    public void ARelayListensAgainWhenItsAdapterListenerStops() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var listeners = new System.Collections.Concurrent.ConcurrentQueue<TcpListener>();
        var vm = new MultiplayerViewModel(service)
        {
            RelayTypingDelay = TimeSpan.Zero,
            AdapterListenForTest = (_, _) =>
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                listeners.Enqueue(l);
                return l;
            },
        };
        var prepared = vm.PrepareGameRelayForTest(onAdapter: true);
        Pump(() => prepared.IsCompleted);
        Pump(() => vm.GameRelayForTest!.ListensOnAdapter);
        // The adapter goes away and comes back a few times: the room listens again each time ...
        for (var i = 1; i <= MultiplayerViewModel.AdapterRelistens; i++)
        {
            listeners.Last().Stop();
            var n = i + 1;
            Pump(() => listeners.Count == n && vm.GameRelayForTest?.ListensOnAdapter == true);
            Assert.True(vm.HasGameRelay);
        }
        // ... until it gives up, and then the room says so instead of looking open.
        listeners.Last().Stop();
        Pump(() => vm.StatusInfo?.Key == "multiplayer.relay.adapter_failed");
        Assert.False(vm.HasGameRelay);
    });

    [Fact]
    public void AProtectedChannelThatCantBindEndsTheRoom() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        var bound = new TaskCompletionSource();
        var watch = vm.WatchAdapterBindAsync(bound.Task, () => true, 47999, relay: false);
        Assert.True(vm.HasGameRelay);
        bound.SetException(new SocketException((int)SocketError.AddressAlreadyInUse));
        Pump(() => watch.IsCompleted);
        Assert.Equal("multiplayer.room.closed_adapter_port", vm.StatusInfo?.Key);
        Assert.Equal(47999, vm.StatusInfo!.Args[0]);
        Assert.False(vm.HasGameRelay);
    });

    [Fact]
    public void AListenerFromAnEarlierRoomChangesNothing() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        var watch = vm.WatchAdapterBindAsync(Task.FromException(new SocketException((int)SocketError.AddressNotAvailable)), () => false, 47999, relay: false);
        Pump(() => watch.IsCompleted);
        Assert.True(vm.HasGameRelay);
        vm.StopCommand.Execute(null);
    });

    [Fact]
    public void AHelperThatGivesUpByItselfIsNotCalledSlow() => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        // Cancelled for its own reasons, long before the wait is up: the reason is shown, not "after two minutes".
        var watch = vm.WatchSessionStartAsync(_ => Task.FromCanceled(new CancellationToken(true)), () => true, TimeSpan.FromMinutes(10));
        Pump(() => watch.IsCompleted);
        Assert.Equal("multiplayer.room.full_lan_start_failed", vm.StatusInfo?.Key);
        Assert.Equal(L.T("multiplayer.room.full_lan_helper_stopped"), vm.StatusInfo!.Args[0]?.ToString());
        Assert.False(vm.HasGameRelay);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWholeLanHelperThatDoesntStartEndsTheRoom(bool hangs) => UiTestThread.Run(() =>
    {
        using var service = new LauncherService(new AppSettings { GameDirectory = TestDataDir.Path, PlayerName = "" });
        var vm = new MultiplayerViewModel(service) { RelayTypingDelay = TimeSpan.Zero };
        var prepared = vm.PrepareGameRelayForTest();
        Pump(() => prepared.IsCompleted);
        Func<CancellationToken, Task> ready = hangs
            ? ct => Task.Delay(Timeout.Infinite, ct)
            : _ => Task.FromException(new IOException("helper gone"));
        var watch = vm.WatchSessionStartAsync(ready, () => true, TimeSpan.FromMilliseconds(100));
        Pump(() => watch.IsCompleted);
        Assert.Equal("multiplayer.room.full_lan_start_failed", vm.StatusInfo?.Key);
        if (!hangs) Assert.Contains("helper gone", vm.StatusInfo!.Args[0]?.ToString(), StringComparison.Ordinal);
        Assert.False(vm.HasGameRelay);
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
