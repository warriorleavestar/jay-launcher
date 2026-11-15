namespace Amber.Core.Multiplayer;

/// <summary>
/// The DNS servers used while traffic goes through a room member (reached through the tunnel, never directly):
/// Cloudflare's and Google's public resolvers. Every platform reads them from here; docs/privacy.md and the
/// "go online through this person" tooltip tell users about the switch.
/// <para>
/// This is the one file allowed to hold these two public addresses (see the repository rules in AGENTS.md). Everything
/// else, code, scripts, comments, docs and tests, refers to them through this class or by name.
/// </para>
/// </summary>
public static class ExitDnsServers
{
    /// <summary>Cloudflare's public resolver.</summary>
    public const string Primary = "1.1.1.1";

    /// <summary>Google's public resolver.</summary>
    public const string Secondary = "8.8.8.8";

    /// <summary>Both, comma-separated, as the Windows DNS script compares them.</summary>
    public const string Key = Primary + "," + Secondary;

    internal static readonly string[] All = [Primary, Secondary];
}
