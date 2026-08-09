using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Identity;

public sealed class AuthSessionMaintenance(CloudEmueraDbContext db, TimeProvider timeProvider) : IAuthSessionMaintenance
{
    public async Task<int> CleanupAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be between 1 and 500.");
        DateTimeOffset now = timeProvider.GetUtcNow();
        string[] ids = await db.AuthSessions.AsNoTracking()
            .Where(session => session.RevokedAt != null || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now)
            .OrderBy(session => session.AbsoluteExpiresAt)
            .ThenBy(session => session.Id)
            .Select(session => session.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return 0;
        return await db.AuthSessions.Where(session => ids.Contains(session.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
