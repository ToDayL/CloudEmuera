namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameRow
{
    public string Id { get; set; } = string.Empty;

    public string OwnerUserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public GameVisibility Visibility { get; set; }

    public GameStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int StateVersion { get; set; }

    public CloudEmueraUser? OwnerUser { get; set; }

    public ICollection<GameVersionRow> Versions { get; } = new List<GameVersionRow>();

    public ICollection<SessionRow> Sessions { get; } = new List<SessionRow>();
}
