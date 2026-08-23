using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudEmuera.Application.Auditing;
using CloudEmuera.Application.Fonts;
using CloudEmuera.Application.Identity;
using CloudEmuera.Infrastructure.Persistence;
using CloudEmuera.Domain.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Identity;

public sealed class IdentityConflictException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class IdentityConcurrencyException : Exception
{
    public IdentityConcurrencyException() : base("STATE_VERSION_CONFLICT") { }
}

public sealed class LocalIdentityService(
    CloudEmueraDbContext db,
    CloudEmueraUserStore userStore,
    IPasswordHasher<CloudEmueraUser> passwordHasher,
    TimeProvider timeProvider,
    IAuditContext auditContext,
    IRuntimeFontCatalog? fontCatalog = null) : ILocalIdentityService
{
    private const int LockoutThreshold = 5;
    private const string SessionStartupDefaultsKey = "sessionStartupDefaults";
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions PreferencesJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private readonly string _dummyHash = passwordHasher.HashPassword(new CloudEmueraUser(), "not-a-user-password");

    public async Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        string normalized;
        try { normalized = IdentityValidation.NormalizeEmail(command.Email); }
        catch (IdentityValidationException) { await VerifyDummyAndAuditAsync(cancellationToken); return null; }
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        CloudEmueraUser? user = await userStore.FindByEmailAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            await VerifyDummyAndAuditAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        bool locked = user.LockoutEnd is { } lockoutEnd && lockoutEnd > now;
        PasswordVerificationResult verify = user.PasswordHash is null ? PasswordVerificationResult.Failed : passwordHasher.VerifyHashedPassword(user, user.PasswordHash, command.Password);
        if (locked || user.Status != UserStatus.Active || verify == PasswordVerificationResult.Failed)
        {
            if (!locked && user.Status == UserStatus.Active)
            {
                // The increment and threshold decision are one SQL statement.  It
                // cannot lose failures when different API processes authenticate
                // the same account at the same time.
                await db.Users.Where(value => value.Id == user.Id && value.Status == UserStatus.Active && (value.LockoutEnd == null || value.LockoutEnd <= now))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.AccessFailedCount, value => value.AccessFailedCount + 1)
                        .SetProperty(value => value.LockoutEnd, value => value.AccessFailedCount + 1 >= LockoutThreshold ? now.Add(LockoutDuration) : value.LockoutEnd)
                        .SetProperty(value => value.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
            }
            AddAudit(AuditActions.LoginFailed, "USER", user.Id, "FAILED", "USER", user.Id, "INVALID_CREDENTIALS");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = now;
        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, command.Password);
            user.PasswordChangedAt = now;
            user.SecurityStamp = NewStamp();
            await RevokeUserSessionsAsync(user.Id, now, "PASSWORD_REHASH", cancellationToken).ConfigureAwait(false);
        }
        LoginResult result = CreateSession(user, command.RememberMe, now);
        AddAudit(AuditActions.LoginSucceeded, "USER", user.Id, "SUCCEEDED", user.Role == UserRole.Admin ? "ADMIN" : "USER", user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<CurrentUser?> GetCurrentUserAsync(string userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().Where(user => user.Id == userId && user.Status == UserStatus.Active).Select(user => ToCurrent(user)).SingleOrDefaultAsync(cancellationToken);

    public async Task<SessionStartupDefaults> GetSessionStartupDefaultsAsync(CurrentActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        CloudEmueraUser? user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == actor.UserId && value.Status == UserStatus.Active, cancellationToken)
            .ConfigureAwait(false);
        return user is null ? throw new KeyNotFoundException() : ReadSessionStartupDefaults(user.PreferencesJson);
    }

    public async Task<SessionStartupDefaults> UpdateSessionStartupDefaultsAsync(CurrentActor actor, SessionStartupDefaultsCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        SessionStartupDefaults defaults = ValidateSessionStartupDefaults(command);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        CloudEmueraUser? user = await db.Users.SingleOrDefaultAsync(value => value.Id == actor.UserId && value.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (user is null) throw new KeyNotFoundException();

        JsonObject preferences = ReadPreferencesObject(user.PreferencesJson);
        preferences[SessionStartupDefaultsKey] = JsonSerializer.SerializeToNode(defaults, PreferencesJsonOptions);
        string serialized = preferences.ToJsonString(PreferencesJsonOptions);
        if (Encoding.UTF8.GetByteCount(serialized) > PersistenceLimits.JsonMaxLength)
            throw new IdentityValidationException("PREFERENCES_TOO_LARGE");

        DateTimeOffset now = timeProvider.GetUtcNow();
        user.PreferencesJson = serialized;
        user.UpdatedAt = now;
        user.StateVersion = checked(user.StateVersion + 1);
        AddAudit(AuditActions.UserPreferencesUpdated, "USER", user.Id, "SUCCEEDED", "USER", user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return defaults;
    }

    public async Task<bool> ValidateSessionAsync(string userId, string sessionId, string securityStamp, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        AuthSessionRow? session = await db.AuthSessions.Include(row => row.User).SingleOrDefaultAsync(row => row.Id == sessionId && row.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (session?.User is not { Status: UserStatus.Active } user || session.RevokedAt is not null || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(session.SecurityStamp), System.Text.Encoding.UTF8.GetBytes(securityStamp)) || session.SecurityStamp != user.SecurityStamp)
            return false;
        if (now - session.LastSeenAt >= TimeSpan.FromMinutes(5))
        {
            session.LastSeenAt = now;
            DateTimeOffset proposed = now.Add(session.IsPersistent ? TimeSpan.FromDays(7) : TimeSpan.FromHours(12));
            session.IdleExpiresAt = proposed < session.AbsoluteExpiresAt ? proposed : session.AbsoluteExpiresAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task LogoutAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        AuthSessionRow? session = await db.AuthSessions.SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null) return;
        session.RevokedAt = now; session.RevokeReason = "LOGOUT";
        AddAudit(AuditActions.Logout, "AUTH_SESSION", session.UserId, "SUCCEEDED", "USER", session.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginResult?> ChangePasswordAsync(CurrentActor actor, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        IdentityValidation.ValidatePassword(newPassword);
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        CloudEmueraUser? user = await db.Users.SingleOrDefaultAsync(value => value.Id == actor.UserId && value.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false);
        if (user is null || user.PasswordHash is null || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed) return null;
        user.PasswordHash = passwordHasher.HashPassword(user, newPassword);
        user.PasswordChangedAt = now; user.MustChangePassword = false; user.SecurityStamp = NewStamp(); user.UpdatedAt = now; user.StateVersion++;
        await RevokeUserSessionsAsync(user.Id, now, "PASSWORD_CHANGED", cancellationToken).ConfigureAwait(false);
        LoginResult result = CreateSession(user, persistent: false, now);
        AddAudit(AuditActions.PasswordChanged, "USER", user.Id, "SUCCEEDED", user.Role == UserRole.Admin ? "ADMIN" : "USER", user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<IReadOnlyList<CurrentUser>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().OrderBy(user => user.CreatedAt).ThenBy(user => user.Id).Select(user => ToCurrent(user)).ToListAsync(cancellationToken).ContinueWith(task => (IReadOnlyList<CurrentUser>)task.Result, cancellationToken);

    public async Task<CurrentUser> CreateUserAsync(CreateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default)
    {
        string normalizedUsername = IdentityValidation.NormalizeUsername(command.Username);
        string normalizedEmail = IdentityValidation.NormalizeEmail(command.Email);
        IdentityValidation.ValidatePassword(command.TemporaryPassword);
        UserRole role = ParseRole(command.Role);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        if (await db.Users.AnyAsync(user => user.NormalizedLoginName == normalizedUsername, cancellationToken).ConfigureAwait(false)) throw new IdentityConflictException("USERNAME_ALREADY_EXISTS");
        if (await db.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken).ConfigureAwait(false)) throw new IdentityConflictException("EMAIL_ALREADY_EXISTS");
        // Keep the required foreign key populated for the first compatibility
        // migration, but do not select or schedule users by quota profile.
        string quotaProfileId = await db.QuotaProfiles.OrderBy(profile => profile.CreatedAt).ThenBy(profile => profile.Id)
            .Select(profile => profile.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IdentityConflictException("QUOTA_PROFILE_NOT_FOUND");
        DateTimeOffset now = timeProvider.GetUtcNow();
        CloudEmueraUser user = NewUser(command.Username, normalizedUsername, command.Email.Trim(), normalizedEmail, quotaProfileId, role, command.TemporaryPassword, now);
        AddAudit(AuditActions.UserCreated, "USER", user.Id, "SUCCEEDED", "ADMIN", actor.UserId);
        IdentityResult created = await userStore.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded) throw new IdentityConflictException(created.Errors.FirstOrDefault()?.Code ?? "USER_CREATE_FAILED");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToCurrent(user);
    }

    public async Task<CurrentUser> UpdateUserAsync(string id, UpdateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default)
    {
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        CloudEmueraUser? user = await db.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken).ConfigureAwait(false);
        if (user is null) throw new KeyNotFoundException();
        if (user.StateVersion != command.ExpectedStateVersion) throw new IdentityConcurrencyException();
        DateTimeOffset now = timeProvider.GetUtcNow(); bool revoke = false; List<string> auditActions = [];
        if (command.Username is not null)
        {
            string normalized = IdentityValidation.NormalizeUsername(command.Username);
            if (await db.Users.AnyAsync(value => value.Id != id && value.NormalizedLoginName == normalized, cancellationToken).ConfigureAwait(false)) throw new IdentityConflictException("USERNAME_ALREADY_EXISTS");
            user.LoginName = command.Username; user.NormalizedLoginName = normalized; revoke = true; auditActions.Add(AuditActions.UserProfileUpdated);
        }
        if (command.Email is not null)
        {
            string normalized = IdentityValidation.NormalizeEmail(command.Email);
            if (await db.Users.AnyAsync(value => value.Id != id && value.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false)) throw new IdentityConflictException("EMAIL_ALREADY_EXISTS");
            user.Email = command.Email.Trim(); user.NormalizedEmail = normalized; revoke = true; auditActions.Add(AuditActions.UserProfileUpdated);
        }
        if (command.Role is not null)
        {
            UserRole role = ParseRole(command.Role);
            if (user.Role == UserRole.Admin && role != UserRole.Admin) await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken).ConfigureAwait(false);
            user.Role = role; revoke = true; auditActions.Add(AuditActions.UserRoleChanged);
        }
        if (command.Status is not null)
        {
            UserStatus status = ParseStatus(command.Status);
            if (user.Role == UserRole.Admin && user.Status == UserStatus.Active && status != UserStatus.Active) await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken).ConfigureAwait(false);
            user.Status = status; revoke = true; auditActions.Add(AuditActions.UserStatusChanged);
        }
        string? securityStamp = revoke ? NewStamp() : user.SecurityStamp;
        int updated = await db.Users.Where(value => value.Id == id && value.StateVersion == command.ExpectedStateVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.LoginName, user.LoginName)
                .SetProperty(value => value.NormalizedLoginName, user.NormalizedLoginName)
                .SetProperty(value => value.Email, user.Email)
                .SetProperty(value => value.NormalizedEmail, user.NormalizedEmail)
                .SetProperty(value => value.Role, user.Role)
                .SetProperty(value => value.Status, user.Status)
                .SetProperty(value => value.SecurityStamp, securityStamp)
                .SetProperty(value => value.UpdatedAt, now)
                .SetProperty(value => value.StateVersion, value => value.StateVersion + 1), cancellationToken).ConfigureAwait(false);
        if (updated != 1) throw new IdentityConcurrencyException();
        if (revoke) await RevokeUserSessionsAsync(user.Id, now, "USER_UPDATED", cancellationToken).ConfigureAwait(false);
        user.SecurityStamp = securityStamp; user.UpdatedAt = now; user.StateVersion++;
        foreach (string action in auditActions.Distinct(StringComparer.Ordinal)) AddAudit(action, "USER", user.Id, "SUCCEEDED", "ADMIN", actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToCurrent(user);
    }

    public async Task ResetPasswordAsync(string id, string temporaryPassword, int expectedStateVersion, CurrentActor actor, CancellationToken cancellationToken = default)
    {
        IdentityValidation.ValidatePassword(temporaryPassword);
        await using SqliteImmediateTransaction transaction = await SqliteImmediateTransaction.BeginAsync(db, cancellationToken).ConfigureAwait(false);
        CloudEmueraUser? user = await db.Users.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, cancellationToken).ConfigureAwait(false);
        if (user is null) throw new KeyNotFoundException();
        if (user.StateVersion != expectedStateVersion) throw new IdentityConcurrencyException();
        DateTimeOffset now = timeProvider.GetUtcNow();
        string passwordHash = passwordHasher.HashPassword(user, temporaryPassword); string securityStamp = NewStamp();
        int updated = await db.Users.Where(value => value.Id == id && value.StateVersion == expectedStateVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.PasswordHash, passwordHash)
                .SetProperty(value => value.PasswordChangedAt, now)
                .SetProperty(value => value.MustChangePassword, true)
                .SetProperty(value => value.SecurityStamp, securityStamp)
                .SetProperty(value => value.UpdatedAt, now)
                .SetProperty(value => value.StateVersion, value => value.StateVersion + 1), cancellationToken).ConfigureAwait(false);
        if (updated != 1) throw new IdentityConcurrencyException();
        await RevokeUserSessionsAsync(user.Id, now, "PASSWORD_RESET", cancellationToken).ConfigureAwait(false);
        AddAudit(AuditActions.PasswordReset, "USER", user.Id, "SUCCEEDED", "ADMIN", actor.UserId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public CloudEmueraUser NewUser(string username, string normalizedUsername, string email, string normalizedEmail, string quotaProfileId, UserRole role, string password, DateTimeOffset now)
    {
        CloudEmueraUser user = new() { LoginName = username, NormalizedLoginName = normalizedUsername, Email = email, NormalizedEmail = normalizedEmail, QuotaProfileId = quotaProfileId, Role = role, Status = UserStatus.Active, CreatedAt = now, UpdatedAt = now, PasswordChangedAt = now, MustChangePassword = true, StateVersion = 0 };
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        return user;
    }

    public void AddAudit(string action, string resourceType, string resourceId, string result, string actorType, string? actorUserId = null, string? reason = null) => db.AuditEvents.Add(new AuditEventRow { Id = $"audit_{Guid.CreateVersion7():N}", OccurredAt = timeProvider.GetUtcNow(), Action = action, ResourceType = resourceType, ResourceId = resourceId, Result = result == "SUCCEEDED" ? AuditResult.Succeeded : AuditResult.Failed, ActorType = actorType switch { "ADMIN" => AuditActorType.Admin, "SYSTEM" => AuditActorType.System, _ => AuditActorType.User }, ActorUserId = actorUserId, ReasonCode = reason, RequestId = auditContext.RequestId, MetadataJson = "{}" });

    private async Task VerifyDummyAndAuditAsync(CancellationToken cancellationToken)
    {
        _ = passwordHasher.VerifyHashedPassword(new CloudEmueraUser(), _dummyHash, "invalid-password");
        AddAudit(AuditActions.LoginFailed, "AUTH", "login", "FAILED", "SYSTEM", reason: "INVALID_CREDENTIALS");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    private LoginResult CreateSession(CloudEmueraUser user, bool persistent, DateTimeOffset now)
    {
        DateTimeOffset absolute = now.Add(persistent ? TimeSpan.FromDays(30) : TimeSpan.FromHours(24));
        DateTimeOffset idle = now.Add(persistent ? TimeSpan.FromDays(7) : TimeSpan.FromHours(12));
        AuthSessionRow session = new() { Id = NewSessionId(), UserId = user.Id, SecurityStamp = user.SecurityStamp!, CreatedAt = now, LastSeenAt = now, IdleExpiresAt = idle, AbsoluteExpiresAt = absolute, IsPersistent = persistent };
        db.AuthSessions.Add(session);
        return new LoginResult(ToCurrent(user), session.Id, absolute);
    }
    private async Task RevokeUserSessionsAsync(string userId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        await db.AuthSessions.Where(session => session.UserId == userId && session.RevokedAt == null).ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now).SetProperty(session => session.RevokeReason, reason), cancellationToken).ConfigureAwait(false);
    private async Task EnsureAnotherActiveAdminAsync(string currentId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id != currentId && user.Role == UserRole.Admin && user.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false)) throw new IdentityConflictException("LAST_ACTIVE_ADMIN");
    }
    private static CurrentUser ToCurrent(CloudEmueraUser user) => new(user.Id, user.LoginName, user.Email ?? string.Empty, user.Role == UserRole.Admin ? "ADMIN" : "PLAYER", user.Status == UserStatus.Active ? "ACTIVE" : "DISABLED", user.MustChangePassword, user.StateVersion);
    private SessionStartupDefaults ValidateSessionStartupDefaults(SessionStartupDefaultsCommand command)
    {
        if (command.FontSize is < 8 or > 72 || command.LineHeight < command.FontSize || command.LineHeight > 128)
            throw new IdentityValidationException("INVALID_SESSION_STARTUP_DEFAULTS");
        if (!SessionWidthConfiguration.IsValid(command.WidthMode, command.CustomWidth))
            throw new IdentityValidationException("INVALID_SESSION_STARTUP_DEFAULTS");
        if (fontCatalog is null)
        {
            if (!string.Equals(command.FontFaceId, RuntimeFontDefaults.DefaultFaceId, StringComparison.Ordinal))
                throw new IdentityValidationException("INVALID_SESSION_STARTUP_FONT");
            return new SessionStartupDefaults(RuntimeFontDefaults.DefaultFaceId, command.FontSize, command.LineHeight, command.WidthMode, command.CustomWidth);
        }
        try
        {
            string faceId = fontCatalog.Require(command.FontFaceId).FaceId;
            return new SessionStartupDefaults(faceId, command.FontSize, command.LineHeight, command.WidthMode, command.CustomWidth);
        }
        catch (RuntimeFontCatalogException)
        {
            throw new IdentityValidationException("INVALID_SESSION_STARTUP_FONT");
        }
    }

    private SessionStartupDefaults ReadSessionStartupDefaults(string preferencesJson)
    {
        try
        {
            JsonObject? preferences = JsonNode.Parse(preferencesJson) as JsonObject;
            SessionStartupDefaults? stored = preferences?[SessionStartupDefaultsKey]?.Deserialize<SessionStartupDefaults>(PreferencesJsonOptions);
            if (stored is null || stored.FontSize is < 8 or > 72 || stored.LineHeight < stored.FontSize || stored.LineHeight > 128 || !SessionWidthConfiguration.IsValid(stored.WidthMode, stored.CustomWidth))
                return SessionStartupDefaults.Default;
            if (fontCatalog is null)
                return string.Equals(stored.FontFaceId, RuntimeFontDefaults.DefaultFaceId, StringComparison.Ordinal) ? stored : SessionStartupDefaults.Default;
            string faceId = fontCatalog.Require(stored.FontFaceId).FaceId;
            return new SessionStartupDefaults(faceId, stored.FontSize, stored.LineHeight, stored.WidthMode, stored.CustomWidth);
        }
        catch (JsonException)
        {
            return SessionStartupDefaults.Default;
        }
        catch (RuntimeFontCatalogException)
        {
            return SessionStartupDefaults.Default;
        }
    }

    private static JsonObject ReadPreferencesObject(string preferencesJson)
    {
        try
        {
            return JsonNode.Parse(preferencesJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
    private static UserRole ParseRole(string role) => role == "ADMIN" ? UserRole.Admin : role == "PLAYER" ? UserRole.Player : throw new IdentityValidationException("INVALID_ROLE");
    private static UserStatus ParseStatus(string status) => status == "ACTIVE" ? UserStatus.Active : status == "DISABLED" ? UserStatus.Disabled : throw new IdentityValidationException("INVALID_STATUS");
    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string NewSessionId() => "auths_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
