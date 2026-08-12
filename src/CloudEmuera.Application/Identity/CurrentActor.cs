namespace CloudEmuera.Application.Identity;

/// <summary>Authenticated request identity. This is deliberately independent of HTTP claims.</summary>
public sealed class CurrentActor(string userId, string role, string authSessionId)
{
    public string UserId { get; } = userId;
    public string Role { get; } = role;
    public string AuthSessionId { get; } = authSessionId;
    public bool IsAdmin => string.Equals(Role, "ADMIN", StringComparison.Ordinal);
}

public sealed class CurrentUser(string id, string username, string email, string role, string status, bool mustChangePassword, int stateVersion)
{
    public string Id { get; } = id; public string Username { get; } = username; public string Email { get; } = email; public string Role { get; } = role; public string Status { get; } = status; public bool MustChangePassword { get; } = mustChangePassword; public int StateVersion { get; } = stateVersion;
}

public sealed class LoginCommand(string email, string password, bool rememberMe) { public string Email { get; } = email; public string Password { get; } = password; public bool RememberMe { get; } = rememberMe; }

public sealed class LoginResult(CurrentUser user, string authSessionId, DateTimeOffset expiresAt) { public CurrentUser User { get; } = user; public string AuthSessionId { get; } = authSessionId; public DateTimeOffset ExpiresAt { get; } = expiresAt; }

public sealed class CreateUserCommand(string username, string email, string temporaryPassword, string role) { public string Username { get; } = username; public string Email { get; } = email; public string TemporaryPassword { get; } = temporaryPassword; public string Role { get; } = role; }

public sealed class UpdateUserCommand(string? username, string? email, string? role, string? status, int expectedStateVersion) { public string? Username { get; } = username; public string? Email { get; } = email; public string? Role { get; } = role; public string? Status { get; } = status; public int ExpectedStateVersion { get; } = expectedStateVersion; }

public sealed class AuthSessionInfo(string id, string userId, DateTimeOffset absoluteExpiresAt, bool persistent) { public string Id { get; } = id; public string UserId { get; } = userId; public DateTimeOffset AbsoluteExpiresAt { get; } = absoluteExpiresAt; public bool Persistent { get; } = persistent; }
