using System.Net;
using System.Text.RegularExpressions;
using Amber.Core.Localization;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Sealed direct codes: no server, and no readable IP in the code; the passphrase opens it.</summary>
public sealed class SealedDirectTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly DirectAddresses Host = new("0123456789abcdef", NatKind.Cone,
    [
        new(PathKind.IPv6, IPEndPoint.Parse("[2408:8000::1]:40000")),
        new(PathKind.Punch, IPEndPoint.Parse("203.0.113.5:51234")),
        new(PathKind.Lan, IPEndPoint.Parse("192.168.1.5:40000")),
    ]);

    private static readonly DirectAddresses Guest = new("ffffffffffffffff", NatKind.Symmetric, [new(PathKind.Punch, IPEndPoint.Parse("198.51.100.7:40001"))]);

    [Fact]
    public void OfferAndReplyRoundTripWithOnePassphrase()
    {
        using var offer = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now);
        Assert.StartsWith(SealedDirect.OfferPrefix, offer.Code);
        Assert.Matches(@"^\d{4} \d{4} \d{4} \d{4}$", offer.Passphrase);
        Assert.Equal(Now + SealedDirect.Lifetime, offer.Expires);

        // Pasted with chat text around it, the passphrase typed without spaces.
        using var opened = SealedDirect.Open("来直连：" + offer.Code + " 快来", offer.Passphrase.Replace(" ", ""), Now.AddMinutes(1));
        Assert.Equal(Host.Session, opened.Host.Session);
        Assert.Equal(Host.Candidates, opened.Host.Candidates);
        Assert.Equal(DirectTestKeys.Invite.ToString(), opened.Invite.ToString());

        var reply = SealedDirect.SealReply(opened, Guest, Now.AddMinutes(2));
        Assert.StartsWith(SealedDirect.ReplyPrefix, reply);
        var guest = offer.OpenReply(reply, Now.AddMinutes(3));
        Assert.Equal(Host.Session, guest.Session);
        Assert.Equal(Guest.Candidates, guest.Candidates);
        Assert.Equal(NatKind.Symmetric, guest.Nat);
    }

    /// <summary>The whole point: nothing readable in either code — no IP, no port, no invite, no session.</summary>
    [Fact]
    public void CodesShowNoAddressOrInvite()
    {
        using var offer = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now);
        using var opened = SealedDirect.Open(offer.Code, offer.Passphrase, Now);
        var reply = SealedDirect.SealReply(opened, Guest, Now);
        foreach (var code in new[] { offer.Code, reply })
        {
            Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", code);
            foreach (var hidden in new[] { "203.0.113.5", "198.51.100.7", "192.168.1.5", "2408:8000", "51234", "40001", Host.Session, Guest.Session, DirectTestKeys.Invite.ToString(), "JAY2-", "Punch" })
                Assert.DoesNotContain(hidden, code, StringComparison.OrdinalIgnoreCase);
            Assert.False(OldDirectCodes.Contains(code));
        }
        Assert.DoesNotContain(offer.Passphrase, offer.ToString());
        Assert.DoesNotContain(offer.Code, offer.ToString());
        // A fresh salt and nonce each time: the same offer twice looks different.
        using var again = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, offer.Passphrase);
        Assert.NotEqual(offer.Code, again.Code);
    }

    [Fact]
    public void WrongPassphraseIsRefused()
    {
        using var offer = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, "1111 2222 3333 4444");
        var e = Assert.Throws<InvalidDataException>(() => SealedDirect.Open(offer.Code, "1111 2222 3333 4445", Now));
        Assert.Equal(L.T("core.mp.sealed.wrong_passphrase"), e.Message);
        e = Assert.Throws<InvalidDataException>(() => SealedDirect.Open(offer.Code, "1111 2222 3333", Now));
        Assert.Equal(L.T("core.mp.sealed.passphrase_format"), e.Message);
    }

    [Fact]
    public void ChangedOrDamagedCodesAreRefused()
    {
        using var offer = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, "1111 2222 3333 4444");
        var body = offer.Code[SealedDirect.OfferPrefix.Length..];
        var flipped = SealedDirect.OfferPrefix + body[..^3] + (body[^3] == 'A' ? 'B' : 'A') + body[^2..];
        Assert.Throws<InvalidDataException>(() => SealedDirect.Open(flipped, "1111222233334444", Now));
        Assert.Throws<InvalidDataException>(() => SealedDirect.Open(SealedDirect.OfferPrefix + "abc", "1111222233334444", Now));
        Assert.Throws<InvalidDataException>(() => SealedDirect.Open("hello", "1111222233334444", Now));
        // A reply pasted as an offer, and an offer pasted as a reply.
        using var opened = SealedDirect.Open(offer.Code, "1111222233334444", Now);
        var reply = SealedDirect.SealReply(opened, Guest, Now);
        Assert.Throws<InvalidDataException>(() => SealedDirect.Open(reply, "1111222233334444", Now));
        Assert.Throws<InvalidDataException>(() => offer.OpenReply(offer.Code, Now));
    }

    [Fact]
    public void ExpiredCodesAreRefused()
    {
        using var offer = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, "1111 2222 3333 4444");
        var e = Assert.Throws<InvalidDataException>(() => SealedDirect.Open(offer.Code, "1111222233334444", Now + SealedDirect.Lifetime + TimeSpan.FromSeconds(1)));
        Assert.Equal(L.T("core.mp.sealed.expired"), e.Message);
        // A code stamped far in the future (a clock set ahead to stretch it) is refused too.
        Assert.Throws<InvalidDataException>(() => SealedDirect.Open(offer.Code, "1111222233334444", Now - TimeSpan.FromHours(1)));
        using var opened = SealedDirect.Open(offer.Code, "1111222233334444", Now);
        var reply = SealedDirect.SealReply(opened, Guest, Now);
        e = Assert.Throws<InvalidDataException>(() => offer.OpenReply(reply, Now + TimeSpan.FromMinutes(11)));
        Assert.Equal(L.T("core.mp.sealed.expired"), e.Message);
    }

    /// <summary>A reply made for another offer (another room, an older code) is named as such, not opened.</summary>
    [Fact]
    public void ReplyForAnotherOfferIsRefused()
    {
        using var first = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, "1111 2222 3333 4444");
        using var second = SealedDirect.Seal(Host, DirectTestKeys.Invite, Now, "1111 2222 3333 4444");
        using var opened = SealedDirect.Open(first.Code, "1111222233334444", Now);
        var reply = SealedDirect.SealReply(opened, Guest, Now);
        var e = Assert.Throws<InvalidDataException>(() => second.OpenReply(reply, Now));
        Assert.Equal(L.T("core.mp.sealed.other_offer"), e.Message);
    }

    [Theory]
    [InlineData("4827 1930 5561 0742", "4827193055610742")]
    [InlineData("4827-1930-5561-0742", "4827193055610742")]
    [InlineData(" ４８２７ １９３０ ５５６１ ０７４２ ", "4827193055610742")]
    [InlineData("4827 1930 5561 074", null)]
    [InlineData("4827 1930 5561 07421", null)]
    [InlineData("4827 1930 5561 074x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PassphrasesAreNormalized(string? text, string? digits) => Assert.Equal(digits, SealedDirect.NormalizePassphrase(text));

    [Fact]
    public void NewPassphrasesAreRandomDigits()
    {
        var all = Enumerable.Range(0, 20).Select(_ => SealedDirect.NewPassphrase()).ToList();
        Assert.All(all, p => Assert.Matches(@"^\d{4} \d{4} \d{4} \d{4}$", p));
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory]
    [InlineData("JAYDS1-abcDEF_-12", PastedCodeKind.SealedOffer)]
    [InlineData("来直连：jayds1-abc", PastedCodeKind.SealedOffer)]
    [InlineData("JAYDR1-abc", PastedCodeKind.SealedReply)]
    public void PastedCodeRecognizesSealedCodes(string text, PastedCodeKind kind) => Assert.Equal(kind, PastedCode.Detect(text));

    [Fact]
    public void DiagnosticsRedactSealedCodes()
    {
        var text = NetDiagnostics.Redact("offer JAYDS1-QUJDREVGR0hJSktMTU5PUA reply jaydr1-QUJDREVGR0hJSktMTU5PUA", [], includePublicIps: true);
        Assert.DoesNotContain("QUJDREVGR0hJSktMTU5PUA", text);
    }
}
