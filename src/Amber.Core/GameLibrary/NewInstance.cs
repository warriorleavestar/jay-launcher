using Amber.Core.Game;
using Amber.Core.Install;

namespace Amber.Core.GameLibrary;

/// <summary>Why a typed instance name can't be used (official design §6.11: shown inline under the name box).</summary>
public enum InstanceNameProblem
{
    None,
    Empty,
    InvalidCharacters,
    Reserved,
    TrailingDotOrSpace,
    Taken,
}

/// <summary>Names for new instances: the generated default and the inline check.</summary>
public static class NewInstanceNames
{
    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Device names Windows refuses as file or folder names, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>"{version}-{loader}", or the bare version for vanilla; a taken name gets "-2", "-3"… (case-insensitive, as on Windows).</summary>
    public static string Default(string mcVersion, LoaderKind? loader, IEnumerable<string> taken) =>
        WithSuffix(mcVersion, loader?.ToString(), taken);

    /// <summary>"1.21.1-Jay" and the like: the game version with a suffix, numbered when taken.</summary>
    public static string WithSuffix(string mcVersion, string? suffix, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = suffix is { Length: > 0 } ? $"{mcVersion}-{suffix}" : mcVersion;
        if (!used.Contains(name)) return name;
        for (var n = 2; ; n++)
            if (!used.Contains($"{name}-{n}")) return $"{name}-{n}";
    }

    /// <summary>
    /// A name made from text that is not the player's own typing (such as “Alex's room”): forbidden characters dropped,
    /// trimmed, Windows device names and taken names get “-2”, “-3”….
    /// </summary>
    public static string Unique(string wanted, IEnumerable<string> taken, string fallback = "instance")
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clean = new string([.. wanted.Where(c => Array.IndexOf(Forbidden, c) < 0 && !char.IsControl(c))]).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") clean = fallback;
        if (clean.Length > 60) clean = clean[..60].TrimEnd('.', ' ');
        if (Check(clean, used) == InstanceNameProblem.None) return clean;
        for (var n = 2; ; n++)
            if (Check($"{clean}-{n}", used) == InstanceNameProblem.None) return $"{clean}-{n}";
    }

    /// <summary>
    /// Checks a name the player typed. The same rules as <see cref="GameDirectory.CheckId"/> on every platform, so a name
    /// made on Linux still opens on Windows, plus Windows device names and names already in the library.
    /// </summary>
    public static InstanceNameProblem Check(string? name, IEnumerable<string> taken)
    {
        if (string.IsNullOrWhiteSpace(name)) return InstanceNameProblem.Empty;
        if (name is "." or ".." || name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl)) return InstanceNameProblem.InvalidCharacters;
        if (name != name.TrimEnd('.', ' ') || name != name.TrimStart()) return InstanceNameProblem.TrailingDotOrSpace;
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem)) return InstanceNameProblem.Reserved;
        if (taken.Contains(name, StringComparer.OrdinalIgnoreCase)) return InstanceNameProblem.Taken;
        return InstanceNameProblem.None;
    }
}

/// <summary>Why a loader can't be picked for a game version before asking its server (official design §6.11).</summary>
public enum LoaderBlock
{
    None,
    /// <summary>Alpha and beta versions from before loaders existed.</summary>
    AncientVersion,
    /// <summary>Forge, NeoForge and OptiFine only publish builds for releases.</summary>
    Snapshot,
    /// <summary>The loader starts at a later version (Fabric and Quilt 1.14, NeoForge 1.20.1).</summary>
    TooOld,
    /// <summary>The Jay loader (the universal loader) doesn't run this game version, or isn't available yet.</summary>
    NotRunByJay,
}

/// <summary>
/// The rules that are certain without going online. Anything not ruled out here is still checked against the loader's
/// version list when the player picks it, so these only need to be right, not complete.
/// </summary>
public static class LoaderSupport
{
    public static LoaderBlock Check(string mcVersion, string versionType, LoaderKind kind)
    {
        if (versionType is "old_alpha" or "old_beta") return LoaderBlock.AncientVersion;
        if (versionType == "snapshot") return kind is LoaderKind.Fabric or LoaderKind.Quilt ? LoaderBlock.None : LoaderBlock.Snapshot;
        if (ParseRelease(mcVersion) is not { } v) return LoaderBlock.None;
        var minimum = kind switch
        {
            LoaderKind.Fabric or LoaderKind.Quilt => new Version(1, 14),
            LoaderKind.NeoForge => new Version(1, 20, 1),
            _ => null,
        };
        return minimum is not null && v < minimum ? LoaderBlock.TooOld : LoaderBlock.None;
    }

    /// <summary>The first version a loader supports, for the "needs 1.14 or later" note.</summary>
    public static string? Minimum(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric or LoaderKind.Quilt => "1.14",
        LoaderKind.NeoForge => "1.20.1",
        _ => null,
    };

    /// <summary>"1.20.1" → 1.20.1; "1.21" → 1.21; anything else (pre-releases, odd ids) → null.</summary>
    internal static Version? ParseRelease(string id)
    {
        var parts = id.Split('.');
        if (parts.Length is < 2 or > 3) return null;
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers.Length == 2 ? new Version(numbers[0], numbers[1]) : new Version(numbers[0], numbers[1], numbers[2]);
    }
}
