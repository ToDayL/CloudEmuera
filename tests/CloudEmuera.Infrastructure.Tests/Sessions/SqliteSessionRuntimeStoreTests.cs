using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Capacity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Infrastructure.Tests.Support;
using CloudEmuera.RuntimeAdapter;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Sessions;

[Trait("Category", "SessionLifecycle")]
[Trait("Category", "WorkerLease")]
public sealed class SqliteSessionRuntimeStoreTests
{
    [Fact]
    public async Task OpenReadyCloseAndReopenPreservesRootBindingAndFencesOldEpoch()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 4, "sess_fixture");
        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System);
        DateTimeOffset now = new(2026, 8, 11, 13, 0, 0, TimeSpan.Zero);

        SessionRuntimeAcquireResult first = await store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_first", "wrk_first", now));
        Assert.True(first.Succeeded);
        SessionRuntimeLease firstLease = first.Lease!;
        Assert.Equal(SessionState.Starting, firstLease.State);
        Assert.Equal(1, firstLease.Binding.WorkerEpoch);
        Assert.Equal("sessions/sess_fixture/root", firstLease.Binding.SessionRootPath);
        Assert.Equal("sha256:" + new string('a', 64), firstLease.Binding.SessionRootManifestDigest);

        WorkerProcessIdentity firstIdentity = new(43101, "00000000-0000-0000-0000-000000000001", 1001);
        SessionRuntimeWriteResult identity = await store.RecordProcessIdentityAsync(firstLease.Binding, firstIdentity, now.AddSeconds(1));
        Assert.True(identity.Applied);
        SessionRuntimeBinding startingBinding = identity.Binding!;
        SessionRuntimeWriteResult ready = await store.MarkReadyAsync(startingBinding, Ready(startingBinding), now.AddSeconds(2));
        Assert.True(ready.Applied);
        SessionRuntimeBinding runningBinding = ready.Binding!;
        SessionRuntimeWriteResult heartbeat = await store.RecordHeartbeatAsync(
            runningBinding,
            new WorkerHeartbeatInfo(firstIdentity, 7, true, "prompt_1", 1024, now.AddSeconds(3)),
            TimeSpan.FromSeconds(30));
        Assert.True(heartbeat.Applied);
        SessionRuntimeBinding heartbeatBinding = heartbeat.Binding!;
        SessionRuntimeWriteResult stopping = await store.BeginStoppingAsync(heartbeatBinding, now.AddSeconds(4));
        Assert.True(stopping.Applied);
        SessionRuntimeBinding stoppingBinding = stopping.Binding!;

        SessionRuntimeCompletionResult closed = await store.CompleteAsync(
            stoppingBinding,
            SessionRuntimeTerminalState.Closed,
            "requested",
            7,
            now.AddSeconds(5));
        Assert.True(closed.Applied);
        Assert.Equal(SessionState.Closed, closed.State);

        SessionRuntimeAcquireResult second = await store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_second", "wrk_second", now.AddSeconds(6)));
        Assert.True(second.Succeeded);
        SessionRuntimeLease secondLease = second.Lease!;
        Assert.Equal(2, secondLease.Binding.WorkerEpoch);
        Assert.Equal(7, secondLease.Binding.InitialOutputSequence);
        Assert.Equal(firstLease.Binding.SessionRootPath, secondLease.Binding.SessionRootPath);
        Assert.Equal(firstLease.Binding.RuntimeManifestJson, secondLease.Binding.RuntimeManifestJson);

        Assert.False((await store.RecordProcessIdentityAsync(firstLease.Binding, firstIdentity, now.AddSeconds(7))).Applied);
        SessionRuntimeCompletionResult stale = await store.CompleteAsync(
            firstLease.Binding,
            SessionRuntimeTerminalState.Crashed,
            "old_worker",
            99,
            now.AddSeconds(8));
        Assert.False(stale.Applied);
        Assert.Equal(SessionState.Crashed, stale.State);

        Assert.True(await store.CompleteAsync(
            secondLease.Binding,
            SessionRuntimeTerminalState.Crashed,
            "test_cleanup",
            7,
            now.AddSeconds(9)) is { Applied: true, State: SessionState.Crashed });

        await using DbContextScope verify = database.OpenContext();
        SessionRow session = await verify.Context.Sessions.SingleAsync(row => row.Id == "sess_fixture");
        Assert.Equal(SessionState.Crashed, session.State);
        Assert.Equal("sessions/sess_fixture/root", session.SessionRootPath);
        Assert.Equal("game_fixture", session.GameId);
        Assert.Equal("sha256:" + new string('a', 64), session.SourceContentDigest);
        Assert.False(await verify.Context.WorkerLeases.AnyAsync(row => row.SessionId == "sess_fixture"));
    }

    [Fact]
    public async Task ConcurrentOpensForTheLastInstanceWorkerSlotHaveOneWinner()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 1, "sess_fixture", "sess_second");
        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System, new InstanceCapacityOptions { MaxActiveWorkers = 1 });
        DateTimeOffset now = new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

        Task<SessionRuntimeAcquireResult> first = store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_quota", "wrk_quota_1", now));
        Task<SessionRuntimeAcquireResult> second = store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_second", "ctl_quota", "wrk_quota_2", now));
        SessionRuntimeAcquireResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Failure == SessionRuntimeAcquireFailure.ActiveWorkerLimitExceeded);

        SessionRuntimeLease winner = results.Single(result => result.Succeeded).Lease!;
        Assert.True((await store.CompleteAsync(
            winner.Binding,
            SessionRuntimeTerminalState.Crashed,
            "test_cleanup",
            0,
            now.AddSeconds(1))).Applied);
    }

    [Fact]
    public async Task ConcurrentOpensForOneSessionCreateOneLeaseAndConsumeOneEpoch()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 32, "sess_fixture");
        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System);
        DateTimeOffset now = new(2026, 8, 11, 15, 0, 0, TimeSpan.Zero);

        SessionRuntimeAcquireResult[] results = await Task.WhenAll(
            Enumerable.Range(1, 16).Select(index => store.TryAcquireOpenLeaseAsync(
                OpenOptions("sess_fixture", "ctl_same_" + index, "wrk_same_" + index, now))));

        SessionRuntimeAcquireResult winner = Assert.Single(results, result => result.Succeeded);
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.True(result.Failure is
                SessionRuntimeAcquireFailure.WorkerAlreadyLeased or
                SessionRuntimeAcquireFailure.SessionNotOpenable));
        Assert.Equal(1, winner.Lease!.Binding.WorkerEpoch);
        Assert.True((await store.CompleteAsync(
            winner.Lease.Binding,
            SessionRuntimeTerminalState.Crashed,
            "test_cleanup",
            0,
            now.AddSeconds(1))).Applied);
    }

    [Fact]
    public async Task BlockedGameRejectsReopenInsideTheOpenTransaction()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 4, "sess_fixture");
        await using (DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWrite))
        {
            GameRow game = await scope.Context.Games.SingleAsync(row => row.Id == "game_fixture");
            game.Status = GameStatus.Blocked;
            game.StateVersion++;
            await scope.Context.SaveChangesAsync();
        }

        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System);
        SessionRuntimeAcquireResult result = await store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_blocked", "wrk_blocked", DateTimeOffset.UtcNow));

        Assert.Equal(SessionRuntimeAcquireFailure.GameBlocked, result.Failure);
        await using DbContextScope verify = database.OpenContext();
        SessionRow session = await verify.Context.Sessions.SingleAsync(row => row.Id == "sess_fixture");
        Assert.Equal(SessionState.Closed, session.State);
        Assert.False(await verify.Context.WorkerLeases.AnyAsync(row => row.SessionId == "sess_fixture"));
    }

    [Fact]
    public async Task SameEpochStaleStateVersionCannotStopOrCompleteAfterHeartbeat()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 4, "sess_fixture");
        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System);
        DateTimeOffset now = new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);

        SessionRuntimeLease lease = (await store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_fence", "wrk_fence", now))).Lease!;
        WorkerProcessIdentity identity = new(43102, "00000000-0000-0000-0000-000000000002", 1002);
        Assert.True((await store.RecordProcessIdentityAsync(lease.Binding, identity, now.AddSeconds(1))).Applied);
        SessionRuntimeBinding running = (await store.MarkReadyAsync(
            lease.Binding,
            Ready(lease.Binding),
            now.AddSeconds(2))).Binding!;
        SessionRuntimeBinding afterHeartbeat = (await store.RecordHeartbeatAsync(
            running,
            new WorkerHeartbeatInfo(identity, running.InitialOutputSequence + 1, true, "prompt_fence", 1024, now.AddSeconds(3)),
            TimeSpan.FromSeconds(30))).Binding!;

        Assert.False((await store.BeginStoppingAsync(running, now.AddSeconds(4))).Applied);
        Assert.False((await store.CompleteAsync(
            running,
            SessionRuntimeTerminalState.Crashed,
            "stale_complete",
            afterHeartbeat.InitialOutputSequence,
            now.AddSeconds(4))).Applied);

        SessionRuntimeBinding stopping = (await store.BeginStoppingAsync(afterHeartbeat, now.AddSeconds(5))).Binding!;
        Assert.True((await store.CompleteAsync(
            stopping,
            SessionRuntimeTerminalState.Closed,
            "requested",
            stopping.InitialOutputSequence,
            now.AddSeconds(6))).Applied);
    }

    [Fact]
    public async Task ReconcileStartingLeaseWithoutProcessIdentityAfterApiRestart()
    {
        using TemporarySqliteDatabase database = new();
        await SeedSessionsAsync(database, quota: 4, "sess_fixture");
        SqliteSessionRuntimeStore store = new(database.Options, TimeProvider.System);
        DateTimeOffset now = new(2026, 8, 11, 17, 0, 0, TimeSpan.Zero);

        SessionRuntimeLease acquired = (await store.TryAcquireOpenLeaseAsync(
            OpenOptions("sess_fixture", "ctl_restarted", "wrk_restarted", now))).Lease!;
        PersistedWorkerLease persisted = Assert.Single(await store.ListPersistedLeasesAsync());
        Assert.Equal(WorkerLeaseStatus.Starting.ToString().ToUpperInvariant(), persisted.Status);
        Assert.Null(persisted.ProcessIdentity);

        Assert.True(await store.ReconcileAsync(persisted, "control_plane_restarted", now.AddSeconds(1)));

        await using DbContextScope verify = database.OpenContext();
        SessionRow session = await verify.Context.Sessions.SingleAsync(row => row.Id == acquired.Binding.SessionId);
        Assert.Equal(SessionState.Crashed, session.State);
        Assert.Equal("control_plane_restarted", session.CloseReason);
        Assert.False(await verify.Context.WorkerLeases.AnyAsync(row => row.SessionId == acquired.Binding.SessionId));
    }

    private static SessionRuntimeOpenOptions OpenOptions(
        string sessionId,
        string controlPlane,
        string workerId,
        DateTimeOffset now) => new(
        sessionId,
        controlPlane,
        workerId,
        "headless-test",
        2,
        $"uds/{workerId}",
        TimeSpan.FromSeconds(30),
        now);

    private static WorkerReadyInfo Ready(SessionRuntimeBinding binding) => new(
        RuntimeBaseline.CloudEmueraIntegrationVersion,
        RuntimeBaseline.UpstreamCommit,
        binding.SaveLayout,
        binding.InitialOutputSequence,
        binding.CompatibilityProfile,
        binding.SessionRootManifestDigest);

    private static async Task SeedSessionsAsync(
        TemporarySqliteDatabase database,
        long quota,
        params string[] sessionIds)
    {
        Assert.NotEmpty(sessionIds);
        MigrationResult migration = await database.MigrateAsync();
        Assert.True(migration.Succeeded, migration.ErrorCode);
        await using DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWrite);
        QuotaProfileRow quotaProfile = PersistenceFixtures.CreateQuotaProfile();
        quotaProfile.MaxActiveSessions = quota;
        scope.Context.QuotaProfiles.Add(quotaProfile);
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame());
        await scope.Context.SaveChangesAsync();
        string gameDirectory = Path.Combine(database.RootPath, "games", "game_fixture");
        GameStorageOwnerMarker.Initialize(gameDirectory, "game_fixture", "usr_fixture");

        foreach (string sessionId in sessionIds)
        {
            SessionRow session = PersistenceFixtures.CreateSession(sessionId);
            session.State = SessionState.Closed;
            session.ClosedAt = PersistenceFixtures.CreatedAt;
            session.RuntimeManifestJson = "{\"compatibilityProfile\":\"v18-compatible\",\"saveLayout\":0,\"manifestDigest\":\"sha256:" + new string('a', 64) + "\"}";
            scope.Context.Sessions.Add(session);
        }

        await scope.Context.SaveChangesAsync();
    }
}
