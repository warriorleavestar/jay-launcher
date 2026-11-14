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
    /// <summary>A one-connection proxy: reads the greeting, answers <paramref name="hello"/>, then (if given) reads the request and answers <paramref name="reply"/>.</summary>
    private static async Task<(int Port, Task<byte[]> Request)> ProxyAsync(byte[] hello, byte[]? reply, bool echo = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                await stream.WriteAsync(hello);
                if (reply is null) return greeting;
                var req = new byte[10];
                await stream.ReadExactlyAsync(req);
                await stream.WriteAsync(reply);
                if (echo)
                {
                    var buffer = new byte[4];
                    await stream.ReadExactlyAsync(buffer);
                    await stream.WriteAsync(buffer);
                }
                return req;
            }
            finally
            {
                listener.Stop();
            }
        });
        await Task.Yield();
        return (port, request);
    }

    private static Task<Stream> Connect(int port) =>
        Socks5Client.ConnectAsync(IPAddress.Loopback, port, "10.144.0.7", 25565, default).WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task SuccessReplyGivesAWorkingStreamAndSendsTheRightRequest()
    {
        var (port, request) = await ProxyAsync([5, 0], [5, 0, 0, 1, 127, 0, 0, 1, 0x30, 0x39], echo: true);
        await using var stream = await Connect(port);
        await stream.WriteAsync("ping"u8.ToArray());
        var back = new byte[4];
        await stream.ReadExactlyAsync(back);
        Assert.Equal("ping"u8.ToArray(), back);
        // CONNECT, IPv4, 10.144.0.7, port 25565 big-endian.
        Assert.Equal(new byte[] { 5, 1, 0, 1, 10, 144, 0, 7, 0x63, 0xDD }, await request);
    }

    [Fact]
    public async Task DomainBoundAddressIsReadToTheEnd()
    {
        // Address type 3: a length byte, the name, then the port. Nothing of it may leak into the game stream.
        var (port, _) = await ProxyAsync([5, 0], [5, 0, 0, 3, 4, (byte)'h', (byte)'o', (byte)'s', (byte)'t', 0, 80], echo: true);
        await using var stream = await Connect(port);
        await stream.WriteAsync("data"u8.ToArray());
        var back = new byte[4];
        await stream.ReadExactlyAsync(back);
        Assert.Equal("data"u8.ToArray(), back);
    }

    [Theory]
    [InlineData(new byte[] { 5, 0xFF })]   // no acceptable authentication method
    [InlineData(new byte[] { 5, 2 })]      // wants username/password, which the client never offered
    [InlineData(new byte[] { 4, 0 })]      // not SOCKS5
    public async Task RefusedOrWrongGreetingThrows(byte[] hello)
    {
        var (port, _) = await ProxyAsync(hello, null);
        await Assert.ThrowsAsync<IOException>(() => Connect(port));
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
        var (port, _) = await ProxyAsync([5, 0], reply);
        await Assert.ThrowsAsync<IOException>(() => Connect(port));
    }

    [Fact]
    public async Task ProxyClosingAfterGreetingThrows()
    {
        var (port, _) = await ProxyAsync([], null);
        await Assert.ThrowsAsync<IOException>(() => Connect(port));
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("fd00::1")]
    public async Task OnlyIPv4TargetsAreAccepted(string target) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Socks5Client.ConnectAsync(IPAddress.Loopback, 1, target, 25565, default));
}
