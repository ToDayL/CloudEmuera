namespace CloudEmuera.Infrastructure.Persistence;

public sealed class AuditEventRow
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }

    public string? ActorUserId { get; set; }

    public AuditActorType ActorType { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public string ResourceId { get; set; } = string.Empty;

    public string? RequestId { get; set; }

    public AuditResult Result { get; set; }

    public string? ReasonCode { get; set; }

    public string MetadataJson { get; set; } = "{}";
}
