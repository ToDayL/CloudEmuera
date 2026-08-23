using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Fonts;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Application.Sessions;

/// <summary>
/// Application-facing Session errors.  HTTP status codes are deliberately kept
/// here as stable outcome metadata; the API adapter owns the actual response.
/// </summary>
public static class SessionErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string GameNotFound = "GAME_NOT_FOUND";
    public const string GameHasNoCurrentContent = "GAME_HAS_NO_CURRENT_CONTENT";
    public const string GameBlocked = "GAME_BLOCKED";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionNotReady = "SESSION_NOT_READY";
    public const string SessionTransitionInProgress = "SESSION_TRANSITION_IN_PROGRESS";
    public const string SessionRootInvalid = "SESSION_ROOT_INVALID";
    public const string ActiveWorkerLimitExceeded = "ACTIVE_WORKER_LIMIT_EXCEEDED";
    public const string ControlPlaneDraining = "CONTROL_PLANE_DRAINING";
    public const string ServiceNotReady = "SERVICE_NOT_READY";
    public const string StorageBudgetExceeded = "SESSION_STORAGE_QUOTA_EXCEEDED";
    public const string StorageUnavailable = "DATA_ROOT_UNAVAILABLE";
    public const string InactiveSessionLimitExceeded = "INACTIVE_SESSION_LIMIT_EXCEEDED";
    public const string SessionNotDeletable = "SESSION_NOT_DELETABLE";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string SessionNotAcceptingInput = "SESSION_NOT_ACCEPTING_INPUT";
    public const string MutationInProgress = "SESSION_MUTATION_IN_PROGRESS";
    public const string RuntimeFontFaceNotFound = "FONT_FACE_NOT_FOUND";
}

public sealed class SessionApplicationException(
    string code,
    string message,
    int statusCode,
    bool persistFailure = true,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public bool PersistFailure { get; } = persistFailure;
}

public sealed record CreateSessionCommand(
    string GameId,
    string Name,
    string IdempotencyKey,
    int FontSize = 18,
    int LineHeight = 19,
    string FontFaceId = RuntimeFontDefaults.DefaultFaceId);

public sealed record SessionLifecycleCommand(string SessionId, string IdempotencyKey, int BrowserWidth = 0);

public sealed record SessionConfigurationCommand(
    string SessionId,
    string Name,
    int FontSize,
    int LineHeight,
    string IdempotencyKey,
    string FontFaceId = RuntimeFontDefaults.DefaultFaceId);

public sealed record SessionDeleteCommand(string SessionId, string IdempotencyKey);

public sealed record SessionListQuery(string? GameId, SessionState? State, string? Cursor, int Limit = 50);

public sealed record SessionGameSummary(string Id, string Name);

/// <summary>
/// The public Session projection.  It intentionally has no path, process,
/// socket, lease or exception fields.
/// </summary>
public sealed record SessionView(
    int SchemaVersion,
    string Id,
    string Name,
    SessionGameSummary Game,
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
    string? CloseReason)
{
    /// <summary>Exact catalog face selected for this persistent Session.</summary>
    public string FontFaceId { get; init; } = RuntimeFontDefaults.DefaultFaceId;
}

public sealed record SessionListPage(IReadOnlyList<SessionView> Items, string? NextCursor);

public sealed record SessionCommandFailure(string Code, string Message, int StatusCode, object? Details = null);

public sealed record SessionCommandResult(
    SessionView? Value,
    int StatusCode,
    bool Replayed,
    bool Pending,
    SessionCommandFailure? Failure = null)
{
    public bool Succeeded => Failure is null && Value is not null;
}

public sealed record SessionDeleteResult(
    int StatusCode,
    bool Replayed,
    bool Pending,
    SessionCommandFailure? Failure = null)
{
    public bool Succeeded => Failure is null && !Pending;
}

public interface ISessionApplicationService
{
    Task<SessionCommandResult> CreateAsync(
        CurrentActor actor,
        CreateSessionCommand command,
        CancellationToken cancellationToken = default);

    Task<SessionListPage> ListAsync(
        CurrentActor actor,
        SessionListQuery query,
        CancellationToken cancellationToken = default);

    Task<SessionView?> GetAsync(
        CurrentActor actor,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult> OpenAsync(
        CurrentActor actor,
        SessionLifecycleCommand command,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult> CloseAsync(
        CurrentActor actor,
        SessionLifecycleCommand command,
        CancellationToken cancellationToken = default);

    Task<SessionCommandResult> UpdateConfigurationAsync(
        CurrentActor actor,
        SessionConfigurationCommand command,
        CancellationToken cancellationToken = default);

    Task<SessionDeleteResult> DeleteAsync(
        CurrentActor actor,
        SessionDeleteCommand command,
        CancellationToken cancellationToken = default);
}

public interface ISessionOperationRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken = default);
}

public sealed record CurrentWorkerRoute(SessionRuntimeBinding Binding, IWorkerProcessHandle Process);

/// <summary>
/// The only application port that can resolve a current process handle.  HTTP
/// adapters never enumerate WorkerManager's in-memory collection.
/// </summary>
public interface ICurrentWorkerRouter
{
    Task<CurrentWorkerRoute?> GetCurrentAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

public interface IWorkerOpenOptionsFactory
{
    SessionRuntimeOpenOptions Create(string sessionId, int browserWidth = 0);
}

public interface ISessionLifecycleExecutor
{
    Task<SessionRuntimeOpenResult> OpenAsync(string sessionId, int browserWidth, CancellationToken cancellationToken = default);
    Task<SessionRuntimeCloseResult> CloseAsync(
        string sessionId,
        string reasonCode = "requested",
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeCloseResult> ForceStopAsync(
        string sessionId,
        string expectedWorkerId,
        long expectedWorkerEpoch,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This lifecycle executor does not support administrative force-stop.");
}

/// <summary>
/// Serializes only external lifecycle side effects per Session.  SQLite and
/// the coordinator remain the correctness authority; this executor is merely a
/// duplicate-side-effect suppressor for one API instance.
/// </summary>
public sealed class SessionLifecycleExecutor(
    SessionRuntimeCoordinator coordinator,
    ICurrentWorkerRouter workerRouter,
    IWorkerOpenOptionsFactory optionsFactory)
    : ISessionLifecycleExecutor, ISessionCommandGate
{
    private const int CloseBindingRefreshAttempts = 3;
    private static readonly TimeSpan CloseBindingRefreshDelay = TimeSpan.FromMilliseconds(25);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionGate> gates = new(StringComparer.Ordinal);

    public Task<SessionRuntimeOpenResult> OpenAsync(
        string sessionId,
        CancellationToken cancellationToken = default) => OpenAsync(sessionId, 0, cancellationToken);

    public async Task<SessionRuntimeOpenResult> OpenAsync(
        string sessionId,
        int browserWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionGate gate = await AcquireGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            // Once the caller has entered the side-effect phase, the host must
            // continue even if the HTTP request disconnects.  The service uses
            // WaitAsync for the HTTP budget and calls this operation with a
            // non-request token after its durable begin record is committed.
            return await coordinator.OpenAsync(optionsFactory.Create(sessionId, browserWidth), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate(sessionId, gate);
        }
    }

    public async Task<SessionRuntimeCloseResult> CloseAsync(
        string sessionId,
        string reasonCode = "requested",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionGate gate = await AcquireGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < CloseBindingRefreshAttempts; attempt++)
            {
                CurrentWorkerRoute? route = await workerRouter.GetCurrentAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (route is null)
                {
                    if (attempt > 0)
                        return new SessionRuntimeCloseResult(SessionRuntimeCompletionResult.Stale());

                    throw new SessionApplicationException(
                        SessionErrorCodes.SessionTransitionInProgress,
                        "当前 Session 的 Worker 路由尚未可用。",
                        409);
                }

                SessionRuntimeCloseResult result = await coordinator.CloseAsync(
                    route.Binding,
                    route.Process,
                    new SessionRuntimeCloseRequest(sessionId, Force: false, reasonCode),
                    cancellationToken).ConfigureAwait(false);
                if (result.Completion.Applied ||
                    !string.Equals(result.Completion.ReasonCode, SessionRuntimeResultCodes.WorkerStaleEpoch, StringComparison.Ordinal) ||
                    attempt == CloseBindingRefreshAttempts - 1)
                    return result;

                // A heartbeat may advance the durable state version between
                // GetCurrentAsync and BeginStoppingAsync. Refresh the same
                // Worker route instead of converting that benign race into a
                // permanently failed close command.
                await Task.Delay(CloseBindingRefreshDelay, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The Session close binding refresh loop did not return.");
        }
        finally
        {
            ReleaseGate(sessionId, gate);
        }
    }

    public async Task<SessionRuntimeCloseResult> ForceStopAsync(
        string sessionId,
        string expectedWorkerId,
        long expectedWorkerEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedWorkerId);
        SessionGate gate = await AcquireGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            CurrentWorkerRoute? route = await workerRouter.GetCurrentAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (route is null || !string.Equals(route.Binding.WorkerId, expectedWorkerId, StringComparison.Ordinal) ||
                route.Binding.WorkerEpoch != expectedWorkerEpoch)
            {
                throw new SessionApplicationException(
                    SessionErrorCodes.SessionTransitionInProgress,
                    "当前 Session 的 Worker 代次已变化。",
                    409);
            }

            return await coordinator.CloseAsync(
                route.Binding,
                route.Process,
                new SessionRuntimeCloseRequest(sessionId, Force: true, "admin_force_stopped"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate(sessionId, gate);
        }
    }

    public async Task<SessionCommandLease> EnterAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionGate gate = await AcquireGateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionCommandLease(() => ReleaseGate(sessionId, gate));
    }

    private async Task<SessionGate> AcquireGateAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (true)
        {
            SessionGate gate = gates.GetOrAdd(sessionId, static _ => new SessionGate());
            Interlocked.Increment(ref gate.References);
            if (!gates.TryGetValue(sessionId, out SessionGate? current) || !ReferenceEquals(current, gate))
            {
                Interlocked.Decrement(ref gate.References);
                continue;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return gate;
            }
            catch
            {
                ReleaseReference(sessionId, gate);
                throw;
            }
        }
    }

    private void ReleaseGate(string sessionId, SessionGate gate)
    {
        gate.Semaphore.Release();
        ReleaseReference(sessionId, gate);
    }

    private void ReleaseReference(string sessionId, SessionGate gate)
    {
        if (Interlocked.Decrement(ref gate.References) == 0)
            gates.TryRemove(new KeyValuePair<string, SessionGate>(sessionId, gate));
    }

    private sealed class SessionGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References;
    }
}

public static class SessionIdempotency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Digest(string actorUserId, string scope, string routeResourceId, object body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeResourceId);
        var normalized = new
        {
            schemaVersion = 1,
            actor = actorUserId,
            scope,
            routeResourceId,
            body,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        return $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}
