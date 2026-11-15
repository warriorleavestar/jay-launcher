using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>
/// Socks5Client (the public network's way into EasyTier's SOCKS5 port) against a scripted loopback proxy:
/// it only hands back a stream after a well-formed success reply, and turns every refusal or broken reply into an IOException.
/// </summary>
public class Socks5ClientTests
{
    /// <summary>How long any step of a test may take; a regression fails the test instead of hanging it.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A one-connection proxy: reads the greeting, answers <c>hello</c>, then (if given) reads the request and answers
    /// <c>reply</c>. Every step has a deadline, and disposing it stops the listener and waits for the proxy to finish,
    /// also when the client never connected.
    /// </summary>
    private sealed class Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new(Limit);

        public Proxy(byte[] hello, byte[]? reply, bool echo)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Request = Task.Run(async () =>
            {
                var ct = _cts.Token;
                using var client = await _listener.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting, ct);
                await stream.WriteAsync(hello, ct);
                if (reply is null) return greeting;
                var req = new byte[10];
                await stream.ReadExactlyAsync(req, ct);
                await stream.WriteAsync(reply, ct);
                if (echo)
                {
                    var buffer = new byte[4];
                    await stream.ReadExactlyAsync(buffer, ct);
                    await stream.WriteAsync(buffer, ct);
                }
                return req;
            });
        }

        public int Port { get; }

        /// <summary>The request the client sent (or its greeting, when no reply is scripted).</summary>
        public Task<byte[]> Request { get; }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await Request; } catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            _cts.Dispose();
        }
    }

    /// <summary>Connects with the test's deadline as the client's own token, so a stuck handshake is cancelled, not left behind.</summary>
    private static async Task<Stream> Connect(int port)
    {
        using var cts = new CancellationTokenSource(Limit);
        return await Socks5Client.ConnectAsync(IPAddress.Loopback, port, "10.144.0.7", 25565, cts.Token);
    }

    [Fact]
    public async Task SuccessReplyGivesAWorkingStreamAndSendsTheRightRequest()
    {
        await using var proxy = new Proxy([5, 0], [5, 0, 0, 1, 127, 0, 0, 1, 0x30, 0x39], echo: true);
        await using var stream = await Connect(proxy.Port);
        using var cts = new CancellationTokenSource(Limit);
        await stream.WriteAsync("ping"u8.ToArray(), cts.Token);
        var back = new byte[4];
        await stream.ReadExactlyAsync(back, cts.Token);
        Assert.Equal("ping"u8.ToArray(), back);
        // CONNECT, IPv4, 10.144.0.7, port 25565 big-endian.
        Assert.Equal(new byte[] { 5, 1, 0, 1, 10, 144, 0, 7, 0x63, 0xDD }, await proxy.Request.WaitAsync(Limit));
    }

    [Fact]
    public async Task DomainBoundAddressIsReadToTheEnd()
    {
        // Address type 3: a length byte, the name, then the port. Nothing of it may leak into the game stream.
        await using var proxy = new Proxy([5, 0], [5, 0, 0, 3, 4, (byte)'h', (byte)'o', (byte)'s', (byte)'t', 0, 80], echo: true);
        await using var stream = await Connect(proxy.Port);
        using var cts = new CancellationTokenSource(Limit);
        await stream.WriteAsync("data"u8.ToArray(), cts.Token);
        var back = new byte[4];
        await stream.ReadExactlyAsync(back, cts.Token);
        Assert.Equal("data"u8.ToArray(), back);
    }

    [Theory]
    [InlineData(new byte[] { 5, 0xFF })]   // no acceptable authentication method
    [InlineData(new byte[] { 5, 2 })]      // wants username/password, which the client never offered
    [InlineData(new byte[] { 4, 0 })]      // not SOCKS5
    public async Task RefusedOrWrongGreetingThrows(byte[] hello)
    {
        await using var proxy = new Proxy(hello, null, echo: false);
        await Assert.ThrowsAsync<IOException>(() => Connect(proxy.Port));
    }

    [Theory]
    [InlineData(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 })]   // connection refused
    [InlineData(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 })]   // general failure
    [InlineData(new byte[] { 4, 0, 0, 1, 0, 0, 0, 0, 0, 0 })]   // wrong version in the reply
    [InlineData(new byte[] { 5, 0, 0, 9, 0, 0, 0, 0, 0, 0 })]   // unknown address type
    [InlineData(new byte[] { 5, 0, 0, 1, 127 })]                // cut off in the bound address
    [InlineData(new byte[] { 5, 0 })]                           // cut off in the reply header
    public async Task FailedOrMalformedReplyThrows(byte[] reply)
    {
        await using var proxy = new Proxy([5, 0], reply, echo: false);
        await Assert.ThrowsAsync<IOException>(() => Connect(proxy.Port));
    }

    [Fact]
    public async Task ProxyClosingAfterGreetingThrows()
    {
        await using var proxy = new Proxy([], null, echo: false);
        await Assert.ThrowsAsync<IOException>(() => Connect(proxy.Port));
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("fd00::1")]
    public async Task OnlyIPv4TargetsAreAccepted(string target) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Socks5Client.ConnectAsync(IPAddress.Loopback, 1, target, 25565, default));
}
