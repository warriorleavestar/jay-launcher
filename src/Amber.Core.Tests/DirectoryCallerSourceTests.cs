using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>Callers handed over from their own loopback address (127.b.c.d) get per-source limits; 127.0.0.1 does not.</summary>
public sealed class DirectoryCallerSourceTests
{
    [Theory]
    [InlineData("127.0.0.1", null)]
    [InlineData("::1", null)]
    [InlineData("127.235.4.7", "127.235.4.7")]
    [InlineData("::ffff:127.235.4.7", "127.235.4.7")]
    [InlineData("203.0.113.9", "203.0.113.9")]
    public void SourceOf(string address, string? expected) =>
        Assert.Equal(expected, RoomDirectoryServer.Source(IPAddress.Parse(address)));

    [Fact]
    public async Task OneSourceMayOnlyHoldAFewConnections()
    {
        // Binding to 127.x.y.z other than 127.0.0.1 needs the whole 127/8 on loopback (Linux, Windows).
        if (OperatingSystem.IsMacOS()) return;
        await using var server = new RoomDirectoryServer(IPAddress.Loopback, 0);
        var held = new List<TcpClient>();
        try
        {
            async Task<TcpClient> From(string address)
            {
                var c = new TcpClient(new IPEndPoint(IPAddress.Parse(address), 0));
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                held.Add(c);
                return c;
            }
            async Task<bool> ClosedAsync(TcpClient c, int ms)
            {
                using var timeout = new CancellationTokenSource(ms);
                try { return await c.GetStream().ReadAsync(new byte[1], timeout.Token) == 0; }
                catch (OperationCanceledException) { return false; }
                catch (Exception e) when (e is IOException or SocketException) { return true; }
            }
            for (var i = 0; i < RoomDirectoryServer.MaxConnectionsPerSource; i++) await From("127.235.4.7");
            await Task.Delay(200);
            var extra = await From("127.235.4.7");
            Assert.True(await ClosedAsync(extra, 3000));
            // Another caller is not affected.
            var other = await From("127.235.4.8");
            Assert.False(await ClosedAsync(other, 500));
        }
        finally
        {
            foreach (var c in held) c.Dispose();
        }
    }
}
