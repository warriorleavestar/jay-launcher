namespace Amber.Mobile;

/// <summary>Creates and upgrades the small set of Minecraft options owned by the mobile port.</summary>
internal static class MobileGameOptions
{
    internal const string MigrationMarkerName = ".jay-mobile-options-v2";

    private const string Defaults = """
        version:4671
        ao:true
        autoJump:true
        biomeBlendRadius:0
        enableVsync:true
        entityDistanceScaling:0.75
        entityShadows:false
        graphicsPreset:"fast"
        guiScale:0
        maxFps:60
        mipmapLevels:2
        onboardAccessibility:false
        particles:1
        pauseOnLostFocus:false
        rawMouseInput:false
        renderClouds:"false"
        renderDistance:8
        simulationDistance:6
        touchscreen:false
        """;

    /// <summary>
    /// Returns mobile-safe options while preserving every setting that is not owned by this
    /// migration. Scale 3 was Jay's old hard-coded Android default; it is upgraded only once so
    /// a later player choice of scale 3 remains untouched.
    /// </summary>
    internal static string Prepare(
        string? existing,
        bool migrateLegacyScale,
        string defaultLanguage = "zh_cn")
    {
        defaultLanguage = SafeLanguage(defaultLanguage);
        if (string.IsNullOrEmpty(existing))
            return Defaults + Environment.NewLine + $"lang:{defaultLanguage}" + Environment.NewLine;

        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var terminated = existing.EndsWith('\n');
        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (terminated && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var hasLanguage = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var option = lines[index].Trim();
            if (option.StartsWith("lang:", StringComparison.Ordinal) && option.Length > "lang:".Length)
                hasLanguage = true;
            if (migrateLegacyScale && option == "guiScale:3")
                lines[index] = "guiScale:0";
        }

        if (!hasLanguage) lines.Add($"lang:{defaultLanguage}");
        return string.Join(newline, lines) + newline;
    }

    private static string SafeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "zh_cn";
        foreach (var character in language)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
                return "zh_cn";
        }

        return language;
    }
}
