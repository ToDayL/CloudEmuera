namespace CloudEmuera.Infrastructure.Persistence;

public sealed class SessionRootMutationLeaseRow
{
    public string SessionId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public SessionRow? Session { get; set; }

    public CloudEmueraUser? ActorUser { get; set; }
}
