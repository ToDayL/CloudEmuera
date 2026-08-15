namespace CloudEmuera.Infrastructure.Persistence;

public sealed class SaveFileOperationRow
{
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string IdempotencyScope { get; set; } = string.Empty;

    public string IdempotencyKeyHash { get; set; } = string.Empty;

    public SaveFileOperationType Type { get; set; }

    public SaveFileOperationStatus Status { get; set; }

    public string? SourcePath { get; set; }

    public string TargetPath { get; set; } = string.Empty;

    public string? PayloadPath { get; set; }

    public long? PayloadSize { get; set; }

    public string? PayloadDigest { get; set; }

    public string? ExpectedSourceIdentityJson { get; set; }

    public bool ExpectedTargetCaptured { get; set; }

    public bool ExpectedTargetExists { get; set; }

    public string? ExpectedTargetIdentityJson { get; set; }

    public string ResultJson { get; set; } = "{}";

    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int StateVersion { get; set; }

    public SessionRow? Session { get; set; }

    public CloudEmueraUser? ActorUser { get; set; }
}
