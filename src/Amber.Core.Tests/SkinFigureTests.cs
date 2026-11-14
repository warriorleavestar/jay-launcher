using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>The turning skin figure on the "Me" page: the pseudo-3D projection and the drawn skin for accounts without one.</summary>
public class SkinFigureTests
{
    /// <summary>A skin where every part's sides have their own colour, so the projection can be checked by colour.</summary>
    private static SkinTexture Marked(bool legacy = false)
    {
        var height = legacy ? 32 : 64;
        var pixels = new uint[64 * height];
        void fill(int x, int y, int w, int h, uint c)
        {
            for (var r = y; r < y + h; r++)
                for (var col = x; col < x + w; col++) pixels[r * 64 + col] = c;
        }
        // Head: right side red, front green, left side blue, back yellow.
        fill(0, 8, 8, 8, 0xFFFF0000);
        fill(8, 8, 8, 8, 0xFF00FF00);
        fill(16, 8, 8, 8, 0xFF0000FF);
        fill(24, 8, 8, 8, 0xFFFFFF00);
        // Body front white, back black-ish; the rest grey.
        fill(16, 20, 24, 12, 0xFF808080);
        fill(20, 20, 8, 12, 0xFFFFFFFF);
        fill(32, 20, 8, 12, 0xFF101010);
        fill(40, 20, 16, 12, 0xFF808080);
        fill(0, 20, 16, 12, 0xFF808080);
        if (!legacy)
        {
            fill(32, 52, 16, 12, 0xFF808080);
            fill(16, 52, 16, 12, 0xFF808080);
        }
        return new SkinTexture(pixels, 64, height, Slim: false);
    }

    private static uint Pixel(uint[] p, int width, double ux, double uy, int scale) => p[(int)(uy * scale) * width + (int)(ux * scale)];

    private static uint Rgb(uint c) => c & 0xFFFFFF;

    [Fact]
    public void FacingTheViewerShowsTheFrontOfTheHeadAndBody()
    {
        var p = SkinTurntable.Render(Marked(), 0, 4, out var w, out var h);
        Assert.Equal(SkinTurntable.UnitsWide * 4, w);
        Assert.Equal(SkinTurntable.UnitsTall * 4, h);
        // The middle of the face (unit x 9 of 18, y 4.5) is green, the middle of the chest white.
        Assert.Equal(0x00FF00u, Rgb(Pixel(p, w, 9, 4.5, 4)));
        Assert.Equal(0xFFFFFFu, Rgb(Pixel(p, w, 9, 14, 4)));
        // Corners stay transparent.
        Assert.Equal(0u, p[0]);
        Assert.Equal(0u, p[^1]);
    }

    [Fact]
    public void TurnedHalfWayRoundShowsTheBack()
    {
        var p = SkinTurntable.Render(Marked(), 180, 4, out var w, out _);
        Assert.Equal(0xFFFF00u, Rgb(Pixel(p, w, 9, 4.5, 4)));
        Assert.Equal(0x101010u, Rgb(Pixel(p, w, 9, 14, 4)));
    }

    [Fact]
    public void TurnedAQuarterShowsOneSideAndTheOtherWayTheOtherSide()
    {
        var plus = SkinTurntable.Render(Marked(), 90, 4, out var w, out _);
        var minus = SkinTurntable.Render(Marked(), -90, 4, out _, out _);
        Assert.Equal(0xFF0000u, Rgb(Pixel(plus, w, 9, 4.5, 4)));
        Assert.Equal(0x0000FFu, Rgb(Pixel(minus, w, 9, 4.5, 4)));
    }

    [Fact]
    public void AtTheRestAngleTheFrontAndOneSideAreBothInView()
    {
        var p = SkinTurntable.Render(Marked(), SkinTurntable.RestAngle, 6, out var w, out _);
        var row = Enumerable.Range(0, w).Select(x => p[(int)(4.5 * 6) * w + x]).Where(c => c != 0).ToList();
        bool only(uint c, int shift) => ((c >> shift) & 0xFF) > 0 && Enumerable.Range(0, 3).All(i => i * 8 == shift || ((c >> (i * 8)) & 0xFF) == 0);
        var front = row.Count(c => only(c, 8));
        var side = row.Count(c => only(c, 0) || only(c, 16));
        Assert.True(front > side && side > 0, $"front {front}, side {side}");
        // Shaded by how much each side faces the viewer: the side that turns away is darker than the front.
        Assert.True(row.Where(c => only(c, 8)).Max(c => (c >> 8) & 0xFF) > row.Where(c => only(c, 0) || only(c, 16)).Max(c => Math.Max(c & 0xFF, (c >> 16) & 0xFF)));
    }

    [Fact]
    public void OldSkinsUseTheRightArmAndLegForTheLeftOnes()
    {
        var p = SkinTurntable.Render(Marked(legacy: true), 0, 4, out var w, out _);
        // The left leg (viewer's right, unit x 10..12 → 11 here at y 26) is drawn although the old layout has no picture of its own for it.
        Assert.NotEqual(0u, Pixel(p, w, 11, 26, 4));
        Assert.NotEqual(0u, Pixel(p, w, 7, 26, 4));
    }

    [Fact]
    public void TheDrawnSkinIsTheSameEveryTimeAndCarriesTheFace()
    {
        var a = PixelSkin.Generate("Robin");
        var b = PixelSkin.Generate("Robin");
        Assert.Equal(a.Pixels, b.Pixels);
        Assert.NotEqual(a.Pixels, PixelSkin.Generate("Sam").Pixels);
        var face = PixelFace.Cells("Robin");
        for (var i = 0; i < face.Length; i++) Assert.Equal(face[i], a.At(8 + i % 8, 8 + i / 8));
        // Every pixel the figure uses is opaque, so the figure has no holes from any side.
        foreach (var angle in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
        {
            var p = SkinTurntable.Render(a, angle, 2, out var w, out _);
            Assert.NotEqual(0u, Pixel(p, w, 9, 4.5, 2));
            Assert.NotEqual(0u, Pixel(p, w, 9, 14, 2));
            Assert.NotEqual(0u, Pixel(p, w, 9, 26, 2));
        }
    }

    [Theory]
    [InlineData(64, 64, true)]
    [InlineData(64, 32, true)]
    [InlineData(128, 128, true)]
    [InlineData(32, 32, false)]
    [InlineData(64, 48, false)]
    public void KnowsWhichSizesAreSkins(int w, int h, bool fits) => Assert.Equal(fits, SkinTexture.Fits(w, h));
}
