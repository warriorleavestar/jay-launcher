using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Amber.Core.Multiplayer;
using Amber.Core.Multiplayer.Transport;

namespace Amber.Core.Tests;

/// <summary>
/// The repository names no real host's address: not in code, scripts, comments, docs or settings. The one exception
/// (see AGENTS.md) is the two public resolvers exit mode uses through the tunnel, in ExitDnsServers.cs and nowhere else.
/// Tests are left to their own check; examples use the documentation ranges (192.0.2.x, 198.51.100.x, 203.0.113.x,
/// 2001:db8::).
/// </summary>
public sealed partial class NoPublicAddressesInRepositoryTests
{
    /// <summary>The only file allowed to hold a public address, and the only addresses it may hold.</summary>
    internal const string ExceptionFile = "src/Amber.Core/Multiplayer/ExitDnsServers.cs";

    [GeneratedRegex(@"(?<![\w.])\d{1,3}(?:\.\d{1,3}){3}(?![\w.])")]
    private static partial Regex IPv4Literal();

    [GeneratedRegex(@"(?<![\w:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?::\d{1,3}(?:\.\d{1,3}){3})?(?![\w:])")]
    private static partial Regex IPv6Literal();

    [GeneratedRegex(@"^/(\d{1,3})")]
    private static partial Regex PrefixLength();

    [Fact]
    public void TrackedFilesHoldNoPublicAddresses()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        if (TrackedFiles(root) is not { } files) return; // a copied test assembly alone has no sources to check
        var found = new List<string>();
        foreach (var relative in files)
        {
            if (relative.StartsWith("src/Amber.Core.Tests/", StringComparison.Ordinal)) continue;
            var path = Path.Combine(root, relative);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 << 20 || IsBinary(path)) continue;
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
                foreach (var address in Violations(relative, lines[i]))
                    found.Add($"{relative}:{i + 1}: {address}");
        }
        Assert.True(found.Count == 0, "Use documentation addresses (192.0.2.x, 198.51.100.x, 203.0.113.x, 2001:db8::):\n" + string.Join("\n", found));
    }

    [Fact]
    public void TheExceptionIsExactlyTheTwoResolversInTheirOneFile()
    {
        // Built from a range edge, since writing a public address out would trip the check itself.
        var other = new IPAddress([100, 63, 255, 255]).ToString();
        Assert.Empty(Violations(ExceptionFile, $"public const string Primary = \"{ExitDnsServers.Primary}\";"));
        Assert.Empty(Violations(ExceptionFile, $"public const string Secondary = \"{ExitDnsServers.Secondary}\";"));
        // Any other address in that file, or the resolvers anywhere else, is refused.
        Assert.Equal([other], Violations(ExceptionFile, $"public const string Third = \"{other}\";"));
        Assert.Equal([ExitDnsServers.Primary], Violations("src/Amber.Core/Multiplayer/UnixExitNetwork.cs", $"// DNS {ExitDnsServers.Primary}"));
        Assert.Equal([ExitDnsServers.Secondary], Violations("docs/privacy.md", $"uses {ExitDnsServers.Secondary} through the tunnel"));
        // A user's public address is refused wherever it is, also inside an IPv6 form or with a port.
        Assert.Equal([other], Violations("node/install.sh", $"#   PUBLIC_HOST={other}"));
        Assert.Single(Violations("docs/a.md", $"tcp://{other}:11010"));
        Assert.Single(Violations("docs/a.md", $"[::ffff:{other}]:1"));
        Assert.Single(Violations("docs/a.md", $"host {other}/32"));
    }

    [Theory]
    [InlineData("tcp://203.0.113.5:11010")]
    [InlineData("192.0.2.1 and 198.51.100.7")]
    [InlineData("[2001:db8::1]:11010")]
    [InlineData("10.0.0.1, 172.16.0.1, 192.168.1.1, 100.64.0.1, 127.0.0.1")]
    [InlineData("routes 0.0.0.0/1 and 128.0.0.0/1")]
    [InlineData("fe80::1 ff02::1 ::1")]
    public void ReservedAddressesPass(string line) => Assert.Empty(Violations("docs/a.md", line));

    [Fact]
    public void RangesAndVersionNumbersPass()
    {
        // Built here: written out, these would look like public hosts to the tests' own address check.
        var looksPublic = new IPAddress([100, 63, 255, 255]).ToString();
        var globalUnicast = new IPAddress([0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).ToString();
        Assert.Empty(Violations("docs/a.md", $"global unicast {globalUnicast}/3"));
        Assert.Empty(Violations("docs/a.md", $"a range {looksPublic}/24"));
        Assert.Empty(Violations("src/a.csproj", $"<PackageReference Include=\"X\" Version=\"{looksPublic}\" />"));
        Assert.Empty(Violations("docs/a.md", $"OpenJDK {looksPublic}, tag jdk-{looksPublic}-ga"));
    }

    /// <summary>The public addresses on one line that its file may not hold.</summary>
    internal static List<string> Violations(string relativePath, string line)
    {
        var found = new List<string>();
        foreach (var match in IPv4Literal().Matches(line).Concat(IPv6Literal().Matches(line)))
        {
            if (!IsPublicHost(line, match)) continue;
            if (relativePath == ExceptionFile && (match.Value == ExitDnsServers.Primary || match.Value == ExitDnsServers.Secondary)) continue;
            found.Add(match.Value);
        }
        return found;
    }

    private static bool IsPublicHost(string line, Match match)
    {
        var text = match.Value;
        // Only full dotted quads: IPAddress also reads "1.2.3" or a bare number as an address.
        if (!text.Contains(':') && text.Count(c => c == '.') != 3) return false;
        if (!IPAddress.TryParse(text, out var address)) return false;
        // A version number written like an address (a package version, a JDK release).
        var before = line[Math.Max(0, match.Index - 16)..match.Index];
        if (!text.Contains(':') && (before.Contains("version", StringComparison.OrdinalIgnoreCase) || before.Contains("jdk", StringComparison.OrdinalIgnoreCase)))
            return false;
        // A range, not a host: an address followed by a prefix shorter than a single host.
        if (PrefixLength().Match(line[(match.Index + match.Length)..]) is { Success: true } prefix
            && int.Parse(prefix.Groups[1].Value) < (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
            return false;
        // A whole /8 written as its first address names no host.
        if (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [_, 0, 0, 0]) return false;
        // Embedded IPv4 (mapped, NAT64, 6to4, Teredo) is judged as that IPv4 address.
        address = ExitPolicy.Normalize(address);
        if (IsDocumentation(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xE0) != 0x20) return false;
        return ExitPolicy.IsGloballyRoutable(address);
    }

    private static bool IsDocumentation(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 192 && b[1] == 0 && b[2] == 2 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113;
        return b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;
    }

    /// <summary>The files git tracks, relative with forward slashes; null when there is no checkout or no git.</summary>
    private static List<string>? TrackedFiles(string root)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return null;
        try
        {
            var start = new ProcessStartInfo("git", ["-C", root, "ls-files", "-z"]) { RedirectStandardOutput = true, UseShellExecute = false };
            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsBinary(string path)
    {
        Span<byte> head = stackalloc byte[8000];
        using var stream = File.OpenRead(path);
        var read = stream.Read(head);
        return head[..read].Contains((byte)0);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
