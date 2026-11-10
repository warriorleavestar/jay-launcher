using System.Globalization;
using Amber.Core.GameLibrary;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amber.App.Views;

/// <summary>
/// Automatic cover art (§6.2, §12 review): one palette per <see cref="CoverArt"/> slot, a diagonal gradient with a faint
/// isometric block pattern over it (original geometry, no game assets). These are artwork, not theme tokens, so they look
/// the same in light and dark themes; white initials on every colour stay at 4.5:1 or better (checked by a unit test).
/// </summary>
public sealed class CoverBrush : IValueConverter
{
    public static readonly CoverBrush Instance = new();

    /// <summary>
    /// The palettes, gradient from top-left to bottom-right. The count must equal <see cref="CoverArt.Slots"/>.
    /// The §12 review screenshotted 12 candidates in dark and light (gallery scope) and dropped three: slate (reads as a
    /// disabled card on the dark theme), deep sea (too close to prismarine) and copper (too close to clay).
    /// </summary>
    internal static readonly (string Name, string From, string To)[] Pairs =
    [
        ("moss", "#2F5D50", "#1E3A33"),
        ("clay", "#7A4A2A", "#4E2E1A"),
        ("lake", "#2C4A7A", "#1B2F50"),
        ("dusk", "#6B3A6E", "#432447"),
        ("amber ore", "#80520F", "#5A3A0B"),
        ("forest", "#3D5A2A", "#26391A"),
        ("nether brick", "#5A3030", "#3A1E1E"),
        ("prismarine", "#2F5A6B", "#1D3A47"),
        ("berry", "#6E2F4C", "#461D31"),
    ];

    /// <summary>Edge of one block of the pattern, in drawing units (the drawing is 320 × 200 and scaled to fill).</summary>
    private const double Edge = 22;
    private const double DrawingWidth = 320, DrawingHeight = 200;

    private static readonly IBrush[] Brushes = Pairs.Select(p => Build(Color.Parse(p.From), Color.Parse(p.To))).ToArray();

    public static IBrush For(int slot) => Brushes[((slot % Brushes.Length) + Brushes.Length) % Brushes.Length];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int slot ? For(slot) : Brushes[0];
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static IBrush Build(Color from, Color to)
    {
        var group = new DrawingGroup();
        var area = new RectangleGeometry(new Rect(0, 0, DrawingWidth, DrawingHeight));
        group.Children.Add(new GeometryDrawing
        {
            Geometry = area,
            Brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
            },
        });
        // Three faces of each block: lit top, plain left, shaded right. Faint, so initials and text stay readable.
        var (top, left, right) = (new StreamGeometry(), new StreamGeometry(), new StreamGeometry());
        using (var t = top.Open())
        using (var l = left.Open())
        using (var r = right.Open())
        {
            var w = Edge * Math.Sqrt(3);
            var row = 0;
            for (var cy = 0.0; cy < DrawingHeight + Edge; cy += Edge * 1.5, row++)
                for (var cx = row % 2 == 0 ? 0 : w / 2; cx < DrawingWidth + w; cx += w)
                {
                    Point c = new(cx, cy), n = new(cx, cy - Edge), s = new(cx, cy + Edge);
                    Point ne = new(cx + w / 2, cy - Edge / 2), se = new(cx + w / 2, cy + Edge / 2);
                    Point nw = new(cx - w / 2, cy - Edge / 2), sw = new(cx - w / 2, cy + Edge / 2);
                    Face(t, n, ne, c, nw);
                    Face(l, nw, c, s, sw);
                    Face(r, c, ne, se, s);
                }
        }
        group.Children.Add(new GeometryDrawing { Geometry = top, Brush = new SolidColorBrush(Colors.White, 0.07) });
        group.Children.Add(new GeometryDrawing { Geometry = left, Brush = new SolidColorBrush(Colors.White, 0.025) });
        group.Children.Add(new GeometryDrawing { Geometry = right, Brush = new SolidColorBrush(Colors.Black, 0.10) });
        group.ClipGeometry = area;
        return new DrawingBrush(group) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    }

    private static void Face(StreamGeometryContext g, Point a, Point b, Point c, Point d)
    {
        g.BeginFigure(a, true);
        g.LineTo(b);
        g.LineTo(c);
        g.LineTo(d);
        g.EndFigure(true);
    }

    /// <summary>Contrast of white text on <paramref name="hex"/> (WCAG 2), for the unit test and the review.</summary>
    internal static double WhiteContrast(string hex)
    {
        var c = Color.Parse(hex);
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var lum = 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        return 1.05 / (lum + 0.05);
    }
}
