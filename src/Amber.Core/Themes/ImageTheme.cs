using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>Colours picked from the theme's pictures: an accent, a page colour for each variant, and a few alternatives to choose from.</summary>
public sealed record ImagePalette(uint Accent, uint DarkBackground, uint LightBackground, IReadOnlyList<uint> Swatches);

/// <summary>One picture going into a slot of a new theme: the slot settings and the encoded image (PNG or JPEG).</summary>
public sealed record ImageThemeSlot(ThemeSlot Settings, byte[] Bytes, string Extension);

/// <summary>
/// "Make a theme from pictures" (design doc "Image themes"): pick colours from the pictures and write a format 2 .jaytheme
/// with the colours and the four image slots. The start button keeps Jay's amber, so it is still the one thing to press.
/// The app decodes and scales the pictures; this class only sees pixels and encoded bytes, so it has no UI dependencies.
/// </summary>
public static class ImageTheme
{
    /// <summary>Longest side each slot's picture is scaled down to before it goes into the pack.</summary>
    public static int MaxSide(string slot) => slot switch
    {
        ThemeSlots.Sidebar => 800,
        ThemeSlots.Mascot => 1200,
        _ => 1920,
    };

    private const int HueBins = 36;

    /// <summary>
    /// Picks the palette from pixels (ARGB, any order; a 64×64 thumbnail is plenty). The accent is the most vivid hue that
    /// covers a fair share of the picture; the page colours take the picture's most common hue, kept dark (or light) and
    /// quiet so text stays readable. A grey picture keeps Jay's blue accent.
    /// </summary>
    public static ImagePalette Extract(ReadOnlySpan<uint> argb)
    {
        var vivid = new double[HueBins];
        var count = new double[HueBins];
        var sums = new (double R, double G, double B, double W)[HueBins];
        double total = 0, totalVivid = 0;
        foreach (var px in argb)
        {
            if (ThemeColor.A(px) < 128) continue;
            var (h, s, l) = ToHsl(px);
            total++;
            if (s < 0.08) continue;
            var bin = (int)(h / 360 * HueBins) % HueBins;
            // Vivid = saturated and away from black and white; those make a good accent.
            var w = s * (1 - Math.Abs(2 * l - 1));
            vivid[bin] += w;
            totalVivid += w;
            count[bin] += 1;
            sums[bin] = (sums[bin].R + ThemeColor.R(px) * w, sums[bin].G + ThemeColor.G(px) * w, sums[bin].B + ThemeColor.B(px) * w, sums[bin].W + w);
        }

        var defaultAccent = ThemeDefaults.Dark["Accent"];
        if (total == 0 || totalVivid / total < 0.04)
        {
            // A grey or nearly empty picture: Jay's blue with neutral pages.
            return new ImagePalette(defaultAccent, ThemeDefaults.Dark["PageBg"], ThemeDefaults.Light["PageBg"], [defaultAccent]);
        }

        uint binColor(int bin)
        {
            var (r, g, b, w) = sums[bin];
            return ThemeColor.Argb(255, (byte)Math.Round(r / w), (byte)Math.Round(g / w), (byte)Math.Round(b / w));
        }

        // Neighbouring bins belong to the same colour; score each bin with half of each neighbour so one hue split over
        // two bins still wins.
        double score(int i) => vivid[i] + 0.5 * (vivid[(i + 1) % HueBins] + vivid[(i + HueBins - 1) % HueBins]);
        var ranked = Enumerable.Range(0, HueBins).Where(i => vivid[i] > 0).OrderByDescending(score).ToList();
        var swatches = new List<uint>();
        var usedHues = new List<double>();
        foreach (var bin in ranked)
        {
            var c = AccentFrom(binColor(bin));
            var hue = ToHsl(c).H;
            if (usedHues.Any(u => HueDistance(u, hue) < 25)) continue;
            usedHues.Add(hue);
            swatches.Add(c);
            if (swatches.Count == 5) break;
        }
        var accent = swatches[0];

        // Page colour: the hue that covers most of the picture, so the window feels like the picture around it.
        var mainBin = Enumerable.Range(0, HueBins).OrderByDescending(i => count[i]).First();
        var pageHue = ToHsl(binColor(mainBin)).H;
        var pageSat = Math.Min(0.3, ToHsl(binColor(mainBin)).S);
        return new ImagePalette(accent, FromHsl(pageHue, pageSat, 0.09), FromHsl(pageHue, pageSat, 0.95), swatches);
    }

    /// <summary>Turns a picked colour into a usable accent: saturated enough to read as a colour, not too dark or light for buttons.</summary>
    internal static uint AccentFrom(uint color)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(h, Math.Max(s, 0.45), Math.Clamp(l, 0.42, 0.62));
    }

    /// <summary>
    /// The colour tokens of both variants for a palette (anything not set falls back to Jay's own colours), with the same
    /// contrast fixes a pack gets when it is read already applied, so the pack it writes reads without remarks.
    /// </summary>
    public static (Dictionary<string, uint> Dark, Dictionary<string, uint> Light) Colors(ImagePalette palette)
    {
        var probe = new ThemePack { Id = "image-theme", Name = "image-theme" };
        foreach (var (k, v) in ThemeColor.Derive(palette.Accent, palette.DarkBackground, dark: true)) probe.DarkColors[k] = v;
        foreach (var (k, v) in ThemeColor.Derive(palette.Accent, palette.LightBackground, dark: false)) probe.LightColors[k] = v;
        ThemeContrast.Enforce(probe);
        return (probe.DarkColors, probe.LightColors);
    }

    /// <summary>Makes a valid theme id from a name: lower-case letters and digits, with a short random tail so two packs don't clash.</summary>
    public static string IdFor(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 32) break;
        }
        var stem = sb.ToString().Trim('-');
        if (stem.Length < 2) stem = "picture-theme";
        return $"{stem}-{Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 3).ToLowerInvariant()}";
    }

    /// <summary>
    /// Writes the .jaytheme and reads it back with the import rules (a pack that would not import is deleted and the reason
    /// thrown). The background slot is also written as the format 1 "background" key, so an older Jay still shows it.
    /// </summary>
    public static ThemePack Write(string path, string id, string name, ImagePalette palette, IReadOnlyDictionary<string, ImageThemeSlot> slots)
    {
        if (!ThemePackReader.IsValidId(id)) throw new ThemePackException("bad_id", id);
        if (SafetyWords.Contains(name)) throw new ThemePackException("reserved_words", "name");
        var (dark, light) = Colors(palette);
        var files = new List<(string Name, byte[] Bytes)>();
        var placed = new Dictionary<string, ThemeSlot>();
        foreach (var slot in ThemeSlots.All)
        {
            if (!slots.TryGetValue(slot, out var s)) continue;
            var ext = s.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";
            var file = $"assets/{slot}{ext}";
            files.Add((file, s.Bytes));
            placed[slot] = s.Settings with { Image = file };
        }

        var options = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] json(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, options)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            return ms.ToArray();
        }
        files.Insert(0, ("theme.json", json(w =>
        {
            w.WriteNumber("format", ThemePackReader.FormatVersion);
            w.WriteString("id", id);
            w.WriteString("name", name);
            w.WriteString("version", "1.0.0");
        })));
        files.Insert(1, ("tokens.json", json(w =>
        {
            w.WriteStartObject("colors");
            foreach (var (key, set) in new[] { ("dark", dark), ("light", light) })
            {
                w.WriteStartObject(key);
                foreach (var k in ThemePackReader.ColorKeys)
                    if (set.TryGetValue(k, out var c)) w.WriteString(ThemeFormat.PublicColorName(k), ThemePackReader.FormatColor(c));
                w.WriteEndObject();
            }
            w.WriteEndObject();
            if (placed.TryGetValue(ThemeSlots.Background, out var bg))
            {
                w.WriteStartObject("background");
                w.WriteString("image", bg.Image);
                w.WriteNumber("opacity", Math.Round(1 - bg.Dim, 3));
                w.WriteEndObject();
            }
            if (placed.Count > 0) ThemeSlots.Write(w, placed);
        })));
        files.Add(("README.txt", Encoding.UTF8.GetBytes(Readme)));

        var temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            // Inside the try: a write that fails (a full disk, say) leaves no .tmp behind.
            using (var file = CreateTempFile(temp))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
                foreach (var (entryName, bytes) in files)
                {
                    using var stream = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                    stream.Write(bytes);
                }
            var pack = ThemePackReader.Read(temp);
            if (pack.Problems.FirstOrDefault(p => p.Code is "bad_image" or "image_too_large" or "missing_file" or "bad_asset_path") is { } problem)
                throw new ThemePackException(problem.Code, problem.Detail);
            File.Move(temp, path, true);
            return pack;
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Opens the pack's temporary file (tests swap in a stream that fails part way, like a full disk).</summary>
    internal static Func<string, Stream> CreateTempFile { get; set; } = File.Create;

    private const string Readme =
        "Jay theme pack made from pictures. tokens.json holds the colours and the image slots (background, sidebar, mascot,\n" +
        "banner); the pictures are in assets/. Edit, zip the folder and import it in Jay settings. See docs/themes.md.\n";

    private static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    internal static (double H, double S, double L) ToHsl(uint c)
    {
        double r = ThemeColor.R(c) / 255.0, g = ThemeColor.G(c) / 255.0, b = ThemeColor.B(c) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    internal static uint FromHsl(double h, double s, double l)
    {
        static double channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s <= 0) r = g = b = l;
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            var hk = h / 360;
            r = channel(p, q, hk + 1.0 / 3);
            g = channel(p, q, hk);
            b = channel(p, q, hk - 1.0 / 3);
        }
        static byte to(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return ThemeColor.Argb(255, to(r), to(g), to(b));
    }
}
