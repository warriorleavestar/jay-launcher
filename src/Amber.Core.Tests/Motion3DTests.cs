using System.IO.Compression;
using System.Text;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>Pseudo-3D motion: the "motion3d" tokens, the layered "scene" slot, and the maths behind tilt and parallax.</summary>
public sealed class Motion3DTests
{
    private const string Manifest = """{ "format": 2, "id": "deep", "name": "Deep" }""";

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private static ThemePack Read(string tokens, params string[] pictures)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void add(string name, byte[] bytes)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
            add("theme.json", Encoding.UTF8.GetBytes(Manifest));
            add("tokens.json", Encoding.UTF8.GetBytes(tokens));
            foreach (var p in pictures) add(p, Png(1920, 1080));
        }
        ms.Position = 0;
        return ThemePackReader.Read(ms);
    }

    [Fact]
    public void APackWithoutMotion3DLeavesItToJay()
    {
        var pack = Read("{}");
        Assert.Null(pack.Motion3D);
        Assert.Null(pack.Scene);
        Assert.Same(Motion3D.Default, Motion3D.Effective(pack.Motion3D, enabled: true, reduced: false));
    }

    [Fact]
    public void ReadsMotion3DAndPullsNumbersIntoRange()
    {
        var pack = Read("""{ "motion3d": { "tilt": 25, "pageDepth": 0.03, "parallax": -4, "perspective": 800, "turntable": false, "spin": 1 } }""");
        var m = pack.Motion3D!;
        Assert.Equal(Motion3D.MaxTilt, m.Tilt);
        Assert.Equal(0.03, m.PageDepth);
        Assert.Equal(0, m.Parallax);
        Assert.Equal(800, m.Perspective);
        Assert.False(m.Turntable);
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped" && p.Detail.StartsWith("motion3d.tilt"));
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped" && p.Detail.StartsWith("motion3d.parallax"));
        Assert.Contains(pack.Problems, p => p.Code == "unknown_key" && p.Detail == "motion3d.spin");
        Assert.Empty(pack.ReservedKeys);
    }

    [Fact]
    public void ReducedMotionOrTheSettingTurnsEverythingOff()
    {
        var themed = new Motion3D { Tilt = 8, Parallax = 20 };
        Assert.Same(themed, Motion3D.Effective(themed, enabled: true, reduced: false));
        Assert.True(Motion3D.Effective(themed, enabled: true, reduced: true).IsFlat);
        Assert.True(Motion3D.Effective(themed, enabled: false, reduced: false).IsFlat);
        Assert.True(Motion3D.Effective(null, enabled: false, reduced: false).IsFlat);
        Assert.False(Motion3D.Default.IsFlat);
    }

    [Fact]
    public void ACardLeansTowardThePointerAndIsFlatInTheMiddle()
    {
        Assert.Equal((0.0, 0.0), Motion3D.TiltAngles(100, 50, 200, 100, 4));
        var (x, y) = Motion3D.TiltAngles(200, 0, 200, 100, 4);
        Assert.Equal(4, x, 6);
        Assert.Equal(4, y, 6);
        // Outside the card (pointer captured while dragging): no more than the maximum.
        var (x2, y2) = Motion3D.TiltAngles(-500, 900, 200, 100, 4);
        Assert.Equal(-4, x2, 6);
        Assert.Equal(-4, y2, 6);
        Assert.Equal((0.0, 0.0), Motion3D.TiltAngles(10, 10, 0, 0, 4));
    }

    [Fact]
    public void NearLayersMoveMoreThanFarOnesAndAgainstThePointer()
    {
        Assert.Equal((0.0, 0.0), Motion3D.ParallaxOffset(1000, 1000, 1000, 1000, 0, 12));
        var near = Motion3D.ParallaxOffset(1000, 500, 1000, 1000, 1, 12);
        var mid = Motion3D.ParallaxOffset(1000, 500, 1000, 1000, 0.5, 12);
        Assert.Equal(-12, near.X, 6);
        Assert.Equal(-6, mid.X, 6);
        Assert.Equal(0, near.Y, 6);
    }

    [Fact]
    public void ReadsALayeredSceneFarToNear()
    {
        var pack = Read("""
            { "slots": { "scene": { "layers": [
                { "image": "assets/sky.png" },
                { "image": "assets/hills.png", "depth": 0.4 },
                { "image": "assets/trees.png", "depth": 3 }
              ], "dim": 0.3, "focus": [0.5, 1] } } }
            """, "assets/sky.png", "assets/hills.png", "assets/trees.png");
        var scene = pack.Scene!;
        Assert.Equal(["assets/sky.png", "assets/hills.png", "assets/trees.png"], scene.Layers.Select(l => l.Image));
        Assert.Equal([0, 0.4, 1], scene.Layers.Select(l => l.Depth));
        Assert.Equal(0.3, scene.Dim);
        Assert.Equal(1, scene.FocusY);
        Assert.Contains(pack.Problems, p => p.Code == "value_clamped" && p.Detail.StartsWith("slots.scene.layers[2].depth"));
        Assert.Empty(pack.Slots);
    }

    [Fact]
    public void OnePictureIsASceneHalfwayBack()
    {
        // The form "slots.scene.image" that packs could already carry while the slot was reserved.
        var pack = Read("""{ "slots": { "scene": { "image": "assets/scene.png" } } }""", "assets/scene.png");
        Assert.Empty(pack.Problems);
        var layer = Assert.Single(pack.Scene!.Layers);
        Assert.Equal(0.5, layer.Depth);
        Assert.Equal(ThemeScene.DefaultDim, pack.Scene.Dim);
    }

    [Fact]
    public void BadLayersAreLeftOutAndAtMostFourAreKept()
    {
        var pack = Read("""
            { "slots": { "scene": { "layers": [
                { "image": "assets/missing.png" }, { "depth": 1 },
                { "image": "assets/a.png" }, { "image": "assets/a.png" }, { "image": "assets/a.png" }, { "image": "assets/a.png" }, { "image": "assets/a.png" }
              ] } } }
            """, "assets/a.png");
        Assert.Equal(ThemeScene.MaxLayers, pack.Scene!.Layers.Count);
        Assert.Contains(pack.Problems, p => p.Code == "missing_file");
        Assert.Contains(pack.Problems, p => p.Code == "bad_value" && p.Detail == "slots.scene.layers[1]");
        Assert.Contains(pack.Problems, p => p.Code == "too_many");

        var none = Read("""{ "slots": { "scene": { "layers": [ { "image": "assets/missing.png" } ] } } }""");
        Assert.Null(none.Scene);
    }
}
