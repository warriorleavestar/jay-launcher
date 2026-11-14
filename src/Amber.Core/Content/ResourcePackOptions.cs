using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amber.Core.Content;

/// <summary>
/// Which resource packs are on and in what order, read from and written to the game's own <c>options.txt</c> (the same
/// list as the in-game resource pack screen; official design §6.7). Minecraft lists packs lowest priority first, with
/// built-in packs like <c>vanilla</c> at the bottom; Jay shows them top = highest, so the order is reversed here.
/// Only the <c>resourcePacks</c> line is touched; every other option is kept as it is.
/// </summary>
public static class ResourcePackOptions
{
    private const string Key = "resourcePacks:";
    private const string FilePrefix = "file/";

    /// <summary>Folder or zip names of the packs that are on, highest priority first.</summary>
    public static IReadOnlyList<string> Enabled(string workDir)
    {
        var list = ReadList(workDir);
        return [.. list.Where(e => e.StartsWith(FilePrefix, StringComparison.Ordinal)).Select(e => e[FilePrefix.Length..]).Reverse()];
    }

    /// <summary>
    /// Turns on exactly these packs (highest priority first). Built-in entries that were there (vanilla, fabric, programmer
    /// art…) stay below them in their old order; vanilla is added when missing.
    /// </summary>
    public static void SetEnabled(string workDir, IReadOnlyList<string> topFirst)
    {
        var builtIn = ReadList(workDir).Where(e => !e.StartsWith(FilePrefix, StringComparison.Ordinal)).ToList();
        if (!builtIn.Contains("vanilla")) builtIn.Insert(0, "vanilla");
        var entries = builtIn.Concat(topFirst.Reverse().Select(n => FilePrefix + n)).ToList();
        var json = new JsonArray(entries.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()).ToJsonString();
        var path = Path.Combine(workDir, "options.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var index = lines.FindIndex(l => l.StartsWith(Key, StringComparison.Ordinal));
        if (index >= 0) lines[index] = Key + json;
        else lines.Add(Key + json);
        Directory.CreateDirectory(workDir);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    /// <summary>Moves one enabled pack up (toward higher priority) or down. Returns the new order.</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> topFirst, string name, int delta)
    {
        var list = topFirst.ToList();
        var i = list.IndexOf(name);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return list;
        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }

    internal static List<string> ReadList(string workDir)
    {
        var path = Path.Combine(workDir, "options.txt");
        if (!File.Exists(path)) return [];
        var line = File.ReadLines(path).FirstOrDefault(l => l.StartsWith(Key, StringComparison.Ordinal));
        if (line is null) return [];
        try
        {
            return JsonNode.Parse(line[Key.Length..]) is JsonArray array
                ? [.. array.Select(n => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null).OfType<string>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Whether a resource pack was made for an older or newer game than the instance (shown, never blocking).</summary>
public enum PackAge { Unknown, Fits, Older, Newer }

/// <summary>Reads <c>pack.mcmeta</c>'s <c>pack_format</c> and compares it with the format a game version expects.</summary>
public static class PackFormat
{
    /// <summary>First release of each resource pack format (format, version).</summary>
    private static readonly (int Format, int[] From)[] Table =
    [
        (1, [1, 6]), (2, [1, 9]), (3, [1, 11]), (4, [1, 13]), (5, [1, 15]), (6, [1, 16, 2]), (7, [1, 17]), (8, [1, 18]),
        (9, [1, 19]), (12, [1, 19, 3]), (13, [1, 19, 4]), (15, [1, 20]), (18, [1, 20, 2]), (22, [1, 20, 3]),
        (32, [1, 20, 5]), (34, [1, 21]), (42, [1, 21, 2]), (46, [1, 21, 4]),
    ];

    /// <summary>The format a release expects, or null for snapshots, very old or unknown newer versions.</summary>
    public static int? Expected(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || parts.Any(p => !int.TryParse(p, out _))) return null;
        var v = parts.Select(int.Parse).ToArray();
        if (Compare(v, [1, 6]) < 0 || Compare(v, [1, 21, 5]) >= 0) return null;
        int? result = null;
        foreach (var (format, from) in Table)
            if (Compare(v, from) >= 0) result = format;
        return result;
    }

    public static PackAge Age(int? packFormat, string mcVersion) => (packFormat, Expected(mcVersion)) switch
    {
        (null, _) or (_, null) => PackAge.Unknown,
        var (p, e) when p == e => PackAge.Fits,
        var (p, e) when p < e => PackAge.Older,
        _ => PackAge.Newer,
    };

    /// <summary><c>pack.pack_format</c> from a zip or a folder, or null when it can't be read.</summary>
    public static int? Read(string path)
    {
        try
        {
            string? text = null;
            if (Directory.Exists(path))
            {
                var meta = Path.Combine(path, "pack.mcmeta");
                if (File.Exists(meta)) text = File.ReadAllText(meta);
            }
            else if (File.Exists(path))
            {
                using var zip = ZipFile.OpenRead(path);
                if (zip.GetEntry("pack.mcmeta") is { Length: < 1024 * 1024 } entry)
                {
                    using var reader = new StreamReader(entry.Open());
                    text = reader.ReadToEnd();
                }
            }
            if (text is null) return null;
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["pack"]?["pack_format"]?.GetValue<int>();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}

/// <summary>
/// The shader pack Iris (or Oculus) uses: <c>config/iris.properties</c> <c>shaderPack</c> and <c>enableShaders</c>.
/// The file is created when missing (Iris reads it at start), other keys are kept.
/// </summary>
public static class ShaderSelection
{
    public static string? Current(string workDir)
    {
        foreach (var file in Files(workDir))
        {
            if (!File.Exists(file)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // A Java properties file: comments start with # or !, a later duplicate wins, and values are escaped.
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#') || trimmed.StartsWith('!')) continue;
                var pair = trimmed.Split('=', 2);
                if (pair.Length == 2) values[pair[0].Trim()] = Unescape(pair[1].Trim());
            }
            if (values.TryGetValue("enableShaders", out var on) && on == "false") return null;
            return values.TryGetValue("shaderPack", out var pack) && pack.Length > 0 ? pack : null;
        }
        return null;
    }

    /// <summary>Uses this shader pack (null turns shaders off).</summary>
    public static void Select(string workDir, string? pack)
    {
        var files = Files(workDir).Where(File.Exists).ToList();
        if (files.Count == 0) files.Add(Files(workDir)[0]);
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file)) File.WriteAllText(file, "");
            if (pack is not null) ShaderToggle.SetKey(file, "shaderPack", Escape(pack));
            ShaderToggle.SetKey(file, "enableShaders", pack is null ? "false" : "true");
        }
        // OptiFine keeps its choice in optionsshaders.txt; only touched when it is already there.
        ShaderToggle.SetKey(Path.Combine(workDir, "optionsshaders.txt"), "shaderPack", pack ?? "OFF");
    }

    /// <summary>
    /// Whether something that can run shader packs is installed: an enabled Iris, Oculus or OptiFine jar in <c>mods</c>,
    /// or OptiFine's own settings file. Without one the shader tab says what to install first.
    /// </summary>
    public static bool HasLoader(string workDir)
    {
        if (File.Exists(Path.Combine(workDir, "optionsshaders.txt"))) return true;
        var mods = Path.Combine(workDir, "mods");
        if (!Directory.Exists(mods)) return false;
        try
        {
            return Directory.EnumerateFiles(mods, "*.jar").Select(Path.GetFileName).Any(n =>
                n!.Contains("iris", StringComparison.OrdinalIgnoreCase) || n.Contains("oculus", StringComparison.OrdinalIgnoreCase)
                || n.Contains("optifine", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Java reads .properties files as ISO-8859-1: a value is written with backslashes doubled and every character
    /// outside printable ASCII as \uXXXX, the way Iris and Oculus write it themselves.
    /// </summary>
    internal static string Escape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\\') sb.Append(@"\\");
            else if (c < 0x20 || c > 0x7E) sb.Append(@"\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Reverses the escapes of a .properties value (\uXXXX, \t, \n, \r, \f and an escaped character).</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 == value.Length) { sb.Append(c); continue; }
            var next = value[++i];
            if (next == 'u' && i + 4 < value.Length
                && int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var code))
            {
                sb.Append((char)code);
                i += 4;
            }
            else sb.Append(next switch { 't' => '\t', 'n' => '\n', 'r' => '\r', 'f' => '\f', _ => next });
        }
        return sb.ToString();
    }

    private static string[] Files(string workDir) =>
        [Path.Combine(workDir, "config", "iris.properties"), Path.Combine(workDir, "config", "oculus.properties")];
}
