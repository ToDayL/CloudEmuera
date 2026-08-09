using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Identity;

public sealed class AuthSessionMaintenanceTests
{
    [Fact]
    [Trait("Category", "IdentitySession")]
    public async Task CleanupIsBoundedAndPreservesActiveSessions()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        DateTimeOffset now = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        await using (DbContextScope seed = database.OpenContext())
        {
            CloudEmueraUser user = PersistenceFixtures.CreateUser("usr_cleanup", "CLEANUP");
            seed.Context.AddRange(PersistenceFixtures.CreateQuotaProfile(), user);
            seed.Context.AuthSessions.AddRange(
                Session("auths_expired_1", user.Id, now.AddHours(-3), now.AddHours(-1)),
                Session("auths_expired_2", user.Id, now.AddHours(-2), now.AddMinutes(-30)),
                Session("auths_revoked_3", user.Id, now.AddHours(-1), now.AddHours(1), now.AddMinutes(-10)),
                Session("auths_active_4", user.Id, now.AddMinutes(-5), now.AddHours(1)));
            await seed.Context.SaveChangesAsync();
        }

        await using (DbContextScope first = database.OpenContext())
            Assert.Equal(2, await new AuthSessionMaintenance(first.Context, new FixedTimeProvider(now)).CleanupAsync(2));
        await using (DbContextScope second = database.OpenContext())
            Assert.Equal(1, await new AuthSessionMaintenance(second.Context, new FixedTimeProvider(now)).CleanupAsync(2));
        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(["auths_active_4"], await verify.Context.AuthSessions.Select(session => session.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [Trait("Category", "IdentitySession")]
    public async Task CleanupRejectsUnboundedBatchSizes(int batchSize)
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AuthSessionMaintenance(scope.Context, TimeProvider.System).CleanupAsync(batchSize));
    }

    private static AuthSessionRow Session(string id, string userId, DateTimeOffset created, DateTimeOffset absolute, DateTimeOffset? revoked = null) => new()
    {
        Id = id,
        UserId = userId,
        SecurityStamp = "session-security-stamp",
        CreatedAt = created,
        LastSeenAt = created,
        IdleExpiresAt = absolute,
        AbsoluteExpiresAt = absolute,
        RevokedAt = revoked,
        RevokeReason = revoked is null ? null : "TEST_REVOKED",
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
