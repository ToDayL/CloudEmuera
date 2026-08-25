using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class SessionRow
{
    public string Id { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public string GameId { get; set; } = string.Empty;

    public string? SourceContentDigest { get; set; }

    public string SessionIdentityMode { get; set; } = "LEGACY_DIGEST";

    public string SessionSnapshotId { get; set; } = string.Empty;

    public long SourceContentRevision { get; set; }

    public string? SessionRootManifestDigest { get; set; }

    public int SaveLayout { get; set; }

    public string RuntimeVersion { get; set; } = string.Empty;

    public string SessionRootPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int FontSize { get; set; } = 18;

    public int LineHeight { get; set; } = 19;

    public string FontFaceId { get; set; } = "sarasa-fixed-sc-1.0.40-regular";

    public SessionWidthMode WidthMode { get; set; } = SessionWidthMode.Origin;

    public int? CustomWidth { get; set; }

    public bool ConvertBackslashToYen { get; set; } = true;

    public SessionState State { get; set; }

    public int StateVersion { get; set; }

    public long WorkerEpoch { get; set; }

    public bool WaitingForInput { get; set; }

    public string? CurrentPromptId { get; set; }

    public long LastOutputSequence { get; set; }

    public string? CloseReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public CloudEmueraUser? OwnerUser { get; set; }

    public GameRow? Game { get; set; }

    public WorkerLeaseRow? WorkerLease { get; set; }

    public SessionCreationOperationRow? CreationOperation { get; set; }

    public SessionRootMutationLeaseRow? MutationLease { get; set; }
}
