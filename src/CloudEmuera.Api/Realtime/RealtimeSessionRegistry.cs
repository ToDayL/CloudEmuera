using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;

namespace CloudEmuera.Api.Realtime;

public sealed record RealtimeSubscriptionRoute(
    string SessionId,
    string WorkerId,
    ulong WorkerEpoch,
    string CapabilityDigest,
    RealtimeSubscription Subscription);

/// <summary>
/// API-local adapter over the Worker Manager.  It captures the current Worker
/// binding once for a subscription and resolves the binding again for every
/// input; a stale route never follows an epoch replacement.
/// </summary>
public interface IRealtimeSessionRegistry
{
    Task<RealtimeSubscriptionRoute?> TrySubscribeAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionInputResult> DispatchInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<RealtimeInputDispatch> BeginInputAsync(
        SessionInputCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The command gate only protects creation of this dispatch. Awaiting the
/// Worker receipt happens after the gate is released, so close can reach its
/// durable BeginStopping linearization point without waiting for IPC.
/// </summary>
public sealed record RealtimeInputDispatch(Task<SessionInputResult> Completion);

public static class RealtimeSessionResults
{
    public static SessionInputResult Error(
        SessionInputCommand command,
        string status,
        string? reasonCode = null) =>
        new(null, command.ClientMessageId, status, reasonCode ?? status);

    public static SessionInputResult WorkerUnavailable(SessionInputCommand command) =>
        Error(command, SessionInputResultCodes.WorkerUnavailable);
}
