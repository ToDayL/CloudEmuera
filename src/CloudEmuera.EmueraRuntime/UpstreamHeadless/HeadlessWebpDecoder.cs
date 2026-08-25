using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CloudEmuera.EmueraRuntime.UpstreamHeadless;

/// <summary>
/// Decodes WebP resources for the pinned upstream AppContents registry.
/// Static PRINT_IMG resources use a metadata-only path and do not need pixel
/// decoding; this bridge is required by native
/// SPRITECREATED/GDRAWSPRITE/CBGSETSPRITE paths that consume Bitmap objects.
/// </summary>
internal static class HeadlessWebpDecoder
{
    private const int MaximumDimension = 8_192;
    private const long MaximumEncodedBytes = 64L * 1024 * 1024;

    internal static Bitmap Decode(string path)
    {
        try
        {
            using System.IO.FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > MaximumEncodedBytes || length > int.MaxValue)
                return null;

            byte[] encoded = new byte[(int)length];
            stream.ReadExactly(encoded);
            if (WebPGetInfo(encoded, (nuint)encoded.Length, out int width, out int height) != 1 ||
                width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
            {
                return null;
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bitmapData = null;
            bool decoded = false;
            try
            {
                bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                if (bitmapData.Stride > 0)
                {
                    long outputBytes = checked((long)bitmapData.Stride * height);
                    IntPtr output = WebPDecodeBGRAInto(
                        encoded,
                        (nuint)encoded.Length,
                        bitmapData.Scan0,
                        (nuint)outputBytes,
                        bitmapData.Stride);
                    decoded = output != IntPtr.Zero;
                }
            }
            finally
            {
                if (bitmapData is not null)
                    bitmap.UnlockBits(bitmapData);
            }

            if (!decoded)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libwebp.so.7", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPGetInfo")]
    private static extern int WebPGetInfo(
        byte[] data,
        nuint dataSize,
        out int width,
        out int height);

    [DllImport("libwebp.so.7", CallingConvention = CallingConvention.Cdecl, EntryPoint = "WebPDecodeBGRAInto")]
    private static extern IntPtr WebPDecodeBGRAInto(
        byte[] data,
        nuint dataSize,
        IntPtr outputBuffer,
        nuint outputBufferSize,
        int outputStride);
}
