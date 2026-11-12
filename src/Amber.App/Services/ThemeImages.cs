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

    /// <summary>Files the "make a theme from pictures" panel accepts (WebP, BMP and GIF are converted to PNG).</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    /// <summary>A picture ready for a slot: the encoded bytes for the pack and a thumbnail for picking colours.</summary>
    public sealed record Prepared(byte[] Bytes, string Extension, int Width, int Height, uint[] Thumbnail);

    /// <summary>Reads, turns upright, scales down and encodes a picture for a slot. Null with an error key when it can't.</summary>
    public static Prepared? Prepare(string path, string slot, out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "theme.image.error.missing"; return null; }
            if (info.Length > MaxSourceBytes) { error = "theme.image.error.too_big"; return null; }
            using var codec = SKCodec.Create(path);
            if (codec is null) { error = "theme.image.error.unreadable"; return null; }
            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null) { error = "theme.image.error.unreadable"; return null; }
            using var upright = Upright(decoded, codec.EncodedOrigin);
            var max = ImageTheme.MaxSide(slot);
            var scale = Math.Min(1.0, (double)max / Math.Max(upright.Width, upright.Height));
            var w = Math.Max(1, (int)Math.Round(upright.Width * scale));
            var h = Math.Max(1, (int)Math.Round(upright.Height * scale));
            using var sized = scale < 1
                ? upright.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                : upright.Copy(SKColorType.Rgba8888);
            if (sized is null) { error = "theme.image.error.unreadable"; return null; }
            // A mascot needs its transparent parts; a photo without transparency is smaller as JPEG.
            var opaque = slot != ThemeSlots.Mascot && IsOpaque(sized);
            using var data = opaque ? sized.Encode(SKEncodedImageFormat.Jpeg, 88) : sized.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) { error = "theme.image.error.unreadable"; return null; }
            return new Prepared(data.ToArray(), opaque ? ".jpg" : ".png", w, h, Thumbnail(sized));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            error = "theme.image.error.unreadable";
            return null;
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

    /// <summary>The bitmap a slot shows, or null when the file is missing or broken (the slot then stays empty).</summary>
    public static Bitmap? Load(string file, string slot, ThemeSlot settings)
    {
        try
        {
            if (slot == ThemeSlots.Background && settings.Blur > 0) return Blurred(file, settings.Blur);
            using var stream = File.OpenRead(file);
            return slot switch
            {
                ThemeSlots.Sidebar => Bitmap.DecodeToWidth(stream, 800),
                ThemeSlots.Mascot => Bitmap.DecodeToHeight(stream, 1200),
                _ => Bitmap.DecodeToWidth(stream, 2560),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Blurs once, at 1280 wide (a blurred picture loses nothing at that size). The radius is in pixels of a 1920-wide
    /// picture, so the same pack looks the same whatever the size of its file.
    /// </summary>
    private static Bitmap? Blurred(string file, double radius)
    {
        using var source = SKBitmap.Decode(file);
        if (source is null) return null;
        const int width = 1280;
        var height = Math.Max(1, (int)Math.Round((double)source.Height * width / source.Width));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null) return null;
        var sigma = (float)(radius * width / 1920 / 2);
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null) })
        using (var image = SKImage.FromBitmap(source))
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        using var snapshot = surface.Snapshot();
        var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = result.Lock())
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            snapshot.ReadPixels(info, buffer.Address, buffer.RowBytes, 0, 0);
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

    /// <summary>Phone photos are often stored sideways with a note saying which way is up.</summary>
    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var (w, h, degrees) = origin switch
        {
            SKEncodedOrigin.BottomRight => (bitmap.Width, bitmap.Height, 180),
            SKEncodedOrigin.RightTop => (bitmap.Height, bitmap.Width, 90),
            SKEncodedOrigin.LeftBottom => (bitmap.Height, bitmap.Width, 270),
            _ => (bitmap.Width, bitmap.Height, 0),
        };
        if (degrees == 0) return bitmap.Copy();
        var rotated = new SKBitmap(w, h, bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }
}
