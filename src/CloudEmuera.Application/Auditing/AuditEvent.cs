namespace CloudEmuera.Application.Auditing;

public sealed class AuditEvent(string action, string resourceType, string resourceId, string result, string actorType, string? actorUserId = null, string? reasonCode = null, string? requestId = null)
{
    public string Action { get; } = action; public string ResourceType { get; } = resourceType; public string ResourceId { get; } = resourceId; public string Result { get; } = result; public string ActorType { get; } = actorType; public string? ActorUserId { get; } = actorUserId; public string? ReasonCode { get; } = reasonCode; public string? RequestId { get; } = requestId;
}

public static class AuditActions
{
    public const string SystemAdminBootstrapped = "SYSTEM_ADMIN_BOOTSTRAPPED";
    public const string SystemAdminBootstrapFailed = "SYSTEM_ADMIN_BOOTSTRAP_FAILED";
    public const string LoginSucceeded = "AUTH_LOGIN_SUCCEEDED";
    public const string LoginFailed = "AUTH_LOGIN_FAILED";
    public const string Logout = "AUTH_LOGOUT";
    public const string PasswordChanged = "AUTH_PASSWORD_CHANGED";
    public const string UserCreated = "USER_CREATED";
    public const string UserProfileUpdated = "USER_PROFILE_UPDATED";
    public const string UserRoleChanged = "USER_ROLE_CHANGED";
    public const string UserStatusChanged = "USER_STATUS_CHANGED";
    public const string PasswordReset = "USER_PASSWORD_RESET";
    public const string GamePackageIngested = "GAME_PACKAGE_INGESTED";
    public const string GamePackageRejected = "GAME_PACKAGE_REJECTED";
    public const string SessionCreateRequested = "SESSION_CREATE_REQUESTED";
    public const string SessionCreated = "SESSION_CREATED";
    public const string SessionCreateFailed = "SESSION_CREATE_FAILED";
    public const string SessionOpenRequested = "SESSION_OPEN_REQUESTED";
    public const string SessionOpened = "SESSION_OPENED";
    public const string SessionOpenFailed = "SESSION_OPEN_FAILED";
    public const string SessionCloseRequested = "SESSION_CLOSE_REQUESTED";
    public const string SessionClosed = "SESSION_CLOSED";
    public const string SessionCloseFailed = "SESSION_CLOSE_FAILED";
    public const string SessionSaveImported = "SESSION_SAVE_IMPORTED";
    public const string SessionSaveRenamed = "SESSION_SAVE_RENAMED";
    public const string SessionSaveDeleted = "SESSION_SAVE_DELETED";
    public const string SessionSaveRecoveryFailed = "SESSION_SAVE_RECOVERY_FAILED";
}
