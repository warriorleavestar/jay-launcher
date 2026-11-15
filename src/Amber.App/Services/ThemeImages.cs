using System.Runtime.InteropServices;
using Amber.Core.Themes;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Amber.App.Services;

/// <summary>
/// Pictures for the theme's image slots, with the Skia that comes with Avalonia: turning a picked file into what goes in
/// the pack (scaled down, PNG or JPEG), the small thumbnail the colours are picked from, and the bitmap a slot shows
/// (the background already blurred, so the window doesn't blur it again on every frame).
/// </summary>
internal static class ThemeImages
{
    /// <summary>Larger files are refused before decoding.</summary>
    public const long MaxSourceBytes = 40L * 1024 * 1024;

    /// <summary>
    /// Pictures that say they are bigger than this (width × height, read from the header) are refused before decoding:
    /// a small, highly compressed file can claim a huge picture.
    /// </summary>
    public const long MaxSourcePixels = 100L * 1000 * 1000;

    /// <summary>
    /// The most pixels held at once while pictures are read, across every picture: the decoded picture, the scaled one,
    /// the turned one and the blurred one all count (JPEG and WebP decode straight at a smaller size; a picture that
    /// can't fit is refused). Each picture in flight holds its share until it is done, so four slots loading together
    /// never hold more than this.
    /// </summary>
    public const long MaxDecodePixels = 40L * 1000 * 1000;

    /// <summary>The longest side a slot's picture is shown at (never scaled up).</summary>
    public static int ShowSide(string slot) => slot switch
    {
        ThemeSlots.Sidebar => 1600,
        ThemeSlots.Mascot => 1200,
        _ => 2560,
    };

    /// <summary>A blurred background is blurred at this longest side (a blurred picture loses nothing at that size).</summary>
    public const int BlurSide = 1280;

    /// <summary>Files the "make a theme from pictures" panel accepts (WebP, BMP and GIF are converted to PNG).</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>A picture ready for a slot: the encoded bytes for the pack and a thumbnail for picking colours.</summary>
    public sealed record Prepared(byte[] Bytes, string Extension, int Width, int Height, uint[] Thumbnail);

    /// <summary>Reads, scales down, turns upright and encodes a picture for a slot. Null with an error key when it can't.</summary>
    public static Prepared? Prepare(string path, string slot, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "theme.image.error.missing"; return null; }
            if (info.Length > MaxSourceBytes) { error = "theme.image.error.too_big"; return null; }
            using var sized = Decode(path, ImageTheme.MaxSide(slot), out error);
            if (sized is null) return null;
            // A mascot needs its transparent parts; a photo without transparency is smaller as JPEG.
            var opaque = slot != ThemeSlots.Mascot && IsOpaque(sized);
            using var data = opaque ? sized.Encode(SKEncodedImageFormat.Jpeg, 88) : sized.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) { error = "theme.image.error.unreadable"; return null; }
            return new Prepared(data.ToArray(), opaque ? ".jpg" : ".png", sized.Width, sized.Height, Thumbnail(sized));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            error = "theme.image.error.unreadable";
            return null;
        }
    }

    /// <summary>
    /// Decodes a picture with its longest side at most <paramref name="maxSide"/> (never larger than the picture itself),
    /// upright. The size is checked from the header first; the full-size picture is never copied or turned. Null with
    /// an error key when it is too big or can't be read.
    /// </summary>
    internal static SKBitmap? Decode(string path, int maxSide, out string? error)
    {
        error = null;
        using var codec = SKCodec.Create(path);
        if (codec is null) { error = "theme.image.error.unreadable"; return null; }
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0) { error = "theme.image.error.unreadable"; return null; }
        if ((long)width * height > MaxSourcePixels) { error = "theme.image.error.too_big"; return null; }
        var (w, h) = Fit(width, height, maxSide);
        // JPEG and WebP decode straight at a smaller size; other formats come at full size and are scaled after.
        var scale = (float)w / width;
        var decodeSize = scale < 1 ? codec.GetScaledDimensions(scale) : codec.Info.Size;
        // A scaled size the codec can't give (or one smaller than asked for) means decoding at full size.
        if (decodeSize.Width < w || decodeSize.Height < h || decodeSize.Width > width || decodeSize.Height > height) decodeSize = codec.Info.Size;
        // The decoded picture, the scaled copy and the turned copy.
        var pixels = checked((long)decodeSize.Width * decodeSize.Height + 2L * w * h);
        if (pixels > MaxDecodePixels) { error = "theme.image.error.too_big"; return null; }
        using var lease = Budget.Take(pixels);
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        SKBitmap? decoded = new(decodeInfo);
        SKBitmap? sized;
        try
        {
            var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { error = "theme.image.error.unreadable"; return null; }
            if (decodeSize.Width == w && decodeSize.Height == h) (sized, decoded) = (decoded, null);
            else sized = decoded.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        finally
        {
            decoded?.Dispose();
        }
        if (sized is null) { error = "theme.image.error.unreadable"; return null; }
        if (codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return sized;
        using (sized) return Orient(sized, codec.EncodedOrigin);
    }

    /// <summary>The size with the longest side at most <paramref name="maxSide"/>, keeping the shape; never larger.</summary>
    internal static (int Width, int Height) Fit(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0 || maxSide <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var longest = Math.Max(width, height);
        if (longest <= maxSide) return (width, height);
        var scale = (double)maxSide / longest;
        return (Math.Clamp((int)Math.Round(width * scale), 1, maxSide), Math.Clamp((int)Math.Round(height * scale), 1, maxSide));
    }

    /// <summary>Pixels being decoded right now, shared by every picture (see <see cref="MaxDecodePixels"/>).</summary>
    internal static class Budget
    {
        private static readonly object Gate = new();
        private static long _inUse;

        /// <summary>The most pixels ever held at once (self-checks and tests).</summary>
        internal static long Peak { get; private set; }

        internal static IDisposable Take(long pixels)
        {
            if (pixels <= 0 || pixels > MaxDecodePixels) throw new ArgumentOutOfRangeException(nameof(pixels));
            lock (Gate)
            {
                while (_inUse + pixels > MaxDecodePixels) Monitor.Wait(Gate);
                _inUse += pixels;
                Peak = Math.Max(Peak, _inUse);
            }
            return new Lease(pixels);
        }

        internal static void ResetPeak()
        {
            lock (Gate) Peak = _inUse;
        }

        private sealed class Lease(long pixels) : IDisposable
        {
            private long _pixels = pixels;

            public void Dispose()
            {
                var pixels = Interlocked.Exchange(ref _pixels, 0);
                if (pixels == 0) return;
                lock (Gate)
                {
                    _inUse -= pixels;
                    Monitor.PulseAll(Gate);
                }
            }
        }
    }

    /// <summary>The picture as at most 64×64 pixels (ARGB), for <see cref="ImageTheme.Extract"/>.</summary>
    public static uint[] Thumbnail(SKBitmap bitmap)
    {
        var scale = Math.Min(1.0, 64.0 / Math.Max(bitmap.Width, bitmap.Height));
        var w = Math.Max(1, (int)(bitmap.Width * scale));
        var h = Math.Max(1, (int)(bitmap.Height * scale));
        using var small = bitmap.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null) return [];
        return small.Pixels.Select(c => (uint)c).ToArray();
    }

    /// <summary>The bitmap a slot shows, or null when the file is missing, too big or broken (the slot then stays empty).</summary>
    public static Bitmap? Load(string file, string slot, ThemeSlot settings)
    {
        try
        {
            using var pixels = LoadPixels(file, slot, settings);
            return pixels is null ? null : ToBitmap(pixels);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pixels a slot shows: at most <see cref="ShowSide"/> on the longest side, never scaled up. A blurred background
    /// is blurred once at <see cref="BlurSide"/> (so the window doesn't blur it again on every frame); the radius is in
    /// pixels of a picture 1920 on its longest side, so the same pack looks the same whatever the size of its file.
    /// </summary>
    internal static SKBitmap? LoadPixels(string file, string slot, ThemeSlot settings)
    {
        if (!File.Exists(file) || new FileInfo(file).Length > MaxSourceBytes) return null;
        var blur = slot == ThemeSlots.Background && settings.Blur > 0;
        var source = Decode(file, blur ? BlurSide : ShowSide(slot), out _);
        if (source is null || !blur) return source;
        using (source) return Blur(source, settings.Blur);
    }

    private static SKBitmap? Blur(SKBitmap source, double radius)
    {
        // The surface, its snapshot and the result.
        using var lease = Budget.Take(checked(3L * source.Width * source.Height));
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return null;
        var sigma = (float)(radius * Math.Max(source.Width, source.Height) / 1920 / 2);
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null) })
        using (var image = SKImage.FromBitmap(source))
            surface.Canvas.DrawImage(image, new SKRect(0, 0, source.Width, source.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        using var snapshot = surface.Snapshot();
        var result = new SKBitmap(info);
        if (!snapshot.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0)) { result.Dispose(); return null; }
        return result;
    }

    private static Bitmap ToBitmap(SKBitmap pixels)
    {
        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var result = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = result.Lock())
        using (var source = pixels.PeekPixels())
        {
            if (source is null || !source.ReadPixels(info, buffer.Address, buffer.RowBytes, 0, 0))
            {
                result.Dispose();
                throw new InvalidOperationException("The picture could not be copied.");
            }
        }
        return result;
    }

    private static bool IsOpaque(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return true;
        foreach (var c in bitmap.Pixels)
            if (c.Alpha < 255) return false;
        return true;
    }

    /// <summary>
    /// Phone photos are often stored sideways or mirrored, with a note (EXIF orientation 1–8) saying which way is up.
    /// Turns the (already small) picture so it shows as meant.
    /// </summary>
    internal static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var (sw, sh) = (bitmap.Width, bitmap.Height);
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swaps ? (sh, sw) : (sw, sh);
        // Where each source pixel goes: x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY.
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 2: mirrored left to right
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 3: upside down
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 4: mirrored top to bottom
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 5: transposed
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),     // 6: needs a quarter turn clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1), // 7: transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),   // 8: needs a quarter turn anticlockwise
            _ => SKMatrix.Identity,
        };
        var result = new SKBitmap(new SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(bitmap, 0, 0);
        return result;
    }
}
