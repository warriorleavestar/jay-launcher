namespace Amber.Core.Content;

/// <summary>
/// Turns shaders off in a game directory without removing any shader pack, for the "last session" panel's fix button.
/// Covers Iris and Oculus (<c>config/iris.properties</c>, <c>config/oculus.properties</c>) and OptiFine
/// (<c>optionsshaders.txt</c>). Only files that already exist are changed.
/// </summary>
public static class ShaderToggle
{
    /// <returns>True if at least one renderer's setting was switched off.</returns>
    public static bool TurnOff(string gameDir)
    {
        var changed = false;
        foreach (var name in new[] { "iris.properties", "oculus.properties" })
            changed |= SetKey(Path.Combine(gameDir, "config", name), "enableShaders", "false");
        changed |= SetKey(Path.Combine(gameDir, "optionsshaders.txt"), "shaderPack", "OFF");
        return changed;
    }

    /// <summary>Sets <paramref name="key"/>=<paramref name="value"/> in a key=value file. False if the file is missing or already has that value.</summary>
    internal static bool SetKey(string path, string key, string value)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path).ToList();
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
        var line = key + "=" + value;
        if (index >= 0)
        {
            if (lines[index].Trim() == line) return false;
            lines[index] = line;
        }
        else lines.Add(line);
        File.WriteAllLines(path, lines);
        return true;
    }
}
