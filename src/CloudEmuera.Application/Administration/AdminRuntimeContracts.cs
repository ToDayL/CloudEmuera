using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Application.Administration;

public static class AdminErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string CsrfValidationFailed = "CSRF_VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionNotActive = "SESSION_NOT_ACTIVE";
    public const string SessionTransitionInProgress = "SESSION_TRANSITION_IN_PROGRESS";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string StaleWorkerEpoch = "STALE_WORKER_EPOCH";
    public const string ServiceNotReady = "SERVICE_NOT_READY";
    public const string WorkerExitUnconfirmed = "WORKER_EXIT_UNCONFIRMED";
}

public static class AdminCommandScopes
{
    public const string ForceStop = "ADMIN_SESSION_FORCE_STOP";
}

public static class AdminAuditActions
{
    public const string ForceStopRequested = "ADMIN_SESSION_FORCE_STOP_REQUESTED";
    public const string ForceStopCompleted = "ADMIN_SESSION_FORCE_STOP_COMPLETED";
    public const string ForceStopFailed = "ADMIN_SESSION_FORCE_STOP_FAILED";
}

public sealed record AdminRuntimeQueryOptions(int RecentFailureLimit = 20)
{
    public int NormalizedRecentFailureLimit => Math.Clamp(RecentFailureLimit, 1, 100);
}

public sealed record AdminPersistentRuntimeSnapshot(
    IReadOnlyList<AdminPersistentSession> ActiveSessions,
    IReadOnlyList<AdminPersistentFailure> RecentFailures);

public sealed record AdminPersistentSession(
    string Id,
    string Name,
    string OwnerUsername,
    string GameId,
    string GameName,
    SessionState State,
    int StateVersion,
    long WorkerEpoch,
    string? LeaseWorkerId,
    long? LeaseEpoch,
    string? LeaseStatus,
    int? Pid,
    string? ControlPlaneInstanceId,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset LastActivityAt,
    long LastOutputSequence);

public sealed record AdminPersistentFailure(
    string SessionId,
    string SessionName,
    string OwnerUsername,
    string GameId,
    string GameName,
    long WorkerEpoch,
    DateTimeOffset? FailedAt,
    string ReasonCode);

/// <summary>
/// Facts owned by the API process.  This contract deliberately contains only
/// operational identifiers and aggregate counters; it has no path, token,
/// input, snapshot text or exception field.
/// </summary>
public sealed record AdminRuntimeHostSnapshot(
    string ControlPlaneInstanceId,
    string ControlPlaneState,
    IReadOnlyList<AdminHostWorker> Workers,
    int WebSocketConnectionCount,
    int SubscriptionCount,
    IReadOnlySet<string> WriteFenceUnconfirmedSessionIds);

public sealed record AdminHostWorker(
    string SessionId,
    string WorkerId,
    long WorkerEpoch,
    int? Pid,
    bool Registered,
    bool Ready,
    bool ProcessExited,
    DateTimeOffset? HeartbeatAt,
    long LastOutputSequence,
    long DroppedPendingEventCount,
    AdminRealtimeHostSnapshot Realtime);

public sealed record AdminRealtimeHostSnapshot(
    string HubState,
    long SnapshotSequence,
    long? SnapshotBytes,
    string SnapshotSizeStatus,
    int SubscriptionCount,
    long ResyncCount,
    long SoftOverflowCount,
    long HardOverflowCount,
    long FaultCount,
    long DroppedPendingEventCount);

public sealed record AdminRuntimeSnapshot(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    AdminInstanceSnapshot Instance,
    IReadOnlyList<AdminWorkerSnapshot> Workers,
    IReadOnlyList<AdminFailureSnapshot> RecentFailures);

public sealed record AdminInstanceSnapshot(
    string ControlPlaneState,
    int ActiveWorkerCount,
    int WebSocketConnectionCount,
    int SubscriptionCount);

public sealed record AdminWorkerSnapshot(
    AdminSessionSnapshot Session,
    AdminWorkerProcessSnapshot Worker,
    AdminRealtimeSnapshot Realtime,
    string RuntimeConsistency);

public sealed record AdminSessionSnapshot(
    string Id,
    string Name,
    string OwnerUsername,
    string GameId,
    string GameName,
    string State,
    int StateVersion,
    DateTimeOffset LastActivityAt);

public sealed record AdminWorkerProcessSnapshot(
    string? WorkerId,
    int? Pid,
    long WorkerEpoch,
    string LeaseStatus,
    DateTimeOffset? HeartbeatAt,
    long? HeartbeatAgeMilliseconds,
    bool Registered,
    bool Ready,
    bool ProcessExited,
    long LastOutputSequence);

public sealed record AdminRealtimeSnapshot(
    string HubState,
    long SnapshotSequence,
    long? SnapshotBytes,
    string SnapshotSizeStatus,
    int SubscriptionCount,
    long ResyncCount,
    long SoftOverflowCount,
    long HardOverflowCount,
    long FaultCount,
    long DroppedPendingEventCount);

public sealed record AdminFailureSnapshot(
    string SessionId,
    string SessionName,
    string OwnerUsername,
    string GameId,
    string GameName,
    long WorkerEpoch,
    DateTimeOffset? FailedAt,
    string ReasonCode);

public sealed record AdminSessionTarget(
    string Id,
    SessionView View,
    string OwnerUserId,
    string? WorkerId,
    long? WorkerEpoch,
    SessionState State,
    string? ControlPlaneInstanceId);

public sealed record AdminIdempotencyRecord(
    string State,
    string RequestDigest,
    int ResponseStatus,
    string ResponseJson,
    string? ResourceId,
    string? ErrorCode);

public sealed record AdminAuditEntry(
    string Action,
    string ResourceType,
    string ResourceId,
    string Result,
    string? ReasonCode,
    string MetadataJson,
    CurrentActor Actor,
    string? RequestId);

public interface IAdminRuntimeStore
{
    Task<AdminPersistentRuntimeSnapshot> ReadRuntimeAsync(
        AdminRuntimeQueryOptions options,
        CancellationToken cancellationToken = default);

    Task<AdminSessionTarget?> ReadSessionTargetAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<bool> HasAuditAsync(
        string action,
        string sessionId,
        string actorUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<string?> ReadRequestedReasonAsync(
        string sessionId,
        string actorUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task AppendAuditAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default);

    Task<AdminIdempotencyRecord> BeginIdempotencyAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task CompleteIdempotencySuccessAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string responseJson,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task CompleteIdempotencyFailureAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string errorCode,
        string responseJson,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminPendingIdempotency>> ListPendingIdempotencyAsync(CancellationToken cancellationToken = default);
}

public sealed record AdminPendingIdempotency(
    string ActorUserId,
    string Scope,
    string Key,
    string RequestDigest,
    string ResourceId);

public interface IAdminRuntimeDiagnostics
{
    Task<AdminRuntimeHostSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IAdminRuntimeQuery
{
    Task<AdminRuntimeSnapshot> ReadAsync(
        AdminRuntimeQueryOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class AdminRuntimeQuery(
    IAdminRuntimeStore store,
    IAdminRuntimeDiagnostics diagnostics,
    TimeProvider timeProvider) : IAdminRuntimeQuery
{
    public async Task<AdminRuntimeSnapshot> ReadAsync(
        AdminRuntimeQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        AdminPersistentRuntimeSnapshot persistent = await store.ReadRuntimeAsync(options, cancellationToken).ConfigureAwait(false);
        AdminRuntimeHostSnapshot host = await diagnostics.ReadAsync(cancellationToken).ConfigureAwait(false);

        var workers = new List<AdminWorkerSnapshot>(persistent.ActiveSessions.Count);
        foreach (AdminPersistentSession session in persistent.ActiveSessions)
        {
            AdminHostWorker? inMemory = host.Workers.FirstOrDefault(worker =>
                string.Equals(worker.SessionId, session.Id, StringComparison.Ordinal));
            string consistency = ResolveConsistency(session, inMemory, host.ControlPlaneInstanceId, host.WriteFenceUnconfirmedSessionIds);
            AdminWorkerProcessSnapshot process = new(
                inMemory?.WorkerId ?? session.LeaseWorkerId,
                inMemory?.Pid ?? session.Pid,
                inMemory?.WorkerEpoch ?? session.WorkerEpoch,
                session.LeaseStatus ?? "UNKNOWN",
                inMemory?.HeartbeatAt ?? session.HeartbeatAt,
                GetHeartbeatAgeMilliseconds(observedAt, inMemory?.HeartbeatAt ?? session.HeartbeatAt),
                inMemory?.Registered ?? false,
                inMemory?.Ready ?? false,
                inMemory?.ProcessExited ?? false,
                inMemory?.LastOutputSequence ?? session.LastOutputSequence);
            AdminRealtimeHostSnapshot realtime = inMemory?.Realtime ?? new AdminRealtimeHostSnapshot(
                "NOT_PRESENT", 0, null, "NOT_READY", 0, 0, 0, 0, 0, 0);
            workers.Add(new AdminWorkerSnapshot(
                new AdminSessionSnapshot(session.Id, session.Name, session.OwnerUsername, session.GameId, session.GameName,
                    session.State.ToString().ToUpperInvariant(), session.StateVersion, session.LastActivityAt),
                process,
                new AdminRealtimeSnapshot(realtime.HubState, realtime.SnapshotSequence, realtime.SnapshotBytes,
                    realtime.SnapshotSizeStatus, realtime.SubscriptionCount, realtime.ResyncCount,
                    realtime.SoftOverflowCount, realtime.HardOverflowCount, realtime.FaultCount,
                    realtime.DroppedPendingEventCount),
                consistency));
        }

        IReadOnlyList<AdminFailureSnapshot> failures = persistent.RecentFailures
            .Select(failure => new AdminFailureSnapshot(failure.SessionId, failure.SessionName, failure.OwnerUsername,
                failure.GameId, failure.GameName, failure.WorkerEpoch, failure.FailedAt, failure.ReasonCode))
            .ToArray();
        return new AdminRuntimeSnapshot(
            1,
            observedAt,
            new AdminInstanceSnapshot(host.ControlPlaneState, workers.Count, host.WebSocketConnectionCount, host.SubscriptionCount),
            workers,
            failures);
    }

    private static string ResolveConsistency(
        AdminPersistentSession session,
        AdminHostWorker? worker,
        string controlPlaneInstanceId,
        IReadOnlySet<string> unconfirmed)
    {
        if (unconfirmed.Contains(session.Id))
            return "WRITE_FENCE_UNCONFIRMED";
        if (worker is null)
            return "LEASE_ONLY";
        if (!string.Equals(controlPlaneInstanceId, session.ControlPlaneInstanceId, StringComparison.Ordinal))
            return "STALE_IN_MEMORY";
        if (!string.Equals(worker.WorkerId, session.LeaseWorkerId, StringComparison.Ordinal) ||
            worker.WorkerEpoch != (session.LeaseEpoch ?? session.WorkerEpoch))
            return "STALE_IN_MEMORY";
        return worker.ProcessExited ? "EXIT_OBSERVED" : "MATCHED";
    }

    private static long? GetHeartbeatAgeMilliseconds(DateTimeOffset observedAt, DateTimeOffset? heartbeatAt)
    {
        if (heartbeatAt is null)
            return null;
        TimeSpan age = observedAt - heartbeatAt.Value;
        return Math.Max(0, (long)age.TotalMilliseconds);
    }
}

public sealed record AdminForceStopRequest(string Reason);

public sealed record AdminCommandFailure(string Code, string Message, int StatusCode, object? Details = null);

public sealed record AdminForceStopResult(
    SessionView? Value,
    int StatusCode,
    bool Replayed,
    bool Pending,
    AdminCommandFailure? Failure = null)
{
    public bool Succeeded => Failure is null && Value is not null;
}

public interface IAdminSessionCommandService
{
    Task<AdminForceStopResult> ForceStopAsync(
        CurrentActor actor,
        string sessionId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default);
}

public interface IAdminForceStopRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken = default);
}

public sealed class AdminSessionCommandException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
