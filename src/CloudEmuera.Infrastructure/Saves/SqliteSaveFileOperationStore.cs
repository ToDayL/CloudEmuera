using CloudEmuera.Application.Saves;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Saves;

public sealed class SqliteSaveFileOperationStore(
    CloudEmueraDbContext db,
    TimeProvider timeProvider) : ISaveFileOperationStore
{
    public async Task<SaveFileOperationRecord?> FindByIdempotencyAsync(
        string actorUserId,
        string scope,
        string keyHash,
        CancellationToken cancellationToken = default)
    {
        SaveFileOperationRow? row = await db.SaveFileOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorUserId == actorUserId && item.IdempotencyScope == scope && item.IdempotencyKeyHash == keyHash, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<SaveFileOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
    {
        SaveFileOperationRow? row = await db.SaveFileOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    public async Task<SaveFileOperationRecord> CreatePreparedAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken = default)
    {
        if (operation.Status != "PREPARED")
            throw new ArgumentException("A new save operation must be PREPARED.", nameof(operation));

        SaveFileOperationRow row = new()
        {
            Id = operation.Id,
            SessionId = operation.SessionId,
            ActorUserId = operation.ActorUserId,
            IdempotencyScope = operation.IdempotencyScope,
            IdempotencyKeyHash = operation.IdempotencyKeyHash,
            Type = ParseType(operation.Type),
            Status = SaveFileOperationStatus.Prepared,
            SourcePath = operation.SourcePath,
            TargetPath = operation.TargetPath,
            PayloadPath = operation.PayloadPath,
            PayloadSize = operation.PayloadSize,
            PayloadDigest = operation.PayloadDigest,
            ExpectedSourceIdentityJson = operation.ExpectedSourceIdentityJson,
            ExpectedTargetCaptured = operation.ExpectedTargetCaptured,
            ExpectedTargetExists = operation.ExpectedTargetExists,
            ExpectedTargetIdentityJson = operation.ExpectedTargetIdentityJson,
            ResultJson = operation.ResultJson,
            ErrorCode = operation.ErrorCode,
            CreatedAt = operation.CreatedAt,
            UpdatedAt = operation.UpdatedAt,
            CompletedAt = null,
            StateVersion = operation.StateVersion,
        };
        db.SaveFileOperations.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Entry(row).State = EntityState.Detached;
        return ToRecord(row);
    }

    public Task<bool> MarkStagedAsync(
        string operationId,
        long payloadSize,
        string payloadDigest,
        string payloadPath,
        bool expectedTargetCaptured,
        bool expectedTargetExists,
        string? expectedTargetIdentityJson,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(operationId, SaveFileOperationStatus.Prepared, SaveFileOperationStatus.Staged, row =>
        {
            row.PayloadSize = payloadSize;
            row.PayloadDigest = payloadDigest;
            row.PayloadPath = payloadPath;
            row.ExpectedTargetCaptured = expectedTargetCaptured;
            row.ExpectedTargetExists = expectedTargetExists;
            row.ExpectedTargetIdentityJson = expectedTargetIdentityJson;
        }, cancellationToken);

    public async Task<bool> MarkPublishedAsync(string operationId, CancellationToken cancellationToken = default)
    {
        SaveFileOperationRow? current = await db.SaveFileOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Status is not (SaveFileOperationStatus.Prepared or SaveFileOperationStatus.Staged))
            return current?.Status == SaveFileOperationStatus.Published;

        DateTimeOffset now = timeProvider.GetUtcNow();
        int stateVersion = current.StateVersion;
        int changed = await db.SaveFileOperations
            .Where(row => row.Id == operationId && row.StateVersion == stateVersion &&
                (row.Status == SaveFileOperationStatus.Prepared || row.Status == SaveFileOperationStatus.Staged))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, SaveFileOperationStatus.Published)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, stateVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        return changed == 1;
    }

    public Task<bool> MarkCommittedAsync(string operationId, string resultJson, CancellationToken cancellationToken = default) =>
        CompleteAsync(operationId, SaveFileOperationStatus.Committed, resultJson, null, cancellationToken);

    public Task<bool> MarkFailedAsync(string operationId, string errorCode, CancellationToken cancellationToken = default) =>
        CompleteAsync(operationId, SaveFileOperationStatus.Failed, "{}", errorCode, cancellationToken);

    public async Task<IReadOnlyList<SaveFileOperationRecord>> ListIncompleteAsync(CancellationToken cancellationToken = default)
    {
        List<SaveFileOperationRow> rows = await db.SaveFileOperations.AsNoTracking()
            .Where(row => row.Status != SaveFileOperationStatus.Committed && row.Status != SaveFileOperationStatus.Failed)
            .OrderBy(row => row.UpdatedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToRecord).ToArray();
    }

    private async Task<bool> TransitionAsync(
        string operationId,
        SaveFileOperationStatus expected,
        SaveFileOperationStatus next,
        Action<SaveFileOperationRow>? update,
        CancellationToken cancellationToken)
    {
        SaveFileOperationRow? current = await db.SaveFileOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == operationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != expected)
            return false;
        update?.Invoke(current);
        DateTimeOffset now = timeProvider.GetUtcNow();
        int stateVersion = current.StateVersion;
        int changed = await db.SaveFileOperations
            .Where(row => row.Id == operationId && row.Status == expected && row.StateVersion == stateVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, next)
                .SetProperty(row => row.PayloadSize, current.PayloadSize)
                .SetProperty(row => row.PayloadDigest, current.PayloadDigest)
                .SetProperty(row => row.PayloadPath, current.PayloadPath)
                .SetProperty(row => row.ExpectedSourceIdentityJson, current.ExpectedSourceIdentityJson)
                .SetProperty(row => row.ExpectedTargetCaptured, current.ExpectedTargetCaptured)
                .SetProperty(row => row.ExpectedTargetExists, current.ExpectedTargetExists)
                .SetProperty(row => row.ExpectedTargetIdentityJson, current.ExpectedTargetIdentityJson)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, stateVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        return changed == 1;
    }

    private async Task<bool> CompleteAsync(
        string operationId,
        SaveFileOperationStatus next,
        string resultJson,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        SaveFileOperationRow? current = await db.SaveFileOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == operationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status is SaveFileOperationStatus.Committed or SaveFileOperationStatus.Failed)
            return current?.Status == next;
        DateTimeOffset now = timeProvider.GetUtcNow();
        int stateVersion = current.StateVersion;
        int changed = await db.SaveFileOperations
            .Where(row => row.Id == operationId && row.StateVersion == stateVersion && row.Status != SaveFileOperationStatus.Committed && row.Status != SaveFileOperationStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, next)
                .SetProperty(row => row.ResultJson, resultJson)
                .SetProperty(row => row.ErrorCode, errorCode)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.CompletedAt, now)
                .SetProperty(row => row.StateVersion, stateVersion + 1), cancellationToken)
            .ConfigureAwait(false);
        return changed == 1;
    }

    private static SaveFileOperationRecord ToRecord(SaveFileOperationRow row) => new(
        row.Id,
        row.SessionId,
        row.ActorUserId,
        row.IdempotencyScope,
        row.IdempotencyKeyHash,
        ToStorageName(row.Type),
        ToStorageName(row.Status),
        row.SourcePath,
        row.TargetPath,
        row.PayloadPath,
        row.PayloadSize,
        row.PayloadDigest,
        row.ExpectedSourceIdentityJson,
        row.ResultJson,
        row.ErrorCode,
        row.CreatedAt,
        row.UpdatedAt,
        row.CompletedAt,
        row.StateVersion)
    {
        ExpectedTargetCaptured = row.ExpectedTargetCaptured,
        ExpectedTargetExists = row.ExpectedTargetExists,
        ExpectedTargetIdentityJson = row.ExpectedTargetIdentityJson,
    };

    private static SaveFileOperationType ParseType(string value) => value switch
    {
        "IMPORT" => SaveFileOperationType.Import,
        "RENAME" => SaveFileOperationType.Rename,
        "DELETE" => SaveFileOperationType.Delete,
        _ => throw new ArgumentException("The save operation type is invalid.", nameof(value)),
    };

    private static string ToStorageName<T>(T value) where T : struct, Enum =>
        value.ToString().ToUpperInvariant();
}
