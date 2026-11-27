using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Amber.Core.Tests;

/// <summary>
/// Tests name no real host's address. Where a public address is needed they use the documentation ranges and
/// <see cref="TestAddresses"/>; this check keeps it that way, including addresses hidden in an IPv6 form.
/// </summary>
public sealed partial class NoPublicAddressesInTestsTests
{
    [GeneratedRegex(@"(?<![\w.])\d{1,3}(?:\.\d{1,3}){3}(?![\w.])")]
    private static partial Regex IPv4Literal();

    [GeneratedRegex(@"(?<![\w:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?::\d{1,3}(?:\.\d{1,3}){3})?(?![\w:])")]
    private static partial Regex IPv6Literal();

    [Fact]
    public void TestSourcesHoldOnlyReservedAddresses()
    {
        var folder = Path.GetDirectoryName(ThisFile())!;
        // The sources are next to the build on CI and dev machines; a copied test assembly alone has nothing to check.
        if (!File.Exists(Path.Combine(folder, "Amber.Core.Tests.csproj"))) return;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("bin", StringComparison.Ordinal) || relative.StartsWith("obj", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (var match in IPv4Literal().Matches(lines[i]).Concat(IPv6Literal().Matches(lines[i])))
                    if (IsPublic(match.Value))
                        found.Add($"{relative}:{i + 1}: {match.Value}");
        }
        Assert.True(found.Count == 0, "Use documentation addresses (192.0.2.x, 198.51.100.x, 203.0.113.x, 2001:db8::) in tests:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.5")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:203.0.113.5")]
    [InlineData("2002:c633:6407::1")]
    [InlineData("10.0.0.1")]
    [InlineData("224.0.0.251")]
    [InlineData("fe80::1")]
    [InlineData("1.2.3")]
    public void TheCheckAllowsReservedAddresses(string text) => Assert.False(IsPublic(text));

    [Fact]
    public void TheCheckFindsPublicAddressesInAnyForm()
    {
        // Built from a range edge here, since writing one out would trip the check itself.
        var v4 = TestAddresses.Before("100.64.0.0");
        var b = v4.GetAddressBytes();
        Assert.True(IsPublic(v4.ToString()));
        Assert.True(IsPublic("::ffff:" + v4));
        Assert.True(IsPublic($"2002:{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}::1"));
        Assert.True(IsPublic($"64:ff9b::{b[0]:x2}{b[1]:x2}:{b[2]:x2}{b[3]:x2}"));
        Assert.True(IsPublic(TestAddresses.Before("2001:db8::").ToString()));
        Assert.Single(IPv4Literal().Matches($"x = \"{v4}\";"));
        Assert.Single(IPv6Literal().Matches($"x = \"[{TestAddresses.Before("2001:db8::")}]:1\";"));
    }

    /// <summary>A global unicast address outside every reserved range, also when written inside an IPv6 form.</summary>
    private static bool IsPublic(string text)
    {
        // Only full dotted quads: IPAddress also reads "1.2.3" or a bare number as an address.
        if (!text.Contains(':') && text.Count(c => c == '.') != 3) return false;
        if (!IPAddress.TryParse(text, out var address)) return false;
        // A whole /8 written as its first address (route halves such as 0.0.0.0/1 and 128.0.0.0/1) names no host.
        if (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [_, 0, 0, 0]) return false;
        // Embedded IPv4 (mapped, NAT64, 6to4, Teredo) is judged as that IPv4 address.
        address = Multiplayer.Transport.ExitPolicy.Normalize(address);
        if (TestAddresses.IsDocumentation(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xE0) != 0x20) return false;
        return Multiplayer.Transport.ExitPolicy.IsGloballyRoutable(address);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
