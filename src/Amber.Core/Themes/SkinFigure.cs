namespace Amber.Core.Themes;

/// <summary>
/// A skin picture in memory: opaque-or-transparent ARGB pixels, row by row. Minecraft skins are 64 × 64 (or the older
/// 64 × 32) at any whole multiple; <see cref="Scale"/> is that multiple.
/// </summary>
public sealed record SkinTexture(uint[] Pixels, int Width, int Height, bool Slim)
{
    public int Scale => Width / 64;

    /// <summary>The older 64 × 32 layout: no second layer except the hat, and the left arm and leg mirror the right ones.</summary>
    public bool Legacy => Height * 2 == Width;

    /// <summary>A usable skin size: 64 wide (or a whole multiple), square or half as tall.</summary>
    public static bool Fits(int width, int height) => width >= 64 && width % 64 == 0 && (height == width || height * 2 == width);

    /// <summary>The pixel at skin coordinates (in 64-wide units), or transparent outside the picture.</summary>
    public uint At(int u, int v)
    {
        var s = Scale;
        var x = u * s;
        var y = v * s;
        return x < 0 || y < 0 || x >= Width || y >= Height ? 0 : Pixels[y * Width + x];
    }
}

/// <summary>
/// Turns a skin into a standing figure seen from any side, as flat pixels: pseudo-3D, no graphics card. Each body part
/// is a box; the figure turns around its upright axis, each side of a box that faces the viewer is squeezed to its
/// on-screen width (straight-on projection) and painted far to near. Original code from the public skin layout.
/// </summary>
public static class SkinTurntable
{
    /// <summary>The figure's size in skin pixels: wide enough for any turn, tall enough for the hat layer.</summary>
    public const int UnitsWide = 18;
    public const int UnitsTall = 33;

    /// <summary>The angle the figure rests at when nobody turns it: a little to one side, so it reads as solid.</summary>
    public const double RestAngle = -25;

    private const double Top = 0.5; // room above the head for the hat layer
    private const double CenterX = 8;

    private readonly record struct Box(double X0, double Y0, double Z0, double W, double H, double D, int U, int V, bool Mirror, double Grow, bool Layer);

    private readonly record struct Face(
        double Ax, double Az, double Bx, double Bz, double Y0, double H,
        int Tu, int Tv, int Tw, int Th, bool Flip, double Nx, double Nz, bool Layer);

    /// <summary>The boxes of the figure, base parts first, then the second (outer) layer.</summary>
    private static List<Box> Boxes(SkinTexture skin)
    {
        var arm = skin.Slim ? 3 : 4;
        var boxes = new List<Box>
        {
            new(4, 0, -4, 8, 8, 8, 0, 0, false, 0, false),            // head
            new(4, 8, -2, 8, 12, 4, 16, 16, false, 0, false),         // body
            new(4 - arm, 8, -2, arm, 12, 4, 40, 16, false, 0, false), // right arm (the viewer's left)
            new(4, 20, -2, 4, 12, 4, 0, 16, false, 0, false),         // right leg
            skin.Legacy ? new(12, 8, -2, arm, 12, 4, 40, 16, true, 0, false) : new(12, 8, -2, arm, 12, 4, 32, 48, false, 0, false),
            skin.Legacy ? new(8, 20, -2, 4, 12, 4, 0, 16, true, 0, false) : new(8, 20, -2, 4, 12, 4, 16, 48, false, 0, false),
        };
        if (!skin.Legacy || !Opaque(skin, 32, 0, 32, 16))
            boxes.Add(new(4, 0, -4, 8, 8, 8, 32, 0, false, 0.5, true)); // hat
        if (!skin.Legacy)
        {
            boxes.Add(new(4, 8, -2, 8, 12, 4, 16, 32, false, 0.25, true));
            boxes.Add(new(4 - arm, 8, -2, arm, 12, 4, 40, 32, false, 0.25, true));
            boxes.Add(new(12, 8, -2, arm, 12, 4, 48, 48, false, 0.25, true));
            boxes.Add(new(4, 20, -2, 4, 12, 4, 0, 32, false, 0.25, true));
            boxes.Add(new(8, 20, -2, 4, 12, 4, 0, 48, false, 0.25, true));
        }
        return boxes;
    }

    /// <summary>Old skins often fill the hat area solid; the game then treats it as empty, and so does this.</summary>
    private static bool Opaque(SkinTexture skin, int u, int v, int w, int h)
    {
        for (var y = v; y < v + h; y++)
            for (var x = u; x < u + w; x++)
                if (skin.At(x, y) >> 24 < 128) return false;
        return true;
    }

    /// <summary>The four upright sides of a box, each with where its texture's first column starts.</summary>
    private static IEnumerable<Face> Faces(Box b)
    {
        var (x0, z0, w, d, g) = (b.X0 - b.Grow, b.Z0 - b.Grow, b.W + 2 * b.Grow, b.D + 2 * b.Grow, b.Grow);
        var (y0, h) = (b.Y0 - g, b.H + 2 * g);
        var (tw, td, th) = ((int)b.W, (int)b.D, (int)b.H);
        var (u, v) = (b.U, b.V + td);
        var front = (u + td, tw);
        var back = (u + 2 * td + tw, tw);
        var right = (u, td);
        var left = (u + td + tw, td);
        // A mirrored limb (old skins' left arm and leg) uses the right one's picture flipped, with its outer and inner sides swapped.
        if (b.Mirror) (right, left) = (left, right);
        yield return new(x0, z0 + d, x0 + w, z0 + d, y0, h, front.Item1, v, front.Item2, th, b.Mirror, 0, 1, b.Layer);
        yield return new(x0 + w, z0, x0, z0, y0, h, back.Item1, v, back.Item2, th, b.Mirror, 0, -1, b.Layer);
        yield return new(x0, z0, x0, z0 + d, y0, h, right.Item1, v, right.Item2, th, b.Mirror, -1, 0, b.Layer);
        yield return new(x0 + w, z0 + d, x0 + w, z0, y0, h, left.Item1, v, left.Item2, th, b.Mirror, 1, 0, b.Layer);
    }

    /// <summary>
    /// The figure turned by <paramref name="degrees"/> (0 = facing the viewer, positive = turning to the figure's left,
    /// so its right side comes into view), <paramref name="pixelsPerUnit"/> screen pixels per skin pixel. Returns ARGB
    /// pixels <see cref="UnitsWide"/> × <see cref="UnitsTall"/> units big; where there is no figure, transparent.
    /// </summary>
    public static uint[] Render(SkinTexture skin, double degrees, int pixelsPerUnit, out int width, out int height)
    {
        var s = Math.Max(1, pixelsPerUnit);
        width = UnitsWide * s;
        height = UnitsTall * s;
        var target = new uint[width * height];
        var a = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        double sx(double x, double z) => (x - CenterX) * cos + z * sin + UnitsWide / 2.0;
        double sz(double x, double z) => -(x - CenterX) * sin + z * cos;

        var faces = new List<(Face F, double Depth, double Facing)>();
        foreach (var box in Boxes(skin))
            foreach (var f in Faces(box))
            {
                var facing = -f.Nx * sin + f.Nz * cos;
                if (facing < 1e-6) continue;
                var depth = (sz(f.Ax, f.Az) + sz(f.Bx, f.Bz)) / 2 + (f.Layer ? 0.01 : 0);
                faces.Add((f, depth, facing));
            }
        foreach (var (f, _, facing) in faces.OrderBy(x => x.Depth))
        {
            var ax = sx(f.Ax, f.Az);
            var bx = sx(f.Bx, f.Bz);
            if (Math.Abs(bx - ax) < 1e-6) continue;
            var shade = 0.7 + 0.3 * facing;
            var left = (int)Math.Floor(Math.Min(ax, bx) * s);
            var right = (int)Math.Ceiling(Math.Max(ax, bx) * s);
            var top = (int)Math.Floor((f.Y0 + Top) * s);
            var bottom = (int)Math.Ceiling((f.Y0 + Top + f.H) * s);
            for (var px = Math.Max(0, left); px < Math.Min(width, right); px++)
            {
                var t = ((px + 0.5) / s - ax) / (bx - ax);
                if (t < 0 || t >= 1) continue;
                var col = (int)(t * f.Tw);
                if (f.Flip) col = f.Tw - 1 - col;
                for (var py = Math.Max(0, top); py < Math.Min(height, bottom); py++)
                {
                    var r = ((py + 0.5) / s - Top - f.Y0) / f.H;
                    if (r < 0 || r >= 1) continue;
                    var c = skin.At(f.Tu + col, f.Tv + (int)(r * f.Th));
                    if (c >> 24 < 128) continue;
                    target[py * width + px] = Shade(c, shade);
                }
            }
        }
        return target;
    }

    private static uint Shade(uint c, double k)
    {
        uint ch(int shift) => (uint)Math.Clamp(Math.Round(((c >> shift) & 0xFF) * k), 0, 255);
        return 0xFF000000 | ch(16) << 16 | ch(8) << 8 | ch(0);
    }
}

/// <summary>
/// A whole skin drawn from a name, for accounts without a skin of their own (offline accounts): the pixel face
/// (<see cref="PixelFace"/>) on a head, hair round the back, a shirt, trousers and shoes. Original artwork.
/// </summary>
public static class PixelSkin
{
    private static readonly uint[] Shirts = [0xFF3D6FB6, 0xFF2F7D5B, 0xFFB5523B, 0xFF7A4FA8, 0xFFC28A2C, 0xFF2D8C93, 0xFF8C3A55, 0xFF55616F];
    private static readonly uint[] Trousers = [0xFF2C3446, 0xFF3B2F27, 0xFF24435C, 0xFF46484D, 0xFF4A3B5E];
    private static readonly uint[] Shoes = [0xFF1B1B1F, 0xFF4A2E1C, 0xFFE4E6EA];

    public static SkinTexture Generate(string name)
    {
        var face = PixelFace.Cells(name);
        var skin = face[3 * PixelFace.Size + 3];
        var hair = face[0];
        var random = new PixelRandom("skin" + name);
        var shirt = random.Pick(Shirts);
        var trousers = random.Pick(Trousers);
        var shoes = random.Pick(Shoes);
        var shirtShade = ThemeColor.Mix(shirt, 0xFF000000, 0.25);
        var skinShade = ThemeColor.Mix(skin, 0xFF000000, 0.12);

        var pixels = new uint[64 * 64];
        void fill(int x, int y, int w, int h, uint color)
        {
            for (var row = y; row < y + h; row++)
                for (var col = x; col < x + w; col++)
                    pixels[row * 64 + col] = color;
        }

        // Head: top, bottom, then the four sides from the right side round to the back.
        fill(8, 0, 8, 8, hair);
        fill(16, 0, 8, 8, skinShade);
        fill(0, 8, 32, 8, skin);
        for (var i = 0; i < face.Length; i++) pixels[(8 + i / 8) * 64 + 8 + i % 8] = face[i];
        fill(0, 8, 8, 3, hair);   // right side: hair over the ear
        fill(16, 8, 8, 3, hair);  // left side
        fill(24, 8, 8, 6, hair);  // back: hair down to the neck
        // Body: shirt all round, a collar of skin at the front, a darker hem.
        fill(20, 16, 16, 4, shirt);
        fill(16, 20, 24, 12, shirt);
        fill(23, 20, 2, 1, skin);
        fill(16, 31, 24, 1, shirtShade);
        // Arms: short sleeves, then skin.
        void arm(int u, int v)
        {
            fill(u + 4, v, 8, 4, shirt);
            fill(u, v + 4, 16, 4, shirt);
            fill(u, v + 8, 16, 8, skin);
            fill(u + 8, v, 4, 4, skin);
        }
        arm(40, 16);
        arm(32, 48);
        // Legs: trousers, shoes at the bottom.
        void leg(int u, int v)
        {
            fill(u + 4, v, 4, 4, trousers);
            fill(u + 8, v, 4, 4, shoes);
            fill(u, v + 4, 16, 9, trousers);
            fill(u, v + 13, 16, 3, shoes);
        }
        leg(0, 16);
        leg(16, 48);
        return new SkinTexture(pixels, 64, 64, Slim: false);
    }
}
