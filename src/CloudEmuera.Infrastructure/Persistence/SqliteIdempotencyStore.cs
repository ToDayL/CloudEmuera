using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Persistence;

public enum PersistentIdempotencyBeginState
{
    Started,
    InProgress,
    Succeeded,
    Failed,
    Conflict,
}

public sealed record PersistentIdempotencyRecord(
    PersistentIdempotencyBeginState State,
    string RequestDigest,
    int ResponseStatus,
    string ResponseJson,
    string? ResourceId,
    string? ErrorCode);

/// <summary>
/// Generic durable command record adapter.  Status, rather than an empty JSON
/// sentinel, is the source of truth and every method uses its own short-lived
/// connection so an HTTP request scope cannot become the recovery authority.
/// </summary>
public sealed class SqliteIdempotencyStore(
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PersistentIdempotencyRecord> BeginAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        string? resourceId = null,
        CancellationToken cancellationToken = default)
    {
        Validate(actorUserId, scope, key, requestDigest, resourceId);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        IdempotencyRecordRow? existing = await db.IdempotencyRecords.SingleOrDefaultAsync(
            row => row.ActorUserId == actorUserId && row.Scope == scope && row.IdempotencyKey == key,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestDigest, requestDigest, StringComparison.Ordinal))
            {
                return new PersistentIdempotencyRecord(
                    PersistentIdempotencyBeginState.Conflict,
                    existing.RequestDigest,
                    existing.ResponseStatus,
                    existing.ResponseJson,
                    existing.ResourceId,
                    existing.ErrorCode);
            }

            if (existing.Status is IdempotencyRecordStatus.InProgress || existing.ExpiresAt > now)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ToRecord(existing, existing.Status == IdempotencyRecordStatus.InProgress
                    ? PersistentIdempotencyBeginState.InProgress
                    : MapStatus(existing.Status));
            }

            // Only terminal records may be reused after retention.  An
            // IN_PROGRESS row is never deleted merely because its clock window
            // elapsed; the recovery service must first reconcile its operation.
            db.IdempotencyRecords.Remove(existing);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var created = new IdempotencyRecordRow
        {
            ActorUserId = actorUserId,
            Scope = scope,
            IdempotencyKey = key,
            RequestDigest = requestDigest,
            Status = IdempotencyRecordStatus.InProgress,
            ResponseStatus = 202,
            ResponseJson = "null",
            ResourceId = resourceId,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
        db.IdempotencyRecords.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(created, PersistentIdempotencyBeginState.Started);
        }
        catch (DbUpdateException)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            return await ReadAfterRaceAsync(actorUserId, scope, key, requestDigest, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task CompleteSuccessAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string responseJson,
        string? resourceId,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(actorUserId, scope, key, requestDigest, IdempotencyRecordStatus.Succeeded, responseStatus, responseJson, resourceId, null, cancellationToken);

    public Task CompleteFailureAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string errorCode,
        string responseJson,
        string? resourceId = null,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(actorUserId, scope, key, requestDigest, IdempotencyRecordStatus.Failed, responseStatus, responseJson, resourceId, errorCode, cancellationToken);

    private async Task CompleteAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        IdempotencyRecordStatus status,
        int responseStatus,
        string responseJson,
        string? resourceId,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        Validate(actorUserId, scope, key, requestDigest, resourceId);
        if (status == IdempotencyRecordStatus.Failed && string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("A failed idempotency command requires an error code.", nameof(errorCode));
        using JsonDocument _ = JsonDocument.Parse(responseJson);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        int changed = await db.IdempotencyRecords
            .Where(row => row.ActorUserId == actorUserId && row.Scope == scope && row.IdempotencyKey == key &&
                row.RequestDigest == requestDigest && row.Status == IdempotencyRecordStatus.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, status)
                .SetProperty(row => row.ResponseStatus, responseStatus)
                .SetProperty(row => row.ResponseJson, responseJson)
                .SetProperty(row => row.ResourceId, resourceId)
                .SetProperty(row => row.ErrorCode, errorCode)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.CompletedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("The idempotency command was not in progress.");
    }

    private async Task<PersistentIdempotencyRecord> ReadAfterRaceAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        IdempotencyRecordRow? raced = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            row => row.ActorUserId == actorUserId && row.Scope == scope && row.IdempotencyKey == key,
            cancellationToken).ConfigureAwait(false);
        if (raced is null)
            throw new InvalidOperationException("The idempotency record disappeared after a unique-key race.");
        return !string.Equals(raced.RequestDigest, requestDigest, StringComparison.Ordinal)
            ? ToRecord(raced, PersistentIdempotencyBeginState.Conflict)
            : ToRecord(raced, raced.Status == IdempotencyRecordStatus.InProgress
                ? PersistentIdempotencyBeginState.InProgress
                : MapStatus(raced.Status));
    }

    private SqliteConnection OpenConnection() =>
        new SqliteConnectionFactory(databaseOptions, createDataRoot: false).OpenConnection(SqliteConnectionAccess.ReadWrite);

    private static CloudEmueraDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options);

    private static PersistentIdempotencyRecord ToRecord(IdempotencyRecordRow row, PersistentIdempotencyBeginState state) =>
        new(state, row.RequestDigest, row.ResponseStatus, row.ResponseJson, row.ResourceId, row.ErrorCode);

    private static PersistentIdempotencyBeginState MapStatus(IdempotencyRecordStatus status) => status switch
    {
        IdempotencyRecordStatus.Succeeded => PersistentIdempotencyBeginState.Succeeded,
        IdempotencyRecordStatus.Failed => PersistentIdempotencyBeginState.Failed,
        _ => PersistentIdempotencyBeginState.InProgress,
    };

    private static void Validate(string actorUserId, string scope, string key, string requestDigest, string? resourceId = null)
    {
        if (string.IsNullOrWhiteSpace(actorUserId) || string.IsNullOrWhiteSpace(scope) ||
            string.IsNullOrWhiteSpace(key) || key.Length > PersistenceLimits.IdempotencyKeyMaxLength || key.Any(char.IsControl) ||
            requestDigest.Length != PersistenceLimits.RequestDigestLength ||
            (resourceId is not null && (resourceId.Length > PersistenceLimits.ResourceIdMaxLength || resourceId.Any(char.IsControl))))
            throw new ArgumentException("The idempotency command key is invalid.");
    }
}
