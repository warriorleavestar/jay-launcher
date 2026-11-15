namespace Amber.Core.Launch;

/// <summary>
/// The automatic update check: once a day Jay asks the release feed whether there is a newer version. A newer release is
/// shown only when enough Jay release keys signed it (<see cref="UpdateCheck"/>); Jay never downloads or installs it, the
/// notice only opens the release page. Users can turn the check off or point it at their own feed.
/// </summary>
public static class AutoUpdateCheck
{
    // warriorleavestar/jay-launcher is the repository where the trustee-signed Jay releases are published (the hardened
    // EasyTier build lives separately in Yumao-Feather/jay-easytier and is not a Jay update feed).

    /// <summary>The official release feed: the latest published (non-draft, non-prerelease) GitHub release.</summary>
    public const string OfficialFeed = "https://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest";

    /// <summary>Release pages of the official feed: the verified tag is appended.</summary>
    public const string OfficialReleasePages = "https://github.com/warriorleavestar/jay-launcher/releases/tag/";

    /// <summary>
    /// Whether this platform has the daily check and the official desktop releases at all: phones and tablets are updated
    /// by the stores and the preview channel, so there Jay neither checks by itself nor asks the desktop releases.
    /// </summary>
    public static bool IsSupported => PlatformFeatures.IsDesktop;

    /// <summary>How often the automatic check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The feed to use: the user's own address, or the official one when none is set.</summary>
    public static string FeedFor(string? userFeed) => string.IsNullOrWhiteSpace(userFeed) ? OfficialFeed : userFeed.Trim();

    /// <summary>
    /// The feed a manual check asks, or null when it makes no request at all: on a phone or tablet
    /// (<paramref name="supported"/> false) without an own address, since the store updates Jay there.
    /// </summary>
    public static string? ManualFeed(string? userFeed, bool supported) =>
        !supported && string.IsNullOrWhiteSpace(userFeed) ? null : FeedFor(userFeed);

    /// <summary>Whether an automatic check is due. A last check in the future (clock moved back) counts as due.</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= Interval;

    /// <summary>
    /// The download page to offer for a verified release, or null when there is none Jay can vouch for. The signatures
    /// cover the tag but not the feed's <c>html_url</c>, so for the official feed the page is built from the verified tag
    /// alone, and so is it for another GitHub repository's <c>releases/latest</c> feed (a fork). For any other feed of
    /// the user's own its page is used only when it is HTTPS on the feed's own host.
    /// </summary>
    public static string? ReleasePage(string feed, VerifiedUpdate update)
    {
        if (!update.IsVerified || update.Check.Tag is not { Length: > 0 } tag) return null;
        if (IsOfficial(feed)) return OfficialReleasePages + Uri.EscapeDataString(tag);
        if (GitHubReleasePages(feed) is { } pages) return pages + Uri.EscapeDataString(tag);
        if (update.Info.Url is null || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUri)
            || !UpdateChecker.TryGetSafeUpdateUrl(update.Info.Url, out var page)) return null;
        return page!.Scheme == Uri.UriSchemeHttps && string.Equals(page.IdnHost, feedUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            ? page.AbsoluteUri : null;
    }

    /// <summary>Whether <paramref name="feed"/> is the official feed, also when typed in by hand (case, a trailing slash).</summary>
    public static bool IsOfficial(string feed) =>
        Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && string.Equals(uri.IdnHost, new Uri(OfficialFeed).IdnHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath.TrimEnd('/'), new Uri(OfficialFeed).AbsolutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For a GitHub API feed <c>https://api.github.com/repos/{owner}/{repo}/releases/latest</c>, that repository's release
    /// pages (<c>https://github.com/{owner}/{repo}/releases/tag/</c>); otherwise null.
    /// </summary>
    public static string? GitHubReleasePages(string feed)
    {
        if (!Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
            || !string.Equals(uri.IdnHost, "api.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 5 || !string.Equals(parts[0], "repos", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[3], "releases", StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[4], "latest", StringComparison.OrdinalIgnoreCase)
            || !IsGitHubName(parts[1]) || !IsGitHubName(parts[2])) return null;
        return $"https://github.com/{parts[1]}/{parts[2]}/releases/tag/";
    }

    private static bool IsGitHubName(string name) =>
        name.Length is > 0 and <= 100 && name is not "." and not ".." && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>Whether a newer version should be shown again: not when the user already dismissed this version.</summary>
    public static bool ShouldNotify(string version, string? dismissedVersion) =>
        !string.Equals(version, dismissedVersion, StringComparison.Ordinal);
}
