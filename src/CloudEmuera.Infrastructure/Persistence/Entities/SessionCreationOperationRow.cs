using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class SessionCreationOperationRow
{
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string ActorUserId { get; set; } = string.Empty;

    public SessionCreationOperationStatus Status { get; set; }

    public string StagingPath { get; set; } = string.Empty;

    public long ReservedBytes { get; set; }

    public long ExpectedFileCount { get; set; }

    public long ExpectedContentBytes { get; set; }

    public int AttemptCount { get; set; }

    public string? LastErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int StateVersion { get; set; }

    public SessionRow? Session { get; set; }

    public CloudEmueraUser? ActorUser { get; set; }
}
