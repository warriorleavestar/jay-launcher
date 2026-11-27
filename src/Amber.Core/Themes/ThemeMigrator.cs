using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>
/// <c>theme-migrate</c> (official §10.9): writes the in-memory upgrade of a pack as a new format 2 pack. theme.json,
/// tokens.json, styles.json and layout/shell.json are written from what Jay read (format 2 names, automatic fixes
/// included); every other file (fonts, images, page layouts, notes) is copied as it is. The result is read back with
/// the import rules before it is kept.
/// </summary>
public static class ThemeMigrator
{
    public static ThemePack Migrate(string input, string output)
    {
        var files = Directory.Exists(input) ? ReadDirectoryFiles(input) : ReadPackFiles(input);
        var pack = ThemePackReader.ParseFiles(files);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, bytes) in files)
            if (name is not ("theme.json" or "tokens.json" or "styles.json" or "layout/shell.json")) result[name] = bytes;
        result["theme.json"] = Json(w => WriteManifest(w, pack));
        result["tokens.json"] = Json(w => WriteTokens(w, pack));
        if (pack.Styles.Count > 0) result["styles.json"] = Json(w => WriteStyles(w, pack));
        if (pack.Shell is { } shell) result["layout/shell.json"] = Json(w => WriteShell(w, shell));

        var temp = output + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            foreach (var (name, bytes) in result.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(bytes);
            }
        try
        {
            var migrated = ThemePackReader.Read(temp);
            File.Move(temp, output, true);
            return migrated;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private static Dictionary<string, byte[]> ReadPackFiles(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ThemePackException("not_found", path);
        if (info.Length > ThemePackReader.MaxPackBytes) throw new ThemePackException("pack_too_large", $"{info.Length}");
        using var stream = info.OpenRead();
        return ThemePackReader.ReadEntries(stream);
    }

    /// <summary>A theme folder as the zip it would become: hidden files skipped, the same limits as an import.</summary>
    private static Dictionary<string, byte[]> ReadDirectoryFiles(string directory)
    {
        var root = Path.GetFullPath(directory);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part.StartsWith('.'))) continue;
                zip.CreateEntryFromFile(file, relative, CompressionLevel.NoCompression);
            }
        ms.Position = 0;
        return ThemePackReader.ReadEntries(ms);
    }

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteManifest(Utf8JsonWriter w, ThemePack pack)
    {
        w.WriteNumber("format", ThemePackReader.FormatVersion);
        w.WriteString("id", pack.Id);
        w.WriteString("name", pack.Name);
        if (pack.Author is { } author) w.WriteString("author", author);
        w.WriteString("version", pack.Version);
        if (pack.MinJay is { } minJay) w.WriteString("minJay", minJay);
    }

    private static void WriteTokens(Utf8JsonWriter w, ThemePack pack)
    {
        w.WriteString("variants", pack.Variants);
        w.WriteStartObject("colors");
        foreach (var (key, set) in new[] { ("dark", pack.DarkColors), ("light", pack.LightColors) })
        {
            if (set.Count == 0 || (pack.Variants == "dark" && key == "light") || (pack.Variants == "light" && key == "dark")) continue;
            w.WriteStartObject(key);
            foreach (var k in ThemePackReader.ColorKeys)
                if (set.TryGetValue(k, out var c)) w.WriteString(ThemeFormat.PublicColorName(k), ThemePackReader.FormatColor(c));
            w.WriteEndObject();
        }
        w.WriteEndObject();
        if (pack.Numbers.Count > 0)
        {
            w.WriteStartObject("sizes");
            foreach (var (k, v) in pack.Numbers.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteNumber(ThemeFormat.PublicNumberName(k), v);
            w.WriteEndObject();
        }
        if (pack.Radii.Count > 0)
        {
            w.WriteStartObject("radius");
            foreach (var (k, v) in pack.Radii.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteNumber(k, v);
            w.WriteEndObject();
        }
        if (pack.Durations.Count > 0)
        {
            w.WriteStartObject("motion");
            foreach (var (name, (resource, _)) in ThemeFormat.MotionKeys)
                if (pack.Durations.TryGetValue(resource, out var ms)) w.WriteNumber(name, ms);
            w.WriteEndObject();
        }
        if (pack.UiFonts.Count > 0 || pack.MonoFont is not null)
        {
            w.WriteStartObject("fonts");
            if (pack.UiFonts.Count > 0)
            {
                w.WriteStartObject("ui");
                foreach (var (lang, font) in pack.UiFonts) w.WriteString(lang, font);
                w.WriteEndObject();
            }
            if (pack.MonoFont is { } mono) w.WriteString("mono", mono);
            w.WriteEndObject();
        }
        if (pack.BackgroundImage is { } image)
        {
            w.WriteStartObject("background");
            w.WriteString("image", image);
            w.WriteNumber("opacity", pack.BackgroundOpacity);
            w.WriteEndObject();
        }
        if (pack.HeroMode is not null || pack.HeroScrim is not null)
        {
            w.WriteStartObject("hero");
            if (pack.HeroMode is { } mode) w.WriteString("mode", mode);
            if (pack.HeroScrim is { } scrim) w.WriteNumber("scrim", scrim);
            w.WriteEndObject();
        }
    }

    private static void WriteStyles(Utf8JsonWriter w, ThemePack pack)
    {
        foreach (var (component, states) in pack.Styles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            w.WriteStartObject(component);
            if (states.TryGetValue("", out var normal)) WriteProps(w, normal);
            foreach (var (state, props) in states.Where(s => s.Key != "").OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(state);
                WriteProps(w, props);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
    }

    private static void WriteProps(Utf8JsonWriter w, StyleProps p)
    {
        void paint(string name, Paint? value)
        {
            if (value is not null) w.WriteString(name, PaintText(value));
        }
        paint("background", p.Background);
        paint("foreground", p.Foreground);
        paint("border", p.Border);
        if (p.BorderWidth is { } bw) w.WriteNumber("borderWidth", bw);
        if (p.Radius is { } r) w.WriteNumber("radius", r);
        if (p.Padding is { } pad)
        {
            w.WriteStartArray("padding");
            w.WriteNumberValue(pad.H);
            w.WriteNumberValue(pad.V);
            w.WriteEndArray();
        }
        if (p.FontWeight is { } weight) w.WriteString("fontWeight", weight);
        if (p.Shadow is { } shadow) w.WriteString("shadow", shadow);
        if (p.Glow is { } glow) w.WriteString("glow", glow);
        paint("indicator", p.Indicator);
        if (p.IndicatorHeight is { } ih) w.WriteNumber("indicatorHeight", ih);
        paint("track", p.Track);
        paint("fill", p.Fill);
        if (p.Scrim is { } scrim) w.WriteNumber("scrim", scrim);
    }

    public static string PaintText(Paint paint) => paint.Kind switch
    {
        PaintKind.Color => ThemePackReader.FormatColor(paint.Colors[0]),
        PaintKind.Token => "token:" + ThemeFormat.PublicColorName(paint.Token!),
        PaintKind.Linear => $"linear({paint.Angle.ToString(CultureInfo.InvariantCulture)}, {string.Join(", ", paint.Colors.Select(ThemePackReader.FormatColor))})",
        PaintKind.Image => "image:" + paint.ImagePath,
        _ => throw new ArgumentOutOfRangeException(nameof(paint)),
    };

    private static void WriteShell(Utf8JsonWriter w, ShellLayout shell)
    {
        w.WriteString("nav", shell.Nav);
        w.WriteString("navStyle", shell.NavStyle);
        if (shell.SidebarWidth is { } sw) w.WriteNumber("sidebarWidth", sw);
        if (shell.RailWidth is { } rw) w.WriteNumber("railWidth", rw);
        w.WriteBoolean("showBrand", shell.ShowBrand);
        w.WriteBoolean("showShelf", shell.ShowShelf);
        if (shell.ContentMaxWidth is { } cw) w.WriteNumber("contentMaxWidth", cw);
        w.WriteStartArray("pages");
        foreach (var s in shell.Sections) w.WriteStringValue(s);
        w.WriteEndArray();
        if (shell.Hidden.Count > 0)
        {
            w.WriteStartArray("hidden");
            foreach (var h in shell.Hidden) w.WriteStringValue(h);
            w.WriteEndArray();
        }
    }
}

/// <summary>
/// The themes that ship inside Jay (official §10.9): “Jay 经典琥珀” (0.7's warm amber look) and “高对比” (body text ≥ 7:1,
/// thicker outlines). They are ordinary format 2 packs embedded in Amber.Core; they can be chosen and exported like any
/// other pack, but not removed or replaced by an import.
/// </summary>
public static class BuiltInThemes
{
    public const string ClassicAmber = "jay-classic-amber";
    public const string HighContrast = "jay-high-contrast";
    public static readonly string[] Ids = [ClassicAmber, HighContrast];

    private const string Prefix = "themes/builtin/";
    private static readonly Lazy<Dictionary<string, Dictionary<string, byte[]>>> Files = new(LoadFiles);

    public static bool IsBuiltIn(string id) => Ids.Contains(id);

    /// <summary>A fresh read of a built-in pack (callers may change what they get), or null for other ids.</summary>
    public static ThemePack? Load(string id) =>
        Files.Value.TryGetValue(id, out var files) ? ThemePackReader.ParseFiles(files) : null;

    private static Dictionary<string, Dictionary<string, byte[]>> LoadFiles()
    {
        var result = new Dictionary<string, Dictionary<string, byte[]>>();
        var assembly = typeof(BuiltInThemes).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // RecursiveDir uses the build machine's separator: normalize before splitting.
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            var rest = name[Prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var folder = rest[..slash];
            if (!result.TryGetValue(folder, out var files)) result[folder] = files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            files[rest[(slash + 1)..]] = ms.ToArray();
        }
        // Keyed by the id inside theme.json, not the folder name.
        return result.Values
            .Select(f => (Id: ThemePackReader.ParseFiles(f).Id, Files: f))
            .ToDictionary(p => p.Id, p => p.Files);
    }
}
