namespace Amber.Core.Launch;

/// <summary>
/// The automatic update check: once a day Jay asks the release feed whether there is a newer version. A newer release is
/// shown only when 2 of the 3 trustees signed it (<see cref="UpdateCheck"/>); Jay never downloads or installs it, the
/// notice only opens the release page. Users can turn the check off or point it at their own feed.
/// </summary>
public static class AutoUpdateCheck
{
    /// <summary>The official release feed: the latest published (non-draft, non-prerelease) GitHub release.</summary>
    public const string OfficialFeed = "https://api.github.com/repos/warriorleavestar/jay-launcher/releases/latest";

    /// <summary>Release pages of the official feed: the verified tag is appended.</summary>
    public const string OfficialReleasePages = "https://github.com/warriorleavestar/jay-launcher/releases/tag/";

    /// <summary>How often the automatic check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The feed to use: the user's own address, or the official one when none is set.</summary>
    public static string FeedFor(string? userFeed) => string.IsNullOrWhiteSpace(userFeed) ? OfficialFeed : userFeed.Trim();

    /// <summary>Whether an automatic check is due. A last check in the future (clock moved back) counts as due.</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= Interval;

    /// <summary>
    /// The download page to offer for a verified release, or null when there is none Jay can vouch for. The signatures
    /// cover the tag but not the feed's <c>html_url</c>, so for the official feed the page is built from the verified tag
    /// alone; for the user's own feed its page is used only when it is HTTPS on the feed's own host.
    /// </summary>
    public static string? ReleasePage(string feed, VerifiedUpdate update)
    {
        if (!update.IsVerified || update.Check.Tag is not { Length: > 0 } tag) return null;
        if (IsOfficial(feed)) return OfficialReleasePages + Uri.EscapeDataString(tag);
        if (update.Info.Url is null || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUri)
            || !UpdateChecker.TryGetSafeUpdateUrl(update.Info.Url, out var page)) return null;
        return page!.Scheme == Uri.UriSchemeHttps && string.Equals(page.IdnHost, feedUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            ? page.AbsoluteUri : null;
    }

    /// <summary>Whether <paramref name="feed"/> is the official feed, also when typed in by hand (case, a trailing slash).</summary>
    public static bool IsOfficial(string feed) =>
        Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && string.Equals(uri.IdnHost, new Uri(OfficialFeed).IdnHost, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath.TrimEnd('/'), new Uri(OfficialFeed).AbsolutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a newer version should be shown again: not when the user already dismissed this version.</summary>
    public static bool ShouldNotify(string version, string? dismissedVersion) =>
        !string.Equals(version, dismissedVersion, StringComparison.Ordinal);
}
