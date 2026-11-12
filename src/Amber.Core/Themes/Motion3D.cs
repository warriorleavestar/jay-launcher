using System.Globalization;
using System.Text.Json;

namespace Amber.Core.Themes;

/// <summary>
/// Pseudo-3D motion (tokens.json "motion3d"): flat layers turned with a perspective transform, no 3D renderer and no
/// graphics card needed. A pack can only set these numbers, never code or shaders. Reduced motion and the "3D effects"
/// setting turn all of it off, and safety surfaces (knock, file offer, privacy row) never move.
/// </summary>
public sealed record Motion3D
{
    public const double DefaultTilt = 8;
    public const double MaxTilt = 15;
    public const double DefaultPageDepth = 0.015;
    public const double MaxPageDepth = 0.06;
    public const double DefaultParallax = 12;
    public const double MaxParallax = 32;
    public const double DefaultPerspective = 600;
    public const double MinPerspective = 300;
    public const double MaxPerspective = 4000;

    /// <summary>How far a card leans toward the pointer, in degrees (0 = cards stay flat).</summary>
    public double Tilt { get; init; } = DefaultTilt;

    /// <summary>How far behind the screen a new page starts, as a share of its size (0 = a plain fade).</summary>
    public double PageDepth { get; init; } = DefaultPageDepth;

    /// <summary>How far the nearest scene layer moves against the pointer, in pixels (0 = the scene stays still).</summary>
    public double Parallax { get; init; } = DefaultParallax;

    /// <summary>Distance from the eye to the screen in pixels: smaller looks deeper.</summary>
    public double Perspective { get; init; } = DefaultPerspective;

    /// <summary>Whether the skin figure on the "Me" page turns by itself while the pointer is on it.</summary>
    public bool Turntable { get; init; } = true;

    /// <summary>What Jay does when the theme says nothing.</summary>
    public static readonly Motion3D Default = new();

    /// <summary>Nothing moves: reduced motion, the setting turned off, or a pack that turns it all off.</summary>
    public static readonly Motion3D Flat = new() { Tilt = 0, PageDepth = 0, Parallax = 0, Turntable = false };

    public bool IsFlat => Tilt == 0 && PageDepth == 0 && Parallax == 0 && !Turntable;

    /// <summary>What applies now: the theme's numbers (or Jay's), unless 3D effects are off or motion is reduced.</summary>
    public static Motion3D Effective(Motion3D? themed, bool enabled, bool reduced) =>
        !enabled || reduced ? Flat : themed ?? Default;

    /// <summary>
    /// The lean of a card for a pointer at (<paramref name="x"/>, <paramref name="y"/>) inside a <paramref name="width"/> ×
    /// <paramref name="height"/> card: the side under the pointer goes back. AngleX turns around the horizontal axis
    /// (pointer near the top: positive), AngleY around the vertical axis (pointer near the right: positive).
    /// </summary>
    public static (double AngleX, double AngleY) TiltAngles(double x, double y, double width, double height, double max)
    {
        if (width <= 0 || height <= 0 || max <= 0) return (0, 0);
        var nx = Math.Clamp(x / width * 2 - 1, -1, 1);
        var ny = Math.Clamp(y / height * 2 - 1, -1, 1);
        return (-ny * max, nx * max);
    }

    /// <summary>
    /// How far a scene layer at <paramref name="depth"/> (0 = far, still; 1 = near) moves for a pointer at (x, y) in a
    /// window of the given size: against the pointer, at most <paramref name="max"/> pixels for the nearest layer.
    /// </summary>
    public static (double X, double Y) ParallaxOffset(double x, double y, double width, double height, double depth, double max)
    {
        if (width <= 0 || height <= 0 || max <= 0) return (0, 0);
        var nx = Math.Clamp(x / width * 2 - 1, -1, 1);
        var ny = Math.Clamp(y / height * 2 - 1, -1, 1);
        var d = Math.Clamp(depth, 0, 1);
        return (-nx * d * max, -ny * d * max);
    }

    private static readonly string[] Keys = ["tilt", "pageDepth", "parallax", "perspective", "turntable"];

    /// <summary>Reads tokens.json "motion3d". Numbers out of range are pulled in and noted; unknown keys are noted.</summary>
    internal static Motion3D Read(ThemePack pack, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) { pack.Problems.Add(new("bad_value", "motion3d")); return Default; }
        double num(string key, double fallback, double min, double max)
        {
            if (!e.TryGetProperty(key, out var v)) return fallback;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d))
            {
                pack.Problems.Add(new("bad_value", "motion3d." + key));
                return fallback;
            }
            var clamped = Math.Clamp(d, min, max);
            if (clamped != d) pack.Problems.Add(new("value_clamped", $"motion3d.{key} {d.ToString(CultureInfo.InvariantCulture)}"));
            return clamped;
        }
        foreach (var p in e.EnumerateObject())
            if (!Keys.Contains(p.Name)) pack.Problems.Add(new("unknown_key", "motion3d." + p.Name));
        var turntable = true;
        if (e.TryGetProperty("turntable", out var t))
        {
            if (t.ValueKind is JsonValueKind.True or JsonValueKind.False) turntable = t.GetBoolean();
            else pack.Problems.Add(new("bad_value", "motion3d.turntable"));
        }
        return new Motion3D
        {
            Tilt = num("tilt", DefaultTilt, 0, MaxTilt),
            PageDepth = num("pageDepth", DefaultPageDepth, 0, MaxPageDepth),
            Parallax = num("parallax", DefaultParallax, 0, MaxParallax),
            Perspective = num("perspective", DefaultPerspective, MinPerspective, MaxPerspective),
            Turntable = turntable,
        };
    }
}

/// <summary>One picture of a theme's scene, and how near it is (0 = far, does not move; 1 = nearest).</summary>
public sealed record SceneLayer(string Image, double Depth);

/// <summary>
/// The scene behind the pages (tokens.json "slots" → "scene"): up to <see cref="MaxLayers"/> pictures stacked far to
/// near that move apart a little with the pointer, so the window looks deep. Drawn where the background picture goes; the
/// theme's page colour covers it by <see cref="Dim"/> so text stays readable.
/// </summary>
public sealed record ThemeScene
{
    public const int MaxLayers = 4;
    public const double DefaultDim = 0.55;

    public required IReadOnlyList<SceneLayer> Layers { get; init; }
    public double Dim { get; init; } = DefaultDim;
    public double FocusX { get; init; } = 0.5;
    public double FocusY { get; init; } = 0.5;

    /// <summary>Reads the "scene" slot. A bad layer is reported and left out; a scene with no good layer is left out.</summary>
    internal static ThemeScene? Read(ThemePack pack, JsonElement s, Func<string, ThemeProblem?> checkImage)
    {
        const string where = "slots.scene";
        if (s.ValueKind != JsonValueKind.Object)
        {
            pack.Problems.Add(new("bad_value", where));
            return null;
        }
        // One picture ("image") is a one-layer scene halfway back; several go in "layers", far to near.
        IEnumerable<JsonElement> layers;
        if (s.TryGetProperty("layers", out var array) && array.ValueKind == JsonValueKind.Array) layers = array.EnumerateArray();
        else if (s.TryGetProperty("image", out _)) layers = [s];
        else
        {
            pack.Problems.Add(new("bad_value", where));
            return null;
        }
        var single = !s.TryGetProperty("layers", out _);
        var list = new List<SceneLayer>();
        var index = 0;
        foreach (var layer in layers)
        {
            var at = single ? where : $"{where}.layers[{index}]";
            index++;
            if (list.Count == MaxLayers) { pack.Problems.Add(new("too_many", at)); break; }
            if (layer.ValueKind != JsonValueKind.Object || !layer.TryGetProperty("image", out var img) || img.ValueKind != JsonValueKind.String)
            {
                pack.Problems.Add(new("bad_value", at));
                continue;
            }
            var image = img.GetString()!;
            if (checkImage(image) is { } error) { pack.Problems.Add(error); continue; }
            var depth = single ? 0.5 : list.Count == 0 ? 0 : 1.0;
            if (!single && layer.TryGetProperty("depth", out var dv))
            {
                if (dv.ValueKind == JsonValueKind.Number && dv.TryGetDouble(out var d) && double.IsFinite(d))
                {
                    depth = Math.Clamp(d, 0, 1);
                    if (depth != d) pack.Problems.Add(new("value_clamped", $"{at}.depth {d.ToString(CultureInfo.InvariantCulture)}"));
                }
                else pack.Problems.Add(new("bad_value", at + ".depth"));
            }
            list.Add(new SceneLayer(image, depth));
        }
        if (list.Count == 0) return null;
        var dim = DefaultDim;
        if (s.TryGetProperty("dim", out var dimValue))
        {
            if (dimValue.ValueKind == JsonValueKind.Number && dimValue.TryGetDouble(out var d) && double.IsFinite(d)) dim = Math.Clamp(d, 0, ThemeSlots.MaxDim);
            else pack.Problems.Add(new("bad_value", where + ".dim"));
        }
        var (fx, fy) = (0.5, 0.5);
        if (s.TryGetProperty("focus", out var focus))
        {
            if (focus.ValueKind == JsonValueKind.Array && focus.GetArrayLength() == 2
                && focus[0].TryGetDouble(out var x) && focus[1].TryGetDouble(out var y) && double.IsFinite(x) && double.IsFinite(y))
                (fx, fy) = (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
            else pack.Problems.Add(new("bad_value", where + ".focus"));
        }
        return new ThemeScene { Layers = list, Dim = dim, FocusX = fx, FocusY = fy };
    }
}
