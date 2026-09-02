// CloudEmuera modification: fast PNG projection for the Linux headless
// Console. The Emuera image model remains System.Drawing-backed; this adapter
// only serializes an already-rendered 32bpp surface for browser/IPC output.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using SkiaSharp;

namespace MinorShift.Emuera.GameView;

internal readonly record struct HeadlessPngEncodingResult(byte[] PngData, string Backend);

internal static class HeadlessPngEncoder
{
    // The benchmarked fast profile avoids the per-row filter search and keeps
    // a small amount of zlib compression. It preserves PNG losslessness while
    // avoiding the high CPU cost of the default filter/compression heuristics.
    internal const int FastZLibLevel = 1;
    internal const string FastBackend = "skia-fast";
    internal const string GdiPlusFallbackBackend = "gdiplus-fallback";

    private static int skiaUnavailable;

    internal static HeadlessPngEncodingResult Encode(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (Volatile.Read(ref skiaUnavailable) != 0)
            return new(EncodeWithGdiPlus(bitmap), GdiPlusFallbackBackend);

        try
        {
            return new(EncodeWithSkia(bitmap), FastBackend);
        }
        catch (Exception exception) when (IsSkiaUnavailable(exception))
        {
            // Keep the existing compatibility path usable if a deployment
            // omits or cannot load the Linux native Skia asset.
            Volatile.Write(ref skiaUnavailable, 1);
            return new(EncodeWithGdiPlus(bitmap), GdiPlusFallbackBackend);
        }
    }

    private static byte[] EncodeWithSkia(Bitmap bitmap)
    {
        BitmapData bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            long minimumRowBytes = checked((long)bitmap.Width * 4);
            if (bitmapData.Stride <= 0 || bitmapData.Stride < minimumRowBytes)
                throw new InvalidOperationException("The bitmap does not expose a positive 32bpp row stride.");

            var imageInfo = new SKImageInfo(
                bitmap.Width,
                bitmap.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul);
            using var pixmap = new SKPixmap(imageInfo, bitmapData.Scan0, bitmapData.Stride);
            using SKData encoded = pixmap.Encode(
                new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, FastZLibLevel))
                ?? throw new InvalidOperationException("SkiaSharp returned no PNG data.");
            return encoded.ToArray();
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static byte[] EncodeWithGdiPlus(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static bool IsSkiaUnavailable(Exception exception)
    {
        while (true)
        {
            if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                return true;

            if (exception is TypeInitializationException { InnerException: not null } initialization)
            {
                exception = initialization.InnerException;
                continue;
            }

            return false;
        }
    }
}
