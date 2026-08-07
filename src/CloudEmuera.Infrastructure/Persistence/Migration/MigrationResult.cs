namespace CloudEmuera.Infrastructure.Persistence;

public static class MigrationExitCodes
{
    public const int Success = 0;
    public const int InvalidConfiguration = 10;
    public const int LockBusy = 11;
    public const int DatabaseNewerThanBinary = 12;
    public const int BackupFailed = 13;
    public const int MigrationFailed = 14;
    public const int IntegrityCheckFailed = 15;
}

public sealed record MigrationResult(
    int ExitCode,
    string Operation,
    string Result,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    string? BackupPath = null,
    string? ErrorCode = null)
{
    public bool Succeeded => ExitCode == MigrationExitCodes.Success;

    public static MigrationResult SuccessResult(
        string operation,
        IReadOnlyList<string>? applied = null,
        IReadOnlyList<string>? pending = null,
        string? backupPath = null) =>
        new(MigrationExitCodes.Success, operation, "succeeded", applied ?? [], pending ?? [], backupPath);
}
