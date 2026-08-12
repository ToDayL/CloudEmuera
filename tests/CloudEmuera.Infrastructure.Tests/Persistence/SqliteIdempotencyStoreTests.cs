using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;

namespace CloudEmuera.Infrastructure.Tests.Persistence;

[Trait("Category", "SessionLifecycle")]
public sealed class SqliteIdempotencyStoreTests
{
    [Fact]
    public async Task SameScopeAndKeyReplaysTerminalFailureButDifferentScopesRemainIndependent()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedUserAsync(database);
        SqliteIdempotencyStore store = new(database.Options, TimeProvider.System);
        string digest = "sha256:" + new string('a', 64);

        PersistentIdempotencyRecord started = await store.BeginAsync("usr_fixture", "SESSION_OPEN", "key-1", digest, resourceId: "sess_fixture");
        Assert.Equal(PersistentIdempotencyBeginState.Started, started.State);
        Assert.Equal("sess_fixture", started.ResourceId);

        PersistentIdempotencyRecord inProgress = await store.BeginAsync("usr_fixture", "SESSION_OPEN", "key-1", digest, resourceId: "sess_fixture");
        Assert.Equal(PersistentIdempotencyBeginState.InProgress, inProgress.State);

        PersistentIdempotencyRecord independent = await store.BeginAsync("usr_fixture", "SESSION_CLOSE", "key-1", digest, resourceId: "sess_fixture");
        Assert.Equal(PersistentIdempotencyBeginState.Started, independent.State);

        PersistentIdempotencyRecord conflict = await store.BeginAsync(
            "usr_fixture",
            "SESSION_OPEN",
            "key-1",
            "sha256:" + new string('b', 64),
            resourceId: "sess_fixture");
        Assert.Equal(PersistentIdempotencyBeginState.Conflict, conflict.State);

        await store.CompleteFailureAsync(
            "usr_fixture",
            "SESSION_OPEN",
            "key-1",
            digest,
            409,
            "SESSION_TRANSITION_IN_PROGRESS",
            "{\"code\":\"SESSION_TRANSITION_IN_PROGRESS\"}",
            resourceId: "sess_fixture");

        PersistentIdempotencyRecord replay = await store.BeginAsync("usr_fixture", "SESSION_OPEN", "key-1", digest, resourceId: "sess_fixture");
        Assert.Equal(PersistentIdempotencyBeginState.Failed, replay.State);
        Assert.Equal("SESSION_TRANSITION_IN_PROGRESS", replay.ErrorCode);

        await using DbContextScope verify = database.OpenContext();
        IdempotencyRecordRow row = await verify.Context.IdempotencyRecords.FindAsync("usr_fixture", "SESSION_OPEN", "key-1")
            ?? throw new Xunit.Sdk.XunitException("The idempotency row was not persisted.");
        Assert.Equal(IdempotencyRecordStatus.Failed, row.Status);
        Assert.Equal("sess_fixture", row.ResourceId);
        Assert.NotNull(row.CompletedAt);
    }

    private static async Task SeedUserAsync(TemporarySqliteDatabase database)
    {
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        scope.Context.Users.Add(PersistenceFixtures.CreateUser());
        await scope.Context.SaveChangesAsync();
    }
}
