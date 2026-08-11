using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Application.Sessions.Runtime;

/// <summary>
/// Stable result codes shared by the lifecycle coordinator and its adapters.
/// They are intentionally independent from HTTP, gRPC and operating-system
/// exception text so a later API surface can expose deterministic errors.
/// </summary>
public static class SessionRuntimeResultCodes
{
    public const string Accepted = "accepted";
    public const string SessionNotFound = "session_not_found";
    public const string SessionNotOpenable = "session_not_openable";
    public const string ActiveSessionQuotaExceeded = "active_session_quota_exceeded";
    public const string WorkerLimitExceeded = "worker_limit_exceeded";
    public const string WorkerStartFailed = "worker_start_failed";
    public const string WorkerRegistrationTimeout = "worker_registration_timeout";
    public const string WorkerReadyTimeout = "worker_ready_timeout";
    public const string WorkerStaleEpoch = "worker_stale_epoch";
    public const string WorkerExitUnconfirmed = "worker_exit_unconfirmed";
    public const string SessionRootInvalid = "session_root_invalid";
    public const string ControlPlaneDraining = "control_plane_draining";
    public const string ControlPlaneReconciliationFailed = "control_plane_reconciliation_failed";
    public const string ProcessIdentityMismatch = "process_identity_mismatch";
    public const string InvalidBinding = "invalid_binding";
}

public sealed class SessionRuntimeException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record WorkerProcessIdentity(long ProcessId, string ProcessBootId, long ProcessStartTicks)
{
    public bool IsComplete => ProcessId > 0 && !string.IsNullOrWhiteSpace(ProcessBootId) && ProcessStartTicks > 0;

    public void Validate()
    {
        if (!IsComplete)
            throw new ArgumentException("A Worker process identity must contain PID, boot ID and start ticks.");
    }
}

public sealed record SessionRuntimeOpenOptions(
    string SessionId,
    string ControlPlaneInstanceId,
    string WorkerId,
    string RuntimeVersion,
    int ProtocolVersion,
    string IpcEndpoint,
    TimeSpan LeaseDuration,
    DateTimeOffset Now);

public sealed record SessionRuntimeBinding(
    string SessionId,
    string WorkerId,
    long WorkerEpoch,
    int StateVersion,
    string ControlPlaneInstanceId,
    string SessionRootPath,
    string CompatibilityProfile,
    int SaveLayout,
    string SessionRootManifestDigest,
    string RuntimeVersion,
    long InitialOutputSequence,
    string RuntimeManifestJson);

public sealed record SessionRuntimeLease(
    SessionRuntimeBinding Binding,
    string OwnerUserId,
    SessionState State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

public enum SessionRuntimeAcquireFailure
{
    None,
    SessionNotFound,
    SessionNotOpenable,
    ActiveSessionQuotaExceeded,
    WorkerAlreadyLeased,
    InvalidConfiguration,
}

public sealed record SessionRuntimeAcquireResult(
    SessionRuntimeAcquireFailure Failure,
    SessionRuntimeLease? Lease = null)
{
    public bool Succeeded => Failure == SessionRuntimeAcquireFailure.None && Lease is not null;

    public static SessionRuntimeAcquireResult Success(SessionRuntimeLease lease) => new(SessionRuntimeAcquireFailure.None, lease);
}

public sealed record WorkerReadyInfo(
    string RuntimeIntegrationVersion,
    string UpstreamCommit,
    int SaveLayout,
    long LastOutputSequence,
    string CompatibilityProfile,
    string SessionRootManifestDigest);

public sealed record WorkerHeartbeatInfo(
    WorkerProcessIdentity ProcessIdentity,
    long OutputSequence,
    bool WaitingForInput,
    string? CurrentPromptId,
    long ResidentMemoryBytes,
    DateTimeOffset ObservedAt);

public enum SessionRuntimeTerminalState
{
    Closed,
    Crashed,
}

public sealed record WorkerExitInfo(
    int? ExitCode,
    bool ProcessExited,
    bool Graceful,
    string ReasonCode,
    long LastOutputSequence,
    DateTimeOffset ObservedAt);

public sealed record SessionRuntimeCompletionResult(bool Applied, SessionState State, string ReasonCode)
{
    public static SessionRuntimeCompletionResult Stale(string reasonCode = SessionRuntimeResultCodes.WorkerStaleEpoch) =>
        new(false, SessionState.Crashed, reasonCode);
}

public sealed record SessionRuntimeWriteResult(bool Applied, SessionRuntimeBinding? Binding)
{
    public static SessionRuntimeWriteResult Stale() => new(false, null);

    public static SessionRuntimeWriteResult Accepted(SessionRuntimeBinding binding) => new(true, binding);
}

public sealed record SessionRootRuntimeDescriptor(
    string AbsoluteSessionRoot,
    int SaveLayout,
    string ManifestDigest,
    string CompatibilityProfile);

public sealed record WorkerLaunchSpec(
    SessionRuntimeBinding Binding,
    SessionRootRuntimeDescriptor SessionRoot,
    DateTimeOffset RegistrationDeadline,
    TimeSpan HeartbeatInterval,
    TimeSpan ShutdownGracePeriod,
    TimeSpan DisconnectGracePeriod,
    TimeSpan RuntimeInitializationTimeout,
    TimeSpan RuntimeExecutionTimeout,
    long ExpectedParentProcessId,
    string ControlSocketPath);

public interface ISessionRuntimeStore
{
    Task<SessionRuntimeAcquireResult> TryAcquireOpenLeaseAsync(
        SessionRuntimeOpenOptions options,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeWriteResult> RecordProcessIdentityAsync(
        SessionRuntimeBinding binding,
        WorkerProcessIdentity identity,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeWriteResult> MarkReadyAsync(
        SessionRuntimeBinding binding,
        WorkerReadyInfo ready,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeWriteResult> RecordHeartbeatAsync(
        SessionRuntimeBinding binding,
        WorkerHeartbeatInfo heartbeat,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeWriteResult> BeginStoppingAsync(
        SessionRuntimeBinding binding,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<SessionRuntimeCompletionResult> CompleteAsync(
        SessionRuntimeBinding binding,
        SessionRuntimeTerminalState terminalState,
        string reasonCode,
        long lastOutputSequence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PersistedWorkerLease>> ListPersistedLeasesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> ReconcileAsync(
        PersistedWorkerLease lease,
        string reasonCode,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);
}

public sealed record PersistedWorkerLease(
    SessionRuntimeBinding Binding,
    WorkerProcessIdentity? ProcessIdentity,
    string Status,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    SessionState SessionState);

public interface ISessionRootRuntimeInspector
{
    Task<SessionRootRuntimeDescriptor> InspectAsync(
        SessionRuntimeLease lease,
        CancellationToken cancellationToken = default);
}

public interface IWorkerProcessHandle : IAsyncDisposable
{
    WorkerProcessIdentity Identity { get; }
    Task<WorkerReadyInfo> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<WorkerExitInfo> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task RequestStopAsync(string reasonCode, DateTimeOffset deadline, CancellationToken cancellationToken = default);
    Task KillAsync(CancellationToken cancellationToken = default);
    void UpdateRuntimeBinding(SessionRuntimeBinding binding, bool persistenceReady = false);
}

public interface ISessionWorkerControl
{
    Task<IWorkerProcessHandle> StartAsync(
        WorkerLaunchSpec spec,
        CancellationToken cancellationToken = default);
}

public sealed record SessionRuntimeOpenRequest(string SessionId);

public sealed record SessionRuntimeCloseRequest(
    string SessionId,
    bool Force,
    string ReasonCode = "requested");

public sealed record SessionRuntimeOpenResult(SessionRuntimeLease Lease, WorkerReadyInfo Ready);

public sealed record SessionRuntimeCloseResult(SessionRuntimeCompletionResult Completion);

/// <summary>
/// Application-level lifecycle orchestration. It owns transaction/external
/// side-effect ordering, while persistence and process adapters remain
/// replaceable and free of HTTP/gRPC types.
/// </summary>
public sealed class SessionRuntimeCoordinator(
    ISessionRuntimeStore store,
    ISessionWorkerControl workerControl,
    ISessionRootRuntimeInspector rootInspector,
    TimeProvider timeProvider)
{
    private int draining;

    public bool IsDraining => Volatile.Read(ref draining) != 0;

    public void BeginDraining() => Interlocked.Exchange(ref draining, 1);

    public async Task<SessionRuntimeOpenResult> OpenAsync(
        SessionRuntimeOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        if (IsDraining)
            throw new SessionRuntimeException(SessionRuntimeResultCodes.ControlPlaneDraining, "The control plane is draining.");

        SessionRuntimeAcquireResult acquired = await store.TryAcquireOpenLeaseAsync(options, cancellationToken).ConfigureAwait(false);
        if (!acquired.Succeeded)
            throw new SessionRuntimeException(MapAcquireFailure(acquired.Failure), "The Session cannot be opened.");

        SessionRuntimeLease lease = acquired.Lease!;
        IWorkerProcessHandle? process = null;
        SessionRuntimeBinding binding = lease.Binding;
        try
        {
            SessionRootRuntimeDescriptor root = await rootInspector.InspectAsync(lease, cancellationToken).ConfigureAwait(false);
            binding = binding with
            {
                SessionRootPath = root.AbsoluteSessionRoot,
                SaveLayout = root.SaveLayout,
                SessionRootManifestDigest = root.ManifestDigest,
                CompatibilityProfile = root.CompatibilityProfile,
            };
            process = await workerControl.StartAsync(
                new WorkerLaunchSpec(
                    binding,
                    root,
                    timeProvider.GetUtcNow().AddSeconds(10),
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(30),
                    Timeout.InfiniteTimeSpan,
                    Environment.ProcessId,
                    string.Empty),
                cancellationToken).ConfigureAwait(false);
            SessionRuntimeWriteResult identityResult = await store.RecordProcessIdentityAsync(
                binding,
                process.Identity,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!identityResult.Applied || identityResult.Binding is null)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerStaleEpoch, "The Worker lease changed during launch.");
            binding = identityResult.Binding;
            process.UpdateRuntimeBinding(binding);

            WorkerReadyInfo ready = await process.WaitForReadyAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            SessionRuntimeWriteResult readyResult = await store.MarkReadyAsync(
                binding,
                ready,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!readyResult.Applied || readyResult.Binding is null)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerStaleEpoch, "The Worker ready event was stale.");
            binding = readyResult.Binding;
            process.UpdateRuntimeBinding(binding, persistenceReady: true);
            return new SessionRuntimeOpenResult(
                lease with { Binding = binding, State = SessionState.Running },
                ready);
        }
        catch (Exception exception)
        {
            if (process is not null)
            {
                try
                {
                    await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
                    WorkerExitInfo exit = await process.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    if (!exit.ProcessExited)
                        throw new InvalidOperationException("The Worker did not confirm process exit.");
                }
                catch
                {
                    // Keep the lease active when exit cannot be proven. The
                    // next reconciliation pass must fail closed in that case.
                    throw new SessionRuntimeException(
                        SessionRuntimeResultCodes.WorkerExitUnconfirmed,
                        "The Worker exit could not be confirmed.");
                }
            }

            string failureReason = exception is SessionRuntimeException runtimeException
                ? runtimeException.Code
                : SessionRuntimeResultCodes.WorkerStartFailed;
            if (string.Equals(failureReason, SessionRuntimeResultCodes.WorkerExitUnconfirmed, StringComparison.Ordinal))
                throw;
            await store.CompleteAsync(
                binding,
                SessionRuntimeTerminalState.Crashed,
                failureReason,
                binding.InitialOutputSequence,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (process is not null)
                await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<SessionRuntimeCloseResult> CloseAsync(
        SessionRuntimeBinding binding,
        IWorkerProcessHandle process,
        SessionRuntimeCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SessionId, binding.SessionId, StringComparison.Ordinal))
            throw new SessionRuntimeException(SessionRuntimeResultCodes.InvalidBinding, "The close request does not match the Worker binding.");
        SessionRuntimeWriteResult stoppingResult = await store.BeginStoppingAsync(
            binding,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!stoppingResult.Applied || stoppingResult.Binding is null)
            return new SessionRuntimeCloseResult(SessionRuntimeCompletionResult.Stale());
        binding = stoppingResult.Binding;
        process.UpdateRuntimeBinding(binding, persistenceReady: true);

        SessionRuntimeTerminalState terminalState = SessionRuntimeTerminalState.Closed;
        string reasonCode = request.ReasonCode;
        long lastOutputSequence = binding.InitialOutputSequence;
        try
        {
            await process.RequestStopAsync(
                request.Force ? "admin_force_stopped" : reasonCode,
                timeProvider.GetUtcNow().AddSeconds(request.Force ? 5 : 15),
                cancellationToken).ConfigureAwait(false);
            WorkerExitInfo exit = await process.WaitForExitAsync(TimeSpan.FromSeconds(request.Force ? 5 : 15), cancellationToken).ConfigureAwait(false);
            lastOutputSequence = Math.Max(lastOutputSequence, exit.LastOutputSequence);
            if (!exit.ProcessExited)
            {
                terminalState = SessionRuntimeTerminalState.Crashed;
                reasonCode = request.Force ? "admin_force_stopped" : SessionRuntimeResultCodes.WorkerExitUnconfirmed;
                await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
                WorkerExitInfo killedExit = await process.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (!killedExit.ProcessExited)
                    throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerExitUnconfirmed, "The Worker exit could not be confirmed.");
            }
        }
        catch
        {
            terminalState = SessionRuntimeTerminalState.Crashed;
            reasonCode = request.Force ? "admin_force_stopped" : "worker_stop_failed";
            await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
            WorkerExitInfo killedExit = await process.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            if (!killedExit.ProcessExited)
                throw new SessionRuntimeException(SessionRuntimeResultCodes.WorkerExitUnconfirmed, "The Worker exit could not be confirmed.");
        }

        SessionRuntimeCompletionResult result = await store.CompleteAsync(
            binding,
            terminalState,
            reasonCode,
            lastOutputSequence,
            timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
        return new SessionRuntimeCloseResult(result);
    }

    private static string MapAcquireFailure(SessionRuntimeAcquireFailure failure) => failure switch
    {
        SessionRuntimeAcquireFailure.SessionNotFound => SessionRuntimeResultCodes.SessionNotFound,
        SessionRuntimeAcquireFailure.SessionNotOpenable => SessionRuntimeResultCodes.SessionNotOpenable,
        SessionRuntimeAcquireFailure.ActiveSessionQuotaExceeded => SessionRuntimeResultCodes.ActiveSessionQuotaExceeded,
        SessionRuntimeAcquireFailure.WorkerAlreadyLeased => SessionRuntimeResultCodes.WorkerStaleEpoch,
        SessionRuntimeAcquireFailure.InvalidConfiguration => SessionRuntimeResultCodes.SessionNotOpenable,
        _ => SessionRuntimeResultCodes.InvalidBinding,
    };
}
