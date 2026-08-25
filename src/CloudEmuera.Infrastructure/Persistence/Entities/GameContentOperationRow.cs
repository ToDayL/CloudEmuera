namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameContentOperationRow
{
    public string Id { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public GameContentOperationType OperationType { get; set; }
    public GameContentOperationStatus Status { get; set; }
    public GameContentOperationStage Stage { get; set; }
    public string? CurrentItem { get; set; }
    public int ExpectedGameStateVersion { get; set; }
    public long ExpectedContentRevision { get; set; }
    public string? IngestionId { get; set; }
    public string? RequestId { get; set; }
    public string? WorkPath { get; set; }
    public string? ContentDigest { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int StateVersion { get; set; }
    public GameRow? Game { get; set; }
}
