using System.Buffers.Binary;
using System.IO.Compression;
using Amber.App.Services;
using Amber.Core.Themes;
using SkiaSharp;

namespace Amber.Core.Tests;

/// <summary>
/// Pictures for theme slots: sizes come from the header and are checked before decoding, nothing is scaled up, a thin
/// picture can't grow into a huge one, the pixels being decoded share one budget, and all eight EXIF orientations.
/// </summary>
[Collection("ThemeImages")]
public sealed class ThemeImagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jay-theme-images-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private string Picture(string name, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(0x4A, 0x1C, 0x6B));
        using var data = bitmap.Encode(format, 90);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>
    /// A small, valid PNG whose header says it is <paramref name="width"/>×<paramref name="height"/>: one compressed
    /// row of zeros, the kind of file that would decode into gigabytes.
    /// </summary>
    private string HugePng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, true))
                z.Write(new byte[1 + width * 3]);
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", []);
        var path = Path.Combine(_dir, $"huge-{width}x{height}.png");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        s.Write(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        s.Write(crc);
    }

    /// <summary>The CRC-32 PNG chunks carry (ISO-HDLC, as in zlib).</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    [Fact]
    public void AHugePictureInASmallFileIsRefusedFromItsHeader()
    {
        var path = HugePng(30000, 30000);
        Assert.True(new FileInfo(path).Length < 200_000);
        ThemeImages.Budget.ResetPeak();
        Assert.Null(ThemeImages.Prepare(path, ThemeSlots.Background, out var error));
        Assert.Equal("theme.image.error.too_big", error);
        Assert.Null(ThemeImages.LoadPixels(path, ThemeSlots.Background, new ThemeSlot { Image = "x", Blur = 20 }));
        Assert.Null(ThemeImages.LoadPixels(path, ThemeSlots.Banner, new ThemeSlot { Image = "x" }));
        Assert.Equal(0, ThemeImages.Budget.Peak);
    }

    [Fact]
    public void APictureThatCantBeDecodedSmallerIsRefusedWhenItsFullSizeIsOverTheBudget()
    {
        // 8000×6000 PNG: under the header limit, but a PNG decodes at full size (48 million pixels).
        var path = HugePng(8000, 6000);
        Assert.Null(ThemeImages.Prepare(path, ThemeSlots.Background, out var error));
        Assert.Equal("theme.image.error.too_big", error);
    }

    [Theory]
    [InlineData(1, 4096)]
    [InlineData(16, 4096)]
    [InlineData(4096, 1)]
    [InlineData(4096, 16)]
    public void AThinPictureStaysSmallInEverySlot(int width, int height)
    {
        var path = Picture($"thin-{width}x{height}.png", width, height);
        foreach (var slot in ThemeSlots.All)
        {
            var prepared = ThemeImages.Prepare(path, slot, out var error);
            Assert.NotNull(prepared);
            Assert.Null(error);
            Assert.InRange(Math.Max(prepared.Width, prepared.Height), 1, ImageTheme.MaxSide(slot));
            Assert.True(prepared.Width <= width && prepared.Height <= height);

            using var shown = ThemeImages.LoadPixels(path, slot, new ThemeSlot { Image = "x", Blur = slot == ThemeSlots.Background ? 40 : 0 });
            Assert.NotNull(shown);
            var limit = slot == ThemeSlots.Background ? ThemeImages.BlurSide : ThemeImages.ShowSide(slot);
            Assert.InRange(Math.Max(shown.Width, shown.Height), 1, limit);
            Assert.True(shown.Width <= width && shown.Height <= height, $"{slot}: {shown.Width}x{shown.Height}");
        }
    }

    [Fact]
    public void ASmallPictureIsNeverScaledUp()
    {
        var path = Picture("small.jpg", 120, 60, SKEncodedImageFormat.Jpeg);
        using var shown = ThemeImages.LoadPixels(path, ThemeSlots.Background, new ThemeSlot { Image = "x", Blur = 12 });
        Assert.Equal((120, 60), (shown!.Width, shown.Height));
        var prepared = ThemeImages.Prepare(path, ThemeSlots.Sidebar, out _);
        Assert.Equal((120, 60), (prepared!.Width, prepared.Height));
    }

    [Fact]
    public void ALargeJpegIsDecodedStraightAtASmallerSize()
    {
        var path = Picture("photo.jpg", 6000, 4000, SKEncodedImageFormat.Jpeg);
        ThemeImages.Budget.ResetPeak();
        var prepared = ThemeImages.Prepare(path, ThemeSlots.Background, out var error);
        Assert.Null(error);
        Assert.Equal((1920, 1280), (prepared!.Width, prepared.Height));
        // A quarter or an eighth of 24 million pixels, plus the scaled and turned copies: far under a full decode.
        Assert.True(ThemeImages.Budget.Peak < 6000L * 4000, $"{ThemeImages.Budget.Peak}");
    }

    [Fact]
    public void FourSlotsLoadingAtOnceStayInsideTheBudget()
    {
        var paths = Enumerable.Range(0, 4).Select(i => Picture($"big-{i}.png", 4000, 3000)).ToArray();
        ThemeImages.Budget.ResetPeak();
        var results = Task.WhenAll(paths.Select((path, i) => Task.Run(() => ThemeImages.Prepare(path, ThemeSlots.All[i], out _)))).GetAwaiter().GetResult();
        Assert.All(results, Assert.NotNull);
        Assert.InRange(ThemeImages.Budget.Peak, 1, ThemeImages.MaxDecodePixels);
        var shown = Task.WhenAll(paths.Select((path, i) => Task.Run(() => ThemeImages.LoadPixels(path, ThemeSlots.All[i], new ThemeSlot { Image = "x", Blur = 20 })))).GetAwaiter().GetResult();
        try
        {
            Assert.All(shown, Assert.NotNull);
            Assert.InRange(ThemeImages.Budget.Peak, 1, ThemeImages.MaxDecodePixels);
        }
        finally
        {
            foreach (var s in shown) s?.Dispose();
        }
    }

    [Fact]
    public void TheBudgetIsReleasedWhenAPictureFailsToDecode()
    {
        // A real header and a broken body: the decode fails, and the pixels it held are given back.
        var good = File.ReadAllBytes(Picture("cut.png", 2000, 2000));
        var path = Path.Combine(_dir, "cut-short.png");
        File.WriteAllBytes(path, good[..60]);
        ThemeImages.Prepare(path, ThemeSlots.Background, out _);
        var whole = Picture("whole.png", 3000, 3000);
        // Would wait forever if the failed decode had kept its share.
        var done = Task.Run(() => Enumerable.Range(0, 3).Select(i => ThemeImages.Prepare(whole, ThemeSlots.Background, out _)).ToList()).Wait(TimeSpan.FromSeconds(30));
        Assert.True(done);
    }

    // ---- EXIF orientations ----

    /// <summary>Where the source pixel (x, y) of a w×h picture ends up for each EXIF orientation.</summary>
    private static (int X, int Y) Expected(SKEncodedOrigin origin, int x, int y, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => (w - 1 - x, y),
        SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
        SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
        SKEncodedOrigin.LeftTop => (y, x),
        SKEncodedOrigin.RightTop => (h - 1 - y, x),
        SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
        SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
        _ => (x, y),
    };

    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft)]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.BottomLeft)]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void EveryOrientationShowsThePictureTheRightWayUp(SKEncodedOrigin origin)
    {
        const int w = 3, h = 2;
        using var source = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                source.SetPixel(x, y, new SKColor((byte)(40 + 60 * x), (byte)(40 + 120 * y), 200));
        using var turned = ThemeImages.Orient(source, origin);
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        Assert.Equal(swaps ? (h, w) : (w, h), (turned.Width, turned.Height));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (tx, ty) = Expected(origin, x, y, w, h);
                Assert.Equal(source.GetPixel(x, y), turned.GetPixel(tx, ty));
            }
    }
}
