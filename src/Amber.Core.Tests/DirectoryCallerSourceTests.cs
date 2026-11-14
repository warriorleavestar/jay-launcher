using Amber.Core.Localization;
using System.Net;
using System.Net.Sockets;
using Amber.Core.Multiplayer.Public;

namespace Amber.Core.Tests;

/// <summary>
/// Callers handed over from 127.235.c.d count as their room-network address 10.235.c.d; every other loopback address
/// shares one source, so no caller goes uncounted.
/// </summary>
public sealed class DirectoryCallerSourceTests
{
    [Theory]
    [InlineData("127.235.4.7", "10.235.4.7")]
    [InlineData("::ffff:127.235.4.7", "10.235.4.7")]
    [InlineData("127.235.255.254", "10.235.255.254")]
    [InlineData("127.235.0.0", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.235.0.1", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.235.255.255", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.0.0.1", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("::1", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.0.1.1", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.36.4.7", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.1.2.3", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("127.255.255.254", RoomDirectoryServer.SharedLoopbackSource)]
    [InlineData("10.1.2.3", "10.1.2.3")]
    public void SourceOf(string address, string? expected) =>
        Assert.Equal(expected, RoomDirectoryServer.Source(IPAddress.Parse(address)));

    [Fact]
    public async Task WarnsWhenNoCallerIsEverMapped()
    {
        var log = new List<string>();
        await using var server = new RoomDirectoryServer(IPAddress.Loopback, 0, line => { lock (log) log.Add(line); });
        for (var i = 0; i < RoomDirectoryServer.UnmappedCallersBeforeWarning + 5; i++)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.Port);
        }
        var warning = L.T("core.mp.public.callers_unmapped");
        for (var i = 0; i < 100 && !Contains(); i++) await Task.Delay(50);
        lock (log) Assert.Single(log, l => l == warning);
        bool Contains() { lock (log) return log.Contains(warning); }
    }

    [Fact]
    public async Task LocalCallersShareAWiderCappedLimitUntilAMappedCallerIsSeen()
    {
        // Without the loopback-source patch every caller arrives from 127.0.0.1 and can't be told apart. They share a
        // wider allowance than one caller's, still capped below the directory's own, so one of them can't take every place.
        await using var server = new RoomDirectoryServer(IPAddress.Loopback, 0);
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < RoomDirectoryServer.MaxConnectionsUnmapped + 4; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                held.Add(c);
            }
            for (var i = 0; i < 200 && server.OpenFrom(RoomDirectoryServer.UnmappedLoopbackSource) < RoomDirectoryServer.MaxConnectionsUnmapped; i++)
                await Task.Delay(20);
            await Task.Delay(200);
            Assert.False(server.MappingSeen);
            Assert.Equal(0, server.OpenFrom(RoomDirectoryServer.SharedLoopbackSource));
            Assert.Equal(RoomDirectoryServer.MaxConnectionsUnmapped, server.OpenFrom(RoomDirectoryServer.UnmappedLoopbackSource));
            Assert.True(RoomDirectoryServer.MaxConnectionsUnmapped < RoomDirectoryServer.MaxConcurrent);
        }
        finally
        {
            foreach (var c in held) c.Dispose();
        }
    }

    [NotOnMacFact]
    public async Task LocalCallersShareOneLimitOnceAMappedCallerIsSeen()
    {
        await using var server = new RoomDirectoryServer(IPAddress.Loopback, 0);
        var held = new List<TcpClient>();
        try
        {
            var mapped = new TcpClient(new IPEndPoint(IPAddress.Parse("127.235.4.9"), 0));
            await mapped.ConnectAsync(IPAddress.Loopback, server.Port);
            held.Add(mapped);
            for (var i = 0; i < 200 && !server.MappingSeen; i++) await Task.Delay(20);
            Assert.True(server.MappingSeen);
            for (var i = 0; i < RoomDirectoryServer.MaxConnectionsShared + 4; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                held.Add(c);
            }
            for (var i = 0; i < 200 && server.OpenFrom(RoomDirectoryServer.SharedLoopbackSource) < RoomDirectoryServer.MaxConnectionsShared; i++)
                await Task.Delay(20);
            await Task.Delay(200);
            Assert.Equal(RoomDirectoryServer.MaxConnectionsShared, server.OpenFrom(RoomDirectoryServer.SharedLoopbackSource));
        }
        finally
        {
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public void NoAddressIsOnlyForCallsInProcess() => Assert.Null(RoomDirectoryServer.Source(null));

    [NotOnMacFact]
    public async Task OneSourceMayOnlyHoldAFewConnections()
    {
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
            for (var i = 0; i < 200 && server.OpenFrom("10.235.4.7") < RoomDirectoryServer.MaxConnectionsPerSource; i++) await Task.Delay(50);
            Assert.Equal(RoomDirectoryServer.MaxConnectionsPerSource, server.OpenFrom("10.235.4.7"));
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
        // Closed connections give their places back (each waits out the 5 s handshake deadline at most).
        for (var i = 0; i < 300 && server.OpenFrom("10.235.4.7") + server.OpenFrom("10.235.4.8") > 0; i++) await Task.Delay(50);
        Assert.Equal(0, server.OpenFrom("10.235.4.7"));
        Assert.Equal(0, server.OpenFrom("10.235.4.8"));
    }
}

/// <summary>Skipped on macOS: binding to 127.x.y.z other than 127.0.0.1 needs the whole 127/8 on loopback (Linux, Windows).</summary>
public sealed class NotOnMacFactAttribute : FactAttribute
{
    public NotOnMacFactAttribute()
    {
        if (OperatingSystem.IsMacOS()) Skip = "macOS has only 127.0.0.1 on loopback";
    }
}
