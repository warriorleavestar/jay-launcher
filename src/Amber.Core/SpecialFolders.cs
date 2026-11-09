namespace Amber.Core;

/// <summary>
/// Per-user folders that are never empty. On Linux and macOS, <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// returns "" when the folder (for example ~/.config) doesn't exist yet, which used to turn Jay's data directory into a
/// path relative to the current directory. These helpers ask .NET to create the folder and fall back to the XDG default
/// under the home directory.
/// </summary>
public static class SpecialFolders
{
    /// <summary>Roaming app data: %APPDATA% on Windows, $XDG_CONFIG_HOME or ~/.config elsewhere.</summary>
    public static string ApplicationData() => Resolve(Environment.SpecialFolder.ApplicationData, ".config", "XDG_CONFIG_HOME");

    /// <summary>Local app data: %LOCALAPPDATA% on Windows, $XDG_DATA_HOME or ~/.local/share elsewhere.</summary>
    public static string LocalApplicationData() => Resolve(Environment.SpecialFolder.LocalApplicationData, Path.Combine(".local", "share"), "XDG_DATA_HOME");

    internal static string Resolve(Environment.SpecialFolder folder, string homeRelative, string xdgVariable,
        Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string>? getFolderPath = null,
        Func<string, string?>? getVariable = null, string? home = null)
    {
        getFolderPath ??= Environment.GetFolderPath;
        getVariable ??= Environment.GetEnvironmentVariable;
        var path = TryGet(getFolderPath, folder, Environment.SpecialFolderOption.None);
        if (IsUsable(path)) return path!;
        path = TryGet(getFolderPath, folder, Environment.SpecialFolderOption.Create);
        if (IsUsable(path)) return path!;
        if (getVariable(xdgVariable) is { } xdg && IsUsable(xdg)) return xdg;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!IsUsable(home)) home = getVariable("HOME");
        if (!IsUsable(home)) throw new InvalidOperationException($"No home directory to put {folder} in; set AMBER_DATA_DIR.");
        return Path.Combine(home!, homeRelative);
    }

    private static string? TryGet(Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> get,
        Environment.SpecialFolder folder, Environment.SpecialFolderOption option)
    {
        try
        {
            return get(folder, option);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A relative path would land in whatever directory Jay was started from, so only rooted paths count.
    private static bool IsUsable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);
}
