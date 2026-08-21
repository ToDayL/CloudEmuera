namespace CloudEmuera.Contracts.Administration;

public sealed record AdminRuntimeResponse(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    AdminInstanceResponse Instance,
    IReadOnlyList<AdminWorkerResponse> Workers,
    IReadOnlyList<AdminFailureResponse> RecentFailures);

public sealed record AdminInstanceResponse(
    string ControlPlaneState,
    int ActiveWorkerCount,
    int WebSocketConnectionCount,
    int SubscriptionCount);

public sealed record AdminWorkerResponse(
    AdminSessionResponse Session,
    AdminWorkerProcessResponse Worker,
    AdminRealtimeResponse Realtime,
    string RuntimeConsistency);

public sealed record AdminSessionResponse(
    string Id,
    string Name,
    string OwnerUsername,
    string GameId,
    string GameName,
    string State,
    int StateVersion,
    DateTimeOffset LastActivityAt);

public sealed record AdminWorkerProcessResponse(
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

public sealed record AdminRealtimeResponse(
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

public sealed record AdminFailureResponse(
    string SessionId,
    string SessionName,
    string OwnerUsername,
    string GameId,
    string GameName,
    long WorkerEpoch,
    DateTimeOffset? FailedAt,
    string ReasonCode);

public sealed record AdminForceStopResponse(string Reason);
