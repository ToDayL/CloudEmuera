using CloudEmuera.Application.Identity;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Tests.Identity;

/// <summary>AUTH-003/004: SQLite, not an in-memory provider, is the authority for identity races.</summary>
public sealed class LocalIdentityConcurrencyTests
{
    private static readonly string[] UpdatedNames = ["identity-one", "identity-two"];

    [Fact]
    [Trait("Category", "IdentityConcurrency")]
    public async Task ConcurrentFailedLoginsUseAtomicIncrementAndPersistTheThresholdLockout()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedUserAsync(database);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => LoginWithWrongPasswordAsync(database)));

        await using DbContextScope verify = database.OpenContext();
        CloudEmueraUser user = await verify.Context.Users.SingleAsync(value => value.Id == "usr_identity");
        Assert.Equal(5, user.AccessFailedCount);
        Assert.NotNull(user.LockoutEnd);
        Assert.Equal(5, await verify.Context.AuditEvents.CountAsync(value => value.Action == "AUTH_LOGIN_FAILED"));
    }

    [Fact]
    [Trait("Category", "IdentityConcurrency")]
    public async Task ConcurrentAdministrativeUpdatesHaveOneCasWinner()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await SeedUserAsync(database);
        CurrentActor admin = new("usr_admin", "ADMIN", "auths_admin");

        Task<CurrentUser?> first = TryUpdateUsernameAsync(database, "identity-one", admin);
        Task<CurrentUser?> second = TryUpdateUsernameAsync(database, "identity-two", admin);
        CurrentUser?[] attempts = await Task.WhenAll(first, second);

        Assert.Single(attempts, result => result is not null);
        await using DbContextScope verify = database.OpenContext();
        CloudEmueraUser user = await verify.Context.Users.SingleAsync(value => value.Id == "usr_identity");
        Assert.Equal(1, user.StateVersion);
        Assert.Contains(user.LoginName, UpdatedNames);
    }

    [Fact]
    [Trait("Category", "Audit")]
    public async Task AuditInsertFailureRollsBackSensitiveUserCreation()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using (DbContextScope seed = database.OpenContext())
        {
            seed.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
            await seed.Context.SaveChangesAsync();
            await using var command = seed.Connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_identity_audit BEFORE INSERT ON audit_events WHEN NEW.action = 'USER_CREATED' BEGIN SELECT RAISE(ABORT, 'audit_failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        await using (DbContextScope attempt = database.OpenContext())
        {
            LocalIdentityService service = CreateService(attempt.Context);
            await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateUserAsync(
                new CreateUserCommand("rollback-user", "rollback@example.test", "rollback-password", "PLAYER"),
                new CurrentActor("usr_admin", "ADMIN", "auths_admin")));
        }

        await using DbContextScope verify = database.OpenContext();
        Assert.False(await verify.Context.Users.AnyAsync(user => user.NormalizedEmail == "ROLLBACK@EXAMPLE.TEST"));
    }

    [Fact]
    [Trait("Category", "IdentityConcurrency")]
    public async Task ConcurrentAdminDisablesPreserveOneActiveAdministrator()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using (DbContextScope seed = database.OpenContext())
        {
            QuotaProfileRow quota = PersistenceFixtures.CreateQuotaProfile();
            CloudEmueraUser first = PersistenceFixtures.CreateUser("usr_admin_one", "ADMIN-ONE");
            CloudEmueraUser second = PersistenceFixtures.CreateUser("usr_admin_two", "ADMIN-TWO");
            first.Role = UserRole.Admin;
            second.Role = UserRole.Admin;
            seed.Context.AddRange(quota, first, second);
            await seed.Context.SaveChangesAsync();
        }

        bool[] results = await Task.WhenAll(TryDisableAdminAsync(database, "usr_admin_one"), TryDisableAdminAsync(database, "usr_admin_two"));

        Assert.Single(results, result => result);
        await using DbContextScope verify = database.OpenContext();
        Assert.Equal(1, await verify.Context.Users.CountAsync(user => user.Role == UserRole.Admin && user.Status == UserStatus.Active));
    }

    private static async Task SeedUserAsync(TemporarySqliteDatabase database)
    {
        await using DbContextScope scope = database.OpenContext();
        CloudEmueraUser user = PersistenceFixtures.CreateUser("usr_identity", "IDENTITY");
        user.Email = "identity@example.test";
        user.NormalizedEmail = "IDENTITY@EXAMPLE.TEST";
        user.MustChangePassword = false;
        user.PasswordChangedAt = PersistenceFixtures.CreatedAt;
        user.PasswordHash = new PasswordHasher<CloudEmueraUser>().HashPassword(user, "identity-password");
        scope.Context.AddRange(PersistenceFixtures.CreateQuotaProfile(), user);
        await scope.Context.SaveChangesAsync();
    }

    private static async Task LoginWithWrongPasswordAsync(TemporarySqliteDatabase database)
    {
        await using DbContextScope scope = database.OpenContext();
        LocalIdentityService service = CreateService(scope.Context);
        Assert.Null(await service.LoginAsync(new LoginCommand("identity@example.test", "wrong-password", false)));
    }

    private static async Task<CurrentUser?> TryUpdateUsernameAsync(TemporarySqliteDatabase database, string username, CurrentActor admin)
    {
        await using DbContextScope scope = database.OpenContext();
        LocalIdentityService service = CreateService(scope.Context);
        try { return await service.UpdateUserAsync("usr_identity", new UpdateUserCommand(username, null, null, null, 0), admin); }
        catch (IdentityConcurrencyException) { return null; }
    }

    private static async Task<bool> TryDisableAdminAsync(TemporarySqliteDatabase database, string userId)
    {
        await using DbContextScope scope = database.OpenContext();
        try
        {
            await CreateService(scope.Context).UpdateUserAsync(userId, new UpdateUserCommand(null, null, null, "DISABLED", 0), new CurrentActor("usr_operator", "ADMIN", "auths_operator"));
            return true;
        }
        catch (IdentityConflictException exception) when (exception.Code == "LAST_ACTIVE_ADMIN") { return false; }
    }

    private static LocalIdentityService CreateService(CloudEmueraDbContext context) =>
        new(context, new CloudEmueraUserStore(context), new PasswordHasher<CloudEmueraUser>(), TimeProvider.System, NullAuditContext.Instance);
}
