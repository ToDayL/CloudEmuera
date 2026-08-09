using CloudEmuera.Application.Authorization;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Authorization;

/// <summary>Projection-only lookup used by every resource boundary; it never returns tracked domain rows.</summary>
public sealed class SqliteResourceAccessReader(CloudEmueraDbContext db) : IResourceAccessReader
{
    public async Task<ResourceDescriptor?> FindAsync(ResourceKind kind, string resourceId, CancellationToken cancellationToken = default) => kind switch
    {
        ResourceKind.Game => await db.Games.AsNoTracking().Where(row => row.Id == resourceId && row.Status != GameStatus.Deleted)
            .Select(row => new ResourceDescriptor(ResourceKind.Game, row.Id, row.OwnerUserId, row.Visibility == GameVisibility.ServerShared)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        ResourceKind.Session or ResourceKind.Save => await db.Sessions.AsNoTracking().Where(row => row.Id == resourceId)
            .Select(row => new ResourceDescriptor(kind, row.Id, row.OwnerUserId)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        ResourceKind.Worker => await db.WorkerLeases.AsNoTracking().Where(row => row.SessionId == resourceId)
            .Join(db.Sessions.AsNoTracking(), lease => lease.SessionId, session => session.Id, (lease, session) => new ResourceDescriptor(ResourceKind.Worker, lease.SessionId, session.OwnerUserId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        ResourceKind.User => await db.Users.AsNoTracking().Where(row => row.Id == resourceId)
            .Select(row => new ResourceDescriptor(ResourceKind.User, row.Id, row.Id)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false),
        ResourceKind.Audit when resourceId == "instance" => new ResourceDescriptor(ResourceKind.Audit, "instance", string.Empty, exists: true),
        _ => null,
    };
}
