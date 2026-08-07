using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

public enum SqliteConnectionAccess
{
    ReadOnly,
    ReadWrite,
    ReadWriteCreate,
}

public sealed class SqliteConnectionFactory
{
    private readonly SqliteDatabaseOptions _options;
    private readonly SqliteDatabasePaths _paths;

    public SqliteConnectionFactory(SqliteDatabaseOptions options, bool createDataRoot)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = options.ResolvePaths(createDataRoot);
        SQLitePCL.Batteries_V2.Init();
    }

    public SqliteDatabasePaths Paths => _paths;

    public SqliteConnection OpenConnection(SqliteConnectionAccess access)
    {
        SafeFileHandle? parentDirectory = null;
        SafeFileHandle? databaseGuard = null;
        SqliteConnectionResources? protectedResources = null;
        try
        {
            string dataSource;
            SafeFileHandle? protectedParent = null;
            SafeFileHandle? protectedDatabase = null;
            if (OperatingSystem.IsLinux())
            {
                parentDirectory = LinuxFileOperations.OpenDirectory(_paths.DataRoot);
                databaseGuard = LinuxFileOperations.OpenRegularFileAt(
                    parentDirectory,
                    Path.GetFileName(_paths.DatabasePath),
                    readOnly: access == SqliteConnectionAccess.ReadOnly,
                    create: access == SqliteConnectionAccess.ReadWriteCreate,
                    exclusive: false);
                protectedParent = parentDirectory;
                protectedDatabase = databaseGuard;
                // Microsoft.Data.Sqlite accepts only a path. /proc/self/fd keeps SQLite's open
                // anchored to the already-validated inode instead of re-resolving the database name.
                dataSource = LinuxFileOperations.GetProcFileDescriptorPath(protectedDatabase);
                protectedResources = new SqliteConnectionResources(protectedDatabase, protectedParent);
                parentDirectory = null;
                databaseGuard = null;
            }
            else
            {
                SqliteDatabasePaths.Resolve(_options, createDataRoot: false);
                dataSource = _paths.DatabasePath;
            }

            SqliteConnectionStringBuilder connectionString = new()
            {
                DataSource = dataSource,
                Mode = access switch
                {
                    SqliteConnectionAccess.ReadOnly => SqliteOpenMode.ReadOnly,
                    SqliteConnectionAccess.ReadWrite => SqliteOpenMode.ReadWrite,
                    SqliteConnectionAccess.ReadWriteCreate => SqliteOpenMode.ReadWriteCreate,
                    _ => throw new ArgumentOutOfRangeException(nameof(access)),
                },
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                Pooling = !OperatingSystem.IsLinux(),
                DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_options.BusyTimeoutMilliseconds / 1000d)),
            };

            SqliteConnection connection = protectedResources is null
                ? new SqliteConnection(connectionString.ToString())
                : new ProtectedSqliteConnection(connectionString.ToString(), protectedResources);
            protectedResources = null;
            try
            {
                connection.Open();
                ConfigureConnection(connection, access != SqliteConnectionAccess.ReadOnly);
                if (OperatingSystem.IsLinux() && protectedParent is not null && protectedDatabase is not null)
                {
                    SqliteFileSecurity.ApplyPrivateMode(protectedDatabase);
                    SqliteFileSecurity.ApplyPrivateModeAt(protectedParent, Path.GetFileName(_paths.DatabasePath) + "-wal");
                    SqliteFileSecurity.ApplyPrivateModeAt(protectedParent, Path.GetFileName(_paths.DatabasePath) + "-shm");
                    using SafeFileHandle actualDatabase = LinuxFileOperations.TryOpenRegularFileAt(
                        protectedParent,
                        Path.GetFileName(_paths.DatabasePath),
                        readOnly: access == SqliteConnectionAccess.ReadOnly)
                        ?? throw new SqlitePathException("The SQLite database changed during connection setup.");
                    LinuxFileOperations.EnsureSameIdentity(protectedDatabase, actualDatabase);
                }
                else
                {
                    SqliteFileSecurity.ApplyPrivateModeToDatabase(_paths.DatabasePath);
                }

                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            databaseGuard?.Dispose();
            parentDirectory?.Dispose();
        }
    }

    private void ConfigureConnection(SqliteConnection connection, bool writable)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
        if (writable)
        {
            string journalMode = ExecuteScalar(connection, "PRAGMA journal_mode = WAL;")?.ToString() ?? string.Empty;
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteConfigurationException("SQLite WAL mode could not be enabled.");
            }

            ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");
        }

        string foreignKeys = ExecuteScalar(connection, "PRAGMA foreign_keys;")?.ToString() ?? string.Empty;
        if (foreignKeys != "1")
        {
            throw new SqliteConfigurationException("SQLite foreign keys are not enabled.");
        }

        string busyTimeout = ExecuteScalar(connection, "PRAGMA busy_timeout;")?.ToString() ?? string.Empty;
        if (!int.TryParse(busyTimeout, NumberStyles.Integer, CultureInfo.InvariantCulture, out int actualBusyTimeout)
            || actualBusyTimeout != _options.BusyTimeoutMilliseconds)
        {
            throw new SqliteConfigurationException("SQLite busy timeout did not match configuration.");
        }

        string actualJournalMode = ExecuteScalar(connection, "PRAGMA journal_mode;")?.ToString() ?? string.Empty;
        if (!string.Equals(actualJournalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteConfigurationException("SQLite database is not using WAL mode.");
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

public sealed class SqliteConfigurationException(string message) : Exception(message);
