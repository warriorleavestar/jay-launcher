using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;
using Xunit;

namespace Amber.Core.Tests;

/// <summary>
/// A relay node set up with the scripts in node/ only carries the networks its whitelist names (unless the owner
/// chose RELAY=all). The default list has to cover every network name Jay makes for a room, or the node refuses
/// the rooms it was set up for.
/// </summary>
public sealed class RelayNodeWhitelistTests
{
    private static readonly string Node = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "node"));

    public static TheoryData<string, string> Defaults() => new()
    {
        { "install.sh", @"WHITELIST=""([^""*]*\*[^""]*)""; else" },
        { "install-windows.ps1", @"\{ ""([^""*]*\*[^""]*)"" \} else" },
        { "docker-compose.yml", @"- --relay-network-whitelist\r?\n\s+- (.+?)\r?\n" },
    };

    [Theory]
    [MemberData(nameof(Defaults))]
    public void TheDefaultWhitelistCoversEveryNetworkNameJayMakes(string file, string pattern)
    {
        var match = Regex.Match(File.ReadAllText(Path.Combine(Node, file)), pattern);
        Assert.True(match.Success, file);
        // EasyTier splits the list at spaces and matches each entry as a wildcard.
        var entries = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain("*", entries);

        var key = RandomNumberGenerator.GetBytes(32);
        var invited = RoomCredentials.Create(key, 25565, TunnelSettings.Default);
        string[] names =
        [
            RoomCode.Create(25565).EasyTierNetworkName,
            invited.Network,
            invited.Rotate().Network,
            LobbyKeys.LobbyName(key),
            SplitNet.Create().Network,
        ];
        foreach (var name in names)
            Assert.True(entries.Any(entry => Wildcard(entry).IsMatch(name)), $"{file}: {name[..name.IndexOf('-')]}-… is not on the default whitelist");
    }

    private static Regex Wildcard(string entry) =>
        new("^" + Regex.Escape(entry).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);
}
