using System.Buffers.Binary;

namespace CloudEmuera.EmueraRuntime.Headless;

/// <summary>
/// Reads bounded WebP container metadata without decoding pixels. Static and
/// animated WebP resources are delivered to the browser as immutable assets;
/// the Worker only needs their canvas dimensions for Sprite rectangles.
/// </summary>
internal static class WebpMetadataReader
{
    internal const int MaximumDimension = 8_192;
    internal const long MaximumEncodedBytes = 64L * 1024 * 1024;

    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private static readonly byte[] Vp8StartCode = [0x9d, 0x01, 0x2a];

    internal static (int Width, int Height) Read(
        Stream stream,
        long length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateLength(length);

        Span<byte> header = stackalloc byte[RiffHeaderLength];
        stream.ReadExactly(header);
        return ReadAfterHeader(stream, length, header, cancellationToken);
    }

    internal static (int Width, int Height) ReadAfterHeader(
        Stream stream,
        long length,
        ReadOnlySpan<byte> header,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateLength(length);
        if (header.Length < RiffHeaderLength ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..12].SequenceEqual("WEBP"u8))
        {
            throw InvalidWebp();
        }

        uint riffPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        long riffEnd = checked(8L + riffPayloadLength);
        if (riffPayloadLength < 4 || riffEnd > length || riffEnd < RiffHeaderLength)
            throw InvalidWebp();

        int width = 0;
        int height = 0;
        bool hasImagePayload = false;
        long cursor = RiffHeaderLength;
        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderLength];
        Span<byte> probe = stackalloc byte[10];
        while (cursor < riffEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (riffEnd - cursor < ChunkHeaderLength)
                throw InvalidWebp();

            stream.ReadExactly(chunkHeader);
            uint chunkPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            long payloadStart = checked(cursor + ChunkHeaderLength);
            long payloadEnd = checked(payloadStart + chunkPayloadLength);
            long paddedEnd = checked(payloadEnd + (chunkPayloadLength & 1U));
            if (payloadEnd > riffEnd || paddedEnd > riffEnd)
                throw InvalidWebp();

            int probeLength = GetProbeLength(chunkHeader[..4], chunkPayloadLength);
            if (probeLength > 0)
            {
                stream.ReadExactly(probe[..probeLength]);
                if (chunkHeader[..4].SequenceEqual("VP8X"u8))
                {
                    (width, height) = ReadVp8xDimensions(probe);
                }
                else if (chunkHeader[..4].SequenceEqual("VP8 "u8))
                {
                    if (TryReadVp8Dimensions(probe, out int vp8Width, out int vp8Height))
                    {
                        width = vp8Width;
                        height = vp8Height;
                    }

                    hasImagePayload = true;
                }
                else if (chunkHeader[..4].SequenceEqual("VP8L"u8))
                {
                    (width, height) = ReadVp8lDimensions(probe);
                    hasImagePayload = true;
                }
                else if (chunkHeader[..4].SequenceEqual("ANMF"u8))
                {
                    hasImagePayload = true;
                }

                SkipExactly(stream, checked((long)chunkPayloadLength - probeLength));
            }
            else
            {
                if (chunkHeader[..4].SequenceEqual("ANMF"u8))
                    hasImagePayload = true;
                SkipExactly(stream, chunkPayloadLength);
            }

            if ((chunkPayloadLength & 1U) != 0)
                SkipExactly(stream, 1);
            cursor = paddedEnd;
        }

        if (cursor != riffEnd || width <= 0 || height <= 0 || !hasImagePayload)
            throw InvalidWebp();
        ValidateDimensions(width, height);
        return (width, height);
    }

    private static int GetProbeLength(ReadOnlySpan<byte> chunkType, uint payloadLength) =>
        chunkType.SequenceEqual("VP8X"u8) ? RequireProbeLength(payloadLength, 10) :
        chunkType.SequenceEqual("VP8 "u8) ? RequireProbeLength(payloadLength, 10) :
        chunkType.SequenceEqual("VP8L"u8) ? RequireProbeLength(payloadLength, 5) :
        0;

    private static int RequireProbeLength(uint payloadLength, int requiredLength)
    {
        if (payloadLength < requiredLength)
            throw InvalidWebp();
        return requiredLength;
    }

    private static (int Width, int Height) ReadVp8xDimensions(ReadOnlySpan<byte> payload)
    {
        int width = checked(ReadUInt24LittleEndian(payload[4..7]) + 1);
        int height = checked(ReadUInt24LittleEndian(payload[7..10]) + 1);
        ValidateDimensions(width, height);
        return (width, height);
    }

    private static bool TryReadVp8Dimensions(
        ReadOnlySpan<byte> payload,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        if ((payload[0] & 1) != 0 || !payload[3..6].SequenceEqual(Vp8StartCode))
            return false;

        width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]) & 0x3fff;
        height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..10]) & 0x3fff;
        ValidateDimensions(width, height);
        return true;
    }

    private static (int Width, int Height) ReadVp8lDimensions(ReadOnlySpan<byte> payload)
    {
        if (payload[0] != 0x2f)
            throw InvalidWebp();

        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
        int width = checked((int)(bits & 0x3fff) + 1);
        int height = checked((int)((bits >> 14) & 0x3fff) + 1);
        ValidateDimensions(width, height);
        return (width, height);
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | (value[1] << 8) | (value[2] << 16);

    private static void ValidateLength(long length)
    {
        if (length <= 0 || length > MaximumEncodedBytes)
            throw new InvalidDataException("The WebP encoded payload exceeds its limit.");
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
            throw new InvalidDataException("The WebP has invalid or oversized dimensions.");
    }

    private static void SkipExactly(Stream stream, long count)
    {
        Span<byte> buffer = stackalloc byte[8 * 1024];
        while (count > 0)
        {
            int read = stream.Read(buffer[..(int)Math.Min(count, buffer.Length)]);
            if (read == 0)
                throw InvalidWebp();
            count -= read;
        }
    }

    private static InvalidDataException InvalidWebp() =>
        new("The WebP container is invalid or truncated.");
}
