using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Amber.Core.Launch;
using Amber.Core.Multiplayer;

namespace Amber.Core.Diagnostics;

/// <summary>
/// The one masking for text that leaves this computer: a log sent to someone, an exported crash bundle, an error report.
/// It covers account tokens and launch secrets, e-mail addresses, the computer user name and home folder, every kind of
/// Jay code in full (room, invite, direct, server, relay, network, friend and device codes), room network names and
/// secrets, and public IP addresses. Safe to run more than once.
/// </summary>
public static partial class Redaction
{
    public const string Code = "<code>";
    public const string Address = "<ip>";
    public const string Secret = "<redacted>";

    public static string ForSharing(string text, IEnumerable<string>? secrets = null, string? userName = null, string? homeDir = null)
    {
        // Codes first: whole, so nothing of a code (not even its room id or port) is left to match later.
        text = AnyCode().Replace(text, Code);
        text = TrustKey().Replace(text, Code);
        text = ErrorReport.Redact(text, userName ?? Environment.UserName, homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        text = TokenField().Replace(text, m => m.Groups[1].Value + Secret);
        // Room network names, --network-secret, printed EasyTier configs, and the room's own secrets wherever they appear.
        text = NetDiagnostics.Redact(text, secrets ?? [], includePublicIps: true);
        return HidePublicAddresses(text);
    }

    /// <summary>Every public IPv4 and IPv6 address becomes <see cref="Address"/>; private, loopback and link-local ones stay.</summary>
    public static string HidePublicAddresses(string text)
    {
        text = IPv4().Replace(text, m => IPAddress.TryParse(m.Value, out var a) && PortMapping.IsPublic(a) ? Address : m.Value);
        return IPv6().Replace(text, m => IPAddress.TryParse(m.Value.Trim('[', ']'), out var a) && a.AddressFamily == AddressFamily.InterNetworkV6
            && !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.IPv6Any) && PortMapping.IsPublic(a) ? Address : m.Value);
    }

    // Every Jay code prefix, then the rest of the code (base32, base64url and the invite's suffix marks).
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:AMB(?:DIRECT\d*|REPLY\d*|NODE)?|JAY(?:2[DR]?|DS\d|DR\d|SS\d|S\d|NS\d|NR\d|R\d|C\d|NET\d*|F\d*))-[A-Za-z0-9_~*$=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyCode();

    [GeneratedRegex(@"jay-key-v\d+:[A-Za-z0-9_=+/-]+", RegexOptions.IgnoreCase)]
    private static partial Regex TrustKey();

    // "access_token": "…", refreshToken=…, x-api-key: … (JSON, query strings, headers).
    [GeneratedRegex(@"((?:access|refresh|id)_?token[""']?\s*[:=]\s*[""']?|x-api-key[""']?\s*[:=]\s*[""']?|[?&](?:code|token|key)=)[^""'\s&,;}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenField();

    // Not part of a version (v1.2.3.4, forge-47.2.0.1, 1.2.3.4-beta).
    [GeneratedRegex(@"(?<![\d.A-Za-z_-])(\d{1,3}\.){3}\d{1,3}(?![\d.A-Za-z_-])")]
    private static partial Regex IPv4();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])")]
    private static partial Regex IPv6();
}
