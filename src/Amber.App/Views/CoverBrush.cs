using System.Globalization;
using Amber.Core.GameLibrary;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amber.App.Views;

/// <summary>
/// Automatic cover art (§6.2; pixel covers from the 2026-11 art direction): one small night landscape per
/// <see cref="CoverArt"/> slot, drawn from the rectangles of <see cref="PixelCover"/>. These are artwork, not theme tokens,
/// so they look the same in light and dark themes; white initials stay readable on every large colour (unit test).
/// </summary>
public sealed class CoverBrush : IValueConverter
{
    public static readonly CoverBrush Instance = new();

    private static readonly IBrush[] Brushes = Enumerable.Range(0, PixelCover.Palettes.Count).Select(slot => PixelBrush.Build(PixelCover.Layers(slot), PixelCover.Width, PixelCover.Height)).ToArray();

    public static IBrush For(int slot) => Brushes[((slot % Brushes.Length) + Brushes.Length) % Brushes.Length];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int slot ? For(slot) : Brushes[0];
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A person's pixel face (<see cref="PixelFace"/>) as a brush, from their name: <c>Background="{Binding Name, Converter={x:Static v:FaceBrush.Instance}}"</c>
/// on a square Border. Sizes that are a multiple of 8 keep every cell on whole pixels.
/// </summary>
public sealed class FaceBrush : IValueConverter
{
    public static readonly FaceBrush Instance = new();

    private static readonly Dictionary<string, IBrush> Cache = [];

    public static IBrush For(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            // A long-running room sees many names; the brushes are small, but don't keep them for ever.
            if (Cache.Count >= 512) Cache.Clear();
            var cells = PixelFace.Cells(name);
            // Skin first, over the whole square: at sizes where a cell is not a whole number of pixels, the soft edge
            // between two cells then shows skin, never the background behind the face.
            var skin = cells[3 * PixelFace.Size + 3];
            var layers = new List<PixelLayer> { new(skin, [new PixelBlock(0, 0, PixelFace.Size, PixelFace.Size)]) };
            layers.AddRange(cells.Select((color, i) => (color, i)).Where(c => c.color != skin).GroupBy(c => c.color)
                .Select(g => new PixelLayer(g.Key, g.Select(c => new PixelBlock(c.i % PixelFace.Size, c.i / PixelFace.Size, 1, 1)).ToList())));
            return Cache[name] = PixelBrush.Build(layers, PixelFace.Size, PixelFace.Size);
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => For(value as string ?? "");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Turns the layers of a pixel drawing into one brush: a geometry per layer, so cells of one colour never show seams.</summary>
internal static class PixelBrush
{
    public static IBrush Build(IReadOnlyList<PixelLayer> layers, int width, int height)
    {
        var group = new DrawingGroup();
        var area = new Rect(0, 0, width, height);
        foreach (var layer in layers)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                // Non-zero winding: overlapping rectangles of one layer stay filled.
                g.SetFillRule(FillRule.NonZero);
                foreach (var r in layer.Rects)
                {
                    var rect = new Rect(r.X, r.Y, r.Width, r.Height).Intersect(area);
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    g.BeginFigure(rect.TopLeft, true);
                    g.LineTo(rect.TopRight);
                    g.LineTo(rect.BottomRight);
                    g.LineTo(rect.BottomLeft);
                    g.EndFigure(true);
                }
            }
            group.Children.Add(new GeometryDrawing { Geometry = geometry, Brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(layer.Color)) });
        }
        group.ClipGeometry = new RectangleGeometry(area);
        return new DrawingBrush(group) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
    }
}
