using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Places for connections in their handshake: who is closed to make room.</summary>
public sealed class HandshakeSlotsTests
{
    private TimeSpan _now = TimeSpan.FromHours(1);
    private HandshakeSlots New(int max, int perSource) => new(max, perSource, clock: () => _now);
    private void PastGrace() => _now += HandshakeSlots.DefaultGrace + TimeSpan.FromMilliseconds(1);

    private static (HandshakeSlots.Slot Slot, Func<bool> Closed) Take(HandshakeSlots slots, string? source)
    {
        var closed = false;
        var slot = slots.TryTake(source, () => closed = true, out _)!;
        return (slot, () => closed);
    }

    [Fact]
    public void FullPlacesCloseTheOldestOfTheBusiestCaller()
    {
        var slots = New(4, 4);
        var a1 = Take(slots, "10.1.0.1");
        var b1 = Take(slots, "10.1.0.2");
        var a2 = Take(slots, "10.1.0.1");
        var a3 = Take(slots, "10.1.0.1");
        PastGrace();
        // Full: the newcomer pushes out the busiest caller's oldest, not the oldest overall.
        var c1 = Take(slots, "10.1.0.3");
        Assert.True(a1.Closed());
        Assert.False(b1.Closed());
        Assert.False(a2.Closed() || a3.Closed() || c1.Closed());
        Assert.Equal(1, slots.Evicted);
        Assert.Equal(5, slots.Held);
        slots.Release(a1.Slot);
        Assert.Equal(4, slots.Held);
    }

    [Fact]
    public void OneCallerOnlyPushesOutItsOwn()
    {
        var slots = New(8, 2);
        var other = Take(slots, "10.1.0.9");
        var a1 = Take(slots, "10.1.0.1");
        var a2 = Take(slots, "10.1.0.1");
        var a3 = Take(slots, "10.1.0.1");
        Assert.True(a1.Closed());
        Assert.False(a2.Closed() || a3.Closed() || other.Closed());
    }

    [Fact]
    public void CallersThatCantBeToldApartShareOneGroupWithoutTheCallerLimit()
    {
        var slots = New(4, 1);
        var loop = Enumerable.Range(0, 3).Select(_ => Take(slots, null)).ToList();
        Assert.All(loop, l => Assert.False(l.Closed()));
        var known = Take(slots, "10.1.0.1");
        PastGrace();
        // Full now: the loopback group is the busiest.
        Take(slots, "10.1.0.2");
        Assert.True(loop[0].Closed());
        Assert.False(known.Closed());
    }

    [Fact]
    public void ReleasedPlacesAreNeverClosedAndTooManyClosingRefuses()
    {
        var slots = New(1, 1);
        var first = Take(slots, null);
        slots.Release(first.Slot);
        slots.Release(first.Slot);
        Assert.Equal(0, slots.Held);
        var second = Take(slots, null);
        Assert.False(first.Closed());
        // The second is told to close for a third, but hasn't ended yet: twice the places held, so a fourth is refused.
        Take(slots, null);
        Assert.True(second.Closed());
        Assert.Null(slots.TryTake(null, () => { }, out _));
    }

    [Fact]
    public void TiesCloseTheOldestNeverTheNewest()
    {
        // Every caller holds one place: each newcomer must push out the oldest, so a member who just arrived survives.
        var slots = New(8, 4);
        var stalled = Enumerable.Range(0, 8).Select(i => Take(slots, $"10.2.0.{i}")).ToList();
        var member = Take(slots, "10.3.0.1");
        Assert.True(stalled[0].Closed());
        for (var i = 0; i < 5; i++) Take(slots, $"10.4.0.{i}");
        Assert.False(member.Closed());
        Assert.All(stalled.Take(6), s => Assert.True(s.Closed()));
        Assert.All(stalled.Skip(6), s => Assert.False(s.Closed()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMemberOpeningSeveralLegsIsntPushedOutByManyOneConnectionCallers(bool floodIsOld)
    {
        // Every place held by a different caller, one stalled connection each; then a member opens 3 legs at once from
        // one address, and the flood goes on. The legs are the busiest caller but still in their grace period.
        var slots = New(256, 16);
        var flood = Enumerable.Range(0, 256).Select(i => Take(slots, $"fd00:0:0:{i:x}::1")).ToList();
        if (floodIsOld) PastGrace();
        var legs = Enumerable.Range(0, 3).Select(_ => Take(slots, "10.5.0.1")).ToList();
        for (var i = 0; i < 20; i++) Take(slots, $"fd00:1:0:{i:x}::1");
        Assert.All(legs, l => Assert.False(l.Closed()));
        Assert.All(flood.Take(23), f => Assert.True(f.Closed()));
    }

    [Fact]
    public void ACallerHoldingFarMoreThanItsShareLosesItsOldestOnceOld()
    {
        var slots = New(8, 8);
        var heavy = Enumerable.Range(0, 5).Select(_ => Take(slots, "10.6.0.1")).ToList();
        var light = Enumerable.Range(0, 3).Select(i => Take(slots, $"10.6.1.{i}")).ToList();
        PastGrace();
        Take(slots, "10.6.2.1");
        Assert.True(heavy[0].Closed());
        Assert.All(light, l => Assert.False(l.Closed()));
    }

    [Fact]
    public void AConnectionThatThrowsWhenClosedStillMakesRoom()
    {
        var slots = New(1, 1);
        slots.TryTake(null, () => throw new InvalidOperationException("boom"), out _);
        var next = slots.TryTake("10.7.0.1", () => { }, out var evicted);
        Assert.NotNull(next);
        Assert.True(evicted);
        Assert.Equal(1, slots.Evicted);
    }

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("fd00:1:2:3:4:5:6:7", "fd00:1:2:3::/64")]
    [InlineData("fd00:1:2:3:ffff::1", "fd00:1:2:3::/64")]
    [InlineData("127.0.0.1", null)]
    [InlineData("127.235.1.2", null)]
    [InlineData("::1", null)]
    public void CallersAreIPv4AddressesOrIPv6Prefixes(string address, string? caller) =>
        Assert.Equal(caller, TunnelServer.CallerOf(System.Net.IPAddress.Parse(address)));
}
