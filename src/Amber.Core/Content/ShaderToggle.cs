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

    /// <summary>
    /// Sets <paramref name="key"/>=<paramref name="value"/> in a key=value file. False if the file is missing or already has that value.
    /// Java (and so Iris and OptiFine) uses the last line for a key, so duplicates are dropped: the value lands in the first one.
    /// </summary>
    internal static bool SetKey(string path, string key, string value)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path).ToList();
        var matches = Enumerable.Range(0, lines.Count).Where(i => IsKey(lines[i], key)).ToList();
        var line = key + "=" + value;
        if (matches.Count == 1 && lines[matches[0]].Trim() == line) return false;
        if (matches.Count == 0) lines.Add(line);
        else
        {
            lines[matches[0]] = line;
            for (var i = matches.Count - 1; i > 0; i--) lines.RemoveAt(matches[i]);
        }
        File.WriteAllLines(path, lines);
        return true;
    }

    private static bool IsKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith('!')) return false;
        var equals = trimmed.IndexOf('=');
        return equals > 0 && trimmed.AsSpan(0, equals).TrimEnd().SequenceEqual(key);
    }
}
