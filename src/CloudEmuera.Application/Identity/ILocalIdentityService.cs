namespace CloudEmuera.Application.Identity;

public interface ILocalIdentityService
{
    Task<LoginResult?> LoginAsync(LoginCommand command, CancellationToken cancellationToken = default);
    Task<CurrentUser?> GetCurrentUserAsync(string userId, CancellationToken cancellationToken = default);
    Task<SessionStartupDefaults> GetSessionStartupDefaultsAsync(CurrentActor actor, CancellationToken cancellationToken = default);
    Task<SessionStartupDefaults> UpdateSessionStartupDefaultsAsync(CurrentActor actor, SessionStartupDefaultsCommand command, CancellationToken cancellationToken = default);
    Task<bool> ValidateSessionAsync(string userId, string sessionId, string securityStamp, CancellationToken cancellationToken = default);
    Task LogoutAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<LoginResult?> ChangePasswordAsync(CurrentActor actor, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CurrentUser>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<CurrentUser> CreateUserAsync(CreateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default);
    Task<CurrentUser> UpdateUserAsync(string id, UpdateUserCommand command, CurrentActor actor, CancellationToken cancellationToken = default);
    Task ResetPasswordAsync(string id, string temporaryPassword, int expectedStateVersion, CurrentActor actor, CancellationToken cancellationToken = default);
}
