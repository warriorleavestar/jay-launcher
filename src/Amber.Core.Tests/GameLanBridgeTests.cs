using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// The bridge is process-wide and these tests act as agents of this test process: nothing else may connect to this
/// process's own ports meanwhile, or it would be routed through them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameLanBridgeCollection
{
    public const string Name = "GameLanBridge";
}

/// <summary>
/// Rooms reaching a game in the Windows strong sandbox: the game's LAN bridge agent (tools/jay-lan) connects out to Jay,
/// and Jay sends a connection through it only when the port belongs to that game.
/// </summary>
[Collection(GameLanBridgeCollection.Name)]
public sealed class GameLanBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-lan-bridge-" + Guid.NewGuid().ToString("N"));
    private readonly List<List<string>> _launches = [];
    private readonly List<IDisposable> _cleanup = [];

    private const string Nonce = "00112233445566778899aabbccddeeff";

    public GameLanBridgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var args in _launches) GameLanBridge.Ended(args);
        foreach (var d in _cleanup) d.Dispose();
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void FirstLineNamesTheLaunch()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        Assert.Equal((token, (long?)null, Nonce), GameLanBridge.Parse($"JAYLAN1 {token} control {Nonce}"));
        // A control connection without a fresh nonce can't be answered with a proof.
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {token} control"));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {token} control 0123"));
        Assert.Equal((token, (long?)42, (string?)null), GameLanBridge.Parse($"JAYLAN1 {token} data 42"));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {token} data -1"));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {token} data"));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {token} open 1 25565"));
        Assert.Null(GameLanBridge.Parse($"JAYLAN2 {token} control"));
        Assert.Null(GameLanBridge.Parse("JAYLAN1 0123456789ABCDEF0123456789ABCDEF control"));
        Assert.Null(GameLanBridge.Parse("JAYLAN1 0123 control"));
    }

    [Fact]
    public void JarPathWithEqualsSignIsNotPassed()
    {
        // -javaagent:<jar>=<options> splits at the first "=": such a path would stop the game from starting.
        Assert.Null(GameLanBridge.AgentArgument(@"D:\MC=1.20\libraries\jay\jay-lan\jay-lan-0.jar"));
        var argument = GameLanBridge.AgentArgument(@"D:\MC 1.20\libraries\jay\jay-lan\jay-lan-0.jar");
        Assert.NotNull(argument);
        _launches.Add([argument]);
        Assert.Matches(@"^-javaagent:D:\\MC 1\.20\\libraries\\jay\\jay-lan\\jay-lan-0\.jar=\d+:[0-9a-f]{32}:[0-9a-f]{32}$", argument);
    }

    [Fact]
    public void PortOwnersNameThisProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new Listener();
        Assert.Contains(Environment.ProcessId, LocalPortOwners.ListenerPids(listener.Port));
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, listener.Port);
        Assert.Equal(Environment.ProcessId, LocalPortOwners.ConnectionPid((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!));
    }

    [Fact]
    public async Task PortsWithoutAnAgentAreReachedDirectly()
    {
        using var listener = new Listener();
        Assert.False(GameLanBridge.IsBridged(listener.Port));
        Assert.False(await GameLanBridge.ListenedButUnreachableAsync(listener.Port));
        using var socket = await GameLanBridge.ConnectAsync(listener.Port, TimeSpan.FromSeconds(5));
        Assert.NotNull(socket);
    }

    [Fact]
    public async Task AFirstLineThatIsNotAnAgentsIsClosed()
    {
        using var stranger = await DialAsync("GET / HTTP/1.1");
        Assert.Null(await ReadLineAsync(stranger));
        using var nobody = await DialAsync($"JAYLAN1 {new string('a', 32)} control {Nonce}");
        Assert.Null(await ReadLineAsync(nobody));
    }

    [Fact]
    public async Task NoRequestReachesAnAgentBeforeItWasAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = new Listener();
        var token = Launch();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GameLanBridge.BeforeOkForTest = () => { holding.TrySetResult(); return release.Task; };
        try
        {
            using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
            _cleanup.Add(control);
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Still in the handshake: the game's port isn't routed through this agent yet, so nothing is asked of it.
            Assert.False(GameLanBridge.IsBridged(game.Port));
            using (var direct = await GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(5))) Assert.NotNull(direct);
            release.SetResult();
            Assert.StartsWith("ok ", await ReadLineAsync(control) ?? "");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!GameLanBridge.IsBridged(game.Port) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(GameLanBridge.IsBridged(game.Port));
            // The first request comes after "ok".
            var connecting = GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(10));
            var parts = (await ReadLineAsync(control))?.Split(' ');
            Assert.True(parts is ["open", _, _]);
            await control.SendAsync(Encoding.ASCII.GetBytes($"fail {parts![1]}\n"));
            Assert.Null(await connecting);
        }
        finally { GameLanBridge.BeforeOkForTest = null; }
    }

    [Fact]
    public async Task PlayersThroughAGameAreLimitedAndGiveTheirSlotBackWhenClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = new Listener();
        var token = Launch();
        using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
        _cleanup.Add(control);
        Assert.StartsWith("ok ", await ReadLineAsync(control) ?? "");
        var before = GameLanBridge.ActiveForTest;

        // The agent answers every request with a data connection.
        var answering = Task.Run(async () =>
        {
            while (await ReadLineAsync(control, TimeSpan.FromSeconds(60)) is { } line)
                if (line.Split(' ') is ["open", var id, _])
                {
                    var data = await DialAsync($"JAYLAN1 {token} data {id}");
                    lock (_cleanup) _cleanup.Add(data);
                }
        });
        var players = new List<Socket>();
        for (var i = 0; i < GameLanBridge.MaxPerGame; i++)
            players.Add(Assert.IsAssignableFrom<Socket>(await GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(10))));
        Assert.Equal(before + GameLanBridge.MaxPerGame, GameLanBridge.ActiveForTest);
        // One more is turned away without asking the agent.
        Assert.Null(await GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(10)));

        // A player leaving gives the slot back.
        players[0].Dispose();
        Assert.Equal(before + GameLanBridge.MaxPerGame - 1, GameLanBridge.ActiveForTest);
        players[0] = Assert.IsAssignableFrom<Socket>(await GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(10)));
        foreach (var p in players) p.Dispose();
        Assert.Equal(before, GameLanBridge.ActiveForTest);
    }

    [Fact]
    public async Task ARequestThatTimesOutOrIsCancelledGivesItsSlotBackAndALateAnswerIsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var game = new Listener();
        var token = Launch();
        using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
        _cleanup.Add(control);
        Assert.StartsWith("ok ", await ReadLineAsync(control) ?? "");
        var before = GameLanBridge.ActiveForTest;

        // The agent gets the request but doesn't answer in time: the player gets nothing and the slot is back.
        var timedOut = GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromMilliseconds(500));
        var request = (await ReadLineAsync(control))?.Split(' ');
        Assert.True(request is ["open", _, _], "no request reached the agent");
        Assert.Null(await timedOut);
        Assert.Equal(before, GameLanBridge.ActiveForTest);
        // Its answer arrives afterwards: that connection is closed, not handed to anyone, and takes no slot.
        using (var late = await DialAsync($"JAYLAN1 {token} data {request![1]}"))
            Assert.Null(await ReadLineAsync(late, TimeSpan.FromSeconds(5)));
        Assert.Equal(before, GameLanBridge.ActiveForTest);

        // The caller gives up (the relay stops) while the agent is asked: the same.
        using var stop = new CancellationTokenSource();
        var cancelled = GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(30), stop.Token);
        request = (await ReadLineAsync(control))?.Split(' ');
        Assert.True(request is ["open", _, _], "no second request reached the agent");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(before, GameLanBridge.ActiveForTest);
        using (var late = await DialAsync($"JAYLAN1 {token} data {request![1]}"))
            Assert.Null(await ReadLineAsync(late, TimeSpan.FromSeconds(5)));
        Assert.Equal(before, GameLanBridge.ActiveForTest);
    }

    [Fact]
    public async Task OtherProgramsCannotCrowdOutAGamesAgentAndOnlyJayCanProveItself()
    {
        if (!OperatingSystem.IsWindows()) return;
        var argument = GameLanBridge.AgentArgument(Path.Combine(_root, "jay-lan.jar"))!;
        List<string> args = [argument];
        _launches.Add(args);
        GameLanBridge.Started(args, Process.GetCurrentProcess());
        var parts = argument[(argument.IndexOf('=') + 1)..].Split(':');
        var (token, key) = (parts[1], Convert.FromHexString(parts[2]));

        // Another program opens far more connections than there are handshake places and sends half a line on each.
        var script = "$c=@();for($i=0;$i -lt 48;$i++){$t=[Net.Sockets.TcpClient]::new('127.0.0.1'," + GameLanBridge.PortForTest + ");" +
                     "$b=[Text.Encoding]::ASCII.GetBytes('JAYLAN1 ');$t.GetStream().Write($b,0,$b.Length);$c+=$t};" +
                     "'READY';[Console]::Out.Flush();Start-Sleep -Seconds 20";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        var crowd = Process.Start(psi)!;
        _cleanup.Add(new Killer(crowd));
        Assert.Equal("READY", await crowd.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        // The game's agent still gets in at once, and Jay's answer proves it holds this launch's key.
        using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
        _cleanup.Add(control);
        Assert.Equal($"ok {GameLanBridge.Proof(key, token, Nonce)}", await ReadLineAsync(control, TimeSpan.FromSeconds(3)));
    }

    private static TcpRow Listening(string address, int port, int pid) =>
        new(new IPEndPoint(IPAddress.Parse(address), port), new IPEndPoint(IPAddress.Any, 0), pid);

    [Fact]
    public void OnlyTheListenerThatTakes127001CountsAndNeverAGuess()
    {
        const int port = 25565, game = 100, other = 200;
        // The game on IPv4, another process on the same port number on ::1 only: the game.
        Assert.Equal(game, TcpRow.LoopbackListener([Listening("127.0.0.1", port, game)], [Listening("::1", port, other)], port));
        Assert.Equal(game, TcpRow.LoopbackListener([Listening("0.0.0.0", port, game)], [Listening("::1", port, other)], port));
        // A dual-stack game ([::]) takes 127.0.0.1 too; so does one listening on both families.
        Assert.Equal(game, TcpRow.LoopbackListener([], [Listening("::", port, game)], port));
        Assert.Equal(game, TcpRow.LoopbackListener([Listening("0.0.0.0", port, game)], [Listening("::", port, game)], port));
        // Another address with the same port number doesn't take 127.0.0.1.
        Assert.Equal(game, TcpRow.LoopbackListener([Listening("127.0.0.1", port, game), Listening("127.0.0.2", port, other)], [], port));
        Assert.Null(TcpRow.LoopbackListener([Listening("127.0.0.2", port, other)], [Listening("::1", port, other)], port));
        // Two processes that could both take it: nobody is picked.
        Assert.Null(TcpRow.LoopbackListener([Listening("127.0.0.1", port, game)], [Listening("::", port, other)], port));
        Assert.Null(TcpRow.LoopbackListener([Listening("0.0.0.0", port, game), Listening("127.0.0.1", port, other)], [], port));
        // Other ports don't count.
        Assert.Null(TcpRow.LoopbackListener([Listening("127.0.0.1", port + 1, game)], [], port));
    }

    [Fact]
    public async Task AnotherGamesAgentOnTheSamePortNumberOverIpv6GetsNothing()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) return;
        // The world: this process, on 127.0.0.1 (IPv4).
        using var world = new Listener();
        var port = world.Port;
        // Another game (a separate process with its own launch and agent) listens on [::1] at the same port number.
        var script = "$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::IPv6Loopback," + port + ");$l.Start();'LISTENING';[Console]::Out.Flush();" +
                     "$t=[Console]::In.ReadLine();$c=[Net.Sockets.TcpClient]::new('127.0.0.1'," + GameLanBridge.PortForTest + ");$s=$c.GetStream();" +
                     "$b=[Text.Encoding]::ASCII.GetBytes(\"JAYLAN1 $t control 00112233445566778899aabbccddeeff`n\");$s.Write($b,0,$b.Length);$r=[IO.StreamReader]::new($s);" +
                     "while(($x=$r.ReadLine()) -ne $null){\"GOT $x\";[Console]::Out.Flush()}";
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        var other = Process.Start(psi)!;
        _cleanup.Add(new Killer(other));
        var lines = new List<string>();
        other.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
        other.BeginOutputReadLine();
        bool Saw(string text) { lock (lines) return lines.Any(l => l.StartsWith(text, StringComparison.Ordinal)); }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Saw("LISTENING") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(Saw("LISTENING"), "the other game didn't start");
        // Its agent is genuine: its own token, from its own process.
        var token = Launch(other);
        await other.StandardInput.WriteLineAsync(token);
        await other.StandardInput.FlushAsync();
        while (!Saw("GOT ok") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(Saw("GOT ok"), "the other game's agent wasn't accepted");

        // A player for the world on 127.0.0.1:port goes to the world, and the other agent is never asked.
        Assert.False(GameLanBridge.IsBridged(port));
        var accepted = world.AcceptAsync();
        using var socket = await GameLanBridge.ConnectAsync(port, TimeSpan.FromSeconds(5));
        Assert.NotNull(socket);
        using var at = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        Assert.False(Saw("GOT open"));
    }

    [Fact]
    public async Task DataConnectionForAnUnknownRequestIsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = Launch();
        using var data = await DialAsync($"JAYLAN1 {token} data 123456789");
        Assert.Null(await ReadLineAsync(data));
    }

    [Fact]
    public async Task ControlFromAnotherProcessIsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The launch started another process: its token alone gets this one nowhere.
        var token = Launch(OtherProcess());
        using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
        Assert.Null(await ReadLineAsync(control));
        Assert.Equal(0, GameLanBridge.AgentCount);
    }

    [Fact]
    public async Task RepeatedControlConnectionsReplaceEachOtherWithoutCrowdingOutOtherGames()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = Launch();
        var old = new List<Socket>();
        for (var i = 0; i < 20; i++)
        {
            var control = await DialAsync($"JAYLAN1 {a} control {Nonce}");
            _cleanup.Add(control);
            Assert.StartsWith("ok ", await ReadLineAsync(control) ?? "");
            old.Add(control);
        }
        // Each new connection replaced the one before: only the last is still open.
        foreach (var s in old.Take(19)) Assert.Null(await ReadLineAsync(s));
        var b = Launch();
        using var other = await DialAsync($"JAYLAN1 {b} control {Nonce}");
        Assert.StartsWith("ok ", await ReadLineAsync(other) ?? "");
        Assert.Equal(2, GameLanBridge.AgentCount);
    }

    [Fact]
    public async Task OnlyTheAskedAgentCanAnswerOrTurnDownARequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tokens = new[] { Launch(), Launch() };
        var controls = new Socket[2];
        for (var i = 0; i < 2; i++)
        {
            controls[i] = await DialAsync($"JAYLAN1 {tokens[i]} control {Nonce}");
            _cleanup.Add(controls[i]);
            Assert.StartsWith("ok ", await ReadLineAsync(controls[i]) ?? "");
        }
        using var game = new Listener();
        var connecting = GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(20));
        var reads = controls.Select(c => ReadLineAsync(c, TimeSpan.FromSeconds(10))).ToArray();
        var first = await Task.WhenAny(reads);
        var asked = Array.IndexOf(reads, first);
        var parts = (await first)?.Split(' ');
        Assert.True(parts is ["open", _, _], "no request reached an agent");
        var id = parts![1];

        // The other launch's agent (a game that could be malicious) turns the request down and answers it: neither counts.
        var other = 1 - asked;
        await controls[other].SendAsync(Encoding.ASCII.GetBytes($"fail {id}\n"));
        using (var stray = await DialAsync($"JAYLAN1 {tokens[other]} data {id}"))
            Assert.Null(await ReadLineAsync(stray, TimeSpan.FromSeconds(5)));
        Assert.False(connecting.IsCompleted);

        using var genuine = await DialAsync($"JAYLAN1 {tokens[asked]} data {id}");
        using var result = await connecting;
        Assert.NotNull(result);
    }

    [Fact]
    public async Task DataFromAnotherProcessIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = Launch();
        using var control = await DialAsync($"JAYLAN1 {token} control {Nonce}");
        Assert.StartsWith("ok ", await ReadLineAsync(control) ?? "");
        using var game = new Listener();
        var connecting = GameLanBridge.ConnectAsync(game.Port, TimeSpan.FromSeconds(60));
        var parts = (await ReadLineAsync(control))?.Split(' ');
        Assert.True(parts is ["open", _, _]);
        var id = parts![1];

        // Another process with the right token and id: closed, and the request stays open.
        var powershell = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"$c=[Net.Sockets.TcpClient]::new('127.0.0.1',{GameLanBridge.PortForTest});$s=$c.GetStream();" +
                     $"$b=[Text.Encoding]::ASCII.GetBytes(\"JAYLAN1 {token} data {id}`n\");$s.Write($b,0,$b.Length);" +
                     "$s.ReadTimeout=8000;try{$r=$s.ReadByte()}catch{$r=-2};\"R=$r\"" })
            powershell.ArgumentList.Add(a);
        using var process = Process.Start(powershell)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Contains("R=-1", output);
        Assert.False(connecting.IsCompleted);

        await control.SendAsync(Encoding.ASCII.GetBytes($"fail {id}\n"));
        Assert.Null(await connecting);
    }

    [Fact]
    public async Task AgentPassesConnectionsToItsGame()
    {
        if (!OperatingSystem.IsWindows() || Prepare() is not var (psi, _)) return;
        await RunGameAsync(psi, WindowsSandboxLevel.Standard);
    }

    /// <summary>
    /// The real thing: a game in the AppContainer (where Windows drops loopback connections from outside) is reached
    /// through its agent, whose control connection comes out of the container. Needs the container's loopback
    /// exemption (Jay's admin step), so it is skipped on machines without it (CI runners).
    /// </summary>
    [Fact]
    public async Task AgentReachesAGameInTheStrongSandbox()
    {
        if (!OperatingSystem.IsWindows() || !WindowsSandbox.LoopbackExempt() || Prepare() is not var (psi, javaHome)) return;
        WindowsSandbox.Prepare(new SandboxGrants { ReadOnly = [javaHome, _root] }, WindowsSandboxLevel.Strong);
        await RunGameAsync(psi, WindowsSandboxLevel.Strong);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A launch whose process is <paramref name="process"/> (this test process unless given); returns its token.</summary>
    private string Launch(Process? process = null)
    {
        var argument = GameLanBridge.AgentArgument(Path.Combine(_root, "jay-lan.jar"))!;
        List<string> args = [argument];
        _launches.Add(args);
        GameLanBridge.Started(args, process ?? Process.GetCurrentProcess());
        return argument[(argument.IndexOf('=') + 1)..].Split(':')[1];
    }

    /// <summary>A process that is not this one and waits (killed when the test ends).</summary>
    private Process OtherProcess()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-n", "60", "127.0.0.1" }) psi.ArgumentList.Add(a);
        var process = Process.Start(psi)!;
        _cleanup.Add(new Killer(process));
        return process;
    }

    private sealed class Killer(Process process) : IDisposable
    {
        public void Dispose()
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    private static async Task<Socket> DialAsync(string firstLine)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, GameLanBridge.PortForTest));
        await socket.SendAsync(Encoding.ASCII.GetBytes(firstLine + "\n"));
        return socket;
    }

    /// <summary>One line, or null when the other side closed (or nothing came in time).</summary>
    private static async Task<string?> ReadLineAsync(Socket socket, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var line = new StringBuilder();
        var one = new byte[1];
        try
        {
            while (true)
            {
                if (await socket.ReceiveAsync(one, SocketFlags.None, cts.Token) == 0) return null;
                if (one[0] == '\n') return line.ToString();
                line.Append((char)one[0]);
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public Listener() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<Socket> AcceptAsync() => _listener.AcceptSocketAsync();

        public void Dispose() => _listener.Stop();
    }

    private async Task RunGameAsync(ProcessStartInfo psi, WindowsSandboxLevel level)
    {
        using var process = level == WindowsSandboxLevel.Standard ? Process.Start(psi)! : WindowsSandbox.Start(psi, level);
        GameLanBridge.Started(psi.ArgumentList, process);
        var output = new StringBuilder();
        var portSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (e.Data.StartsWith("PORT ", StringComparison.Ordinal)) portSeen.TrySetResult(int.Parse(e.Data[5..]));
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var port = await portSeen.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!GameLanBridge.IsBridged(port) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.True(GameLanBridge.IsBridged(port), "the agent never connected: " + output);

            var before = GameLanBridge.ViaAgentCount;
            for (var i = 0; i < 2; i++)
            {
                using var socket = await GameLanBridge.ConnectAsync(port, TimeSpan.FromSeconds(10));
                Assert.True(socket is not null, "no connection through the agent: " + output);
                using var stream = new NetworkStream(socket, ownsSocket: false);
                stream.ReadTimeout = 10000;
                await stream.WriteAsync("hello"u8.ToArray());
                var echo = new byte[5];
                await stream.ReadExactlyAsync(echo);
                Assert.Equal("HELLO", Encoding.ASCII.GetString(echo));
            }
            Assert.Equal(before + 2, GameLanBridge.ViaAgentCount);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
        }
    }

    /// <summary>A tiny "game": opens a LAN port like Minecraft (all addresses) and answers in upper case. Null without Java 11+ or the jar.</summary>
    private (ProcessStartInfo Psi, string JavaHome)? Prepare()
    {
        if (FindJava() is not { } javaHome) return null;
        if (WindowsSandbox.LanBridgeJar(_root) is not { } jar || GameLanBridge.AgentArgument(jar) is not { } agent) return null;
        _launches.Add([agent]);
        var source = Path.Combine(_root, "Game.java");
        File.WriteAllText(source, """
            import java.io.*;
            import java.net.*;

            public class Game {
                public static void main(String[] args) throws Exception {
                    ServerSocket server = new ServerSocket(0, 50, InetAddress.getByName("0.0.0.0"));
                    System.out.println("PORT " + server.getLocalPort());
                    System.out.flush();
                    while (true) {
                        final Socket client = server.accept();
                        new Thread(() -> {
                            try {
                                InputStream in = client.getInputStream();
                                OutputStream out = client.getOutputStream();
                                int b;
                                while ((b = in.read()) >= 0) { out.write(Character.toUpperCase(b)); out.flush(); }
                            } catch (IOException e) {
                            } finally {
                                try { client.close(); } catch (IOException e) { }
                            }
                        }).start();
                    }
                }
            }
            """);
        var psi = new ProcessStartInfo(Path.Combine(javaHome, "bin", "java.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _root,
        };
        psi.ArgumentList.Add(agent);
        psi.ArgumentList.Add(source);
        return (psi, javaHome);
    }

    private static string? FindJava()
    {
        foreach (var name in new[] { "JAVA_HOME_21_X64", "JAVA_HOME_25_X64", "JAVA_HOME_17_X64", "JAVA_HOME" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } home && Usable(Path.TrimEndingDirectorySeparator(home)))
                return Path.TrimEndingDirectorySeparator(home);
        // A PATH entry is the JDK's bin folder, with or without a trailing separator or quotes.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Trim('"') is { Length: > 0 } bin && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bin)) is { } home && Usable(home))
                return home;
        return null;

        static bool Usable(string home) =>
            File.Exists(Path.Combine(home, "bin", "java.exe")) && WindowsSandbox.JavaMajor(Path.Combine(home, "bin", "java.exe")) >= 11;
    }
}
