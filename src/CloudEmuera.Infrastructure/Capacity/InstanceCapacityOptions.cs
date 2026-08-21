namespace CloudEmuera.Infrastructure.Capacity;

/// <summary>
/// The trusted self-hosted MVP has one capacity budget for the whole instance.
/// The database still carries the historical quota-profile columns for schema
/// compatibility, but runtime admission never reads them.
/// </summary>
public sealed record InstanceCapacityOptions
{
    public const int DefaultMaxActiveWorkers = 8;
    public const int DefaultMaxInactiveSessions = 64;
    public const long DefaultMaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultMaxGamePackageBytes = DefaultMaxArchiveBytes;
    public const long DefaultMaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const long DefaultMaxArchiveSingleFileBytes = 1L * 1024 * 1024 * 1024;
    public const int DefaultMaxArchiveEntryCount = 50_000;
    public const long DefaultMaxSessionRootBytes = 4L * 1024 * 1024 * 1024;
    public const int DefaultMaxSessionRootFileCount = 50_000;
    public const long DefaultMaxStagingReservedBytes = 12L * 1024 * 1024 * 1024;
    public const long DefaultMinDataRootFreeBytes = 1L * 1024 * 1024 * 1024;
    public const long DefaultMaxSaveFileBytes = 64L * 1024 * 1024;
    public const int DefaultMaxSaveListedFiles = 4_096;
    public const long DefaultMaxSaveListBytes = 8L * 1024 * 1024;

    public const int AbsoluteMaxWorkerCount = 4_096;
    public const int AbsoluteMaxInactiveSessionCount = 4_096;
    public const int AbsoluteMaxArchiveEntryCount = 65_535;
    public const int AbsoluteMaxSessionRootFileCount = 1_000_000;
    public const long AbsoluteMaxCapacityBytes = 16L * 1024 * 1024 * 1024 * 1024;

    private long archiveBytes = DefaultMaxArchiveBytes;

    public int MaxActiveWorkers { get; init; } = DefaultMaxActiveWorkers;
    public int MaxInactiveSessions { get; init; } = DefaultMaxInactiveSessions;
    public long MaxArchiveBytes
    {
        get => archiveBytes;
        init => archiveBytes = value;
    }

    /// <summary>
    /// Compatibility alias for the pre-P1-13 key/property. New code must use
    /// <see cref="MaxArchiveBytes"/>.
    /// </summary>
    public long MaxGamePackageBytes
    {
        get => archiveBytes;
        init => archiveBytes = value;
    }

    public long MaxExpandedBytes { get; init; } = DefaultMaxExpandedBytes;
    public long MaxArchiveSingleFileBytes { get; init; } = DefaultMaxArchiveSingleFileBytes;
    public int MaxArchiveEntryCount { get; init; } = DefaultMaxArchiveEntryCount;
    public long MaxSessionRootBytes { get; init; } = DefaultMaxSessionRootBytes;
    public int MaxSessionRootFileCount { get; init; } = DefaultMaxSessionRootFileCount;
    public long MaxStagingReservedBytes { get; init; } = DefaultMaxStagingReservedBytes;
    public long MinDataRootFreeBytes { get; init; } = DefaultMinDataRootFreeBytes;
    public long MaxSaveFileBytes { get; init; } = DefaultMaxSaveFileBytes;
    public int MaxSaveListedFiles { get; init; } = DefaultMaxSaveListedFiles;
    public long MaxSaveListBytes { get; init; } = DefaultMaxSaveListBytes;

    public static InstanceCapacityOptions Default => new();

    public void Validate()
    {
        if (MaxActiveWorkers is <= 0 or > AbsoluteMaxWorkerCount
            || MaxInactiveSessions is <= 0 or > AbsoluteMaxInactiveSessionCount
            || MaxArchiveBytes <= 0
            || MaxExpandedBytes <= 0
            || MaxArchiveSingleFileBytes <= 0
            || MaxArchiveEntryCount is <= 0 or > AbsoluteMaxArchiveEntryCount
            || MaxSessionRootBytes <= 0
            || MaxSessionRootFileCount is <= 0 or > AbsoluteMaxSessionRootFileCount
            || MaxStagingReservedBytes <= 0
            || MaxSaveFileBytes <= 0
            || MaxSaveListedFiles is <= 0 or > AbsoluteMaxSessionRootFileCount
            || MaxSaveListBytes <= 0
            || MaxSaveFileBytes > MaxSessionRootBytes
            || MaxArchiveSingleFileBytes > MaxExpandedBytes
            || MaxExpandedBytes > MaxSessionRootBytes
            || MaxArchiveBytes > AbsoluteMaxCapacityBytes
            || MaxExpandedBytes > AbsoluteMaxCapacityBytes
            || MaxArchiveSingleFileBytes > AbsoluteMaxCapacityBytes
            || MaxSessionRootBytes > AbsoluteMaxCapacityBytes
            || MaxStagingReservedBytes > AbsoluteMaxCapacityBytes
            || MaxSaveFileBytes > AbsoluteMaxCapacityBytes
            || MaxSaveListBytes > AbsoluteMaxCapacityBytes
            || MaxArchiveBytes > MaxStagingReservedBytes - MaxExpandedBytes
            || MinDataRootFreeBytes < 0
            || MinDataRootFreeBytes > AbsoluteMaxCapacityBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(InstanceCapacityOptions), "Instance capacity options are inconsistent.");
        }

        if (MaxArchiveBytes + MaxExpandedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStagingReservedBytes), "The staging reservation overflows.");
    }
}
