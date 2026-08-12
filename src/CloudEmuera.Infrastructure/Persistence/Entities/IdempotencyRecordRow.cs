namespace CloudEmuera.Infrastructure.Persistence;

public sealed class IdempotencyRecordRow
{
    public string ActorUserId { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestDigest { get; set; } = string.Empty;

    public IdempotencyRecordStatus Status { get; set; } = IdempotencyRecordStatus.InProgress;

    public int ResponseStatus { get; set; }

    public string ResponseJson { get; set; } = "{}";

    public string? ResourceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public CloudEmueraUser? ActorUser { get; set; }
}
