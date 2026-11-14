namespace Amber.Core.Multiplayer;

/// <summary>
/// The DNS servers used while traffic goes through a room member (reached through the tunnel, never directly):
/// Cloudflare's and Google's public resolvers. Every platform reads them from here; docs/privacy.md and the
/// "go online through this person" tooltip tell users about the switch.
/// </summary>
internal static class ExitDnsServers
{
    /// <summary>Cloudflare's public resolver.</summary>
    internal const string Primary = "1.1.1.1";

    /// <summary>Google's public resolver.</summary>
    internal const string Secondary = "8.8.8.8";

    /// <summary>Both, comma-separated, as the Windows DNS script compares them.</summary>
    internal const string Key = Primary + "," + Secondary;

    internal static readonly string[] All = [Primary, Secondary];
}
