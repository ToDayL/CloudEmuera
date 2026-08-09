namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GamePackageIngestionRow
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public GamePackageIngestionStatus Status { get; set; }
    public string StagingPath { get; set; } = string.Empty;
    public long ReservedBytes { get; set; }
    public long ArchiveBytes { get; set; }
    public long ExpandedBytes { get; set; }
    public int EntryCount { get; set; }
    public string? ArchiveDigest { get; set; }
    public string? ContentDigest { get; set; }
    public string LimitsJson { get; set; } = "{}";
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ReservationReleasedAt { get; set; }
    public DateTimeOffset? CleanupCompletedAt { get; set; }
    public int StateVersion { get; set; }
    public CloudEmueraUser Owner { get; set; } = null!;
}

public enum GamePackageIngestionStatus
{
    Reserved,
    Receiving,
    Inspecting,
    Extracting,
    Analyzing,
    Ready,
    Consuming,
    Consumed,
    Failed,
    Abandoned,
}
