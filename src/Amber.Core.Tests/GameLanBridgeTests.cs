using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Rooms reaching a game in the Windows strong sandbox: the game's LAN bridge agent (tools/jay-lan) connects out to Jay,
/// and Jay sends a connection through it only when the port belongs to that game.
/// </summary>
public sealed class GameLanBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jay-lan-bridge-" + Guid.NewGuid().ToString("N"));

    public GameLanBridgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void FirstLineNeedsTheSecret()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        Assert.Equal((true, (long?)null), GameLanBridge.Parse($"JAYLAN1 {secret} control", secret));
        Assert.Equal((false, (long?)42), GameLanBridge.Parse($"JAYLAN1 {secret} data 42", secret));
        Assert.Null(GameLanBridge.Parse("JAYLAN1 0123456789abcdef0123456789abcdee control", secret));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {secret} data -1", secret));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {secret} data", secret));
        Assert.Null(GameLanBridge.Parse($"JAYLAN1 {secret} open 1 25565", secret));
        Assert.Null(GameLanBridge.Parse($"JAYLAN2 {secret} control", secret));
        Assert.Null(GameLanBridge.Parse("JAYLAN1  control", ""));
    }

    [Fact]
    public void PortOwnersNameThisProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Contains(Environment.ProcessId, LocalPortOwners.ListenerPids(port));
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            Assert.Equal(Environment.ProcessId, LocalPortOwners.ConnectionPid((IPEndPoint)client.Client.LocalEndPoint!, (IPEndPoint)client.Client.RemoteEndPoint!));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task PortsOfOtherProcessesAreReachedDirectly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(GameLanBridge.HasAgentFor(port));
            using var socket = await GameLanBridge.ConnectAsync(port, TimeSpan.FromSeconds(5));
            Assert.NotNull(socket);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AgentPassesConnectionsToItsGame()
    {
        if (!OperatingSystem.IsWindows() || Prepare() is not var (psi, _)) return;
        await RunGameAsync(psi, WindowsSandboxLevel.Standard);
    }

    /// <summary>
    /// The real thing: a game in the AppContainer (where Windows drops loopback connections from outside) is reached
    /// through its agent, whose control connection comes out of the container. Needs the
    /// container's loopback exemption (Jay's admin step), so it is skipped on machines without it (CI runners).
    /// </summary>
    [Fact]
    public async Task AgentReachesAGameInTheStrongSandbox()
    {
        if (!OperatingSystem.IsWindows() || !WindowsSandbox.LoopbackExempt() || Prepare() is not var (psi, javaHome)) return;
        WindowsSandbox.Prepare(new SandboxGrants { ReadOnly = [javaHome, _root] }, WindowsSandboxLevel.Strong);
        await RunGameAsync(psi, WindowsSandboxLevel.Strong);
    }

    private async Task RunGameAsync(ProcessStartInfo psi, WindowsSandboxLevel level)
    {
        using var process = level == WindowsSandboxLevel.Standard ? Process.Start(psi)! : WindowsSandbox.Start(psi, level);
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
            while (!GameLanBridge.HasAgentFor(port) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.True(GameLanBridge.HasAgentFor(port), "the agent never connected: " + output);

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
        if (WindowsSandbox.LanBridgeJar(_root) is not { } jar) return null;
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
        psi.ArgumentList.Add(GameLanBridge.AgentArgument(jar));
        psi.ArgumentList.Add(source);
        return (psi, javaHome);
    }

    private static string? FindJava()
    {
        foreach (var name in new[] { "JAVA_HOME_21_X64", "JAVA_HOME_25_X64", "JAVA_HOME_17_X64", "JAVA_HOME" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } home && File.Exists(Path.Combine(home, "bin", "java.exe"))
                && WindowsSandbox.JavaMajor(Path.Combine(home, "bin", "java.exe")) is 0 or >= 11)
                return home;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "java.exe")) && Path.GetDirectoryName(dir) is { } home
                && WindowsSandbox.JavaMajor(Path.Combine(dir, "java.exe")) >= 11)
                return home;
        return null;
    }
}
