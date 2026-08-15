using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Saves;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Saves;

/// <summary>
/// Reconciles durable save operations only from facts that can be observed
/// through the protected SessionRoot accessor. It never guesses which native
/// file is authoritative when an identity or digest is ambiguous.
/// </summary>
public sealed class SaveFileOperationRecovery(
    CloudEmueraDbContext db,
    ISaveFileOperationStore operationStore,
    ISessionSaveRootAccessor rootAccessor,
    ISessionRootMutationLeaseStore mutationLeases,
    TimeProvider timeProvider) : ISaveFileOperationRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RecoveryGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationRetentionPeriod = TimeSpan.FromHours(24);

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset recoveryCutoff = timeProvider.GetUtcNow() - RecoveryGracePeriod;
        IReadOnlyList<SaveFileOperationRecord> operations = await operationStore.ListIncompleteAsync(cancellationToken).ConfigureAwait(false);
        foreach (SaveFileOperationRecord listed in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (listed.UpdatedAt > recoveryCutoff)
                continue;

            SaveFileOperationRecord? operation = await operationStore.GetAsync(listed.Id, cancellationToken).ConfigureAwait(false);
            if (operation is null || operation.IsTerminal)
                continue;
            await RecoverOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        await CleanupTerminalStagingAsync(cancellationToken).ConfigureAwait(false);
        await ReleaseTerminalLeasesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAllMutationLeasesHaveKnownOperationsAsync(cancellationToken).ConfigureAwait(false);
        await CleanupExpiredTerminalOperationsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverOperationAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        ValidateOperation(operation);
        if (operation.Status == "PREPARED")
        {
            await RecoverPreparedAsync(operation, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (operation.Type)
        {
            case "IMPORT":
                await RecoverImportAsync(operation, cancellationToken).ConfigureAwait(false);
                break;
            case "RENAME":
                await RecoverRenameAsync(operation, cancellationToken).ConfigureAwait(false);
                break;
            case "DELETE":
                await RecoverDeleteAsync(operation, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw RecoveryRequired();
        }
    }

    private async Task RecoverPreparedAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        if (!await EnsureOperationLeaseCanBeReclaimedAsync(operation, cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            await rootAccessor.CleanupStagingAsync(operation.SessionId, operation.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SessionSaveException)
        {
            throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档操作等待恢复。", 503, exception);
        }

        await CompleteFailureAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverImportAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        if (operation.Status is not ("STAGED" or "PUBLISHED") ||
            operation.PayloadSize is not long expectedSize ||
            string.IsNullOrWhiteSpace(operation.PayloadDigest) ||
            !operation.ExpectedTargetCaptured)
            throw RecoveryRequired();

        SessionSaveFileObservation? targetObservation = await InspectMaybeAsync(operation.SessionId, operation.TargetPath, cancellationToken).ConfigureAwait(false);
        ContentFact? target = await ReadContentAsync(operation.SessionId, operation.TargetPath, cancellationToken).ConfigureAwait(false);
        bool targetMatches = MatchesPayload(target, expectedSize, operation.PayloadDigest);
        if (!await EnsureOperationLeaseCanBeReclaimedAsync(operation, cancellationToken).ConfigureAwait(false))
            return;

        if (targetMatches)
        {
            ContentFact matchedTarget = target!;
            SessionSaveItem targetItem = matchedTarget.Item!;
            await MarkPublishedAndCommitAsync(operation, targetItem, responseStatus: operation.ExpectedTargetExists ? 204 : 201, AuditActions.SessionSaveImported,
                new { path = targetItem.Path, kind = targetItem.Kind.ToString().ToUpperInvariant(), size = matchedTarget.Size, digest = operation.PayloadDigest }, cancellationToken).ConfigureAwait(false);
            await CleanupCommittedStagingAsync(operation, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation.Status == "PUBLISHED")
            throw RecoveryRequired();

        if (!MatchesExpectedTarget(targetObservation, operation))
            throw RecoveryRequired();

        ContentFact? staging = await TryReadStagingAsync(operation.SessionId, operation.Id, cancellationToken).ConfigureAwait(false);
        if (!MatchesPayload(staging, expectedSize, operation.PayloadDigest))
            throw RecoveryRequired();

        SessionRootMutationLease lease = await AcquireRecoveryLeaseAsync(operation, SessionRootMutationPurpose.SaveImport, cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSaveFileObservation? currentTarget = await InspectMaybeAsync(operation.SessionId, operation.TargetPath, CancellationToken.None).ConfigureAwait(false);
            if (!MatchesExpectedTarget(currentTarget, operation))
                throw RecoveryRequired();
            SessionSavePublishResult publication = await rootAccessor.PublishAsync(operation.SessionId, operation.Id, operation.TargetPath, replace: true, CancellationToken.None).ConfigureAwait(false);
            ContentFact? published = await ReadContentAsync(operation.SessionId, operation.TargetPath, CancellationToken.None).ConfigureAwait(false);
            if (!MatchesPayload(published, expectedSize, operation.PayloadDigest))
                throw RecoveryRequired();

            ContentFact matchedPublished = published!;
            SessionSaveItem publishedItem = matchedPublished.Item!;
            await MarkPublishedAndCommitAsync(operation, publishedItem, publication.Created ? 201 : 204, AuditActions.SessionSaveImported,
                new { path = publishedItem.Path, kind = publishedItem.Kind.ToString().ToUpperInvariant(), size = matchedPublished.Size, digest = operation.PayloadDigest }, CancellationToken.None).ConfigureAwait(false);
            _ = await mutationLeases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            await CleanupCommittedStagingAsync(operation, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Keep the lease as a recovery barrier when the point of the
            // filesystem side effect cannot be proven. It will expire and be
            // reclaimed only by the next recovery pass.
            throw;
        }
    }

    private async Task RecoverRenameAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        if (operation.SourcePath is null || operation.ExpectedSourceIdentityJson is null || operation.Status is not ("PREPARED" or "PUBLISHED"))
            throw RecoveryRequired();

        SessionSaveFileObservation? source = await InspectMaybeAsync(operation.SessionId, operation.SourcePath, cancellationToken).ConfigureAwait(false);
        SessionSaveFileObservation? target = await InspectMaybeAsync(operation.SessionId, operation.TargetPath, cancellationToken).ConfigureAwait(false);
        bool sourceMatches = MatchesIdentity(source, operation.ExpectedSourceIdentityJson);
        bool targetMatches = MatchesIdentity(target, operation.ExpectedSourceIdentityJson);
        bool samePath = string.Equals(operation.SourcePath, operation.TargetPath, StringComparison.Ordinal);

        if (!await EnsureOperationLeaseCanBeReclaimedAsync(operation, cancellationToken).ConfigureAwait(false))
            return;

        if (samePath && sourceMatches)
        {
            SessionSaveFileObservation sameSource = source!;
            await MarkPublishedAndCommitAsync(operation, sameSource.ToItem(), 204, AuditActions.SessionSaveRenamed,
                new { sourcePath = operation.SourcePath, targetPath = operation.TargetPath, kind = sameSource.Kind.ToString().ToUpperInvariant(), size = sameSource.SizeBytes }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (targetMatches && source is null)
        {
            SessionSaveFileObservation matchedTarget = target!;
            await MarkPublishedAndCommitAsync(operation, matchedTarget.ToItem(), 204, AuditActions.SessionSaveRenamed,
                new { sourcePath = operation.SourcePath, targetPath = operation.TargetPath, kind = matchedTarget.Kind.ToString().ToUpperInvariant(), size = matchedTarget.SizeBytes }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (operation.Status == "PUBLISHED" || !sourceMatches || target is not null)
            throw RecoveryRequired();

        SessionRootMutationLease lease = await AcquireRecoveryLeaseAsync(operation, SessionRootMutationPurpose.SaveRename, cancellationToken).ConfigureAwait(false);
        try
        {
            SessionSaveRenameResult renamed = await rootAccessor.RenameAsync(operation.SessionId, operation.SourcePath, operation.TargetPath, CancellationToken.None).ConfigureAwait(false);
            SessionSaveFileObservation? published = await InspectMaybeAsync(operation.SessionId, operation.TargetPath, CancellationToken.None).ConfigureAwait(false);
            if (!MatchesIdentity(published, operation.ExpectedSourceIdentityJson))
                throw RecoveryRequired();
            await MarkPublishedAndCommitAsync(operation, published!.ToItem(), 204, AuditActions.SessionSaveRenamed,
                new { sourcePath = operation.SourcePath, targetPath = operation.TargetPath, kind = renamed.Target.Kind.ToString().ToUpperInvariant(), size = renamed.Target.SizeBytes }, CancellationToken.None).ConfigureAwait(false);
            _ = await mutationLeases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    private async Task RecoverDeleteAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        if (operation.SourcePath is null || operation.ExpectedSourceIdentityJson is null || operation.Status is not ("PREPARED" or "PUBLISHED"))
            throw RecoveryRequired();

        SessionSaveFileObservation? source = await InspectMaybeAsync(operation.SessionId, operation.SourcePath, cancellationToken).ConfigureAwait(false);
        if (!await EnsureOperationLeaseCanBeReclaimedAsync(operation, cancellationToken).ConfigureAwait(false))
            return;
        if (source is null)
        {
            await MarkPublishedAndCommitAsync(operation, item: null, 204, AuditActions.SessionSaveDeleted,
                new { path = operation.SourcePath, kind = InferKind(operation.SourcePath).ToString().ToUpperInvariant(), size = ReadExpectedSize(operation.ExpectedSourceIdentityJson) }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!MatchesIdentity(source, operation.ExpectedSourceIdentityJson) || operation.Status == "PUBLISHED")
            throw RecoveryRequired();

        SessionRootMutationLease lease = await AcquireRecoveryLeaseAsync(operation, SessionRootMutationPurpose.SaveDelete, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await rootAccessor.DeleteAsync(operation.SessionId, operation.SourcePath, operation.ExpectedSourceIdentityJson, CancellationToken.None).ConfigureAwait(false))
                throw RecoveryRequired();
            if (await InspectMaybeAsync(operation.SessionId, operation.SourcePath, CancellationToken.None).ConfigureAwait(false) is not null)
                throw RecoveryRequired();
            await MarkPublishedAndCommitAsync(operation, item: null, 204, AuditActions.SessionSaveDeleted,
                new { path = operation.SourcePath, kind = source.Kind.ToString().ToUpperInvariant(), size = source.SizeBytes }, CancellationToken.None).ConfigureAwait(false);
            _ = await mutationLeases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    private async Task<SessionRootMutationLease> AcquireRecoveryLeaseAsync(
        SaveFileOperationRecord operation,
        SessionRootMutationPurpose purpose,
        CancellationToken cancellationToken)
    {
        SessionRootMutationAcquireResult result = await mutationLeases.TryAcquireAsync(
            operation.SessionId,
            operation.ActorUserId,
            operation.Id,
            purpose,
            TimeSpan.FromMinutes(2),
            CancellationToken.None).ConfigureAwait(false);
        return result.Failure switch
        {
            SessionRootMutationAcquireFailure.None when result.Lease is not null => result.Lease,
            _ => throw RecoveryRequired(),
        };
    }

    private async Task<bool> EnsureOperationLeaseCanBeReclaimedAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        SessionRootMutationLeaseRow? lease = await db.SessionRootMutationLeases.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == operation.Id, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
            return true;
        if (!string.Equals(lease.SessionId, operation.SessionId, StringComparison.Ordinal) ||
            !string.Equals(lease.ActorUserId, operation.ActorUserId, StringComparison.Ordinal))
            throw RecoveryRequired();
        if (lease.ExpiresAt > timeProvider.GetUtcNow())
            return false;
        _ = await mutationLeases.ReleaseExpiredAsync(operation.SessionId, operation.Id, CancellationToken.None).ConfigureAwait(false);
        if (await db.SessionRootMutationLeases.AsNoTracking().AnyAsync(row => row.OperationId == operation.Id, cancellationToken).ConfigureAwait(false))
            throw RecoveryRequired();
        return true;
    }

    private async Task MarkPublishedAndCommitAsync(
        SaveFileOperationRecord operation,
        SessionSaveItem? item,
        int responseStatus,
        string auditAction,
        object metadata,
        CancellationToken cancellationToken)
    {
        bool transitioned = await operationStore.MarkPublishedAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        if (!transitioned)
        {
            SaveFileOperationRecord? current = await operationStore.GetAsync(operation.Id, cancellationToken).ConfigureAwait(false);
            if (current?.Status == "COMMITTED")
                return;
            if (current?.Status != "PUBLISHED")
                throw RecoveryRequired();
        }

        SessionSaveMutationResult result = new(responseStatus, false, item);
        await CompleteSuccessAsync(operation, result, auditAction, metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteSuccessAsync(
        SaveFileOperationRecord operation,
        SessionSaveMutationResult result,
        string auditAction,
        object metadata,
        CancellationToken cancellationToken)
    {
        string? idempotencyKey = await FindIdempotencyKeyAsync(operation, cancellationToken).ConfigureAwait(false);
        if (idempotencyKey is null)
            throw RecoveryRequired();

        DateTimeOffset now = timeProvider.GetUtcNow();
        string resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        SaveFileOperationRow row = await db.SaveFileOperations.SingleAsync(item => item.Id == operation.Id, CancellationToken.None).ConfigureAwait(false);
        if (row.Status == SaveFileOperationStatus.Committed)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (row.Status != SaveFileOperationStatus.Published)
            throw RecoveryRequired();

        IdempotencyRecordRow idempotency = await db.IdempotencyRecords.SingleAsync(item =>
            item.ActorUserId == operation.ActorUserId && item.ResourceId == operation.SessionId && item.Scope == operation.IdempotencyScope && item.IdempotencyKey == idempotencyKey, CancellationToken.None).ConfigureAwait(false);
        if (idempotency.Status == IdempotencyRecordStatus.Failed)
            throw RecoveryRequired();
        if (idempotency.Status != IdempotencyRecordStatus.Succeeded)
        {
            idempotency.Status = IdempotencyRecordStatus.Succeeded;
            idempotency.ResponseStatus = result.StatusCode;
            idempotency.ResponseJson = resultJson;
            idempotency.ErrorCode = null;
            idempotency.UpdatedAt = now;
            idempotency.CompletedAt = now;
        }
        idempotency.ExpiresAt = now.Add(OperationRetentionPeriod);

        row.Status = SaveFileOperationStatus.Committed;
        row.ResultJson = idempotency.Status == IdempotencyRecordStatus.Succeeded ? idempotency.ResponseJson : resultJson;
        row.ErrorCode = null;
        row.CompletedAt = now;
        row.UpdatedAt = now;
        row.StateVersion++;

        if (!await HasSaveAuditAsync(operation.SessionId, operation.Id, auditAction, CancellationToken.None).ConfigureAwait(false))
        {
            db.AuditEvents.Add(new AuditEventRow
            {
                Id = $"audit_{Guid.CreateVersion7():N}",
                OccurredAt = now,
                ActorUserId = operation.ActorUserId,
                ActorType = await GetAuditActorTypeAsync(operation.ActorUserId, CancellationToken.None).ConfigureAwait(false),
                Action = auditAction,
                ResourceType = "SESSION_SAVE",
                ResourceId = operation.SessionId,
                Result = AuditResult.Succeeded,
                MetadataJson = JsonSerializer.Serialize(new { operationId = operation.Id, metadata }, JsonOptions),
            });
        }

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CompleteFailureAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        SaveFileOperationRow row = await db.SaveFileOperations.SingleAsync(item => item.Id == operation.Id, CancellationToken.None).ConfigureAwait(false);
        if (row.Status is SaveFileOperationStatus.Committed or SaveFileOperationStatus.Failed)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        row.Status = SaveFileOperationStatus.Failed;
        row.ErrorCode = SaveErrorCodes.RecoveryRequired;
        row.ResultJson = "{}";
        row.CompletedAt = now;
        row.UpdatedAt = now;
        row.StateVersion++;

        string? idempotencyKey = await FindIdempotencyKeyAsync(operation, CancellationToken.None).ConfigureAwait(false);
        if (idempotencyKey is not null)
        {
            IdempotencyRecordRow? idempotency = await db.IdempotencyRecords.SingleOrDefaultAsync(item =>
                item.ActorUserId == operation.ActorUserId && item.ResourceId == operation.SessionId && item.Scope == operation.IdempotencyScope && item.IdempotencyKey == idempotencyKey, CancellationToken.None).ConfigureAwait(false);
            if (idempotency is not null && idempotency.Status == IdempotencyRecordStatus.InProgress)
            {
                idempotency.Status = IdempotencyRecordStatus.Failed;
                idempotency.ResponseStatus = 503;
                idempotency.ResponseJson = "{}";
                idempotency.ErrorCode = SaveErrorCodes.RecoveryRequired;
                idempotency.UpdatedAt = now;
                idempotency.CompletedAt = now;
                idempotency.ExpiresAt = now.Add(OperationRetentionPeriod);
            }
        }

        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = now,
            ActorUserId = operation.ActorUserId,
            ActorType = AuditActorType.System,
            Action = AuditActions.SessionSaveRecoveryFailed,
            ResourceType = "SESSION_SAVE_OPERATION",
            ResourceId = operation.SessionId,
            Result = AuditResult.Failed,
            ReasonCode = SaveErrorCodes.RecoveryRequired,
            MetadataJson = JsonSerializer.Serialize(new { operationId = operation.Id, type = operation.Type }, JsonOptions),
        });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CleanupCommittedStagingAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        if (operation.PayloadPath is null)
            return;
        await rootAccessor.CleanupStagingAsync(operation.SessionId, operation.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupTerminalStagingAsync(CancellationToken cancellationToken)
    {
        List<SaveFileOperationRow> operations = await db.SaveFileOperations.AsNoTracking()
            .Where(row => (row.Status == SaveFileOperationStatus.Committed || row.Status == SaveFileOperationStatus.Failed) && row.PayloadPath != null)
            .OrderBy(row => row.UpdatedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (SaveFileOperationRow operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await rootAccessor.CleanupStagingAsync(operation.SessionId, operation.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseTerminalLeasesAsync(CancellationToken cancellationToken)
    {
        List<SessionRootMutationLeaseRow> leases = await db.SessionRootMutationLeases.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (SessionRootMutationLeaseRow leaseRow in leases)
        {
            SaveFileOperationRecord? operation = await operationStore.GetAsync(leaseRow.OperationId, cancellationToken).ConfigureAwait(false);
            if (operation is null || !operation.IsTerminal)
                continue;
            ValidateLeaseOwnership(leaseRow, operation);
            bool released;
            if (leaseRow.ExpiresAt <= timeProvider.GetUtcNow())
            {
                released = await mutationLeases.ReleaseExpiredAsync(leaseRow.SessionId, leaseRow.OperationId, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                SessionRootMutationLease lease = new(
                    leaseRow.SessionId,
                    leaseRow.OperationId,
                    leaseRow.ActorUserId,
                    ParsePurpose(leaseRow.Purpose),
                    leaseRow.AcquiredAt,
                    leaseRow.ExpiresAt);
                released = await mutationLeases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
            if (!released && await db.SessionRootMutationLeases.AsNoTracking().AnyAsync(row => row.OperationId == leaseRow.OperationId, cancellationToken).ConfigureAwait(false))
                throw RecoveryRequired();
        }
    }

    private async Task CleanupExpiredTerminalOperationsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - OperationRetentionPeriod;
        List<SaveFileOperationRow> candidates = await db.SaveFileOperations.AsNoTracking()
            .Where(row => (row.Status == SaveFileOperationStatus.Committed || row.Status == SaveFileOperationStatus.Failed) &&
                row.CompletedAt != null && row.CompletedAt <= cutoff)
            .OrderBy(row => row.CompletedAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (SaveFileOperationRow candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await db.SessionRootMutationLeases.AsNoTracking().AnyAsync(row => row.OperationId == candidate.Id, cancellationToken).ConfigureAwait(false))
                continue;

            List<IdempotencyRecordRow> idempotencyRecords = await db.IdempotencyRecords
                .Where(row => row.ActorUserId == candidate.ActorUserId && row.ResourceId == candidate.SessionId && row.Scope == candidate.IdempotencyScope)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            List<IdempotencyRecordRow> matchingIdempotencyRecords = idempotencyRecords
                .Where(row => string.Equals(HashText(row.IdempotencyKey), candidate.IdempotencyKeyHash, StringComparison.Ordinal))
                .ToList();
            bool hasBlockingIdempotency = matchingIdempotencyRecords.Any(row =>
                row.Status == IdempotencyRecordStatus.InProgress || row.ExpiresAt > now);
            if (hasBlockingIdempotency)
                continue;

            foreach (IdempotencyRecordRow idempotency in matchingIdempotencyRecords.Where(row => row.Status != IdempotencyRecordStatus.InProgress && row.ExpiresAt <= now))
                db.IdempotencyRecords.Remove(idempotency);
            db.SaveFileOperations.Remove(candidate);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAllMutationLeasesHaveKnownOperationsAsync(CancellationToken cancellationToken)
    {
        List<SessionRootMutationLeaseRow> leases = await db.SessionRootMutationLeases.AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (SessionRootMutationLeaseRow lease in leases)
        {
            SaveFileOperationRecord? operation = await operationStore.GetAsync(lease.OperationId, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                // A legacy mut_* lease or a dangling sfop_* lease has no
                // marker/identity facts that this recovery pass can own. Do
                // not silently treat it as expired: surface the barrier and
                // leave the row in place for an explicit repair procedure.
                throw RecoveryRequired(new InvalidOperationException(
                    $"Mutation lease '{lease.OperationId}' has no durable save operation."));
            }
            ValidateLeaseOwnership(lease, operation);
        }
    }

    private static void ValidateLeaseOwnership(SessionRootMutationLeaseRow lease, SaveFileOperationRecord operation)
    {
        if (!string.Equals(lease.SessionId, operation.SessionId, StringComparison.Ordinal) ||
            !string.Equals(lease.ActorUserId, operation.ActorUserId, StringComparison.Ordinal) ||
            !string.Equals(lease.Purpose, operation.IdempotencyScope, StringComparison.Ordinal))
            throw RecoveryRequired(new InvalidOperationException(
                $"Mutation lease '{lease.OperationId}' does not match its save operation owner or purpose."));
    }

    private async Task<SessionSaveFileObservation?> InspectMaybeAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await rootAccessor.InspectFileAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionSaveException exception) when (exception.Code == SaveErrorCodes.NotFound)
        {
            return null;
        }
    }

    private async Task<ContentFact?> ReadContentAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        SessionSaveFileRead? file;
        try
        {
            file = await rootAccessor.OpenReadAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionSaveException exception) when (exception.Code == SaveErrorCodes.NotFound)
        {
            return null;
        }
        if (file is null)
            return null;
        await using Stream content = file.Content;
        (long size, string digest) = await HashAsync(content, cancellationToken).ConfigureAwait(false);
        if (size != file.SizeBytes)
            throw RecoveryRequired();
        return new ContentFact(new SessionSaveItem(file.Path, file.Kind, size, file.ModifiedAt), size, digest);
    }

    private async Task<ContentFact?> TryReadStagingAsync(string sessionId, string operationId, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream content = await rootAccessor.OpenStagingReadAsync(sessionId, operationId, cancellationToken).ConfigureAwait(false);
            (long size, string digest) = await HashAsync(content, cancellationToken).ConfigureAwait(false);
            return new ContentFact(null, size, digest);
        }
        catch (Exception exception) when (IsMissingStagingPayload(exception))
        {
            return null;
        }
    }

    private static async Task<(long Size, string Digest)> HashAsync(Stream content, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            size = checked(size + read);
            hash.AppendData(buffer, 0, read);
        }
        return (size, $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private static bool IsMissingStagingPayload(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException ||
        exception is LinuxFileOperations.LinuxFileOperationException { Error: 2 };

    private static bool MatchesPayload(ContentFact? fact, long expectedSize, string expectedDigest) =>
        fact is not null && fact.Size == expectedSize && string.Equals(fact.Digest, expectedDigest, StringComparison.Ordinal);

    private static bool MatchesExpectedTarget(SessionSaveFileObservation? observation, SaveFileOperationRecord operation)
    {
        if (!operation.ExpectedTargetCaptured)
            return false;
        if (!operation.ExpectedTargetExists)
            return observation is null;
        return observation is not null && operation.ExpectedTargetIdentityJson is not null && MatchesIdentity(observation, operation.ExpectedTargetIdentityJson);
    }

    private static bool MatchesIdentity(SessionSaveFileObservation? observation, string expectedIdentityJson)
    {
        if (observation is null)
            return false;
        try
        {
            using JsonDocument expected = JsonDocument.Parse(expectedIdentityJson);
            using JsonDocument actual = JsonDocument.Parse(observation.IdentityJson);
            return expected.RootElement.GetProperty("deviceMajor").GetUInt32() == actual.RootElement.GetProperty("deviceMajor").GetUInt32() &&
                expected.RootElement.GetProperty("deviceMinor").GetUInt32() == actual.RootElement.GetProperty("deviceMinor").GetUInt32() &&
                expected.RootElement.GetProperty("inode").GetUInt64() == actual.RootElement.GetProperty("inode").GetUInt64() &&
                expected.RootElement.GetProperty("size").GetInt64() == actual.RootElement.GetProperty("size").GetInt64();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw RecoveryRequired(exception);
        }
    }

    private async Task<string?> FindIdempotencyKeyAsync(SaveFileOperationRecord operation, CancellationToken cancellationToken)
    {
        List<string> keys = await db.IdempotencyRecords.AsNoTracking()
            .Where(row => row.ActorUserId == operation.ActorUserId && row.ResourceId == operation.SessionId && row.Scope == operation.IdempotencyScope)
            .Select(row => row.IdempotencyKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return keys.SingleOrDefault(key => string.Equals(HashText(key), operation.IdempotencyKeyHash, StringComparison.Ordinal));
    }

    private async Task<bool> HasSaveAuditAsync(string sessionId, string operationId, string action, CancellationToken cancellationToken)
    {
        string marker = $"\"operationId\":\"{operationId}\"";
        return await db.AuditEvents.AsNoTracking().AnyAsync(row => row.ResourceType == "SESSION_SAVE" &&
            row.ResourceId == sessionId && row.Action == action && row.MetadataJson.Contains(marker), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuditActorType> GetAuditActorTypeAsync(string actorUserId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(row => row.Id == actorUserId).Select(row => row.Role).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) == UserRole.Admin
            ? AuditActorType.Admin
            : AuditActorType.User;

    private static void ValidateOperation(SaveFileOperationRecord operation)
    {
        bool valid = operation.Type switch
        {
            "IMPORT" => operation.IdempotencyScope == "SAVE_IMPORT" && operation.SourcePath is null,
            "RENAME" => operation.IdempotencyScope == "SAVE_RENAME" && operation.SourcePath is not null,
            "DELETE" => operation.IdempotencyScope == "SAVE_DELETE" && operation.SourcePath is not null,
            _ => false,
        };
        if (!valid || operation.Status is not ("PREPARED" or "STAGED" or "PUBLISHED"))
            throw RecoveryRequired();

        if (operation.Type == "IMPORT" && operation.Status is ("STAGED" or "PUBLISHED"))
        {
            if (!operation.ExpectedTargetCaptured ||
                (operation.ExpectedTargetExists && string.IsNullOrWhiteSpace(operation.ExpectedTargetIdentityJson)) ||
                (!operation.ExpectedTargetExists && operation.ExpectedTargetIdentityJson is not null))
                throw RecoveryRequired();
        }
    }

    private static SessionRootMutationPurpose ParsePurpose(string purpose) => purpose switch
    {
        "SAVE_IMPORT" => SessionRootMutationPurpose.SaveImport,
        "SAVE_RENAME" => SessionRootMutationPurpose.SaveRename,
        "SAVE_DELETE" => SessionRootMutationPurpose.SaveDelete,
        "SAVE_COPY" => SessionRootMutationPurpose.SaveCopy,
        _ => throw RecoveryRequired(),
    };

    private static SessionSaveFileKind InferKind(string path)
    {
        string file = path.Split('/', StringSplitOptions.None)[^1];
        if (file.Equals("global.sav", StringComparison.Ordinal)) return SessionSaveFileKind.Global;
        if (file.StartsWith("save", StringComparison.Ordinal)) return SessionSaveFileKind.Normal;
        if (file.StartsWith("txt", StringComparison.Ordinal)) return SessionSaveFileKind.AuxiliaryText;
        if (file.StartsWith("img", StringComparison.Ordinal)) return SessionSaveFileKind.AuxiliaryImage;
        throw RecoveryRequired();
    }

    private static long ReadExpectedSize(string identityJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(identityJson);
            return document.RootElement.GetProperty("size").GetInt64();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw RecoveryRequired(exception);
        }
    }

    private static string HashText(string value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";

    private static SessionSaveException RecoveryRequired(Exception? innerException = null) =>
        new(SaveErrorCodes.RecoveryRequired, "存档操作等待恢复。", 503, innerException);

    private sealed record ContentFact(SessionSaveItem? Item, long Size, string Digest);
}
