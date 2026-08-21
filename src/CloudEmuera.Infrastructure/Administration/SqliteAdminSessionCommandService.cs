using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudEmuera.Application.Administration;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Infrastructure.Administration;

/// <summary>
/// Durable administrative command boundary. A requested audit is committed
/// before the Worker side effect; the idempotency row remains IN_PROGRESS until
/// the Session terminal state and the completion audit are both observable.
/// </summary>
public sealed class SqliteAdminSessionCommandService(
    IAdminRuntimeStore store,
    ISessionLifecycleExecutor lifecycle,
    IServiceScopeFactory scopes,
    ILogger<SqliteAdminSessionCommandService> logger) : IAdminSessionCommandService, IAdminForceStopRecovery
{
    private const int ReasonMaxScalars = 500;
    private const string ForceStopReasonCode = "admin_force_stopped";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<AdminForceStopResult> ForceStopAsync(
        CurrentActor actor,
        string sessionId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string normalizedSessionId = NormalizeSessionId(sessionId);
        string normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        string normalizedReason = NormalizeReason(reason);
        return ExecuteAsync(actor, normalizedSessionId, normalizedKey, normalizedReason, cancellationToken);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AdminPendingIdempotency> pending = await store.ListPendingIdempotencyAsync(cancellationToken).ConfigureAwait(false);
        foreach (AdminPendingIdempotency operation in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The command was accepted under an administrator identity. A
            // later role change must not strand an already accepted operation.
            var recoveryActor = new CurrentActor(operation.ActorUserId, "ADMIN", "admin-force-stop-recovery");
            string? requestedReason = await store.ReadRequestedReasonAsync(operation.ResourceId, operation.ActorUserId, operation.Key, cancellationToken).ConfigureAwait(false);
            if (requestedReason is null)
            {
                await CompleteFailureAsync(operation, AdminErrorCodes.ValidationFailed, 409, cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                await ExecuteAsync(recoveryActor, operation.ResourceId, operation.Key, requestedReason, cancellationToken).ConfigureAwait(false);
            }
            catch (AdminSessionCommandException)
            {
                // A recovery pass must not prevent the next pending command
                // from being inspected. The durable row remains failed or in
                // progress according to the operation outcome.
            }
        }
    }

    private async Task<AdminForceStopResult> ExecuteAsync(
        CurrentActor actor,
        string sessionId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!actor.IsAdmin)
            return Failure(AdminErrorCodes.SessionNotFound, "Session 不存在。", 404);

        string digest = Digest(sessionId, reason);
        AdminIdempotencyRecord record = await store.BeginIdempotencyAsync(
            actor.UserId, AdminCommandScopes.ForceStop, idempotencyKey, digest, sessionId, cancellationToken).ConfigureAwait(false);
        switch (record.State)
        {
            case "CONFLICT":
                return Failure(AdminErrorCodes.IdempotencyKeyReused, "幂等键已用于其他请求。", 409, replayed: true);
            case "SUCCEEDED":
            case "FAILED":
                return Replay(record);
        }

        AdminSessionTarget? target = await store.ReadSessionTargetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest, AdminErrorCodes.SessionNotFound, "Session 不存在。", 404, cancellationToken).ConfigureAwait(false);

        if (target.State is not (SessionState.Starting or SessionState.Running or SessionState.Stopping) ||
            string.IsNullOrWhiteSpace(target.WorkerId) || target.WorkerEpoch is null)
        {
            // A replay after the side effect may observe the terminal state.
            if (IsForceStopped(target.View))
                return await CompleteSuccessForExistingAsync(actor, sessionId, idempotencyKey, digest, target.View, replayed: true, cancellationToken).ConfigureAwait(false);
            return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest, AdminErrorCodes.SessionNotActive, "Session 当前没有可停止的 Worker。", 409, cancellationToken).ConfigureAwait(false);
        }

        if (!await store.HasAuditAsync(AdminAuditActions.ForceStopRequested, sessionId, actor.UserId, idempotencyKey, cancellationToken).ConfigureAwait(false))
        {
            await store.AppendAuditAsync(new AdminAuditEntry(
                AdminAuditActions.ForceStopRequested,
                "SESSION",
                sessionId,
                "SUCCEEDED",
                ForceStopReasonCode,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    idempotencyKey,
                    reason,
                    workerId = target.WorkerId,
                    workerEpoch = target.WorkerEpoch,
                }, JsonOptions),
                actor,
                CurrentRequestId()), cancellationToken).ConfigureAwait(false);
        }

        ForceStopStartedLog(logger, sessionId, target.WorkerId!, target.WorkerEpoch.Value, reason.Length > 0, null);

        SessionRuntimeCloseResult stopped;
        try
        {
            // BeginStopping is the linearization point. The lifecycle executor
            // and coordinator must finish despite an HTTP disconnect.
            stopped = await lifecycle.ForceStopAsync(
                sessionId,
                target.WorkerId!,
                target.WorkerEpoch.Value,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (SessionApplicationException exception)
        {
            AdminSessionTarget? afterTransition = await store.ReadSessionTargetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (afterTransition is not null && IsForceStopped(afterTransition.View))
                return await CompleteSuccessForExistingAsync(actor, sessionId, idempotencyKey, digest, afterTransition.View, replayed: true, CancellationToken.None).ConfigureAwait(false);
            return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest,
                exception.Code == SessionErrorCodes.SessionTransitionInProgress ? AdminErrorCodes.StaleWorkerEpoch : exception.Code,
                exception.Code == SessionErrorCodes.SessionTransitionInProgress ? "Worker 代次已变化。" : "Session 强制停止失败。",
                exception.Code == SessionErrorCodes.SessionTransitionInProgress ? 409 : 503,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (SessionRuntimeException exception)
        {
            if (string.Equals(exception.Code, SessionRuntimeResultCodes.WorkerExitUnconfirmed, StringComparison.Ordinal))
                return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest, AdminErrorCodes.WorkerExitUnconfirmed, "Worker 退出尚未确认。", 503, CancellationToken.None, leaveInProgress: true).ConfigureAwait(false);
            return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest, AdminErrorCodes.StaleWorkerEpoch, "Worker 代次已变化。", 409, CancellationToken.None).ConfigureAwait(false);
        }

        AdminSessionTarget? completed = await store.ReadSessionTargetAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        // The Worker observer may win the final SQLite write after the
        // process-exit barrier has completed. It is still the same fenced
        // force-stop outcome when the durable terminal projection is the
        // requested crash-equivalent state.
        if (completed is null || !IsForceStopped(completed.View))
            return await FailAndAuditAsync(actor, sessionId, idempotencyKey, digest, AdminErrorCodes.WorkerExitUnconfirmed, "Worker 退出尚未确认。", 503, CancellationToken.None, leaveInProgress: true).ConfigureAwait(false);

        return await CompleteSuccessForExistingAsync(actor, sessionId, idempotencyKey, digest, completed.View, replayed: false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<AdminForceStopResult> CompleteSuccessForExistingAsync(
        CurrentActor actor,
        string sessionId,
        string key,
        string digest,
        SessionView view,
        bool replayed,
        CancellationToken cancellationToken)
    {
        string responseJson = JsonSerializer.Serialize(view, JsonOptions);
        if (!await store.HasAuditAsync(AdminAuditActions.ForceStopCompleted, sessionId, actor.UserId, key, cancellationToken).ConfigureAwait(false))
        {
            await store.AppendAuditAsync(new AdminAuditEntry(
                AdminAuditActions.ForceStopCompleted,
                "SESSION",
                sessionId,
                "SUCCEEDED",
                ForceStopReasonCode,
                JsonSerializer.Serialize(new { schemaVersion = 1, idempotencyKey = key, workerEpoch = view.WorkerEpoch }, JsonOptions),
                actor,
                CurrentRequestId()), cancellationToken).ConfigureAwait(false);
        }
        await store.CompleteIdempotencySuccessAsync(actor.UserId, AdminCommandScopes.ForceStop, key, digest, 200, responseJson, sessionId, cancellationToken).ConfigureAwait(false);
        ForceStopCompletedLog(logger, sessionId, view.WorkerEpoch, replayed, null);
        return new AdminForceStopResult(view, 200, replayed, false);
    }

    private async Task<AdminForceStopResult> FailAndAuditAsync(
        CurrentActor actor,
        string sessionId,
        string key,
        string digest,
        string code,
        string message,
        int status,
        CancellationToken cancellationToken,
        bool leaveInProgress = false)
    {
        if (!await store.HasAuditAsync(AdminAuditActions.ForceStopFailed, sessionId, actor.UserId, key, cancellationToken).ConfigureAwait(false))
        {
            await store.AppendAuditAsync(new AdminAuditEntry(
                AdminAuditActions.ForceStopFailed,
                "SESSION",
                sessionId,
                "FAILED",
                code,
                JsonSerializer.Serialize(new { schemaVersion = 1, idempotencyKey = key, status }, JsonOptions),
                actor,
                CurrentRequestId()), cancellationToken).ConfigureAwait(false);
        }
        if (!leaveInProgress)
        {
            AdminCommandFailure failure = new(code, message, status);
            await store.CompleteIdempotencyFailureAsync(actor.UserId, AdminCommandScopes.ForceStop, key, digest, status, code,
                JsonSerializer.Serialize(failure, JsonOptions), sessionId, cancellationToken).ConfigureAwait(false);
        }
        ForceStopFailedLog(logger, sessionId, code, status, null);
        return new AdminForceStopResult(null, status, false, leaveInProgress, new AdminCommandFailure(code, message, status));
    }

    private async Task CompleteFailureAsync(AdminPendingIdempotency operation, string code, int status, CancellationToken cancellationToken)
    {
        CurrentActor actor = new(operation.ActorUserId, "ADMIN", "admin-force-stop-recovery");
        if (!await store.HasAuditAsync(AdminAuditActions.ForceStopFailed, operation.ResourceId, operation.ActorUserId, operation.Key, cancellationToken).ConfigureAwait(false))
        {
            await store.AppendAuditAsync(new AdminAuditEntry(
                AdminAuditActions.ForceStopFailed,
                "SESSION",
                operation.ResourceId,
                "FAILED",
                code,
                JsonSerializer.Serialize(new { schemaVersion = 1, idempotencyKey = operation.Key, status }, JsonOptions),
                actor,
                CurrentRequestId()), cancellationToken).ConfigureAwait(false);
        }
        AdminCommandFailure failure = new(code, "强制停止请求无法恢复。", status);
        await store.CompleteIdempotencyFailureAsync(operation.ActorUserId, operation.Scope, operation.Key, operation.RequestDigest,
            status, code, JsonSerializer.Serialize(failure, JsonOptions), operation.ResourceId, cancellationToken).ConfigureAwait(false);
        ForceStopFailedLog(logger, operation.ResourceId, code, status, null);
    }

    private static AdminForceStopResult Replay(AdminIdempotencyRecord record)
    {
        if (record.State == "SUCCEEDED")
        {
            SessionView? view = JsonSerializer.Deserialize<SessionView>(record.ResponseJson, JsonOptions);
            return new AdminForceStopResult(view, record.ResponseStatus, true, false,
                view is null ? new AdminCommandFailure(AdminErrorCodes.ServiceNotReady, "无法读取强制停止结果。", 503) : null);
        }
        AdminCommandFailure? failure = JsonSerializer.Deserialize<AdminCommandFailure>(record.ResponseJson, JsonOptions);
        return new AdminForceStopResult(null, record.ResponseStatus, true, record.State == "INPROGRESS", failure);
    }

    private static AdminForceStopResult Failure(string code, string message, int status, bool replayed = false) =>
        new(null, status, replayed, false, new AdminCommandFailure(code, message, status));

    private static bool IsForceStopped(SessionView view) =>
        view.State == SessionState.Crashed && string.Equals(view.CloseReason, ForceStopReasonCode, StringComparison.Ordinal);

    private static string NormalizeSessionId(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 64 || normalized.Any(char.IsControl))
            throw new AdminSessionCommandException(AdminErrorCodes.ValidationFailed, "Session 标识无效。", 400);
        return normalized;
    }

    private static string NormalizeIdempotencyKey(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 128 || normalized.Any(char.IsControl))
            throw new AdminSessionCommandException(AdminErrorCodes.IdempotencyKeyRequired, "需要有效的 Idempotency-Key。", 400);
        return normalized;
    }

    private static string NormalizeReason(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.EnumerateRunes().Count() > ReasonMaxScalars ||
            normalized.Any(character => character is '\0' or '\r' or '\n' || char.IsControl(character)))
            throw new AdminSessionCommandException(AdminErrorCodes.ValidationFailed, "强制停止原因无效。", 400);
        return normalized;
    }

    private static string Digest(string sessionId, string reason) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"CloudEmuera/admin-force-stop/v1\n{sessionId}\n{reason}"))).ToLowerInvariant()}";

    private string? CurrentRequestId()
    {
        using IServiceScope scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<IAuditContext>()?.RequestId;
    }

    private static readonly Action<ILogger, string, string, long, bool, Exception?> ForceStopStartedLog =
        LoggerMessage.Define<string, string, long, bool>(
            LogLevel.Information,
            new EventId(2701, "AdminForceStopStarted"),
            "session.force_stop.started sessionId={SessionId} workerId={WorkerId} workerEpoch={WorkerEpoch} reasonProvided={ReasonProvided}");

    private static readonly Action<ILogger, string, long, bool, Exception?> ForceStopCompletedLog =
        LoggerMessage.Define<string, long, bool>(
            LogLevel.Information,
            new EventId(2702, "AdminForceStopCompleted"),
            "session.force_stop.completed sessionId={SessionId} workerEpoch={WorkerEpoch} replayed={Replayed}");

    private static readonly Action<ILogger, string, string, int, Exception?> ForceStopFailedLog =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Warning,
            new EventId(2703, "AdminForceStopFailed"),
            "session.force_stop.failed sessionId={SessionId} reasonCode={ReasonCode} statusCode={StatusCode}");
}
