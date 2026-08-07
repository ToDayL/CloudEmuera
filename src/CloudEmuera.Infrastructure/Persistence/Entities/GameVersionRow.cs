namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameVersionRow
{
    public string Id { get; set; } = string.Empty;

    public string GameId { get; set; } = string.Empty;

    public string VersionLabel { get; set; } = string.Empty;

    public GameVersionStatus Status { get; set; }

    public string? ContentDigest { get; set; }

    public string ContentPath { get; set; } = string.Empty;

    public string ManifestJson { get; set; } = "{}";

    public string RuntimeConfigJson { get; set; } = "{}";

    public string CompatibilitySummaryJson { get; set; } = "{}";

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int StateVersion { get; set; }

    public GameRow? Game { get; set; }

    public CloudEmueraUser? Creator { get; set; }

    public ICollection<SessionRow> Sessions { get; } = new List<SessionRow>();
}
