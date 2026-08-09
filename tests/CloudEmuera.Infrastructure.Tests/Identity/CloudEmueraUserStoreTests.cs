using CloudEmuera.Infrastructure.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Infrastructure.Tests.Support;
using Microsoft.AspNetCore.Identity;

namespace CloudEmuera.Infrastructure.Tests.Identity;

public sealed class CloudEmueraUserStoreTests
{
    [Fact]
    [Trait("Category", "IdentityStore")]
    public async Task StoreMapsIdentityContractsToTheExistingUsersTable()
    {
        using TemporarySqliteDatabase database = new();
        Assert.True((await database.MigrateAsync()).Succeeded);
        await using DbContextScope scope = database.OpenContext();
        scope.Context.QuotaProfiles.Add(PersistenceFixtures.CreateQuotaProfile());
        await scope.Context.SaveChangesAsync();
        CloudEmueraUser user = PersistenceFixtures.CreateUser("usr_store", "STORE");
        user.Email = "store@example.test";
        user.NormalizedEmail = "STORE@EXAMPLE.TEST";
        user.PasswordHash = "identity-hash";
        user.PasswordChangedAt = PersistenceFixtures.CreatedAt;
        CloudEmueraUserStore store = new(scope.Context);

        Assert.True((await store.CreateAsync(user, CancellationToken.None)).Succeeded);
        Assert.Equal(user.Id, (await store.FindByEmailAsync("STORE@EXAMPLE.TEST", CancellationToken.None))?.Id);
        Assert.Equal(user.Id, (await store.FindByNameAsync("STORE", CancellationToken.None))?.Id);
        Assert.True(await store.HasPasswordAsync(user, CancellationToken.None));
        IdentityResult deleted = await store.DeleteAsync(user, CancellationToken.None);
        Assert.False(deleted.Succeeded);
        Assert.Contains(deleted.Errors, error => error.Code == "PHYSICAL_USER_DELETE_FORBIDDEN");
    }
}
