using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Amber.App.Views.Controls;

/// <summary>
/// Jay's own line drawings (official §4.9): two colors from the theme (<c>IlloLine</c>, <c>IlloFill</c>), no text and
/// no faces, drawn on a 240×160 board and scaled to fit. Purely decorative; whatever they show is also said in words.
/// </summary>
public sealed class LineArt : Control
{
    public const double BoardWidth = 240, BoardHeight = 160;

    public static readonly StyledProperty<string?> PictureProperty = AvaloniaProperty.Register<LineArt, string?>(nameof(Picture));

    public string? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    static LineArt() => AffectsRender<LineArt>(PictureProperty);

    public LineArt()
    {
        // Theme switches and theme packs change the two brushes.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>True when there is a drawing for this id (the unit of "every lesson picture exists").</summary>
    public static bool Has(string id) => Drawings.ContainsKey(id);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Min(availableSize.Width, BoardWidth) : BoardWidth;
        var height = Math.Min(width * BoardHeight / BoardWidth, double.IsFinite(availableSize.Height) ? availableSize.Height : BoardHeight);
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        if (Picture is not { } id || !Drawings.TryGetValue(id, out var shapes)) return;
        var line = this.TryFindResource("IlloLine", ActualThemeVariant, out var l) && l is IBrush lb ? lb : Brushes.SteelBlue;
        var fill = this.TryFindResource("IlloFill", ActualThemeVariant, out var f) && f is IBrush fb ? fb : Brushes.Transparent;
        var scale = Math.Min(Bounds.Width / BoardWidth, Bounds.Height / BoardHeight);
        if (scale <= 0) return;
        var pen = new Pen(line, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var dx = (Bounds.Width - BoardWidth * scale) / 2;
        var dy = (Bounds.Height - BoardHeight * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            foreach (var (geometry, filled) in shapes.Value)
                context.DrawGeometry(filled ? fill : null, pen, geometry);
    }

    private static string C(double cx, double cy, double r) =>
        string.Create(CultureInfo.InvariantCulture, $"M{cx - r},{cy} a{r},{r} 0 1 0 {2 * r},0 a{r},{r} 0 1 0 {-2 * r},0 Z");

    /// <summary>Picture id → shapes (path data, filled?). Lines first where a filled shape should sit on top.</summary>
    private static readonly Dictionary<string, Lazy<(Geometry, bool)[]>> Drawings = new[]
    {
        // A room: your world, a locked channel, a friend's small world at the other end.
        ("room", new[] { ("M30,58 h80 v66 h-80 Z", true), ("M30,78 h80", false), ("M110,92 C140,52 170,52 196,92", false),
            ("M144,56 h22 v18 h-22 Z", true), ("M149,56 v-6 a6,6 0 0 1 12,0 v6", false), ("M188,92 h34 v32 h-34 Z", true), ("M188,102 h34", false) }),
        // An invite: a ticket with a code, and a paper plane carrying it away.
        ("invite", new[] { ("M30,52 h112 v58 h-112 Z", true), ("M118,52 v58", false), ("M44,72 h20 M70,72 h20 M96,72 h12", false), ("M44,90 h40", false),
            ("M160,78 L218,46 L200,104 L186,84 Z", true), ("M186,84 L218,46", false) }),
        // A knock: a door, and the code on screen next to the code read out (same marks, ticked).
        ("knock", new[] { ("M40,28 h64 v112 h-64 Z", true), ("M52,40 h40 v40 h-40 Z", false), (C(94, 92, 4), true),
            ("M128,50 h84 v24 h-84 Z", true), ("M138,62 h10 M154,62 h10 M170,62 h10 M186,62 h10", false),
            ("M128,88 h84 v24 h-84 Z", true), ("M138,100 h10 M154,100 h10 M170,100 h10 M186,100 h10", false),
            ("M180,128 l7,7 l16,-18", false) }),
        // Open to LAN: a screen with a menu and a switch turned on.
        ("lan", new[] { ("M40,28 h160 v92 h-160 Z", true), ("M120,120 v16 M96,138 h48", false), ("M72,44 h96 v62 h-96 Z", false),
            ("M84,58 h60 M84,68 h40", false), ("M88,84 h56 a8,8 0 0 1 0,16 h-56 a8,8 0 0 1 0,-16 Z", true), (C(144, 92, 5), true) }),
        // Trouble: two screens, a broken line between them, a magnifying glass.
        ("trouble", new[] { ("M24,52 h56 v44 h-56 Z", true), ("M44,96 v10 M36,108 h16", false), ("M160,52 h56 v44 h-56 Z", true), ("M188,96 v10 M180,108 h16", false),
            ("M80,74 h24 M136,74 h24", false), ("M112,62 l10,12 l-8,2 l10,14", false), (C(120, 122, 13), true), ("M129,131 l14,14", false) }),
        // Invites only to people you know: a ticket going to a single house with a heart.
        ("invite_friends", new[] { ("M24,62 h84 v46 h-84 Z", true), ("M38,80 h20 M64,80 h20", false), ("M120,85 h34 m-9,-9 l9,9 l-9,9", false),
            ("M168,84 l28,-24 l28,24 v42 h-56 Z", true), ("M196,110 c0,0 -12,-7 -12,-14 c0,-8 12,-8 12,0 c0,-8 12,-8 12,0 c0,7 -12,14 -12,14 Z", false) }),
        // Same code, then let in: two identical code boxes, an equals sign, a tick.
        ("codes", new[] { ("M24,50 h84 v32 h-84 Z", true), ("M34,66 h10 M50,66 h10 M66,66 h10 M82,66 h10", false),
            ("M132,50 h84 v32 h-84 Z", true), ("M142,66 h10 M158,66 h10 M174,66 h10 M190,66 h10", false),
            ("M113,61 h14 M113,71 h14", false), (C(120, 118, 18), true), ("M111,118 l6,6 l13,-14", false) }),
        // Remote control: a screen with a pointer, and a shield.
        ("remote", new[] { ("M28,36 h116 v80 h-116 Z", true), ("M86,116 v14 M66,132 h40", false),
            ("M76,54 L76,96 L86,86 L94,104 L101,101 L93,83 L107,83 Z", true),
            ("M188,44 l28,11 v24 c0,22 -14,33 -28,40 c-14,-7 -28,-18 -28,-40 v-24 Z", true), ("M177,80 l8,8 l15,-17", false) }),
        // Program files: a file with a gear, and a cross.
        ("program", new[] { ("M60,26 h62 l22,22 v94 h-84 Z", true), ("M122,26 v22 h22", false), (C(102, 96, 15), false), (C(102, 96, 5), true),
            ("M102,74 v6 M102,112 v6 M80,96 h6 M118,96 h6 M87,81 l4,4 M113,107 l4,4 M117,81 l-4,4 M91,107 l-4,4", false),
            (C(180, 112, 18), true), ("M171,103 l18,18 M189,103 l-18,18", false) }),
        // Public rooms: a list of rooms, and an eye looking at it.
        ("public", new[] { ("M24,32 h112 v100 h-112 Z", true), ("M36,52 h88 M36,74 h88 M36,96 h88 M36,118 h56", false),
            ("M150,82 c18,-26 56,-26 74,0 c-18,26 -56,26 -74,0 Z", true), (C(187, 82, 9), false) }),
    }.ToDictionary(p => p.Item1, p => new Lazy<(Geometry, bool)[]>(() => p.Item2.Select(s => ((Geometry)StreamGeometry.Parse(s.Item1), s.Item2)).ToArray()));
}
