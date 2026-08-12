using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Api.Security;

internal sealed record IdempotencyExecution<T>(T Value, int StatusCode, bool Replayed);

/// <summary>
/// Persists the result of retryable game operations in the same SQLite transaction as
/// the operation. A key can never silently be reused for a different request body.
/// </summary>
internal sealed class ApiIdempotencyStore(CloudEmueraDbContext db, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IdempotencyExecution<T>> ExecuteAsync<T>(
        CurrentActor actor,
        string scope,
        string key,
        object request,
        Func<Task<T>> action,
        int statusCode = StatusCodes.Status200OK,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(char.IsControl))
            throw new GameLibraryException(GameLibraryErrorCodes.InvalidInput, "The idempotency key is invalid.");
        string requestDigest = Digest(request);
        DateTimeOffset now = timeProvider.GetUtcNow();
        IdempotencyRecordRow? existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ActorUserId == actor.UserId && row.Scope == scope && row.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.ExpiresAt > now)
        {
            if (!string.Equals(existing.RequestDigest, requestDigest, StringComparison.Ordinal))
                throw new GameLibraryException(GameLibraryErrorCodes.IdempotencyConflict, "The idempotency key was already used for another request.");
            if (existing.Status == IdempotencyRecordStatus.InProgress)
                throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The idempotent request is still in progress.");
            if (existing.Status == IdempotencyRecordStatus.Failed)
                throw new GameLibraryException(existing.ErrorCode ?? GameLibraryErrorCodes.Conflict, "The idempotent request previously failed.");
            T? replay = JsonSerializer.Deserialize<T>(existing.ResponseJson, JsonOptions);
            if (replay is null) throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The idempotency record is invalid.");
            return new(replay, existing.ResponseStatus, true);
        }

        IdempotencyRecordRow record = new()
        {
            ActorUserId = actor.UserId,
            Scope = scope,
            IdempotencyKey = key,
            RequestDigest = requestDigest,
            Status = IdempotencyRecordStatus.InProgress,
            ResponseStatus = statusCode,
            ResponseJson = "null",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (existing is not null)
            {
                await db.IdempotencyRecords.Where(row => row.ActorUserId == actor.UserId && row.Scope == scope && row.IdempotencyKey == key)
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            db.IdempotencyRecords.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                IdempotencyRecordRow? raced = await db.IdempotencyRecords.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.ActorUserId == actor.UserId && row.Scope == scope && row.IdempotencyKey == key, cancellationToken)
                    .ConfigureAwait(false);
                if (raced is null || raced.ExpiresAt <= now) throw;
                if (!string.Equals(raced.RequestDigest, requestDigest, StringComparison.Ordinal))
                    throw new GameLibraryException(GameLibraryErrorCodes.IdempotencyConflict, "The idempotency key was already used for another request.");
                if (raced.Status == IdempotencyRecordStatus.InProgress)
                    throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The idempotent request is still in progress.");
                if (raced.Status == IdempotencyRecordStatus.Failed)
                    throw new GameLibraryException(raced.ErrorCode ?? GameLibraryErrorCodes.Conflict, "The idempotent request previously failed.");
                T? replay = JsonSerializer.Deserialize<T>(raced.ResponseJson, JsonOptions);
                if (replay is null) throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The idempotency record is invalid.");
                return new(replay, raced.ResponseStatus, true);
            }
        }

        db.Entry(record).State = EntityState.Detached;
        try
        {
            T value = await action().ConfigureAwait(false);
            string responseJson = JsonSerializer.Serialize(value, JsonOptions);
            string? resourceId = value is GameLibraryItem item ? item.Id : null;
            DateTimeOffset completedAt = timeProvider.GetUtcNow();
            int changed = await db.IdempotencyRecords
                .Where(row => row.ActorUserId == actor.UserId && row.Scope == scope && row.IdempotencyKey == key && row.RequestDigest == requestDigest && row.Status == IdempotencyRecordStatus.InProgress)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, IdempotencyRecordStatus.Succeeded)
                    .SetProperty(row => row.ResponseJson, responseJson)
                    .SetProperty(row => row.ResourceId, resourceId)
                    .SetProperty(row => row.UpdatedAt, completedAt)
                    .SetProperty(row => row.CompletedAt, completedAt), cancellationToken)
                .ConfigureAwait(false);
            if (changed != 1) throw new GameLibraryException(GameLibraryErrorCodes.Conflict, "The idempotency record was lost.");
            return new(value, statusCode, false);
        }
        catch (Exception exception)
        {
            string errorCode = exception is GameLibraryException gameException
                ? gameException.Code
                : GameLibraryErrorCodes.Conflict;
            try
            {
                await db.IdempotencyRecords
                    .Where(row => row.ActorUserId == actor.UserId && row.Scope == scope && row.IdempotencyKey == key && row.RequestDigest == requestDigest && row.Status == IdempotencyRecordStatus.InProgress)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.Status, IdempotencyRecordStatus.Failed)
                        .SetProperty(row => row.ResponseStatus, StatusCodes.Status409Conflict)
                        .SetProperty(row => row.ResponseJson, "{}")
                        .SetProperty(row => row.ErrorCode, errorCode)
                        .SetProperty(row => row.UpdatedAt, timeProvider.GetUtcNow())
                        .SetProperty(row => row.CompletedAt, timeProvider.GetUtcNow()), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original operation exception.  A later request
                // will either observe the durable FAILED row or retry only if
                // this completion write itself was unavailable.
            }
            throw;
        }
    }

    private static string Digest(object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}
