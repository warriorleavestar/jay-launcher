namespace Amber.Core.Navigation;

/// <summary>The instance page's tabs as route parts (<c>library/{id}/{tab}</c>, official §2.2), in tab order.</summary>
public static class InstanceTabs
{
    public static readonly IReadOnlyList<string> Names = ["overview", "mods", "worlds", "packs", "shaders", "servers", "shots", "logs", "settings"];

    /// <summary>The tab index for a route part, or null when it isn't a tab (an unknown part keeps the current tab).</summary>
    public static int? IndexOf(string? part)
    {
        if (string.IsNullOrEmpty(part)) return null;
        for (var i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], part, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}
