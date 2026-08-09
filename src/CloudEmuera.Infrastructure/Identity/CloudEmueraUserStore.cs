using CloudEmuera.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity adapter for the fixed P1 metadata schema. It maps
/// Identity's username surface to CloudEmuera's display/login-name columns;
/// product authentication deliberately calls the email lookup only.
/// </summary>
public sealed class CloudEmueraUserStore(CloudEmueraDbContext db) :
    IUserStore<CloudEmueraUser>,
    IUserPasswordStore<CloudEmueraUser>,
    IUserEmailStore<CloudEmueraUser>,
    IUserSecurityStampStore<CloudEmueraUser>,
    IUserLockoutStore<CloudEmueraUser>
{
    private bool _disposed;

    public Task<string> GetUserIdAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).Id);

    public Task<string?> GetUserNameAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(User(user, cancellationToken).LoginName);

    public Task SetUserNameAsync(CloudEmueraUser user, string? userName, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).LoginName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(User(user, cancellationToken).NormalizedLoginName);

    public Task SetNormalizedUserNameAsync(CloudEmueraUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).NormalizedLoginName = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> CreateAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        db.Users.Update(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return IdentityResult.Success;
    }

    public Task<IdentityResult> DeleteAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        return Task.FromResult(IdentityResult.Failed(new IdentityError
        {
            Code = "PHYSICAL_USER_DELETE_FORBIDDEN",
            Description = "CloudEmuera users must be disabled rather than physically deleted.",
        }));
    }

    public Task<CloudEmueraUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        Active(cancellationToken);
        return db.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    public Task<CloudEmueraUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        Active(cancellationToken);
        return db.Users.SingleOrDefaultAsync(user => user.NormalizedLoginName == normalizedUserName, cancellationToken);
    }

    public Task SetPasswordHashAsync(CloudEmueraUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).PasswordHash);

    public Task<bool> HasPasswordAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).PasswordHash is not null);

    public Task SetEmailAsync(CloudEmueraUser user, string? email, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).Email);

    public Task<bool> GetEmailConfirmedAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        return Task.FromResult(true);
    }

    public Task SetEmailConfirmedAsync(CloudEmueraUser user, bool confirmed, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        if (!confirmed) throw new NotSupportedException("P1-02 does not model email verification state.");
        return Task.CompletedTask;
    }

    public Task<CloudEmueraUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        Active(cancellationToken);
        return db.Users.SingleOrDefaultAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    public Task<string?> GetNormalizedEmailAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).NormalizedEmail);

    public Task SetNormalizedEmailAsync(CloudEmueraUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    public Task SetSecurityStampAsync(CloudEmueraUser user, string stamp, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).LockoutEnd);

    public Task SetLockoutEndDateAsync(CloudEmueraUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        CloudEmueraUser value = User(user, cancellationToken);
        value.AccessFailedCount++;
        return Task.FromResult(value.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken).AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(CloudEmueraUser user, CancellationToken cancellationToken) =>
        Task.FromResult(User(user, cancellationToken).AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(CloudEmueraUser user, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        return Task.FromResult(true);
    }

    public Task SetLockoutEnabledAsync(CloudEmueraUser user, bool enabled, CancellationToken cancellationToken)
    {
        User(user, cancellationToken);
        if (!enabled) throw new NotSupportedException("Persistent lockout cannot be disabled per user.");
        return Task.CompletedTask;
    }

    public void Dispose() => _disposed = true;

    private CloudEmueraUser User(CloudEmueraUser? user, CancellationToken cancellationToken)
    {
        Active(cancellationToken);
        return user ?? throw new ArgumentNullException(nameof(user));
    }

    private void Active(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
