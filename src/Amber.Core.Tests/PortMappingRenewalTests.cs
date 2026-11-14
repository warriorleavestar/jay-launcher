using System.Buffers.Binary;
using System.Net;
using System.Text;
using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

/// <summary>
/// Router mappings are leased: Jay renews them halfway through the lease while the room is open, and when renewals keep
/// failing until the lease runs out it stops offering the mapped port and the "open" NAT verdict.
/// </summary>
public sealed class PortMappingRenewalTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress LocalAddress = IPAddress.Parse("192.168.1.20");
    private static readonly Uri Location = new("http://192.168.1.1:49152/igd/device.xml");

    private static Task NoWait(TimeSpan _, CancellationToken ct) => ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

    [Fact]
    public async Task RenewsHalfwayThroughTheLeaseWhileItWorks()
    {
        var waits = new List<TimeSpan>();
        var renewals = 0;
        using var stop = new CancellationTokenSource();
        var lost = false;
        var mapping = new PortMappingResult("UPnP", IPEndPoint.Parse("198.51.100.7:40000"), null, _ =>
        {
            if (++renewals == 3) stop.Cancel();
            return Task.FromResult(true);
        }, TimeSpan.FromMinutes(40));
        await DirectSession.KeepMappingAsync(mapping, TimeSpan.FromMinutes(1), (wait, ct) => { waits.Add(wait); return NoWait(wait, ct); },
            _ => { }, () => lost = true, stop.Token);
        Assert.Equal(3, renewals);
        Assert.False(lost);
        Assert.All(waits.Take(3), w => Assert.Equal(TimeSpan.FromMinutes(20), w));
    }

    [Fact]
    public async Task RetriesUntilTheLeaseWouldRunOutThenGivesTheMappingUp()
    {
        var waits = new List<TimeSpan>();
        var renewals = 0;
        var lost = 0;
        var mapping = new PortMappingResult("NAT-PMP", IPEndPoint.Parse("198.51.100.7:40000"), null, _ =>
        {
            renewals++;
            if (renewals == 2) throw new System.Net.Sockets.SocketException();
            return Task.FromResult(false);
        }, TimeSpan.FromMinutes(10));
        await DirectSession.KeepMappingAsync(mapping, TimeSpan.FromMinutes(1), (wait, ct) => { waits.Add(wait); return NoWait(wait, ct); },
            _ => { }, () => lost++, default);
        Assert.Equal(1, lost);
        // Halfway (5 min), then once a minute until no full retry interval of the lease is left.
        Assert.Equal([TimeSpan.FromMinutes(5), .. Enumerable.Repeat(TimeSpan.FromMinutes(1), 4)], waits);
        Assert.Equal(5, renewals);
    }

    [Fact]
    public async Task ARenewalAfterAFailureRestartsTheLease()
    {
        var results = new Queue<bool>([false, true, false, false, false, false, false]);
        var lost = false;
        var renewals = 0;
        var mapping = new PortMappingResult("UPnP", IPEndPoint.Parse("198.51.100.7:40000"), null,
            _ => { renewals++; return Task.FromResult(results.Dequeue()); }, TimeSpan.FromMinutes(4));
        await DirectSession.KeepMappingAsync(mapping, TimeSpan.FromMinutes(1), NoWait, _ => { }, () => lost = true, default);
        Assert.True(lost);
        // 2 min, fail, 1 min, renewed; 2 min, fail, 1 min, fail, then less than a retry interval is left.
        Assert.Equal(4, renewals);
    }

    [Theory]
    [InlineData(3600u, 3600)]
    [InlineData(0u, 7200)]
    [InlineData(5u, 20)]
    [InlineData(999999u, 7200)]
    public void NatPmpLeaseComesFromTheReply(uint granted, int seconds)
    {
        var reply = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), granted);
        Assert.Equal(TimeSpan.FromSeconds(seconds), PortMapping.PmpLease(reply));
    }

    [Fact]
    public async Task UpnpRenewalRepeatsTheSameMappingRequest()
    {
        var actions = new List<(string Action, string Body)>();
        var fail = false;
        var result = await PortMapping.MapUpnpAsync(25565, Gateway, LocalAddress, Location, _ => { }, default, () => new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return Ok(Description);
            var action = request.Headers.GetValues("SOAPAction").Single();
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            lock (actions) actions.Add((action, body));
            if (action.Contains("GetExternalIPAddress")) return Ok("<NewExternalIPAddress>8.8.8.8</NewExternalIPAddress>");
            return fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<errorDescription>busy</errorDescription>") } : Ok("<ok/>");
        }));
        Assert.NotNull(result);
        Assert.Equal(PortMappingResult.RequestedLease, result!.Lease);
        Assert.True(await result.Renew!(default));
        var adds = actions.Where(a => a.Action.Contains("AddPortMapping")).ToList();
        Assert.Equal(2, adds.Count);
        Assert.Equal(adds[0].Body, adds[1].Body);
        Assert.Contains("<NewLeaseDuration>7200</NewLeaseDuration>", adds[1].Body);
        fail = true;
        Assert.False(await result.Renew(default));
    }

    [Fact]
    public async Task HostStopsOfferingAMappingItCouldNotRenew()
    {
        var renewals = 0;
        var mapped = new Candidate(PathKind.Mapped, IPEndPoint.Parse("198.51.100.7:40000"));
        await using var host = new DirectSession(null!, Path.Combine(Path.GetTempPath(), "amber-mapping-" + Guid.NewGuid().ToString("N")),
            new RoomCode("K7Q2M9XD", "4HZP8RW2N6TC", 25565), true, new DirectOptions([], true, [], false, false, false), new DiagnosticLog())
        {
            BridgeKey = DirectTestKeys.Bridge,
            StartForTest = (o, _) => Task.FromResult<IEasyTierSession>(new FakeEasyTier(o)),
            SpeedTestPortForTest = 0,
            MappingRetryInterval = TimeSpan.FromMilliseconds(100),
            GatherForTest = _ => Task.FromResult(new DirectGatherer.Gathered(
                [new(PathKind.Lan, IPEndPoint.Parse("192.168.1.20:40000")), mapped], NatKind.Open, "",
                new PortMappingResult("UPnP", mapped.EndPoint, null, _ => { Interlocked.Increment(ref renewals); return Task.FromResult(false); },
                    TimeSpan.FromMilliseconds(400)), NatKind.Symmetric)),
        };
        var offer = await host.PrepareAsync();
        Assert.Contains(mapped, offer.Candidates);
        Assert.Equal(NatKind.Open, offer.Nat);
        Assert.True(host.Bridge!.OwnsMappedPort);
        for (var i = 0; i < 100 && host.Offer()!.Candidates.Contains(mapped); i++) await Task.Delay(50);
        var later = host.Offer()!;
        Assert.DoesNotContain(mapped, later.Candidates);
        Assert.Equal(NatKind.Symmetric, later.Nat);
        Assert.Equal(offer.Session, later.Session);
        Assert.False(host.Bridge.OwnsMappedPort);
        Assert.True(Volatile.Read(ref renewals) >= 2);
    }

    private sealed class FakeEasyTier(EasyTierOptions options) : IEasyTierSession
    {
        public EasyTierOptions Options { get; } = options;
        public bool HasExited { get; private set; }
        public IReadOnlyList<string> RecentOutput => [];
        public Task<List<PeerInfo>> PeersAsync(CancellationToken ct = default) => Task.FromResult(new List<PeerInfo>());
        public void Dispose() => HasExited = true;
    }

    private const string Description =
        "<root xmlns='urn:schemas-upnp-org:device-1-0'><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/control</controlURL></service></root>";

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
