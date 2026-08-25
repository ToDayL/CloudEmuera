using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

/// <summary>Reads PNG metadata without constructing a bitmap or graphics device.</summary>
public sealed class RuntimeImageMetadataPort(IRuntimeFileSystem fileSystem) : IRuntimeImagePort
{
    private const int MaximumDimension = 8_192;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public RuntimeImageMetadata Load(RuntimeFilePath resourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resourcePath.Area != RuntimeFileArea.GameContent)
        {
            throw new RuntimeFileAccessException(
                RuntimePathReasonCodes.PathOutsideArea,
                "Images must be supplied from GameContent.",
                resourcePath.LogicalPath,
                resourcePath.Area);
        }

        RuntimeFilePath canonicalPath = fileSystem.ResolveExistingPath(resourcePath, cancellationToken);
        using Stream stream = fileSystem.OpenRead(canonicalPath, cancellationToken);
        long length = stream.CanSeek ? stream.Length : fileSystem.GetMetadata(resourcePath, cancellationToken).Length;
        if (length <= 0 || length > WebpMetadataReader.MaximumEncodedBytes)
            throw new InvalidDataException("The encoded image payload exceeds its limit.");

        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        int width;
        int height;
        string mediaType;
        if (header[..8].SequenceEqual(PngSignature))
        {
            Span<byte> pngHeader = stackalloc byte[24];
            header.CopyTo(pngHeader);
            stream.ReadExactly(pngHeader[12..24]);
            if (!pngHeader[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Only a valid PNG resource is supported by the headless image port.");

            width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pngHeader[16..20]);
            height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(pngHeader[20..24]);
            if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
                throw new InvalidDataException("The PNG has invalid or oversized dimensions.");
            mediaType = "image/png";
        }
        else if (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            (width, height) = WebpMetadataReader.ReadAfterHeader(stream, length, header, cancellationToken);
            mediaType = "image/webp";
        }
        else
        {
            throw new InvalidDataException("Only valid PNG or WebP resources are supported by the headless image port.");
        }

        return new RuntimeImageMetadata(
            ConsoleAssetIdCodec.EncodePath(canonicalPath.LogicalPath),
            mediaType,
            width,
            height,
            length);
    }
}
