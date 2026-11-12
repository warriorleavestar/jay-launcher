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

    /// <summary>How often the automatic check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The feed to use: the user's own address, or the official one when none is set.</summary>
    public static string FeedFor(string? userFeed) => string.IsNullOrWhiteSpace(userFeed) ? OfficialFeed : userFeed.Trim();

    /// <summary>Whether an automatic check is due. A last check in the future (clock moved back) counts as due.</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= Interval;

    /// <summary>Whether a newer version should be shown again: not when the user already dismissed this version.</summary>
    public static bool ShouldNotify(string version, string? dismissedVersion) =>
        !string.Equals(version, dismissedVersion, StringComparison.Ordinal);
}
