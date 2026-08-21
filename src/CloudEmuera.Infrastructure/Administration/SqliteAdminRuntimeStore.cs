using System.Text.Json;
using CloudEmuera.Application.Administration;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Sessions;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Administration;

/// <summary>
/// Administrative reads are intentionally short, no-tracking SQLite queries.
/// The store is a projection boundary: it never returns SessionRoot, IPC or
/// process identity paths to the API contract.
/// </summary>
public sealed class SqliteAdminRuntimeStore(
    SqliteDatabaseOptions databaseOptions,
    TimeProvider timeProvider,
    SqliteIdempotencyStore idempotency) : IAdminRuntimeStore
{
    public async Task<AdminPersistentRuntimeSnapshot> ReadRuntimeAsync(
        AdminRuntimeQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        int limit = options.NormalizedRecentFailureLimit;
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);

        IReadOnlyList<PersistentActiveRow> activeRows = await db.Sessions
            .AsNoTracking()
            .Where(row => row.State == SessionState.Starting || row.State == SessionState.Running || row.State == SessionState.Stopping)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .Select(row => new PersistentActiveRow(
                row.Id,
                row.Name,
                row.OwnerUser == null ? "" : row.OwnerUser.LoginName,
                row.GameId,
                row.Game == null ? "" : row.Game.Name,
                row.State,
                row.StateVersion,
                row.WorkerEpoch,
                row.WorkerLease == null ? null : row.WorkerLease.WorkerId,
                row.WorkerLease == null ? null : row.WorkerLease.Epoch,
                row.WorkerLease == null ? null : (int?)row.WorkerLease.Status,
                row.WorkerLease == null ? null : row.WorkerLease.Pid == null ? null : (int?)row.WorkerLease.Pid,
                row.WorkerLease == null ? null : row.WorkerLease.ControlPlaneInstanceId,
                row.WorkerLease == null ? null : row.WorkerLease.HeartbeatAt,
                row.LastActivityAt,
                row.LastOutputSequence))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<AdminPersistentSession> active = activeRows
            .Select(row => new AdminPersistentSession(
                row.Id, row.Name, row.OwnerUsername, row.GameId, row.GameName, row.State, row.StateVersion,
                row.WorkerEpoch, row.LeaseWorkerId, row.LeaseEpoch,
                row.LeaseStatus is null ? null : ((WorkerLeaseStatus)row.LeaseStatus.Value).ToString().ToUpperInvariant(),
                row.Pid, row.ControlPlaneInstanceId, row.HeartbeatAt, row.LastActivityAt, row.LastOutputSequence))
            .ToArray();

        IReadOnlyList<AdminPersistentFailure> failures = await db.Sessions
            .AsNoTracking()
            .Where(row => row.State == SessionState.Crashed)
            .OrderByDescending(row => row.ClosedAt ?? row.LastActivityAt)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(row => new AdminPersistentFailure(
                row.Id,
                row.Name,
                row.OwnerUser == null ? "" : row.OwnerUser.LoginName,
                row.GameId,
                row.Game == null ? "" : row.Game.Name,
                row.WorkerEpoch,
                row.ClosedAt,
                string.IsNullOrWhiteSpace(row.CloseReason) ? "unknown" : row.CloseReason!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new AdminPersistentRuntimeSnapshot(active, failures);
    }

    public async Task<AdminSessionTarget?> ReadSessionTargetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        var row = await db.Sessions
            .AsNoTracking()
            .Where(value => value.Id == sessionId)
            .Select(value => new
            {
                value.Id,
                value.OwnerUserId,
                value.GameId,
                GameName = value.Game == null ? "" : value.Game.Name,
                value.SourceContentDigest,
                value.SourceContentRevision,
                value.RuntimeVersion,
                value.FontSize,
                value.LineHeight,
                value.State,
                value.StateVersion,
                value.WorkerEpoch,
                value.WaitingForInput,
                value.CreatedAt,
                value.StartedAt,
                value.LastActivityAt,
                value.ClosedAt,
                value.CloseReason,
                LeaseWorkerId = value.WorkerLease == null ? null : value.WorkerLease.WorkerId,
                LeaseEpoch = value.WorkerLease == null ? null : (long?)value.WorkerLease.Epoch,
                ControlPlane = value.WorkerLease == null ? null : value.WorkerLease.ControlPlaneInstanceId,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return null;

        SessionView view = new(
            1,
            row.Id,
            // The name is selected separately below to keep the anonymous
            // projection compatible with SQLite providers that do not support
            // nullable navigation projections of complex records.
            await ReadSessionNameAsync(db, row.Id, cancellationToken).ConfigureAwait(false),
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
        return new AdminSessionTarget(row.Id, view, row.OwnerUserId, row.LeaseWorkerId, row.LeaseEpoch, row.State, row.ControlPlane);
    }

    public async Task<bool> HasAuditAsync(
        string action,
        string sessionId,
        string actorUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        IReadOnlyList<string> metadata = await db.AuditEvents
            .AsNoTracking()
            .Where(row => row.Action == action && row.ResourceId == sessionId && row.ActorUserId == actorUserId)
            .OrderByDescending(row => row.OccurredAt)
            .Take(32)
            .Select(row => row.MetadataJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (string value in metadata)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                if (document.RootElement.TryGetProperty("idempotencyKey", out JsonElement key) &&
                    string.Equals(key.GetString(), idempotencyKey, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // A malformed historical metadata row cannot authorize a new
                // command and is therefore treated as absent.
            }
        }
        return false;
    }

    public async Task<string?> ReadRequestedReasonAsync(
        string sessionId,
        string actorUserId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        IReadOnlyList<string> metadata = await db.AuditEvents
            .AsNoTracking()
            .Where(row => row.Action == AdminAuditActions.ForceStopRequested && row.ResourceId == sessionId && row.ActorUserId == actorUserId)
            .OrderByDescending(row => row.OccurredAt)
            .Take(32)
            .Select(row => row.MetadataJson)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (string value in metadata)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                if (!document.RootElement.TryGetProperty("idempotencyKey", out JsonElement key) ||
                    !string.Equals(key.GetString(), idempotencyKey, StringComparison.Ordinal))
                    continue;
                if (document.RootElement.TryGetProperty("reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String)
                    return reason.GetString();
            }
            catch (JsonException)
            {
                // Malformed historical metadata is not a recoverable command envelope.
            }
        }
        return null;
    }

    public async Task AppendAuditAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using SqliteConnection connection = OpenConnection();
        await using CloudEmueraDbContext db = CreateContext(connection);
        db.AuditEvents.Add(new AuditEventRow
        {
            Id = $"audit_{Guid.CreateVersion7():N}",
            OccurredAt = timeProvider.GetUtcNow(),
            ActorUserId = entry.Actor.UserId,
            ActorType = AuditActorType.Admin,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            RequestId = entry.RequestId,
            Result = string.Equals(entry.Result, "SUCCEEDED", StringComparison.Ordinal)
                ? AuditResult.Succeeded
                : AuditResult.Failed,
            ReasonCode = entry.ReasonCode,
            MetadataJson = entry.MetadataJson,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminIdempotencyRecord> BeginIdempotencyAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        PersistentIdempotencyRecord result = await idempotency.BeginAsync(
            actorUserId, scope, key, requestDigest, resourceId, cancellationToken).ConfigureAwait(false);
        return new AdminIdempotencyRecord(
            result.State.ToString().ToUpperInvariant(),
            result.RequestDigest,
            result.ResponseStatus,
            result.ResponseJson,
            result.ResourceId,
            result.ErrorCode);
    }

    public Task CompleteIdempotencySuccessAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string responseJson,
        string resourceId,
        CancellationToken cancellationToken = default) => idempotency.CompleteSuccessAsync(
            actorUserId, scope, key, requestDigest, responseStatus, responseJson, resourceId, cancellationToken);

    public Task CompleteIdempotencyFailureAsync(
        string actorUserId,
        string scope,
        string key,
        string requestDigest,
        int responseStatus,
        string errorCode,
        string responseJson,
        string resourceId,
        CancellationToken cancellationToken = default) => idempotency.CompleteFailureAsync(
            actorUserId, scope, key, requestDigest, responseStatus, errorCode, responseJson, resourceId, cancellationToken);

    public async Task<IReadOnlyList<AdminPendingIdempotency>> ListPendingIdempotencyAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = OpenConnection(SqliteConnectionAccess.ReadOnly);
        await using CloudEmueraDbContext db = CreateContext(connection);
        return await db.IdempotencyRecords
            .AsNoTracking()
            .Where(row => row.Scope == AdminCommandScopes.ForceStop && row.Status == IdempotencyRecordStatus.InProgress && row.ResourceId != null)
            .OrderBy(row => row.CreatedAt)
            .Select(row => new AdminPendingIdempotency(row.ActorUserId, row.Scope, row.IdempotencyKey, row.RequestDigest, row.ResourceId!))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadSessionNameAsync(CloudEmueraDbContext db, string sessionId, CancellationToken cancellationToken) =>
        await db.Sessions.AsNoTracking().Where(row => row.Id == sessionId).Select(row => row.Name).SingleAsync(cancellationToken).ConfigureAwait(false);

    private SqliteConnection OpenConnection(SqliteConnectionAccess access = SqliteConnectionAccess.ReadWrite) =>
        new SqliteConnectionFactory(databaseOptions, createDataRoot: false).OpenConnection(access);

    private static CloudEmueraDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CloudEmueraDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsHistoryTable(SqliteStorageConventions.MigrationHistoryTable))
            .Options);

    private sealed record PersistentActiveRow(
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
        int? LeaseStatus,
        int? Pid,
        string? ControlPlaneInstanceId,
        DateTimeOffset? HeartbeatAt,
        DateTimeOffset LastActivityAt,
        long LastOutputSequence);
}
