namespace Amber.Core.Net;

/// <summary>
/// Download addresses written as plain http (old Forge profiles, some third-party version JSONs) are fetched over https.
/// The http address is kept only as a last fallback for a file whose SHA-1 is known, so a copy that was changed on the
/// way is still refused; a file without a hash is never taken over plain http.
/// </summary>
internal static class HttpsUpgrade
{
    // Hosts that moved: the old http address has no https twin at the same path.
    private static readonly (string From, string To)[] Moved =
    [
        ("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
    ];

    /// <summary>The https address to use for a plain-http one; null when the address is not plain http or is on this computer.</summary>
    public static string? Upgrade(string url)
    {
        const string plain = "http://";
        if (!url.StartsWith(plain, StringComparison.OrdinalIgnoreCase)) return null;
        // This computer itself (a local test server or cache): there is no network path to protect.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback) return null;
        foreach (var (from, to) in Moved)
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase)) return to + url[from.Length..];
        return "https://" + url[plain.Length..];
    }
}
