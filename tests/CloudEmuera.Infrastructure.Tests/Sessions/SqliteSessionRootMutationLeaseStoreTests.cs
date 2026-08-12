using CloudEmuera.Application.Sessions;
using CloudEmuera.Application.Sessions.Runtime;
using CloudEmuera.Domain.Sessions;
using CloudEmuera.Infrastructure.Games;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Sessions;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Sessions;

[Trait("Category", "SessionLifecycle")]
public sealed class SqliteSessionRootMutationLeaseStoreTests
{
    [Fact]
    public async Task ClosedSessionMutationLeaseBlocksOpenAndReleasesExactlyByOwner()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedClosedSessionAsync(database);
        SqliteSessionRootMutationLeaseStore mutations = new(database.Options, TimeProvider.System);

        SessionRootMutationAcquireResult acquired = await mutations.TryAcquireAsync(
            "sess_fixture",
            "usr_fixture",
            "mut_fixture",
            SessionRootMutationPurpose.SaveImport,
            TimeSpan.FromMinutes(5));
        Assert.True(acquired.Succeeded, acquired.Failure.ToString());
        Assert.Equal(SessionRootMutationPurpose.SaveImport, acquired.Lease!.Purpose);

        SessionRootMutationAcquireResult duplicate = await mutations.TryAcquireAsync(
            "sess_fixture",
            "usr_fixture",
            "mut_other",
            SessionRootMutationPurpose.SaveRename,
            TimeSpan.FromMinutes(5));
        Assert.Equal(SessionRootMutationAcquireFailure.MutationLeaseActive, duplicate.Failure);

        SqliteSessionRuntimeStore runtime = new(database.Options, TimeProvider.System);
        SessionRuntimeAcquireResult open = await runtime.TryAcquireOpenLeaseAsync(new SessionRuntimeOpenOptions(
            "sess_fixture",
            "ctl_fixture",
            "wrk_fixture",
            "headless-test",
            2,
            "uds/wrk_fixture",
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow));
        Assert.Equal(SessionRuntimeAcquireFailure.MutationLeaseActive, open.Failure);

        Assert.False(await mutations.ReleaseAsync(acquired.Lease with { OperationId = "mut_other" }));
        Assert.True(await mutations.ReleaseAsync(acquired.Lease));
        Assert.False(await mutations.ReleaseAsync(acquired.Lease));
        Assert.True((await runtime.TryAcquireOpenLeaseAsync(new SessionRuntimeOpenOptions(
            "sess_fixture",
            "ctl_fixture",
            "wrk_fixture",
            "headless-test",
            2,
            "uds/wrk_fixture",
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow))).Succeeded);
    }

    [Fact]
    public async Task MutationLeaseDoesNotCrossOwnerBoundary()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedClosedSessionAsync(database);
        SqliteSessionRootMutationLeaseStore mutations = new(database.Options, TimeProvider.System);

        SessionRootMutationAcquireResult result = await mutations.TryAcquireAsync(
            "sess_fixture",
            "usr_other",
            "mut_fixture",
            SessionRootMutationPurpose.SaveDelete,
            TimeSpan.FromMinutes(5));
        Assert.Equal(SessionRootMutationAcquireFailure.SessionNotFound, result.Failure);
    }

    [Fact]
    public async Task ExpiredMutationLeaseIsReclaimedWithoutBlockingAnewOperation()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedClosedSessionAsync(database);
        await using (DbContextScope scope = database.OpenContext(SqliteConnectionAccess.ReadWrite))
        {
            scope.Context.SessionRootMutationLeases.Add(new SessionRootMutationLeaseRow
            {
                SessionId = "sess_fixture",
                OperationId = "mut_expired",
                ActorUserId = "usr_fixture",
                Purpose = "SAVE_IMPORT",
                AcquiredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await scope.Context.SaveChangesAsync();
        }

        SqliteSessionRootMutationLeaseStore mutations = new(database.Options, TimeProvider.System);
        SessionRootMutationAcquireResult acquired = await mutations.TryAcquireAsync(
            "sess_fixture",
            "usr_fixture",
            "mut_reclaimed",
            SessionRootMutationPurpose.SaveRename,
            TimeSpan.FromMinutes(5));

        Assert.True(acquired.Succeeded, acquired.Failure.ToString());
        await using DbContextScope verify = database.OpenContext();
        SessionRootMutationLeaseRow row = await verify.Context.SessionRootMutationLeases.SingleAsync();
        Assert.Equal("mut_reclaimed", row.OperationId);
    }

    private static async Task SeedClosedSessionAsync(TemporarySqliteDatabase database)
    {
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        scope.Context.Games.Add(PersistenceFixtures.CreateGame());
        SessionRow session = PersistenceFixtures.CreateSession();
        session.State = SessionState.Closed;
        session.StateVersion = 1;
        session.ClosedAt = PersistenceFixtures.CreatedAt;
        scope.Context.Sessions.Add(session);
        await scope.Context.SaveChangesAsync();
        GameStorageOwnerMarker.Initialize(Path.Combine(database.RootPath, "games", "game_fixture"), "game_fixture", "usr_fixture");
    }
}
