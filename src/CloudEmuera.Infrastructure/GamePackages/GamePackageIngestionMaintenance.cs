using CloudEmuera.Application.GamePackages;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.GamePackages;

public sealed class GamePackageIngestionMaintenance(
    CloudEmueraDbContext db,
    GamePackageStorageOptions options,
    TimeProvider timeProvider) : IGamePackageIngestionMaintenance
{
    public async Task<int> ReapExpiredAsync(int maxItems = 32, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        DateTimeOffset now = timeProvider.GetUtcNow();
        var candidates = await db.GamePackageIngestions.AsNoTracking()
            .Where(row => row.CleanupCompletedAt == null
                && ((row.ExpiresAt <= now && row.Status != GamePackageIngestionStatus.Consumed)
                    || row.Status == GamePackageIngestionStatus.Consumed
                    || row.Status == GamePackageIngestionStatus.Failed
                    || row.Status == GamePackageIngestionStatus.Abandoned))
            .OrderBy(row => row.ExpiresAt).ThenBy(row => row.Id)
            .Take(maxItems)
            .Select(row => new { row.Id, row.Status, row.StateVersion, row.StagingPath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int reaped = 0;
        using var stagingStore = new LinuxGamePackageStagingStore(options);
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.StagingPath, $"games/staging/{candidate.Id}", StringComparison.Ordinal)) continue;
            bool terminal = candidate.Status is GamePackageIngestionStatus.Consumed
                or GamePackageIngestionStatus.Failed or GamePackageIngestionStatus.Abandoned;
            if (!terminal)
            {
                int claimed = await db.GamePackageIngestions
                    .Where(row => row.Id == candidate.Id && row.Status == candidate.Status
                        && row.StateVersion == candidate.StateVersion && row.ExpiresAt <= now)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, GamePackageIngestionStatus.Abandoned)
                        .SetProperty(row => row.UpdatedAt, now)
                        .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), cancellationToken).ConfigureAwait(false);
                if (claimed != 1) continue;
            }

            try
            {
                if (!stagingStore.DeleteIngestion(candidate.Id)) continue;
            }
            catch (IOException)
            {
                continue;
            }

            int released = await db.GamePackageIngestions
                .Where(row => row.Id == candidate.Id && row.CleanupCompletedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReservedBytes, 0L)
                    .SetProperty(row => row.ReservationReleasedAt, row => row.ReservationReleasedAt ?? now)
                    .SetProperty(row => row.CleanupCompletedAt, now).SetProperty(row => row.UpdatedAt, now)
                    .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), cancellationToken).ConfigureAwait(false);
            reaped += released;
        }
        return reaped;
    }
}
