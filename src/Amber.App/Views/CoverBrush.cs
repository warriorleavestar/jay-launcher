using System.Globalization;
using Amber.Core.GameLibrary;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amber.App.Views;

/// <summary>
/// Automatic cover art (§6.2): a fixed gradient per <see cref="CoverArt"/> slot. These are artwork, not theme tokens, so they look
/// the same in light and dark themes; white initials on them stay above 4.5:1.
/// </summary>
public sealed class CoverBrush : IValueConverter
{
    public static readonly CoverBrush Instance = new();

    private static readonly (string From, string To)[] Pairs =
    [
        ("#2F5D50", "#1E3A33"), // moss
        ("#7A4A2A", "#4E2E1A"), // clay
        ("#2C4A7A", "#1B2F50"), // lake
        ("#6B3A6E", "#432447"), // dusk
        ("#8A5A12", "#5A3A0B"), // amber ore
        ("#3D5A2A", "#26391A"), // forest
        ("#5A3030", "#3A1E1E"), // nether brick
        ("#365C6E", "#203A47"), // prismarine
    ];

    private static readonly IBrush[] Brushes = Pairs.Select(p => (IBrush)new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(p.From), 0), new GradientStop(Color.Parse(p.To), 1) },
    }.ToImmutable()).ToArray();

    public static IBrush For(int slot) => Brushes[((slot % Brushes.Length) + Brushes.Length) % Brushes.Length];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int slot ? For(slot) : Brushes[0];
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
