using Amber.Core.Multiplayer;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Amber.Core.Tests;

/// <summary>
/// NAT-PMP against a fake gateway on a loopback UDP socket: the whole request flow (address, then mapping), with stale
/// replies, datagrams from another sender, a mapping for another internal port, a lost request and cancellation.
/// </summary>
public sealed class NatPmpGatewayTests
{
    private const int LocalPort = 40123;
    private const int ExternalPort = 40555;
    private static readonly IPAddress External = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Forged = IPAddress.Parse("198.51.100.9");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task StaleRepliesAndOtherSendersArePassedOverAndTheMatchingReplyIsTaken()
    {
        using var gateway = new FakeGateway(async request =>
        {
            if (request.Opcode == 0)
            {
                // Another sender on the network answers first; then a late answer to an earlier mapping request.
                await request.FromStranger(AddressReply(Forged));
                await request.FromGateway(MapReply(51000, 51001));
                await request.FromGateway(AddressReply(External));
            }
            else
            {
                await request.FromStranger(MapReply(LocalPort, 1111));
                await request.FromGateway(AddressReply(Forged));
                await request.FromGateway(MapReply(LocalPort, ExternalPort));
            }
        });
        var result = await PortMapping.MapNatPmpAsync(LocalPort, gateway.EndPoint, _ => { }, CancellationToken.None).WaitAsync(Deadline);
        Assert.NotNull(result);
        Assert.Equal("NAT-PMP", result.Method);
        Assert.Equal(new IPEndPoint(External, ExternalPort), result.External);
    }

    [Fact]
    public async Task AMappingForAnotherInternalPortIsRejected()
    {
        var logged = new List<string>();
        using var gateway = new FakeGateway(request => request.Opcode == 0
            ? request.FromGateway(AddressReply(External))
            : request.FromGateway(MapReply(LocalPort + 1, ExternalPort)));
        var result = await PortMapping.MapNatPmpAsync(LocalPort, gateway.EndPoint, logged.Add, CancellationToken.None).WaitAsync(Deadline);
        Assert.Null(result);
        Assert.DoesNotContain(logged, line => line.Contains(ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    /// <summary>Only noise arrives for the mapping request: it is sent twice (the retry) and then given up.</summary>
    [Fact]
    public async Task WithoutAMatchingReplyTheRequestTimesOutAfterOneRetry()
    {
        using var gateway = new FakeGateway(async request =>
        {
            if (request.Opcode == 0)
            {
                await request.FromGateway(AddressReply(External));
                return;
            }
            await request.FromStranger(MapReply(LocalPort, 1111));
            await request.FromGateway(AddressReply(Forged));
        });
        var result = await PortMapping.MapNatPmpAsync(LocalPort, gateway.EndPoint, _ => { }, CancellationToken.None).WaitAsync(Deadline);
        Assert.Null(result);
        await gateway.WaitForAsync(1, 2);
        Assert.Equal(1, gateway.Count(0));
        Assert.Equal(2, gateway.Count(1));
    }

    /// <summary>The first mapping request is lost (only another sender answers it); the retry gets the gateway's answer.</summary>
    [Fact]
    public async Task ALostRequestIsRetriedAndTheRetryIsAnswered()
    {
        using var gateway = new FakeGateway(async request =>
        {
            if (request.Opcode == 0) await request.FromGateway(AddressReply(External));
            else if (request.Nth == 1) await request.FromStranger(MapReply(LocalPort, 1111));
            else await request.FromGateway(MapReply(LocalPort, ExternalPort));
        });
        var result = await PortMapping.MapNatPmpAsync(LocalPort, gateway.EndPoint, _ => { }, CancellationToken.None).WaitAsync(Deadline);
        Assert.NotNull(result);
        Assert.Equal(new IPEndPoint(External, ExternalPort), result.External);
        Assert.Equal(2, gateway.Count(1));
    }

    /// <summary>
    /// Only another sender answers the address request; the session is cancelled while the retry waits. The call ends with
    /// the cancellation and never gets as far as asking for a mapping.
    /// </summary>
    [Fact]
    public async Task CancellingWhileTheRetryWaitsStopsTheRequest()
    {
        using var cancel = new CancellationTokenSource();
        using var gateway = new FakeGateway(async request =>
        {
            if (request.Opcode == 0 && request.Nth == 1) await request.FromStranger(AddressReply(Forged));
            else cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PortMapping.MapNatPmpAsync(LocalPort, gateway.EndPoint, _ => { }, cancel.Token).WaitAsync(Deadline));
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(2, gateway.Count(0));
        Assert.Equal(0, gateway.Count(1));
    }

    private static byte[] AddressReply(IPAddress external)
    {
        var reply = new byte[12];
        reply[1] = 128;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 1000);
        external.GetAddressBytes().CopyTo(reply, 8);
        return reply;
    }

    private static byte[] MapReply(int internalPort, int externalPort)
    {
        var reply = new byte[16];
        reply[1] = 129;
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), 1000);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), 7200);
        return reply;
    }

    private sealed record Request(byte Opcode, int Nth, IPEndPoint Client, UdpClient Gateway, UdpClient Stranger)
    {
        public Task FromGateway(byte[] data) => Gateway.SendAsync(data, Client).AsTask();
        public Task FromStranger(byte[] data) => Stranger.SendAsync(data, Client).AsTask();
    }

    /// <summary>A gateway on a loopback port, plus a second socket that plays another sender on the network.</summary>
    private sealed class FakeGateway : IDisposable
    {
        private readonly UdpClient _gateway = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _stranger = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<byte, int> _counts = [];
        private readonly Func<Request, Task> _answer;
        private readonly Task _loop;

        public FakeGateway(Func<Request, Task> answer)
        {
            _answer = answer;
            _loop = Task.Run(LoopAsync);
        }

        public IPEndPoint EndPoint => (IPEndPoint)_gateway.Client.LocalEndPoint!;

        public int Count(byte opcode)
        {
            lock (_counts) return _counts.GetValueOrDefault(opcode);
        }

        public async Task WaitForAsync(byte opcode, int count)
        {
            using var limit = new CancellationTokenSource(Deadline);
            while (Count(opcode) < count) await Task.Delay(20, limit.Token);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _gateway.ReceiveAsync(_stop.Token); }
                catch (SocketException) { continue; }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
                if (r.Buffer.Length < 2 || r.Buffer[0] != 0) continue;
                int nth;
                lock (_counts) _counts[r.Buffer[1]] = nth = _counts.GetValueOrDefault(r.Buffer[1]) + 1;
                try { await _answer(new Request(r.Buffer[1], nth, r.RemoteEndPoint, _gateway, _stranger)); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _gateway.Dispose();
            _stranger.Dispose();
            _loop.Wait(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
