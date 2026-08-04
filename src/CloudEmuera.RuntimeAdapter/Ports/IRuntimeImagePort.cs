namespace CloudEmuera.RuntimeAdapter;

public sealed record RuntimeImageMetadata
{
    public RuntimeImageMetadata(
        string resourceId,
        string mediaType,
        int width,
        int height,
        long byteLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (width < 0 || height < 0 || byteLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        ResourceId = resourceId;
        MediaType = mediaType;
        Width = width;
        Height = height;
        ByteLength = byteLength;
    }

    public string ResourceId { get; }

    public string MediaType { get; }

    public int Width { get; }

    public int Height { get; }

    public long ByteLength { get; }
}

/// <summary>
/// Platform-neutral image capability. The resource is controlled by the file
/// area and is not an arbitrary host path. Pixel buffers and desktop image
/// objects are intentionally outside this P0-02 contract.
/// </summary>
public interface IRuntimeImagePort
{
    RuntimeImageMetadata Load(RuntimeFilePath resourcePath, CancellationToken cancellationToken = default);

    RuntimeImageMetadata GetMetadata(
        RuntimeFilePath resourcePath,
        CancellationToken cancellationToken = default) => Load(resourcePath, cancellationToken);
}
