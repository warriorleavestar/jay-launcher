using Amber.Core.GameLibrary;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>Automatic covers (§6.2; pixel covers): one scene per slot, and white initials readable on every large colour.</summary>
public sealed class CoverBrushTests
{
    private const uint White = 0xFFFFFFFF;

    [Fact]
    public void OnePalettePerSlot()
    {
        Assert.Equal(CoverArt.Slots, PixelCover.Palettes.Count);
        Assert.Equal(PixelCover.Palettes.Count, PixelCover.Palettes.Select(p => p.Name).Distinct().Count());
        Assert.Equal(PixelCover.Palettes.Count, PixelCover.Palettes.Select(p => p.SkyTop).Distinct().Count());
    }

    [Fact]
    public void WhiteInitialsMeetAaOnEveryLargeColour()
    {
        foreach (var palette in PixelCover.Palettes)
            foreach (var color in palette.Areas)
                // The initial is drawn at 92% opacity, so leave room above 4.5:1.
                Assert.True(ThemeColor.Contrast(White, color) >= 5.0, $"{palette.Name} {ThemePackReader.FormatColor(color)}: {ThemeColor.Contrast(White, color):0.00}");
    }

    [Fact]
    public void ScenesAreStableAndFillTheCanvas()
    {
        for (var slot = 0; slot < CoverArt.Slots; slot++)
        {
            var layers = PixelCover.Layers(slot);
            var again = PixelCover.Layers(slot);
            Assert.Equal(layers.Select(l => (l.Color, l.Rects.Count)), again.Select(l => (l.Color, l.Rects.Count)));
            Assert.Equal(layers.SelectMany(l => l.Rects), again.SelectMany(l => l.Rects));
            // The first layer is the sky over the whole canvas, so nothing behind the cover shows through.
            Assert.Equal(new PixelBlock(0, 0, PixelCover.Width, PixelCover.Height), layers[0].Rects[0]);
            Assert.Equal(0xFFu, layers[0].Color >> 24);
            Assert.All(layers.SelectMany(l => l.Rects), r => Assert.True(r.Width > 0 && r.Height > 0));
            Assert.True(layers.Count >= 15, $"slot {slot}: {layers.Count} layers");
        }
        // Slots wrap instead of throwing (the brush converter may be handed anything).
        Assert.Equal(PixelCover.Layers(0).Count, PixelCover.Layers(CoverArt.Slots).Count);
        Assert.Equal(PixelCover.Layers(CoverArt.Slots - 1).Count, PixelCover.Layers(-1).Count);
    }

    [Fact]
    public void BrightDetailsStayClearOfTheInitial()
    {
        var box = PixelCover.InitialBox;
        for (var slot = 0; slot < CoverArt.Slots; slot++)
        {
            var palette = PixelCover.Palettes[slot];
            // Anything that is not one of the palette's large colours (or a sky step between two of them) is a light.
            var dark = palette.Areas.ToHashSet();
            foreach (var layer in PixelCover.Layers(slot))
            {
                var solid = ThemeColor.Over(layer.Color, palette.SkyBottom);
                if (dark.Contains(layer.Color) || ThemeColor.Contrast(White, solid) >= 5.0) continue;
                Assert.All(layer.Rects, r => Assert.False(
                    r.X < box.X + box.Width && r.X + r.Width > box.X && r.Y < box.Y + box.Height && r.Y + r.Height > box.Y,
                    $"{palette.Name}: a bright block at {r.X},{r.Y} is behind the initial"));
            }
        }
    }
}
