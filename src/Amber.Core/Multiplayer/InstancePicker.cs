namespace Amber.Core.Multiplayer;

/// <summary>An instance the joiner could use. <see cref="Fingerprint"/> is filled only for instances worth hashing (same version and loader).</summary>
public sealed record InstanceCandidate(string Id, string McVersion, string? Loader, int ModCount, byte[]? Fingerprint = null);

public enum InstanceMatchKind
{
    /// <summary>Same version, loader and exactly the same mod files.</summary>
    Exact,
    /// <summary>Same version and loader; mods may differ (checked after the host approves).</summary>
    SameGame,
}

public sealed record InstanceMatch(InstanceCandidate Instance, InstanceMatchKind Kind);

/// <summary>
/// Picks the instance closest to the host's game from an invite (official §6.14 “auto-pick the instance”). Only an
/// instance with the same Minecraft version and loader can join at all; among those an exact mod fingerprint wins, then the
/// closest mod count, then the caller's order (focused / recently played first). No match → offer to install one.
/// </summary>
public static class InstancePicker
{
    public static bool SameGame(InviteGame game, InstanceCandidate candidate) =>
        string.Equals(game.McVersion, candidate.McVersion, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeLoader(game.Loader), NormalizeLoader(candidate.Loader), StringComparison.OrdinalIgnoreCase);

    public static InstanceMatch? Pick(InviteGame game, IEnumerable<InstanceCandidate> candidates)
    {
        InstanceMatch? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            if (!SameGame(game, c)) continue;
            if (c.Fingerprint is { } fp && fp.AsSpan().SequenceEqual(game.ModsFingerprint) && c.ModCount == game.ModCount)
                return new InstanceMatch(c, InstanceMatchKind.Exact);
            var distance = Math.Abs(c.ModCount - game.ModCount);
            if (distance < bestDistance)
            {
                best = new InstanceMatch(c, InstanceMatchKind.SameGame);
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>OptiFine alone is played like vanilla as far as the server is concerned.</summary>
    private static string NormalizeLoader(string? loader) => loader is null or "" or "optifine" ? "vanilla" : loader;
}
