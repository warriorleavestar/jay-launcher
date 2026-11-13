using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Launch;

namespace Amber.Core.Tests;

/// <summary>
/// The LAN bridge agent (tools/jay-lan) on its own, in a plain JVM, against a stand-in for Jay: it talks to Jay only once,
/// and passes on a bounded number of players. Runs wherever Java 11+ is found (JAVA_HOME or PATH).
/// </summary>
public sealed class LanBridgeAgentTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-lan-agent-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _cleanup = [];

    public LanBridgeAgentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) d.Dispose();
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OnceJayIsGoneTheAgentNeverTalksToItsPortAgain()
    {
        var jay = Listen(0);
        var port = ((IPEndPoint)jay.LocalEndpoint).Port;
        if (Start(port) is not { } game) return;
        using var control = await jay.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal($"JAYLAN1 {Token} control", await ReadLineAsync(control));
        await control.SendAsync("ok\n"u8.ToArray());

        // Jay closes; another program takes the port. The agent must not come back to it: it would hand over the token
        // and take "open" requests from whoever listens there now.
        control.Dispose();
        jay.Stop();
        var taker = Listen(port);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await taker.AcceptSocketAsync(wait.Token));
        Assert.False(game.HasExited);
        Assert.Contains("Restart the game", game.Output);
    }

    [Fact]
    public async Task PlayersBeyondTheLimitAreTurnedDownAndSlotsComeBack()
    {
        const int limit = 64; // LanBridge.MAX_ACTIVE
        var jay = Listen(0);
        var gameWorld = Listen(0);
        var worldPort = ((IPEndPoint)gameWorld.LocalEndpoint).Port;
        if (Start(((IPEndPoint)jay.LocalEndpoint).Port) is null) return;
        using var control = await jay.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal($"JAYLAN1 {Token} control", await ReadLineAsync(control));
        await control.SendAsync("ok\n"u8.ToArray());

        var data = new List<Socket>();
        var players = new List<Socket>();
        var requests = new StringBuilder();
        for (var i = 1; i <= limit + 6; i++) requests.Append($"open {i} {worldPort}\n");
        await control.SendAsync(Encoding.ASCII.GetBytes(requests.ToString()));

        // Every request is either passed on (a data connection to Jay) or turned down; never more than the limit at once.
        var fails = 0;
        var failReader = Task.Run(async () =>
        {
            while (await ReadLineAsync(control, TimeSpan.FromSeconds(20)) is { } line)
                if (line.StartsWith("fail ", StringComparison.Ordinal) && Interlocked.Increment(ref fails) == 6) return;
        });
        for (var i = 0; i < limit; i++)
        {
            var s = await jay.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _cleanup.Add(s);
            data.Add(s);
            Assert.StartsWith($"JAYLAN1 {Token} data ", await ReadLineAsync(s));
        }
        for (var i = 0; i < limit; i++) players.Add(Track(await gameWorld.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(20))));
        await failReader.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(6, Volatile.Read(ref fails));

        // Two players leave: their slots come back and two more get through.
        for (var i = 0; i < 2; i++) data[i].Dispose();
        await Task.Delay(1000);
        await control.SendAsync(Encoding.ASCII.GetBytes($"open 100 {worldPort}\nopen 101 {worldPort}\n"));
        for (var i = 0; i < 2; i++)
        {
            var s = Track(await jay.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.StartsWith($"JAYLAN1 {Token} data 10", await ReadLineAsync(s));
        }
    }

    private Socket Track(Socket s)
    {
        _cleanup.Add(s);
        return s;
    }

    private TcpListener Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (!OperatingSystem.IsWindows()) listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start(256);
        _cleanup.Add(new Stopper(listener));
        return listener;
    }

    private sealed class Stopper(TcpListener listener) : IDisposable
    {
        public void Dispose() => listener.Stop();
    }

    private sealed class Game(Process process) : IDisposable
    {
        private readonly StringBuilder _output = new();

        public bool HasExited => process.HasExited;

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        public void Attach()
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public void Dispose()
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(10000);
            process.Dispose();
        }
    }

    /// <summary>A JVM that only sleeps, with the agent pointed at <paramref name="jayPort"/>. Null without Java 11+ or the jar.</summary>
    private Game? Start(int jayPort)
    {
        if (FindJava() is not { } java || Jar() is not { } jar) return null;
        var source = Path.Combine(_root, "Idle.java");
        File.WriteAllText(source, "public class Idle { public static void main(String[] a) throws Exception { Thread.sleep(600000); } }");
        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = _root,
        };
        psi.ArgumentList.Add($"-javaagent:{jar}={jayPort}:{Token}");
        psi.ArgumentList.Add(source);
        var game = new Game(Process.Start(psi)!);
        _cleanup.Add(game);
        game.Attach();
        return game;
    }

    /// <summary>The agent jar embedded in Amber.Core (null when the build had no JDK).</summary>
    private string? Jar()
    {
        var resource = typeof(WindowsSandbox).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("jay-lan.jar", StringComparison.Ordinal));
        if (resource is null) return null;
        using var stream = typeof(WindowsSandbox).Assembly.GetManifestResourceStream(resource)!;
        var path = Path.Combine(_root, "jay-lan.jar");
        using (var file = File.Create(path)) stream.CopyTo(file);
        return path;
    }

    private static string? FindJava()
    {
        var exe = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var homes = new[] { "JAVA_HOME_21_X64", "JAVA_HOME_25_X64", "JAVA_HOME_17_X64", "JAVA_HOME" }
            .Select(Environment.GetEnvironmentVariable).OfType<string>().Where(h => h.Length > 0)
            .Select(h => Path.Combine(Path.TrimEndingDirectorySeparator(h), "bin"))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => d.Trim('"')).Where(d => d.Length > 0));
        foreach (var bin in homes)
        {
            var path = Path.Combine(bin, exe);
            if (!File.Exists(path)) continue;
            // /usr/bin/java and the like link into the JDK, whose "release" file names the version.
            path = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            if (Major(path) >= 11) return path;
        }
        return null;
    }

    /// <summary>The JDK's major version from its "release" file (0 when unknown).</summary>
    private static int Major(string java)
    {
        var release = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(java)!)!, "release");
        if (!File.Exists(release)) return 0;
        foreach (var line in File.ReadLines(release))
            if (line.StartsWith("JAVA_VERSION=\"", StringComparison.Ordinal))
            {
                var version = line["JAVA_VERSION=\"".Length..].TrimEnd('"').Split('.');
                var major = version[0] == "1" && version.Length > 1 ? version[1] : version[0];
                return int.TryParse(major, out var n) ? n : 0;
            }
        return 0;
    }

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
}
