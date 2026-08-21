using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.Application.Authorization;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Games;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.RuntimeAdapter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Infrastructure.Sessions;

/// <summary>
/// Persistent Session use cases.  The class is singleton-safe: every database
/// operation, including work continued after an HTTP disconnect, creates a new
/// scope and short-lived DbContext.
/// </summary>
public sealed partial class SqliteSessionApplicationService(
    IServiceScopeFactory scopeFactory,
    SqliteDatabaseOptions databaseOptions,
    SqliteIdempotencyStore idempotency,
    ISessionLifecycleExecutor lifecycle,
    TimeProvider timeProvider,
    ILogger<SqliteSessionApplicationService> logger,
    InstanceCapacityOptions? capacityOptions = null,
    ISessionCommandGate? commandGate = null) : ISessionApplicationService, ISessionOperationRecovery
{
    private const string CreateScope = "SESSION_CREATE";
    private const string OpenScope = "SESSION_OPEN";
    private const string CloseScope = "SESSION_CLOSE";
    private const string DeleteScope = "SESSION_DELETE";
    private static readonly TimeSpan HttpWaitBudget = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ConcurrentDictionary<string, Lazy<Task<SessionCommandResult>>> createOperations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<SessionCommandResult>>> openOperations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<SessionCommandResult>>> closeOperations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<SessionDeleteResult>>> deleteOperations = new(StringComparer.Ordinal);
    private InstanceCapacityOptions Capacity => capacityOptions ?? InstanceCapacityOptions.Default;

    public async Task<SessionCommandResult> CreateAsync(
        CurrentActor actor,
        CreateSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        string name = NormalizeName(command.Name);
        ValidateIdempotencyKey(command.IdempotencyKey);
        ValidateTextLayout(command.FontSize, command.LineHeight);
        string digest = SessionIdempotency.Digest(actor.UserId, CreateScope, "sessions", new { gameId = command.GameId, name, command.FontSize, command.LineHeight });

        CreatePreparation preparation;
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            preparation = await PrepareCreateAsync(scope.ServiceProvider, actor, command.GameId, name, command.FontSize, command.LineHeight, command.IdempotencyKey, digest, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionApplicationException)
        {
            throw;
        }
        catch (GameLibraryException exception)
        {
            throw ToSessionException(exception);
        }

        if (preparation.Existing is not null)
            return ConvertExisting(preparation);

        Task<SessionCommandResult> operation = ScheduleCreateOperation(preparation.OperationId!, preparation.SessionId!, actor.UserId);
        return await WaitForHttpBudgetAsync(operation, preparation.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        CreateRecoveryItem[] items = await db.SessionCreationOperations.AsNoTracking()
            .Where(row => row.Status != SessionCreationOperationStatus.Committed && row.Status != SessionCreationOperationStatus.Failed)
            .Select(row => new CreateRecoveryItem(row.Id, row.SessionId, row.ActorUserId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (CreateRecoveryItem item in items)
        {
            Task<SessionCommandResult> operation = ScheduleCreateOperation(item.OperationId, item.SessionId, item.ActorUserId);
            try
            {
                await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // ExecuteCreateAsync records a stable failure and performs
                // ownership-checked cleanup. Recovery continues with the next
                // operation so one damaged staging tree cannot hide others.
            }
        }

        LifecycleRecoveryItem[] lifecycleItems = await db.IdempotencyRecords.AsNoTracking()
            .Where(row => row.Status == IdempotencyRecordStatus.InProgress &&
                (row.Scope == OpenScope || row.Scope == CloseScope) && row.ResourceId != null)
            .Select(row => new LifecycleRecoveryItem(row.ActorUserId, row.Scope, row.IdempotencyKey, row.RequestDigest, row.ResourceId!))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (LifecycleRecoveryItem item in lifecycleItems)
        {
            await RecoverLifecycleOperationAsync(item, cancellationToken).ConfigureAwait(false);
        }

        DeleteRecoveryItem[] deleteItems = await db.IdempotencyRecords.AsNoTracking()
            .Where(row => row.Status == IdempotencyRecordStatus.InProgress && row.Scope == DeleteScope && row.ResourceId != null)
            .Select(row => new DeleteRecoveryItem(row.ActorUserId, row.IdempotencyKey, row.RequestDigest, row.ResourceId!))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (DeleteRecoveryItem item in deleteItems)
        {
            Task<SessionDeleteResult> operation = ScheduleDeleteOperation(
                item.ActorUserId,
                false,
                new SessionDeleteCommand(item.SessionId, item.IdempotencyKey),
                item.RequestDigest,
                allowMissingRoot: true);
            SessionDeleteResult result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Pending)
                throw new InvalidOperationException("A Session delete operation remains pending after recovery.");
        }

        // Readiness is a durable barrier, not merely an indication that one
        // recovery pass was started.  A command whose completion write failed
        // must remain IN_PROGRESS and keep the control plane closed until a
        // later pass can reconcile it.
        await EnsureNoRecoverableOperationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionListPage> ListAsync(
        CurrentActor actor,
        SessionListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (query.Limit is < 1 or > 100)
            throw new SessionApplicationException(SessionErrorCodes.ValidationFailed, "limit 必须在 1 到 100 之间。", 400);
        if (query.GameId is not null)
            ValidateGameId(query.GameId);
        CursorData? cursor = DecodeCursor(actor.UserId, query, query.Cursor);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        IQueryable<SessionRow> source = db.Sessions.AsNoTracking()
            .Where(row => row.OwnerUserId == actor.UserId &&
                (query.GameId == null || row.GameId == query.GameId) &&
                (query.State == null || row.State == query.State));
        if (cursor is not null)
        {
            DateTimeOffset after = DateTimeOffset.FromUnixTimeMilliseconds(cursor.CreatedAtUnixMilliseconds);
            source = source.Where(row => row.CreatedAt < after || (row.CreatedAt == after && row.Id.CompareTo(cursor.Id) < 0));
        }

        SessionProjection[] rows = await Project(source
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .Take(query.Limit + 1))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rows.Length > query.Limit;
        SessionProjection[] page = hasMore ? rows[..query.Limit] : rows;
        string? next = hasMore ? EncodeCursor(actor.UserId, query, page[^1]) : null;
        return new SessionListPage(page.Select(ToView).ToArray(), next);
    }

    public async Task<SessionView?> GetAsync(
        CurrentActor actor,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateSessionId(sessionId);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        SessionProjection? row = await Project(db.Sessions.AsNoTracking().Where(value => value.Id == sessionId && value.OwnerUserId == actor.UserId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToView(row);
    }

    public Task<SessionCommandResult> OpenAsync(
        CurrentActor actor,
        SessionLifecycleCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteLifecycleCommandAsync(actor, command, open: true, cancellationToken);

    public Task<SessionCommandResult> CloseAsync(
        CurrentActor actor,
        SessionLifecycleCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteLifecycleCommandAsync(actor, command, open: false, cancellationToken);

    public async Task<SessionCommandResult> UpdateConfigurationAsync(CurrentActor actor, SessionConfigurationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(command);
        ValidateSessionId(command.SessionId); ValidateIdempotencyKey(command.IdempotencyKey);
        string name = NormalizeName(command.Name); ValidateTextLayout(command.FontSize, command.LineHeight);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.Include(row => row.Game).SingleOrDefaultAsync(row => row.Id == command.SessionId && row.OwnerUserId == actor.UserId, cancellationToken).ConfigureAwait(false);
        if (session is null) throw new SessionApplicationException(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
        if (session.State is not (SessionState.Closed or SessionState.Crashed)) throw new SessionApplicationException(SessionErrorCodes.SessionNotReady, "只能在 Session 停止后修改显示配置。", 409);
        session.Name = name; session.FontSize = command.FontSize; session.LineHeight = command.LineHeight;
        session.StateVersion = checked(session.StateVersion + 1); session.LastActivityAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionCommandResult(ToView(new SessionProjection(session.Id, session.Name, session.GameId, session.Game!.Name, session.SourceContentDigest, session.SourceContentRevision, session.RuntimeVersion, session.FontSize, session.LineHeight, session.State, session.StateVersion, session.WorkerEpoch, session.WaitingForInput, session.CreatedAt, session.StartedAt, session.LastActivityAt, session.ClosedAt, session.CloseReason)), 200, false, false);
    }

    public async Task<SessionDeleteResult> DeleteAsync(
        CurrentActor actor,
        SessionDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        ValidateSessionId(command.SessionId);
        ValidateIdempotencyKey(command.IdempotencyKey);
        string digest = SessionIdempotency.Digest(actor.UserId, DeleteScope, command.SessionId, new { sessionId = command.SessionId });
        PersistentIdempotencyRecord record = await idempotency.BeginAsync(
            actor.UserId,
            DeleteScope,
            command.IdempotencyKey,
            digest,
            resourceId: command.SessionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (record.State == PersistentIdempotencyBeginState.Conflict)
            throw new SessionApplicationException(SessionErrorCodes.IdempotencyKeyReused, "Idempotency-Key 已用于另一规范请求。", 409);
        if (record.State is PersistentIdempotencyBeginState.Succeeded or PersistentIdempotencyBeginState.Failed)
            return ConvertExistingDelete(record);

        SessionView? current = await GetAsync(actor, command.SessionId, cancellationToken).ConfigureAwait(false);
        bool recovery = record.State == PersistentIdempotencyBeginState.InProgress;
        if (current is null)
        {
            if (recovery)
            {
                if (!await TryCompleteDeleteSuccessAsync(actor.UserId, command.IdempotencyKey, digest, command.SessionId).ConfigureAwait(false))
                    return new SessionDeleteResult(202, Replayed: true, Pending: true);
                return new SessionDeleteResult(204, Replayed: true, Pending: false);
            }

            return await CompleteDeleteFailureResultAsync(
                actor.UserId,
                command.IdempotencyKey,
                digest,
                command.SessionId,
                new SessionCommandFailure(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404),
                replayed: false).ConfigureAwait(false);
        }

        if (!recovery && !current.State.IsQuiescent())
        {
            return await CompleteDeleteFailureResultAsync(
                actor.UserId,
                command.IdempotencyKey,
                digest,
                command.SessionId,
                new SessionCommandFailure(SessionErrorCodes.SessionNotDeletable, "仅 CLOSED 或 CRASHED Session 可删除。", 409),
                replayed: false).ConfigureAwait(false);
        }

        Task<SessionDeleteResult> operation = ScheduleDeleteOperation(
            actor.UserId,
            actor.IsAdmin,
            command,
            digest,
            allowMissingRoot: recovery);
        return await WaitForDeleteHttpBudgetAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionCommandResult> ExecuteLifecycleCommandAsync(
        CurrentActor actor,
        SessionLifecycleCommand command,
        bool open,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        ValidateSessionId(command.SessionId);
        ValidateIdempotencyKey(command.IdempotencyKey);
        string scope = open ? OpenScope : CloseScope;
        string digest = SessionIdempotency.Digest(actor.UserId, scope, command.SessionId, new { sessionId = command.SessionId, browserWidth = open ? command.BrowserWidth : 0 });
        PersistentIdempotencyRecord record = await idempotency.BeginAsync(
            actor.UserId,
            scope,
            command.IdempotencyKey,
            digest,
            resourceId: command.SessionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (record.State == PersistentIdempotencyBeginState.Conflict)
            throw new SessionApplicationException(SessionErrorCodes.IdempotencyKeyReused, "Idempotency-Key 已用于另一规范请求。", 409);
        if (record.State is PersistentIdempotencyBeginState.Succeeded or PersistentIdempotencyBeginState.Failed)
            return ConvertExisting(record);

        SessionView? current = await GetAsync(actor, command.SessionId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            SessionCommandFailure missing = new(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
            if (!await TryCompleteFailureAsync(actor.UserId, scope, command.IdempotencyKey, digest, missing, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(null, 202, Replayed: record.State == PersistentIdempotencyBeginState.InProgress, Pending: true);
            return new SessionCommandResult(null, missing.StatusCode, false, false, missing);
        }

        bool firstExecution = record.State == PersistentIdempotencyBeginState.Started;
        if (firstExecution)
        {
            await TryWriteLifecycleAuditAsync(
                actor.UserId,
                command.SessionId,
                open,
                requested: true,
                succeeded: true,
                current,
                null,
                current.State,
                current.State).ConfigureAwait(false);
        }

        if (record.State == PersistentIdempotencyBeginState.InProgress)
        {
            Task<SessionCommandResult>? existingTask = FindLifecycleTask(command.SessionId, open);
            if (existingTask is not null)
                return await WaitForHttpBudgetAsync(existingTask, command.SessionId, cancellationToken).ConfigureAwait(false);
            if ((open && current.State == SessionState.Running) || (!open && current.State.IsQuiescent()))
            {
                if (!await TryCompleteSuccessAsync(actor.UserId, scope, command.IdempotencyKey, digest, 200, current, command.SessionId).ConfigureAwait(false))
                    return new SessionCommandResult(current, 202, Replayed: true, Pending: true);
                await TryWriteLifecycleAuditAsync(actor.UserId, command.SessionId, open, requested: false, succeeded: true, current, null, current.State, current.State).ConfigureAwait(false);
                return new SessionCommandResult(current, 200, Replayed: true, Pending: false);
            }

            if (CanScheduleLifecycleRecovery(current, open))
            {
                Task<SessionCommandResult> recovered = ScheduleLifecycleOperation(
                    actor.UserId,
                    command,
                    scope,
                    digest,
                    open);
                return await WaitForHttpBudgetAsync(recovered, command.SessionId, cancellationToken).ConfigureAwait(false);
            }

            return new SessionCommandResult(current, 202, Replayed: true, Pending: true);
        }

        if ((open && current.State == SessionState.Running) || (!open && current.State.IsQuiescent()))
        {
            if (!await TryCompleteSuccessAsync(actor.UserId, scope, command.IdempotencyKey, digest, 200, current, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(current, 202, Replayed: false, Pending: true);
            await TryWriteLifecycleAuditAsync(actor.UserId, command.SessionId, open, requested: false, succeeded: true, current, null, current.State, current.State).ConfigureAwait(false);
            return new SessionCommandResult(current, 200, Replayed: false, Pending: false);
        }

        if ((open && current.State == SessionState.Starting) || (!open && current.State == SessionState.Stopping))
        {
            Task<SessionCommandResult>? existingTask = FindLifecycleTask(command.SessionId, open);
            if (existingTask is not null)
                return await WaitForHttpBudgetAsync(existingTask, command.SessionId, cancellationToken).ConfigureAwait(false);
            return new SessionCommandResult(current, 202, Replayed: false, Pending: true);
        }

        SessionCommandFailure? immediateFailure = ValidateLifecycleState(current, open);
        if (immediateFailure is not null)
        {
            if (!await TryCompleteFailureAsync(actor.UserId, scope, command.IdempotencyKey, digest, immediateFailure, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(current, 202, Replayed: false, Pending: true);
            await TryWriteLifecycleAuditAsync(actor.UserId, command.SessionId, open, requested: false, succeeded: false, current, immediateFailure.Code, current.State, current.State).ConfigureAwait(false);
            return new SessionCommandResult(null, immediateFailure.StatusCode, false, false, immediateFailure);
        }

        Task<SessionCommandResult> operation = ScheduleLifecycleOperation(actor.UserId, command, scope, digest, open);
        return await WaitForHttpBudgetAsync(operation, command.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverLifecycleOperationAsync(
        LifecycleRecoveryItem item,
        CancellationToken cancellationToken)
    {
        bool open = item.Scope == OpenScope;
        if (!await IsLifecycleStillAuthorizedAsync(item.ActorUserId, item.SessionId).ConfigureAwait(false))
        {
            await TryCompleteFailureAsync(
                item.ActorUserId,
                item.Scope,
                item.IdempotencyKey,
                item.RequestDigest,
                new SessionCommandFailure(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404),
                item.SessionId).ConfigureAwait(false);
            return;
        }
        SessionView? current = await GetByOwnerAsync(item.ActorUserId, item.SessionId).ConfigureAwait(false);
        if (current is null)
        {
            await TryCompleteFailureAsync(
                item.ActorUserId,
                item.Scope,
                item.IdempotencyKey,
                item.RequestDigest,
                new SessionCommandFailure(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404),
                item.SessionId).ConfigureAwait(false);
            return;
        }

        if ((open && current.State == SessionState.Running) || (!open && current.State.IsQuiescent()))
        {
            await TryCompleteSuccessAsync(
                item.ActorUserId,
                item.Scope,
                item.IdempotencyKey,
                item.RequestDigest,
                200,
                current,
                item.SessionId).ConfigureAwait(false);
            return;
        }

        // A STOPPING session may still be converging through the Worker
        // reconciliation barrier after an API restart. Do not send a second
        // close through a route that has not been reconstructed yet; the next
        // periodic pass will complete the durable command from CLOSED/CRASHED.
        if (!CanScheduleLifecycleRecovery(current, open))
            return;

        Task<SessionCommandResult> operation = ScheduleLifecycleOperation(
            item.ActorUserId,
            new SessionLifecycleCommand(item.SessionId, item.IdempotencyKey),
            item.Scope,
            item.RequestDigest,
            open);
        SessionCommandResult result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Pending)
            throw new InvalidOperationException("A Session lifecycle operation remains pending after recovery.");
    }

    private async Task EnsureNoRecoverableOperationsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        // A periodic pass can overlap a normal request started by this API.
        // Its durable record is intentionally still IN_PROGRESS while the
        // Worker is starting, so it must not make the whole control plane
        // fail readiness. Only records without a live in-process operation
        // are evidence of an orphaned operation after restart.
        string[] pendingCreateIds = await db.SessionCreationOperations.AsNoTracking()
            .Where(row => row.Status != SessionCreationOperationStatus.Committed && row.Status != SessionCreationOperationStatus.Failed)
            .Select(row => row.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasPendingCreate = pendingCreateIds.Any(operationId => !IsCreateOperationActive(operationId));
        var pendingCommands = await db.IdempotencyRecords.AsNoTracking()
            .Where(row => row.Status == IdempotencyRecordStatus.InProgress &&
                (row.Scope == OpenScope || row.Scope == CloseScope || row.Scope == DeleteScope) &&
                row.ResourceId != null)
            .Select(row => new { row.ActorUserId, row.Scope, row.IdempotencyKey, row.ResourceId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasPendingCommand = pendingCommands.Any(row => !IsCommandOperationActive(row.ActorUserId, row.Scope, row.IdempotencyKey, row.ResourceId!));
        if (hasPendingCreate || hasPendingCommand)
            throw new InvalidOperationException("Durable Session recovery is not complete.");
    }

    private bool IsCreateOperationActive(string operationId) =>
        createOperations.TryGetValue(operationId, out Lazy<Task<SessionCommandResult>>? operation) &&
        operation.IsValueCreated && !operation.Value.IsCompleted;

    private bool IsCommandOperationActive(string actorUserId, string scope, string idempotencyKey, string resourceId)
    {
        if (scope == OpenScope || scope == CloseScope)
        {
            bool open = scope == OpenScope;
            Task<SessionCommandResult>? operation = FindLifecycleTask(resourceId, open);
            return operation is not null && !operation.IsCompleted;
        }

        if (scope == DeleteScope)
            return deleteOperations.TryGetValue($"{actorUserId}\u001f{idempotencyKey}", out Lazy<Task<SessionDeleteResult>>? operation) &&
                operation.IsValueCreated && !operation.Value.IsCompleted;

        return false;
    }

    private async Task<SessionCommandResult> ExecuteLifecycleAsync(
        string actorUserId,
        SessionLifecycleCommand command,
        string scope,
        string digest,
        bool open)
    {
        bool coordinatorReturned = false;
        SessionView? before = null;
        try
        {
            before = await TryGetByOwnerAsync(actorUserId, command.SessionId).ConfigureAwait(false);
            if (before is null || !await IsLifecycleStillAuthorizedAsync(actorUserId, command.SessionId).ConfigureAwait(false))
            {
                SessionCommandFailure denied = new(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
                if (!await TryCompleteFailureAsync(actorUserId, scope, command.IdempotencyKey, digest, denied, command.SessionId).ConfigureAwait(false))
                    return new SessionCommandResult(null, 202, false, true);
                return new SessionCommandResult(null, denied.StatusCode, false, false, denied);
            }

            if (open)
            {
                await lifecycle.OpenAsync(command.SessionId, command.BrowserWidth, command.TextMetrics, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                SessionRuntimeCloseResult closeResult = await lifecycle.CloseAsync(command.SessionId, "requested", CancellationToken.None).ConfigureAwait(false);
                if (!closeResult.Completion.Applied)
                    throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerStaleEpoch, "The close operation lost its persisted binding.");
            }
            coordinatorReturned = true;

            SessionView? view = await GetByOwnerAsync(actorUserId, command.SessionId).ConfigureAwait(false);
            if (view is null)
                throw new SessionApplicationException(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
            // The Worker side effect and the idempotency completion are separate
            // durable writes.  If the latter is unavailable, leave IN_PROGRESS
            // for the recovery pass instead of converting an unknown outcome to
            // a terminal failure.
            if (!await TryCompleteSuccessAsync(actorUserId, scope, command.IdempotencyKey, digest, 200, view, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(view, 202, false, true);
            await TryWriteLifecycleAuditAsync(actorUserId, command.SessionId, open, requested: false, succeeded: true, view, null, before.State, view.State).ConfigureAwait(false);
            return new SessionCommandResult(view, 200, false, false);
        }
        catch (SessionRuntimeException exception)
        {
            SessionView? current = await TryGetByOwnerAsync(actorUserId, command.SessionId).ConfigureAwait(false);
            if ((!open && current is not null && current.State.IsQuiescent()) ||
                (open && current is not null && current.State == SessionState.Running))
            {
                if (!await TryCompleteSuccessAsync(actorUserId, scope, command.IdempotencyKey, digest, 200, current, command.SessionId).ConfigureAwait(false))
                    return new SessionCommandResult(current, 202, false, true);
                await TryWriteLifecycleAuditAsync(actorUserId, command.SessionId, open, requested: false, succeeded: true, current, null, before?.State ?? current.State, current.State).ConfigureAwait(false);
                return new SessionCommandResult(current, 200, false, false);
            }

            if (string.Equals(exception.Code, SessionRuntimeResultCodes.WorkerExitUnconfirmed, StringComparison.Ordinal) ||
                string.Equals(exception.Code, SessionRuntimeResultCodes.ControlPlaneReconciliationFailed, StringComparison.Ordinal))
                return new SessionCommandResult(current, 202, false, true);

            SessionCommandFailure failure = MapRuntimeFailure(exception);
            if (!await TryCompleteFailureAsync(actorUserId, scope, command.IdempotencyKey, digest, failure, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(current, 202, false, true);
            await TryWriteLifecycleAuditAsync(actorUserId, command.SessionId, open, requested: false, succeeded: false, current, failure.Code, before?.State ?? current?.State, current?.State).ConfigureAwait(false);
            return new SessionCommandResult(null, failure.StatusCode, false, false, failure);
        }
        catch (SessionApplicationException exception)
        {
            if (coordinatorReturned)
                return new SessionCommandResult(await TryGetByOwnerAsync(actorUserId, command.SessionId).ConfigureAwait(false), 202, false, true);
            SessionCommandFailure failure = new(exception.Code, exception.Message, exception.StatusCode);
            if (!await TryCompleteFailureAsync(actorUserId, scope, command.IdempotencyKey, digest, failure, command.SessionId).ConfigureAwait(false))
                return new SessionCommandResult(null, 202, false, true);
            await TryWriteLifecycleAuditAsync(actorUserId, command.SessionId, open, requested: false, succeeded: false, before, failure.Code, before?.State, before?.State).ConfigureAwait(false);
            return new SessionCommandResult(null, failure.StatusCode, false, false, failure);
        }
        catch (Exception)
        {
            LogLifecycleFailed(logger, command.SessionId, open ? "open" : "close");
            // An unexpected exception can happen after the Worker has changed
            // the durable Session state (for example while reading the view or
            // writing the idempotency completion).  Keep the command pending so
            // recovery can reconcile the state rather than replay a side effect.
            return new SessionCommandResult(await TryGetByOwnerAsync(actorUserId, command.SessionId).ConfigureAwait(false), 202, false, true);
        }
    }

    private async Task<SessionCommandResult> ExecuteCreateAsync(string operationId, string sessionId, string actorUserId)
    {
        try
        {
            await CopyAndPublishAsync(operationId, sessionId, actorUserId).ConfigureAwait(false);
            SessionView view = await GetByOwnerAsync(actorUserId, sessionId).ConfigureAwait(false)
                ?? throw new SessionApplicationException(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
            return new SessionCommandResult(view, 201, false, false);
        }
        catch (Exception exception)
        {
            string code = StableCreateFailureCode(exception);
            LogCreateFailed(logger, sessionId, operationId, code);
            int status = code switch
            {
                SessionErrorCodes.GameHasNoCurrentContent or SessionErrorCodes.GameBlocked => 409,
                SessionErrorCodes.StorageBudgetExceeded => 413,
                SessionErrorCodes.SessionRootInvalid => 503,
                _ => 400,
            };
            string message = code == SessionErrorCodes.SessionRootInvalid
                ? "SessionRoot 校验失败。"
                : "Session 创建失败。";
            SessionCommandFailure failure = new(code, message, status);
            CreateFailureDisposition disposition = await FailCreateAsync(operationId, sessionId, actorUserId, failure).ConfigureAwait(false);
            if (disposition == CreateFailureDisposition.Pending)
                return new SessionCommandResult(await GetAnyOwnedViewAsync(sessionId).ConfigureAwait(false), 202, false, true);
            if (disposition == CreateFailureDisposition.Committed)
                return new SessionCommandResult(await GetAnyOwnedViewAsync(sessionId).ConfigureAwait(false), 201, true, false);
            return new SessionCommandResult(null, status, false, false, failure);
        }
    }

    private Task<SessionCommandResult> ScheduleCreateOperation(string operationId, string sessionId, string actorUserId)
    {
        Lazy<Task<SessionCommandResult>> operation = createOperations.GetOrAdd(
            operationId,
            _ => new Lazy<Task<SessionCommandResult>>(
                () => Task.Run(() => ExecuteCreateAsync(operationId, sessionId, actorUserId), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<SessionCommandResult> task = operation.Value;
        _ = task.ContinueWith(
            _ => createOperations.TryRemove(new KeyValuePair<string, Lazy<Task<SessionCommandResult>>>(operationId, operation)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private async Task<CreatePreparation> PrepareCreateAsync(
        IServiceProvider services,
        CurrentActor actor,
        string gameId,
        string name, int fontSize, int lineHeight,
        string key,
        string digest,
        CancellationToken cancellationToken)
    {
        CloudEmueraDbContext db = services.GetRequiredService<CloudEmueraDbContext>();
        IAuditContext? auditContext = services.GetService<IAuditContext>();
        ValidateGameId(gameId);
        CreatePreparation? existingPreparation = await TryReadExistingCreateAsync(db, actor.UserId, key, digest, cancellationToken).ConfigureAwait(false);
        if (existingPreparation is not null)
            return existingPreparation;

        // The lock must be acquired before BEGIN IMMEDIATE for a new create;
        // Game activation uses the same lock and may need the SQLite writer.
        await using FileStream mutationLock = await AcquireGameMutationLockAsync(gameId, cancellationToken).ConfigureAwait(false);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        existingPreparation = await TryReadExistingCreateAsync(db, actor.UserId, key, digest, cancellationToken).ConfigureAwait(false);
        if (existingPreparation is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existingPreparation;
        }

        int inactiveSessionCount = await db.Sessions.CountAsync(
            row => row.State == SessionState.Creating || row.State == SessionState.Closed || row.State == SessionState.Crashed,
            cancellationToken).ConfigureAwait(false);
        if (inactiveSessionCount >= Capacity.MaxInactiveSessions)
            throw new SessionApplicationException(SessionErrorCodes.InactiveSessionLimitExceeded, "实例未启动 Session 上限已用尽。", 409);

        GameRow? game = await db.Games.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == gameId && row.Status != GameStatus.Deleted &&
                (row.OwnerUserId == actor.UserId || row.Visibility == GameVisibility.ServerShared),
            cancellationToken).ConfigureAwait(false);
        if (game is null)
            throw new SessionApplicationException(SessionErrorCodes.GameNotFound, "游戏不存在。", 404);
        if (game.Status == GameStatus.Blocked)
            throw new SessionApplicationException(SessionErrorCodes.GameBlocked, "游戏当前不可运行。", 409);
        if (game.CurrentContentPath is null || game.ContentDigest is null || game.ContentRevision <= 0)
            throw new SessionApplicationException(SessionErrorCodes.GameHasNoCurrentContent, "游戏没有可运行的 current content。", 409);

        GameFileRow[] files = await db.GameFiles.AsNoTracking()
            .Where(row => row.GameId == game.Id && row.Scope == "CURRENT")
            .OrderBy(row => row.LogicalPath)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (files.Length == 0)
            throw new SessionApplicationException(SessionErrorCodes.GameHasNoCurrentContent, "游戏 current content 清单不可用。", 409);
        RuntimeSaveLayout saveLayout = InspectSourceSaveLayout(game.CurrentContentPath);
        SessionRootPublishedManifest sourceManifest = CreatePublishedManifest(game, files);
        long fileCount = files.LongCount(row => row.EntryKind == "FILE");
        long directoryCount = files.LongCount(row => row.EntryKind == "DIRECTORY");
        long contentBytes = files.Where(row => row.EntryKind == "FILE").Sum(row => row.ByteLength);
        if (fileCount > Capacity.MaxSessionRootFileCount)
            throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "SessionRoot 文件数量超过实例上限。", 413);
        string sessionId = $"sess_{Guid.CreateVersion7():N}";
        string operationId = $"scop_{Guid.CreateVersion7():N}";
        string stagingPath = $"sessions/.staging/{sessionId}-{operationId}";
        string runtimeManifestJson = CreateRuntimeManifest(game, sourceManifest, files, saveLayout);
        if (runtimeManifestJson.Length > PersistenceLimits.SessionRuntimeManifestMaxLength)
            throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "Session runtime manifest 超过存储上限。", 413);
        long runtimeManifestBytes = Encoding.UTF8.GetByteCount(runtimeManifestJson);
        long reservedBytes = checked(contentBytes + 64 * 1024 + fileCount * 512 + directoryCount * 256 + runtimeManifestBytes);
        if (reservedBytes > Capacity.MaxSessionRootBytes)
            throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "SessionRoot 超过实例存储上限。", 413);
        long alreadyReserved = await db.SessionCreationOperations
            .Where(row => row.Status != SessionCreationOperationStatus.Committed && row.Status != SessionCreationOperationStatus.Failed)
            .SumAsync(row => (long?)row.ReservedBytes, cancellationToken).ConfigureAwait(false) ?? 0;
        if (alreadyReserved > Capacity.MaxSessionRootBytes - reservedBytes)
            throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "实例 SessionRoot 创建预留已达到存储上限。", 413);
        long globalReserved = await db.SessionCreationOperations
            .Where(row => row.Status != SessionCreationOperationStatus.Committed && row.Status != SessionCreationOperationStatus.Failed)
            .SumAsync(row => (long?)row.ReservedBytes, cancellationToken).ConfigureAwait(false) ?? 0;
        EnsureDataRootFreeSpace(checked(globalReserved + reservedBytes));
        var session = new SessionRow
        {
            Id = sessionId,
            OwnerUserId = actor.UserId,
            GameId = game.Id,
            SourceContentDigest = game.ContentDigest,
            SourceContentRevision = game.ContentRevision,
            SessionRootManifestDigest = sourceManifest.ManifestDigest,
            SaveLayout = (int)saveLayout,
            RuntimeVersion = RuntimeBaseline.CloudEmueraIntegrationVersion,
            SessionRootPath = $"sessions/{sessionId}/root",
            Name = name,
            FontSize = fontSize,
            LineHeight = lineHeight,
            State = SessionState.Creating,
            StateVersion = 0,
            WorkerEpoch = 0,
            WaitingForInput = false,
            LastOutputSequence = 0,
            CreatedAt = now,
            LastActivityAt = now,
        };
        var operation = new SessionCreationOperationRow
        {
            Id = operationId,
            SessionId = sessionId,
            ActorUserId = actor.UserId,
            Status = SessionCreationOperationStatus.Prepared,
            StagingPath = stagingPath,
            ReservedBytes = reservedBytes,
            ExpectedFileCount = fileCount,
            ExpectedContentBytes = contentBytes,
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            StateVersion = 0,
        };
        db.Sessions.Add(session);
        db.SessionCreationOperations.Add(operation);
        db.IdempotencyRecords.Add(new IdempotencyRecordRow
        {
            ActorUserId = actor.UserId,
            Scope = CreateScope,
            IdempotencyKey = key,
            RequestDigest = digest,
            Status = IdempotencyRecordStatus.InProgress,
            ResponseStatus = 202,
            ResponseJson = "null",
            ResourceId = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
        });
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = now,
            ActorUserId = actor.UserId,
            ActorType = actor.IsAdmin ? AuditActorType.Admin : AuditActorType.User,
            Action = AuditActions.SessionCreateRequested,
            ResourceType = "SESSION",
            ResourceId = sessionId,
            Result = AuditResult.Succeeded,
            RequestId = auditContext?.RequestId,
            MetadataJson = JsonSerializer.Serialize(new { gameId = game.Id, sourceContentRevision = game.ContentRevision, reservedBytes }),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreatePreparation(null, null, operationId, sessionId);
    }

    private static async Task<CreatePreparation?> TryReadExistingCreateAsync(
        CloudEmueraDbContext db,
        string actorUserId,
        string key,
        string digest,
        CancellationToken cancellationToken)
    {
        IdempotencyRecordRow? existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            row => row.ActorUserId == actorUserId && row.Scope == CreateScope && row.IdempotencyKey == key,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return null;
        if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
            throw new SessionApplicationException(SessionErrorCodes.IdempotencyKeyReused, "Idempotency-Key 已用于另一规范请求。", 409);

        SessionView? existingView = existing.ResourceId is null
            ? null
            : await Project(db.Sessions.AsNoTracking().Where(row => row.Id == existing.ResourceId && row.OwnerUserId == actorUserId))
                .Select(ToViewExpression())
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        PersistentIdempotencyBeginState state = existing.Status switch
        {
            IdempotencyRecordStatus.Succeeded => PersistentIdempotencyBeginState.Succeeded,
            IdempotencyRecordStatus.Failed => PersistentIdempotencyBeginState.Failed,
            _ => PersistentIdempotencyBeginState.InProgress,
        };
        return new CreatePreparation(
            new PersistentIdempotencyRecord(state, existing.RequestDigest, existing.ResponseStatus, existing.ResponseJson, existing.ResourceId, existing.ErrorCode),
            existingView);
    }

    private async Task CopyAndPublishAsync(string operationId, string sessionId, string actorUserId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        SessionCreationOperationRow operation = await db.SessionCreationOperations.SingleAsync(row => row.Id == operationId && row.SessionId == sessionId);
        SessionRow session = await db.Sessions.SingleAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId);
        if (operation.Status == SessionCreationOperationStatus.Committed)
            return;
        if (operation.Status == SessionCreationOperationStatus.Failed)
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session 创建操作已失败。", 503);
        if (operation.Status == SessionCreationOperationStatus.RootPublished)
        {
            await CommitCreateAsync(operationId, sessionId, actorUserId).ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.SessionCreationOperations
            .Where(row => row.Id == operationId && row.Status == SessionCreationOperationStatus.Prepared)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, SessionCreationOperationStatus.Copying)
                .SetProperty(row => row.AttemptCount, row => row.AttemptCount + 1)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1))
            .ConfigureAwait(false);
        if (changed == 0)
        {
            operation = await db.SessionCreationOperations.SingleAsync(row => row.Id == operationId);
            if (operation.Status == SessionCreationOperationStatus.Committed) return;
        }

        string stagingContainer = ResolveDataPath(operation.StagingPath);
        string finalContainer = SessionRootProtectedMarkerStore.ContainerPath(databaseOptions, sessionId);
        ValidateStagingContainer(stagingContainer, operation.StagingPath);
        string sessionsDirectory = Path.Combine(databaseOptions.DataRoot, "sessions");
        string stagingDirectory = Path.Combine(sessionsDirectory, ".staging");
        Directory.CreateDirectory(stagingDirectory);
        SetPrivateDirectoryMode(sessionsDirectory);
        SetPrivateDirectoryMode(stagingDirectory);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(stagingContainer, "session-staging");

        IGameContentCopyLeaseStore copyLeases = scope.ServiceProvider.GetRequiredService<IGameContentCopyLeaseStore>();
        await using GameContentCopyLease lease = await copyLeases.AcquireAsync(
            session.GameId,
            session.SourceContentRevision,
            session.SourceContentDigest,
            "SESSION_CREATE",
            session.Id,
            CancellationToken.None).ConfigureAwait(false);
        try
        {
            GameRow game = await db.Games.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == session.GameId && row.Status == GameStatus.Active &&
                    row.ContentRevision == session.SourceContentRevision && row.ContentDigest == session.SourceContentDigest)
                .ConfigureAwait(false)
                ?? throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session source content 已发生变化。", 503);
            GameFileRow[] files = await db.GameFiles.AsNoTracking()
                .Where(row => row.GameId == session.GameId && row.Scope == "CURRENT")
                .OrderBy(row => row.LogicalPath)
                .ToArrayAsync()
                .ConfigureAwait(false);
            if (files.Length == 0)
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session source content 清单不可用。", 503);
            if (files.LongCount(row => row.EntryKind == "FILE") > Capacity.MaxSessionRootFileCount)
                throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "SessionRoot 文件数量超过实例上限。", 413);
            SessionRootPublishedManifest manifest = CreatePublishedManifest(game, files);
            if (!string.Equals(manifest.ManifestDigest, session.SessionRootManifestDigest, StringComparison.OrdinalIgnoreCase))
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session source manifest 已发生变化。", 503);
            if (!Enum.IsDefined((RuntimeSaveLayout)session.SaveLayout))
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session save layout 无效。", 503);
            RuntimeSaveLayout saveLayout = (RuntimeSaveLayout)session.SaveLayout;
            if (InspectSourceSaveLayoutAtRoot(lease.ContentRootPath) != saveLayout)
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session source save layout 已发生变化。", 503);
            string runtimeManifestJson = CreateRuntimeManifest(game, manifest, files, saveLayout);
            if (runtimeManifestJson.Length > PersistenceLimits.SessionRuntimeManifestMaxLength)
                throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "Session runtime manifest 超过存储上限。", 413);
            if (Directory.Exists(finalContainer) || File.Exists(finalContainer) || RuntimePathUtilities.IsReparsePoint(finalContainer))
            {
                if (!IsPublishedRootValid(sessionId))
                    throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot final container 已被占用。", 503);
                await MarkRootPublishedAsync(operationId, sessionId).ConfigureAwait(false);
                await CommitCreateAsync(operationId, sessionId, actorUserId).ConfigureAwait(false);
                return;
            }
            if (Directory.Exists(stagingContainer) || File.Exists(stagingContainer) || RuntimePathUtilities.IsReparsePoint(stagingContainer))
                SafeDeleteOwnedStaging(stagingContainer, operation.StagingPath);
            Directory.CreateDirectory(stagingContainer);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(stagingContainer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SessionRootCopyLimits limits = new(
                maxFileCount: Capacity.MaxSessionRootFileCount,
                maxDirectoryCount: Capacity.MaxSessionRootFileCount,
                maxTotalBytes: Capacity.MaxSessionRootBytes,
                maxSingleFileBytes: Capacity.MaxSessionRootBytes);
            SessionRootLayout layout = new SessionRootLayoutBuilder(lease.ContentRootPath, stagingContainer, saveLayout)
                .WithPublishedManifest(manifest)
                .WithCopyLimits(limits)
                .Build();
            SetPrivateDirectoryMode(stagingContainer);
            SetPrivateDirectoryMode(layout.SessionRoot);
            SessionRootProtectedMarkerStore.Write(
                databaseOptions,
                stagingContainer,
                session.Id,
                session.OwnerUserId,
                session.GameId,
                session.SourceContentRevision,
                session.SourceContentDigest,
                manifest.ManifestDigest,
                layout.CopiedManifestDigest,
                saveLayout,
                session.RuntimeVersion,
                session.CreatedAt,
                layout.SessionRoot);
            SessionRootProtectedMarkerStore.WriteRuntimeManifest(stagingContainer, runtimeManifestJson);
            SyncDirectoryTree(stagingContainer);
            if (Directory.Exists(finalContainer) || File.Exists(finalContainer) || RuntimePathUtilities.IsReparsePoint(finalContainer))
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot final container 已被占用。", 503);
            Directory.CreateDirectory(sessionsDirectory);
            SetPrivateDirectoryMode(sessionsDirectory);
            Directory.Move(stagingContainer, finalContainer);
            SyncDirectoryTree(Path.GetDirectoryName(finalContainer)!);
            await MarkRootPublishedAsync(operationId, sessionId).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        await CommitCreateAsync(operationId, sessionId, actorUserId).ConfigureAwait(false);
    }

    private async Task CommitCreateAsync(string operationId, string sessionId, string actorUserId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        IAuditContext? auditContext = scope.ServiceProvider.GetService<IAuditContext>();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db).ConfigureAwait(false);
        SessionCreationOperationRow operation = await db.SessionCreationOperations.SingleAsync(row => row.Id == operationId && row.SessionId == sessionId && row.ActorUserId == actorUserId);
        SessionRow session = await db.Sessions.SingleAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId);
        if (operation.Status == SessionCreationOperationStatus.Committed)
        {
            await CompleteCommittedCreateAsync(db, transaction, operation, session, actorUserId).ConfigureAwait(false);
            return;
        }
        if (operation.Status != SessionCreationOperationStatus.RootPublished)
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 尚未发布。", 503);
        SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(databaseOptions, sessionId);
        string finalRoot = ResolveDataPath(session.SessionRootPath);
        if (!Directory.Exists(finalRoot) ||
            !SessionRootProtectedMarkerStore.SameRootIdentity(marker, finalRoot) ||
            !string.Equals(marker.SessionId, session.Id, StringComparison.Ordinal) ||
            !string.Equals(marker.OwnerUserId, session.OwnerUserId, StringComparison.Ordinal) ||
            !string.Equals(marker.GameId, session.GameId, StringComparison.Ordinal) ||
            marker.SourceContentRevision != session.SourceContentRevision ||
            !string.Equals(marker.SourceContentDigest, session.SourceContentDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.RuntimeVersion, session.RuntimeVersion, StringComparison.Ordinal))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot identity 校验失败。", 503);
        DateTimeOffset now = timeProvider.GetUtcNow();
        session.State = SessionState.Closed;
        session.StateVersion = checked(session.StateVersion + 1);
        session.ClosedAt = now;
        session.LastActivityAt = now;
        operation.Status = SessionCreationOperationStatus.Committed;
        operation.UpdatedAt = now;
        operation.CompletedAt = now;
        operation.StateVersion = checked(operation.StateVersion + 1);
        GameRow game = await db.Games.AsNoTracking().SingleAsync(row => row.Id == session.GameId);
        SessionView view = ToView(new SessionProjection(
            session.Id,
            session.Name,
            session.GameId,
            game.Name,
            session.SourceContentDigest,
            session.SourceContentRevision,
            session.RuntimeVersion,
            session.FontSize,
            session.LineHeight,
            session.State,
            session.StateVersion,
            session.WorkerEpoch,
            session.WaitingForInput,
            session.CreatedAt,
            session.StartedAt,
            session.LastActivityAt,
            session.ClosedAt,
            session.CloseReason));
        string responseJson = JsonSerializer.Serialize(view, JsonOptions);
        await db.IdempotencyRecords
            .Where(row => row.ActorUserId == actorUserId && row.Scope == CreateScope && row.ResourceId == sessionId && row.Status == IdempotencyRecordStatus.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, IdempotencyRecordStatus.Succeeded)
                .SetProperty(row => row.ResponseStatus, 201)
                .SetProperty(row => row.ResponseJson, responseJson)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.CompletedAt, now), CancellationToken.None)
            .ConfigureAwait(false);
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = now,
            ActorUserId = actorUserId,
            ActorType = AuditActorType.User,
            Action = AuditActions.SessionCreated,
            ResourceType = "SESSION",
            ResourceId = sessionId,
            Result = AuditResult.Succeeded,
            RequestId = auditContext?.RequestId,
            MetadataJson = JsonSerializer.Serialize(new { gameId = session.GameId, sourceContentRevision = session.SourceContentRevision }),
        });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CompleteCommittedCreateAsync(
        CloudEmueraDbContext db,
        SqliteImmediateTransaction transaction,
        SessionCreationOperationRow operation,
        SessionRow session,
        string actorUserId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        GameRow game = await db.Games.AsNoTracking().SingleAsync(row => row.Id == session.GameId).ConfigureAwait(false);
        SessionView view = ToView(new SessionProjection(
            session.Id,
            session.Name,
            session.GameId,
            game.Name,
            session.SourceContentDigest,
            session.SourceContentRevision,
            session.RuntimeVersion,
            session.FontSize,
            session.LineHeight,
            session.State,
            session.StateVersion,
            session.WorkerEpoch,
            session.WaitingForInput,
            session.CreatedAt,
            session.StartedAt,
            session.LastActivityAt,
            session.ClosedAt,
            session.CloseReason));
        string responseJson = JsonSerializer.Serialize(view, JsonOptions);
        await db.IdempotencyRecords
            .Where(row => row.ActorUserId == actorUserId && row.Scope == CreateScope &&
                row.ResourceId == session.Id && row.Status == IdempotencyRecordStatus.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, IdempotencyRecordStatus.Succeeded)
                .SetProperty(row => row.ResponseStatus, 201)
                .SetProperty(row => row.ResponseJson, responseJson)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.CompletedAt, now), CancellationToken.None)
            .ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MarkRootPublishedAsync(string operationId, string sessionId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        DateTimeOffset now = timeProvider.GetUtcNow();
        int changed = await db.SessionCreationOperations
            .Where(row => row.Id == operationId && row.SessionId == sessionId &&
                (row.Status == SessionCreationOperationStatus.Prepared || row.Status == SessionCreationOperationStatus.Copying))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, SessionCreationOperationStatus.RootPublished)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.StateVersion, row => row.StateVersion + 1), CancellationToken.None)
            .ConfigureAwait(false);
        if (changed != 1)
        {
            SessionCreationOperationStatus status = await db.SessionCreationOperations
                .Where(row => row.Id == operationId && row.SessionId == sessionId)
                .Select(row => row.Status)
                .SingleOrDefaultAsync()
                .ConfigureAwait(false);
            if (status is not (SessionCreationOperationStatus.RootPublished or SessionCreationOperationStatus.Committed))
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session 创建操作状态发生变化。", 503);
        }
    }

    private async Task<CreateFailureDisposition> FailCreateAsync(string operationId, string sessionId, string actorUserId, SessionCommandFailure failure)
    {
        string? stagingPath = null;
        bool finalPublished = false;
        bool finalContainerPresent = false;
        bool cleanupProven = false;
        try
        {
            await using AsyncServiceScope readScope = scopeFactory.CreateAsyncScope();
            CloudEmueraDbContext readDb = readScope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
            SessionCreationOperationRow? operation = await readDb.SessionCreationOperations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == operationId && row.SessionId == sessionId);
            SessionRow? session = await readDb.Sessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId);
            stagingPath = operation?.StagingPath;
            finalContainerPresent = session is not null && IsFinalContainerPresent(sessionId);
            finalPublished = session is not null && IsPublishedRootValid(sessionId);
            if (finalContainerPresent && !finalPublished)
            {
                failure = new SessionCommandFailure(SessionErrorCodes.SessionRootInvalid, "Session 创建现场无法安全确认。", 503);
            }
            else if (!finalPublished && stagingPath is not null)
                SafeDeleteOwnedStaging(ResolveDataPath(stagingPath), stagingPath);
            cleanupProven = !finalPublished && !finalContainerPresent;
        }
        catch (Exception)
        {
            LogCreateCleanupFailed(logger, sessionId, operationId);
            failure = new SessionCommandFailure(SessionErrorCodes.SessionRootInvalid, "Session 创建现场无法安全清理。", 503);
        }

        if (finalPublished)
        {
            try
            {
                await MarkRootPublishedAsync(operationId, sessionId).ConfigureAwait(false);
                await CommitCreateAsync(operationId, sessionId, actorUserId).ConfigureAwait(false);
                return CreateFailureDisposition.Committed;
            }
            catch (Exception)
            {
                LogCreateRecoveryCommitFailed(logger, sessionId, operationId);
                // The final container is valid, but the durable commit outcome
                // is unknown.  Never turn this into FAILED: that would make the
                // recovery scanner skip a valid root permanently.
                return CreateFailureDisposition.Pending;
            }
        }

        // If the final container exists but cannot be proven safe, or the
        // staging tree could not be removed with ownership checks, the result
        // is unknown.  Keep the durable operation recoverable; a terminal
        // FAILED row would make the recovery scanner skip the filesystem
        // evidence permanently.
        if (!cleanupProven)
            return CreateFailureDisposition.Pending;

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        IAuditContext? auditContext = scope.ServiceProvider.GetService<IAuditContext>();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db).ConfigureAwait(false);
        SessionCreationOperationRow? operationRow = await db.SessionCreationOperations.SingleOrDefaultAsync(row => row.Id == operationId && row.SessionId == sessionId);
        SessionRow? sessionRow = await db.Sessions.SingleOrDefaultAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (operationRow?.Status == SessionCreationOperationStatus.Committed)
            return CreateFailureDisposition.Committed;
        if (operationRow?.Status == SessionCreationOperationStatus.RootPublished)
            return CreateFailureDisposition.Pending;
        if (operationRow is not null && operationRow.Status is not (SessionCreationOperationStatus.Prepared or SessionCreationOperationStatus.Copying))
            return CreateFailureDisposition.Pending;
        if (operationRow is not null)
        {
            operationRow.Status = SessionCreationOperationStatus.Failed;
            operationRow.LastErrorCode = failure.Code;
            operationRow.UpdatedAt = now;
            operationRow.CompletedAt = now;
            operationRow.StateVersion = checked(operationRow.StateVersion + 1);
        }
        await db.IdempotencyRecords
            .Where(row => row.ActorUserId == actorUserId && row.Scope == CreateScope && row.ResourceId == sessionId && row.Status == IdempotencyRecordStatus.InProgress)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, IdempotencyRecordStatus.Failed)
                .SetProperty(row => row.ResponseStatus, failure.StatusCode)
                .SetProperty(row => row.ResponseJson, JsonSerializer.Serialize(failure, JsonOptions))
                .SetProperty(row => row.ErrorCode, failure.Code)
                .SetProperty(row => row.UpdatedAt, now)
                .SetProperty(row => row.CompletedAt, now), CancellationToken.None)
            .ConfigureAwait(false);
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = now,
            ActorUserId = actorUserId,
            ActorType = AuditActorType.User,
            Action = AuditActions.SessionCreateFailed,
            ResourceType = "SESSION",
            ResourceId = sessionId,
            Result = AuditResult.Failed,
            RequestId = auditContext?.RequestId,
            ReasonCode = failure.Code,
            MetadataJson = "{}",
        });
        // A failed pre-publication Session is only a cleanup anchor.  Once the
        // owned staging tree is gone it is removed in the same short commit.
        if (sessionRow is not null && !finalPublished && cleanupProven && operationRow is not null)
        {
            db.SessionCreationOperations.Remove(operationRow);
            db.Sessions.Remove(sessionRow);
        }
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return CreateFailureDisposition.Failed;
    }

    private async Task<SessionCommandResult> WaitForHttpBudgetAsync(
        Task<SessionCommandResult> operation,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(HttpWaitBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SessionView? current = sessionId is null ? null : await GetAnyOwnedViewAsync(sessionId).ConfigureAwait(false);
            return new SessionCommandResult(current, 202, false, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SessionView? current = sessionId is null ? null : await GetAnyOwnedViewAsync(sessionId).ConfigureAwait(false);
            return new SessionCommandResult(current, 202, false, true);
        }
    }

    private Task<SessionCommandResult>? FindLifecycleTask(string sessionId, bool open)
    {
        ConcurrentDictionary<string, Lazy<Task<SessionCommandResult>>> operations = open ? openOperations : closeOperations;
        return operations.TryGetValue(sessionId, out Lazy<Task<SessionCommandResult>>? operation) ? operation.Value : null;
    }

    private Task<SessionCommandResult> ScheduleLifecycleOperation(
        string actorUserId,
        SessionLifecycleCommand command,
        string scope,
        string digest,
        bool open)
    {
        ConcurrentDictionary<string, Lazy<Task<SessionCommandResult>>> operations = open ? openOperations : closeOperations;
        Lazy<Task<SessionCommandResult>> operation = operations.GetOrAdd(
            command.SessionId,
            _ => new Lazy<Task<SessionCommandResult>>(
                () => Task.Run(() => ExecuteLifecycleAsync(actorUserId, command, scope, digest, open), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<SessionCommandResult> task = operation.Value;
        _ = task.ContinueWith(
            _ => operations.TryRemove(new KeyValuePair<string, Lazy<Task<SessionCommandResult>>>(command.SessionId, operation)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private Task<SessionDeleteResult> ScheduleDeleteOperation(
        string actorUserId,
        bool actorIsAdmin,
        SessionDeleteCommand command,
        string digest,
        bool allowMissingRoot)
    {
        string operationKey = $"{actorUserId}\u001f{command.IdempotencyKey}";
        Lazy<Task<SessionDeleteResult>> operation = deleteOperations.GetOrAdd(
            operationKey,
            _ => new Lazy<Task<SessionDeleteResult>>(
                () => Task.Run(() => ExecuteDeleteAsync(actorUserId, actorIsAdmin, command, digest, allowMissingRoot), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<SessionDeleteResult> task = operation.Value;
        _ = task.ContinueWith(
            _ => deleteOperations.TryRemove(new KeyValuePair<string, Lazy<Task<SessionDeleteResult>>>(operationKey, operation)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static async Task<SessionDeleteResult> WaitForDeleteHttpBudgetAsync(
        Task<SessionDeleteResult> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(HttpWaitBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new SessionDeleteResult(202, Replayed: false, Pending: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SessionDeleteResult(202, Replayed: false, Pending: true);
        }
    }

    private async Task<SessionDeleteResult> ExecuteDeleteAsync(
        string actorUserId,
        bool actorIsAdmin,
        SessionDeleteCommand command,
        string digest,
        bool allowMissingRoot)
    {
        try
        {
            if (commandGate is not null)
            {
                await using SessionCommandLease lease = await commandGate.EnterAsync(command.SessionId, CancellationToken.None).ConfigureAwait(false);
                return await ExecuteDeleteUnderGateAsync(actorUserId, actorIsAdmin, command, digest, allowMissingRoot).ConfigureAwait(false);
            }

            return await ExecuteDeleteUnderGateAsync(actorUserId, actorIsAdmin, command, digest, allowMissingRoot).ConfigureAwait(false);
        }
        catch (SessionApplicationException exception)
        {
            SessionCommandFailure failure = new(exception.Code, exception.Message, exception.StatusCode);
            if (!await TryCompleteFailureAsync(actorUserId, DeleteScope, command.IdempotencyKey, digest, failure, command.SessionId).ConfigureAwait(false))
                return new SessionDeleteResult(202, Replayed: false, Pending: true, failure);
            return new SessionDeleteResult(failure.StatusCode, Replayed: false, Pending: false, failure);
        }
        catch (Exception)
        {
            LogDeleteFailed(logger, command.SessionId);
            // Filesystem and database failures leave the durable command in
            // progress so a later recovery pass can retry without guessing
            // whether the root was fully removed.
            return new SessionDeleteResult(202, Replayed: false, Pending: true);
        }
    }

    private async Task<SessionDeleteResult> ExecuteDeleteUnderGateAsync(
        string actorUserId,
        bool actorIsAdmin,
        SessionDeleteCommand command,
        string digest,
        bool allowMissingRoot)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        IAuditContext? auditContext = scope.ServiceProvider.GetService<IAuditContext>();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(
            row => row.Id == command.SessionId && row.OwnerUserId == actorUserId).ConfigureAwait(false);
        if (session is null)
        {
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!await TryCompleteDeleteSuccessAsync(actorUserId, command.IdempotencyKey, digest, command.SessionId).ConfigureAwait(false))
                return new SessionDeleteResult(202, Replayed: false, Pending: true);
            return new SessionDeleteResult(204, Replayed: false, Pending: false);
        }

        await EnsureDeletePreconditionsAsync(db, session).ConfigureAwait(false);
        DeleteSessionContainer(session, allowMissingRoot);

        SessionCreationOperationRow? creation = await db.SessionCreationOperations.SingleOrDefaultAsync(
            row => row.SessionId == session.Id).ConfigureAwait(false);
        await db.SaveFileOperations
            .Where(row => row.SessionId == session.Id &&
                (row.Status == SaveFileOperationStatus.Committed || row.Status == SaveFileOperationStatus.Failed))
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
        if (creation is not null)
            db.SessionCreationOperations.Remove(creation);
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = actorUserId,
            ActorType = actorIsAdmin ? AuditActorType.Admin : AuditActorType.User,
            Action = AuditActions.SessionDeleted,
            ResourceType = "SESSION",
            ResourceId = session.Id,
            RequestId = auditContext?.RequestId,
            Result = AuditResult.Succeeded,
            MetadataJson = JsonSerializer.Serialize(new { gameId = session.GameId, previousState = session.State.ToString().ToUpperInvariant() }),
        });
        db.Sessions.Remove(session);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

        if (!await TryCompleteDeleteSuccessAsync(actorUserId, command.IdempotencyKey, digest, command.SessionId).ConfigureAwait(false))
            return new SessionDeleteResult(202, Replayed: false, Pending: true);
        return new SessionDeleteResult(204, Replayed: false, Pending: false);
    }

    private static async Task EnsureDeletePreconditionsAsync(CloudEmueraDbContext db, SessionRow session)
    {
        if (!session.State.IsQuiescent())
            throw new SessionApplicationException(SessionErrorCodes.SessionNotDeletable, "仅 CLOSED 或 CRASHED Session 可删除。", 409);
        if (await db.WorkerLeases.AnyAsync(row => row.SessionId == session.Id).ConfigureAwait(false))
            throw new SessionApplicationException(SessionErrorCodes.SessionNotDeletable, "Session 仍有 Worker 租约。", 409);
        if (await db.SessionRootMutationLeases.AnyAsync(row => row.SessionId == session.Id).ConfigureAwait(false))
            throw new SessionApplicationException(SessionErrorCodes.MutationInProgress, "Session 当前有文件操作正在执行。", 409);
        if (await db.SaveFileOperations.AnyAsync(row => row.SessionId == session.Id &&
                row.Status != SaveFileOperationStatus.Committed && row.Status != SaveFileOperationStatus.Failed).ConfigureAwait(false))
            throw new SessionApplicationException(SessionErrorCodes.MutationInProgress, "Session 当前有文件操作正在执行。", 409);

        SessionCreationOperationRow? creation = await db.SessionCreationOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SessionId == session.Id).ConfigureAwait(false);
        if (creation is not null && creation.Status != SessionCreationOperationStatus.Committed)
            throw new SessionApplicationException(SessionErrorCodes.SessionNotReady, "Session 创建操作尚未完成。", 409);
    }

    private static bool CanScheduleLifecycleRecovery(SessionView view, bool open) => open
        ? view.State is SessionState.Closed or SessionState.Crashed
        : view.State is SessionState.Starting or SessionState.Running;

    private async Task<SessionView?> GetByOwnerAsync(string actorUserId, string sessionId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        return await Project(db.Sessions.AsNoTracking().Where(row => row.Id == sessionId && row.OwnerUserId == actorUserId))
            .Select(ToViewExpression()).SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task<SessionView?> TryGetByOwnerAsync(string actorUserId, string sessionId)
    {
        try
        {
            return await GetByOwnerAsync(actorUserId, sessionId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogLifecycleReadFailed(logger, sessionId);
            return null;
        }
    }

    private async Task<bool> IsLifecycleStillAuthorizedAsync(string actorUserId, string sessionId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        var actor = await db.Users.AsNoTracking()
            .Where(row => row.Id == actorUserId && row.Status == UserStatus.Active)
            .Select(row => new { row.Id, row.Role })
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);
        if (actor is null || !await db.Sessions.AsNoTracking().AnyAsync(row => row.Id == sessionId && row.OwnerUserId == actorUserId).ConfigureAwait(false))
            return false;

        IResourceAuthorizer? authorizer = scope.ServiceProvider.GetService<IResourceAuthorizer>();
        if (authorizer is null)
            return true;
        ResourceAccessDecision decision = await authorizer.AuthorizeAsync(
            new CurrentActor(actor.Id, actor.Role == UserRole.Admin ? "ADMIN" : "PLAYER", "recovery"),
            ResourceKind.Session,
            sessionId,
            ResourceAction.SessionControl,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return decision == ResourceAccessDecision.Allowed;
    }

    private async Task<SessionView?> GetAnyOwnedViewAsync(string sessionId)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
        return await Project(db.Sessions.AsNoTracking().Where(row => row.Id == sessionId)).Select(ToViewExpression()).SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private static IQueryable<SessionProjection> Project(IQueryable<SessionRow> query) => query.Select(row => new SessionProjection(
        row.Id,
        row.Name,
        row.GameId,
        row.Game == null ? string.Empty : row.Game.Name,
        row.SourceContentDigest,
        row.SourceContentRevision,
        row.RuntimeVersion,
        row.FontSize,
        row.LineHeight,
        row.State,
        row.StateVersion,
        row.WorkerEpoch,
        row.WaitingForInput,
        row.CreatedAt,
        row.StartedAt,
        row.LastActivityAt,
        row.ClosedAt,
        row.CloseReason));

    private static System.Linq.Expressions.Expression<Func<SessionProjection, SessionView>> ToViewExpression() => row => new SessionView(
        1,
        row.Id,
        row.Name,
        new SessionGameSummary(row.GameId, row.GameName),
        row.SourceContentDigest,
        row.SourceContentRevision,
        row.RuntimeVersion,
        row.FontSize,
        row.LineHeight,
        row.State,
        row.StateVersion,
        row.WorkerEpoch,
        row.WaitingForInput,
        row.CreatedAt,
        row.StartedAt,
        row.LastActivityAt,
        row.ClosedAt,
        row.CloseReason);

    private static SessionView ToView(SessionProjection row) => new(
        1, row.Id, row.Name, new SessionGameSummary(row.GameId, row.GameName), row.SourceContentDigest,
        row.SourceContentRevision, row.RuntimeVersion, row.FontSize, row.LineHeight, row.State, row.StateVersion, row.WorkerEpoch,
        row.WaitingForInput, row.CreatedAt, row.StartedAt, row.LastActivityAt, row.ClosedAt, row.CloseReason);

    private static SessionCommandResult ConvertExisting(CreatePreparation preparation)
    {
        if (preparation.Existing is null) throw new InvalidOperationException("The create preparation has no existing record.");
        return ConvertExisting(preparation.Existing, preparation.ExistingView);
    }

    private static SessionCommandResult ConvertExisting(PersistentIdempotencyRecord record)
    {
        SessionView? value = record.State == PersistentIdempotencyBeginState.Succeeded ? Deserialize<SessionView>(record.ResponseJson) : null;
        SessionCommandFailure? failure = record.State == PersistentIdempotencyBeginState.Failed ? Deserialize<SessionCommandFailure>(record.ResponseJson) : null;
        return new SessionCommandResult(value, record.ResponseStatus, true, record.State == PersistentIdempotencyBeginState.InProgress, failure);
    }

    private static SessionDeleteResult ConvertExistingDelete(PersistentIdempotencyRecord record)
    {
        SessionCommandFailure? failure = record.State == PersistentIdempotencyBeginState.Failed
            ? Deserialize<SessionCommandFailure>(record.ResponseJson)
            : null;
        return new SessionDeleteResult(record.ResponseStatus, Replayed: true, Pending: record.State == PersistentIdempotencyBeginState.InProgress, failure);
    }

    private static SessionCommandResult ConvertExisting(PersistentIdempotencyRecord record, SessionView? pendingView)
    {
        if (record.State == PersistentIdempotencyBeginState.InProgress)
            return new SessionCommandResult(pendingView, 202, true, true);
        return ConvertExisting(record);
    }

    private async Task CompleteSuccessAsync(string actorUserId, string scope, string key, string digest, int status, SessionView view, string resourceId) =>
        await idempotency.CompleteSuccessAsync(actorUserId, scope, key, digest, status, JsonSerializer.Serialize(view, JsonOptions), resourceId, CancellationToken.None).ConfigureAwait(false);

    private async Task CompleteFailureAsync(
        string actorUserId,
        string scope,
        string key,
        string digest,
        SessionCommandFailure failure,
        string? resourceId = null) =>
        await idempotency.CompleteFailureAsync(
            actorUserId,
            scope,
            key,
            digest,
            failure.StatusCode,
            failure.Code,
            JsonSerializer.Serialize(failure, JsonOptions),
            resourceId,
            CancellationToken.None).ConfigureAwait(false);

    private Task CompleteDeleteSuccessAsync(string actorUserId, string key, string digest, string resourceId) =>
        idempotency.CompleteSuccessAsync(
            actorUserId,
            DeleteScope,
            key,
            digest,
            204,
            "{\"deleted\":true}",
            resourceId,
            CancellationToken.None);

    private async Task<bool> TryCompleteDeleteSuccessAsync(string actorUserId, string key, string digest, string resourceId)
    {
        try
        {
            await CompleteDeleteSuccessAsync(actorUserId, key, digest, resourceId).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            LogLifecycleCompletionFailed(logger, resourceId, "delete-success");
            return false;
        }
    }

    private async Task<SessionDeleteResult> CompleteDeleteFailureResultAsync(
        string actorUserId,
        string key,
        string digest,
        string resourceId,
        SessionCommandFailure failure,
        bool replayed)
    {
        if (!await TryCompleteFailureAsync(actorUserId, DeleteScope, key, digest, failure, resourceId).ConfigureAwait(false))
            return new SessionDeleteResult(202, replayed, Pending: true, failure);
        return new SessionDeleteResult(failure.StatusCode, replayed, Pending: false, failure);
    }

    private async Task<bool> TryCompleteSuccessAsync(
        string actorUserId,
        string scope,
        string key,
        string digest,
        int status,
        SessionView view,
        string resourceId)
    {
        try
        {
            await CompleteSuccessAsync(actorUserId, scope, key, digest, status, view, resourceId).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            LogLifecycleCompletionFailed(logger, resourceId, "success");
            return false;
        }
    }

    private async Task<bool> TryCompleteFailureAsync(
        string actorUserId,
        string scope,
        string key,
        string digest,
        SessionCommandFailure failure,
        string? resourceId = null)
    {
        try
        {
            await CompleteFailureAsync(actorUserId, scope, key, digest, failure, resourceId).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            LogLifecycleCompletionFailed(logger, resourceId ?? string.Empty, "failure");
            return false;
        }
    }

    private async Task TryWriteLifecycleAuditAsync(
        string actorUserId,
        string sessionId,
        bool open,
        bool requested,
        bool succeeded,
        SessionView? view,
        string? reasonCode,
        SessionState? oldState,
        SessionState? newState)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            CloudEmueraDbContext db = scope.ServiceProvider.GetRequiredService<CloudEmueraDbContext>();
            IAuditContext? auditContext = scope.ServiceProvider.GetService<IAuditContext>();
            db.AuditEvents.Add(new AuditEventRow
            {
                Id = $"audit_{Guid.CreateVersion7():N}",
                OccurredAt = timeProvider.GetUtcNow(),
                ActorUserId = actorUserId,
                ActorType = AuditActorType.User,
                Action = open
                    ? requested ? AuditActions.SessionOpenRequested : succeeded ? AuditActions.SessionOpened : AuditActions.SessionOpenFailed
                    : requested ? AuditActions.SessionCloseRequested : succeeded ? AuditActions.SessionClosed : AuditActions.SessionCloseFailed,
                ResourceType = "SESSION",
                ResourceId = sessionId,
                RequestId = auditContext?.RequestId,
                Result = succeeded ? AuditResult.Succeeded : AuditResult.Failed,
                ReasonCode = reasonCode,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    gameId = view?.Game.Id,
                    oldState = (oldState ?? view?.State)?.ToString().ToUpperInvariant(),
                    newState = (newState ?? view?.State)?.ToString().ToUpperInvariant(),
                    workerEpoch = view?.WorkerEpoch,
                }),
            });
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogLifecycleAuditFailed(logger, sessionId, open ? "open" : "close");
        }
    }

    private static SessionCommandFailure? ValidateLifecycleState(SessionView view, bool open) => open
        ? view.State switch
        {
            SessionState.Creating => new(SessionErrorCodes.SessionNotReady, "Session 仍在创建中。", 409),
            SessionState.Stopping => new(SessionErrorCodes.SessionTransitionInProgress, "Session 正在关闭。", 409),
            SessionState.Starting => null,
            SessionState.Running => null,
            _ => null,
        }
        : view.State switch
        {
            SessionState.Creating => new(SessionErrorCodes.SessionNotReady, "Session 仍在创建中。", 409),
            SessionState.Stopping => null,
            SessionState.Closed or SessionState.Crashed => null,
            _ => null,
        };

    private static SessionCommandFailure MapRuntimeFailure(SessionRuntimeException exception) => exception.Code switch
    {
        SessionRuntimeResultCodes.ActiveWorkerLimitExceeded => new(SessionErrorCodes.ActiveWorkerLimitExceeded, "实例活动 Worker 上限已用尽。", 409),
        SessionRuntimeResultCodes.GameBlocked => new(SessionErrorCodes.GameBlocked, "游戏当前不可运行。", 409),
        SessionRuntimeResultCodes.ControlPlaneDraining or SessionRuntimeResultCodes.ControlPlaneReconciliationFailed => new(SessionErrorCodes.ServiceNotReady, "控制面当前不可用。", 503),
        SessionRuntimeResultCodes.SessionRootInvalid => new(SessionErrorCodes.SessionRootInvalid, "SessionRoot 校验失败。", 503),
        SessionRuntimeResultCodes.SessionMutationInProgress => new(SessionErrorCodes.MutationInProgress, "SessionRoot 当前被停止态文件操作占用。", 409),
        SessionRuntimeResultCodes.SessionNotOpenable => new(SessionErrorCodes.SessionTransitionInProgress, "Session 当前不接受开启。", 409),
        _ => new("SESSION_LIFECYCLE_FAILED", "Session 生命周期操作失败。", 503),
    };

    private static SessionApplicationException ToSessionException(GameLibraryException exception) => exception.Code switch
    {
        GameLibraryErrorCodes.NotFound => new(SessionErrorCodes.GameNotFound, "游戏不存在。", 404),
        GameLibraryErrorCodes.HasNoCurrentContent => new(SessionErrorCodes.GameHasNoCurrentContent, "游戏没有可运行的 current content。", 409),
        _ => new("SESSION_CREATE_FAILED", "Session 创建失败。", 400, innerException: exception),
    };

    private string ResolveDataPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains('\0') || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session 存储路径不安全。", 503);
        string full = Path.GetFullPath(Path.Combine(databaseOptions.DataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!RuntimePathUtilities.IsStrictlyWithin(full, databaseOptions.DataRoot))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session 存储路径越界。", 503);
        return full;
    }

    private async Task<FileStream> AcquireGameMutationLockAsync(string gameId, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(databaseOptions.DataRoot, "games", gameId);
        try
        {
            GameStorageOwnerMarker.Validate(directory, gameId);
        }
        catch (DirectoryNotFoundException)
        {
            throw new SessionApplicationException(SessionErrorCodes.GameNotFound, "游戏不存在。", 404);
        }
        catch (FileNotFoundException)
        {
            throw new SessionApplicationException(SessionErrorCodes.GameNotFound, "游戏不存在。", 404);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        string lockPath = Path.Combine(directory, ".mutation.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new SessionApplicationException(
                    SessionErrorCodes.SessionTransitionInProgress,
                    "游戏当前有内容操作正在进行。",
                    409,
                    innerException: exception);
            }
        }
    }

    private RuntimeSaveLayout InspectSourceSaveLayout(string relativeContentPath)
    {
        string root = ResolveDataPath(relativeContentPath);
        return InspectSourceSaveLayoutAtRoot(root);
    }

    private static RuntimeSaveLayout InspectSourceSaveLayoutAtRoot(string root)
    {
        string? configuration = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .SingleOrDefault(path => string.Equals(Path.GetFileName(path), "emuera.config", StringComparison.OrdinalIgnoreCase));
        if (configuration is null)
            throw new SessionApplicationException(SessionErrorCodes.GameHasNoCurrentContent, "游戏配置文件不存在。", 409);
        return EmueraSaveLayoutInspector.InspectFile(configuration);
    }

    private void EnsureDataRootFreeSpace(long reservation)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(databaseOptions.DataRoot)) ?? Path.DirectorySeparatorChar.ToString();
            long available = new DriveInfo(root).AvailableFreeSpace;
            if (available < databaseOptions.MinDataRootFreeBytes ||
                available - databaseOptions.MinDataRootFreeBytes < reservation)
                throw new SessionApplicationException(SessionErrorCodes.StorageBudgetExceeded, "DataRoot 可用空间低于 Session 安全余量。", 413);
        }
        catch (SessionApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new SessionApplicationException(SessionErrorCodes.StorageUnavailable, "无法确认 DataRoot 可用空间。", 503, innerException: exception);
        }
    }

    private static SessionRootPublishedManifest CreatePublishedManifest(GameRow game, IReadOnlyList<GameFileRow> files)
    {
        var entries = files.Select(row => new SessionRootManifestEntry(
            row.LogicalPath,
            row.EntryKind == "DIRECTORY" ? SessionRootManifestEntryKind.Directory : SessionRootManifestEntryKind.File,
            row.ByteLength,
            row.EntryKind == "DIRECTORY" ? string.Empty : (row.ContentDigest ?? string.Empty).Replace("sha256:", string.Empty, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new SessionRootPublishedManifest(entries, game.ContentDigest);
    }

    private static string CreateRuntimeManifest(GameRow game, SessionRootPublishedManifest manifest, IReadOnlyList<GameFileRow> files, RuntimeSaveLayout saveLayout)
    {
        FrozenRuntimeManifest value = new(
            1,
            game.ManifestJson,
            game.RuntimeConfigJson,
            game.CompatibilitySummaryJson,
            RuntimeBaseline.CompatibilityProfile,
            RuntimeBaseline.UpstreamCommit,
            RuntimeBaseline.CloudEmueraIntegrationVersion,
            saveLayout,
            manifest.ManifestDigest,
            files.Select(row => new FrozenManifestEntry(row.LogicalPath, row.EntryKind, row.ByteLength, row.ContentDigest ?? string.Empty)).ToArray());
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private bool IsPublishedRootValid(string sessionId)
    {
        try
        {
            SessionRootProtectedMarker marker = SessionRootProtectedMarkerStore.Read(databaseOptions, sessionId);
            string root = ResolveDataPath($"sessions/{sessionId}/root");
            return Directory.Exists(root) && SessionRootProtectedMarkerStore.SameRootIdentity(marker, root);
        }
        catch
        {
            return false;
        }
    }

    private bool IsFinalContainerPresent(string sessionId)
    {
        string path = SessionRootProtectedMarkerStore.ContainerPath(databaseOptions, sessionId);
        return Directory.Exists(path) || File.Exists(path) || RuntimePathUtilities.IsReparsePoint(path);
    }

    private void DeleteSessionContainer(SessionRow session, bool allowMissingRoot)
    {
        string container = SessionRootProtectedMarkerStore.ContainerPath(databaseOptions, session.Id);
        string root = ResolveDataPath(session.SessionRootPath);
        string expectedRoot = Path.GetFullPath(Path.Combine(container, "root"));
        if (!string.Equals(root, expectedRoot, StringComparison.Ordinal))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 路径与受保护容器不匹配。", 503);

        bool containerPresent = Directory.Exists(container) || File.Exists(container) || RuntimePathUtilities.IsReparsePoint(container);
        if (!containerPresent)
        {
            if (allowMissingRoot) return;
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 不存在。", 503);
        }
        if (!Directory.Exists(container) || RuntimePathUtilities.IsReparsePoint(container))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 容器不是安全目录。", 503);

        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(container, "session-container");
        SessionRootProtectedMarker marker;
        try
        {
            marker = SessionRootProtectedMarkerStore.Read(databaseOptions, session.Id);
        }
        catch (SessionRuntimeException exception)
        {
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 保护标记无效。", 503, innerException: exception);
        }
        if (marker.SchemaVersion != 1 ||
            !string.Equals(marker.SessionId, session.Id, StringComparison.Ordinal) ||
            !string.Equals(marker.OwnerUserId, session.OwnerUserId, StringComparison.Ordinal) ||
            !string.Equals(marker.GameId, session.GameId, StringComparison.Ordinal))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 保护标记与 Session 不匹配。", 503);

        bool rootIsReparse = RuntimePathUtilities.IsReparsePoint(root);
        bool rootPresent = Directory.Exists(root) || File.Exists(root) || rootIsReparse;
        if (rootIsReparse)
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 根目录不能是符号链接。", 503);
        if (!rootPresent && !allowMissingRoot)
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 根目录不存在。", 503);
        if (rootPresent)
        {
            try
            {
                if (!Directory.Exists(root) || !SessionRootProtectedMarkerStore.SameRootIdentity(marker, root))
                    throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 根目录身份校验失败。", 503);
            }
            catch (SessionApplicationException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "SessionRoot 根目录身份校验失败。", 503, innerException: exception);
            }
        }

        if (OperatingSystem.IsLinux())
        {
            string sessionsDirectory = Path.Combine(Path.GetFullPath(databaseOptions.DataRoot), "sessions");
            RuntimePathUtilities.ValidateNoReparsePointsAlongPath(sessionsDirectory, "session-container-parent");
            using Microsoft.Win32.SafeHandles.SafeFileHandle parent = LinuxFileOperations.OpenDirectory(sessionsDirectory);
            using Microsoft.Win32.SafeHandles.SafeFileHandle current = LinuxFileOperations.OpenDirectory(container);
            LinuxFileOperations.FileIdentity identity = LinuxFileOperations.ReadIdentity(current);
            LinuxFileOperations.DeleteTreeAt(parent, session.Id, expectedIdentity: identity, allowReadOnly: true);
            LinuxFileOperations.Sync(parent);
            return;
        }

        SafeDeleteOwnedStaging(container, $"sessions/{session.Id}");
    }

    private void ValidateStagingContainer(string path, string relative)
    {
        string expectedPrefix = Path.Combine(databaseOptions.DataRoot, "sessions", ".staging") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal) || !Path.GetFileName(path).StartsWith("sess_", StringComparison.Ordinal))
            throw new SessionApplicationException(SessionErrorCodes.SessionRootInvalid, "Session staging ownership 无效。", 503);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, relative);
    }

    private static void SafeDeleteOwnedStaging(string path, string relative)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(path, relative);
        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            RuntimePathUtilities.ThrowIfReparsePoint(entry.FullName, relative, missingIsAllowed: false);
            if (entry is FileInfo file) RuntimePathUtilities.ThrowIfHardLink(file.FullName, relative);
            else if (entry is not DirectoryInfo) throw new IOException("Staging contains a special entry.");
        }
        Directory.Delete(path, recursive: true);
    }

    private static void SyncDirectoryTree(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = LinuxFileOperations.OpenDirectory(path);
        LinuxFileOperations.Sync(handle);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string NormalizeName(string name)
    {
        string normalized = (name ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalized.EnumerateRunes().Count() is < 1 or > 200 || normalized.Any(char.IsControl))
            throw new SessionApplicationException(SessionErrorCodes.ValidationFailed, "Session 名称必须是 1～200 个 Unicode scalar 且不能包含控制字符。", 400);
        return normalized;
    }

    private static void ValidateTextLayout(int fontSize, int lineHeight)
    {
        if (fontSize is < 8 or > 72 || lineHeight < fontSize || lineHeight > 128)
            throw new SessionApplicationException(SessionErrorCodes.ValidationFailed, "字号必须为 8～72px，行高必须不小于字号且不超过 128px。", 400);
    }

    private static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > PersistenceLimits.IdempotencyKeyMaxLength || key.Any(char.IsControl))
            throw new SessionApplicationException(SessionErrorCodes.IdempotencyKeyRequired, "需要有效的 Idempotency-Key。", 428);
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 6 or > 64 || !sessionId.StartsWith("sess_", StringComparison.Ordinal) || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new SessionApplicationException(SessionErrorCodes.SessionNotFound, "Session 不存在。", 404);
    }

    private static void ValidateGameId(string gameId)
    {
        if (string.IsNullOrEmpty(gameId) || gameId.Length is < 6 or > 64 || !gameId.StartsWith("game_", StringComparison.Ordinal) || gameId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new SessionApplicationException(SessionErrorCodes.GameNotFound, "游戏不存在。", 404);
    }

    private static string StableCreateFailureCode(Exception exception) => exception switch
    {
        SessionApplicationException value => value.Code,
        GameLibraryException value when value.Code == GameLibraryErrorCodes.HasNoCurrentContent => SessionErrorCodes.GameHasNoCurrentContent,
        RuntimePathException => SessionErrorCodes.SessionRootInvalid,
        IOException or UnauthorizedAccessException => "SESSION_CREATE_IO_FAILED",
        _ => "SESSION_CREATE_FAILED",
    };

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private CursorData? DecodeCursor(string actorUserId, SessionListQuery query, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            byte[] bytes = FromBase64Url(value);
            string text = Encoding.UTF8.GetString(bytes);
            string[] parts = text.Split('|');
            if (parts.Length != 6 || parts[0] != actorUserId || parts[1] != (query.GameId ?? string.Empty) || parts[2] != (query.State?.ToString() ?? string.Empty) || !long.TryParse(parts[3], out long createdAt))
                throw new FormatException();
            string id = parts[4];
            string expected = CursorSignature(actorUserId, query.GameId, query.State, createdAt, id);
            if (!CryptographicEquals(parts[5], expected)) throw new FormatException();
            return new CursorData(createdAt, id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException)
        {
            throw new SessionApplicationException(SessionErrorCodes.ValidationFailed, "Session cursor 无效。", 400, innerException: exception);
        }
    }

    private string EncodeCursor(string actorUserId, SessionListQuery query, SessionProjection row)
    {
        long timestamp = row.CreatedAt.ToUnixTimeMilliseconds();
        string signature = CursorSignature(actorUserId, query.GameId, query.State, timestamp, row.Id);
        return ToBase64Url(Encoding.UTF8.GetBytes(string.Join('|', actorUserId, query.GameId ?? string.Empty, query.State?.ToString() ?? string.Empty, timestamp, row.Id, signature)));
    }

    private string CursorSignature(string actorUserId, string? gameId, SessionState? state, long createdAt, string id)
    {
        byte[] key = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"CloudEmuera/session-cursor/v1/{databaseOptions.DataRoot}"));
        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('|', actorUserId, gameId ?? string.Empty, state?.ToString() ?? string.Empty, createdAt, id));
        return Convert.ToHexString(hmac.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static bool CryptographicEquals(string first, string second)
    {
        byte[] left = Encoding.UTF8.GetBytes(first);
        byte[] right = Encoding.UTF8.GetBytes(second);
        return left.Length == right.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private sealed record SessionProjection(
        string Id,
        string Name,
        string GameId,
        string GameName,
        string SourceContentDigest,
        long SourceContentRevision,
        string RuntimeVersion,
        int FontSize,
        int LineHeight,
        SessionState State,
        int StateVersion,
        long WorkerEpoch,
        bool WaitingForInput,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset LastActivityAt,
        DateTimeOffset? ClosedAt,
        string? CloseReason);

    private sealed record CursorData(long CreatedAtUnixMilliseconds, string Id);

    private sealed record CreateRecoveryItem(string OperationId, string SessionId, string ActorUserId);

    private sealed record LifecycleRecoveryItem(string ActorUserId, string Scope, string IdempotencyKey, string RequestDigest, string SessionId);

    private sealed record DeleteRecoveryItem(string ActorUserId, string IdempotencyKey, string RequestDigest, string SessionId);

    private enum CreateFailureDisposition
    {
        Failed,
        Pending,
        Committed,
    }

    private sealed record CreatePreparation(
        PersistentIdempotencyRecord? Existing,
        SessionView? ExistingView,
        string? OperationId = null,
        string? SessionId = null);

    private sealed record FrozenManifestEntry(string Path, string EntryKind, long Bytes, string Digest);

    private sealed record FrozenRuntimeManifest(
        int SchemaVersion,
        string GameManifestJson,
        string RuntimeConfigJson,
        string CompatibilitySummaryJson,
        string CompatibilityProfile,
        string UpstreamCommit,
        string RuntimeVersion,
        RuntimeSaveLayout SaveLayout,
        string SourceManifestDigest,
        IReadOnlyList<FrozenManifestEntry> Entries,
        string CapabilityMatrixVersion = RuntimeBaseline.CapabilityMatrixVersion,
        string CapabilitySetDigest = RuntimeBaseline.CapabilitySetDigest);

    [LoggerMessage(EventId = 2601, Level = LogLevel.Error, Message = "session_lifecycle_failed sessionId={SessionId} operation={Operation}")]
    private static partial void LogLifecycleFailed(ILogger logger, string sessionId, string operation);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Error, Message = "session_create_cleanup_failed sessionId={SessionId} operationId={OperationId}")]
    private static partial void LogCreateCleanupFailed(ILogger logger, string sessionId, string operationId);

    [LoggerMessage(EventId = 2606, Level = LogLevel.Error, Message = "session_create_failed sessionId={SessionId} operationId={OperationId} code={Code}")]
    private static partial void LogCreateFailed(ILogger logger, string sessionId, string operationId, string code);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Error, Message = "session_create_recovery_commit_failed sessionId={SessionId} operationId={OperationId}")]
    private static partial void LogCreateRecoveryCommitFailed(ILogger logger, string sessionId, string operationId);

    [LoggerMessage(EventId = 2605, Level = LogLevel.Warning, Message = "session_lifecycle_audit_failed sessionId={SessionId} operation={Operation}")]
    private static partial void LogLifecycleAuditFailed(ILogger logger, string sessionId, string operation);

    [LoggerMessage(EventId = 2607, Level = LogLevel.Warning, Message = "session_lifecycle_idempotency_completion_failed sessionId={SessionId} result={Result}")]
    private static partial void LogLifecycleCompletionFailed(ILogger logger, string sessionId, string result);

    [LoggerMessage(EventId = 2608, Level = LogLevel.Warning, Message = "session_lifecycle_view_read_failed sessionId={SessionId}")]
    private static partial void LogLifecycleReadFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 2609, Level = LogLevel.Error, Message = "session_delete_failed sessionId={SessionId}")]
    private static partial void LogDeleteFailed(ILogger logger, string sessionId);
}
