namespace Amber.Core.Themes;

/// <summary>A filled rectangle of a pixel drawing, in drawing units.</summary>
public readonly record struct PixelBlock(int X, int Y, int Width, int Height);

/// <summary>One paint layer of a pixel drawing: every rectangle has the same colour, and later layers cover earlier ones.</summary>
public sealed record PixelLayer(uint Color, IReadOnlyList<PixelBlock> Rects);

/// <summary>
/// A small seeded generator (FNV-1a seed, mulberry32 steps): the same text gives the same drawing on every machine and
/// in every run, which <see cref="Random"/> and string.GetHashCode do not promise.
/// </summary>
internal struct PixelRandom
{
    private uint _state;

    public PixelRandom(string seed)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in seed) hash = (hash ^ c) * 16777619u;
            _state = hash;
        }
    }

    /// <summary>The next value in [0, 1).</summary>
    public double Next()
    {
        unchecked
        {
            var t = _state += 0x6D2B79F5u;
            t = (t ^ (t >> 15)) * (t | 1u);
            t ^= t + (t ^ (t >> 7)) * (t | 61u);
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>The next whole number in [0, count).</summary>
    public int Next(int count) => (int)(Next() * count);

    public T Pick<T>(T[] items) => items[Next(items.Length)];
}

/// <summary>
/// Pixel faces: an 8 × 8 portrait drawn from a person's name, so everyone in a list has a face before (or without) a
/// picture of their own. Original artwork made of flat cells; the mouth is always centred under the nose.
/// </summary>
public static class PixelFace
{
    public const int Size = 8;
    public const uint MouthColor = 0xFF8A4A3A;
    public const uint EyeWhite = 0xFFFFFFFF;

    private static readonly uint[] Skins = [0xFFF2CBA6, 0xFFE3B083, 0xFFC98D5C, 0xFF99623F, 0xFFF7DCC4];
    private static readonly uint[] Hairs = [0xFF2B1E15, 0xFF5B3B23, 0xFFB87A3B, 0xFFE0C77E, 0xFF1E2C48, 0xFF7C3140, 0xFFDFE3EA, 0xFF356650, 0xFF6A4BB0];
    private static readonly uint[] Eyes = [0xFF2C4F93, 0xFF38291C, 0xFF2E6C46, 0xFF5B3B8C];

    /// <summary>The 64 cells of the face for <paramref name="name"/>, row by row from the top-left, as opaque ARGB.</summary>
    public static uint[] Cells(string name)
    {
        var random = new PixelRandom("face" + name);
        var skin = random.Pick(Skins);
        var hair = random.Pick(Hairs);
        var eye = random.Pick(Eyes);
        var fringe = random.Next(4);
        var longHair = random.Next() > 0.5;
        var beard = random.Next() > 0.82;
        var shade = ThemeColor.Mix(skin, 0xFF000000, 0.16);
        var hairShade = ThemeColor.Mix(hair, 0xFF000000, 0.28);

        var cells = new uint[Size * Size];
        void fill(int x, int y, int width, int height, uint color)
        {
            for (var row = y; row < y + height; row++)
                for (var column = x; column < x + width; column++)
                    cells[row * Size + column] = color;
        }

        fill(0, 0, 8, 8, skin);
        fill(0, 0, 8, 2, hair);
        fill(0, 1, 8, 1, hairShade);
        switch (fringe)
        {
            case 0: fill(0, 2, 8, 1, hair); break;                          // straight fringe
            case 1: fill(0, 2, 4, 1, hair); fill(7, 2, 1, 1, hair); break;  // swept to one side
            case 2: fill(0, 2, 1, 1, hair); fill(7, 2, 1, 1, hair); break;  // open forehead
            default: fill(0, 2, 2, 1, hair); fill(6, 2, 2, 1, hair); break; // parted
        }
        if (longHair)
        {
            fill(0, 3, 1, 3, hair);
            fill(7, 3, 1, 3, hair);
        }
        fill(1, 4, 1, 1, EyeWhite);
        fill(2, 4, 1, 1, eye);
        fill(5, 4, 1, 1, eye);
        fill(6, 4, 1, 1, EyeWhite);
        fill(3, 5, 2, 1, shade);
        if (beard)
        {
            fill(1, 6, 6, 2, hair);
            fill(3, 6, 2, 1, MouthColor);
        }
        else
        {
            // One draw decides the width; the position follows from it, so the mouth cannot sit off centre.
            var width = random.Next() > 0.5 ? 4 : 2;
            fill(4 - width / 2, 6, width, 1, MouthColor);
        }
        return cells;
    }
}

/// <summary>
/// Pixel covers: a small night landscape per <see cref="GameLibrary.CoverArt"/> slot, for instances that have no picture
/// of their own. Original artwork (ridges, trees, towers, water drawn from rectangles), no game assets. They are
/// artwork, not theme tokens, so they look the same in light and dark themes. Every large colour is dark enough for
/// white initials (a unit test checks 4.5:1 with room to spare); only small lights (moon, stars, windows) are bright.
/// </summary>
public static class PixelCover
{
    public const int Width = 320, Height = 200;

    /// <summary>One pixel of the drawing, in drawing units.</summary>
    public const int Cell = 5;

    /// <summary>
    /// Where the instance's initial is drawn on a card or a square tile (both crop the canvas around its centre).
    /// Bright details stay out of this box.
    /// </summary>
    public static readonly PixelBlock InitialBox = new(120, 60, 80, 80);

    private static bool InInitialBox(int x, int y, int width, int height) =>
        x < InitialBox.X + InitialBox.Width && x + width > InitialBox.X && y < InitialBox.Y + InitialBox.Height && y + height > InitialBox.Y;

    public enum Scene { Hills, Pines, Mesa, Islands, City, Lava, Sea }

    /// <summary>
    /// Colours of one cover. <see cref="Areas"/> lists the ones that fill large areas; <see cref="Light"/> and
    /// <see cref="Glow"/> are the small bright details.
    /// </summary>
    public sealed record Palette(string Name, Scene Kind, bool Stars, uint SkyTop, uint SkyBottom, uint Far, uint Near, uint NearEdge,
        uint Front, uint Detail, uint DetailTop, uint Light, uint Glow)
    {
        public IReadOnlyList<uint> Areas => [SkyTop, SkyBottom, Far, Near, NearEdge, Front, Detail, DetailTop];
    }

    /// <summary>One palette per cover slot; the count must equal <see cref="GameLibrary.CoverArt.Slots"/>.</summary>
    public static readonly IReadOnlyList<Palette> Palettes =
    [
        new("moss", Scene.Hills, true, 0xFF0F2647, 0xFF24528A, 0xFF21497A, 0xFF246848, 0xFF2D7952, 0xFF173F31, 0xFF4A3120, 0xFF1F6040, 0xFFFFE2A0, 0xFFFFE2A0),
        new("clay", Scene.Mesa, false, 0xFF2E1B3D, 0xFF8A4632, 0xFF6E3830, 0xFF7E4428, 0xFF945632, 0xFF46251F, 0xFF2A5F3C, 0xFF347248, 0xFFFFD27A, 0xFFFFD27A),
        new("lake", Scene.Hills, true, 0xFF0B1F3F, 0xFF26508A, 0xFF1D3F6E, 0xFF1F4F74, 0xFF2B658C, 0xFF12305A, 0xFF16294A, 0xFF1B4266, 0xFFDCE8FF, 0xFF6FA6E0),
        new("dusk", Scene.Islands, true, 0xFF150F33, 0xFF46378A, 0xFF2A2360, 0xFF55402F, 0xFF367A4E, 0xFF251F58, 0xFF432E27, 0xFF2A6B45, 0xFFE8E4FF, 0xFF8FB8FF),
        new("amber ore", Scene.City, true, 0xFF070D1C, 0xFF17325C, 0xFF0F2038, 0xFF16294A, 0xFF2B4A7E, 0xFF0A1424, 0xFF16294A, 0xFF2B4A7E, 0xFFCFE0FF, 0xFFFFB53F),
        new("forest", Scene.Pines, true, 0xFF0A2424, 0xFF1D564A, 0xFF16463C, 0xFF1A5438, 0xFF276A44, 0xFF0D2C22, 0xFF3A2A1C, 0xFF124030, 0xFFF4F0C8, 0xFFF4F0C8),
        new("nether brick", Scene.Lava, false, 0xFF1A0A0E, 0xFF5A1E1E, 0xFF3A1518, 0xFF4A1C1C, 0xFF6A2A22, 0xFF7E2F12, 0xFF2A0F12, 0xFF5A2018, 0xFFFF9A4A, 0xFFFFB53F),
        new("prismarine", Scene.Sea, true, 0xFF08222C, 0xFF1D5A68, 0xFF164650, 0xFF12404A, 0xFF1F5F66, 0xFF0B2C34, 0xFF164650, 0xFF2A6E64, 0xFFE0FFF4, 0xFF6FD6C8),
        new("berry", Scene.Hills, false, 0xFF2A0F2E, 0xFF772E5A, 0xFF582346, 0xFF692C48, 0xFF84405C, 0xFF3C172E, 0xFF34202A, 0xFF74304E, 0xFFFFD9E8, 0xFFFFD9E8),
    ];

    /// <summary>The layers of the cover for <paramref name="slot"/>, bottom layer first, on a 320 × 200 canvas.</summary>
    public static IReadOnlyList<PixelLayer> Layers(int slot)
    {
        var count = Palettes.Count;
        var palette = Palettes[((slot % count) + count) % count];
        var random = new PixelRandom("cover" + palette.Name);
        var layers = new List<PixelLayer>();
        List<PixelBlock> layer(uint color)
        {
            var rects = new List<PixelBlock>();
            layers.Add(new PixelLayer(color, rects));
            return rects;
        }

        // Sky: twelve steps down to the horizon. Each band runs to the bottom so no seam can show between bands.
        const int bands = 12, horizon = 120;
        for (var i = 0; i < bands; i++)
            layer(ThemeColor.Mix(palette.SkyTop, palette.SkyBottom, i / (double)(bands - 1))).Add(new(0, i * horizon / bands, Width, Height - i * horizon / bands));
        if (palette.Stars)
        {
            var stars = layer(0xB3FFFFFF);
            for (var i = 0; i < 26; i++)
            {
                var (x, y) = (random.Next(Width / Cell) * Cell, random.Next(16) * Cell);
                if (!InInitialBox(x, y, 3, 3)) stars.Add(new(x, y, 3, 3));
            }
        }
        // The moon sits top-right: clear of the initial in the middle, and outside the band a wide banner shows.
        layer((palette.Light & 0x00FFFFFF) | 0x24000000).Add(new(230, 20, 30, 30));
        layer(palette.Light).Add(new(235, 25, 20, 20));

        // A ridge is a row of columns whose top wanders one cell at a time; returns the top of each column.
        int[] ridge(List<PixelBlock> into, int baseTop, int range, int step, int jump = Cell)
        {
            var tops = new int[Width / step];
            var top = baseTop;
            for (var i = 0; i < tops.Length; i++)
            {
                top = Math.Clamp(top + (random.Next(3) - 1) * jump, baseTop - range, baseTop + range);
                tops[i] = top;
                into.Add(new(i * step, top, step, Height - top));
            }
            return tops;
        }
        void edge(List<PixelBlock> into, int[] tops, int step)
        {
            for (var i = 0; i < tops.Length; i++) into.Add(new(i * step, tops[i], step, Cell));
        }

        switch (palette.Kind)
        {
            case Scene.Hills:
            {
                ridge(layer(palette.Far), 100, 15, 20);
                var tops = ridge(layer(palette.Near), 125, 10, 10);
                edge(layer(palette.NearEdge), tops, 10);
                var (trunks, crowns, crownTops) = (layer(palette.Detail), layer(palette.DetailTop), layer(palette.NearEdge));
                for (var i = 0; i < 9; i++)
                {
                    var column = random.Next(tops.Length - 2) + 1;
                    var (x, y) = (column * 10, tops[column]);
                    trunks.Add(new(x, y - 10, 5, 10));
                    crowns.Add(new(x - 5, y - 25, 15, 15));
                    crownTops.Add(new(x, y - 30, 5, 5));
                }
                var front = ridge(layer(palette.Front), 168, 5, 20);
                if (palette.Glow != palette.Light)
                {
                    // A lake in front: flat water with short glints instead of a dark bank.
                    layer(palette.Front).Add(new(0, 160, Width, Height - 160));
                    var glints = layer((palette.Glow & 0x00FFFFFF) | 0x99000000);
                    for (var i = 0; i < 14; i++) glints.Add(new(random.Next(Width / Cell) * Cell, 165 + random.Next(6) * Cell, 10 + random.Next(2) * 5, 2));
                }
                else edge(layer(ThemeColor.Mix(palette.Front, palette.Near, 0.5)), front, 20);
                break;
            }
            case Scene.Pines:
            {
                ridge(layer(palette.Far), 100, 15, 20);
                var tops = ridge(layer(palette.Near), 128, 8, 10);
                edge(layer(palette.NearEdge), tops, 10);
                var (trunks, needles) = (layer(palette.Detail), layer(palette.DetailTop));
                for (var i = 0; i < 16; i++)
                {
                    var column = random.Next(tops.Length - 2) + 1;
                    var (x, y) = (column * 10, tops[column]);
                    var tiers = 3 + random.Next(2);
                    trunks.Add(new(x, y - 5, 5, 5));
                    for (var tier = 0; tier < tiers; tier++)
                        needles.Add(new(x - (tiers - 1 - tier) * 5, y - 5 - (tier + 1) * 7, 5 + (tiers - 1 - tier) * 10, 7));
                }
                ridge(layer(palette.Front), 170, 5, 20);
                break;
            }
            case Scene.Mesa:
            {
                // Flat-topped rock: wide steps, tall jumps.
                var far = ridge(layer(palette.Far), 98, 16, 40, jump: 2 * Cell);
                edge(layer(palette.Near), far, 40);
                var tops = ridge(layer(palette.Near), 132, 6, 20);
                edge(layer(palette.NearEdge), tops, 20);
                var cacti = layer(palette.Detail);
                for (var i = 0; i < 6; i++)
                {
                    var column = random.Next(tops.Length - 2) + 1;
                    var (x, y) = (column * 20 + 5, tops[column]);
                    cacti.Add(new(x, y - 20, 5, 20));
                    cacti.Add(new(x - 5, y - 15, 5, 5));
                    cacti.Add(new(x + 5, y - 12, 5, 5));
                }
                ridge(layer(palette.Front), 170, 5, 20);
                break;
            }
            case Scene.Islands:
            {
                layer(palette.Far).Add(new(0, 150, Width, Height - 150));
                var (earth, deepEarth, grass, crowns, falls) = (layer(palette.Near), layer(palette.Detail), layer(palette.NearEdge), layer(palette.DetailTop), layer((palette.Glow & 0x00FFFFFF) | 0x99000000));
                (int X, int Y, int Width)[] islands = [(15, 110, 55), (95, 85, 70), (175, 120, 45), (215, 80, 60), (285, 112, 40)];
                for (var i = 0; i < islands.Length; i++)
                {
                    var (x, y, width) = islands[i];
                    grass.Add(new(x, y, width, 5));
                    earth.Add(new(x + 5, y + 5, width - 10, 10));
                    deepEarth.Add(new(x + 10, y + 15, width - 20, 10));
                    if (width > 50) deepEarth.Add(new(x + 20, y + 25, width - 40, 5));
                    // Waterfalls only off the outer islands, clear of the initial in the middle.
                    if (i is 0 or 3) falls.Add(new(x + width - 15, y + 5, 5, 70));
                    crowns.Add(new(x + 10, y - 20, 15, 15));
                    earth.Add(new(x + 15, y - 5, 5, 5));
                }
                layer(palette.Front).Add(new(0, 175, Width, Height - 175));
                break;
            }
            case Scene.City:
            {
                ridge(layer(palette.Far), 105, 15, 20);
                var (towers, roofs, warm, cool) = (layer(palette.Near), layer(palette.NearEdge), layer(palette.Glow), layer(0xFF7FC0FF));
                for (var x = 0; x < Width;)
                {
                    var width = 20 + random.Next(4) * 5;
                    var top = 60 + random.Next(13) * 5;
                    towers.Add(new(x, top, width - 5, Height - top));
                    roofs.Add(new(x, top, width - 5, 3));
                    if (random.Next() > 0.7) roofs.Add(new(x + (width - 5) / 2, top - 15, 2, 15));
                    // Lit windows, except behind the initial in the middle of the cover.
                    for (var wy = top + 10; wy < 165; wy += 12)
                        for (var wx = x + 5; wx < x + width - 10; wx += 10)
                        {
                            var lit = random.Next() > 0.5;
                            var warmLight = random.Next() > 0.3;
                            if (lit && !InInitialBox(wx, wy, 5, 5)) (warmLight ? warm : cool).Add(new(wx, wy, 5, 5));
                        }
                    x += width;
                }
                layer(palette.Front).Add(new(0, 180, Width, Height - 180));
                break;
            }
            case Scene.Lava:
            {
                ridge(layer(palette.Far), 95, 20, 20, jump: 2 * Cell);
                var tops = ridge(layer(palette.Near), 128, 12, 10, jump: 2 * Cell);
                edge(layer(palette.NearEdge), tops, 10);
                layer(palette.Detail).Add(new(0, 160, Width, Height - 160));
                var lava = ridge(layer(palette.Front), 172, 4, 20);
                var glow = layer(palette.Glow);
                for (var i = 0; i < lava.Length; i += 2) glow.Add(new(i * 20 + random.Next(3) * 5, lava[i], 10, 3));
                for (var i = 0; i < 14; i++)
                {
                    var (x, y) = (random.Next(Width / Cell) * Cell, 60 + random.Next(20) * Cell);
                    if (!InInitialBox(x, y, 3, 3)) glow.Add(new(x, y, 3, 3));
                }
                break;
            }
            default:
            {
                // Sea: a far shore, two islets, then water in darker steps with short glints.
                ridge(layer(palette.Far), 112, 6, 20);
                layer(palette.Near).Add(new(0, 122, Width, Height - 122));
                var (islet, isletTop) = (layer(palette.Detail), layer(palette.DetailTop));
                foreach (var (x, width) in new[] { (40, 60), (200, 40) })
                {
                    islet.Add(new(x, 112, width, 12));
                    islet.Add(new(x + 10, 104, width - 20, 8));
                    isletTop.Add(new(x + 10, 104, width - 20, 4));
                }
                layer(ThemeColor.Mix(palette.Near, palette.Front, 0.5)).Add(new(0, 146, Width, Height - 146));
                layer(palette.Front).Add(new(0, 172, Width, Height - 172));
                var glints = layer(palette.NearEdge);
                for (var i = 0; i < 22; i++) glints.Add(new(random.Next(Width / Cell) * Cell, 126 + random.Next(13) * Cell, 10 + random.Next(2) * 5, 2));
                // The moon's path on the water.
                var path = layer((palette.Glow & 0x00FFFFFF) | 0x80000000);
                for (var y = 126; y < 190; y += 8) path.Add(new(240 - random.Next(2) * 5, y, 10 + random.Next(2) * 5, 3));
                break;
            }
        }
        return layers;
    }
}
