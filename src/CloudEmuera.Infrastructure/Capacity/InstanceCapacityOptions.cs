namespace CloudEmuera.Infrastructure.Capacity;

/// <summary>
/// The trusted self-hosted MVP has one capacity budget for the whole instance.
/// The database still carries the historical quota-profile columns for schema
/// compatibility, but runtime admission never reads them.
/// </summary>
public sealed record InstanceCapacityOptions
{
    public const int DefaultMaxActiveWorkers = 4;
    public const long DefaultMaxGamePackageBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultMaxSessionRootBytes = 4L * 1024 * 1024 * 1024;
    public const long DefaultMaxStagingReservedBytes = 12L * 1024 * 1024 * 1024;
    public const long DefaultMinDataRootFreeBytes = 1L * 1024 * 1024 * 1024;
    public const long DefaultMaxSaveFileBytes = 64L * 1024 * 1024;

    public int MaxActiveWorkers { get; init; } = DefaultMaxActiveWorkers;
    public long MaxGamePackageBytes { get; init; } = DefaultMaxGamePackageBytes;
    public long MaxSessionRootBytes { get; init; } = DefaultMaxSessionRootBytes;
    public long MaxStagingReservedBytes { get; init; } = DefaultMaxStagingReservedBytes;
    public long MinDataRootFreeBytes { get; init; } = DefaultMinDataRootFreeBytes;
    public long MaxSaveFileBytes { get; init; } = DefaultMaxSaveFileBytes;

    public static InstanceCapacityOptions Default => new();

    public void Validate()
    {
        if (MaxActiveWorkers is <= 0 or > 4096
            || MaxGamePackageBytes <= 0
            || MaxSessionRootBytes <= 0
            || MaxStagingReservedBytes <= 0
            || MaxSaveFileBytes <= 0
            || MaxSaveFileBytes > MaxSessionRootBytes
            || MinDataRootFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InstanceCapacityOptions), "Instance capacity options are inconsistent.");
        }
    }
}
