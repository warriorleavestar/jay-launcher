using System.Globalization;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>
/// A picture in one of the theme's four image slots (design doc "Image themes"): the window background, the sidebar, the
/// mascot in a corner of the content area, and the banner behind the library's "continue" card and the room banner.
/// Every number is clamped on read, so a pack can't make a slot unreadable or cover the content.
/// </summary>
public sealed record ThemeSlot
{
    /// <summary>The picture inside the pack (assets/…, PNG or JPEG).</summary>
    public required string Image { get; init; }

    /// <summary>How much of the theme's own colour covers the picture, so text on it stays readable (background, sidebar).</summary>
    public double Dim { get; init; }

    /// <summary>Blur radius in pixels (background only).</summary>
    public double Blur { get; init; }

    /// <summary>The point of the picture that stays in view when it is cropped to fill, 0..1 from the left and the top.</summary>
    public double FocusX { get; init; } = 0.5;
    public double FocusY { get; init; } = 0.5;

    /// <summary>Mascot: its height as a share of the content area's height.</summary>
    public double Size { get; init; } = ThemeSlots.DefaultMascotSize;

    /// <summary>Mascot: "right" or "left" corner.</summary>
    public string Side { get; init; } = "right";

    /// <summary>Mascot: how solid it is.</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>The image slots of tokens.json ("slots"): names, limits, reading and writing.</summary>
public static class ThemeSlots
{
    public const string Background = "background";
    public const string Sidebar = "sidebar";
    public const string Mascot = "mascot";
    public const string Banner = "banner";

    /// <summary>In the order the "make a theme from pictures" panel lists them.</summary>
    public static readonly string[] All = [Background, Sidebar, Mascot, Banner];

    /// <summary>Reserved for a later version: a 3D or pseudo-3D scene behind the pages. Read past without a problem.</summary>
    public const string Scene = "scene";
    public static readonly string[] Reserved = [Scene];

    public const double MaxDim = 0.9;
    public const double MaxBlur = 40;
    public const double MinMascotSize = 0.15;
    public const double MaxMascotSize = 0.8;
    public const double DefaultMascotSize = 0.45;
    public const double MinMascotOpacity = 0.2;

    /// <summary>What a new slot starts with in the panel: enough cover that text on the picture reads well.</summary>
    public static double DefaultDim(string slot) => slot switch
    {
        Background => 0.55,
        Sidebar => 0.6,
        _ => 0,
    };

    /// <summary>Reads tokens.json "slots". A bad slot is reported and left out; the rest of the pack still loads.</summary>
    internal static void Read(ThemePack pack, JsonElement slots, Func<string, ThemeProblem?> checkImage)
    {
        if (slots.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", "slots")); return; }
        foreach (var p in slots.EnumerateObject())
        {
            var where = "slots." + p.Name;
            if (Reserved.Contains(p.Name)) { pack.ReservedKeys.Add(where); continue; }
            if (!All.Contains(p.Name)) { pack.Problems.Add(new("unknown_key", where)); continue; }
            var s = p.Value;
            if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("image", out var img) || img.ValueKind != JsonValueKind.String)
            {
                pack.Problems.Add(new("bad_value", where));
                continue;
            }
            var image = img.GetString()!;
            if (checkImage(image) is { } error) { pack.Problems.Add(error); continue; }
            double num(string key, double fallback, double min, double max)
            {
                if (!s.TryGetProperty(key, out var v)) return fallback;
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d))
                {
                    pack.Problems.Add(new("bad_value", $"{where}.{key}"));
                    return fallback;
                }
                var clamped = Math.Clamp(d, min, max);
                if (clamped != d) pack.Problems.Add(new("value_clamped", $"{where}.{key} {d.ToString(CultureInfo.InvariantCulture)}"));
                return clamped;
            }
            var (fx, fy) = (0.5, 0.5);
            if (s.TryGetProperty("focus", out var focus))
            {
                if (focus.ValueKind == JsonValueKind.Array && focus.GetArrayLength() == 2
                    && focus[0].TryGetDouble(out var x) && focus[1].TryGetDouble(out var y) && double.IsFinite(x) && double.IsFinite(y))
                    (fx, fy) = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
                else pack.Problems.Add(new("bad_value", where + ".focus"));
            }
            var side = "right";
            if (s.TryGetProperty("side", out var sv))
            {
                if (sv.ValueKind == JsonValueKind.String && sv.GetString() is "left" or "right") side = sv.GetString()!;
                else pack.Problems.Add(new("bad_value", where + ".side"));
            }
            pack.Slots[p.Name] = new ThemeSlot
            {
                Image = image,
                Dim = num("dim", 0, 0, MaxDim),
                Blur = p.Name == Background ? num("blur", 0, 0, MaxBlur) : 0,
                FocusX = fx,
                FocusY = fy,
                Size = p.Name == Mascot ? num("size", DefaultMascotSize, MinMascotSize, MaxMascotSize) : DefaultMascotSize,
                Side = side,
                Opacity = p.Name == Mascot ? num("opacity", 1, MinMascotOpacity, 1) : 1,
            };
        }
    }

    /// <summary>Writes the "slots" object (only what each slot uses).</summary>
    public static void Write(Utf8JsonWriter w, IReadOnlyDictionary<string, ThemeSlot> slots)
    {
        w.WriteStartObject("slots");
        foreach (var name in All)
        {
            if (!slots.TryGetValue(name, out var slot)) continue;
            w.WriteStartObject(name);
            w.WriteString("image", slot.Image);
            if (name is Background or Sidebar) w.WriteNumber("dim", Round(slot.Dim));
            if (name == Background) w.WriteNumber("blur", Round(slot.Blur));
            if (name != Mascot)
            {
                w.WriteStartArray("focus");
                w.WriteNumberValue(Round(slot.FocusX));
                w.WriteNumberValue(Round(slot.FocusY));
                w.WriteEndArray();
            }
            if (name == Mascot)
            {
                w.WriteNumber("size", Round(slot.Size));
                w.WriteString("side", slot.Side);
                w.WriteNumber("opacity", Round(slot.Opacity));
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static double Round(double value) => Math.Round(value, 3);
}
