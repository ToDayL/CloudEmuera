using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Application.Games;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.RuntimeAdapter;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Sessions;

/// <summary>
/// Short-transaction SQLite authority for Session open/close and WorkerLease
/// fencing. No process, file-system or IPC operation is performed while the
/// immediate write transaction is held.
/// </summary>
public sealed class SqliteSessionRuntimeStore(
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider,
    InstanceCapacityOptions? capacityOptions = null) : ISessionRuntimeStore, ICurrentSessionRuntimeLeaseReader
{
    public async Task<SessionRuntimeAcquireResult> TryAcquireOpenLeaseAsync(
        SessionRuntimeOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenOptions(options);
        string? gameId = await ReadSessionGameIdAsync(options.SessionId, cancellationToken).ConfigureAwait(false);
        if (gameId is null)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.SessionNotFound);

        FileStream gameMutationLock;
        try
        {
            gameMutationLock = await AcquireGameMutationLockAsync(gameId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException or UnauthorizedAccessException or InvalidDataException or ArgumentException or GameLibraryException)
        {
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.InvalidConfiguration);
        }
        await using (gameMutationLock.ConfigureAwait(false))
        {
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);

        SessionRow? session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == options.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.SessionNotFound);
        if (!session.State.CanOpen())
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.SessionNotOpenable);
        GameStatus? gameStatus = await db.Games.AsNoTracking()
            .Where(row => row.Id == session.GameId)
            .Select(row => (GameStatus?)row.Status)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (gameStatus is null or GameStatus.Deleted)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.SessionNotOpenable);
        if (gameStatus == GameStatus.Blocked)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.GameBlocked);
        if (await db.WorkerLeases.AnyAsync(row => row.SessionId == session.Id, cancellationToken).ConfigureAwait(false))
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.WorkerAlreadyLeased);
        DateTimeOffset now = options.Now == default ? timeProvider.GetUtcNow() : options.Now;
        // An expired mutation row remains a recovery barrier. Only the save
        // operation recovery path may inspect its marker and release it.
        if (await db.SessionRootMutationLeases.AnyAsync(row => row.SessionId == session.Id, cancellationToken).ConfigureAwait(false))
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.MutationLeaseActive);

        InstanceCapacityOptions capacity = capacityOptions ?? InstanceCapacityOptions.Default;
        if (capacity.MaxActiveWorkers <= 0)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.InvalidConfiguration);
        long active = await db.Sessions
            .CountAsync(row => row.State == SessionState.Starting || row.State == SessionState.Running || row.State == SessionState.Stopping, cancellationToken)
            .ConfigureAwait(false);
        if (active >= capacity.MaxActiveWorkers)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.ActiveWorkerLimitExceeded);

        if (session.WorkerEpoch == long.MaxValue)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.InvalidConfiguration);

        long nextEpoch = session.WorkerEpoch + 1;
        int nextStateVersion = checked(session.StateVersion + 1);
        int updated = await db.Sessions
            .Where(row => row.Id == session.Id && row.StateVersion == session.StateVersion && row.WorkerEpoch == session.WorkerEpoch &&
                (row.State == SessionState.Closed || row.State == SessionState.Crashed))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, SessionState.Starting)
                .SetProperty(row => row.StateVersion, nextStateVersion)
                .SetProperty(row => row.WorkerEpoch, nextEpoch)
                .SetProperty(row => row.WaitingForInput, false)
                .SetProperty(row => row.CurrentPromptId, (string?)null)
                .SetProperty(row => row.CloseReason, (string?)null)
                .SetProperty(row => row.ClosedAt, (DateTimeOffset?)null)
                .SetProperty(row => row.LastActivityAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
            return new SessionRuntimeAcquireResult(SessionRuntimeAcquireFailure.WorkerAlreadyLeased);

        session.State = SessionState.Starting;
        session.StateVersion = nextStateVersion;
        session.WorkerEpoch = nextEpoch;
        session.WaitingForInput = false;
        session.CurrentPromptId = null;
        session.CloseReason = null;
        session.ClosedAt = null;
        session.LastActivityAt = now;

        var lease = new WorkerLeaseRow
        {
            SessionId = session.Id,
            WorkerId = options.WorkerId,
            Epoch = nextEpoch,
            Status = WorkerLeaseStatus.Starting,
            Pid = null,
            ControlPlaneInstanceId = options.ControlPlaneInstanceId,
            ProcessBootId = null,
            ProcessStartTicks = null,
            IpcEndpoint = options.IpcEndpoint,
            RuntimeVersion = options.RuntimeVersion,
            ProtocolVersion = options.ProtocolVersion,
            AcquiredAt = now,
            HeartbeatAt = now,
            ExpiresAt = now.Add(options.LeaseDuration),
        };
        db.WorkerLeases.Add(lease);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        SessionRuntimeBinding binding = new(
            session.Id,
            lease.WorkerId,
            nextEpoch,
            nextStateVersion,
            options.ControlPlaneInstanceId,
            session.SessionRootPath,
            RuntimeBaseline.CompatibilityProfile,
            session.SaveLayout,
            session.SessionRootManifestDigest,
            session.RuntimeVersion,
            session.LastOutputSequence,
            session.OwnerUserId,
            session.GameId,
            session.SourceContentRevision,
            session.SourceContentDigest);
        return SessionRuntimeAcquireResult.Success(new SessionRuntimeLease(binding, session.OwnerUserId, session.State, now, lease.ExpiresAt, session.FontSize, session.LineHeight));
        }
    }

    private async Task<string?> ReadSessionGameIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        return await db.Sessions.AsNoTracking()
            .Where(row => row.Id == sessionId)
            .Select(row => row.GameId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FileStream> AcquireGameMutationLockAsync(string gameId, CancellationToken cancellationToken)
    {
        if (gameId.Length is < 6 or > 64 || !gameId.StartsWith("game_", StringComparison.Ordinal) ||
            gameId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("The Game ID is invalid.", nameof(gameId));
        string root = Path.GetFullPath(databaseOptions.DataRoot);
        string directory = Path.GetFullPath(Path.Combine(root, "games", gameId));
        if (!RuntimePathUtilities.IsStrictlyWithin(directory, root) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        RuntimePathUtilities.ValidateNoReparsePointsAlongPath(directory, "game-storage");
        GameStorageOwnerMarker.Validate(directory, gameId);

        string path = Path.Combine(directory, ".mutation.lock");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<SessionRuntimeWriteResult> RecordProcessIdentityAsync(
        SessionRuntimeBinding binding,
        WorkerProcessIdentity identity,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        identity.Validate();
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        WorkerLeaseRow? lease = await FindLeaseAsync(db, binding, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(row => row.Id == binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null || session is null || lease.Status != WorkerLeaseStatus.Starting || session.StateVersion != binding.StateVersion)
            return SessionRuntimeWriteResult.Stale();
        if (lease.Pid is not null)
            return SameIdentity(lease, identity) ? SessionRuntimeWriteResult.Accepted(binding) : SessionRuntimeWriteResult.Stale();
        lease.Pid = identity.ProcessId;
        lease.ProcessBootId = identity.ProcessBootId;
        lease.ProcessStartTicks = identity.ProcessStartTicks;
        lease.HeartbeatAt = observedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SessionRuntimeWriteResult.Accepted(binding);
    }

    public async Task<SessionRuntimeWriteResult> MarkReadyAsync(
        SessionRuntimeBinding binding,
        WorkerReadyInfo ready,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateReady(binding, ready);
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        WorkerLeaseRow? lease = await FindLeaseAsync(db, binding, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(row => row.Id == binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null || session is null || lease.Status != WorkerLeaseStatus.Starting || session.State != SessionState.Starting ||
            session.StateVersion != binding.StateVersion || lease.Pid is null || lease.ProcessBootId is null || lease.ProcessStartTicks is null)
            return SessionRuntimeWriteResult.Stale();
        if (!string.Equals(ready.CompatibilityProfile, binding.CompatibilityProfile, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(binding.SessionRootManifestDigest) &&
             !string.Equals(ready.SessionRootManifestDigest, binding.SessionRootManifestDigest, StringComparison.OrdinalIgnoreCase)) ||
            ready.LastOutputSequence < session.LastOutputSequence)
            return SessionRuntimeWriteResult.Stale();

        session.State = SessionState.Running;
        session.StateVersion = checked(session.StateVersion + 1);
        session.StartedAt = observedAt;
        session.LastActivityAt = observedAt;
        session.LastOutputSequence = Math.Max(session.LastOutputSequence, ready.LastOutputSequence);
        lease.Status = WorkerLeaseStatus.Active;
        lease.HeartbeatAt = observedAt;
        lease.ExpiresAt = observedAt.Add(GetLeaseDuration(lease));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SessionRuntimeWriteResult.Accepted(binding with
        {
            StateVersion = session.StateVersion,
            InitialOutputSequence = session.LastOutputSequence,
        });
    }

    public async Task<SessionRuntimeWriteResult> RecordHeartbeatAsync(
        SessionRuntimeBinding binding,
        WorkerHeartbeatInfo heartbeat,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        heartbeat.ProcessIdentity.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        if ((heartbeat.WaitingForInput && string.IsNullOrWhiteSpace(heartbeat.CurrentPromptId)) ||
            (!heartbeat.WaitingForInput && !string.IsNullOrEmpty(heartbeat.CurrentPromptId)) || heartbeat.OutputSequence < 0 ||
            heartbeat.ResidentMemoryBytes < 0)
            throw new ArgumentException("The Worker heartbeat is invalid.", nameof(heartbeat));
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        WorkerLeaseRow? lease = await FindLeaseAsync(db, binding, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(row => row.Id == binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null || session is null || lease.Status != WorkerLeaseStatus.Active || session.State != SessionState.Running ||
            session.StateVersion != binding.StateVersion || !SameIdentity(lease, heartbeat.ProcessIdentity))
            return SessionRuntimeWriteResult.Stale();

        bool stateChanged = session.WaitingForInput != heartbeat.WaitingForInput ||
            !string.Equals(session.CurrentPromptId, heartbeat.CurrentPromptId, StringComparison.Ordinal) ||
            heartbeat.OutputSequence > session.LastOutputSequence;
        session.LastActivityAt = heartbeat.ObservedAt;
        session.LastOutputSequence = Math.Max(session.LastOutputSequence, heartbeat.OutputSequence);
        session.WaitingForInput = heartbeat.WaitingForInput;
        session.CurrentPromptId = heartbeat.WaitingForInput ? heartbeat.CurrentPromptId : null;
        if (stateChanged)
            session.StateVersion = checked(session.StateVersion + 1);
        lease.HeartbeatAt = heartbeat.ObservedAt;
        lease.ExpiresAt = heartbeat.ObservedAt.Add(leaseDuration);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SessionRuntimeWriteResult.Accepted(binding with
        {
            StateVersion = session.StateVersion,
            InitialOutputSequence = session.LastOutputSequence,
        });
    }

    public async Task<SessionRuntimeWriteResult> BeginStoppingAsync(
        SessionRuntimeBinding binding,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        WorkerLeaseRow? lease = await FindLeaseAsync(db, binding, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(row => row.Id == binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null || session is null || session.State is not (SessionState.Starting or SessionState.Running) ||
            session.StateVersion != binding.StateVersion || lease.Status is not (WorkerLeaseStatus.Starting or WorkerLeaseStatus.Active))
            return SessionRuntimeWriteResult.Stale();
        session.State = SessionState.Stopping;
        session.StateVersion = checked(session.StateVersion + 1);
        session.WaitingForInput = false;
        session.CurrentPromptId = null;
        session.LastActivityAt = observedAt;
        lease.Status = WorkerLeaseStatus.Stopping;
        lease.HeartbeatAt = observedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SessionRuntimeWriteResult.Accepted(binding with { StateVersion = session.StateVersion });
    }

    public async Task<SessionRuntimeCompletionResult> CompleteAsync(
        SessionRuntimeBinding binding,
        SessionRuntimeTerminalState terminalState,
        string reasonCode,
        long lastOutputSequence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(terminalState) || string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A terminal state and reason code are required.");
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        WorkerLeaseRow? lease = await FindLeaseAsync(db, binding, cancellationToken).ConfigureAwait(false);
        SessionRow? session = await db.Sessions.SingleOrDefaultAsync(row => row.Id == binding.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return SessionRuntimeCompletionResult.Stale(SessionRuntimeResultCodes.SessionNotFound);
        if (lease is null)
        {
            if (session.State.IsQuiescent())
                return new SessionRuntimeCompletionResult(false, session.State, session.CloseReason ?? reasonCode);
            return SessionRuntimeCompletionResult.Stale();
        }
        if (session.WorkerEpoch != binding.WorkerEpoch || !string.Equals(lease.WorkerId, binding.WorkerId, StringComparison.Ordinal) ||
            !string.Equals(lease.ControlPlaneInstanceId, binding.ControlPlaneInstanceId, StringComparison.Ordinal) ||
            session.StateVersion != binding.StateVersion)
            return SessionRuntimeCompletionResult.Stale();

        session.State = terminalState == SessionRuntimeTerminalState.Closed ? SessionState.Closed : SessionState.Crashed;
        session.StateVersion = checked(session.StateVersion + 1);
        session.WaitingForInput = false;
        session.CurrentPromptId = null;
        session.CloseReason = NormalizeReason(reasonCode);
        session.ClosedAt = observedAt;
        session.LastActivityAt = observedAt;
        session.LastOutputSequence = Math.Max(session.LastOutputSequence, Math.Max(0, lastOutputSequence));
        db.WorkerLeases.Remove(lease);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionRuntimeCompletionResult(true, session.State, session.CloseReason);
    }

    public async Task<IReadOnlyList<PersistedWorkerLease>> ListPersistedLeasesAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        return await db.WorkerLeases.AsNoTracking()
            .Join(db.Sessions.AsNoTracking(), lease => lease.SessionId, session => session.Id, (lease, session) => new PersistedWorkerLease(
                new SessionRuntimeBinding(
                    session.Id,
                    lease.WorkerId,
                    lease.Epoch,
                    session.StateVersion,
                    lease.ControlPlaneInstanceId,
                    session.SessionRootPath,
                    RuntimeBaseline.CompatibilityProfile,
                    session.SaveLayout,
                    session.SessionRootManifestDigest,
                    session.RuntimeVersion,
                    session.LastOutputSequence,
                    session.OwnerUserId,
                    session.GameId,
                    session.SourceContentRevision,
                    session.SourceContentDigest),
                lease.Pid != null && lease.ProcessBootId != null && lease.ProcessStartTicks != null
                    ? new WorkerProcessIdentity(lease.Pid.Value, lease.ProcessBootId, lease.ProcessStartTicks.Value)
                    : null,
                lease.Status.ToString().ToUpperInvariant(),
                lease.AcquiredAt,
                lease.HeartbeatAt,
                lease.ExpiresAt,
                session.State))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionRuntimeLease?> GetCurrentLeaseAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A Session ID is required.", nameof(sessionId));
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        WorkerLeaseRow? lease = await db.WorkerLeases.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SessionId == sessionId, cancellationToken)
            .ConfigureAwait(false);
        SessionRow? session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null || session is null || lease.Status != WorkerLeaseStatus.Active || session.State != SessionState.Running)
            return null;

        SessionRuntimeBinding binding = new(
            session.Id,
            lease.WorkerId,
            lease.Epoch,
            session.StateVersion,
            lease.ControlPlaneInstanceId,
            session.SessionRootPath,
            RuntimeBaseline.CompatibilityProfile,
            session.SaveLayout,
            session.SessionRootManifestDigest,
            session.RuntimeVersion,
            session.LastOutputSequence,
            session.OwnerUserId,
            session.GameId,
            session.SourceContentRevision,
            session.SourceContentDigest);
        return new SessionRuntimeLease(binding, session.OwnerUserId, session.State, lease.AcquiredAt, lease.ExpiresAt, session.FontSize, session.LineHeight);
    }

    public async Task<bool> ReconcileAsync(
        PersistedWorkerLease lease,
        string reasonCode,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        SessionRuntimeCompletionResult result = await CompleteAsync(
            lease.Binding,
            SessionRuntimeTerminalState.Crashed,
            reasonCode,
            lease.Binding.InitialOutputSequence,
            observedAt,
            cancellationToken).ConfigureAwait(false);
        return result.Applied || result.State == SessionState.Crashed;
    }

    private SqliteConnection OpenConnection(SqliteConnectionAccess access = SqliteConnectionAccess.ReadWrite) =>
        new SqliteConnectionFactory(databaseOptions, createDataRoot: false).OpenConnection(access);

    private static CloudEmueraDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options);

    private static Task<WorkerLeaseRow?> FindLeaseAsync(CloudEmueraDbContext db, SessionRuntimeBinding binding, CancellationToken cancellationToken) =>
        db.WorkerLeases.SingleOrDefaultAsync(row => row.SessionId == binding.SessionId && row.Epoch == binding.WorkerEpoch &&
            row.WorkerId == binding.WorkerId && row.ControlPlaneInstanceId == binding.ControlPlaneInstanceId, cancellationToken);

    private static bool SameIdentity(WorkerLeaseRow lease, WorkerProcessIdentity identity) =>
        lease.Pid == identity.ProcessId && string.Equals(lease.ProcessBootId, identity.ProcessBootId, StringComparison.Ordinal) &&
        lease.ProcessStartTicks == identity.ProcessStartTicks;

    private static TimeSpan GetLeaseDuration(WorkerLeaseRow lease) =>
        lease.ExpiresAt - lease.HeartbeatAt > TimeSpan.Zero ? lease.ExpiresAt - lease.HeartbeatAt : TimeSpan.FromSeconds(5);

    private static void ValidateOpenOptions(SessionRuntimeOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SessionId) || !options.SessionId.StartsWith("sess_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.ControlPlaneInstanceId) || !options.ControlPlaneInstanceId.StartsWith("ctl_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.WorkerId) || !options.WorkerId.StartsWith("wrk_", StringComparison.Ordinal) ||
            options.ProtocolVersion <= 0 || string.IsNullOrWhiteSpace(options.RuntimeVersion) || string.IsNullOrWhiteSpace(options.IpcEndpoint) ||
            options.LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("The Worker open options are invalid.", nameof(options));
    }

    private static void ValidateReady(SessionRuntimeBinding binding, WorkerReadyInfo ready)
    {
        ArgumentNullException.ThrowIfNull(ready);
        bool valid = !string.IsNullOrWhiteSpace(ready.RuntimeIntegrationVersion) &&
            !string.IsNullOrWhiteSpace(ready.UpstreamCommit) &&
            string.Equals(ready.RuntimeIntegrationVersion, RuntimeBaseline.CloudEmueraIntegrationVersion, StringComparison.Ordinal) &&
            string.Equals(ready.UpstreamCommit, RuntimeBaseline.UpstreamCommit, StringComparison.Ordinal) &&
            ready.SaveLayout is >= 0 and <= 1 && ready.SaveLayout == binding.SaveLayout && ready.LastOutputSequence >= 0 &&
            string.Equals(ready.CompatibilityProfile, binding.CompatibilityProfile, StringComparison.Ordinal);
        if (!valid)
            throw new ArgumentException("The Worker ready event is invalid.", nameof(ready));
    }

    private static string NormalizeReason(string value)
    {
        string normalized = new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "worker_finished" : normalized[..Math.Min(128, normalized.Length)];
    }

}
