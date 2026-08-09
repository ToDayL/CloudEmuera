namespace CloudEmuera.Infrastructure.GamePackages;

public sealed record GamePackageStorageOptions
{
    public required string DataRoot { get; init; }
    public long MaxStagingReservedBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long MinDataRootFreeBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public TimeSpan ReadyLifetime { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ConsumptionLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ReaperInterval { get; init; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataRoot) || MaxStagingReservedBytes <= 0 || MinDataRootFreeBytes < 0
            || ReadyLifetime <= TimeSpan.Zero || ConsumptionLifetime <= TimeSpan.Zero || ReaperInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(GamePackageStorageOptions), "Game package storage options are inconsistent.");
    }
}
