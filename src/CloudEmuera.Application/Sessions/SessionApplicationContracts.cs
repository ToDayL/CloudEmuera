using System.Text.Json;
using System.Text.Json.Serialization;
using CloudEmuera.Application.Identity;
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
    public const string ActiveSessionQuotaExceeded = "ACTIVE_SESSION_QUOTA_EXCEEDED";
    public const string ControlPlaneDraining = "CONTROL_PLANE_DRAINING";
    public const string ServiceNotReady = "SERVICE_NOT_READY";
    public const string StorageBudgetExceeded = "SESSION_STORAGE_QUOTA_EXCEEDED";
    public const string StorageUnavailable = "DATA_ROOT_UNAVAILABLE";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string SessionNotAcceptingInput = "SESSION_NOT_ACCEPTING_INPUT";
    public const string MutationInProgress = "SESSION_MUTATION_IN_PROGRESS";
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

public sealed record CreateSessionCommand(string GameId, string Name, string IdempotencyKey);

public sealed record SessionLifecycleCommand(string SessionId, string IdempotencyKey);

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
    SessionState State,
    int StateVersion,
    long WorkerEpoch,
    bool WaitingForInput,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason);

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
    SessionRuntimeOpenOptions Create(string sessionId);
}

public interface ISessionLifecycleExecutor
{
    Task<SessionRuntimeOpenResult> OpenAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeCloseResult> CloseAsync(
        string sessionId,
        string reasonCode = "requested",
        CancellationToken cancellationToken = default);
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
    : ISessionLifecycleExecutor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionGate> gates = new(StringComparer.Ordinal);

    public async Task<SessionRuntimeOpenResult> OpenAsync(
        string sessionId,
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
            return await coordinator.OpenAsync(optionsFactory.Create(sessionId), cancellationToken).ConfigureAwait(false);
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
            CurrentWorkerRoute? route = await workerRouter.GetCurrentAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (route is null)
            {
                throw new SessionApplicationException(
                    SessionErrorCodes.SessionTransitionInProgress,
                    "当前 Session 的 Worker 路由尚未可用。",
                    409);
            }

            return await coordinator.CloseAsync(
                route.Binding,
                route.Process,
                new SessionRuntimeCloseRequest(sessionId, Force: false, reasonCode),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate(sessionId, gate);
        }
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
