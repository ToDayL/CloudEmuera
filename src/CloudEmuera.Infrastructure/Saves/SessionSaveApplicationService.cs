using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Saves;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CloudEmuera.RuntimeAdapter;

namespace CloudEmuera.Infrastructure.Saves;

public sealed class SessionSaveApplicationService(
    CloudEmueraDbContext db,
    ISessionSaveRootAccessor rootAccessor,
    ISaveFileOperationStore operationStore,
    ISaveFileFormatValidator formatValidator,
    ISessionRootMutationLeaseStore mutationLeases,
    IResourceAuthorizer authorizer,
    InstanceCapacityOptions capacityOptions,
    SqliteDatabaseOptions databaseOptions,
    IAuditContext auditContext,
    TimeProvider timeProvider) : ISessionSaveApplicationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ImportScope = "SAVE_IMPORT";
    private const string RenameScope = "SAVE_RENAME";
    private const string DeleteScope = "SAVE_DELETE";
    private static readonly TimeSpan MutationLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MutationLeaseRenewalInterval = TimeSpan.FromSeconds(30);

    public async Task<SessionSaveList> ListAsync(CurrentActor actor, string sessionId, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(actor, sessionId, ResourceAction.SaveList, cancellationToken).ConfigureAwait(false);
        SessionSaveRootSnapshot snapshot = await rootAccessor.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionSaveList(1, snapshot.Layout, snapshot.Items);
    }

    public async Task<SessionSaveDownload> OpenReadAsync(CurrentActor actor, string sessionId, string path, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(actor, sessionId, ResourceAction.SaveDownload, cancellationToken).ConfigureAwait(false);
        SessionSaveFileRead? file = await rootAccessor.OpenReadAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        if (file is null)
            throw new SessionSaveException(SaveErrorCodes.NotFound, "存档文件不存在。", 404);
        return new SessionSaveDownload(file.Path, file.Kind, file.SizeBytes, file.ModifiedAt, file.Content);
    }

    public async Task<SessionSaveMutationResult> ImportAsync(SaveImportCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        SessionRow session = await AuthorizeAsync(command.Actor, command.SessionId, ResourceAction.SaveMutate, cancellationToken).ConfigureAwait(false);
        if (command.DeclaredLength is < 0 || command.DeclaredLength > capacityOptions.MaxSaveFileBytes)
            throw new SessionSaveException(SaveErrorCodes.FileTooLarge, "存档文件超过大小上限。", 413);

        string storageKey = StorageIdempotencyKey(command.SessionId, command.IdempotencyKey);
        string keyHash = HashText(storageKey);
        SaveFileOperationRecord? existing = await operationStore.FindByIdempotencyAsync(command.Actor.UserId, ImportScope, keyHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            (long size, string digest) = await DrainAndHashAsync(command.Content, cancellationToken).ConfigureAwait(false);
            return await ReplayOrRejectAsync(existing, command.TargetPath, digest, size, command.DeclaredLength, storageKey, cancellationToken).ConfigureAwait(false);
        }

        IdempotencyRecordRow? existingIdempotency = await FindIdempotencyAsync(command.Actor.UserId, command.SessionId, ImportScope, storageKey, cancellationToken).ConfigureAwait(false);
        if (existingIdempotency is not null)
        {
            (long size, string digest) = await DrainAndHashAsync(command.Content, cancellationToken).ConfigureAwait(false);
            string requestDigest = RequestDigest(new { command.TargetPath, size, digest });
            if (existingIdempotency.Status == IdempotencyRecordStatus.InProgress)
            {
                string provisionalDigest = RequestDigest(new { command.TargetPath, command.DeclaredLength });
                if (!string.Equals(existingIdempotency.RequestDigest, provisionalDigest, StringComparison.Ordinal) &&
                    !string.Equals(existingIdempotency.RequestDigest, requestDigest, StringComparison.Ordinal))
                    throw IdempotencyConflict();
                throw RecoveryRequired();
            }
            if (!string.Equals(existingIdempotency.RequestDigest, requestDigest, StringComparison.Ordinal))
                throw IdempotencyConflict();
            return ReadIdempotencyReplay(existingIdempotency);
        }

        EnsureQuiescent(session);
        EnsureSpace();

        string operationId = $"sfop_{Guid.CreateVersion7():N}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        SaveFileOperationRecord operation = NewOperation(
            operationId,
            command.SessionId,
            command.Actor.UserId,
            ImportScope,
            keyHash,
            "IMPORT",
            sourcePath: null,
            command.TargetPath,
            now);
        AddIdempotencyRecord(command.Actor.UserId, command.SessionId, ImportScope, storageKey, RequestDigest(new { command.TargetPath, command.DeclaredLength }), now, 201);
        try
        {
            await operationStore.CreatePreparedAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            if (IsConstraintViolation(exception))
                return await ResolveConcurrentImportAsync(command, storageKey, keyHash, cancellationToken).ConfigureAwait(false);
            throw StorageFailure(exception);
        }
        db.ChangeTracker.Clear();

        SessionSaveStaging? staging = null;
        SessionRootMutationLease? lease = null;
        MutationLeaseGuard? leaseGuard = null;
        bool publishAttempted = false;
        bool published = false;
        bool retainLeaseForRecovery = false;
        try
        {
            // The mutation lease covers staging, validation and publication.
            // This prevents a slow upload from becoming an unowned STAGED
            // operation that can race a later save request or recovery pass.
            lease = await AcquireMutationAsync(command.SessionId, command.Actor.UserId, operationId, SessionRootMutationPurpose.SaveImport, cancellationToken).ConfigureAwait(false);
            leaseGuard = new MutationLeaseGuard(mutationLeases, timeProvider, lease, MutationLeaseDuration, MutationLeaseRenewalInterval);
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            session = await ReloadSessionAsync(command.SessionId, command.Actor.UserId, CancellationToken.None).ConfigureAwait(false);
            EnsureQuiescent(session);
            SessionSaveFileObservation? expectedTarget = await rootAccessor.InspectFileAsync(command.SessionId, command.TargetPath, CancellationToken.None).ConfigureAwait(false);
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            staging = await rootAccessor.CreateStagingAsync(command.SessionId, operationId, command.TargetPath, command.Actor.UserId, cancellationToken).ConfigureAwait(false);
            (long size, string digest) = await CopyAndHashAsync(command.Content, staging.Content, cancellationToken).ConfigureAwait(false);
            await staging.Content.FlushAsync(cancellationToken).ConfigureAwait(false);
            await staging.Content.DisposeAsync().ConfigureAwait(false);
            staging = staging with { Content = Stream.Null };
            EnsureSpace();
            // Persist the content digest before format validation so even a
            // terminal 415 response can safely distinguish a later replay
            // with a different body.
            await UpdateIdempotencyDigestAsync(command.Actor.UserId, command.SessionId, ImportScope, storageKey, RequestDigest(new { command.TargetPath, size, digest }), CancellationToken.None).ConfigureAwait(false);
            await rootAccessor.FinalizeStagingAsync(command.SessionId, operationId, cancellationToken).ConfigureAwait(false);
            await using Stream stagedContent = await rootAccessor.OpenStagingReadAsync(command.SessionId, operationId, cancellationToken).ConfigureAwait(false);
            SaveFormatValidationResult validation = await formatValidator.ValidateAsync(stagedContent, InferKind(command.TargetPath), LastPathSegment(command.TargetPath), size, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(validation.Digest, digest, StringComparison.Ordinal) || validation.SizeBytes != size)
                throw new SessionSaveException(SaveErrorCodes.FormatInvalid, "存档文件格式无效。", 415);
            if (!await operationStore.MarkStagedAsync(operationId, size, digest, staging.PayloadPath,
                expectedTargetCaptured: true,
                expectedTargetExists: expectedTarget is not null,
                expectedTargetIdentityJson: expectedTarget?.IdentityJson,
                cancellationToken).ConfigureAwait(false))
                throw RecoveryRequired();
            SessionSaveFileObservation? currentTarget = await rootAccessor.InspectFileAsync(command.SessionId, command.TargetPath, CancellationToken.None).ConfigureAwait(false);
            if (!MatchesExpectedTarget(currentTarget, expectedTarget))
                throw RecoveryRequired();
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            publishAttempted = true;
            SessionSavePublishResult publication = await rootAccessor.PublishAsync(command.SessionId, operationId, command.TargetPath, replace: true, CancellationToken.None).ConfigureAwait(false);
            published = true;
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            if (!await operationStore.MarkPublishedAsync(operationId, CancellationToken.None).ConfigureAwait(false))
                throw RecoveryRequired();
            int status = publication.Created ? 201 : 204;
            SessionSaveMutationResult result = new(status, false, publication.Target.ToItem());
            await CommitSuccessAsync(operationId, command.Actor, command.SessionId, ImportScope, storageKey, result,
                AuditActions.SessionSaveImported, new { path = publication.Target.Path, kind = publication.Target.Kind.ToString().ToUpperInvariant(), size = publication.Target.SizeBytes, digest }, CancellationToken.None).ConfigureAwait(false);
            await leaseGuard.DisposeAsync().ConfigureAwait(false);
            leaseGuard = null;
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            lease = null;
            TryCleanup(command.SessionId, operationId);
            return result;
        }
        catch (Exception exception)
        {
            bool rethrowWrapped = false;
            bool recoveryRequired = leaseGuard?.IsLost == true ||
                (publishAttempted && exception is not SessionSaveException { Code: SaveErrorCodes.TargetExists }) ||
                exception is SessionSaveException saveException && saveException.Code == SaveErrorCodes.RecoveryRequired;
            if (published)
                recoveryRequired = true;
            if (recoveryRequired)
            {
                retainLeaseForRecovery = lease is not null;
                if (exception is not SessionSaveException { Code: SaveErrorCodes.RecoveryRequired })
                {
                    exception = new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档发布等待恢复。", 503, exception);
                    rethrowWrapped = true;
                }
            }
            else
            {
                if (exception is not SessionSaveException and not OperationCanceledException)
                {
                    exception = StorageFailure(exception);
                    rethrowWrapped = true;
                }
                bool failed = await FailOperationAsync(operationId, command.Actor.UserId, ImportScope, storageKey, exception is SessionSaveException failure ? failure.Code : SaveErrorCodes.StorageFailure, exception is SessionSaveException failedException ? failedException.StatusCode : 503).ConfigureAwait(false);
                if (failed)
                    TryCleanup(command.SessionId, operationId);
            }
            if (rethrowWrapped)
                throw exception;
            throw;
        }
        finally
        {
            if (staging is not null && staging.Content != Stream.Null)
                await staging.Content.DisposeAsync().ConfigureAwait(false);
            if (leaseGuard is not null)
                await leaseGuard.DisposeAsync().ConfigureAwait(false);
            if (lease is not null && !retainLeaseForRecovery)
                await ReleaseLeaseAsync(lease).ConfigureAwait(false);
        }
    }

    public async Task<SessionSaveMutationResult> RenameAsync(SaveRenameCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        SessionRow session = await AuthorizeAsync(command.Actor, command.SessionId, ResourceAction.SaveMutate, cancellationToken).ConfigureAwait(false);
        string storageKey = StorageIdempotencyKey(command.SessionId, command.IdempotencyKey);
        string keyHash = HashText(storageKey);
        SaveFileOperationRecord? existing = await operationStore.FindByIdempotencyAsync(command.Actor.UserId, RenameScope, keyHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayOrRejectAsync(existing, command.SourcePath, command.TargetPath, storageKey, cancellationToken).ConfigureAwait(false);
        await EnsureNoExistingIdempotencyAsync(command.Actor.UserId, command.SessionId, RenameScope, storageKey, cancellationToken).ConfigureAwait(false);
        EnsureQuiescent(session);
        SessionSaveFileObservation? source = await rootAccessor.InspectFileAsync(command.SessionId, command.SourcePath, cancellationToken).ConfigureAwait(false);
        if (source is null)
            throw new SessionSaveException(SaveErrorCodes.NotFound, "存档文件不存在。", 404);

        string operationId = $"sfop_{Guid.CreateVersion7():N}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        SaveFileOperationRecord operation = NewOperation(operationId, command.SessionId, command.Actor.UserId, RenameScope, keyHash, "RENAME", command.SourcePath, command.TargetPath, now) with
        {
            ExpectedSourceIdentityJson = source.IdentityJson,
        };
        AddIdempotencyRecord(command.Actor.UserId, command.SessionId, RenameScope, storageKey, RequestDigest(new { command.SourcePath, command.TargetPath }), now, 204);
        try
        {
            await operationStore.CreatePreparedAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            if (IsConstraintViolation(exception))
                return await ResolveConcurrentOperationAsync(command.Actor.UserId, command.SessionId, RenameScope, storageKey,
                    keyHash, command.SourcePath, command.TargetPath, cancellationToken).ConfigureAwait(false);
            throw StorageFailure(exception);
        }
        db.ChangeTracker.Clear();
        SessionRootMutationLease? lease = null;
        MutationLeaseGuard? leaseGuard = null;
        bool renameAttempted = false;
        bool mutated = false;
        bool retainLeaseForRecovery = false;
        try
        {
            lease = await AcquireMutationAsync(command.SessionId, command.Actor.UserId, operationId, SessionRootMutationPurpose.SaveRename, cancellationToken).ConfigureAwait(false);
            leaseGuard = new MutationLeaseGuard(mutationLeases, timeProvider, lease, MutationLeaseDuration, MutationLeaseRenewalInterval);
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            SessionSaveFileObservation? currentSource = await rootAccessor.InspectFileAsync(command.SessionId, command.SourcePath, CancellationToken.None).ConfigureAwait(false);
            if (!MatchesIdentity(currentSource, source.IdentityJson))
                throw RecoveryRequired();
            renameAttempted = true;
            SessionSaveRenameResult renamed = await rootAccessor.RenameAsync(command.SessionId, command.SourcePath, command.TargetPath, CancellationToken.None).ConfigureAwait(false);
            mutated = true;
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            if (!await operationStore.MarkPublishedAsync(operationId, CancellationToken.None).ConfigureAwait(false))
                throw RecoveryRequired();
            SessionSaveMutationResult result = new(204, false, renamed.Target.ToItem());
            await CommitSuccessAsync(operationId, command.Actor, command.SessionId, RenameScope, storageKey, result,
                AuditActions.SessionSaveRenamed, new { sourcePath = command.SourcePath, targetPath = command.TargetPath, kind = renamed.Target.Kind.ToString().ToUpperInvariant(), size = renamed.Target.SizeBytes }, CancellationToken.None).ConfigureAwait(false);
            await leaseGuard.DisposeAsync().ConfigureAwait(false);
            leaseGuard = null;
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            lease = null;
            return result;
        }
        catch (Exception exception)
        {
            bool rethrowWrapped = false;
            bool recoveryRequired = leaseGuard?.IsLost == true ||
                (renameAttempted && exception is not SessionSaveException { Code: SaveErrorCodes.TargetExists }) ||
                exception is SessionSaveException saveException && saveException.Code == SaveErrorCodes.RecoveryRequired;
            if (mutated)
                recoveryRequired = true;
            if (recoveryRequired)
            {
                retainLeaseForRecovery = lease is not null;
                if (exception is not SessionSaveException { Code: SaveErrorCodes.RecoveryRequired })
                {
                    exception = new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档重命名等待恢复。", 503, exception);
                    rethrowWrapped = true;
                }
            }
            else
            {
                if (exception is not SessionSaveException and not OperationCanceledException)
                {
                    exception = StorageFailure(exception);
                    rethrowWrapped = true;
                }
                _ = await FailOperationAsync(operationId, command.Actor.UserId, RenameScope, storageKey, exception is SessionSaveException failure ? failure.Code : SaveErrorCodes.StorageFailure, exception is SessionSaveException failedException ? failedException.StatusCode : 503).ConfigureAwait(false);
            }
            if (rethrowWrapped)
                throw exception;
            throw;
        }
        finally
        {
            if (leaseGuard is not null)
                await leaseGuard.DisposeAsync().ConfigureAwait(false);
            if (lease is not null && !retainLeaseForRecovery)
                await ReleaseLeaseAsync(lease).ConfigureAwait(false);
        }
    }

    public async Task<SessionSaveMutationResult> DeleteAsync(SaveDeleteCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        SessionRow session = await AuthorizeAsync(command.Actor, command.SessionId, ResourceAction.SaveMutate, cancellationToken).ConfigureAwait(false);
        if (!command.Confirmed)
            throw new SessionSaveException(SaveErrorCodes.DeleteConfirmationRequired, "删除存档需要显式确认。", 428);
        string storageKey = StorageIdempotencyKey(command.SessionId, command.IdempotencyKey);
        string keyHash = HashText(storageKey);
        SaveFileOperationRecord? existing = await operationStore.FindByIdempotencyAsync(command.Actor.UserId, DeleteScope, keyHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayOrRejectAsync(existing, command.SourcePath, command.SourcePath, storageKey, cancellationToken).ConfigureAwait(false);
        await EnsureNoExistingIdempotencyAsync(command.Actor.UserId, command.SessionId, DeleteScope, storageKey, cancellationToken).ConfigureAwait(false);
        EnsureQuiescent(session);
        SessionSaveFileObservation? source = await rootAccessor.InspectFileAsync(command.SessionId, command.SourcePath, cancellationToken).ConfigureAwait(false);
        if (source is null)
            throw new SessionSaveException(SaveErrorCodes.NotFound, "存档文件不存在。", 404);

        string operationId = $"sfop_{Guid.CreateVersion7():N}";
        DateTimeOffset now = timeProvider.GetUtcNow();
        SaveFileOperationRecord operation = NewOperation(operationId, command.SessionId, command.Actor.UserId, DeleteScope, keyHash, "DELETE", command.SourcePath, command.SourcePath, now) with
        {
            ExpectedSourceIdentityJson = source.IdentityJson,
        };
        AddIdempotencyRecord(command.Actor.UserId, command.SessionId, DeleteScope, storageKey, RequestDigest(new { command.SourcePath }), now, 204);
        try
        {
            await operationStore.CreatePreparedAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            if (IsConstraintViolation(exception))
                return await ResolveConcurrentOperationAsync(command.Actor.UserId, command.SessionId, DeleteScope, storageKey,
                    keyHash, command.SourcePath, command.SourcePath, cancellationToken).ConfigureAwait(false);
            throw StorageFailure(exception);
        }
        db.ChangeTracker.Clear();
        SessionRootMutationLease? lease = null;
        MutationLeaseGuard? leaseGuard = null;
        bool deleteAttempted = false;
        bool mutated = false;
        bool retainLeaseForRecovery = false;
        try
        {
            lease = await AcquireMutationAsync(command.SessionId, command.Actor.UserId, operationId, SessionRootMutationPurpose.SaveDelete, cancellationToken).ConfigureAwait(false);
            leaseGuard = new MutationLeaseGuard(mutationLeases, timeProvider, lease, MutationLeaseDuration, MutationLeaseRenewalInterval);
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            deleteAttempted = true;
            bool removed = await rootAccessor.DeleteAsync(command.SessionId, command.SourcePath, source.IdentityJson, CancellationToken.None).ConfigureAwait(false);
            if (!removed)
                throw new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档删除等待恢复。", 503);
            mutated = true;
            await leaseGuard.EnsureValidAsync().ConfigureAwait(false);
            if (!await operationStore.MarkPublishedAsync(operationId, CancellationToken.None).ConfigureAwait(false))
                throw RecoveryRequired();
            SessionSaveMutationResult result = new(204, false, source.ToItem());
            await CommitSuccessAsync(operationId, command.Actor, command.SessionId, DeleteScope, storageKey, result,
                AuditActions.SessionSaveDeleted, new { path = command.SourcePath, kind = source.Kind.ToString().ToUpperInvariant(), size = source.SizeBytes }, CancellationToken.None).ConfigureAwait(false);
            await leaseGuard.DisposeAsync().ConfigureAwait(false);
            leaseGuard = null;
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            lease = null;
            return result;
        }
        catch (Exception exception)
        {
            bool rethrowWrapped = false;
            bool recoveryRequired = leaseGuard?.IsLost == true || deleteAttempted || exception is SessionSaveException saveException && saveException.Code == SaveErrorCodes.RecoveryRequired;
            if (mutated)
                recoveryRequired = true;
            if (recoveryRequired)
            {
                retainLeaseForRecovery = lease is not null;
                if (exception is not SessionSaveException { Code: SaveErrorCodes.RecoveryRequired })
                {
                    exception = new SessionSaveException(SaveErrorCodes.RecoveryRequired, "存档删除等待恢复。", 503, exception);
                    rethrowWrapped = true;
                }
            }
            else
            {
                if (exception is not SessionSaveException and not OperationCanceledException)
                {
                    exception = StorageFailure(exception);
                    rethrowWrapped = true;
                }
                _ = await FailOperationAsync(operationId, command.Actor.UserId, DeleteScope, storageKey, exception is SessionSaveException failure ? failure.Code : SaveErrorCodes.StorageFailure, exception is SessionSaveException failedException ? failedException.StatusCode : 503).ConfigureAwait(false);
            }
            if (rethrowWrapped)
                throw exception;
            throw;
        }
        finally
        {
            if (leaseGuard is not null)
                await leaseGuard.DisposeAsync().ConfigureAwait(false);
            if (lease is not null && !retainLeaseForRecovery)
                await ReleaseLeaseAsync(lease).ConfigureAwait(false);
        }
    }

    private async Task<SessionRow> AuthorizeAsync(CurrentActor actor, string sessionId, ResourceAction action, CancellationToken cancellationToken)
    {
        SessionRow? session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == sessionId && row.OwnerUserId == actor.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
            throw new SessionSaveException(SaveErrorCodes.SessionNotFound, "Session 不存在。", 404);
        ResourceAccessDecision decision = await authorizer.AuthorizeAsync(actor, ResourceKind.Save, sessionId, action, false, cancellationToken).ConfigureAwait(false);
        if (decision != ResourceAccessDecision.Allowed)
            throw new SessionSaveException(SaveErrorCodes.SessionNotFound, "Session 不存在。", 404);
        return session;
    }

    private static void EnsureQuiescent(SessionRow session)
    {
        if (session.State is not (SessionState.Closed or SessionState.Crashed))
            throw new SessionSaveException(SaveErrorCodes.SessionNotQuiescent, "Session 必须处于已关闭或已完成回收的崩溃状态。", 409);
    }

    private async Task<SessionRow> ReloadSessionAsync(string sessionId, string actorUserId, CancellationToken cancellationToken) =>
        await db.Sessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId, cancellationToken).ConfigureAwait(false)
        ?? throw new SessionSaveException(SaveErrorCodes.SessionNotFound, "Session 不存在。", 404);

    private async Task<SessionRootMutationLease> AcquireMutationAsync(string sessionId, string actorUserId, string operationId, SessionRootMutationPurpose purpose, CancellationToken cancellationToken)
    {
        SessionRootMutationAcquireResult acquired = await mutationLeases.TryAcquireAsync(sessionId, actorUserId, operationId, purpose, MutationLeaseDuration, cancellationToken).ConfigureAwait(false);
        return acquired.Failure switch
        {
            SessionRootMutationAcquireFailure.None when acquired.Lease is not null => acquired.Lease,
            SessionRootMutationAcquireFailure.RecoveryRequired => throw RecoveryRequired(),
            SessionRootMutationAcquireFailure.MutationLeaseActive => throw new SessionSaveException(SaveErrorCodes.MutationInProgress, "Session 的另一个存档操作正在执行。", 409),
            SessionRootMutationAcquireFailure.WorkerLeaseActive => throw new SessionSaveException(SaveErrorCodes.SessionHasActiveWorker, "Session 仍有活动 Worker。", 409),
            SessionRootMutationAcquireFailure.SessionNotQuiescent => throw new SessionSaveException(SaveErrorCodes.SessionNotQuiescent, "Session 必须处于静止状态。", 409),
            _ => throw new SessionSaveException(SaveErrorCodes.SessionRootInvalid, "SessionRoot 无效。", 503),
        };
    }

    private async Task ReleaseLeaseAsync(SessionRootMutationLease lease) =>
        _ = await mutationLeases.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);

    private void EnsureSpace()
    {
        try
        {
            DriveInfo drive = new(Path.GetFullPath(databaseOptions.DataRoot));
            if (drive.AvailableFreeSpace < capacityOptions.MinDataRootFreeBytes)
                throw new SessionSaveException(SaveErrorCodes.DataRootSpaceLow, "数据目录可用空间不足。", 503);
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new SessionSaveException(SaveErrorCodes.DataRootSpaceLow, "数据目录空间无法确认。", 503, exception);
        }
    }

    private async Task<(long Size, string Digest)> CopyAndHashAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            size = checked(size + read);
            if (size > capacityOptions.MaxSaveFileBytes)
                throw new SessionSaveException(SaveErrorCodes.FileTooLarge, "存档文件超过大小上限。", 413);
            EnsureSpace();
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return (size, $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private async Task<(long Size, string Digest)> DrainAndHashAsync(Stream source, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long size = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            size = checked(size + read);
            if (size > capacityOptions.MaxSaveFileBytes)
                throw new SessionSaveException(SaveErrorCodes.FileTooLarge, "存档文件超过大小上限。", 413);
            hash.AppendData(buffer, 0, read);
        }
        return (size, $"sha256:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
    }

    private async Task<IdempotencyRecordRow?> FindIdempotencyAsync(string actorUserId, string sessionId, string scope, string storageKey, CancellationToken cancellationToken)
    {
        IdempotencyRecordRow? record = await db.IdempotencyRecords.SingleOrDefaultAsync(row =>
            row.ActorUserId == actorUserId && row.ResourceId == sessionId && row.Scope == scope && row.IdempotencyKey == storageKey,
            cancellationToken).ConfigureAwait(false);
        if (record is not null && record.ExpiresAt <= timeProvider.GetUtcNow())
        {
            db.IdempotencyRecords.Remove(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        return record;
    }

    private async Task EnsureNoExistingIdempotencyAsync(string actorUserId, string sessionId, string scope, string storageKey, CancellationToken cancellationToken)
    {
        IdempotencyRecordRow? existing = await FindIdempotencyAsync(actorUserId, sessionId, scope, storageKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return;
        if (existing.Status == IdempotencyRecordStatus.InProgress)
            throw RecoveryRequired();
        throw ReadIdempotencyFailure(existing);
    }

    private static SessionSaveException ReadIdempotencyFailure(IdempotencyRecordRow record) =>
        new(record.ErrorCode ?? SaveErrorCodes.StorageFailure, "幂等请求此前失败。", record.ResponseStatus);

    private void AddIdempotencyRecord(string actorUserId, string sessionId, string scope, string storageKey, string digest, DateTimeOffset now, int responseStatus) =>
        db.IdempotencyRecords.Add(new IdempotencyRecordRow
        {
            ActorUserId = actorUserId,
            ResourceId = sessionId,
            Scope = scope,
            IdempotencyKey = storageKey,
            RequestDigest = digest,
            Status = IdempotencyRecordStatus.InProgress,
            ResponseStatus = responseStatus,
            ResponseJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
        });

    private async Task UpdateIdempotencyDigestAsync(string actorUserId, string sessionId, string scope, string storageKey, string digest, CancellationToken cancellationToken)
    {
        await db.IdempotencyRecords.Where(row => row.ActorUserId == actorUserId && row.ResourceId == sessionId && row.Scope == scope && row.IdempotencyKey == storageKey && row.Status == IdempotencyRecordStatus.InProgress)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RequestDigest, digest).SetProperty(row => row.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CommitSuccessAsync(
        string operationId,
        CurrentActor actor,
        string sessionId,
        string scope,
        string idempotencyKey,
        SessionSaveMutationResult result,
        string auditAction,
        object metadata,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string resultJson = JsonSerializer.Serialize(result, JsonOptions);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveFileOperationRow operation = await db.SaveFileOperations.SingleAsync(row => row.Id == operationId, cancellationToken).ConfigureAwait(false);
        operation.Status = SaveFileOperationStatus.Committed;
        operation.ResultJson = resultJson;
        operation.ErrorCode = null;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
        operation.StateVersion++;
        IdempotencyRecordRow idempotency = await db.IdempotencyRecords.SingleAsync(row =>
            row.ActorUserId == actor.UserId && row.ResourceId == sessionId && row.Scope == scope && row.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        idempotency.Status = IdempotencyRecordStatus.Succeeded;
        idempotency.ResponseStatus = result.StatusCode;
            idempotency.ResponseJson = resultJson;
            idempotency.ErrorCode = null;
            idempotency.UpdatedAt = now;
            idempotency.CompletedAt = now;
            idempotency.ExpiresAt = now.AddHours(24);
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = now,
            ActorUserId = actor.UserId,
            ActorType = actor.IsAdmin ? AuditActorType.Admin : AuditActorType.User,
            Action = auditAction,
            ResourceType = "SESSION_SAVE",
            ResourceId = sessionId,
            RequestId = auditContext.RequestId,
            Result = AuditResult.Succeeded,
            MetadataJson = JsonSerializer.Serialize(new { operationId, metadata }, JsonOptions),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> FailOperationAsync(string operationId, string actorUserId, string scope, string key, string errorCode, int responseStatus)
    {
        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            SaveFileOperationRow row = await db.SaveFileOperations.SingleAsync(item => item.Id == operationId, CancellationToken.None).ConfigureAwait(false);
            if (row.Status == SaveFileOperationStatus.Published)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            if (row.Status is SaveFileOperationStatus.Committed or SaveFileOperationStatus.Failed)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return row.Status == SaveFileOperationStatus.Failed;
            }

            row.Status = SaveFileOperationStatus.Failed;
            row.ResultJson = "{}";
            row.ErrorCode = errorCode;
            row.CompletedAt = now;
            row.UpdatedAt = now;
            row.StateVersion = checked(row.StateVersion + 1);
            IdempotencyRecordRow? idempotency = await db.IdempotencyRecords.SingleOrDefaultAsync(item =>
                item.ActorUserId == actorUserId && item.ResourceId == row.SessionId && item.Scope == scope && item.IdempotencyKey == key,
                CancellationToken.None).ConfigureAwait(false);
            if (idempotency is not null && idempotency.Status == IdempotencyRecordStatus.InProgress)
            {
                idempotency.Status = IdempotencyRecordStatus.Failed;
                idempotency.ErrorCode = errorCode;
                idempotency.ResponseStatus = responseStatus;
                idempotency.ResponseJson = "{}";
                idempotency.UpdatedAt = now;
                idempotency.CompletedAt = now;
                idempotency.ExpiresAt = now.AddHours(24);
            }
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // The operation remains non-terminal and is intentionally left for
            // the recovery service rather than being guessed away in memory.
            return false;
        }
    }

    private void TryCleanup(string sessionId, string operationId)
    {
        try
        {
            rootAccessor.CleanupStagingAsync(sessionId, operationId, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Published content is authoritative; recovery may remove only a
            // marker-owned staging tree later.
        }
    }

    private static SaveFileOperationRecord NewOperation(string id, string sessionId, string actorUserId, string scope, string keyHash, string type, string? sourcePath, string targetPath, DateTimeOffset now) =>
        new(id, sessionId, actorUserId, scope, keyHash, type, "PREPARED", sourcePath, targetPath, null, null, null, null, "{}", null, now, now, null, 0);

    private static string HashText(string value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";

    private static string RequestDigest(object value) =>
        HashText(JsonSerializer.Serialize(value, JsonOptions));

    private static string LastPathSegment(string path) =>
        path.Split('/', StringSplitOptions.None)[^1];

    private static SessionSaveFileKind InferKind(string path)
    {
        string file = LastPathSegment(path);
        if (file.Equals("global.sav", StringComparison.Ordinal)) return SessionSaveFileKind.Global;
        if (file.StartsWith("save", StringComparison.Ordinal)) return SessionSaveFileKind.Normal;
        if (file.StartsWith("txt", StringComparison.Ordinal)) return SessionSaveFileKind.AuxiliaryText;
        if (file.StartsWith("img", StringComparison.Ordinal)) return SessionSaveFileKind.AuxiliaryImage;
        throw new SessionSaveException(SaveErrorCodes.PathInvalid, "存档路径无效。", 400);
    }

    private async Task<SessionSaveMutationResult> ReplayOrRejectAsync(
        SaveFileOperationRecord operation,
        string expectedSource,
        string expectedTarget,
        string storageKey,
        CancellationToken cancellationToken)
    {
        bool sameRequest = string.Equals(operation.SourcePath ?? operation.TargetPath, expectedSource, StringComparison.Ordinal) &&
            string.Equals(operation.TargetPath, expectedTarget, StringComparison.Ordinal);
        if (!sameRequest)
            throw IdempotencyConflict();

        IdempotencyRecordRow? idempotency = await FindIdempotencyAsync(
            operation.ActorUserId,
            operation.SessionId,
            operation.IdempotencyScope,
            storageKey,
            cancellationToken).ConfigureAwait(false);
        if (idempotency is null)
            throw RecoveryRequired();

        string expectedDigest = operation.Type == "DELETE"
            ? RequestDigest(new { SourcePath = expectedSource })
            : RequestDigest(new { SourcePath = expectedSource, TargetPath = expectedTarget });
        if (!string.Equals(idempotency.RequestDigest, expectedDigest, StringComparison.Ordinal))
            throw IdempotencyConflict();
        return ReplayTerminalOperation(operation, idempotency);
    }

    private async Task<SessionSaveMutationResult> ReplayOrRejectAsync(
        SaveFileOperationRecord operation,
        string expectedTarget,
        string digest,
        long size,
        long? declaredLength,
        string storageKey,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(operation.TargetPath, expectedTarget, StringComparison.Ordinal))
            throw IdempotencyConflict();

        IdempotencyRecordRow? idempotency = await FindIdempotencyAsync(
            operation.ActorUserId,
            operation.SessionId,
            operation.IdempotencyScope,
            storageKey,
            cancellationToken).ConfigureAwait(false);
        if (idempotency is null)
            throw RecoveryRequired();

        string actualDigest = RequestDigest(new { TargetPath = expectedTarget, Size = size, Digest = digest });
        if (operation.PayloadDigest is not null &&
            (!string.Equals(operation.PayloadDigest, digest, StringComparison.Ordinal) || operation.PayloadSize != size))
            throw IdempotencyConflict();

        if (operation.Status is "PREPARED" or "STAGED")
        {
            string provisionalDigest = RequestDigest(new { TargetPath = expectedTarget, DeclaredLength = declaredLength });
            if (!string.Equals(idempotency.RequestDigest, actualDigest, StringComparison.Ordinal) &&
                !string.Equals(idempotency.RequestDigest, provisionalDigest, StringComparison.Ordinal))
                throw IdempotencyConflict();
            throw RecoveryRequired();
        }

        if (!string.Equals(idempotency.RequestDigest, actualDigest, StringComparison.Ordinal))
            throw IdempotencyConflict();
        return ReplayTerminalOperation(operation, idempotency);
    }

    private async Task<SessionSaveMutationResult> ResolveConcurrentImportAsync(
        SaveImportCommand command,
        string storageKey,
        string keyHash,
        CancellationToken cancellationToken)
    {
        (long size, string digest) = await DrainAndHashAsync(command.Content, cancellationToken).ConfigureAwait(false);
        SaveFileOperationRecord? existing = await operationStore.FindByIdempotencyAsync(command.Actor.UserId, ImportScope, keyHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayOrRejectAsync(existing, command.TargetPath, digest, size, command.DeclaredLength, storageKey, cancellationToken).ConfigureAwait(false);

        IdempotencyRecordRow? idempotency = await FindIdempotencyAsync(command.Actor.UserId, command.SessionId, ImportScope, storageKey, cancellationToken).ConfigureAwait(false);
        if (idempotency is null)
            throw StorageFailure(new InvalidOperationException("The concurrent save operation disappeared before it became durable."));

        string actualDigest = RequestDigest(new { TargetPath = command.TargetPath, Size = size, Digest = digest });
        if (idempotency.Status == IdempotencyRecordStatus.InProgress)
        {
            string provisionalDigest = RequestDigest(new { TargetPath = command.TargetPath, DeclaredLength = command.DeclaredLength });
            if (!string.Equals(idempotency.RequestDigest, actualDigest, StringComparison.Ordinal) &&
                !string.Equals(idempotency.RequestDigest, provisionalDigest, StringComparison.Ordinal))
                throw IdempotencyConflict();
            throw RecoveryRequired();
        }
        if (!string.Equals(idempotency.RequestDigest, actualDigest, StringComparison.Ordinal))
            throw IdempotencyConflict();
        return ReadIdempotencyReplay(idempotency);
    }

    private async Task<SessionSaveMutationResult> ResolveConcurrentOperationAsync(
        string actorUserId,
        string sessionId,
        string scope,
        string storageKey,
        string keyHash,
        string expectedSource,
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        SaveFileOperationRecord? existing = await operationStore.FindByIdempotencyAsync(actorUserId, scope, keyHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayOrRejectAsync(existing, expectedSource, expectedTarget, storageKey, cancellationToken).ConfigureAwait(false);

        IdempotencyRecordRow? idempotency = await FindIdempotencyAsync(actorUserId, sessionId, scope, storageKey, cancellationToken).ConfigureAwait(false);
        if (idempotency is null)
            throw StorageFailure(new InvalidOperationException("The concurrent save operation disappeared before it became durable."));
        if (idempotency.Status == IdempotencyRecordStatus.InProgress)
            throw RecoveryRequired();

        string expectedDigest = scope == DeleteScope
            ? RequestDigest(new { SourcePath = expectedSource })
            : RequestDigest(new { SourcePath = expectedSource, TargetPath = expectedTarget });
        if (!string.Equals(idempotency.RequestDigest, expectedDigest, StringComparison.Ordinal))
            throw IdempotencyConflict();
        return ReadIdempotencyReplay(idempotency);
    }

    private static SessionSaveMutationResult ReplayTerminalOperation(SaveFileOperationRecord operation, IdempotencyRecordRow idempotency)
    {
        if (operation.Status == "COMMITTED" && idempotency.Status == IdempotencyRecordStatus.Succeeded)
            return ReplayResult(idempotency.ResponseJson);
        if (operation.Status == "FAILED" && idempotency.Status == IdempotencyRecordStatus.Failed)
            throw new SessionSaveException(idempotency.ErrorCode ?? operation.ErrorCode ?? SaveErrorCodes.StorageFailure, "幂等请求此前失败。", idempotency.ResponseStatus);
        throw RecoveryRequired();
    }

    private static bool IsConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is SqliteException { SqliteErrorCode: 19 };

    private static SessionSaveException StorageFailure(Exception exception) =>
        new(SaveErrorCodes.StorageFailure, "存档持久化暂时不可用。", 503, exception);

    private static string StorageIdempotencyKey(string sessionId, string rawKey) =>
        $"save:{sessionId}:{HashText(rawKey)}";

    private static bool MatchesExpectedTarget(SessionSaveFileObservation? actual, SessionSaveFileObservation? expected) =>
        expected is null
            ? actual is null
            : actual is not null && MatchesIdentity(actual, expected.IdentityJson);

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

    private static SessionSaveMutationResult ReadIdempotencyReplay(IdempotencyRecordRow record)
    {
        if (record.Status == IdempotencyRecordStatus.Succeeded)
            return ReplayResult(record.ResponseJson);
        if (record.Status == IdempotencyRecordStatus.Failed)
            throw new SessionSaveException(record.ErrorCode ?? SaveErrorCodes.StorageFailure, "幂等请求此前失败。", record.ResponseStatus);
        throw RecoveryRequired();
    }

    private static SessionSaveMutationResult ReplayResult(string json)
    {
        try
        {
            SessionSaveMutationResult result = JsonSerializer.Deserialize<SessionSaveMutationResult>(json, JsonOptions) ?? throw RecoveryRequired();
            if (result.StatusCode is not (201 or 204))
                throw RecoveryRequired();
            return result with { Replayed = true };
        }
        catch (SessionSaveException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw RecoveryRequired(exception);
        }
    }

    private static SessionSaveException IdempotencyConflict() =>
        new(SaveErrorCodes.IdempotencyKeyReused, "Idempotency-Key 已用于其他存档请求。", 409);

    private static SessionSaveException RecoveryRequired(Exception? innerException = null) =>
        new(SaveErrorCodes.RecoveryRequired, "存档操作等待恢复。", 503, innerException);

    private sealed class MutationLeaseGuard : IAsyncDisposable
    {
        private readonly ISessionRootMutationLeaseStore store;
        private readonly TimeProvider timeProvider;
        private readonly TimeSpan duration;
        private readonly TimeSpan renewalInterval;
        private readonly CancellationTokenSource stop = new();
        private readonly Task renewalLoop;
        private SessionRootMutationLease lease;
        private int lost;
        private int disposed;

        public MutationLeaseGuard(
            ISessionRootMutationLeaseStore store,
            TimeProvider timeProvider,
            SessionRootMutationLease lease,
            TimeSpan duration,
            TimeSpan renewalInterval)
        {
            this.store = store;
            this.timeProvider = timeProvider;
            this.lease = lease;
            this.duration = duration;
            this.renewalInterval = renewalInterval;
            renewalLoop = RenewPeriodicallyAsync();
        }

        public bool IsLost => Volatile.Read(ref lost) != 0;

        public async Task EnsureValidAsync()
        {
            if (Volatile.Read(ref lost) != 0)
                throw RecoveryRequired();
            if (!await RenewOnceAsync(CancellationToken.None).ConfigureAwait(false))
                throw RecoveryRequired();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            stop.Cancel();
            try
            {
                await renewalLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            stop.Dispose();
        }

        private async Task RenewPeriodicallyAsync()
        {
            try
            {
                using PeriodicTimer timer = new(renewalInterval);
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                {
                    if (!await RenewOnceAsync(CancellationToken.None).ConfigureAwait(false))
                        return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch
            {
                Volatile.Write(ref lost, 1);
            }
        }

        private async Task<bool> RenewOnceAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref lost) != 0)
                return false;
            bool renewed = await store.RenewAsync(lease, duration, cancellationToken).ConfigureAwait(false);
            if (!renewed)
            {
                Volatile.Write(ref lost, 1);
                return false;
            }
            lease = lease with { ExpiresAt = timeProvider.GetUtcNow().Add(duration) };
            return true;
        }
    }
}

internal static class SessionSaveProjectionExtensions
{
    public static SessionSaveItem ToItem(this SessionSaveFileObservation observation) =>
        new(observation.Path, observation.Kind, observation.SizeBytes, observation.ModifiedAt);
}
