namespace CloudEmuera.Infrastructure.Persistence;

public sealed class QuotaProfileRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long MaxActiveSessions { get; set; }

    public long MaxGamePackageBytes { get; set; }

    public long MaxSessionBytes { get; set; }

    public long MaxOutputBytesPerSecond { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int StateVersion { get; set; }

    public ICollection<CloudEmueraUser> Users { get; } = new List<CloudEmueraUser>();
}
