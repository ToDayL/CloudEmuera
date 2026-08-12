using System.Buffers.Binary;
using System.Security.Cryptography;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.EmueraRuntime.Headless;

/// <summary>Reads PNG metadata without constructing a bitmap or graphics device.</summary>
public sealed class RuntimeImageMetadataPort(IRuntimeFileSystem fileSystem) : IRuntimeImagePort
{
    private const int MaximumDimension = 8_192;
    private const long MaximumEncodedBytes = 64L * 1024 * 1024;
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

        using Stream stream = fileSystem.OpenRead(resourcePath, cancellationToken);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("Only a valid PNG resource is supported by the headless image port.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension)
        {
            throw new InvalidDataException("The PNG has invalid or oversized dimensions.");
        }

        long length = stream.CanSeek ? stream.Length : fileSystem.GetMetadata(resourcePath, cancellationToken).Length;
        if (length <= 0 || length > MaximumEncodedBytes)
            throw new InvalidDataException("The PNG encoded payload exceeds its limit.");
        if (stream.CanSeek)
            stream.Position = 0;
        string digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new RuntimeImageMetadata($"sha256-{digest}", "image/png", width, height, length);
    }
}
