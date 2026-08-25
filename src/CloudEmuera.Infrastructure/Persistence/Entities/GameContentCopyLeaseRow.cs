namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameContentCopyLeaseRow
{
    public string Id { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public long ContentRevision { get; set; }
    public string? ContentDigest { get; set; }
    public string? SourceContentPath { get; set; }
    public string ConsumerType { get; set; } = string.Empty;
    public string ConsumerId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public GameRow? Game { get; set; }
}
