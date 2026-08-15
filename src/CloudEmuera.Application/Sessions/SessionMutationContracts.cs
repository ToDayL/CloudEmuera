namespace CloudEmuera.Application.Sessions;

public enum SessionRootMutationPurpose
{
    SaveImport,
    SaveRename,
    SaveDelete,
    SaveCopy,
}

public enum SessionRootMutationAcquireFailure
{
    None,
    SessionNotFound,
    SessionNotQuiescent,
    WorkerLeaseActive,
    MutationLeaseActive,
    RecoveryRequired,
    InvalidRequest,
}

public sealed record SessionRootMutationLease(
    string SessionId,
    string OperationId,
    string ActorUserId,
    SessionRootMutationPurpose Purpose,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt);

public sealed record SessionRootMutationAcquireResult(
    SessionRootMutationAcquireFailure Failure,
    SessionRootMutationLease? Lease = null)
{
    public bool Succeeded => Failure == SessionRootMutationAcquireFailure.None && Lease is not null;
}

/// <summary>
/// Durable write-authority boundary for future stopped-state SessionRoot file
/// operations. Implementations must linearize acquisition against WorkerLease
/// creation in the same SQLite write transaction.
/// </summary>
public interface ISessionRootMutationLeaseStore
{
    Task<SessionRootMutationAcquireResult> TryAcquireAsync(
        string sessionId,
        string actorUserId,
        string operationId,
        SessionRootMutationPurpose purpose,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(
        SessionRootMutationLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(
        SessionRootMutationLease lease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases only an expired lease owned by the specified durable
    /// operation. Ordinary request paths must use <see cref="ReleaseAsync"/>;
    /// this method is reserved for crash recovery after the operation facts
    /// have been checked.
    /// </summary>
    Task<bool> ReleaseExpiredAsync(
        string sessionId,
        string operationId,
        CancellationToken cancellationToken = default);
}
