namespace CloudEmuera.Infrastructure.Persistence;

public static class SqliteStorageConventions
{
    public const string MigrationHistoryTable = "schema_migrations";
    public const string DatabaseFileName = "cloudemuera.db";
    public const string MigrationLockSuffix = ".migration.lock";
    public const string BackupDirectoryName = "backups";

    public const string QuotaProfilesTable = "quota_profiles";
    public const string UsersTable = "users";
    public const string GamesTable = "games";
    public const string SessionsTable = "sessions";
    public const string WorkerLeasesTable = "worker_leases";
    public const string IdempotencyRecordsTable = "idempotency_records";
    public const string AuditEventsTable = "audit_events";
    public const string AuthSessionsTable = "auth_sessions";
    public const string InstanceStateTable = "instance_state";
    public const string GamePackageIngestionsTable = "game_package_ingestions";
    public const string GameContentOperationsTable = "game_content_operations";
    public const string GameFilesTable = "game_files";
    public const string CompatibilityDiagnosticsTable = "compatibility_diagnostics";
    public const string GameContentCopyLeasesTable = "game_content_copy_leases";
}

public static class PersistenceLimits
{
    public const int IdMaxLength = 64;
    public const int LoginNameMaxLength = 128;
    public const int PasswordHashMaxLength = 512;
    public const int SecurityStampMaxLength = 128;
    public const int RoleMaxLength = 16;
    public const int StatusMaxLength = 16;
    public const int NameMaxLength = 200;
    public const int RuntimeVersionMaxLength = 128;
    public const int PathMaxLength = 512;
    public const int DigestLength = 71;
    public const int JsonMaxLength = 1_048_576;
    public const int PromptIdMaxLength = 256;
    public const int CloseReasonMaxLength = 256;
    public const int WorkerIdMaxLength = 128;
    public const int IpcEndpointMaxLength = 512;
    public const int ScopeMaxLength = 100;
    public const int IdempotencyKeyMaxLength = 256;
    public const int ResponseJsonMaxLength = 1_048_576;
    public const int RequestDigestLength = 71;
    public const int ActionMaxLength = 128;
    public const int ResourceTypeMaxLength = 64;
    public const int ResourceIdMaxLength = 128;
    public const int RequestIdMaxLength = 128;
    public const int ReasonCodeMaxLength = 128;
    public const int EmailMaxLength = 254;

    public const int DefaultBusyTimeoutMilliseconds = 5_000;
    public const int MinimumBusyTimeoutMilliseconds = 100;
    public const int MaximumBusyTimeoutMilliseconds = 60_000;
}

public enum UserRole
{
    Player,
    Admin,
}

public enum UserStatus
{
    Active,
    Disabled,
}

public enum GameVisibility
{
    Private,
    ServerShared,
}

public enum GameStatus
{
    Active,
    Blocked,
    Deleted,
}

public enum GameWorkspaceStatus
{
    None,
    Draft,
    Validating,
}

public enum GameContentOperationType { Import, ResetWorkspace, Validate, Activate }
public enum GameContentOperationStatus { Pending, Running, ContentReady, Committed, Failed }

public enum WorkerLeaseStatus
{
    Starting,
    Active,
    Stopping,
    Expired,
}

public enum AuditActorType
{
    User,
    Admin,
    System,
}

public enum AuditResult
{
    Succeeded,
    Failed,
}
