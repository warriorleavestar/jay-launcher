using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>Invite v3: the game segment (version, loader, mod fingerprint) after the check symbol, and v2 compatibility.</summary>
public sealed class InviteV3Tests
{
    private static readonly byte[] L = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
    private static readonly byte[] K = [.. Enumerable.Range(16, 16).Select(i => (byte)i)];
    private const string V2 = "JAY2-00041061050R3GG28A1C60T3GF-0G2491650N2RBHG68T3CE1T7GZ-2";
    private static readonly InviteGame Game = new("1.20.1", "fabric", 87, [1, 2, 3, 4, 5, 6]);

    [Fact]
    public void GameSegmentRoundTrips()
    {
        var text = new JayInvite(L, K, TunnelSettings.Default, Game).ToString();
        Assert.StartsWith(V2 + "-G", text);
        Assert.True(JayInvite.TryParse(text, out var parsed));
        Assert.Equal(Game, parsed.Game);
        Assert.Equal(L, parsed.LobbyKey);
    }

    [Theory]
    [InlineData("1.20.1", "fabric", 87)]
    [InlineData("1.7.10", "forge", 300)]
    [InlineData("24w14a", null, 0)]
    [InlineData("1.21.4", "neoforge", 65535)]
    [InlineData("1.20.1", "quilt", 1)]
    public void EveryLoaderAndCountRoundTrips(string version, string? loader, int count)
    {
        var game = new InviteGame(version, loader, count, System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        var tunnel = new TunnelSettings(TransportMode.High, true);
        var invite = JayInvite.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), tunnel, game);
        Assert.True(JayInvite.TryParse("来玩：" + invite.ToString().ToLowerInvariant() + "。", out var parsed));
        Assert.Equal(game, parsed.Game);
        Assert.Equal(tunnel, parsed.Tunnel);
    }

    [Fact]
    public void EveryCheckSymbolSurvives()
    {
        // Covers the non-alphanumeric check symbols (* ~ $ =) at the end of the game segment.
        var seen = new HashSet<char>();
        for (var i = 0; i < 600; i++)
        {
            var game = new InviteGame("1.20.1", "fabric", i, System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
            var text = JayInvite.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16), TunnelSettings.Default, game).ToString();
            seen.Add(text[^1]);
            Assert.True(JayInvite.TryParse(text + " ok", out var parsed));
            Assert.Equal(game, parsed.Game);
        }
        Assert.Contains('*', seen);
    }

    [Fact]
    public void V2CodesStillParseWithoutGame()
    {
        Assert.True(JayInvite.TryParse(V2, out var parsed));
        Assert.Null(parsed.Game);
    }

    [Fact]
    public void OlderParsersSeeOnlyPunctuationAfterTheCheckSymbol()
    {
        // v2 parsers accept any non-alphanumeric character right after the check symbol; the game segment starts with '-'.
        var text = new JayInvite(L, K, TunnelSettings.Default, Game).ToString();
        Assert.Equal('-', text[V2.Length]);
    }

    [Fact]
    public void TamperedGameSegmentIsDroppedButKeysSurvive()
    {
        var text = new JayInvite(L, K, TunnelSettings.Default, Game).ToString();
        var at = V2.Length + 4;
        var tampered = text[..at] + (text[at] == '0' ? '1' : '0') + text[(at + 1)..];
        Assert.True(JayInvite.TryParse(tampered, out var parsed));
        Assert.Null(parsed.Game);
        Assert.Equal(K, parsed.Token);
    }

    [Fact]
    public void GameSegmentIsBoundToTheKeys()
    {
        var text = new JayInvite(L, K, TunnelSettings.Default, Game).ToString();
        var segment = text[(V2.Length + 1)..];
        var other = new JayInvite(K, L, TunnelSettings.Default).ToString();
        Assert.True(JayInvite.TryParse(other + "-" + segment, out var parsed));
        Assert.Null(parsed.Game);
    }

    [Fact]
    public void LongVersionsAreCut()
    {
        var game = new InviteGame(new string('9', 40), "fabric", 3, new byte[6]);
        Assert.True(JayInvite.TryParse(new JayInvite(L, K, TunnelSettings.Default, game).ToString(), out var parsed));
        Assert.Equal(InviteGame.MaxVersionBytes, parsed.Game!.McVersion.Length);
    }

    [Fact]
    public void InviteBookWritesTheGame()
    {
        var book = new InviteBook { Game = Game };
        Assert.Equal(Game, book.Create(1, null, false, TunnelSettings.Default).Invite.Game);
    }

    [Fact]
    public void DiagnosticsHideTheWholeCode()
    {
        var text = new JayInvite(L, K, TunnelSettings.Default, Game).ToString();
        var redacted = NetDiagnostics.Redact("code " + text + " end", [], false);
        Assert.DoesNotContain("-G", redacted);
        Assert.DoesNotContain(text[^6..], redacted);
    }
}
