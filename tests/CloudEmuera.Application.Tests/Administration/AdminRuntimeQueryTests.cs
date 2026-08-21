using CloudEmuera.Application.Administration;
using CloudEmuera.Domain.Sessions;
using Xunit;

namespace CloudEmuera.Application.Tests.Administration;

[Trait("Category", "AdminDiagnostics")]
public sealed class AdminRuntimeQueryTests
{
    [Fact]
    public async Task MergesDurableLeaseAndApiWorkerFactsWithBoundedFields()
    {
        DateTimeOffset observedAt = new(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);
        AdminPersistentSession session = ActiveSession(observedAt);
        AdminRuntimeHostSnapshot host = new(
            "instance_1",
            "READY",
            [new AdminHostWorker(
                session.Id,
                "worker_1",
                7,
                431,
                Registered: true,
                Ready: true,
                ProcessExited: false,
                observedAt.AddSeconds(-3),
                LastOutputSequence: 19,
                DroppedPendingEventCount: 2,
                new AdminRealtimeHostSnapshot("LIVE", 19, 4096, "KNOWN", 1, 2, 3, 4, 5, 2))],
            WebSocketConnectionCount: 2,
            SubscriptionCount: 1,
            WriteFenceUnconfirmedSessionIds: new HashSet<string>(StringComparer.Ordinal));
        AdminRuntimeSnapshot result = await new AdminRuntimeQuery(
            new Store(new AdminPersistentRuntimeSnapshot([session], [])),
            new Diagnostics(host),
            new FixedTimeProvider(observedAt)).ReadAsync(new AdminRuntimeQueryOptions());

        AdminWorkerSnapshot worker = Assert.Single(result.Workers);
        Assert.Equal("MATCHED", worker.RuntimeConsistency);
        Assert.Equal(3000, worker.Worker.HeartbeatAgeMilliseconds);
        Assert.Equal(431, worker.Worker.Pid);
        Assert.Equal(19, worker.Worker.LastOutputSequence);
        Assert.Equal(4096, worker.Realtime.SnapshotBytes);
        Assert.Equal(3, worker.Realtime.SoftOverflowCount);
        Assert.Equal(2, result.Instance.WebSocketConnectionCount);
        Assert.Equal(1, result.Instance.SubscriptionCount);
    }

    [Fact]
    public async Task IsolatesAnUnconfirmedWriteFenceInsteadOfMakingTheWholeInstanceNotReady()
    {
        DateTimeOffset observedAt = new(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);
        AdminPersistentSession session = ActiveSession(observedAt);
        AdminRuntimeHostSnapshot host = new(
            "instance_1",
            "READY",
            [new AdminHostWorker(
                session.Id,
                "worker_1",
                7,
                431,
                Registered: false,
                Ready: false,
                ProcessExited: true,
                HeartbeatAt: null,
                LastOutputSequence: 0,
                DroppedPendingEventCount: 0,
                new AdminRealtimeHostSnapshot("DISPOSED", 0, null, "NOT_READY", 0, 0, 0, 0, 0, 0))],
            0,
            0,
            new HashSet<string>([session.Id], StringComparer.Ordinal));

        AdminRuntimeSnapshot result = await new AdminRuntimeQuery(
            new Store(new AdminPersistentRuntimeSnapshot([session], [])),
            new Diagnostics(host),
            new FixedTimeProvider(observedAt)).ReadAsync(new AdminRuntimeQueryOptions());

        Assert.Equal("READY", result.Instance.ControlPlaneState);
        Assert.Equal("WRITE_FENCE_UNCONFIRMED", Assert.Single(result.Workers).RuntimeConsistency);
    }

    [Fact]
    public async Task DoesNotMatchFactsFromAnOlderControlPlaneInstance()
    {
        DateTimeOffset observedAt = new(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);
        AdminPersistentSession session = ActiveSession(observedAt);
        AdminRuntimeHostSnapshot host = new(
            "instance_2",
            "READY",
            [new AdminHostWorker(
                session.Id,
                "worker_1",
                7,
                431,
                Registered: true,
                Ready: true,
                ProcessExited: false,
                observedAt.AddSeconds(-3),
                LastOutputSequence: 19,
                DroppedPendingEventCount: 0,
                new AdminRealtimeHostSnapshot("LIVE", 19, 4096, "KNOWN", 0, 0, 0, 0, 0, 0))],
            0,
            0,
            new HashSet<string>(StringComparer.Ordinal));

        AdminRuntimeSnapshot result = await new AdminRuntimeQuery(
            new Store(new AdminPersistentRuntimeSnapshot([session], [])),
            new Diagnostics(host),
            new FixedTimeProvider(observedAt)).ReadAsync(new AdminRuntimeQueryOptions());

        Assert.Equal("STALE_IN_MEMORY", Assert.Single(result.Workers).RuntimeConsistency);
    }

    private static AdminPersistentSession ActiveSession(DateTimeOffset observedAt) => new(
        "sess_1",
        "Session One",
        "owner",
        "game_1",
        "Game One",
        SessionState.Running,
        4,
        7,
        "worker_1",
        7,
        "ACTIVE",
        431,
        "instance_1",
        observedAt.AddSeconds(-3),
        observedAt.AddSeconds(-4),
        19);

    private sealed class Store(AdminPersistentRuntimeSnapshot snapshot) : IAdminRuntimeStore
    {
        public Task<AdminPersistentRuntimeSnapshot> ReadRuntimeAsync(AdminRuntimeQueryOptions options, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<AdminSessionTarget?> ReadSessionTargetAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromResult<AdminSessionTarget?>(null);
        public Task<bool> HasAuditAsync(string action, string sessionId, string actorUserId, string idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> ReadRequestedReasonAsync(string sessionId, string actorUserId, string idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task AppendAuditAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdminIdempotencyRecord> BeginIdempotencyAsync(string actorUserId, string scope, string key, string requestDigest, string resourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteIdempotencySuccessAsync(string actorUserId, string scope, string key, string requestDigest, int responseStatus, string responseJson, string resourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteIdempotencyFailureAsync(string actorUserId, string scope, string key, string requestDigest, int responseStatus, string errorCode, string responseJson, string resourceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AdminPendingIdempotency>> ListPendingIdempotencyAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AdminPendingIdempotency>>([]);
    }

    private sealed class Diagnostics(AdminRuntimeHostSnapshot snapshot) : IAdminRuntimeDiagnostics
    {
        public Task<AdminRuntimeHostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
