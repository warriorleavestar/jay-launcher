namespace Amber.App.Views;

/// <summary>
/// The GUI self-checks change settings, install versions and launch the game. They must never run against the
/// developer's own Jay settings or the real .minecraft folder, so they refuse to start without separate test folders.
/// </summary>
internal static class SelfTestGuard
{
    /// <summary>Why the self-check must not touch this data folder, or null when it is a separate test folder.</summary>
    public static string? DataDirProblem(string? dataDirVariable) =>
        string.IsNullOrWhiteSpace(dataDirVariable)
            ? "AMBER_DATA_DIR is not set: the self-check would change this computer's own Jay settings (use tools/run-selftest.ps1 or set it to a test folder)"
            : null;

    /// <summary>Why the self-check must not install into or launch from this game folder, or null when it is a test folder.</summary>
    public static string? GameDirProblem(string gameDirectory, string defaultRoot) =>
        string.IsNullOrWhiteSpace(gameDirectory) || SamePath(gameDirectory, defaultRoot)
            ? "the game folder in the test settings is the default .minecraft: set GameDirectory in AMBER_DATA_DIR's settings.json to a test folder"
            : null;

    /// <summary>Only an empty scope (the full run) and "content" reach the full flow; any other name is a typo.</summary>
    public static bool IsFullFlowScope(string? scope) => string.IsNullOrEmpty(scope) || scope == "content";

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Norm(a), Norm(b), comparison);
    }
}
