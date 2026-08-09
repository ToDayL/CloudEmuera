namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameRow
{
    public string Id { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public GameVisibility Visibility { get; set; }

    public GameStatus Status { get; set; }

    public GameWorkspaceStatus WorkspaceStatus { get; set; }

    public string? WorkspacePath { get; set; }

    public string? CurrentContentPath { get; set; }

    public string? ContentDigest { get; set; }

    public long ContentRevision { get; set; }

    public string ManifestJson { get; set; } = "{}";

    public string RuntimeConfigJson { get; set; } = "{}";

    public string CompatibilitySummaryJson { get; set; } = "{}";

    public string? ActivatedBy { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public string? DeletedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int StateVersion { get; set; }

    public CloudEmueraUser? OwnerUser { get; set; }

    public ICollection<SessionRow> Sessions { get; } = new List<SessionRow>();
}
