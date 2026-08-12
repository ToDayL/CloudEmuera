using CloudEmuera.Application.Sessions;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Sessions;

/// <summary>
/// SQLite implementation of the stopped-state SessionRoot write boundary.
/// An expired row is no longer a valid fencing fact.  It is removed only while
/// the same immediate transaction that installs the replacement lease is held;
/// Renew/Release also match the complete owner tuple so an old operation cannot
/// renew or release a later operation's lease.
/// </summary>
public sealed class SqliteSessionRootMutationLeaseStore(
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider) : ISessionRootMutationLeaseStore
{
    public async Task<SessionRootMutationAcquireResult> TryAcquireAsync(
        string sessionId,
        string actorUserId,
        string operationId,
        SessionRootMutationPurpose purpose,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdentifier(sessionId, "sess_") || !IsIdentifier(actorUserId, "usr_") ||
            !IsIdentifier(operationId, "mut_") || !Enum.IsDefined(purpose) ||
            duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            return new(SessionRootMutationAcquireFailure.InvalidRequest);

        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || !string.Equals(session.OwnerUserId, actorUserId, StringComparison.Ordinal))
            return new(SessionRootMutationAcquireFailure.SessionNotFound);
        if (!session.State.IsQuiescent())
            return new(SessionRootMutationAcquireFailure.SessionNotQuiescent);
        if (await db.WorkerLeases.AnyAsync(row => row.SessionId == sessionId, cancellationToken).ConfigureAwait(false))
            return new(SessionRootMutationAcquireFailure.WorkerLeaseActive);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await db.SessionRootMutationLeases
            .Where(row => row.SessionId == sessionId && row.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await db.SessionRootMutationLeases.AnyAsync(row => row.SessionId == sessionId && row.ExpiresAt > now, cancellationToken).ConfigureAwait(false))
            return new(SessionRootMutationAcquireFailure.MutationLeaseActive);

        DateTimeOffset acquiredAt = timeProvider.GetUtcNow();
        var rowToInsert = new SessionRootMutationLeaseRow
        {
            SessionId = sessionId,
            OperationId = operationId,
            ActorUserId = actorUserId,
            Purpose = ToPurposeCode(purpose),
            AcquiredAt = acquiredAt,
            ExpiresAt = acquiredAt.Add(duration),
        };
        db.SessionRootMutationLeases.Add(rowToInsert);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(SessionRootMutationAcquireFailure.None, new SessionRootMutationLease(
                sessionId,
                operationId,
                actorUserId,
                purpose,
                acquiredAt,
                rowToInsert.ExpiresAt));
        }
        catch (DbUpdateException)
        {
            return new(SessionRootMutationAcquireFailure.MutationLeaseActive);
        }
    }

    public async Task<bool> RenewAsync(
        SessionRootMutationLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            return false;
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(duration);
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        int changed = await db.SessionRootMutationLeases
            .Where(row => row.SessionId == lease.SessionId && row.OperationId == lease.OperationId &&
                row.ActorUserId == lease.ActorUserId && row.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, expiresAt), cancellationToken)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<bool> ReleaseAsync(
        SessionRootMutationLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        int changed = await db.SessionRootMutationLeases
            .Where(row => row.SessionId == lease.SessionId && row.OperationId == lease.OperationId && row.ActorUserId == lease.ActorUserId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return changed == 1;
    }

    private SqliteConnection OpenConnection() =>
        new SqliteConnectionFactory(databaseOptions, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);

    private static CloudEmueraDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options);

    private static bool IsIdentifier(string value, string prefix) =>
        value.Length is >= 5 and <= 64 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string ToPurposeCode(SessionRootMutationPurpose purpose) => purpose switch
    {
        SessionRootMutationPurpose.SaveImport => "SAVE_IMPORT",
        SessionRootMutationPurpose.SaveRename => "SAVE_RENAME",
        SessionRootMutationPurpose.SaveDelete => "SAVE_DELETE",
        SessionRootMutationPurpose.SaveCopy => "SAVE_COPY",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}
