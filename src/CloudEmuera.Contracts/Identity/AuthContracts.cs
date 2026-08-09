namespace CloudEmuera.Contracts.Identity;

public sealed record CsrfResponse(string Token);
public sealed record LoginRequest(string Email, string Password, bool RememberMe);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CurrentUserResponse(string Id, string Username, string Email, string Role, string Status, bool MustChangePassword, int StateVersion);
public sealed record CreateUserRequest(string Username, string Email, string TemporaryPassword, string Role, string? QuotaProfileId);
public sealed record UpdateUserRequest(string? Username, string? Email, string? Role, string? Status);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record ApiError(string Code, string Message, string RequestId, object? Details = null);
