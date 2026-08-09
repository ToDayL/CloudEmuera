using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class SqliteDatabaseOptions
{
    public string DataRoot { get; init; } = string.Empty;

    public string DatabaseName { get; init; } = SqliteStorageConventions.DatabaseFileName;

    public int BusyTimeoutMilliseconds { get; init; } = PersistenceLimits.DefaultBusyTimeoutMilliseconds;

    public string MigrationsAssembly { get; init; } = typeof(CloudEmueraDbContext).Assembly.GetName().Name!;

    /// <summary>Optional operator-supplied, identity-bound plan for the legacy Game collapse.</summary>
    public string? GameCollapsePlanPath { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataRoot))
        {
            throw new SqlitePathException("Data root is required.");
        }

        if (string.IsNullOrWhiteSpace(DatabaseName)
            || Path.IsPathRooted(DatabaseName)
            || Path.GetFileName(DatabaseName) != DatabaseName
            || DatabaseName is "." or ".."
            || DatabaseName.Contains(Path.DirectorySeparatorChar)
            || DatabaseName.Contains(Path.AltDirectorySeparatorChar)
            || DatabaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new SqlitePathException("Database name must be one regular file name below the data root.");
        }

        if (BusyTimeoutMilliseconds is < PersistenceLimits.MinimumBusyTimeoutMilliseconds or > PersistenceLimits.MaximumBusyTimeoutMilliseconds)
        {
            throw new SqlitePathException("Busy timeout is outside the supported range.");
        }

        if (string.IsNullOrWhiteSpace(MigrationsAssembly))
        {
            throw new SqlitePathException("Migration assembly is required.");
        }
    }

    public SqliteDatabasePaths ResolvePaths(bool createDataRoot)
    {
        Validate();
        return SqliteDatabasePaths.Resolve(this, createDataRoot);
    }
}

public sealed record SqliteDatabasePaths(
    string DataRoot,
    string DatabasePath,
    string MigrationLockPath,
    string BackupDirectoryPath)
{
    public static SqliteDatabasePaths Resolve(SqliteDatabaseOptions options, bool createDataRoot)
    {
        string dataRoot = Path.GetFullPath(options.DataRoot);
        if (dataRoot == Path.GetPathRoot(dataRoot))
        {
            throw new SqlitePathException("The data root cannot be a filesystem root.");
        }

        SqlitePathSecurity.EnsureNoSymlinkAncestors(dataRoot);
        if (createDataRoot)
        {
            if (OperatingSystem.IsLinux())
            {
                using SafeFileHandle dataRootHandle = LinuxFileOperations.OpenOrCreateDirectory(dataRoot);
            }
            else
            {
                Directory.CreateDirectory(dataRoot);
            }
        }

        if (!Directory.Exists(dataRoot))
        {
            throw new SqlitePathException("The data root does not exist.");
        }

        SqlitePathSecurity.ValidateOptionalDirectory(dataRoot, "data root");

        string databasePath = Path.Combine(dataRoot, options.DatabaseName);
        string migrationLockPath = databasePath + SqliteStorageConventions.MigrationLockSuffix;
        string backupDirectoryPath = Path.Combine(dataRoot, SqliteStorageConventions.BackupDirectoryName);
        SqlitePathSecurity.EnsureNoSymlinkAncestors(databasePath);
        SqlitePathSecurity.ValidateOptionalFile(databasePath, "database");
        SqlitePathSecurity.ValidateOptionalFile(databasePath + "-wal", "database WAL");
        SqlitePathSecurity.ValidateOptionalFile(databasePath + "-shm", "database shared-memory");
        SqlitePathSecurity.ValidateOptionalFile(migrationLockPath, "migration lock");
        SqlitePathSecurity.ValidateOptionalDirectory(backupDirectoryPath, "backup directory");
        return new SqliteDatabasePaths(dataRoot, databasePath, migrationLockPath, backupDirectoryPath);
    }
}

public sealed class SqlitePathException(string message) : Exception(message);
